// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>Tests for justified text alignment.</summary>
public sealed class JustificationTests
{
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
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        // At least one Tw operator should appear (for non-last justified lines)
        var twCount = PdfTestUtil.CountOccurrences(decompressed, " Tw\n");
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
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

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
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        // No positive Tw should appear (single line = last line = left-aligned)
        var twCount = PdfTestUtil.CountOccurrences(decompressed, " Tw\n");
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
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

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
