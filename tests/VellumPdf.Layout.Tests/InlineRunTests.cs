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
        var decompressed = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

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
