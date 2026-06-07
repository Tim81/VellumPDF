// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

public sealed class PdfDocumentTests
{
    [Fact]
    public void Save_emptyDocument_producesValidPdfBytes()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);

        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 0, "Output must not be empty");
        // PDF header
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
        // PDF footer
        var tail = bytes[^7..];
        Assert.Equal("%%EOF\n"u8.ToArray(), tail[1..]);
    }

    [Fact]
    public void Save_singlePageWithText_containsFontResource()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);

        canvas
            .BeginText()
            .SetFont(font, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720)
            .ShowText("Hello, VellumPdf!")
            .EndText();

        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);

        // The font resource and base font name are in the page dict (uncompressed).
        var content = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Helvetica", content);  // BaseFont name in font dict
        Assert.Contains("/Type1", content);       // Subtype in font dict
        Assert.Contains("/Font", content);        // Font resource key
    }

    [Fact]
    public void Save_multiplePages_pageCountCorrect()
    {
        using var doc = new PdfDocument();
        for (var i = 0; i < 3; i++)
            doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        // /Count 3 must appear in the Pages node
        Assert.Contains("/Count 3", content);
    }

    [Fact]
    public void Save_withInfo_writesMetadata()
    {
        using var doc = new PdfDocument();
        doc.Info.Title = "Test Document";
        doc.Info.Author = "Timothy van der Ham";
        doc.Info.Producer = "VellumPdf";
        doc.AddPage();

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // Info dict must be present (check the /Info key in the trailer)
        var content = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Info", content);
        Assert.Contains("/Author", content);
    }

    [Fact]
    public void DefaultPageSize_isA4()
    {
        using var doc = new PdfDocument();
        Assert.Equal(PageSize.A4.Width, doc.DefaultPageSize.Width, precision: 2);
        Assert.Equal(PageSize.A4.Height, doc.DefaultPageSize.Height, precision: 2);
    }

    [Fact]
    public void UseFont_sameFontTwice_returnsSameResource()
    {
        using var doc = new PdfDocument();
        var f1 = doc.UseFont(Standard14.Helvetica);
        var f2 = doc.UseFont(Standard14.Helvetica);
        Assert.Same(f1, f2);
    }

    [Fact]
    public void UseFont_differentFonts_differentResourceNames()
    {
        using var doc = new PdfDocument();
        var hv = doc.UseFont(Standard14.Helvetica);
        var cou = doc.UseFont(Standard14.Courier);
        Assert.NotEqual(hv.ResourceName, cou.ResourceName);
    }
}
