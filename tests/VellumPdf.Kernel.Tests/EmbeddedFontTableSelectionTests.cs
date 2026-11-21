// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Which source tables the embedded font program keeps (#612). A font that has an <c>fvar</c> table
/// is variable; its embedded program omits avar, cvar, fvar, gvar, HVAR, MVAR, VVAR and STAT, so it
/// holds the default instance alone. The program is read back from the saved file.
///
/// No variable font is added to the repository. The variable fixtures are DejaVu Sans and Source
/// Sans 3 ExtraLight with extra tables appended to the directory in memory (<see
/// cref="EmbeddedFontFixtures.WithTables"/>); the library does not parse those tables, so their
/// contents are arbitrary bytes of the length a small real table would have (fvar 64, gvar 96).
/// </summary>
public sealed class EmbeddedFontTableSelectionTests
{
    private static readonly string[] VariationTags = ["HVAR", "MVAR", "STAT", "VVAR", "avar", "cvar", "fvar", "gvar"];

    // DejaVu Sans as shipped, read with fontTools 4.62.1; the sort is by tag bytes, as the
    // directory is.
    private static readonly string[] DejaVuSourceTags =
    [
        "FFTM", "GDEF", "GPOS", "GSUB", "MATH", "OS/2", "cmap", "cvt ", "fpgm", "gasp", "glyf", "head", "hhea",
        "hmtx", "kern", "loca", "maxp", "name", "post", "prep",
    ];

    // What the embedded glyf program holds: the source's tables without cmap (#632).
    private static readonly string[] DejaVuTags = [.. DejaVuSourceTags.Where(t => t != "cmap")];

    private static readonly string[] SourceSansTags =
        ["BASE", "CFF ", "DSIG", "GDEF", "GPOS", "GSUB", "OS/2", "cmap", "head", "hhea", "hmtx", "maxp", "name", "post"];

    private static Dictionary<string, byte[]> Variation(params string[] tags) =>
        tags.ToDictionary(t => t, t => Enumerable.Range(0, t == "fvar" ? 64 : t == "gvar" ? 96 : 32)
            .Select(i => (byte)(i * 7 + t[0])).ToArray());

    private static byte[] Program(byte[] font)
    {
        var (_, program) = Assert.Single(EmbeddedFontFixtures.Descriptors(EmbeddedFontFixtures.SaveKernel(font, "Hello")));
        return program;
    }

    /// <summary> The fixture is built the way a variable font is, so the test sees a program that
    /// would have kept the tables: the source font has all eight, the embedded program has none,
    /// and what is left is exactly what the same font without them gives.
    /// </summary>
    [Fact]
    public void DejaVuWithAllEightVariationTables_embedsAsPlainDejaVu()
    {
        var plain = EmbeddedFontFixtures.Load("DejaVuSans.ttf");
        var variable = EmbeddedFontFixtures.WithTables(plain, Variation(VariationTags));
        Assert.Equal(DejaVuSourceTags.Concat(VariationTags).Order(StringComparer.Ordinal), EmbeddedFontFixtures.Tags(variable));

        var embeddedVariable = Program(variable);
        var embeddedPlain = Program(plain);

        Assert.Equal(DejaVuTags, EmbeddedFontFixtures.Tags(embeddedVariable));
        Assert.Equal(embeddedPlain, embeddedVariable);
    }

    /// <summary>
    /// A 64-byte fvar and a 96-byte gvar added 192 bytes to the program (two 16-byte directory
    /// entries and the 160 bytes of table data). Now the program is byte-equal to the plain font's,
    /// and its digest is pinned.
    /// </summary>
    [Fact]
    public void DejaVuWithFvarAndGvar_embedsAsPlainDejaVu_whoseDigestIsPinned()
    {
        var plain = EmbeddedFontFixtures.Load("DejaVuSans.ttf");
        var variable = EmbeddedFontFixtures.WithTables(plain, Variation("fvar", "gvar"));

        var embeddedVariable = Program(variable);
        var embeddedPlain = Program(plain);

        Assert.Equal(embeddedPlain, embeddedVariable);
        Assert.Equal(195_736, embeddedPlain.Length);
        Assert.Equal(
            "4392B930C6B87B90C9422D602441266F12571ACEC446F48A70A4B0F7A87C61E6",
            Convert.ToHexString(SHA256.HashData(embeddedPlain)));
    }

    /// <summary>Every table the program keeps is the source's own bytes, except head, which carries
    /// the new loca format and checksum adjustment, and glyf and loca, which are the
    /// subset's.</summary>
    [Fact]
    public void KeptTables_areTheSourcesBytes()
    {
        var plain = EmbeddedFontFixtures.Load("DejaVuSans.ttf");
        var embedded = Program(EmbeddedFontFixtures.WithTables(plain, Variation(VariationTags)));

        foreach (var tag in DejaVuTags.Except(["glyf", "loca", "head"]))
            Assert.True(
                EmbeddedFontFixtures.Table(plain, tag).AsSpan().SequenceEqual(EmbeddedFontFixtures.Table(embedded, tag)),
                $"table {tag} differs from the source");
    }

    /// <summary>The drop is keyed on <c>fvar</c>: a static font that carries STAT, or a stray gvar
    /// or HVAR, keeps what it has.</summary>
    [Theory]
    [InlineData("STAT")]
    [InlineData("gvar")]
    [InlineData("HVAR")]
    [InlineData("avar")]
    public void DejaVuWithoutFvar_keepsAVariationTableItCarries(string tag)
    {
        var plain = EmbeddedFontFixtures.Load("DejaVuSans.ttf");
        var withTag = EmbeddedFontFixtures.WithTables(plain, Variation(tag));

        var embedded = Program(withTag);

        Assert.Equal(DejaVuTags.Append(tag).Order(StringComparer.Ordinal), EmbeddedFontFixtures.Tags(embedded));
        Assert.Equal(Variation(tag)[tag], EmbeddedFontFixtures.Table(embedded, tag));
    }

    /// <summary>The CFF path: Source Sans 3 with fvar and HVAR embeds as Source Sans 3 without
    /// them.</summary>
    [Fact]
    public void SourceSansWithFvarAndHvar_embedsAsPlainSourceSans()
    {
        var plain = EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");
        var variable = EmbeddedFontFixtures.WithTables(plain, Variation("fvar", "HVAR"));

        var embeddedVariable = Program(variable);
        var embeddedPlain = Program(plain);

        Assert.Equal(SourceSansTags, EmbeddedFontFixtures.Tags(embeddedVariable));
        Assert.Equal(embeddedPlain, embeddedVariable);
    }

    [Fact]
    public void SourceSansWithAllEightVariationTables_embedsWithNoneOfThem()
    {
        var plain = EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");

        var embedded = Program(EmbeddedFontFixtures.WithTables(plain, Variation(VariationTags)));

        Assert.Equal(SourceSansTags, EmbeddedFontFixtures.Tags(embedded));
    }

    /// <summary>A variable font embeds the same bytes whichever other text it draws, so the drop
    /// does not depend on the glyphs used: the 1-glyph and the many-glyph subset both lose the
    /// tables.</summary>
    [Fact]
    public void VariableDejaVu_anySubset_losesTheTables()
    {
        var variable = EmbeddedFontFixtures.WithTables(EmbeddedFontFixtures.Load("DejaVuSans.ttf"), Variation(VariationTags));

        var one = Assert.Single(EmbeddedFontFixtures.Descriptors(EmbeddedFontFixtures.SaveKernel(variable, "i"))).Program;
        var many = Assert.Single(EmbeddedFontFixtures.Descriptors(
            EmbeddedFontFixtures.SaveKernel(variable, "The quick brown fox jumps over the lazy dog 0123456789"))).Program;

        Assert.Equal(DejaVuTags, EmbeddedFontFixtures.Tags(one));
        Assert.Equal(DejaVuTags, EmbeddedFontFixtures.Tags(many));
    }

    /// <summary>The descriptor's metrics come from the kept default-instance tables, so a variable
    /// font's descriptor numbers are the plain font's.</summary>
    [Fact]
    public void VariableDejaVu_descriptorNumbers_equalThePlainFonts()
    {
        var plain = EmbeddedFontFixtures.Load("DejaVuSans.ttf");
        var variable = EmbeddedFontFixtures.WithTables(plain, Variation("fvar", "gvar"));

        var plainDescriptor = Assert.Single(EmbeddedFontFixtures.Descriptors(EmbeddedFontFixtures.SaveKernel(plain, "Hello"))).Descriptor;
        var variableDescriptor = Assert.Single(EmbeddedFontFixtures.Descriptors(EmbeddedFontFixtures.SaveKernel(variable, "Hello"))).Descriptor;

        foreach (var key in new[] { "Ascent", "Descent", "CapHeight", "ItalicAngle", "Flags" })
            Assert.Equal(EmbeddedFontFixtures.Number(plainDescriptor, key), EmbeddedFontFixtures.Number(variableDescriptor, key));
        Assert.Equal(EmbeddedFontFixtures.FontBBox(plainDescriptor), EmbeddedFontFixtures.FontBBox(variableDescriptor));
    }
}
