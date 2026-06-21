// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Conformance.Tests.Oracle;
using Xunit;

namespace VellumPdf.Conformance.Tests;

public sealed class Type1GlyphsTests
{
    // The 52 glyphs the Noto Sans Shavian Type 1 program defines: .notdef, the 48 Shavian letters
    // (U+10450–U+1047F), and uni000D / uni00A0 / uniFEFF. Enumerated independently of the parser.
    private static readonly string[] _expected =
    [
        ".notdef", "uni000D", "uni00A0", "uniFEFF",
        "u10450", "u10451", "u10452", "u10453", "u10454", "u10455", "u10456", "u10457",
        "u10458", "u10459", "u1045A", "u1045B", "u1045C", "u1045D", "u1045E", "u1045F",
        "u10460", "u10461", "u10462", "u10463", "u10464", "u10465", "u10466", "u10467",
        "u10468", "u10469", "u1046A", "u1046B", "u1046C", "u1046D", "u1046E", "u1046F",
        "u10470", "u10471", "u10472", "u10473", "u10474", "u10475", "u10476", "u10477",
        "u10478", "u10479", "u1047A", "u1047B", "u1047C", "u1047D", "u1047E", "u1047F",
    ];

    [Fact]
    public void TryEnumerate_RealType1Program_ReturnsEveryGlyphName()
    {
        var (fontFile, length1, _, _) = Type1FontAsset.ToFontFile();

        var glyphs = Type1Glyphs.TryEnumerate(fontFile, length1);

        Assert.NotNull(glyphs);
        Assert.Equal(_expected.Length, glyphs!.Count);
        foreach (var name in _expected)
            Assert.Contains(name, glyphs);
    }

    [Fact]
    public void TryEnumerate_LocatesEexecWhenLength1IsAbsent()
    {
        // With an unknown /Length1 (-1) the parser falls back to scanning for the eexec keyword.
        var (fontFile, _, _, _) = Type1FontAsset.ToFontFile();

        var glyphs = Type1Glyphs.TryEnumerate(fontFile, length1: -1);

        Assert.NotNull(glyphs);
        Assert.Contains("u10450", glyphs!);
    }

    [Fact]
    public void TryEnumerate_Garbage_ReturnsNullWithoutThrowing()
    {
        Assert.Null(Type1Glyphs.TryEnumerate([1, 2, 3, 4, 5, 6, 7, 8], length1: 4));
    }
}
