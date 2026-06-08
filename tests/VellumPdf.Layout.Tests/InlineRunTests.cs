// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>Tests for mixed-style inline text runs within a single paragraph.</summary>
public sealed class InlineRunTests
{
    // ── Helpers shared with LayoutFixTests ───────────────────────────────────

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

    // ── Tests ────────────────────────────────────────────────────────────────

    [Fact]
    public void Paragraph_twoFontStyles_bothFontsAppearInResources()
    {
        using var doc = new Document();

        var styleHelvetica = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        var styleCourier = new TextStyle { Font = Standard14.Courier, FontSize = 12 };

        var para = new Paragraph([
            new TextRun("Hello in Helvetica ", styleHelvetica),
            new TextRun("world in Courier", styleCourier),
        ]);
        doc.Add(para);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Both font resource names should appear in the PDF
        Assert.Contains("/Helvetica", content);
        Assert.Contains("/Courier", content);
        Assert.True(ms.Length > 100);
    }

    [Fact]
    public void Paragraph_twoColors_colorChangeAppearsInContentStream()
    {
        using var doc = new Document();

        var red = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, Color = new ColorRgb(1, 0, 0) };
        var blue = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, Color = new ColorRgb(0, 0, 1) };

        var para = new Paragraph([
            new TextRun("Red text ", red),
            new TextRun("blue text", blue),
        ]);
        doc.Add(para);

        var ms = new MemoryStream();
        doc.Save(ms);
        var decompressed = DecompressAllFlatStreams(ms.ToArray());

        // Red: "1 0 0 rg"
        Assert.Contains("1 0 0 rg", decompressed);
        // Blue: "0 0 1 rg"
        Assert.Contains("0 0 1 rg", decompressed);
    }

    [Fact]
    public void Paragraph_twoFontsAndColors_twoFontResourcesInPdf()
    {
        using var doc = new Document();

        var s1 = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, Color = new ColorRgb(1, 0, 0) };
        var s2 = new TextStyle { Font = Standard14.TimesRoman, FontSize = 14, Color = new ColorRgb(0, 0.5, 0) };

        var para = new Paragraph([
            new TextRun("First run ", s1),
            new TextRun("second run", s2),
        ]);
        doc.Add(para);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Both font names must appear as PDF resource entries
        Assert.Contains("/Helvetica", content);
        Assert.Contains("/Times-Roman", content);
    }

    [Fact]
    public void Paragraph_fluentAdd_buildsMultiRunParagraph()
    {
        using var doc = new Document();

        var s1 = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        var s2 = new TextStyle { Font = Standard14.HelveticaBold, FontSize = 12 };

        var para = new Paragraph("Normal text ", s1)
            .Add("bold text", s2);

        Assert.Equal(2, para.Runs.Count);

        doc.Add(para);
        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 100);
    }

    [Fact]
    public void Paragraph_singleRunBackCompat_textAndStyleAccessors()
    {
        var style = new TextStyle { Font = Standard14.Courier, FontSize = 10 };
        var para = new Paragraph("Hello", style);

        Assert.Equal("Hello", para.Text);
        Assert.Equal(style, para.Style);
        Assert.Single(para.Runs);
    }

    [Fact]
    public void Paragraph_multiRunWrapping_doesNotThrow()
    {
        using var doc = new Document();

        var s1 = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        var s2 = new TextStyle { Font = Standard14.Courier, FontSize = 12 };

        // Long paragraph that will force line wrapping
        var para = new Paragraph([
            new TextRun("The quick brown fox jumps over the lazy dog and ", s1),
            new TextRun("then keeps running far into the distance without stopping.", s2),
        ]);
        doc.Add(para);

        var ms = new MemoryStream();
        doc.Save(ms);   // must not throw
        Assert.True(ms.Length > 100);
    }
}
