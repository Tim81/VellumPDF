// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="BarcodeRenderer"/> and <see cref="DocumentBarcodeExtensions"/>: pagination, alignment, tagging, and the fluent binding.</summary>
public sealed class BarcodeRendererTests
{
    [Fact]
    public void Constructor_nullBarcode_throws() =>
        Assert.Throws<ArgumentNullException>(() => new BarcodeRenderer(null!));

    [Fact]
    public void TooTallForRemainingSpace_overflowsToPage2()
    {
        using var doc = new VellumPdf.Layout.Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = EdgeInsets.Zero,
        };

        // The first barcode consumes 250pt of the 300pt-tall page, leaving 50pt. The second is
        // 100pt tall: it does not fit that 50pt remainder, but does fit a fresh 300pt page — the
        // "Nothing" -> new page -> retry path in DocumentRenderer.PlaceRenderer.
        doc.Add(new Code128Barcode("FILLER") { ShowText = false, BarHeight = 250 });
        doc.Add(new Code128Barcode("SECOND") { ShowText = false, BarHeight = 100 });

        var ms = new MemoryStream();
        doc.Save(ms);
        var pdfText = Encoding.Latin1.GetString(ms.ToArray());

        var countMatch = Regex.Match(pdfText, @"/Count (\d+)");
        Assert.True(countMatch.Success, "expected a /Count entry in the page tree");
        Assert.Equal(2, int.Parse(countMatch.Groups[1].Value));
    }

    [Fact]
    public void TooWideForPage_throwsArgumentException()
    {
        using var doc = new VellumPdf.Layout.Document
        {
            PageSize = new PdfRectangle(0, 0, 50, 300),
            Margins = EdgeInsets.Zero,
        };
        doc.Add(new QrCode("A") { ModuleSize = 5 }); // far wider than the 50pt page

        var ms = new MemoryStream();
        Assert.Throws<ArgumentException>(() => doc.Save(ms));
    }

    [Theory]
    [InlineData(HorizontalAlignment.Left, 8.0)]
    [InlineData(HorizontalAlignment.Center, 129.0)]
    [InlineData(HorizontalAlignment.Right, 250.0)]
    public void Alignment_offsetsTheSymbolHorizontally(HorizontalAlignment alignment, double expectedFirstRectX)
    {
        // Version-1 QR at ModuleSize 2: quiet zone 4 modules each side, matrix 21x21 ->
        // footprint (21 + 2*4) * 2 = 58pt wide on a 300pt-wide, zero-margin page. Row 0's first
        // dark run is the top-left finder corner (module (0,0) is always dark), so the very
        // first "re" operator's x is the symbol's left edge (quiet zone included).
        using var doc = new VellumPdf.Layout.Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = EdgeInsets.Zero,
        };
        doc.Add(new QrCode("A") { Version = 1, ErrorCorrection = QrErrorCorrection.L, ModuleSize = 2, Alignment = alignment });

        var ms = new MemoryStream();
        doc.Save(ms);
        var ops = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        var match = Regex.Match(ops, @"(?<x>-?[\d.]+) -?[\d.]+ -?[\d.]+ -?[\d.]+ re");
        Assert.True(match.Success, "expected at least one re operator");
        Assert.Equal(expectedFirstRectX, double.Parse(match.Groups["x"].Value), 3);
    }

    [Fact]
    public void Tagged_dataBearingBarcode_emitsFigureWithAltText()
    {
        using var doc = new VellumPdf.Layout.Document { Tagged = true, Language = "en-US" };
        doc.Add(new QrCode("hello"));

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Contains("/Figure", ops, StringComparison.Ordinal);
        Assert.DoesNotContain("/Artifact", ops, StringComparison.Ordinal);

        var raw = Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Figure", raw, StringComparison.Ordinal);
        Assert.Contains("/Alt", raw, StringComparison.Ordinal);
        Assert.True(ContainsUtf16BeLiteral(bytes, "QR code: hello"), "expected the default alt text as a /Alt UTF-16BE literal string");
    }

    [Fact]
    public void Decorative_emitsArtifactNotFigure()
    {
        using var doc = new VellumPdf.Layout.Document { Tagged = true, Language = "en-US" };
        doc.Add(new QrCode("hello") { Decorative = true });

        var ms = new MemoryStream();
        doc.Save(ms);
        var ops = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.Contains("/Artifact", ops, StringComparison.Ordinal);
        Assert.DoesNotContain("/Figure", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitAltText_overridesTheDefault()
    {
        using var doc = new VellumPdf.Layout.Document { Tagged = true, Language = "en-US" };
        doc.Add(new QrCode("hello") { AltText = "Custom description" });

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(ContainsUtf16BeLiteral(bytes, "Custom description"), "expected the explicit AltText as a /Alt UTF-16BE literal string");
        Assert.False(ContainsUtf16BeLiteral(bytes, "QR code: hello"), "the default alt text must not be written once AltText is set");
    }

    [Fact]
    public void DocumentAddExtension_qrCode_bindsAndProducesValidPdf()
    {
        using var doc = new VellumPdf.Layout.Document();
        var returned = doc.Add(new QrCode("https://example.com/vellumpdf"));

        Assert.Same(doc, returned); // fluent chaining returns the same document

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void DocumentAddExtension_nullDocument_throws() =>
        Assert.Throws<ArgumentNullException>(() =>
            VellumPdf.Barcodes.DocumentBarcodeExtensions.Add(null!, new QrCode("A")));

    [Fact]
    public void DocumentAddExtension_nullBarcode_throws()
    {
        using var doc = new VellumPdf.Layout.Document();
        Assert.Throws<ArgumentNullException>(() => doc.Add((Barcode)null!));
    }

    /// <summary>
    /// Whether <paramref name="pdf"/> contains <paramref name="text"/> encoded the way
    /// <c>PdfLiteralString.FromUnicode</c> writes a <c>/Alt</c> value: a UTF-16BE literal
    /// string with a leading <c>FE FF</c> byte-order mark.
    /// </summary>
    private static bool ContainsUtf16BeLiteral(byte[] pdf, string text)
    {
        var needle = new byte[2 + (text.Length * 2)];
        needle[0] = 0xFE;
        needle[1] = 0xFF;
        Encoding.BigEndianUnicode.GetBytes(text).CopyTo(needle, 2);

        for (var i = 0; i <= pdf.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (pdf[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }

        return false;
    }
}
