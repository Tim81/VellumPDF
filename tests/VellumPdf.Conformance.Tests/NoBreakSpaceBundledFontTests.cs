// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
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
}
