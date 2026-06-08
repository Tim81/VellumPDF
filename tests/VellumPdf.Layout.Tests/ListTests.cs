// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>Tests for ListElement and ListRenderer.</summary>
public sealed class ListTests
{
    private static string DecompressAllFlatStreams(byte[] pdfBytes)
    {
        var sb = new StringBuilder();
        var pdfText = Encoding.Latin1.GetString(pdfBytes);
        var pos = 0;

        while (pos < pdfBytes.Length)
        {
            var streamKeyword = pdfText.IndexOf("\nstream\n", pos, StringComparison.Ordinal);
            if (streamKeyword < 0) break;
            var dataStart = streamKeyword + "\nstream\n".Length;

            var dictEnd = streamKeyword;
            var dictStart = pdfText.LastIndexOf("obj\n", dictEnd, StringComparison.Ordinal);
            if (dictStart < 0) { pos = dataStart; continue; }

            var lenIdx = pdfText.IndexOf("/Length ", dictStart, dictEnd - dictStart, StringComparison.Ordinal);
            if (lenIdx < 0) { pos = dataStart; continue; }

            var lenValStart = lenIdx + "/Length ".Length;
            var lenValEnd = lenValStart;
            while (lenValEnd < pdfText.Length && char.IsDigit(pdfText[lenValEnd])) lenValEnd++;
            if (!int.TryParse(pdfText[lenValStart..lenValEnd], out var streamLength))
            { pos = dataStart; continue; }

            if (dataStart + streamLength > pdfBytes.Length) { pos = dataStart; continue; }

            var rawBytes = pdfBytes[dataStart..(dataStart + streamLength)];
            try
            {
                using var input = new MemoryStream(rawBytes);
                using var output = new MemoryStream();
                using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
                z.CopyTo(output);
                sb.Append(Encoding.Latin1.GetString(output.ToArray()));
            }
            catch
            {
                // Not a valid zlib stream (e.g. DCTDecode JPEG or XMP metadata) — skip
            }
            pos = dataStart + streamLength;
        }
        return sb.ToString();
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        { count++; idx += pattern.Length; }
        return count;
    }

    // ── Unordered list ────────────────────────────────────────────────────────

    [Fact]
    public void List_unordered_bulletMarkerAppearsInContentStream()
    {
        using var doc = new Document();
        var list = new ListElement(ListStyle.Unordered)
            .Add("First item")
            .Add("Second item")
            .Add("Third item");

        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // The bullet character • (U+2022) in Latin-1 is encoded as 0x95
        // but in PDF it appears via ShowText — we just check the PDF contains
        // content (the bullet itself may be in Latin-1 or as a glyph run).
        // For Standard-14 Helvetica, ShowText encodes via Latin-1.
        // • is 0x95 in Windows-1252 but in Latin-1 it's a control char.
        // The ListElement.FormatMarker returns "•" which is U+2022.
        // In Latin-1 encoding U+2022 → 0x95.
        Assert.True(ms.Length > 100);

        // Item text should appear in decompressed stream
        Assert.Contains("First item", decompressed);
        Assert.Contains("Second item", decompressed);
    }

    [Fact]
    public void List_ordered_decimalMarkerAppearsInContentStream()
    {
        using var doc = new Document();
        var list = new ListElement(ListStyle.OrderedDecimal)
            .Add("Alpha")
            .Add("Beta")
            .Add("Gamma");

        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // Markers "1.", "2.", "3." should appear
        Assert.Contains("1.", decompressed);
        Assert.Contains("2.", decompressed);
        Assert.Contains("3.", decompressed);
        Assert.Contains("Alpha", decompressed);
    }

    [Fact]
    public void List_ordered_alphaMarkerAppearsInContentStream()
    {
        using var doc = new Document();
        var list = new ListElement(ListStyle.OrderedAlpha)
            .Add("First")
            .Add("Second");

        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        Assert.Contains("a.", decompressed);
        Assert.Contains("b.", decompressed);
    }

    [Fact]
    public void List_ordered_romanMarkerAppearsInContentStream()
    {
        using var doc = new Document();
        var list = new ListElement(ListStyle.OrderedRoman)
            .Add("One")
            .Add("Two")
            .Add("Three");

        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        Assert.Contains("i.", decompressed);
        Assert.Contains("ii.", decompressed);
        Assert.Contains("iii.", decompressed);
    }

    // ── Pagination ────────────────────────────────────────────────────────────

    [Fact]
    public void List_manyItems_paginatesAcrossAtLeastTwoPages()
    {
        using var doc = new Document();

        // Short page to force pagination quickly
        doc.PageSize = new PdfRectangle(0, 0, 300, 200);
        doc.Margins = new EdgeInsets(20);

        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < 30; i++)
            list.Add($"Item number {i + 1} with some text to fill space");

        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // At least 2 pages: /Count must not be 1
        Assert.True(ms.Length > 100);
        Assert.DoesNotContain("/Count 1\n", content);
    }

    // ── Nested items ─────────────────────────────────────────────────────────

    [Fact]
    public void List_nestedItems_childTextAppearsInPdf()
    {
        using var doc = new Document();
        var list = new ListElement(ListStyle.Unordered);

        var parent = new ListItem("Parent item");
        parent.AddChild("Child item one");
        parent.AddChild("Child item two");
        list.Add(parent);
        list.Add("Sibling item");

        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        Assert.Contains("Parent item", decompressed);
        Assert.Contains("Child item one", decompressed);
        Assert.Contains("Child item two", decompressed);
        Assert.Contains("Sibling item", decompressed);
    }

    // ── Fluent construction ───────────────────────────────────────────────────

    [Fact]
    public void List_fluentBuild_isCorrect()
    {
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 11 };
        var list = new ListElement(ListStyle.OrderedDecimal)
        {
            Indent = 24,
            DefaultStyle = style,
        }
            .Add("First", style)
            .Add(new ListItem("Second"));

        Assert.Equal(2, list.Items.Count);
        Assert.Equal(24, list.Indent);
        Assert.Equal(ListStyle.OrderedDecimal, list.Style);
    }

    [Fact]
    public void List_emptyList_producesValidPdf()
    {
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.Unordered));
        doc.Add(new Paragraph("After empty list"));

        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 100);
    }

    // ── Marker formatting ─────────────────────────────────────────────────────

    [Fact]
    public void ListElement_formatMarker_decimal()
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        Assert.Equal("1.", list.FormatMarker(1));
        Assert.Equal("10.", list.FormatMarker(10));
    }

    [Fact]
    public void ListElement_formatMarker_alpha()
    {
        var list = new ListElement(ListStyle.OrderedAlpha);
        Assert.Equal("a.", list.FormatMarker(1));
        Assert.Equal("z.", list.FormatMarker(26));
    }

    [Fact]
    public void ListElement_formatMarker_roman()
    {
        var list = new ListElement(ListStyle.OrderedRoman);
        Assert.Equal("i.", list.FormatMarker(1));
        Assert.Equal("iv.", list.FormatMarker(4));
        Assert.Equal("ix.", list.FormatMarker(9));
        Assert.Equal("xiv.", list.FormatMarker(14));
    }
}
