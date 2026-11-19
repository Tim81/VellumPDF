// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary><c>DocumentRenderer.Render</c> refusals come before the layout, "no pages" takes precedence, and Symbol and ZapfDingbats keep their justification divisor.</summary>
public sealed class RenderRetryAndOrderTests
{
    private static TextStyle Style(Standard14 font = Standard14.Helvetica) =>
        new() { FontRef = new FontReference(font), FontSize = 12 };

    private static Document Good() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 200),
        Margins = new EdgeInsets(10),
    };

    private static int Pages(byte[] pdf) =>
        Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    // ── DocumentRenderer.Render: refusals come before the layout ────────────

    private static (PdfDocument Pdf, DocumentRenderer Renderer) OnePageRenderer()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 200), new EdgeInsets(10));
        renderer.Add(new ParagraphRenderer(new Paragraph("Hello", Style())));
        return (pdf, renderer);
    }

    [Fact]
    public void Render_toANonWritableStream_throws_andARetryWritesOnePageNotTwo()
    {
        var (pdf, renderer) = OnePageRenderer();
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        var ex = Assert.Throws<ArgumentException>(() => renderer.Render(readOnly));
        Assert.Equal("destination", ex.ParamName);
        Assert.Empty(pdf.Pages);                      // refused before the layout added anything

        using var ok = new MemoryStream();
        renderer.Render(ok);

        Assert.Equal(1, Pages(ok.ToArray()));
    }

    [Fact]
    public void Render_toAClosedStream_throws_andARetryWritesOnePageNotTwo()
    {
        var (pdf, renderer) = OnePageRenderer();
        var closed = new MemoryStream();
        closed.Dispose();

        Assert.Throws<ArgumentException>(() => renderer.Render(closed));
        Assert.Empty(pdf.Pages);

        using var ok = new MemoryStream();
        renderer.Render(ok);
        Assert.Equal(1, Pages(ok.ToArray()));
    }

    [Fact]
    public void Render_withOptionsTheSaveCannotHonour_throwsBeforeTheLayout_andACorrectedRetryWritesOnePage()
    {
        var (pdf, renderer) = OnePageRenderer();
        pdf.UseObjectStreams = true;
        pdf.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });
        using var first = new MemoryStream();

        Assert.Throws<NotSupportedException>(() => renderer.Render(first));
        Assert.Empty(pdf.Pages);

        pdf.UseObjectStreams = false;
        using var ok = new MemoryStream();
        renderer.Render(ok);
        Assert.Equal(1, Pages(ok.ToArray()));
    }

    [Fact]
    public void Render_toANullStream_throwsBeforeTheLayout()
    {
        var (pdf, renderer) = OnePageRenderer();

        Assert.Throws<ArgumentNullException>(() => renderer.Render(null!));
        Assert.Empty(pdf.Pages);
    }

    [Fact]
    public void Render_afterASuccessfulRender_isStillRefusedAsWritten()
    {
        var (pdf, renderer) = OnePageRenderer();
        using var first = new MemoryStream();
        renderer.Render(first);
        var pagesAfterTheFirst = pdf.Pages.Count;

        using var second = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(second));
        Assert.Contains("already been written", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, second.Length);

        // The refusal comes before the layout, so the refused call has not appended a second copy of the page.
        // PdfDocument.Save raises the same exception later, which is why the message alone cannot tell the two apart.
        Assert.Equal(1, pagesAfterTheFirst);
        Assert.Equal(pagesAfterTheFirst, pdf.Pages.Count);
    }

    // ── "No pages" is reported first ─────────────────────────────────────────

    [Fact]
    public void Render_withNothingAdded_reportsNoPagesBeforeADestinationOrOptionProblem()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 200));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(readOnly));
        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);

        pdf.UseObjectStreams = true;
        pdf.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });
        using var ms = new MemoryStream();
        Assert.Contains("no pages", Assert.Throws<InvalidOperationException>(() => renderer.Render(ms)).Message, StringComparison.Ordinal);
    }

    /// <summary>With nothing added the layout still checks the geometry and the bands, so a band that consumes the page is refused as such and not as "no pages".</summary>
    [Fact]
    public void Render_withNothingAddedAndABandThatConsumesThePage_refusesTheGeometryBeforeNoPages()
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 200), new EdgeInsets(10))
        {
            Header = new RunningBand("h") { Height = 300 },
        };
        using var ms = new MemoryStream();

        var ex = Assert.Throws<ArgumentException>(() => renderer.Render(ms));

        Assert.Equal("margins", ex.ParamName);
    }

    [Fact]
    public void EmptyDocument_reportsNoPages_beforeAnUnwritableStream()
    {
        using var doc = Good();
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(readOnly));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmptyDocument_reportsNoPages_beforeAnUnwritableStream_async()
    {
        using var doc = Good();
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => doc.SaveAsync(readOnly, TestContext.Current.CancellationToken));

        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyDocument_reportsNoPages_beforeIncompatibleOptions()
    {
        using var doc = Good();
        doc.UseObjectStreams = true;
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "pw" });
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
        Assert.Contains("no pages", ex.Message, StringComparison.Ordinal);

        doc.UseObjectStreams = false;
        doc.Conformance = PdfConformance.PdfA2b;
        Assert.Contains("no pages", Assert.Throws<InvalidOperationException>(() => doc.Save(ms)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentWithContent_stillReportsTheDestinationBeforeTheLayout()
    {
        using var doc = Good();
        doc.Add(new Paragraph("Hello", Style()));
        using var readOnly = new MemoryStream(new byte[8], writable: false);

        Assert.Equal("destination", Assert.Throws<ArgumentException>(() => doc.Save(readOnly)).ParamName);
    }

    // ── Symbol and ZapfDingbats keep their justification divisor ─────────────

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    /// <summary>Both fonts measure every glyph as zero, so the first line of "aa bb cc" has the whole 200pt box as slack; divided by the two spaces it would put 100pt of word spacing on each, but the divisor, as in 2.3.3, counts the four gaps around the two spaces.</summary>
    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    public void JustifiedLineInAZeroMeasuredFont_keepsTheOldDivisor(Standard14 font)
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 400), Margins = new EdgeInsets(50) };
        doc.Add(new Paragraph("aa bb cc\ndd ee", Style(font)) { Alignment = HorizontalAlignment.Justify });
        using var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        var tw = D(Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value);

        Assert.Equal(50.0, tw, 3);        // 200 / 4 gaps; counting spaces alone would give 100
    }

    [Fact]
    public void JustifiedLineInHelvetica_isStillDividedOverItsSpaces()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 400), Margins = new EdgeInsets(50) };
        doc.Add(new Paragraph("aa bb cc\ndd ee", Style()) { Alignment = HorizontalAlignment.Justify });
        using var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        var tw = D(Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value);
        var natural = Style().MeasureString("aa bb cc");

        Assert.Equal((200 - natural) / 2, tw, 3);
    }
}
