// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

/// <summary>
/// Pins <see cref="SimpleFontEncodings"/> cell by cell. Every value here was read directly from a
/// rendered image of ISO 32000-2:2020 Annex D.2 (not from this reader's own output, and not from
/// <c>src/VellumPdf.Conformance/Rules/Fonts/SimpleFontEncoding.cs</c>).
/// </summary>
public sealed class SimpleFontEncodingsTests
{
    [Fact]
    public void WinAnsi_pinnedCells()
    {
        var t = SimpleFontEncodings.WinAnsi;
        Assert.Equal("A", t[0x41]);
        Assert.Equal("quotesingle", t[0x27]);
        Assert.Equal("grave", t[0x60]);
        // Footnote 3 (bullet fill): all unused codes above octal 40 map to bullet.
        Assert.Equal("bullet", t[0x7F]);
        Assert.Equal("Euro", t[0x80]);
        Assert.Equal("bullet", t[0x81]);
        Assert.Equal("bullet", t[0x8D]);
        Assert.Equal("bullet", t[0x8F]);
        Assert.Equal("bullet", t[0x90]);
        Assert.Equal("bullet", t[0x95]); // the one code the footnote names specifically.
        Assert.Equal("bullet", t[0x9D]);
        // Footnotes 5 and 6.
        Assert.Equal("space", t[0xA0]);
        Assert.Equal("hyphen", t[0xAD]);
        Assert.Equal("ydieresis", t[0xFF]);
    }

    [Fact]
    public void WinAnsi_nonNullCount_is224_everyCodeFrom0x20To0xFF()
    {
        var t = SimpleFontEncodings.WinAnsi;
        var nonNull = 0;
        for (var i = 0x20; i <= 0xFF; i++)
        {
            Assert.NotNull(t[i]);
            nonNull++;
        }
        Assert.Equal(224, nonNull);
        for (var i = 0x00; i <= 0x1F; i++)
            Assert.Null(t[i]);
    }

    [Fact]
    public void Standard_pinnedCells()
    {
        var t = SimpleFontEncodings.Standard;
        Assert.Equal("quoteright", t[0x27]); // the discriminating cell against WinAnsi's quotesingle.
        Assert.Equal("quoteleft", t[0x60]);
        Assert.Equal("fraction", t[0xA4]);
        Assert.Equal("fi", t[0xAE]);
        Assert.Equal("fl", t[0xAF]);
        Assert.Equal("oe", t[0xFA]);
        Assert.Null(t[0xFF]);
        Assert.Null(t[0x7F]);
    }

    [Fact]
    public void Standard_nonNullCount_is149()
    {
        var t = SimpleFontEncodings.Standard;
        var nonNull = 0;
        for (var i = 0; i < 256; i++)
            if (t[i] is not null)
                nonNull++;
        Assert.Equal(149, nonNull);
    }

    [Fact]
    public void MacRoman_pinnedCells()
    {
        var t = SimpleFontEncodings.MacRoman;
        Assert.Equal("Adieresis", t[0x80]);
        // Footnote 6's dual mapping: MacRoman 0312 (octal) also reads as space.
        Assert.Equal("space", t[0xCA]);
        // Footnote 1: Annex D.2 and its own text both read "currency" here, not the Euro sign
        // Apple's own later Mac OS Roman revision substituted.
        Assert.Equal("currency", t[0xDB]);
        Assert.Equal("fi", t[0xDE]);
        Assert.Equal("fl", t[0xDF]);
        Assert.Equal("caron", t[0xFF]);
        Assert.Null(t[0xF0]); // Annex D.2 lists no glyph at this code (Table 113's own "apple").
    }

    [Theory]
    // The 15 Table 113 cells the Conformance copy folds into its own MacRoman table (ISO
    // 32000-2's own (1, 0) cmap-fallback table, not part of Annex D.2's MacRomanEncoding), pinned
    // as undefined here by re-rendering Annex D.2 pp. 854-858 and finding no row for the name at
    // this code.
    [InlineData(0xAD)] // notequal
    [InlineData(0xB0)] // infinity
    [InlineData(0xB2)] // lessequal
    [InlineData(0xB3)] // greaterequal
    [InlineData(0xB6)] // partialdiff
    [InlineData(0xB7)] // summation
    [InlineData(0xB8)] // product
    [InlineData(0xB9)] // pi
    [InlineData(0xBA)] // integral
    [InlineData(0xBD)] // Omega
    [InlineData(0xC3)] // radical
    [InlineData(0xC5)] // approxequal
    [InlineData(0xC6)] // Delta
    [InlineData(0xD7)] // lozenge
    [InlineData(0xF0)] // apple
    public void MacRoman_table113CellsAreUndefined(int code)
    {
        Assert.Null(SimpleFontEncodings.MacRoman[code]);
    }

    [Fact]
    public void MacRoman_nonNullCount_is208()
    {
        // This reader's own count of the rendered Annex D.2 table: 224 codes 0x20-0xFF, minus
        // 0x7F (undefined in MacRoman, unlike WinAnsi), minus the 15 Table 113 cells above.
        var t = SimpleFontEncodings.MacRoman;
        var nonNull = 0;
        for (var i = 0x20; i <= 0xFF; i++)
            if (t[i] is not null)
                nonNull++;
        Assert.Equal(208, nonNull);
    }

    [Fact]
    public void Symbol_pinnedCells()
    {
        var t = SymbolFontMetrics.SymbolEncoding;
        Assert.Equal("space", t[0x20]);
        Assert.Equal("universal", t[0x22]);
        Assert.Equal("Alpha", t[0x41]);
        Assert.Equal("alpha", t[0x61]);
        Assert.Null(t[0x80]);
        Assert.Null(t[0x8D]);
        Assert.Null(t[0x8E]);
        Assert.Equal("Upsilon1", t[0xA1]);
        Assert.Equal("infinity", t[0xA5]);
        Assert.Equal("gradient", t[0xD1]);
        Assert.Equal("integral", t[0xF2]);
        Assert.Equal("bracerightbt", t[0xFE]);
        Assert.Null(t[0xFF]);
    }

    [Fact]
    public void Symbol_nonNullCount_is189()
    {
        var t = SymbolFontMetrics.SymbolEncoding;
        var nonNull = 0;
        for (var i = 0; i < 256; i++)
            if (t[i] is not null)
                nonNull++;
        Assert.Equal(189, nonNull);
    }

    [Fact]
    public void ZapfDingbats_pinnedCells()
    {
        var t = SymbolFontMetrics.ZapfDingbatsEncoding;
        Assert.Equal("space", t[0x20]);
        Assert.Equal("a2", t[0x22]);
        Assert.Equal("a10", t[0x41]);
        Assert.Equal("a60", t[0x61]);
        Assert.Equal("a89", t[0x80]); // AFM-only code, not in Annex D.6.
        Assert.Equal("a96", t[0x8D]); // AFM-only code, not in Annex D.6.
        Assert.Null(t[0x8E]);
        Assert.Equal("a101", t[0xA1]);
        Assert.Equal("a106", t[0xA5]);
        Assert.Equal("a157", t[0xD1]);
        Assert.Equal("a183", t[0xF2]);
        Assert.Equal("a191", t[0xFE]);
        Assert.Null(t[0xFF]);
    }

    [Fact]
    public void ZapfDingbats_nonNullCount_is202()
    {
        var t = SymbolFontMetrics.ZapfDingbatsEncoding;
        var nonNull = 0;
        for (var i = 0; i < 256; i++)
            if (t[i] is not null)
                nonNull++;
        Assert.Equal(202, nonNull);
    }

    [Theory]
    [InlineData("StandardEncoding")]
    [InlineData("WinAnsiEncoding")]
    [InlineData("MacRomanEncoding")]
    [InlineData("MacExpertEncoding")]
    public void TryGetNamed_recognisesTheFourNames(string name)
    {
        Assert.True(SimpleFontEncodings.TryGetNamed(name, out _));
    }

    [Theory]
    [InlineData("StandardEncodingX")]
    [InlineData("")]
    [InlineData("WinAnsi")]
    public void TryGetNamed_rejectsAnythingElse(string name)
    {
        Assert.False(SimpleFontEncodings.TryGetNamed(name, out _));
    }

    [Fact]
    public void MacExpert_isAllNull()
    {
        var t = SimpleFontEncodings.MacExpert;
        for (var i = 0; i < 256; i++)
            Assert.Null(t[i]);
    }

    [Fact]
    public void WinAnsi_definesEveryCode_thatStandardDefines()
    {
        // So §9.6.5.4's StandardEncoding fill (SimpleFontReader) changes nothing over a WinAnsi
        // base table; the twelve cells it does change are all MacRoman's (next test).
        for (var code = 0; code < 256; code++)
        {
            if (SimpleFontEncodings.Standard[code] is not null)
                Assert.NotNull(SimpleFontEncodings.WinAnsi[code]);
        }
    }

    [Fact]
    public void MacRoman_leavesExactlyTwelveStandardCodes_undefined()
    {
        // The cells §9.6.5.4's StandardEncoding fill adds over a /BaseEncoding /MacRomanEncoding
        // table, read off Annex D.2: each is a StandardEncoding column entry whose MacRoman column
        // is blank.
        var expected = new Dictionary<int, string>
        {
            [0xAD] = "guilsinglright",
            [0xB2] = "dagger",
            [0xB3] = "daggerdbl",
            [0xB6] = "paragraph",
            [0xB7] = "bullet",
            [0xB8] = "quotesinglbase",
            [0xB9] = "quotedblbase",
            [0xBA] = "quotedblright",
            [0xBD] = "perthousand",
            [0xC3] = "circumflex",
            [0xC5] = "macron",
            [0xC6] = "breve",
        };

        var actual = new Dictionary<int, string>();
        for (var code = 0; code < 256; code++)
        {
            if (SimpleFontEncodings.Standard[code] is { } name && SimpleFontEncodings.MacRoman[code] is null)
                actual[code] = name;
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SharedStatics_areImmutable_acrossFonts()
    {
        // A per-font table is always a fresh copy (SimpleFontReader.ToArray()s the shared span
        // before applying /Differences). Building the copy and mutating it must never affect the
        // shared static a later font's own copy is built from.
        var perFont = SimpleFontEncodings.WinAnsi.ToArray();
        perFont[0x41] = "B";
        Assert.Equal("B", perFont[0x41]);

        Assert.Equal("A", SimpleFontEncodings.WinAnsi[0x41]);

        var second = SimpleFontEncodings.WinAnsi.ToArray();
        Assert.Equal("A", second[0x41]);
    }
}
