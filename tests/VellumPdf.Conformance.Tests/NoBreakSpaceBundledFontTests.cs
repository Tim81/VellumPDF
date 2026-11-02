// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.IO;
using Xunit;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// #562, against fonts that really lack a glyph. Layout now keeps U+202F NARROW NO-BREAK SPACE in a
/// paragraph's text instead of folding it into a plain space, so the embedded font has to be able to
/// draw it. The Liberation Mono that <c>EmbedStandard14Font</c> bundles as the Courier substitute has
/// no glyph for U+202F (it does have U+00A0 and U+2007), so without a fallback a Courier document
/// containing one would have drawn the missing-glyph box where a space used to be. Liberation Sans,
/// the Helvetica substitute, has all three, which shows the fallback leaves a glyph the font has alone.
///
/// The code points are numbers, not escapes or literals, so the test pins which characters these are.
/// </summary>
public sealed class NoBreakSpaceBundledFontTests
{
    private const int NoBreakSpace = 0x00A0;
    private const int NarrowNoBreakSpace = 0x202F;
    private const int FigureSpace = 0x2007;

    [Fact]
    public void LiberationMono_lacksTheNarrowSpace_soItIsDrawnAsTheNoBreakSpace()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        var narrow = courier.GetGlyphId(NarrowNoBreakSpace);

        Assert.NotEqual(0, narrow);                                   // not the missing-glyph box
        Assert.Equal(courier.GetGlyphId(NoBreakSpace), narrow);       // the fallback
    }

    /// <summary>What is measured is what is drawn: both come through the same glyph lookup.</summary>
    [Fact]
    public void LiberationMono_measuresTheNarrowSpaceAsTheNoBreakSpace()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        var narrow = courier.MeasureString(((char)NarrowNoBreakSpace).ToString(), 12);
        var nbsp = courier.MeasureString(((char)NoBreakSpace).ToString(), 12);

        Assert.True(nbsp > 0);
        Assert.Equal(nbsp, narrow);
    }

    [Fact]
    public void LiberationMono_hasTheFigureSpace_soItKeepsItsOwnGlyph()
    {
        using var doc = new PdfDocument();
        var courier = doc.EmbedStandard14Font(Standard14.Courier);

        Assert.NotEqual(0, courier.GetGlyphId(FigureSpace));
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

        return Regex.Matches(text, @"^<([0-9A-F]{4})> <([0-9A-F]{4})>$", RegexOptions.Multiline)
            .Select(m => (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)))
            .ToList();
    }

    /// <summary>
    /// Liberation Mono has no U+202F, so it is drawn with the glyph of U+00A0. That glyph is also the
    /// one a plain space uses where the font maps both to it, and the ToUnicode CMap holds one string
    /// per glyph. The stand-in must not be the string a reader copies for the glyph.
    /// </summary>
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
}
