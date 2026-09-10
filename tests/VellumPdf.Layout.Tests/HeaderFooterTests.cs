// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

public sealed class HeaderFooterTests
{
    private static string SaveToString(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>
    /// A band never set a fill colour, and the two text renderers do not bracket their fill colour
    /// in q/Q, so the band inherited whatever colour the page's last content left set. Bands are
    /// drawn from FinishCurrentPage, after the content, which is what makes the inherited value the
    /// content's rather than the page default. Measured before the fix on this exact document: the
    /// band's own text object held no rg at all, and the last one on the page was the paragraph's
    /// 1 0 0 rg, so a footer asking for blue rendered red.
    /// </summary>
    [Fact]
    public void Document_bandWithItsOwnColour_doesNotInheritTheContentColour()
    {
        var red = new TextStyle { FontSize = 10, Color = new ColorRgb(1, 0, 0) };
        var blue = new TextStyle { FontSize = 10, Color = new ColorRgb(0, 0, 1) };

        using var doc = new Document
        {
            PageSize = new VellumPdf.Document.PdfRectangle(0, 0, 300, 200),
            Margins = new EdgeInsets(50),
        };
        doc.Footer = new RunningBand("FOOTER", blue);
        doc.Add(new Paragraph("red body text", red));

        var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        // The band's colour is set, once, and it is the one the style asked for.
        Assert.Equal(1, PdfTestUtil.CountOccurrences(stream, "0 0 1 rg"));

        // And it is set inside the band's own text object rather than left to the content's: the
        // last colour operator in the stream is the band's, not the paragraph's.
        var lastBlue = stream.LastIndexOf("0 0 1 rg", StringComparison.Ordinal);
        var lastRed = stream.LastIndexOf("1 0 0 rg", StringComparison.Ordinal);
        Assert.True(lastBlue > lastRed,
            $"band colour at {lastBlue} should follow the content colour at {lastRed}");
    }

    [Fact]
    public void Document_withHeader_rendersWithoutException()
    {
        using var doc = new Document();
        doc.SetHeader("Page {page}");
        doc.Add("Some content");

        var ms = new MemoryStream();
        doc.Save(ms); // should not throw
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Document_withFooter_rendersWithoutException()
    {
        using var doc = new Document();
        doc.SetFooter("Page {page} of {pages}");
        doc.Add("Some content");

        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void Document_withHeader_producesValidPdf()
    {
        // Content streams are compressed so raw text is not visible; validate
        // structural properties instead (valid PDF header, page count, no exception).
        using var doc = new Document();
        doc.SetHeader("Page 1");
        doc.Add("Body text.");

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void Document_withFooter_producesValidPdf()
    {
        using var doc = new Document();
        doc.SetFooter("Footer text");
        doc.Add("Body text.");

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void Document_withHeaderAndMultiplePages_multiplePageObjects()
    {
        // Verifies that two-pass pagination correctly produces >= 2 pages.
        using var doc = new Document();
        doc.SetHeader("Pg {page}", alignment: HorizontalAlignment.Center);

        for (var i = 0; i < 60; i++)
            doc.Add($"Paragraph {i + 1}: The quick brown fox jumps over the lazy dog.");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        Assert.DoesNotContain("/Count 1\n", content);
    }

    [Fact]
    public void Document_withFooterPagesToken_pagesCountIsAccurate()
    {
        // Two-pass render; total page count is >= 2 for a document that overflows.
        using var doc = new Document();
        doc.SetFooter("Page {page} of {pages}");

        for (var i = 0; i < 60; i++)
            doc.Add($"Line {i + 1}: The quick brown fox jumps over the lazy dog.");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        // The /Count entry in the page tree gives us the actual page count.
        Assert.DoesNotContain("/Count 1\n", content);
    }

    [Fact]
    public void Document_withoutHeaderFooter_stillPaginatesCorrectly()
    {
        using var doc = new Document();
        for (var i = 0; i < 60; i++)
            doc.Add($"Paragraph {i + 1}.");

        var content = SaveToString(doc);
        Assert.DoesNotContain("/Count 1\n", content);
    }

    [Fact]
    public void SetHeader_returnsDocumentForChaining()
    {
        using var doc = new Document();
        var returned = doc.SetHeader("Header");
        Assert.Same(doc, returned);
    }

    [Fact]
    public void SetFooter_returnsDocumentForChaining()
    {
        using var doc = new Document();
        var returned = doc.SetFooter("Footer");
        Assert.Same(doc, returned);
    }

    [Fact]
    public void RunningBand_resolveSubstitutesPageAndPages()
    {
        var band = new RunningBand("Page {page} of {pages}");
        Assert.Equal("Page 3 of 10", band.Resolve(3, 10));
    }

    [Fact]
    public void RunningBand_resolveNoTokens_returnsTemplate()
    {
        var band = new RunningBand("Static header");
        Assert.Equal("Static header", band.Resolve(1, 5));
    }
}
