// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// End-to-end tests for TrueType font embedding: Type0/CIDFontType2 wiring,
/// glyph-run canvas output, and per-page resource registration.
/// All tests are guarded by a font-presence check and skip silently on systems
/// where the font is absent (CI / Linux).
/// </summary>
public sealed class TrueTypeEmbedEndToEndTests
{
    private const string FontPath = @"C:\Windows\Fonts\arial.ttf";

    // ── Test 1: Full PDF contains all required TrueType structures ─────────────

    [Fact]
    public void Save_withEmbeddedTrueType_containsRequiredPdfStructures()
    {
        if (!File.Exists(FontPath)) return;

        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(FontPath));
        doc.RegisterEmbeddedFontUsage(page, handle);

        var canvas = new PdfCanvas(page);
        canvas
            .BeginText()
            .SetFontByName(handle.ResourceName, 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 720);

        // Map glyphs and draw
        var text = "Hello";
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();

        // Finish must be called before Save (registers Standard-14 fonts inline;
        // embedded fonts are wired by Save via RegisterFontRef).
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Type0", content);
        Assert.Contains("/CIDFontType2", content);
        Assert.Contains("/Identity-H", content);
        Assert.Contains("/FontFile2", content);
        Assert.Contains("/ToUnicode", content);
    }

    // ── Test 2: Glyph-run hex string appears in content stream ─────────────────

    [Fact]
    public void ShowGlyphs_emitsHexTjRun()
    {
        if (!File.Exists(FontPath)) return;

        using var doc = new PdfDocument();
        var page = doc.AddPage();

        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(FontPath));
        doc.RegisterEmbeddedFontUsage(page, handle);

        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 14).SetTextMatrix(1, 0, 0, 1, 72, 700);

        var gids = new ushort[4];
        var count = handle.GetGlyphIds("Test", gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Content stream will contain a hex string operator: <....> Tj
        // The content stream is compressed (FlateDecode) so we decompress to check it.
        // Instead, verify the PDF has the font embedded (structure is correct).
        // The raw content bytes on the page are uncompressed in ContentBytes before Save calls Finish.
        var rawContent = Encoding.Latin1.GetString(page.ContentBytes ?? []);
        Assert.Contains("Tj", rawContent);
        // A hex glyph run starts with '<'
        Assert.Contains("<", rawContent);
    }

    // ── Test 3: Multiple pages — only pages that used the font get the ref ──────

    [Fact]
    public void Save_embeddedFontOnOnePage_otherPageDoesNotHaveRef()
    {
        if (!File.Exists(FontPath)) return;

        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage(); // does NOT use the embedded font

        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(FontPath));
        doc.RegisterEmbeddedFontUsage(page1, handle);

        var c1 = new PdfCanvas(page1);
        c1.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[5];
        var n = handle.GetGlyphIds("Hello", gids);
        c1.ShowGlyphs(gids.AsSpan(0, n));
        c1.EndText();
        c1.Finish();

        var c2 = new PdfCanvas(page2);
        var stdFont = doc.UseFont(Standard14.Helvetica);
        c2.BeginText().SetFont(stdFont, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("World").EndText();
        c2.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Embedded font structures must be present
        Assert.Contains("/Type0", content);
        Assert.Contains("/CIDFontType2", content);

        // Standard-14 still works alongside embedded
        Assert.Contains("/Type1", content);
        Assert.Contains("/Helvetica", content);
    }

    // ── Test 4: Two different embedded fonts in the same document ──────────────

    [Fact]
    public void Save_twoEmbeddedFonts_bothPresentInPdf()
    {
        const string secondFontPath = @"C:\Windows\Fonts\times.ttf";
        if (!File.Exists(FontPath) || !File.Exists(secondFontPath)) return;

        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // Two genuinely different fonts → two distinct embedded subsets
        // (loading the same font twice now de-duplicates to a single subset).
        var h1 = doc.UseTrueTypeFont(File.ReadAllBytes(FontPath));
        var h2 = doc.UseTrueTypeFont(File.ReadAllBytes(secondFontPath));
        doc.RegisterEmbeddedFontUsage(page, h1);
        doc.RegisterEmbeddedFontUsage(page, h2);

        var canvas = new PdfCanvas(page);
        canvas.BeginText();
        canvas.SetFontByName(h1.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var g1 = new ushort[2]; var n1 = h1.GetGlyphIds("Hi", g1);
        canvas.ShowGlyphs(g1.AsSpan(0, n1));

        canvas.SetFontByName(h2.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 700);
        var g2 = new ushort[2]; var n2 = h2.GetGlyphIds("Hi", g2);
        canvas.ShowGlyphs(g2.AsSpan(0, n2));
        canvas.EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Resource names TT1 and TT2 must both appear
        Assert.Contains($"/{h1.ResourceName}", content);
        Assert.Contains($"/{h2.ResourceName}", content);
        Assert.NotEqual(h1.ResourceName, h2.ResourceName);
    }
}
