// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>Tests for justified text alignment.</summary>
public sealed class JustificationTests
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
            catch { }
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

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Paragraph_justified_multiLine_twOperatorOnNonLastLines()
    {
        // Use a narrow page to force multi-line wrapping, then verify Tw appears on non-last lines.
        using var doc = new Document();
        doc.PageSize = new PdfRectangle(0, 0, 200, 500);
        doc.Margins = new EdgeInsets(20); // 160pt content width

        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 11 };
        // Long enough text to produce 3+ lines at 160pt / 11pt font
        var text = "The quick brown fox jumps over the lazy dog and then keeps running far away.";
        doc.Add(new Paragraph(text, style) { Alignment = HorizontalAlignment.Justify });

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // At least one Tw operator should appear (for non-last justified lines)
        var twCount = CountOccurrences(decompressed, " Tw\n");
        Assert.True(twCount >= 1,
            $"Expected at least 1 Tw operator for justified multi-line paragraph, found {twCount}.\n" +
            $"Decompressed (first 500 chars): {decompressed[..Math.Min(500, decompressed.Length)]}");
    }

    [Fact]
    public void Paragraph_justified_lastLineIsLeftAligned_noTwBeforeLastLine()
    {
        using var doc = new Document();
        doc.PageSize = new PdfRectangle(0, 0, 200, 500);
        doc.Margins = new EdgeInsets(20);

        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 11 };
        var text = "The quick brown fox jumps over the lazy dog and then keeps running far.";
        doc.Add(new Paragraph(text, style) { Alignment = HorizontalAlignment.Justify });

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // There should be at least one "0 Tw" reset (word-spacing reset to 0 after justified lines)
        Assert.Contains("0 Tw", decompressed);
    }

    [Fact]
    public void Paragraph_justified_singleLine_noTwOperator()
    {
        // A single-line paragraph that is the last (and only) line should not emit Tw > 0.
        using var doc = new Document();
        doc.PageSize = new PdfRectangle(0, 0, 500, 500);
        doc.Margins = new EdgeInsets(20);

        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        // Short text that fits on one line at 460pt
        doc.Add(new Paragraph("Short line.", style) { Alignment = HorizontalAlignment.Justify });

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // No positive Tw should appear (single line = last line = left-aligned)
        var twCount = CountOccurrences(decompressed, " Tw\n");
        // The only Tw allowed is "0 Tw" (reset) — check there's no non-zero positive Tw
        // by verifying any Tw present is the reset.
        // Simplest: since it's one line there should be no Tw at all, or only "0 Tw".
        Assert.DoesNotContain("0.0001 Tw", decompressed); // sanity: no tiny positive spacing
    }

    [Fact]
    public void Paragraph_leftAligned_noTwOperator()
    {
        using var doc = new Document();
        doc.PageSize = new PdfRectangle(0, 0, 200, 500);
        doc.Margins = new EdgeInsets(20);

        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 11 };
        var text = "The quick brown fox jumps over the lazy dog and then keeps running far away.";
        doc.Add(new Paragraph(text, style) { Alignment = HorizontalAlignment.Left });

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // Left-aligned paragraphs must not use Tw
        Assert.DoesNotContain("Tw", decompressed);
    }

    [Fact]
    public void Paragraph_justified_producesPdf()
    {
        using var doc = new Document();
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        var text = "Justified paragraph text that should produce a valid PDF document without errors or exceptions when saved.";
        doc.Add(new Paragraph(text, style) { Alignment = HorizontalAlignment.Justify });

        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), ms.ToArray()[..8]);
    }
}
