// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.Reader;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #603: <c>{chapter}</c> in a running band, switched on by <see cref="RunningBand.ChapterToken"/>.
/// A page is 300 x 300 pt with 20 pt margins and a header of 18.4 pt. A paragraph with a 150 pt
/// top margin is 164.4 pt of the content area, so two of them never share a page and one fits
/// below a heading.
/// Expected titles are worked out from where each heading is placed, not read back from the file.
/// </summary>
public sealed class ChapterTokenTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static readonly TextStyle Plain = new() { Font = Standard14.Helvetica, FontSize = 12 };

    private static Document NewDoc() => new() { PageSize = Page, Margins = Margins };

    private static Paragraph Tall(string text, TextStyle? style = null) =>
        new(text, style ?? Plain) { Margins = new EdgeInsets(150, 0, 0, 0) };

    private static Heading Chapter(
        string title, TextStyle? style = null, int level = 0, bool newPage = true, string? bookmark = null) =>
        new(title, style ?? Plain)
        {
            Level = level,
            BreakBefore = newPage ? PageBreakMode.Page : PageBreakMode.None,
            BookmarkTitle = bookmark,
        };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static RunningBand ChapterBand(string template, TextStyle? style = null,
        HorizontalAlignment alignment = HorizontalAlignment.Center) =>
        new(template, style ?? Plain, alignment) { ChapterToken = true };

    // The band's line on each page: the line of the page's text that starts with `prefix`.
    private static string[] BandLines(byte[] pdf, string prefix)
    {
        using var reader = PdfReader.Open(pdf);
        var result = new string[reader.PageCount];
        for (var i = 0; i < result.Length; i++)
        {
            var text = reader.GetPage(i).ExtractText().Text;
            result[i] = text.Split('\n').Select(l => l.Trim())
                .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) ?? "";
        }

        return result;
    }

    private static readonly string[] Dutch =
        ["Hoofdstuk één: Inleiding", "Hoofdstuk twee: Ontwikkeling", "Hoofdstuk drie: Slotbeschouwing ïë"];

    private static readonly string[] SixExpected =
    [
        "H: Hoofdstuk één: Inleiding | 1", "H: Hoofdstuk één: Inleiding | 2",
        "H: Hoofdstuk twee: Ontwikkeling | 3", "H: Hoofdstuk twee: Ontwikkeling | 4",
        "H: Hoofdstuk drie: Slotbeschouwing ïë | 5", "H: Hoofdstuk drie: Slotbeschouwing ïë | 6",
    ];

    // ── The acceptance table ─────────────────────────────────────────────────

    // What the text extractor reads off each page: the line that starts with `prefix`. The reader of
    // this library cannot read an embedded font's text, so these tests use the oracle, after qpdf
    // has passed the file.
    private static string[] PdftotextBandLines(byte[] pdf, string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), "vellum_chapter_" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            ExternalTool.TryRun("qpdf", ["--check", path], out var qexit, out var qout, out var qerr, out var qtimed);
            Assert.False(qtimed);
            Assert.True(qexit == 0, qout + qerr);
            Assert.Contains("No syntax or stream encoding errors found", qout, StringComparison.Ordinal);

            var pages = PageCount(pdf);
            var result = new string[pages];
            for (var page = 1; page <= pages; page++)
            {
                var n = page.ToString(CultureInfo.InvariantCulture);
                ExternalTool.TryRun("pdftotext", ["-layout", "-f", n, "-l", n, path, "-"],
                    out var exit, out var text, out var stderr, out var timedOut);
                Assert.False(timedOut);
                Assert.True(exit == 0, $"pdftotext page {page} exit {exit}: {stderr}");
                result[page - 1] = text.Split('\n').Select(l => l.Trim())
                    .FirstOrDefault(l => l.StartsWith(prefix, StringComparison.Ordinal)) ?? "";
            }

            return result;
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Six pages, three chapters of two pages each, in an embedded font.
    private static Document SixPages(Document doc, TextStyle style)
    {
        doc.Header = ChapterBand("H: {chapter} | {page}", style);
        for (var c = 0; c < 3; c++)
        {
            doc.Add(Chapter(Dutch[c], style));
            doc.Add(Tall($"inhoud {c}a", style));
            doc.Add(new PageBreak());
            doc.Add(Tall($"inhoud {c}b", style));
        }

        return doc;
    }

    [Fact]
    public void ThreeChaptersOverSixPages_pdftotextReadsTheHeaderOffEachPage()
    {
        using var doc = NewDoc();
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 12 };

        var pdf = Save(SixPages(doc, style));

        Assert.Equal(SixExpected, PdftotextBandLines(pdf, "H:"));
    }

    [Fact]
    public void APageBeforeTheFirstChapter_showsNoTitle_andNoLiteralToken()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("Boek | {chapter} | {page}");
        doc.Add(Tall("omslag"));
        doc.Add(Chapter("Eerste"));
        doc.Add(Tall("tekst"));

        var pdf = Save(doc);

        // The template's own text stays, and the token leaves an empty place between its spaces.
        Assert.Equal(["Boek |  | 1", "Boek | Eerste | 2"], FontReportSupport.ShowLines(pdf)
            .Where(l => l.Contains("Boek", StringComparison.Ordinal))
            .Select(l => l[1..l.IndexOf(") Tj", StringComparison.Ordinal)]));
        Assert.DoesNotContain("{chapter}", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void AChapterThatStartsMidPage_isTheOneThatPageShows()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Een"));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("kort", Plain));
        doc.Add(Chapter("Twee", newPage: false));
        doc.Add(new Paragraph("ook kort", Plain));

        // The page holds the end of "Een" above "Twee", and shows the one that starts on it.
        Assert.Equal(["H: Een", "H: Twee"], BandLines(Save(doc), "H:"));
    }

    [Fact]
    public void TheFirstChapterOnAPage_winsOverALaterOne()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Een"));
        doc.Add(Chapter("Twee", newPage: false));
        doc.Add(new Paragraph("tekst", Plain));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("volgende pagina", Plain));

        // Page 2 is in force from the last chapter drawn before it.
        Assert.Equal(["H: Een", "H: Twee"], BandLines(Save(doc), "H:"));
    }

    [Fact]
    public void ASplitChapterHeading_countsOnThePageItStartsOn_notWhereItsOverflowLands()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Een"));
        // A heading of many lines that starts at the top of page 2 and carries on over page 3.
        doc.Add(Chapter("Twee " + string.Join(' ', Enumerable.Range(0, 150).Select(i => $"lang{i}"))));
        // A second chapter, after the overflow, on the page the overflow lands on.
        doc.Add(Chapter("Drie", newPage: false));
        doc.Add(new Paragraph("slot", Plain));

        var lines = BandLines(Save(doc), "H:");

        Assert.Equal(3, lines.Length);
        Assert.StartsWith("H: Een", lines[0], StringComparison.Ordinal);
        Assert.StartsWith("H: Twee", lines[1], StringComparison.Ordinal);
        // The overflow of "Twee" is not a chapter start, so the first one that starts on the page
        // holding it is "Drie".
        Assert.Equal("H: Drie", lines[2]);
    }

    [Fact]
    public void ASplitChapter_isInForceOnThePagesItsOverflowFills()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Een"));
        var long_ = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"lang{i}"));
        doc.Add(Chapter("Twee " + long_));
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("slot", Plain));

        var lines = BandLines(Save(doc), "H:");

        Assert.True(lines.Length >= 4, string.Join(" / ", lines));
        Assert.StartsWith("H: Een", lines[0], StringComparison.Ordinal);
        for (var i = 1; i < lines.Length; i++)
            Assert.StartsWith("H: Twee", lines[i], StringComparison.Ordinal);
    }

    [Fact]
    public void OtherLevels_startNoChapter_andABookmarkTitleIsUsedWhenThereIsOne()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Een", bookmark: "Een (kort)"));
        doc.Add(Chapter("Paragraaf", level: 1, newPage: false));
        doc.Add(new PageBreak());
        doc.Add(Chapter("Subparagraaf", level: 2, newPage: false));
        doc.Add(Chapter("Twee"));

        Assert.Equal(["H: Een (kort)", "H: Een (kort)", "H: Twee"], BandLines(Save(doc), "H:"));
    }

    [Fact]
    public void ACustomRenderer_contributesNoChapter()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Een"));
        doc.Add(new PageBreak());
        doc.Add(new HeadingInACustomRenderer(Chapter("Verborgen", newPage: false)));

        Assert.Equal(["H: Een", "H: Een"], BandLines(Save(doc), "H:"));
    }

    private sealed class HeadingInACustomRenderer(Heading heading) : IRenderer
    {
        private readonly IRenderer _inner = new HeadingRenderer(heading);

        public LayoutResult Layout(LayoutContext context) => _inner.Layout(context);

        public void Draw(DrawContext context) => _inner.Draw(context);
    }

    // ── The token is opt-in ──────────────────────────────────────────────────

    [Fact]
    public void WithoutChapterToken_theTextStaysLiteral_asItAlwaysDid()
    {
        var doc = NewDoc();
        doc.Header = new RunningBand("{chapter} | {page}/{pages}", Plain);
        doc.Add(Chapter("Een"));
        doc.Add(new PageBreak());
        doc.Add(Tall("tekst"));

        var lines = FontReportSupport.ShowLines(Save(doc));

        Assert.Contains("({chapter} | 1/2) Tj", lines);
        Assert.Contains("({chapter} | 2/2) Tj", lines);
    }

    [Fact]
    public void ChapterToken_leavesATemplateWithoutTheTokenByteForByteAsItWas()
    {
        static byte[] Build(bool chapterToken)
        {
            var doc = NewDoc();
            doc.Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
            doc.DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");
            doc.Header = new RunningBand("Kop {page}/{pages}", Plain) { ChapterToken = chapterToken };
            doc.Footer = new RunningBand("{Chapter} {chapter", Plain) { ChapterToken = chapterToken };
            doc.Add(Chapter("Een"));
            doc.Add(Tall("a"));
            doc.Add(Chapter("Twee"));
            doc.Add(Tall("b"));
            return Save(doc);
        }

        Assert.Equal(NormalisedSha256(Build(false)), NormalisedSha256(Build(true)));
    }

    [Fact]
    public void ResolveKnowsNoHeadings_soItLeavesTheTokenAsGiven()
    {
        var band = ChapterBand("{chapter} {page}/{pages}");

        Assert.Equal("{chapter} 3/9", band.Resolve(3, 9));
    }

    // ── Cost and passes ──────────────────────────────────────────────────────

    private static DocumentRenderer Renderer(RunningBand? header)
    {
        var renderer = new DocumentRenderer(NewPdf(), Page, Margins) { Header = header };
        renderer.Add(new HeadingRenderer(Chapter("Een")));
        renderer.Add(new ParagraphRenderer(Tall("a")));
        renderer.Add(new HeadingRenderer(Chapter("Twee")));
        renderer.Add(new ParagraphRenderer(Tall("b")));
        return renderer;
    }

    [Fact]
    public void WithoutTheToken_theTrackerIsNeverCalled()
    {
        foreach (var header in new RunningBand?[]
        {
            null,
            new RunningBand("{page}/{pages}", Plain),
            new RunningBand("{chapter}", Plain),
            new RunningBand("{page}", Plain) { ChapterToken = true },
        })
        {
            var renderer = Renderer(header);
            Render(renderer);
            Assert.Equal(0, renderer.ChapterNotes);
        }
    }

    [Fact]
    public void WithTheToken_theTrackerSeesTheDrawingPassOnly_andTheLayoutIsUnchanged()
    {
        var without = Renderer(new RunningBand("{page}/{pages}", Plain));
        var with = Renderer(ChapterBand("{chapter} {page}/{pages}"));

        var withoutBytes = Render(without);
        var withBytes = Render(with);

        // Four elements drawn once each: a measuring pass that noted them would count eight.
        Assert.Equal(4, with.ChapterNotes);
        Assert.Equal(without.MeasureRuns, with.MeasureRuns);
        Assert.Equal(1, with.MeasureRuns);
        Assert.Equal(without.LayoutCalls, with.LayoutCalls);
        Assert.Equal(PageCount(withoutBytes), PageCount(withBytes));
    }

    [Fact]
    public void TheTwoPassesAgree_soAChapterAndThePageCountPrintTogether()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter} {page}/{pages}");
        for (var c = 0; c < 3; c++)
        {
            doc.Add(Chapter($"Kop{c}"));
            doc.Add(Tall("a"));
            doc.Add(new PageBreak());
            doc.Add(Tall("b"));
        }

        Assert.Equal(
            ["H: Kop0 1/6", "H: Kop0 2/6", "H: Kop1 3/6", "H: Kop1 4/6", "H: Kop2 5/6", "H: Kop2 6/6"],
            BandLines(Save(doc), "H:"));
    }

    [Fact]
    public void ABandWithTheTokenAndNoBreaks_pageCountAndChapterWorkWithoutAnyHeading()
    {
        var doc = NewDoc();
        doc.Footer = ChapterBand("F: [{chapter}] {page}/{pages}");
        doc.Add(Tall("a"));
        doc.Add(Tall("b"));

        Assert.Equal(["F: [] 1/2", "F: [] 2/2"], BandLines(Save(doc), "F:"));
    }

    [Fact]
    public void BothBandsShowTheSameChapter()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Footer = ChapterBand("F: {chapter} {page}");
        doc.Add(Chapter("Een"));
        doc.Add(Tall("a"));
        doc.Add(Chapter("Twee"));
        doc.Add(new Paragraph("b", Plain));

        var pdf = Save(doc);

        Assert.Equal(["H: Een", "H: Twee"], BandLines(pdf, "H:"));
        Assert.Equal(["F: Een 1", "F: Twee 2"], BandLines(pdf, "F:"));
    }

    [Fact]
    public void TenThousandHeadings_costTheTrackerNothingPerHeadingBeyondTheHeading()
    {
        static (long Allocated, DocumentRenderer Renderer) Run(int headings, bool token)
        {
            var renderer = new DocumentRenderer(NewPdf(), Page, Margins)
            {
                Header = token ? ChapterBand("{chapter}") : new RunningBand("{page}", Plain),
            };
            for (var i = 0; i < headings; i++)
                renderer.Add(new HeadingRenderer(Chapter($"Hoofdstuk {i}", newPage: false)));
            GC.Collect();
            var before = GC.GetAllocatedBytesForCurrentThread();
            Render(renderer);
            return (GC.GetAllocatedBytesForCurrentThread() - before, renderer);
        }

        double PerHeading(int headings)
        {
            var (with, withRenderer) = Run(headings, true);
            var (without, _) = Run(headings, false);
            Assert.Equal(headings, withRenderer.ChapterNotes);
            return (with - without) / (double)headings;
        }

        var small = PerHeading(1_000);
        var large = PerHeading(10_000);

        // The titles hold no control white space, so the tracker keeps the heading's own string
        // and allocates nothing of its own; the difference is the band text of the pages.
        Assert.True(large <= Math.Max(small * 1.5, 200), $"{small:F0} bytes per heading at 1,000, {large:F0} at 10,000");
    }

    [Fact]
    public void ALongTitle_costsAPageWhatTheCharactersThatFitCost()
    {
        static long Produced(int titleLength)
        {
            // The heading is short; the long title is its bookmark title, which is the chapter's.
            var heading = new Heading("T", Plain) { BookmarkTitle = new string('x', titleLength) };
            var renderer = new DocumentRenderer(NewPdf(), Page, Margins) { Header = ChapterBand("{chapter}") };
            renderer.Add(new HeadingRenderer(heading));
            for (var i = 0; i < 20; i++) renderer.Add(new ParagraphRenderer(Tall("p")));
            Render(renderer);
            return renderer.BandCharactersProduced;
        }

        // Twenty-one pages; what a page produces follows the width, not the 500,000 characters.
        Assert.Equal(Produced(1_000), Produced(500_000));
    }

    // ── What the title is ────────────────────────────────────────────────────

    [Fact]
    public void AControlCharacterInATitle_isDrawnAsASpace_andNeverReachesTheFont()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter}");
        doc.Add(Chapter("Ka\tp\nitel\f1\r2"));

        var pdf = Save(doc);

        var shown = Assert.Single(FontReportSupport.ShowLines(pdf), l => l.Contains("H:", StringComparison.Ordinal));
        Assert.Equal("(H: Ka p itel 1 2) Tj", shown);
    }

    [Fact]
    public void ATokenInsideATitle_isPrintedAsText_andNeverSubstitutedAgain()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("H: {chapter} | {page}");
        doc.Add(Chapter("A {page} B {chapter} C {pages}"));

        Assert.Equal(["H: A {page} B {chapter} C {pages} | 1"], BandLines(Save(doc), "H:"));
    }

    [Fact]
    public void ThePageLabel_andTheChapterPrintTogether()
    {
        var doc = NewDoc();
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman, "{chapter}-", 4);
        doc.Header = ChapterBand("H: {chapter} | {page}");
        doc.Add(Chapter("Een"));

        // The label is copied as it is: its own {chapter} is text, not a second substitution.
        Assert.Equal(["H: Een | {chapter}-iv"], BandLines(Save(doc), "H:"));
    }

    [Fact]
    public void ATitleLongerThanTheBand_isCut_andReportedOnce()
    {
        var doc = NewDoc();
        doc.Header = ChapterBand("{chapter}");
        var title = string.Join(' ', Enumerable.Range(0, 80).Select(i => $"woord{i}"));
        doc.Add(Chapter(title));
        doc.Add(Tall("a"));
        doc.Add(Chapter("kort"));

        var pdf = Save(doc);

        var cut = Assert.Single(doc.BandTruncations);
        Assert.Equal(RunningBandKind.Header, cut.Band);
        Assert.Equal(1, cut.PageNumber);
        Assert.Equal(title.Length, cut.ResolvedCharacters);
        Assert.True(cut.DrawnCharacters is > 0 and < 100);
        var item = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.BandTruncated);
        Assert.Equal(LayoutElementKind.Header, item.ElementKind);
        Assert.Contains("kort", BandLines(pdf, "kort")[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void ACharacterTheBandFontLacks_isReportedAsAnyBandTextIs()
    {
        var doc = NewDoc();
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 12 };
        doc.Header = ChapterBand("{chapter}", style);
        doc.Add(Chapter("Een", bookmark: "A ⚹ B"));

        Save(doc);

        var item = Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.NotdefGlyph);
        Assert.Equal(LayoutElementKind.Header, item.ElementKind);
        Assert.Contains("U+26B9", item.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnpairedSurrogateInABookmarkTitle_isRefusedWhereTheBandIsDrawnInAnEmbeddedFont()
    {
        var doc = NewDoc();
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 12 };
        doc.Header = ChapterBand("{chapter}", style);
        doc.Add(Chapter("Een", style, bookmark: "Een\uD800"));

        Assert.Throws<ArgumentException>(() => Save(doc));
    }

    [Theory]
    [InlineData(HorizontalAlignment.Left)]
    [InlineData(HorizontalAlignment.Right)]
    public void WithMirroredMargins_theChapterIsStillTheOnesOfThePage(HorizontalAlignment alignment)
    {
        var doc = NewDoc();
        doc.MirrorMargins = true;
        doc.Header = ChapterBand("H: {chapter} {page}", Plain, alignment);
        doc.Add(Chapter("Een"));
        doc.Add(Tall("a"));
        doc.Add(Chapter("Twee"));
        doc.Add(Tall("b"));

        Assert.Equal(["H: Een 1", "H: Twee 2"], BandLines(Save(doc), "H:"));
    }

    // ── Conformance ──────────────────────────────────────────────────────────

    [Fact]
    public void ADutchChapterHeader_inPdfA2u_passesVeraPdf_andTheHeaderReadsBack()
    {
        var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        using (doc)
        {
            doc.PageSize = Page;
            doc.Margins = Margins;
            var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 12 };
            doc.Header = ChapterBand("H: {chapter} | {page}", style);
            doc.Footer = new RunningBand("{page}", style);
            for (var c = 0; c < 3; c++)
            {
                doc.Add(Chapter(Dutch[c], style));
                doc.Add(Tall($"inhoud {c}a", style));
                doc.Add(new PageBreak());
                doc.Add(Tall($"inhoud {c}b", style));
            }

            var pdf = FontReportSupport.Save(doc);

            Assert.Equal(SixExpected, PdftotextBandLines(pdf, "H:"));
            Assert.Empty(FontReportSupport.FailedRules(FontReportSupport.VeraPdf("2u", pdf)));
        }
    }
}
