// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #311: <see cref="PageBreak"/>, <c>BreakBefore</c> and the blank page a recto break inserts. Every
/// page assertion reads the decoded content stream of each page, in page-tree order, so a blank page
/// is a page whose stream holds no operator at all and not a page that merely drew no text.
/// </summary>
public sealed class PageBreakTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vellumpagebreak_{Guid.NewGuid():N}");

    public PageBreakTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static readonly PdfImageXObject FixtureImage =
        PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());

    // A 300 x 300 pt page with 20 pt margins has a 260 pt content area. A paragraph with a 200 pt
    // top margin is about 214 pt tall, so two of them never share a page and each draws its
    // label, which is what lets the tests read which page an element landed on.
    private static Paragraph Tall(string label, PageBreakMode breakBefore = PageBreakMode.None) =>
        new(label, Body) { Margins = new EdgeInsets(200, 0, 0, 0), BreakBefore = breakBefore };

    private static Document NewDocument() => new()
    {
        PageSize = Page,
        Margins = Margins,
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private sealed record PageContent(int ObjectNumber, string Operators)
    {
        internal IReadOnlyList<string> Texts { get; } =
            [.. Regex.Matches(Operators, @"\((?<t>(?:\\.|[^\\)])*)\) Tj").Select(m => m.Groups["t"].Value)];
    }

    // The pages in page-tree order with their decoded content. Decoded, never raw: net10.0 writes an
    // empty body as /Length 0 with no zlib header and net11.0 as eight bytes, so the raw bytes of an
    // empty page differ by runtime and its decoded stream does not.
    private static List<PageContent> Pages(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pages = new List<PageContent>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            var number = int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture);
            var page = Regex.Match(text, $@"(?m)^{number} 0 obj\n<<(?<d>.*?)>>\nendobj", RegexOptions.Singleline);
            Assert.True(page.Success, $"page object {number}");
            var contents = Regex.Match(page.Groups["d"].Value, @"/Contents (\d+) 0 R");
            Assert.True(contents.Success, $"page object {number} has no /Contents reference");
            pages.Add(new PageContent(number, StreamOf(pdf, text, int.Parse(contents.Groups[1].Value, CultureInfo.InvariantCulture))));
        }

        return pages;
    }

    private static string StreamOf(byte[] pdf, string text, int objectNumber)
    {
        var head = Regex.Match(text, $@"(?m)^{objectNumber} 0 obj\n(?<d><<.*?>>)\nstream\n", RegexOptions.Singleline);
        Assert.True(head.Success, $"stream object {objectNumber}");
        var length = int.Parse(Regex.Match(head.Groups["d"].Value, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        if (length == 0)
            return string.Empty;
        using var input = new MemoryStream(pdf, head.Index + head.Length, length);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    private static string[] Labels(byte[] pdf) =>
        [.. Pages(pdf).Select(p => string.Join('|', p.Texts))];

    private static RunningBand Footer() => new("{page}/{pages}");

    // ── Rules, as pages ──────────────────────────────────────────────────────

    [Fact]
    public void PageBreakBetweenTwoElements_startsTheSecondOnANewPage_andFootersCountBoth()
    {
        using var doc = NewDocument();
        doc.Footer = Footer();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("B", Body));

        // The footer is the last text of its page: the band is drawn after the content.
        Assert.Equal(["A|1/2", "B|2/2"], Labels(Save(doc)));
    }

    [Fact]
    public void PageBreakFirst_addsNoBlankPage()
    {
        using var doc = NewDocument();
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("A", Body));

        Assert.Equal(["A"], Labels(Save(doc)));
    }

    [Fact]
    public void ConsecutivePageBreaks_areOneBreak()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new PageBreak());
        doc.Add(new PageBreak());
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("B", Body));

        Assert.Equal(["A", "B"], Labels(Save(doc)));
    }

    [Fact]
    public void PlainBreakAfterAnElementWithBreakBefore_isNotASecondBreak()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new Paragraph("B", Body) { BreakBefore = PageBreakMode.Page });
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("C", Body));

        Assert.Equal(["A", "B", "C"], Labels(Save(doc)));
    }

    [Fact]
    public void RectoBreakAfterPageOne_insertsABlankPage_andTheNextElementStartsOnPageThree()
    {
        using var doc = NewDocument();
        doc.Footer = Footer();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("B", Body));

        var pages = Pages(Save(doc));

        Assert.Equal(3, pages.Count);
        // Page 2 holds no operator at all: no content and no band.
        Assert.Equal(string.Empty, pages[1].Operators);
        // The footer on page 3 counts the blank page, and {pages} counts it too.
        Assert.Equal(["A|1/3", "", "B|3/3"], pages.Select(p => string.Join('|', p.Texts)).ToArray());
    }

    [Fact]
    public void RectoBreakAfterAnEvenPage_addsNoBlankPage()
    {
        using var doc = NewDocument();
        doc.Add(Tall("A1"));
        doc.Add(Tall("A2"));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("B", Body));

        Assert.Equal(["A1", "A2", "B"], Labels(Save(doc)));
    }

    [Fact]
    public void RectoBreakAfterAnOddPageOfSeveral_insertsOneBlank()
    {
        using var doc = NewDocument();
        doc.Add(Tall("A1"));
        doc.Add(Tall("A2"));
        doc.Add(Tall("A3"));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("B", Body));

        Assert.Equal(["A1", "A2", "A3", "", "B"], Labels(Save(doc)));
    }

    [Fact]
    public void HeadingWithRectoBreakFirst_staysOnPageOne()
    {
        using var doc = NewDocument();
        doc.Add(new Heading("Chapter", Body) { BreakBefore = PageBreakMode.Recto });
        doc.Add(new Paragraph("text", Body));

        Assert.Equal(["Chapter|text"], Labels(Save(doc)));
    }

    [Fact]
    public void PageBreakThenRecto_andRectoThenPage_giveTheSamePages()
    {
        string[] Run(params PageBreak[] breaks)
        {
            using var doc = NewDocument();
            doc.Add(new Paragraph("A", Body));
            foreach (var b in breaks)
                doc.Add(b);
            doc.Add(new Paragraph("B", Body));
            return Labels(Save(doc));
        }

        // One page of A, so a recto break needs a blank; the plain break beside it changes nothing,
        // whichever comes first.
        Assert.Equal(["A", "", "B"], Run(new PageBreak(), new PageBreak { Recto = true }));
        Assert.Equal(["A", "", "B"], Run(new PageBreak { Recto = true }, new PageBreak()));
    }

    [Fact]
    public void TrailingBreak_addsNothing()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new PageBreak { Recto = true });

        Assert.Equal(["A"], Labels(Save(doc)));
    }

    [Fact]
    public void DocumentOfBreaksAlone_hasNoPages_andCanStillBeSaved()
    {
        using var doc = NewDocument();
        doc.Add(new PageBreak());
        doc.Add(new PageBreak { Recto = true });

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);

        // Refused before any layout, so the document is still usable (a retry saves once).
        doc.Add(new Paragraph("A", Body));
        Assert.Equal(["A"], Labels(Save(doc)));
    }

    [Fact]
    public void ManyConsecutiveRectoBreaks_insertExactlyOneBlank()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        for (var i = 0; i < 10_000; i++)
            doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("B", Body));

        Assert.Equal(["A", "", "B"], Labels(Save(doc)));
    }

    [Fact]
    public void BreakBefore_onEachElementKind_startsANewPage()
    {
        var table = new TableElement { BreakBefore = PageBreakMode.Page };
        table.AddRow().AddCell("cell");
        var list = new ListElement { BreakBefore = PageBreakMode.Page };
        list.Add("item", Body);

        using var doc = NewDocument();
        doc.Add(new Paragraph("first", Body));
        doc.Add(new Paragraph("para", Body) { BreakBefore = PageBreakMode.Page });
        doc.Add(new Heading("head", Body) { BreakBefore = PageBreakMode.Page });
        doc.Add(table);
        doc.Add(list);
        doc.Add(new LayoutImage(FixtureImage) { Width = 20, Height = 20, BreakBefore = PageBreakMode.Page });

        var pages = Pages(Save(doc));

        Assert.Equal(6, pages.Count);
        Assert.Equal(["first"], pages[0].Texts);
        Assert.Equal(["para"], pages[1].Texts);
        Assert.Equal(["head"], pages[2].Texts);
        Assert.Equal(["cell"], pages[3].Texts);
        Assert.Contains("item", pages[4].Texts);
        Assert.Contains(" Do", pages[5].Operators, StringComparison.Ordinal);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(3)]
    public void AnUndefinedMode_isRefusedAtInit_onEveryElementKind(int value)
    {
        var mode = (PageBreakMode)value;

        AssertRefused(() => new Paragraph("p", Body) { BreakBefore = mode });
        AssertRefused(() => new Heading("h", Body) { BreakBefore = mode });
        AssertRefused(() => new TableElement { BreakBefore = mode });
        AssertRefused(() => new ListElement { BreakBefore = mode });
        AssertRefused(() => new LayoutImage(FixtureImage) { BreakBefore = mode });

        void AssertRefused(Action init)
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(init);
            Assert.Equal("value", ex.ParamName);
            Assert.Equal(value, ex.ActualValue);
        }
    }

    [Fact]
    public void ADefinedMode_isStoredAsGiven()
    {
        Assert.Equal(PageBreakMode.None, new Paragraph("p", Body).BreakBefore);
        Assert.Equal(PageBreakMode.Page, new Paragraph("p", Body) { BreakBefore = PageBreakMode.Page }.BreakBefore);
        Assert.Equal(PageBreakMode.Recto, new ListElement { BreakBefore = PageBreakMode.Recto }.BreakBefore);
    }

    [Fact]
    public void AddingANullBreak_isRefusedAtTheCall()
    {
        using var doc = NewDocument();

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Add((PageBreak)null!));
        Assert.Equal("pageBreak", ex.ParamName);
    }

    // ── Public renderers and custom renderers (Add(IRenderer)) ───────────────

    [Fact]
    public void ABuiltInRendererAddedThroughAddRenderer_honoursItsElementsBreakBefore()
    {
        var table = new TableElement { BreakBefore = PageBreakMode.Page };
        table.AddRow().AddCell("cell");
        var list = new ListElement { BreakBefore = PageBreakMode.Page };
        list.Add("item", Body);

        using var doc = NewDocument();
        doc.Add(new Paragraph("first", Body));
        doc.Add(new ParagraphRenderer(new Paragraph("para", Body) { BreakBefore = PageBreakMode.Page }));
        doc.Add(new TableRenderer(table));
        doc.Add(new ListRenderer(list));
        doc.Add(new LayoutImageRenderer(new LayoutImage(FixtureImage) { Width = 20, Height = 20, BreakBefore = PageBreakMode.Page }));

        Assert.Equal(5, Pages(Save(doc)).Count);
    }

    private sealed class Probe : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));
        public void Draw(DrawContext context) { }
    }

    [Fact]
    public void ACustomRenderer_cannotBreak_andAZeroHeightPartialStillMakesAPage()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new Probe());
        doc.Add(new Paragraph("B", Body));

        Assert.Equal(["A|B"], Labels(Save(doc)));
    }

    // ── Measured equals drawn, calls, plans ──────────────────────────────────

    private static DocumentRenderer BandedRenderer(PdfDocument pdf, params IRenderer[] content)
    {
        var renderer = new DocumentRenderer(pdf, Page, Margins) { Footer = Footer(), RecordPlacements = true };
        foreach (var r in content)
            renderer.Add(r);
        return renderer;
    }

    [Fact]
    public void WithAFooterAndARectoBlank_measuredPagesEqualDrawnPages_andTheBlankHasNoBand()
    {
        var renderer = BandedRenderer(
            NewPdf(),
            new ParagraphRenderer(new Paragraph("A", Body)),
            new PageBreakRenderer(new PageBreak { Recto = true }),
            new ParagraphRenderer(new Paragraph("B", Body)));

        var pages = Pages(Render(renderer));

        Assert.Equal(3, renderer.MeasurePlan!.PageCount);
        Assert.Equal(renderer.MeasurePlan.PageCount, renderer.DrawPlan!.PageCount);
        Assert.Equal(["A|1/3", "", "B|3/3"], pages.Select(p => string.Join('|', p.Texts)).ToArray());
        Assert.Equal(string.Empty, pages[1].Operators);
    }

    [Fact]
    public void ABreak_addsNoLayoutCalls_whateverThePageCountBecomes()
    {
        int Calls(bool withBreak, bool recto)
        {
            var renderer = BandedRenderer(NewPdf(), new ParagraphRenderer(new Paragraph("A", Body)));
            if (withBreak)
                renderer.Add(new PageBreakRenderer(new PageBreak { Recto = recto }));
            renderer.Add(new ParagraphRenderer(new Paragraph("B", Body)));
            Render(renderer);
            return renderer.LayoutCalls;
        }

        // One measuring pass and one drawing pass over two elements: four calls, with or without a
        // break, and with or without the blank page it inserts.
        Assert.Equal(4, Calls(false, false));
        Assert.Equal(4, Calls(true, false));
        Assert.Equal(4, Calls(true, true));
    }

    [Fact]
    public void ARectoBreak_inADocumentThatAlreadyHoldsAPage_countsPhysicalPages()
    {
        var pdf = NewPdf();
        pdf.AddPage(Page);
        var renderer = new DocumentRenderer(pdf, Page, Margins);
        renderer.Add(new ParagraphRenderer(new Paragraph("A", Body) { BreakBefore = PageBreakMode.Recto }));

        var pages = Pages(Render(renderer));

        // Physical page 2 is even, so the layout adds a blank page 2 and starts on page 3.
        Assert.Equal(3, pages.Count);
        Assert.Equal(string.Empty, pages[1].Operators);
        Assert.Equal(["A"], pages[2].Texts);
    }

    [Fact]
    public void EachAppliedBreak_isReportedOnceInBothPasses_atTheTopOfTheContentArea()
    {
        var plain = new PageBreakRenderer(new PageBreak());
        var recto = new PageBreakRenderer(new PageBreak { Recto = true });
        var heading = new HeadingRenderer(new Heading("H", Body) { BreakBefore = PageBreakMode.Page });
        var renderer = BandedRenderer(
            NewPdf(),
            new ParagraphRenderer(new Paragraph("A", Body)),
            plain,
            recto,
            new ParagraphRenderer(new Paragraph("B", Body)),
            heading);

        Render(renderer);

        // plain and recto both land on page index 2 (page 3, after the blank page 2); the heading,
        // after B, starts page index 3. The footer takes the bottom of the area, so the top is the
        // margin. A heading reports its own placement as well, keyed by the heading.
        Assert.Equal(renderer.MeasurePlan!.Placements.Count, renderer.DrawPlan!.Placements.Count);
        var reports = renderer.DrawPlan.Placements;
        var breakReports = reports.Where(p => ReferenceEquals(p.Key, p.Renderer)).ToList();
        Assert.Equal(3, breakReports.Count);
        Assert.Same(plain, breakReports[0].Key);
        Assert.Same(recto, breakReports[1].Key);
        Assert.Same(heading, breakReports[2].Key);
        Assert.Equal([2, 2, 3], breakReports.Select(p => p.PageIndex).ToArray());
        Assert.All(breakReports, p =>
        {
            Assert.Equal(20, p.Y);
            Assert.Equal(0, p.Box.Height);
        });
        // The heading's own report is separate and keyed by the element, as before.
        Assert.Single(reports, p => p.Key is Heading);
    }

    [Fact]
    public void AnElementIndexInADiagnostic_countsAPageBreakInAddOrder()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph([new TextRun("   ", null!)]));

        Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullTextRunStyle, item.Code);
        Assert.Equal(2, item.ElementIndex);
        Assert.Equal(2, item.PageNumber);
    }

    // ── Allocation per break ─────────────────────────────────────────────────

    private static long AllocatedBy(int breaks)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        using var doc = NewDocument();
        doc.Add(new Paragraph("A", Body));
        for (var i = 0; i < breaks; i++)
            doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("B", Body));
        Save(doc);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void AllocationPerBreak_doesNotGrowWithTheNumberOfBreaks()
    {
        _ = AllocatedBy(10); // warm up the JIT and the static tables

        var baseline = AllocatedBy(0);
        var perBreakSmall = (AllocatedBy(1_000) - baseline) / 1_000.0;
        var perBreakLarge = (AllocatedBy(10_000) - baseline) / 10_000.0;

        Assert.True(
            perBreakLarge <= perBreakSmall * 1.5,
            $"{perBreakLarge:F1} bytes per break at 10,000 against {perBreakSmall:F1} at 1,000");
    }

    // ── Tagged document ──────────────────────────────────────────────────────

    [Fact]
    public void ATaggedDocumentWithARectoBlank_hasNoStructureElementOnTheBlankPage()
    {
        using var doc = NewDocument();
        doc.Tagged = true;
        doc.Add(new Paragraph("A", Body));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Heading("B", Body));
        var pdf = Save(doc);

        var pages = Pages(pdf);
        var text = Encoding.Latin1.GetString(pdf);
        var pgRefs = Regex.Matches(text, @"/Pg (\d+) 0 R")
            .Select(m => int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToList();

        Assert.Equal(3, pages.Count);
        Assert.Equal(string.Empty, pages[1].Operators);
        // The structure elements of A and of B reference their own pages and never the blank one.
        Assert.Contains(pages[0].ObjectNumber, pgRefs);
        Assert.Contains(pages[2].ObjectNumber, pgRefs);
        Assert.DoesNotContain(pages[1].ObjectNumber, pgRefs);
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    private string WriteToTemp(Document doc, string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, Save(doc));
        return path;
    }

    private static string PageText(string path, int page)
    {
        ExternalTool.TryRun(
            "pdftotext",
            ["-layout", "-f", page.ToString(CultureInfo.InvariantCulture), "-l", page.ToString(CultureInfo.InvariantCulture), path, "-"],
            out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        return stdout;
    }

    [Fact]
    public void RectoBlank_pdftotextReadsEachPageSeparately_andQpdfChecksTheFile()
    {
        using var doc = NewDocument();
        doc.Footer = Footer();
        doc.Add(new Paragraph("Alpha", Body));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("Bravo", Body));
        var path = WriteToTemp(doc, "recto.pdf");

        var one = PageText(path, 1);
        var two = PageText(path, 2);
        var three = PageText(path, 3);

        Assert.Contains("Alpha", one, StringComparison.Ordinal);
        Assert.Contains("1/3", one, StringComparison.Ordinal);
        Assert.Equal(string.Empty, two.Trim().Replace("\f", string.Empty, StringComparison.Ordinal));
        Assert.Contains("Bravo", three, StringComparison.Ordinal);
        Assert.Contains("3/3", three, StringComparison.Ordinal);

        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfA2u_withARectoBlank_veraPdfReportsCompliant()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) { OracleGate.Unavailable("platform font for the PDF/A oracle"); }

        using var doc = NewDocument();
        doc.Conformance = PdfConformance.PdfA2u;
        doc.Info.Title = "VellumPdf page break oracle";
        var style = new TextStyle { FontRef = doc.LoadTrueTypeFont(fontPath), FontSize = 12 };
        doc.Footer = new RunningBand("{page}/{pages}", style);
        doc.Add(new Paragraph("Alpha", style));
        doc.Add(new PageBreak { Recto = true });
        doc.Add(new Paragraph("Bravo", style));
        var path = WriteToTemp(doc, "recto-pdfa2u.pdf");
        Assert.Equal(3, Pages(File.ReadAllBytes(path)).Count);

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut, "veraPDF timed out");
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
        Assert.True(
            report.Contains("isCompliant=\"true\"", StringComparison.Ordinal),
            $"veraPDF reported the document non-compliant (exit {exit}).\n{report}");
    }
}
