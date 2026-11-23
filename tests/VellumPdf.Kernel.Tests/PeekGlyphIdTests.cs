// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>The glyph lookup that a pre-draw check uses must answer as <c>GetGlyphId</c> does and leave the subset alone, so a check that refuses leaves nothing for a retry to inherit.</summary>
public sealed class PeekGlyphIdTests
{
    public static TheoryData<string> Fonts() => ["LiberationMono-Regular.ttf", "DejaVuSans.ttf"];

    private static TrueTypeFontEmbedder Embed(string file) =>
        new(EmbeddedFontFixtures.Load(file), "TT1");

    /// <summary>What the embedder would write for its subset: the font program and the ToUnicode map, which together hold every registered glyph and code point.</summary>
    private static byte[] Written(TrueTypeFontEmbedder embedder)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        embedder.BuildFontFileStream().WriteTo(writer);
        embedder.BuildToUnicodeCMap().WriteTo(writer);
        return ms.ToArray();
    }

    private static IEnumerable<int> Scalars()
    {
        for (var cp = 0; cp <= 0x10FFFF; cp++)
            if (cp is < 0xD800 or > 0xDFFF) yield return cp;
    }

    /// <summary>Every scalar value, the BMP and the astral planes: the peek on one embedder equals the registering lookup on another, and the peeks changed nothing the embedder writes.</summary>
    [Theory]
    [MemberData(nameof(Fonts))]
    public void Peek_equalsGetGlyphId_forEveryScalar_andRegistersNothing(string file)
    {
        var peeker = Embed(file);
        var before = Written(peeker);
        var peeked = new ushort[0x110000];
        foreach (var cp in Scalars()) peeked[cp] = peeker.PeekGlyphId(cp);

        Assert.Equal(before, Written(peeker));

        var getter = Embed(file);
        var mismatches = Scalars().Where(cp => peeked[cp] != getter.GetGlyphId(cp)).Take(5).ToArray();
        Assert.Empty(mismatches);
        Assert.NotEqual(before, Written(getter));
    }

    /// <summary>A peek made after the registering lookup returns the registered answer, including the stand-in glyph the font's own cmap lacks.</summary>
    [Fact]
    public void Peek_afterGetGlyphId_returnsTheRegisteredGlyph_standInIncluded()
    {
        var embedder = Embed("LiberationMono-Regular.ttf");
        const int narrowNoBreakSpace = 0x202F;

        var registered = embedder.GetGlyphId(narrowNoBreakSpace);

        Assert.NotEqual((ushort)0, registered);
        Assert.Equal(embedder.GetGlyphId(0x00A0), registered);
        Assert.Equal(registered, embedder.PeekGlyphId(narrowNoBreakSpace));
    }

    /// <summary>The same through the public handle's internal pass-through, on a fresh document.</summary>
    [Fact]
    public void Handle_peekLeavesTheSubsetAsItWas()
    {
        using var doc = new VellumPdf.Document.PdfDocument();
        var handle = doc.UseTrueTypeFont(EmbeddedFontFixtures.Load("LiberationMono-Regular.ttf"));
        var before = Written(handle.Embedder);

        var gid = handle.PeekGlyphId('A');

        Assert.NotEqual((ushort)0, gid);
        Assert.Equal(before, Written(handle.Embedder));
        Assert.Equal(gid, handle.GetGlyphId('A'));
        Assert.NotEqual(before, Written(handle.Embedder));
    }

    /// <summary>
    /// The width measured without registering equals <c>MeasureString</c>'s on another embedder,
    /// for text with no glyph in the font, a tab, a line feed and the two spaces the font
    /// synthesizes, and leaves what the embedder writes as it was.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fonts))]
    public void MeasureStringWithoutRegistering_equalsMeasureString_andRegistersNothing(string file)
    {
        string[] texts = ["Hello", "a\tb\nc", "x\u202Fy\u2007z", "\u4E00 and \u0000", "\U0001F600", ""];
        var measurer = Embed(file);
        var before = Written(measurer);
        var widths = texts.Select(t => measurer.MeasureStringWithoutRegistering(t, 12)).ToArray();

        Assert.Equal(before, Written(measurer));

        var registering = Embed(file);
        Assert.Equal(texts.Select(t => registering.MeasureString(t, 12)), widths);
        Assert.NotEqual(before, Written(registering));
    }
}
