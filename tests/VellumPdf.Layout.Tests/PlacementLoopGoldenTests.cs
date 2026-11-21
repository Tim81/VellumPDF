// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #288 pins for the placement loop on inputs that exist today: the bytes (after normalisation),
/// the order and areas of every <c>Layout</c> call, and what a caught <c>Render</c> exception
/// leaves in a caller-owned <see cref="PdfDocument"/>. Each value was recorded on the commit
/// before the loop was merged into one routine, so a difference here means the output or the
/// call sequence of an existing input moved.
/// </summary>
public sealed class PlacementLoopGoldenTests
{
    // Assert.Equal truncates long strings, and a pin is only useful if a failure prints the new
    // value.
    private static void Pin(string expected, string actual) =>
        Assert.True(expected == actual, $"computed {actual}");

    private static string Summary(RunningBand? header, RunningBand? footer, bool existingPage = false)
    {
        var pdf = NewPdf();
        if (existingPage)
            pdf.AddPage(Page);
        var renderer = new DocumentRenderer(pdf, Page, Margins) { Header = header, Footer = footer };
        var log = new CallLog();
        foreach (var r in StandardContent(log))
            renderer.Add(r);
        var bytes = Render(renderer);
        return $"{NormalisedSha256(bytes)} calls={Sha256(log.Text)} n={log.Calls} pages={PageCount(bytes)}";
    }

    [Fact]
    public void Plain_pinsBytesAndLayoutCalls() =>
        Pin("EF54931F3AF68214B4771AB1A0C7D6A033FF32C63B81EC31F635ED781BC37E41 calls=5D97D336CB4974A41BB055CEDB427D9218F684911ABAF90A92337B2637FD42C6 n=36 pages=8", Summary(null, null));

    [Fact]
    public void Footer_pinsBytesAndLayoutCalls() =>
        Pin("7E385010EDBBC5CC3F478F52B7E044C956711D15155EBFD704A08E905A20FC8D calls=CD9A78B740AB92E75C5B11ABC97C5E7A335CE41B0919195C880437881D243519 n=68 pages=8", Summary(null, new RunningBand("Page {page}")));

    [Fact]
    public void HeaderAndFooterWithPagesToken_pinBytesAndLayoutCalls() =>
        Pin("DB554F95C62B4DDBD2A7AB99D8F64112F3BC1E015CE352CB3E8777757B5781FF calls=5ADF195645B63065606DBC400D2EA692F093D9B1027C6C6BAE9E607D1AFEC8EB n=76 pages=9", Summary(new RunningBand("Header"), new RunningBand("{page} of {pages}")));

    // A caller-owned PdfDocument that already holds a page: {page} counts it, {pages} does not.
    [Fact]
    public void PdfDocumentWithAnExistingPage_andAFooter_pinsBytesAndLayoutCalls() =>
        Pin("4DBD3B2D411EECBDBAF7681AE2BE3E099573259114FC76A47044CDEBB0E4E6BB calls=CD9A78B740AB92E75C5B11ABC97C5E7A335CE41B0919195C880437881D243519 n=68 pages=9", Summary(null, new RunningBand("{page}/{pages}"), existingPage: true));

    // ── Too tall, in each pass, and what the caller's document holds afterwards ─

    private sealed class NothingRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Nothing();
        public void Draw(DrawContext context) { }
    }

    private static string CatchTooTall(RunningBand? footer)
    {
        var pdf = NewPdf();
        var renderer = new DocumentRenderer(pdf, Page, Margins) { Footer = footer };
        renderer.Add(new ParagraphRenderer(new Paragraph("before", Body)));
        renderer.Add(new NothingRenderer());
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(new MemoryStream()));
        return $"{ex.Message}|{pdf.Pages.Count}";
    }

    [Fact]
    public void TooTall_inTheDrawPass_leavesTheCommittedPages() =>
        Pin("An element is too tall to fit on a single page and cannot be rendered. Reduce the element's content or increase the page size.|2", CatchTooTall(null));

    // The drawing pass commits the page it has open before it throws: only Finish turns a page's
    // ContentBytes from null into an array.
    [Fact]
    public void TooTall_inTheDrawPass_commitsTheOpenPageBeforeThrowing()
    {
        var pdf = NewPdf();
        var renderer = new DocumentRenderer(pdf, Page, Margins);
        renderer.Add(new ParagraphRenderer(new Paragraph("before", Body)));
        renderer.Add(new NothingRenderer());
        Assert.Throws<InvalidOperationException>(() => renderer.Render(new MemoryStream()));
        Assert.All(pdf.Pages, page => Assert.NotNull(page.ContentBytes));
    }

    [Fact]
    public void TooTall_inTheMeasurePass_leavesNoPages() =>
        Pin("An element is too tall to fit on a single page and cannot be rendered. On a 300.0pt page, 40.0pt of margins and 18.4pt of running bands (header 0.0pt, footer 18.4pt) leave a content area 241.6pt tall. Reduce the element's content, lower the band heights, reduce the margins, or increase the page size.|0", CatchTooTall(new RunningBand("f")));

    // ── The continuation cap, in each pass ───────────────────────────────────

    private const string CapMessage =
        "A single element needed more than 50000 page continuations, so its layout is not converging. Either an overflow renderer never advances, or the element genuinely spans more pages than one element may and should be split into several, or its height sits within a rounding error of the content area, so pagination refuses and accepts it by turns.";

    private sealed class NeverAdvancing : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Partial(context.Area.WithHeight(0), this, this);
        public void Draw(DrawContext context) { }
    }

    private static string CatchCap(RunningBand? footer)
    {
        var pdf = NewPdf();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 200), new EdgeInsets(10)) { Footer = footer };
        renderer.Add(new NeverAdvancing());
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(new MemoryStream()));
        return $"{ex.Message}|{pdf.Pages.Count}";
    }

#pragma warning disable xUnit1069 // DocumentRenderer.Render takes no cancellation token.
    [Fact(Timeout = 300_000)]
    public void Cap_inTheDrawPass_pinsMessageAndPages() =>
        Pin(CapMessage + "|50001", CatchCap(null));

    [Fact(Timeout = 300_000)]
    public void Cap_inTheMeasurePass_pinsMessageAndPages() =>
        Pin(CapMessage + "|0", CatchCap(new RunningBand("f")));
#pragma warning restore xUnit1069

    // ── A footer and nothing to place ────────────────────────────────────────

    [Fact]
    public void BandAndNoContent_saysTheDocumentHasNoPages()
    {
        var pdf = NewPdf();
        var renderer = new DocumentRenderer(pdf, Page, Margins) { Footer = new RunningBand("f") };
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(new MemoryStream()));
        Pin("The document has no pages.|0", $"{ex.Message}|{pdf.Pages.Count}");
    }

    // ── A custom renderer that answers differently per call, with a band ─────

    /// <summary>Full at 20pt on its first call and at 200pt on every later one.</summary>
    private sealed class ShrinkThenGrow : IRenderer
    {
        private int _calls;
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(context.Area.WithHeight(_calls++ == 0 ? 20 : 200));
        public void Draw(DrawContext context) { }
    }

    [Fact]
    public void CustomRendererAnsweringDifferentlyPerCall_withABand_savesAndPinsBytes()
    {
        var pdf = NewPdf();
        var renderer = new DocumentRenderer(pdf, Page, Margins) { Footer = new RunningBand("{page} of {pages}") };
        var log = new CallLog();
        renderer.Add(Logging.Wrap(new ShrinkThenGrow(), log));
        renderer.Add(Logging.Wrap(new ParagraphRenderer(new Paragraph("after", Body)), log));
        var bytes = Render(renderer);
        Pin("9BE80EE031AA3DB29B5B00C5E6A7673FD3922EF88A009AAE42630DDE312115E1 calls=A1ED36B08776FD606503253BF737F648DAE802C9206EC1460A4B25BE0D10CF98 n=4 pages=1", $"{NormalisedSha256(bytes)} calls={Sha256(log.Text)} n={log.Calls} pages={PageCount(bytes)}");
    }

    // ── Cost: Layout calls and allocation, no clock (#400) ───────────────────

    private static (int Calls, long Allocated) MeasureCost(Func<List<IRenderer>> content, RunningBand? footer)
    {
        var pdf = NewPdf();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 595, 842), new EdgeInsets(40)) { Footer = footer };
        var log = new CallLog();
        foreach (var r in content())
            renderer.Add(Logging.Wrap(r, log));
        using var ms = new MemoryStream();
        var before = GC.GetAllocatedBytesForCurrentThread();
        renderer.Render(ms);
        return (log.Calls, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static List<IRenderer> ThousandParagraphs() =>
    [
        .. Enumerable.Range(0, 1000).Select(i => (IRenderer)new ParagraphRenderer(new Paragraph(ParagraphText(i), Body))),
    ];

    private static List<IRenderer> BigTable()
    {
        var table = new Elements.Table.TableElement();
        for (var r = 0; r < 1000; r++)
        {
            var row = table.AddRow();
            row.AddCell($"Row {r} cell A");
            row.AddCell($"Row {r} cell B with a little more text to wrap in the column");
        }
        return [new Rendering.Table.TableRenderer(table)];
    }

    [Fact]
    public void Cost_thousandParagraphs_pinsLayoutCallsAndTheAllocationRatio()
    {
        // Warm the JIT and the font metrics, so the first measured run does not carry them.
        MeasureCost(ThousandParagraphs, null);
        var plain = MeasureCost(ThousandParagraphs, null);
        var banded = MeasureCost(ThousandParagraphs, new RunningBand("Page {page}"));
        Assert.Equal((1076, 2116), (plain.Calls, banded.Calls));
        Assert.True(banded.Allocated <= plain.Allocated * 1.05, $"{plain.Allocated} {banded.Allocated}");
    }

    // Only the call counts are pinned. Table allocation is not: its cost belongs to #517.
    [Fact]
    public void Cost_thousandRowTable_pinsLayoutCalls()
    {
        var plain = MeasureCost(BigTable, null);
        var banded = MeasureCost(BigTable, new RunningBand("Page {page}"));
        Assert.Equal((30, 62), (plain.Calls, banded.Calls));
    }

    // ── PDF/A-2u, headings (the second splits across two pages), a footer with a page count ───

    private static readonly string Regular =
        Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    private static byte[] PdfA2uDocument()
    {
        using var doc = new Document
        {
            PageSize = Page,
            Margins = Margins,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Conformance = PdfConformance.PdfA2u,
            Tagged = true,
            Language = "en-US",
        };
        doc.Info.Title = "Placement loop";
        var handle = doc.LoadTrueTypeFont(Regular);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.SetFooter("Page {page} of {pages}", style);
        for (var chapter = 1; chapter <= 4; chapter++)
        {
            doc.Add(new Heading(string.Join(' ', Enumerable.Repeat($"Chapter {chapter} heading", chapter == 2 ? 30 : 1)), style));
            for (var i = 0; i < (chapter == 1 ? 4 : 6); i++)
                doc.Add(new Paragraph(ParagraphText(i), style));
        }

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void PdfA2uDocument_pinsBytes() =>
        Pin("C560DB01C9CA871DC8A9EB35839E86D5F4C3930ECAB4A3ABC7E1118838F87C92", NormalisedSha256(PdfA2uDocument(), foldToUnicodeLineEnds: true));

    [Fact]
    public void PdfA2uDocument_veraPdfReportsCompliant()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vellum-placement-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, PdfA2uDocument());
        try
        {
            ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
                out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);

            Assert.False(timedOut, $"veraPDF timed out.\nstderr:\n{stderr}");
            Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
            Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
