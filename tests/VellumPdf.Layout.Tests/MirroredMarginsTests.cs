// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.ContentStreamReadback;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #608: <see cref="Document.MirrorMargins"/>. Every position is read back from the decoded
/// content stream of each page, in page-tree order, and compared with a number computed from the
/// margins below, not with what the layout code computes.
/// </summary>
/// <remarks>
/// The page is 300 pt wide. Left is 80 (inside), right is 40 (outside), so the content box is 180
/// wide on every page and its left edge is 80 on a right-hand page and 40 on a left-hand one.
/// </remarks>
public sealed class MirroredMarginsTests : IDisposable
{
    private const double Inside = 80;
    private const double Outside = 40;
    private const double PageWidth = 300;

    private static readonly EdgeInsets Mirrored = new(20, Outside, 20, Inside);

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vellummirror_{Guid.NewGuid():N}");

    public MirroredMarginsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    // A 300 x 300 pt page with 20 pt top and bottom margins has a 260 pt content area. A paragraph
    // with a 200 pt top margin is about 214 pt tall, so two never share a page and each lands on
    // its own, which is what lets a test choose the page an element is drawn on.
    private static Paragraph Tall(string label, HorizontalAlignment alignment = HorizontalAlignment.Left,
        PageBreakMode breakBefore = PageBreakMode.None) =>
        new(label, Body)
        {
            Margins = new EdgeInsets(200, 0, 0, 0),
            Alignment = alignment,
            BreakBefore = breakBefore,
        };

    private static Document NewDocument(bool mirror = true) => new()
    {
        PageSize = Page,
        Margins = Mirrored,
        MirrorMargins = mirror,
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // The decoded content stream of every page, in page-tree order. A blank page decodes to the
    // empty string.
    private static List<string> PageStreams(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pages = new List<string>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            var number = int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture);
            var page = Regex.Match(text, $@"(?m)^{number} 0 obj\n<<(?<d>.*?)>>\nendobj", RegexOptions.Singleline);
            Assert.True(page.Success, $"page object {number}");
            var contents = Regex.Match(page.Groups["d"].Value, @"/Contents (\d+) 0 R");
            Assert.True(contents.Success, $"page object {number} has no /Contents reference");
            pages.Add(StreamOf(pdf, text, int.Parse(contents.Groups[1].Value, CultureInfo.InvariantCulture)));
        }

        return pages;
    }

    private static string StreamOf(byte[] pdf, string text, int objectNumber)
    {
        var head = Regex.Match(text, $@"(?m)^{objectNumber} 0 obj\n(?<d><<.*?>>)\nstream\n", RegexOptions.Singleline);
        Assert.True(head.Success, $"stream object {objectNumber}");
        var length = int.Parse(Regex.Match(head.Groups["d"].Value, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        using var input = new MemoryStream(pdf, head.Index + head.Length, length);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private static TextPlacement Only(string stream, string text) =>
        Assert.Single(TextPlacements(stream), p => p.Text == text);

    // ── The body ─────────────────────────────────────────────────────────────

    [Fact]
    public void BodyText_startsAtTheInsideMarginOnRectoAndTheOutsideMarginOnVerso()
    {
        using var doc = NewDocument();
        for (var i = 1; i <= 4; i++)
            doc.Add(Tall($"P{i}"));

        var pages = PageStreams(Save(doc));

        Assert.Equal(4, pages.Count);
        Assert.Equal(Inside, Only(pages[0], "P1").X);
        Assert.Equal(Outside, Only(pages[1], "P2").X);
        Assert.Equal(Inside, Only(pages[2], "P3").X);
        Assert.Equal(Outside, Only(pages[3], "P4").X);
    }

    [Fact]
    public void WithoutTheProperty_everyPageUsesTheLeftMargin()
    {
        using var doc = NewDocument(mirror: false);
        for (var i = 1; i <= 3; i++)
            doc.Add(Tall($"P{i}"));

        var pages = PageStreams(Save(doc));

        Assert.All(pages.Select((p, i) => Only(p, $"P{i + 1}").X), x => Assert.Equal(Inside, x));
    }

    [Fact]
    public void RightAlignedBody_endsAtTheContentBoxRightEdgeOfItsOwnPage_andDoesNotSwap()
    {
        using var doc = NewDocument();
        doc.Add(Tall("R1", HorizontalAlignment.Right));
        doc.Add(Tall("R2", HorizontalAlignment.Right));

        var pages = PageStreams(Save(doc));
        var width = Body.MeasureString("R1");

        // Page 1 ends at 300 - 40 = 260, page 2 at 300 - 80 = 220. A swapped alignment would put
        // page 2's text at its left edge, 40.
        Assert.Equal(PageWidth - Outside, Only(pages[0], "R1").X + width, 1e-4);
        Assert.Equal(PageWidth - Inside, Only(pages[1], "R2").X + width, 1e-4);
    }

    // The same paragraph laid out on page 1 of one document and on page 2 of another breaks at the
    // same words, because the width does not change; only its x does.
    [Fact]
    public void ALongParagraph_wrapsIdenticallyOnRectoAndVerso()
    {
        var text = string.Join(' ', Enumerable.Range(0, 40).Select(i => $"word{i}{new string('x', i % 7)}"));

        List<TextPlacement> OnPage(bool afterFiller, bool mirror)
        {
            using var doc = NewDocument(mirror);
            if (afterFiller)
            {
                doc.Add(Tall("filler"));
                doc.Add(new PageBreak());
            }

            doc.Add(new Paragraph(text, Body));
            var pages = PageStreams(Save(doc));
            var page = afterFiller ? pages[1] : pages[0];
            return [.. TextPlacements(page).Where(p => p.Text != "filler")];
        }

        var recto = OnPage(afterFiller: false, mirror: true);
        var verso = OnPage(afterFiller: true, mirror: true);
        var plain = OnPage(afterFiller: false, mirror: false);

        Assert.True(recto.Count > 5, "the paragraph must wrap to several lines");
        Assert.Equal(recto.Select(p => p.Text), verso.Select(p => p.Text));
        Assert.Equal(recto.Select(p => p.Text), plain.Select(p => p.Text));
        Assert.All(recto, p => Assert.Equal(Inside, p.X));
        Assert.All(verso, p => Assert.Equal(Outside, p.X));
    }

    private static TableElement Grid()
    {
        var table = new TableElement();
        for (var r = 0; r < 2; r++)
        {
            var row = table.AddRow();
            row.AddCell($"a{r}");
            row.AddCell($"b{r}");
        }

        return table;
    }

    [Fact]
    public void ATableAndAnImageOnVerso_areShiftedByTheMarginDifference()
    {
        var image = PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());

        using var doc = NewDocument();
        doc.Add(Grid());
        doc.Add(new LayoutImage(image) { Width = 50, Height = 50 });
        doc.Add(new PageBreak());
        doc.Add(Grid());
        doc.Add(new LayoutImage(image) { Width = 50, Height = 50 });

        var pages = PageStreams(Save(doc));
        Assert.Equal(2, pages.Count);

        var recto = GeometryExtent(pages[0])!.Value;
        var verso = GeometryExtent(pages[1])!.Value;

        Assert.Equal(Inside, recto.MinX, 1e-4);
        Assert.Equal(Outside, verso.MinX, 1e-4);

        // The table is as wide on both pages, so its right edge moves by the same 40, and the
        // cell text starts at the same offset from the left edge on both.
        Assert.Equal(recto.MaxX - recto.MinX, verso.MaxX - verso.MinX, 1e-4);
        var cellRecto = Only(pages[0], "a0").X - recto.MinX;
        var cellVerso = Only(pages[1], "a0").X - verso.MinX;
        Assert.Equal(cellRecto, cellVerso, 1e-4);

        // The image matrix: its translation is the left edge of the content box.
        var cm = Regex.Matches(pages[1], @"(?m)^50 0 0 50 (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) cm$");
        Assert.Single(cm);
        Assert.Equal(Outside, double.Parse(cm[0].Groups[1].Value, CultureInfo.InvariantCulture), 1e-4);
        Assert.Equal(Inside, double.Parse(
            Regex.Match(pages[0], @"(?m)^50 0 0 50 (-?\d+(?:\.\d+)?) -?\d+(?:\.\d+)? cm$").Groups[1].Value,
            CultureInfo.InvariantCulture), 1e-4);
    }

    // ── The running bands ────────────────────────────────────────────────────

    [Fact]
    public void BandAlignmentsSwapOnVerso_andCentreFollowsTheContentBox()
    {
        var style = Body;
        using var doc = NewDocument();
        doc.Header = new RunningBand("H{page}", style, HorizontalAlignment.Left);
        doc.Footer = new RunningBand("F{page}", style, HorizontalAlignment.Right);
        doc.Add(Tall("P1"));
        doc.Add(Tall("P2"));
        doc.Add(Tall("P3"));

        var pages = PageStreams(Save(doc));
        Assert.Equal(3, pages.Count);

        double HeaderX(int page) => Only(pages[page - 1], $"H{page}").X;
        double FooterEnd(int page) => Only(pages[page - 1], $"F{page}").X + style.MeasureString($"F{page}");

        // Recto: header on the inside margin, footer ending at the outside margin.
        Assert.Equal(Inside, HeaderX(1));
        Assert.Equal(PageWidth - Outside, FooterEnd(1), 1e-4);

        // Verso: the footer is on the outside, which is the left; the header ends at the inside,
        // which is the right.
        Assert.Equal(Outside, Only(pages[1], "F2").X, 1e-4);
        Assert.Equal(PageWidth - Inside, HeaderX(2) + style.MeasureString("H2"), 1e-4);

        Assert.Equal(Inside, HeaderX(3));
        Assert.Equal(PageWidth - Outside, FooterEnd(3), 1e-4);
    }

    [Fact]
    public void ACentredBand_centresInTheContentBoxOfItsOwnPage()
    {
        using var doc = NewDocument();
        doc.Footer = new RunningBand("{page}", Body, HorizontalAlignment.Center);
        doc.Add(Tall("P1"));
        doc.Add(Tall("P2"));

        var pages = PageStreams(Save(doc));
        var width = Body.MeasureString("1");

        Assert.Equal(Inside + (180 - width) / 2, Only(pages[0], "1").X, 1e-4);
        Assert.Equal(Outside + (180 - width) / 2, Only(pages[1], "2").X, 1e-4);
    }

    [Fact]
    public void WithoutTheProperty_bandsStayWhereTheyWere()
    {
        using var doc = NewDocument(mirror: false);
        doc.Footer = new RunningBand("{page}", Body, HorizontalAlignment.Right);
        doc.Add(Tall("P1"));
        doc.Add(Tall("P2"));

        var pages = PageStreams(Save(doc));
        var width = Body.MeasureString("2");

        Assert.Equal(PageWidth - Outside, Only(pages[1], "2").X + width, 1e-4);
    }

    // ── Recto breaks and blank pages ─────────────────────────────────────────

    [Fact]
    public void ABlankPageARectoBreakInserts_countsInTheParity()
    {
        using var doc = NewDocument();
        doc.Footer = new RunningBand("{page}", Body, HorizontalAlignment.Right);
        doc.Add(Tall("A"));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(Tall("B"));
        doc.Add(Tall("C"));

        var pages = PageStreams(Save(doc));

        // A on page 1, a blank page 2, B on page 3 (recto, inside margin), C on page 4 (verso).
        Assert.Equal(4, pages.Count);
        Assert.Equal("", pages[1]);
        Assert.Equal(Inside, Only(pages[0], "A").X);
        Assert.Equal(Inside, Only(pages[2], "B").X);
        Assert.Equal(Outside, Only(pages[3], "C").X);
        Assert.Equal(PageWidth - Outside, Only(pages[2], "3").X + Body.MeasureString("3"), 1e-4);
        Assert.Equal(Outside, Only(pages[3], "4").X, 1e-4);
    }

    [Fact]
    public void ARectoBreakAfterAnEvenPage_addsNoBlankPage_andStartsOnTheInsideMargin()
    {
        using var doc = NewDocument();
        doc.Add(Tall("A"));
        doc.Add(Tall("B"));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(Tall("C"));

        var pages = PageStreams(Save(doc));

        Assert.Equal(3, pages.Count);
        Assert.Equal(Outside, Only(pages[1], "B").X);
        Assert.Equal(Inside, Only(pages[2], "C").X);
    }

    // ── Both passes, and what a renderer is offered ──────────────────────────

    [Fact]
    public void BothPassesOfferEveryRendererTheSameArea()
    {
        var log = new CallLog();
        var renderer = new DocumentRenderer(NewPdf(), Page, Mirrored)
        {
            MirrorMargins = true,
            Footer = new RunningBand("{page}/{pages}"),
        };
        for (var i = 0; i < 3; i++)
            renderer.Add(Logging.Wrap(new ParagraphRenderer(Tall($"P{i}")), log));
        Render(renderer);

        // One measuring run, then the drawing pass: the same calls in the same order.
        Assert.Equal(1, renderer.MeasureRuns);
        var lines = log.Text.Split('\n');
        Assert.Equal(0, lines.Length % 2);
        var half = lines.Length / 2;
        Assert.Equal(lines[..half], lines[half..]);

        // Two tall paragraphs never share a page. P1 is refused on page 1, probed on page 2 and
        // then laid out there; P2 the same from page 2 to page 3.
        var xs = lines[..half].Select(l => double.Parse(l.Split(' ')[1], CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal([Inside, Inside, Outside, Outside, Outside, Inside, Inside], xs);
    }

    [Fact]
    public void BothPassesRecordTheSamePlacementX()
    {
        var renderer = new DocumentRenderer(NewPdf(), Page, Mirrored)
        {
            MirrorMargins = true,
            Footer = new RunningBand("{page}"),
            RecordPlacements = true,
        };
        renderer.Add(new ParagraphRenderer(Tall("A")));
        renderer.Add(new PageBreakRenderer(new PageBreak()));
        renderer.Add(new ParagraphRenderer(Tall("B")));
        renderer.Add(new PageBreakRenderer(new PageBreak()));
        renderer.Add(new ParagraphRenderer(Tall("C")));
        renderer.Add(new PageBreakRenderer(new PageBreak { Recto = true }));
        renderer.Add(new ParagraphRenderer(Tall("D")));
        Render(renderer);

        var measured = renderer.MeasurePlan!.Placements.Select(p => (p.PageIndex, p.Box.X)).ToArray();
        var drawn = renderer.DrawPlan!.Placements.Select(p => (p.PageIndex, p.Box.X)).ToArray();

        // A break reports the top of the content area of the page the next element starts on: page
        // 2 (verso), page 3 (recto), and page 5 (recto) after the blank page 4 the recto break
        // inserts after page 3.
        Assert.Equal([(1, Outside), (2, Inside), (4, Inside)], measured);
        Assert.Equal(measured, drawn);
    }

    // A renderer that answers Nothing once, so the document retries it on the next page, and
    // records what each call was offered.
    // The document calls an element three times when it answers Nothing: the refused call, the
    // retry that proves the next page takes it, and the call that places it. The first call of
    // each pass is refused and the other two are accepted.
    private sealed class RecordsOffers(List<(LayoutBox Area, LayoutBox? Fresh)> offers, bool refuseFirst) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var refuse = refuseFirst && offers.Count % 3 == 0;
            offers.Add((context.Area, context.FreshPageArea));
            if (refuse) return LayoutResult.Nothing();

            return LayoutResult.Full(context.Area.WithHeight(10));
        }

        public void Draw(DrawContext context) { }
    }

    [Fact]
    public void TheFreshPageAreaIsTheAreaOfThePageTheRetryLandsOn()
    {
        var offers = new List<(LayoutBox Area, LayoutBox? Fresh)>();
        var renderer = new DocumentRenderer(NewPdf(), Page, Mirrored)
        {
            MirrorMargins = true,
            Footer = new RunningBand("{page}"),
        };
        renderer.Add(new RecordsOffers(offers, refuseFirst: true));
        Render(renderer);

        // Measuring pass then drawing pass, three calls each. The first call is on page 1 (recto)
        // and is retried on page 2 (verso): its fresh area is page 2's. The retry is already on
        // that page, so its fresh area is its own; the call after it is on page 2 again, so its
        // fresh area is page 3's.
        Assert.Equal(6, offers.Count);
        foreach (var pass in new[] { offers[..3], offers[3..] })
        {
            Assert.Equal([Inside, Outside, Outside], pass.Select(o => o.Area.X));
            Assert.Equal([Outside, Outside, Inside], pass.Select(o => o.Fresh!.Value.X));
            Assert.All(pass, o => Assert.Equal(180, o.Fresh!.Value.Width));
            Assert.All(pass, o => Assert.Equal(o.Area.Y, o.Fresh!.Value.Y, 1e-9));
        }
    }

    [Fact]
    public void TheFreshPageAreaOfACallOnVerso_isTheNextRectoPage()
    {
        var offers = new List<(LayoutBox Area, LayoutBox? Fresh)>();
        var renderer = new DocumentRenderer(NewPdf(), Page, Mirrored)
        {
            MirrorMargins = true,
            Footer = new RunningBand("{page}"),
        };
        renderer.Add(new ParagraphRenderer(Tall("A")));
        renderer.Add(new ParagraphRenderer(Tall("B")));
        renderer.Add(new RecordsOffers(offers, refuseFirst: false));
        Render(renderer);

        // The recording renderer follows the second tall paragraph on page 2 (verso), which has
        // room for it. A refused call would be retried on page 3, which is recto.
        Assert.Equal(2, offers.Count);
        Assert.All(offers, o =>
        {
            Assert.Equal(Outside, o.Area.X);
            Assert.Equal(Inside, o.Fresh!.Value.X);
        });
    }

    [Fact]
    public void WithoutTheProperty_theFreshPageAreaIsTheOfferedArea()
    {
        var offers = new List<(LayoutBox Area, LayoutBox? Fresh)>();
        var renderer = new DocumentRenderer(NewPdf(), Page, Mirrored) { Footer = new RunningBand("{page}") };
        renderer.Add(new RecordsOffers(offers, refuseFirst: true));
        Render(renderer);

        Assert.Equal(6, offers.Count);
        Assert.All(offers, o => Assert.Equal(Inside, o.Fresh!.Value.X));
        Assert.All(offers, o => Assert.Equal(Inside, o.Area.X));
    }

    // A table row taller than a page is cut between its lines (#661); the cut reads the fresh
    // area's height, and the table must still save whichever side the page is on.
    [Fact]
    public void ATallTableRow_isCutOnEitherSideOfThePage()
    {
        var words = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"w{i}"));
        using var doc = NewDocument();
        doc.Add(Tall("filler"));
        var table = new TableElement();
        table.AddRow().AddCell(words);
        doc.Add(table);

        var pages = PageStreams(Save(doc));

        Assert.True(pages.Count >= 3, $"pages: {pages.Count}");
        // The cut row's lines on a verso page start at the outside margin plus the cell padding,
        // and on a recto page at the inside margin plus the same padding.
        var padding = TextPlacements(pages[1]).Min(p => p.X) - Outside;
        Assert.True(padding >= 0 && padding < 20, $"padding {padding}");
        Assert.Equal(Inside + padding, TextPlacements(pages[2]).Min(p => p.X), 1e-4);
    }

    // ── Refusals are the ones Margins already has ────────────────────────────

    [Theory]
    [InlineData(200, 100)]
    [InlineData(150, 150)]
    [InlineData(300, 0)]
    public void MarginsThatLeaveNoContentWidth_areRefusedWithTheSameMessage(double left, double right)
    {
        string Message(bool mirror)
        {
            using var doc = NewDocument(mirror);
            doc.Margins = new EdgeInsets(20, right, 20, left);
            doc.Add(new Paragraph("x", Body));
            return Assert.Throws<ArgumentException>(() => Save(doc)).Message;
        }

        Assert.Equal(Message(mirror: false), Message(mirror: true));
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    private static Document Claimed(PdfConformance conformance)
    {
        var doc = NewDocument();
        doc.Conformance = conformance;
        doc.Info.Title = "Mirrored margins";
        doc.Language = "en-US";
        doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");
        return doc;
    }

    private string Write(byte[] pdf, string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    [Fact]
    public void AMirroredPdfA2uDocument_passesQpdfPdftotextAndVeraPdf()
    {
        var fontPath = Path.Combine(FontReportSupport.FontsDir, "DejaVuSans.ttf");
        if (!File.Exists(fontPath))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");

        using var doc = Claimed(PdfConformance.PdfA2u);
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath)), FontSize = 12 };
        doc.Header = new RunningBand("Mirrored header", style, HorizontalAlignment.Left);
        doc.Footer = new RunningBand("Page {page} of {pages}", style, HorizontalAlignment.Right);
        string[] labels = ["alpha first", "beta second", "gamma third", "delta fourth"];
        foreach (var label in labels)
        {
            doc.Add(new Paragraph(label, style) { Margins = new EdgeInsets(200, 0, 0, 0) });
        }

        var bytes = Save(doc);
        var path = Write(bytes, "mirrored-2u.pdf");

        // The oracle sees the shifted text: assert on its output, not on a clean exit.
        ExternalTool.TryRun("qpdf", ["--check", path], out var qexit, out var qout, out var qerr, out var qtimed);
        Assert.False(qtimed);
        Assert.True(qexit == 0, qout + qerr);
        Assert.Contains("No syntax or stream encoding errors found", qout, StringComparison.Ordinal);

        for (var page = 1; page <= labels.Length; page++)
        {
            ExternalTool.TryRun("pdftotext", ["-layout", "-f", page.ToString(CultureInfo.InvariantCulture),
                "-l", page.ToString(CultureInfo.InvariantCulture), path, "-"],
                out var exit, out var text, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, stderr);
            Assert.Contains(labels[page - 1], text, StringComparison.Ordinal);
            Assert.Contains("Mirrored header", text, StringComparison.Ordinal);
            Assert.Contains($"Page {page} of {labels.Length}", text, StringComparison.Ordinal);

            // pdftotext -layout indents a line by its x, so the outside-aligned footer sits further
            // left on a verso page than on a recto one: the leading blanks are what moved.
            var footer = text.Split('\n').Single(l => l.Contains("Page ", StringComparison.Ordinal));
            var indent = footer.Length - footer.TrimStart().Length;
            if (page % 2 == 0) Assert.True(indent < 15, $"verso footer indent {indent}: {footer}");
            else Assert.True(indent > 15, $"recto footer indent {indent}: {footer}");
        }

        var report = FontReportSupport.VeraPdf("2u", bytes);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
    }
}
