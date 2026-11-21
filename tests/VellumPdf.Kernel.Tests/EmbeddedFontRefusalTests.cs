// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// What building the embedded font program refuses, and the variable-font cases the
/// <c>UseTrueTypeFont</c> and <c>BuildFontFileStream</c> documentation names (#612). The fixtures
/// are in-tree fonts with the directory edited in memory.
/// </summary>
public sealed class EmbeddedFontRefusalTests
{
    /// <summary>
    /// A variable font that stores its outlines in a <c>CFF2</c> table has no <c>CFF </c> table. This
    /// library reads <c>CFF </c> only, so the font loads and the program cannot be built: the save
    /// throws <see cref="InvalidOperationException"/> naming the missing table, as it does for any
    /// OpenType CFF font that lacks it. The font is Source Sans 3 with its <c>CFF </c> table renamed
    /// <c>CFF2</c> and an <c>fvar</c> table added.
    /// </summary>
    [Fact]
    public void CffTwoFontWithFvar_loads_andIsRefusedAtSave_withInvalidOperationException()
    {
        var source = EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");
        var cff = EmbeddedFontFixtures.Table(source, "CFF ");
        var font = EmbeddedFontFixtures.WithTables(
            source,
            new Dictionary<string, byte[]> { ["CFF2"] = cff, ["fvar"] = new byte[64] },
            remove: ["CFF "]);
        Assert.DoesNotContain("CFF ", EmbeddedFontFixtures.Tags(font));      // fixture check
        Assert.Contains("CFF2", EmbeddedFontFixtures.Tags(font));

        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(font);                              // the load accepts it
        doc.RegisterEmbeddedFontUsage(page, handle);
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
        Assert.Equal("Font is missing required table 'CFF '.", ex.Message);
    }

    [Fact]
    public void BuildFontFileStream_cffTwoFont_throwsInvalidOperationException()
    {
        var source = EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");
        var font = EmbeddedFontFixtures.WithTables(
            source,
            new Dictionary<string, byte[]> { ["CFF2"] = EmbeddedFontFixtures.Table(source, "CFF "), ["fvar"] = new byte[64] },
            remove: ["CFF "]);
        var embedder = new TrueTypeFontEmbedder(font, "F1");
        embedder.GetGlyphId('H');

        var ex = Assert.Throws<InvalidOperationException>(() => embedder.BuildFontFileStream());

        Assert.Equal("Font is missing required table 'CFF '.", ex.Message);
    }

    /// <summary>A glyf font with no <c>glyf</c> table, and one with no <c>loca</c>, are refused the same way.</summary>
    [Theory]
    [InlineData("glyf")]
    [InlineData("loca")]
    public void BuildFontFileStream_missingOutlineTable_throwsInvalidOperationException(string tag)
    {
        var font = EmbeddedFontFixtures.WithTables(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), remove: [tag]);
        var embedder = new TrueTypeFontEmbedder(font, "F1");
        embedder.GetGlyphId('H');

        var ex = Assert.Throws<InvalidOperationException>(() => embedder.BuildFontFileStream());

        Assert.Equal($"Font is missing required table '{tag}'.", ex.Message);
    }

    /// <summary>
    /// An OpenType CFF font whose <c>CFF </c> table does not parse is embedded whole, so a variable
    /// one keeps its <c>fvar</c> and <c>HVAR</c> tables. The program is the source font's own bytes.
    /// </summary>
    [Fact]
    public void UnparseableCffFont_isEmbeddedWhole_andKeepsItsVariationTables()
    {
        var source = EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");
        var font = EmbeddedFontFixtures.WithTables(
            source,
            new Dictionary<string, byte[]>
            {
                ["CFF "] = new byte[8],
                ["fvar"] = new byte[64],
                ["HVAR"] = new byte[32],
            });
        var (_, program) = Assert.Single(EmbeddedFontFixtures.Descriptors(EmbeddedFontFixtures.SaveKernel(font, "Hello")));

        Assert.Equal(font, program);
        Assert.Contains("fvar", EmbeddedFontFixtures.Tags(program));
        Assert.Contains("HVAR", EmbeddedFontFixtures.Tags(program));
    }

    /// <summary>Malformed glyf data (a <c>glyf</c> table cut short, so <c>loca</c> points past its end).</summary>
    [Fact]
    public void BuildFontFileStream_glyfTableCutShort_throwsInvalidDataException()
    {
        var source = EmbeddedFontFixtures.Load("DejaVuSans.ttf");
        var font = EmbeddedFontFixtures.WithTables(
            source, new Dictionary<string, byte[]> { ["glyf"] = EmbeddedFontFixtures.Table(source, "glyf")[..100] });
        var embedder = new TrueTypeFontEmbedder(font, "F1");
        embedder.GetGlyphId('H');

        var ex = Record.Exception(() => embedder.BuildFontFileStream());

        Assert.IsType<InvalidDataException>(ex);
    }
}
