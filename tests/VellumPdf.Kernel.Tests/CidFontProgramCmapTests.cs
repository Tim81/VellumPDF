// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary> An embedded glyf program is used with a CIDFontType2 descendant, so it carries no
/// <c>cmap</c> table (#632): ISO 32000-2, 9.9.1 says that with a CIDFont dictionary the table "is
/// not needed and shall not be present". The OpenType CFF program keeps it, because Table 124
/// requires it for a <c>/FontFile3</c> OpenType program. The tag lists are written as literals,
/// read from each source font with fontTools 4.62.1; the programs are read back from the saved
/// file.
/// </summary>
public sealed class CidFontProgramCmapTests
{
    private static readonly string[] DejaVuProgramTags =
    [
        "FFTM", "GDEF", "GPOS", "GSUB", "MATH", "OS/2", "cvt ", "fpgm", "gasp", "glyf", "head", "hhea", "hmtx",
        "kern", "loca", "maxp", "name", "post", "prep",
    ];

    private static readonly string[] LiberationSansProgramTags =
    [
        "FFTM", "GDEF", "GPOS", "GSUB", "OS/2", "cvt ", "fpgm", "gasp", "glyf", "head", "hhea", "hmtx", "kern",
        "loca", "maxp", "name", "post", "prep",
    ];

    private static readonly string[] LiberationMonoProgramTags =
    [
        "FFTM", "GDEF", "GPOS", "GSUB", "OS/2", "cvt ", "fpgm", "gasp", "glyf", "head", "hhea", "hmtx", "loca",
        "maxp", "name", "post", "prep",
    ];

    private static readonly string[] SourceSansProgramTags =
        ["BASE", "CFF ", "DSIG", "GDEF", "GPOS", "GSUB", "OS/2", "cmap", "head", "hhea", "hmtx", "maxp", "name", "post"];

    private static byte[] Program(string font)
    {
        var (_, program) = Assert.Single(EmbeddedFontFixtures.Descriptors(
            EmbeddedFontFixtures.SaveKernel(EmbeddedFontFixtures.Load(font), "Hello")));
        return program;
    }

    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("LiberationSans-Regular.ttf")]
    [InlineData("LiberationMono-Regular.ttf")]
    public void GlyfProgram_hasNoCmapTable(string font)
    {
        var expected = font switch
        {
            "DejaVuSans.ttf" => DejaVuProgramTags,
            "LiberationSans-Regular.ttf" => LiberationSansProgramTags,
            _ => LiberationMonoProgramTags,
        };
        // The source has the table, so its absence below is the change and not a font without one.
        Assert.Contains("cmap", EmbeddedFontFixtures.Tags(EmbeddedFontFixtures.Load(font)));

        Assert.Equal(expected, EmbeddedFontFixtures.Tags(Program(font)));
    }

    /// <summary>The descriptor of a glyf font names /FontFile2, and its descendant is a
    /// CIDFontType2, which is why the program needs no cmap.</summary>
    [Fact]
    public void GlyfFont_isWrittenAsAType0FontWithACidFontType2Descendant()
    {
        var pdf = Encoding.Latin1.GetString(EmbeddedFontFixtures.SaveKernel(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), "Hello"));

        Assert.Contains("/Subtype /CIDFontType2", pdf);
        Assert.Contains("/CIDToGIDMap /Identity", pdf);
        Assert.Contains("/FontFile2 ", pdf);
        Assert.DoesNotContain("/Subtype /TrueType", pdf);
    }

    /// <summary>The CFF program keeps its cmap, byte for byte.</summary>
    [Fact]
    public void CffProgram_keepsItsCmapTable()
    {
        var source = EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");

        var program = Program("SourceSans3-ExtraLight.otf");

        Assert.Equal(SourceSansProgramTags, EmbeddedFontFixtures.Tags(program));
        Assert.Equal(EmbeddedFontFixtures.Table(source, "cmap"), EmbeddedFontFixtures.Table(program, "cmap"));
    }

    /// <summary>Every table the glyf program keeps is the source's own bytes except head (loca
    /// format and checksum adjustment), glyf and loca (the subset's), so nothing but cmap was
    /// removed.</summary>
    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("LiberationMono-Regular.ttf")]
    public void GlyfProgram_keptTablesAreTheSourcesBytes(string font)
    {
        var source = EmbeddedFontFixtures.Load(font);
        var program = Program(font);

        foreach (var tag in EmbeddedFontFixtures.Tags(program).Except(["glyf", "loca", "head"]))
            Assert.True(
                EmbeddedFontFixtures.Table(source, tag).AsSpan().SequenceEqual(EmbeddedFontFixtures.Table(program, tag)),
                $"table {tag} differs from the source");
    }

    /// <summary> The directory the program is written with is consistent for the new table count:
    /// the offset table's numTables, searchRange, entrySelector and rangeShift for 19 tables
    /// (DejaVu), each table's checksum, and the whole-file checksum that head.checkSumAdjustment
    /// makes 0xB1B0AFBA.
    /// </summary>
    [Fact]
    public void GlyfProgram_directoryAndChecksumsAreConsistent()
    {
        var program = Program("DejaVuSans.ttf");

        ushort U16(int o) => (ushort)((program[o] << 8) | program[o + 1]);
        uint U32(int o) => ((uint)U16(o) << 16) | U16(o + 2);
        Assert.Equal(0x00010000u, U32(0));
        Assert.Equal(19, U16(4));
        Assert.Equal(16 * 16, U16(6));            // searchRange: largest power of two <= 19 is 16, times 16
        Assert.Equal(4, U16(8));                  // entrySelector: log2(16)
        Assert.Equal(19 * 16 - 16 * 16, U16(10)); // rangeShift
        for (var i = 0; i < 19; i++)
        {
            var entry = 12 + i * 16;
            var offset = (int)U32(entry + 8);
            var length = (int)U32(entry + 12);
            var data = program[offset..(offset + length)];
            var tag = Encoding.ASCII.GetString(program, entry, 4);
            // The head table's own checksum is taken with checkSumAdjustment zeroed.
            if (tag == "head") { data = (byte[])data.Clone(); data[8] = data[9] = data[10] = data[11] = 0; }
            Assert.Equal(TrueTypeFontEmbedder.Checksum(data), U32(entry + 4));
        }
        Assert.Equal(0xB1B0AFBAu, TrueTypeFontEmbedder.Checksum(program));
    }

    /// <summary>A source font with no cmap table is refused at load, as it was.</summary>
    [Fact]
    public void FontWithoutACmapTable_isStillRefusedAtLoad()
    {
        var font = EmbeddedFontFixtures.WithTables(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), remove: ["cmap"]);

        var ex = Assert.Throws<InvalidOperationException>(() => new TrueTypeFontEmbedder(font, "F1"));

        Assert.Equal("Font is missing required table 'cmap'.", ex.Message);
    }

    /// <summary>The ToUnicode map and the glyph ids in the content stream are what they were: the
    /// text still extracts, because it never went through the program's cmap.</summary>
    [Fact]
    public void ToUnicodeMap_stillMapsEveryGlyphUsed()
    {
        var pdf = EmbeddedFontFixtures.SaveKernel(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), "Hello");

        var all = Encoding.Latin1.GetString(EmbeddedFontFixtures.StreamBodies(pdf));

        Assert.Contains("<002B> <0048>", all);   // H is glyph 43
        Assert.Contains("<0048> <0065>", all);   // e is glyph 72
        Assert.Contains("<004F> <006C>", all);   // l is glyph 79
        Assert.Contains("<0052> <006F>", all);   // o is glyph 82
        Assert.Contains("<002B0048004F004F0052>", all);   // the content stream's glyph run
    }
}
