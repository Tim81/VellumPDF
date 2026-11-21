// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// /FontBBox of an embedded font is the font's own <c>head</c> box scaled to 1000 units per em and
/// rounded away from the glyphs (#596). It used to be <c>[0, hhea descender, 1000 * 1000 /
/// unitsPerEm, hhea ascender]</c>, which is 488 wide for every 2048-unit font.
///
/// The expected numbers are written as literals. They are the <c>head</c> xMin, yMin, xMax and yMax
/// read from each font with fontTools 4.62.1, times 1000 / unitsPerEm, floor for the two minima and
/// ceiling for the two maxima: DejaVu Sans (2048 units) -2090 -948 3673 2524, Liberation Sans
/// Regular -1114 -621 2666 2007, Liberation Mono Regular -987 -615 1521 2009, Source Sans 3
/// ExtraLight (1000 units, CFF) -590 -288 2160 934.
/// </summary>
public sealed class FontBBoxTests
{
    private static readonly PdfIndirectReference FileRef = new(7, 0);

    [Theory]
    [InlineData("DejaVuSans.ttf", new double[] { -1021, -463, 1794, 1233 })]
    [InlineData("LiberationSans-Regular.ttf", new double[] { -544, -304, 1302, 980 })]
    [InlineData("LiberationMono-Regular.ttf", new double[] { -482, -301, 743, 981 })]
    [InlineData("SourceSans3-ExtraLight.otf", new double[] { -590, -288, 2160, 934 })]
    public void BuildFontDescriptor_writesTheHeadBoxScaledAndRoundedOutward(string font, double[] expected)
    {
        var embedder = new TrueTypeFontEmbedder(EmbeddedFontFixtures.Load(font), "F1");
        embedder.GetGlyphId('H');

        var descriptor = embedder.BuildFontDescriptor(FileRef);

        Assert.Equal(expected, EmbeddedFontFixtures.FontBBox(descriptor));
    }

    /// <summary> DejaVu's xMax is 3673 * 1000 / 2048 = 1793.46, which rounding to nearest takes to
    /// 1793, inside the glyph; the written 1794 encloses it. The other three edges are checked the
    /// same way.
    /// </summary>
    [Fact]
    public void DejaVu_boxEnclosesTheHeadBox_onEveryEdge()
    {
        var box = EmbeddedFontFixtures.FontBBox(
            new TrueTypeFontEmbedder(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), "F1").BuildFontDescriptor(FileRef));

        Assert.True(box[0] <= -2090 * 1000.0 / 2048);
        Assert.True(box[1] <= -948 * 1000.0 / 2048);
        Assert.True(box[2] >= 3673 * 1000.0 / 2048);
        Assert.True(box[3] >= 2524 * 1000.0 / 2048);
    }

    /// <summary>The saved file, read back: the descriptor holds the same numbers, written as
    /// integer tokens.</summary>
    [Fact]
    public void KernelDocument_dejaVu_descriptorCarriesTheHeadBox()
    {
        var pdf = EmbeddedFontFixtures.SaveKernel(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), "Hello");

        var (descriptor, _) = Assert.Single(EmbeddedFontFixtures.Descriptors(pdf));

        Assert.Equal([-1021, -463, 1794, 1233], EmbeddedFontFixtures.FontBBox(descriptor));
        Assert.Contains("/FontBBox [-1021 -463 1794 1233]", Encoding.Latin1.GetString(pdf));
    }

    [Fact]
    public void LayoutDocument_dejaVu_descriptorCarriesTheHeadBox()
    {
        using var doc = new VellumPdf.Layout.Document();
        var font = doc.UseTrueTypeFont(EmbeddedFontFixtures.Load("DejaVuSans.ttf"));
        doc.Add(new VellumPdf.Layout.Elements.Paragraph(
            "Hello", new VellumPdf.Layout.Core.TextStyle { FontRef = font, FontSize = 12 }));
        using var ms = new MemoryStream();
        doc.Save(ms);

        var (descriptor, _) = Assert.Single(EmbeddedFontFixtures.Descriptors(ms.ToArray()));

        Assert.Equal([-1021, -463, 1794, 1233], EmbeddedFontFixtures.FontBBox(descriptor));
    }

    /// <summary>The CFF path writes the same descriptor, in font units that are already 1000 per
    /// em.</summary>
    [Fact]
    public void KernelDocument_sourceSans_descriptorCarriesTheUnscaledHeadBox()
    {
        var pdf = EmbeddedFontFixtures.SaveKernel(EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf"), "Hello");

        var (descriptor, program) = Assert.Single(EmbeddedFontFixtures.Descriptors(pdf));

        Assert.Equal([-590, -288, 2160, 934], EmbeddedFontFixtures.FontBBox(descriptor));
        Assert.NotNull(descriptor.Get(new PdfName("FontFile3")));
        Assert.Equal("OTTO", Encoding.ASCII.GetString(program, 0, 4));
    }

    /// <summary>A subset keeps the whole font's box, so one glyph and 500 glyphs give the same
    /// numbers.</summary>
    [Fact]
    public void Subset_keepsTheWholeFontsBox()
    {
        var one = new TrueTypeFontEmbedder(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), "F1");
        one.GetGlyphId('i');
        var many = new TrueTypeFontEmbedder(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), "F1");
        var registered = 0;
        for (var cp = 0x20; registered < 500; cp++)
            if (many.GetGlyphId(cp) != 0) registered++;

        var expected = new double[] { -1021, -463, 1794, 1233 };
        Assert.Equal(expected, EmbeddedFontFixtures.FontBBox(one.BuildFontDescriptor(FileRef)));
        Assert.Equal(expected, EmbeddedFontFixtures.FontBBox(many.BuildFontDescriptor(FileRef)));
    }

    /// <summary>
    /// A font whose unitsPerEm is 0 (format 4 present, so the format 12 check at load does not
    /// apply) loads and refuses at the descriptor, and so at save. The refusal comes from /Ascent,
    /// before the box, so no value of the box can have been cast to an integer on the way.
    /// </summary>
    [Fact]
    public void UnitsPerEmZero_isRefusedAtTheDescriptor_andAtSave()
    {
        var font = EmbeddedFontFixtures.WithHead(
            EmbeddedFontFixtures.Load("DejaVuSans.ttf"), head => EmbeddedFontFixtures.PutI16(head, 18, 0));
        var embedder = new TrueTypeFontEmbedder(font, "F1");

        var ex = Assert.Throws<ArgumentException>(() => embedder.BuildFontDescriptor(FileRef));
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value.", ex.Message);

        var save = Assert.Throws<ArgumentException>(() => EmbeddedFontFixtures.SaveKernel(font, "Hello"));
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value.", save.Message);
    }

    /// <summary>
    /// A <c>head</c> box with its minima above its maxima is written as it reads (ISO 32000-2 7.9.5
    /// takes any two opposite corners). xMax is -1, whose scaled value -0.488 has a ceiling of
    /// negative zero; the file must hold <c>0</c>.
    /// </summary>
    [Fact]
    public void InvertedHeadBox_isWrittenAsRead_andNegativeZeroIsWrittenAsZero()
    {
        var font = EmbeddedFontFixtures.WithHead(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), head =>
        {
            EmbeddedFontFixtures.PutI16(head, 36, 1000);
            EmbeddedFontFixtures.PutI16(head, 38, 500);
            EmbeddedFontFixtures.PutI16(head, 40, -1);
            EmbeddedFontFixtures.PutI16(head, 42, -1000);
        });

        var pdf = EmbeddedFontFixtures.SaveKernel(font, "Hello");

        Assert.Contains("/FontBBox [488 244 0 -488]", Encoding.Latin1.GetString(pdf));
    }
}
