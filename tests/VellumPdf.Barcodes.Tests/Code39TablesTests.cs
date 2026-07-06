// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Code39;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="Code39Tables"/>: the structural invariants of the ISO/IEC 16388 pattern data.</summary>
public sealed class Code39TablesTests
{
    [Fact]
    public void Characters_hasFortyThreeEntries() => Assert.Equal(43, Code39Tables.Characters.Length);

    [Theory]
    [InlineData('0', 0)]
    [InlineData('9', 9)]
    [InlineData('A', 10)]
    [InlineData('Z', 35)]
    [InlineData('-', 36)]
    [InlineData('.', 37)]
    [InlineData(' ', 38)]
    [InlineData('$', 39)]
    [InlineData('/', 40)]
    [InlineData('+', 41)]
    [InlineData('%', 42)]
    public void ValueOf_matchesTheModulo43ValueTable(char c, int expectedValue) =>
        Assert.Equal(expectedValue, Code39Tables.ValueOf(c));

    [Fact]
    public void ValueOf_lowercaseLetter_isNotAStandardCharacter() => Assert.Equal(-1, Code39Tables.ValueOf('a'));

    [Fact]
    public void EveryPattern_hasNineElements_withExactlyThreeWide()
    {
        foreach (var c in Code39Tables.Characters)
        {
            var pattern = Code39Tables.PatternOf(c);
            Assert.Equal(9, pattern.Length);

            var wideCount = pattern.Count(e => e == 'W');
            Assert.True(wideCount == 3, $"'{c}' has {wideCount} wide elements (expected 3): {pattern}");
            Assert.True(pattern.All(e => e is 'N' or 'W'), $"'{c}' pattern has an unexpected element: {pattern}");
        }
    }

    [Fact]
    public void EveryPattern_isUnique()
    {
        var patterns = Code39Tables.Characters.Select(Code39Tables.PatternOf).ToList();
        Assert.Equal(patterns.Count, patterns.Distinct().Count());
    }

    [Fact]
    public void StartStopPattern_hasNineElements_withExactlyThreeWide_andIsDistinctFromEveryDataCharacter()
    {
        Assert.Equal(9, Code39Tables.StartStopPattern.Length);
        Assert.Equal(3, Code39Tables.StartStopPattern.Count(e => e == 'W'));

        foreach (var c in Code39Tables.Characters)
            Assert.NotEqual(Code39Tables.StartStopPattern, Code39Tables.PatternOf(c));
    }

    [Fact]
    public void PatternOf_rejectsANonStandardCharacter() => Assert.Throws<ArgumentException>(() => Code39Tables.PatternOf('a'));

    [Theory]
    [InlineData(0, "%U")]   // NUL
    [InlineData(32, " ")]   // space: literal, already in the standard 43-character set
    [InlineData(45, "-")]   // '-': literal
    [InlineData(46, ".")]   // '.': literal
    [InlineData(48, "0")]   // '0': literal
    [InlineData(57, "9")]   // '9': literal
    [InlineData(65, "A")]   // 'A': literal
    [InlineData(90, "Z")]   // 'Z': literal
    [InlineData(36, "/D")]  // literal '$' is itself a shift precedence code, so it needs a substitution
    [InlineData(37, "/E")]  // literal '%' likewise needs a substitution
    [InlineData(42, "/J")]  // literal '*' (the start/stop delimiter) needs a substitution
    [InlineData(43, "/K")]  // literal '+' likewise needs a substitution
    [InlineData(47, "/O")]  // literal '/' likewise needs a substitution
    [InlineData(97, "+A")]  // 'a': lowercase shift pair
    [InlineData(122, "+Z")] // 'z'
    [InlineData(1, "$A")]   // SOH: control-character shift pair
    [InlineData(26, "$Z")]  // SUB
    [InlineData(127, "%T")] // DEL
    public void FullAsciiSubstitution_matchesTheAimUss39Table(int asciiCode, string expected) =>
        Assert.Equal(expected, Code39Tables.FullAsciiSubstitution(asciiCode));

    [Fact]
    public void FullAsciiSubstitution_everyEntry_expandsToOneOrTwoStandardCharacters()
    {
        for (var code = 0; code < 128; code++)
        {
            var substitution = Code39Tables.FullAsciiSubstitution(code);
            Assert.True(substitution.Length is 1 or 2, $"code point {code} substitution '{substitution}' has an unexpected length.");
            foreach (var c in substitution)
                Assert.True(Code39Tables.ValueOf(c) >= 0, $"code point {code} substitution '{substitution}' contains non-standard character '{c}'.");
        }
    }

    [Fact]
    public void FullAsciiSubstitution_rejectsCodePointsOutsideAscii() =>
        Assert.Throws<ArgumentException>(() => Code39Tables.FullAsciiSubstitution(128));
}
