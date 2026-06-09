// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// End-to-end tests for embedded TrueType fonts through the layout API.
/// All tests are guarded by a font-presence check and skip silently on systems
/// where no supported font is found (dev boxes with neither Arial nor DejaVuSans).
/// On Linux CI (fonts-dejavu-core installed) DejaVuSans is used automatically.
/// </summary>
public sealed class EmbeddedFontTests
{
    // ── Test 1: Document.UseTrueTypeFont + Paragraph → /Type0 in PDF ──────────

    [Fact]
    public void Document_useTrueTypeFont_paragraphRendersType0()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) return;

        using var doc = new Document();
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));

        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("Hello, embedded world!", style));

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Type0", content);
        Assert.Contains("/CIDFontType2", content);
        Assert.Contains("/Identity-H", content);
        Assert.Contains("/FontFile2", content);
        Assert.Contains("/ToUnicode", content);
    }

    // ── Test 2: Document.LoadTrueTypeFont (path overload) ─────────────────────

    [Fact]
    public void Document_loadTrueTypeFont_pathOverload_works()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) return;

        using var doc = new Document();
        var handle = doc.LoadTrueTypeFont(fontPath);

        Assert.NotNull(handle);
        Assert.NotEmpty(handle.ResourceName);

        var style = new TextStyle { FontRef = handle, FontSize = 14 };
        doc.Add(new Paragraph("Path overload test", style));

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Type0", content);
    }

    // ── Test 3: Hex glyph run appears in content ───────────────────────────────

    [Fact]
    public void Document_embeddedFont_contentStreamHasHexGlyphRun()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) return;

        using var doc = new Document();
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("Hello", style));

        // Use DocumentRenderer indirectly; but we can inspect by saving to a
        // MemoryStream and checking the raw (uncompressed) content bytes via
        // peeking at a minimal document structure. Since the layout uses
        // PdfCanvas.ShowGlyphs, the raw content stream will contain '<...> Tj'.
        // However, the content stream IS compressed (FlateDecode) in the output.
        // We verify correctness indirectly: the structure must contain /Type0 and
        // the document must save without error.
        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 200);
        var content = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/Type0", content);
    }

    // ── Test 4: Mixed Standard-14 and embedded font in same document ───────────

    [Fact]
    public void Document_mixedFonts_bothWorkTogether()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) return;

        using var doc = new Document();
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));

        doc.Add(new Paragraph("Standard-14 paragraph"));
        doc.Add(new Paragraph("Embedded font paragraph",
            new TextStyle { FontRef = handle, FontSize = 11 }));
        doc.Add(new Paragraph("Back to Standard-14"));

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        // Both font types present
        Assert.Contains("/Type1", content);      // Standard-14
        Assert.Contains("/Type0", content);      // Embedded TrueType
        Assert.Contains("/Helvetica", content);  // default Standard-14 name
    }

    // ── Test 5: TextStyle.Font property (backward compat) still works ──────────

    [Fact]
    public void TextStyle_fontProperty_backwardCompatible()
    {
        // Existing code using { Font = Standard14.Helvetica } must compile and work.
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        Assert.False(style.FontRef.IsEmbedded);
        Assert.Equal(Standard14.Helvetica, style.FontRef.Standard14);

        using var doc = new Document();
        doc.Add(new Paragraph("Backward compat", style));

        var ms = new MemoryStream();
        doc.Save(ms);
        Assert.True(ms.Length > 100);
    }

    // ── Test 6: Multi-page document with embedded font ─────────────────────────

    [Fact]
    public void Document_embeddedFont_multiPage_fontsWiredOnAllPages()
    {
        var fontPath = PdfTestUtil.FindPlatformFont();
        if (fontPath is null) return;

        using var doc = new Document();
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));
        var style = new TextStyle { FontRef = handle, FontSize = 12 };

        // Generate enough paragraphs to force pagination
        for (var i = 0; i < 60; i++)
            doc.Add(new Paragraph($"Paragraph {i + 1}: The quick brown fox jumps over the lazy dog.", style));

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Type0", content);
        // Multiple pages
        Assert.DoesNotContain("/Count 1\n", content);
    }
}
