// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Known answers for the six public record structs rewritten from positional declarations to
/// explicit properties, so that each property can carry its own remarks. The expected values
/// come from the positional declarations at 574c62b, where these tests also pass. They pin
/// <c>ToString</c>, equality, <c>with</c> and <c>Deconstruct</c>. They do not pin attributes:
/// the hand-written <c>Deconstruct</c> methods carry no <c>[CompilerGenerated]</c>.
/// </summary>
public sealed class RecordShapeTests
{
    private static string Invariant(Func<string> format)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return format();
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void ColorRgb_shape()
    {
        var c = new ColorRgb(0.25, 0.5, 1);
        Assert.Equal("ColorRgb { R = 0.25, G = 0.5, B = 1 }", Invariant(c.ToString));

        var (r, g, b) = c;
        Assert.Equal((0.25, 0.5, 1.0), (r, g, b));

        Assert.Equal(new ColorRgb(0.25, 0.5, 1), c);
        Assert.True(c == new ColorRgb(0.25, 0.5, 1));
        Assert.Equal(new ColorRgb(0.25, 0.5, 1).GetHashCode(), c.GetHashCode());
        Assert.NotEqual(new ColorRgb(0.25, 0.5, 0.75), c);
        Assert.Equal(new ColorRgb(0.25, 0.5, 0.75), c with { B = 0.75 });
        Assert.Equal(new ColorRgb(0, 0, 0), default);
    }

    [Fact]
    public void ColorCmyk_shape()
    {
        var c = new ColorCmyk(0, 0.5, 1, 0.25);
        Assert.Equal("ColorCmyk { C = 0, M = 0.5, Y = 1, K = 0.25 }", Invariant(c.ToString));

        var (cyan, magenta, yellow, black) = c;
        Assert.Equal((0.0, 0.5, 1.0, 0.25), (cyan, magenta, yellow, black));

        Assert.Equal(new ColorCmyk(0, 0.5, 1, 0.25), c);
        Assert.True(c == new ColorCmyk(0, 0.5, 1, 0.25));
        Assert.Equal(new ColorCmyk(0, 0.5, 1, 0.25).GetHashCode(), c.GetHashCode());
        Assert.NotEqual(new ColorCmyk(0, 0.5, 1, 0.5), c);
        Assert.Equal(new ColorCmyk(0, 0.5, 1, 0.5), c with { K = 0.5 });
        Assert.Equal(new ColorCmyk(0, 0, 0, 0), default);
    }

    [Fact]
    public void EdgeInsets_shape()
    {
        var e = new EdgeInsets(1, 2, 3, 4);
        Assert.Equal(
            "EdgeInsets { Top = 1, Right = 2, Bottom = 3, Left = 4, Horizontal = 6, Vertical = 4 }",
            Invariant(e.ToString));

        var (top, right, bottom, left) = e;
        Assert.Equal((1.0, 2.0, 3.0, 4.0), (top, right, bottom, left));

        Assert.Equal(new EdgeInsets(1, 2, 3, 4), e);
        Assert.True(e == new EdgeInsets(1, 2, 3, 4));
        Assert.Equal(new EdgeInsets(1, 2, 3, 4).GetHashCode(), e.GetHashCode());
        Assert.NotEqual(new EdgeInsets(1, 2, 3, 5), e);
        Assert.Equal(new EdgeInsets(1, 2, 3, 5), e with { Left = 5 });
        Assert.Equal(new EdgeInsets(0, 0, 0, 0), default);
    }

    [Fact]
    public void LayoutBox_shape()
    {
        var box = new LayoutBox(1, 2, 3, 4);
        Assert.Equal("(1.0,2.0 3.0×4.0)", Invariant(box.ToString));

        var (x, y, width, height) = box;
        Assert.Equal((1.0, 2.0, 3.0, 4.0), (x, y, width, height));

        Assert.Equal(new LayoutBox(1, 2, 3, 4), box);
        Assert.True(box == new LayoutBox(1, 2, 3, 4));
        Assert.Equal(new LayoutBox(1, 2, 3, 4).GetHashCode(), box.GetHashCode());
        Assert.NotEqual(new LayoutBox(1, 2, 3, 5), box);
        Assert.Equal(new LayoutBox(1, 2, 3, 5), box with { Height = 5 });
        Assert.Equal(new LayoutBox(0, 0, 0, 0), default);
    }

    [Fact]
    public void LayoutBox_ToString_formats_in_the_current_culture()
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-NL");
        try
        {
            Assert.Equal("(1,0,2,0 3,0×4,0)", new LayoutBox(1, 2, 3, 4).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }

    [Fact]
    public void BandTruncationWarning_shape()
    {
        var w = new BandTruncationWarning(RunningBandKind.Footer, 3, 10, 25);
        Assert.Equal(
            "BandTruncationWarning { Band = Footer, PageNumber = 3, DrawnCharacters = 10, "
            + "ResolvedCharacters = 25, DroppedCharacters = 15 }",
            Invariant(w.ToString));

        var (band, page, drawn, resolved) = w;
        Assert.Equal((RunningBandKind.Footer, 3, 10, 25), (band, page, drawn, resolved));

        Assert.Equal(new BandTruncationWarning(RunningBandKind.Footer, 3, 10, 25), w);
        Assert.True(w == new BandTruncationWarning(RunningBandKind.Footer, 3, 10, 25));
        Assert.Equal(
            new BandTruncationWarning(RunningBandKind.Footer, 3, 10, 25).GetHashCode(),
            w.GetHashCode());
        Assert.NotEqual(new BandTruncationWarning(RunningBandKind.Footer, 3, 10, 26), w);
        Assert.Equal(
            new BandTruncationWarning(RunningBandKind.Footer, 3, 10, 26),
            w with { ResolvedCharacters = 26 });
        Assert.Equal(new BandTruncationWarning(RunningBandKind.Header, 0, 0, 0), default);
    }

    [Fact]
    public void PieSlice_shape()
    {
        var s = new PieSlice(2.5, ColorRgb.Black, "a");
        Assert.Equal(
            "PieSlice { Value = 2.5, Color = ColorRgb { R = 0, G = 0, B = 0 }, Label = a }",
            Invariant(s.ToString));

        var (value, color, label) = s;
        Assert.Equal((2.5, ColorRgb.Black, "a"), (value, color, label));

        Assert.Null(new PieSlice(2.5, ColorRgb.Black).Label);
        Assert.Equal(new PieSlice(2.5, ColorRgb.Black, "a"), s);
        Assert.True(s == new PieSlice(2.5, ColorRgb.Black, "a"));
        Assert.Equal(new PieSlice(2.5, ColorRgb.Black, "a").GetHashCode(), s.GetHashCode());
        Assert.NotEqual(new PieSlice(2.5, ColorRgb.Black, "b"), s);
        Assert.Equal(new PieSlice(2.5, ColorRgb.Black, "b"), s with { Label = "b" });
        Assert.Equal(new PieSlice(0, new ColorRgb(0, 0, 0), null), default);
    }

    [Fact]
    public void NaN_components_compare_equal_to_themselves()
    {
        Assert.Equal(new LayoutBox(double.NaN, 0, 0, 0), new LayoutBox(double.NaN, 0, 0, 0));
        Assert.Equal(new EdgeInsets(double.NaN), new EdgeInsets(double.NaN));
        Assert.Equal(new ColorRgb(double.NaN, 0, 0), new ColorRgb(double.NaN, 0, 0));
    }
}
