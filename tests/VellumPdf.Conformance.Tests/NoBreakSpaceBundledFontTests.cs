// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.IO;
using Xunit;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Liberation Mono, the Courier substitute <c>EmbedStandard14Font</c> bundles, has no glyph for U+202F (it has U+00A0
/// and U+2007), so a fallback must stop it drawing the missing-glyph box; Liberation Sans has all three and keeps its
/// own glyphs. The code points are numbers, not literals, so the test pins which characters these are.
/// </summary>
public sealed class NoBreakSpaceBundledFontTests
{
    private const int NoBreakSpace = 0x00A0;
    private const int NarrowNoBreakSpace = 0x202F;
    private const int FigureSpace = 0x2007;

    // Measurement of the fallback is not tested on Liberation Mono: its .notdef and U+00A0 advances are both 600, so a
    // measurement cannot tell the fallback from .notdef. NoBreakSpaceSyntheticFontTests measures it on a font where
    // the advances differ.
    [Fact]
    public void LiberationMono_lacksTheNarrowSpace_soItIsDrawnAsTheNoBreakSpace()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        var narrow = courier.GetGlyphId(NarrowNoBreakSpace);

        Assert.NotEqual(0, narrow);                                   // not the missing-glyph box
        Assert.Equal(courier.GetGlyphId(NoBreakSpace), narrow);       // the fallback
    }

    [Fact]
    public void LiberationMono_hasTheFigureSpace_soItKeepsItsOwnGlyph()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        var figure = courier.GetGlyphId(FigureSpace);

        // Its own glyph, not the stand-in a font without it would get: neither the missing-glyph box nor the glyph of
        // U+00A0 or U+0020, which is what a fallback that replaced the figure space would return.
        Assert.NotEqual(0, figure);
        Assert.NotEqual(courier.GetGlyphId(NoBreakSpace), figure);
        Assert.NotEqual(courier.GetGlyphId(0x20), figure);
    }

    [Fact]
    public void LiberationSans_hasAllThree_soNoneIsReplaced()
    {
        using var doc = new PdfDocument();
        var helvetica = doc.EmbedStandard14Font(Standard14.Helvetica);

        var nbsp = helvetica.GetGlyphId(NoBreakSpace);
        var narrow = helvetica.GetGlyphId(NarrowNoBreakSpace);

        Assert.NotEqual(0, nbsp);
        Assert.NotEqual(0, narrow);
        Assert.NotEqual(nbsp, narrow);      // its own narrow glyph, not the fallback
        Assert.NotEqual(0, helvetica.GetGlyphId(FigureSpace));
    }

    /// <summary>The fallback is for the two spaces only; another character the font lacks stays .notdef.</summary>
    [Fact]
    public void LiberationMono_aCharacterItLacksAndIsNotASpace_isStillNotdef()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        Assert.Equal(0, courier.GetGlyphId(0x4E00));    // a CJK ideograph
    }

    // ── The ToUnicode CMap: a stand-in glyph must not speak for a glyph that owns it ──────────

    /// <summary>
    /// The glyph-to-text entries of the font's /ToUnicode CMap, as written to the file: each
    /// <c>&lt;gid&gt; &lt;utf16be&gt;</c> line of its beginbfchar sets, as (glyph id, code point).
    /// </summary>
    private static List<(int Gid, int CodePoint)> ToUnicodeEntries(EmbeddedFontHandle font)
    {
        using var ms = new MemoryStream();
        font.Embedder.BuildToUnicodeCMap().WriteTo(new PdfWriter(ms));
        var raw = ms.ToArray();

        // The body sits between "stream\n" and "\nendstream" and is zlib-compressed.
        var start = System.Text.Encoding.Latin1.GetString(raw).IndexOf("\nstream\n", StringComparison.Ordinal) + "\nstream\n".Length;
        var end = System.Text.Encoding.Latin1.GetString(raw).LastIndexOf("\nendstream", StringComparison.Ordinal);
        using var inflated = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(new MemoryStream(raw, start, end - start), System.IO.Compression.CompressionMode.Decompress))
            z.CopyTo(inflated);
        var text = System.Text.Encoding.Latin1.GetString(inflated.ToArray());

        // \r?: the CMap is built with AppendLine, so its lines end in CRLF on Windows.
        return Regex.Matches(text, @"^<([0-9A-F]{4})> <([0-9A-F]{4})>\r?$", RegexOptions.Multiline)
            .Select(m => (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)))
            .ToList();
    }

    /// <summary>Liberation Mono has no U+202F, so it is drawn with the glyph of U+00A0; the ToUnicode CMap holds one string per glyph, and the stand-in must not be the string a reader copies for it.</summary>
    [Fact]
    public void LiberationMono_toUnicode_aStandInDoesNotSpeakForTheGlyphOfARealSpace()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        var space = courier.GetGlyphId(0x20);
        var nbsp = courier.GetGlyphId(NoBreakSpace);
        var narrow = courier.GetGlyphId(NarrowNoBreakSpace);
        Assert.Equal(nbsp, narrow);                       // the stand-in shares U+00A0's glyph

        var entries = ToUnicodeEntries(courier);

        // One entry for the glyph, and it names a code point the font really maps to it.
        var forNarrowGlyph = entries.Where(e => e.Gid == narrow).ToList();
        Assert.DoesNotContain(forNarrowGlyph, e => e.CodePoint == NarrowNoBreakSpace);
        Assert.Contains((nbsp, NoBreakSpace), forNarrowGlyph);
        Assert.Equal(space == nbsp ? 2 : 1, forNarrowGlyph.Count);
        Assert.DoesNotContain(entries, e => e.CodePoint == NarrowNoBreakSpace);
    }

    /// <summary>With nothing else on the glyph, the stand-in is all there is to name it, so it stays.</summary>
    [Fact]
    public void LiberationMono_toUnicode_aStandInAloneStillNamesItsGlyph()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        var narrow = courier.GetGlyphId(NarrowNoBreakSpace);
        var entries = ToUnicodeEntries(courier);

        Assert.Contains((narrow, NarrowNoBreakSpace), entries);
    }

    /// <summary>Characters the font lacks that are not one of the two spaces get no stand-in, so each keeps its own glyph-0 entry in the CMap.</summary>
    [Fact]
    public void LiberationMono_toUnicode_missingCharactersThatAreNotSpacesKeepTheirNotdefEntries()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);
        Assert.Equal(0, courier.GetGlyphId(0x4E00));
        Assert.Equal(0, courier.GetGlyphId(0x4E01));

        var entries = ToUnicodeEntries(courier);

        Assert.Contains((0, 0x4E00), entries);
        Assert.Contains((0, 0x4E01), entries);
    }

    /// <summary>
    /// A foreign handle for the same font program is merged into this document's own embedding at save, and its
    /// stand-in code points come with its glyphs: the foreign handle drew U+202F through U+00A0's glyph, so after the
    /// merge the CMap must not list U+202F for that glyph.
    /// </summary>
    [Fact]
    public void RegisterEmbeddedFontUsage_withAForeignHandleThatUsedAStandIn_doesNotListItInTheMergedToUnicode()
    {
        using var owner = new PdfDocument();
        var foreign = owner.EmbedStandard14Font(Standard14.Courier);
        var nbsp = foreign.GetGlyphId(NoBreakSpace);
        Assert.Equal(nbsp, foreign.GetGlyphId(NarrowNoBreakSpace));      // drawn with the stand-in

        using var pdf = new PdfDocument();
        var own = pdf.EmbedStandard14Font(Standard14.Courier);
        own.GetGlyphId(NoBreakSpace);
        var page = pdf.AddPage();
        pdf.RegisterEmbeddedFontUsage(page, foreign);
        pdf.Save(new MemoryStream());

        var entries = ToUnicodeEntries(own);

        Assert.Contains((nbsp, NoBreakSpace), entries);
        Assert.DoesNotContain(entries, e => e.CodePoint == NarrowNoBreakSpace);
    }

    // ── A handle belongs to the document that embedded it ────────────────────

    [Fact]
    public void RegisterEmbeddedFontUsage_withAHandleFromAnotherDocument_isRefusedBySaveNamingTheHandle()
    {
        using var owner = new PdfDocument();
        var foreign = owner.EmbedStandard14Font(Standard14.Courier);
        using var pdf = new PdfDocument();
        var page = pdf.AddPage();

        // The registration is accepted: whether the name is defined is decided at save, once every
        // UseTrueTypeFont call has been made.
        pdf.RegisterEmbeddedFontUsage(page, foreign);
        var ex = Assert.Throws<ArgumentException>(() => pdf.Save(new MemoryStream()));

        Assert.Equal("handle", ex.ParamName);
        Assert.Contains("belongs to a different document", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RegisterEmbeddedFontUsage_withItsOwnHandle_isAccepted()
    {
        using var pdf = new PdfDocument();
        var own = pdf.EmbedStandard14Font(Standard14.Courier);
        var page = pdf.AddPage();

        pdf.RegisterEmbeddedFontUsage(page, own);
    }

    [Fact]
    public void RegisterEmbeddedFontUsage_withNullArguments_namesThem()
    {
        using var pdf = new PdfDocument();
        var own = pdf.EmbedStandard14Font(Standard14.Courier);
        var page = pdf.AddPage();

        Assert.Equal("page", Assert.Throws<ArgumentNullException>(() => pdf.RegisterEmbeddedFontUsage(null!, own)).ParamName);
        Assert.Equal("handle", Assert.Throws<ArgumentNullException>(() => pdf.RegisterEmbeddedFontUsage(page, null!)).ParamName);
    }
}
