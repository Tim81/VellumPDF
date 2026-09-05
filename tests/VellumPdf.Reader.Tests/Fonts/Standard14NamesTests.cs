// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Reader.Fonts;

namespace VellumPdf.Reader.Tests.Fonts;

public sealed class Standard14NamesTests
{
    [Theory]
    [InlineData("Helvetica")]
    [InlineData("Helvetica-Bold")]
    [InlineData("Helvetica-Oblique")]
    [InlineData("Helvetica-BoldOblique")]
    [InlineData("Times-Roman")]
    [InlineData("Times-Bold")]
    [InlineData("Times-Italic")]
    [InlineData("Times-BoldItalic")]
    [InlineData("Courier")]
    [InlineData("Courier-Bold")]
    [InlineData("Courier-Oblique")]
    [InlineData("Courier-BoldOblique")]
    [InlineData("Symbol")]
    [InlineData("ZapfDingbats")]
    public void TryResolve_theFourteenExactNames(string name)
    {
        Assert.True(Standard14Names.TryResolve(name, out var afmName));
        Assert.Equal(name, afmName);
    }

    [Fact]
    public void TryResolve_stripsSubsetTag()
    {
        Assert.True(Standard14Names.TryResolve("ABCDEF+Helvetica", out var afmName));
        Assert.Equal("Helvetica", afmName);
    }

    [Fact]
    public void TryResolve_arialBold_toHelveticaBold()
    {
        Assert.True(Standard14Names.TryResolve("Arial,Bold", out var afmName));
        Assert.Equal("Helvetica-Bold", afmName);
    }

    [Fact]
    public void TryResolve_timesNewRomanPSBoldItalicMT_toTimesBoldItalic()
    {
        Assert.True(Standard14Names.TryResolve("TimesNewRomanPS-BoldItalicMT", out var afmName));
        Assert.Equal("Times-BoldItalic", afmName);
    }

    [Fact]
    public void TryResolve_courierNew_toCourier()
    {
        Assert.True(Standard14Names.TryResolve("CourierNew", out var afmName));
        Assert.Equal("Courier", afmName);
    }

    [Fact]
    public void TryResolve_isCaseSensitive()
    {
        Assert.False(Standard14Names.TryResolve("arial", out _));
    }

    [Fact]
    public void TryResolve_helveticaNarrow_false()
    {
        Assert.False(Standard14Names.TryResolve("Helvetica-Narrow", out _));
    }

    [Fact]
    public void TryResolve_200CharacterName_false()
    {
        Assert.False(Standard14Names.TryResolve(new string('A', 200), out _));
    }

    [Fact]
    public void TryGetKernelFont_timesRoman_givesKernelEnum()
    {
        Assert.True(Standard14Names.TryGetKernelFont("Times-Roman", out var font));
        Assert.Equal(Standard14.TimesRoman, font);
    }

    [Fact]
    public void TryGetKernelFont_symbol_false()
    {
        Assert.False(Standard14Names.TryGetKernelFont("Symbol", out _));
    }
}
