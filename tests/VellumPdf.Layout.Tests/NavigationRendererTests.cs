// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #262: a <see cref="PdfDocument"/> that already holds page-label ranges, handed to
/// <see cref="DocumentRenderer"/>. The renderer runs the page-independent save checks before it lays
/// out, when the document has no pages, so the range check must not be among them.
/// </summary>
public sealed class NavigationRendererTests
{
    private static DocumentRenderer Renderer(PdfDocument pdf)
    {
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 300, 200), new EdgeInsets(10));
        renderer.Add(new ParagraphRenderer(new Paragraph(string.Join(' ', Enumerable.Repeat("word", 900)))));
        return renderer;
    }

    [Fact]
    public void Render_withARangeOnAPageTheLayoutProduces_writesTheRange()
    {
        var pdf = new PdfDocument();
        pdf.AddPageLabelRange(1, PdfPageLabelStyle.LowerRoman);
        using var ms = new MemoryStream();

        Renderer(pdf).Render(ms);

        Assert.True(pdf.Pages.Count > 1, "the content must span pages for the range to be reachable");
        var text = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/PageLabels <<\n/Nums [0 <<\n/S /D\n>> 1 <<\n/S /r\n>>]\n>>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_withARangePastTheLastPage_isRefusedAfterTheLayout_andTheKernelDocumentCanBeSaved()
    {
        var pdf = new PdfDocument();
        pdf.AddPageLabelRange(500, PdfPageLabelStyle.Decimal);
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => Renderer(pdf).Render(ms));

        Assert.Contains("page index 500", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ms.Length);
        // The layout's pages are in the kernel document, which is not marked written.
        var laidOut = pdf.Pages.Count;
        Assert.True(laidOut > 1);
        while (pdf.Pages.Count < 501)
            pdf.AddPage();
        using var saved = new MemoryStream();
        pdf.Save(saved);
        Assert.Contains("500 <<\n/S /D\n>>", Encoding.Latin1.GetString(saved.ToArray()), StringComparison.Ordinal);
    }
}
