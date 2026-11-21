// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Fonts.Sfnt;
using VellumPdf.TestSupport;

namespace VellumPdf.Conformance.Tests.Fonts;

// The enclosing VellumPdf.Conformance namespace has its own PdfConformance; this file means the
// document one.
using PdfConformance = VellumPdf.Document.PdfConformance;

/// <summary>
/// cmap format 12 against the two fonts in the tree that carry a format 12 subtable, a TrueType
/// one (DejaVu Sans, glyf outlines) and an OpenType-CFF one (Source Sans 3 ExtraLight) (#595). The
/// other 12 fonts in the tree, the Liberation set, have formats 4 and 6 only (checked with
/// fontTools 4.62.1 over all 14 files).
///
/// Neither font is changed on disk. The "format 12 only" and "format 4 only" fonts are built in
/// memory by appending a new cmap table to the asset's bytes and pointing the table directory at
/// it, so the glyph data is the asset's own and no font file is added to the repository.
///
/// The expected glyph ids are known answers read from the fonts with fontTools 4.62.1 (the cmap
/// subtables, then the glyph order), not computed by the code under test: DejaVu maps U+1F600 to
/// glyph 5857, U+10300 to 5373 and U+1F643 to 5920, has 548 code points above U+FFFF and 5370
/// below it, and Source Sans maps U+1F10D to 1481 and U+1F916 to 2251 with 14 above U+FFFF and
/// 1600 below. The OpenType cmap chapter (OpenType 1.9.1, format 12 and the platform and encoding
/// tables) is not held under docs/iso, so the layout rules these tests pin come from that page on
/// learn.microsoft.com, fetched 2026-11-19.
/// </summary>
[Collection("veraPDF")]
public sealed class CmapFormat12Tests
{
    private const int DejaVuBmpCount = 5370;
    private const int DejaVuSupplementaryCount = 548;

    // ---------------------------------------------------------------- the fonts as they are

    [Fact]
    public void DejaVu_withBothSubtables_mapsTheSupplementaryPlanes()
    {
        var table = Parse(LoadAsset("DejaVuSans.ttf"));

        Assert.Equal(36, table.GetGlyphId('A'));
        Assert.Equal(5857, table.GetGlyphId(0x1F600));
        Assert.Equal(5373, table.GetGlyphId(0x10300));
        Assert.Equal(5920, table.GetGlyphId(0x1F643));
        Assert.Equal((DejaVuBmpCount, DejaVuSupplementaryCount), Count(table));
    }

    [Fact]
    public void SourceSans_withBothSubtables_mapsTheSupplementaryPlanes()
    {
        var table = Parse(LoadAsset("SourceSans3-ExtraLight.otf"));

        Assert.Equal(2, table.GetGlyphId('A'));
        Assert.Equal(1481, table.GetGlyphId(0x1F10D));
        Assert.Equal(2251, table.GetGlyphId(0x1F916));
        Assert.Equal(0, table.GetGlyphId(0x1F600));   // the font has no U+1F600
        Assert.Equal((1600, 14), Count(table));
    }

    // ---------------------------------------------------------------- one subtable of each kind

    /// <summary>
    /// The font whose only Unicode subtable is the platform 3 / encoding 10 format 12 one loads and
    /// maps every code point the full font does. 2.3.3 threw NotSupportedException here.
    /// </summary>
    [Theory]
    [InlineData("DejaVuSans.ttf", 3, 10)]
    [InlineData("SourceSans3-ExtraLight.otf", 3, 10)]
    [InlineData("SourceSans3-ExtraLight.otf", 0, 4)]
    public void FontWithOnlyAFormat12Subtable_loadsAndMapsEveryCodePointTheFullFontDoes(
        string asset, int platform, int encoding)
    {
        var full = LoadAsset(asset);
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == platform && s.Encoding == encoding && s.Format == 12));

        var reference = Parse(full);
        var table = Parse(only12);

        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            var expected = reference.TryGetGlyphId(cp, out var g) ? g : (ushort)0;
            var actual = table.TryGetGlyphId(cp, out var h) ? h : (ushort)0;
            if (expected != actual)
                Assert.Fail($"U+{cp:X}: full font maps to {expected}, format-12-only font to {actual}.");
        }
    }

    /// <summary>The same two format 12 fonts, one KAT each, so a failure above names a
    /// value.</summary>
    [Fact]
    public void Format12OnlyDejaVu_mapsU1F600ToGlyph5857()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var table = Parse(WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12)));

        Assert.Equal(5857, table.GetGlyphId(0x1F600));
        Assert.Equal((DejaVuBmpCount, DejaVuSupplementaryCount), Count(table));
    }

    /// <summary>
    /// A font with no format 12 subtable behaves as it did: format 4 for the BMP, nothing above it.
    /// </summary>
    [Fact]
    public void FontWithOnlyFormat4_isUnchanged()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var only4 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Encoding == 1));

        var table = Parse(only4);

        Assert.Equal(36, table.GetGlyphId('A'));
        Assert.Equal(0, table.GetGlyphId(0x1F600));
        Assert.Equal((DejaVuBmpCount, 0), Count(table));
    }

    // ---------------------------------------------------------------- the whole path, with oracles

    /// <summary>
    /// U+1F600 drawn with DejaVu: the content stream names glyph 5857 (0x16E1) and the ToUnicode
    /// map has <c>&lt;16E1&gt; &lt;D83DDE00&gt;</c>, the surrogate pair, for it. Run with the font
    /// whole and with its cmap cut to the format 12 subtable alone; the two files must carry the
    /// same pair, since the glyph data is the same.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EmbeddedDejaVu_drawsU1F600WithItsRealGlyph_andToUnicodeHasTheSurrogatePair(bool format12Only)
    {
        var font = LoadAsset("DejaVuSans.ttf");
        if (format12Only)
            font = WithCmap(font, Subtables(font).Where(s => s.Platform == 3 && s.Format == 12));

        var pdf = DrawText(font, "A\U0001F600", PdfConformance.None);

        var all = Encoding.Latin1.GetString(Inflated(pdf));
        Assert.Contains("<16E1> <D83DDE00>", all);
        Assert.Contains("<0024> <0041>", all);        // 'A' is glyph 36
        Assert.Contains("<002416E1>", all);           // the content stream's glyph run
    }

    /// <summary>
    /// The same file read back by independent tools: pdftotext extracts the emoji from the page,
    /// and veraPDF accepts the PDF/A-2u build of it. pdftotext reads the text through the
    /// ToUnicode map, so it fails if that holds anything but the surrogate pair for the glyph.
    /// </summary>
    [Fact]
    public void EmbeddedDejaVu_u1F600_isExtractedByPdftotext()
    {
        var font = LoadAsset("DejaVuSans.ttf");
        font = WithCmap(font, Subtables(font).Where(s => s.Platform == 3 && s.Format == 12));
        var pdf = DrawText(font, "A\U0001F600B", PdfConformance.None);
        var path = Path.Combine(Path.GetTempPath(), $"vellum-cmap12-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            ExternalTool.TryRun("pdftotext", [path, "-"], out _, out var text, out var stderr, out _,
                outputEncoding: Encoding.UTF8);

            Assert.True(text.Contains("A\U0001F600B", StringComparison.Ordinal),
                $"pdftotext did not return the text.\nstdout: {text}\nstderr: {stderr}");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void EmbeddedDejaVu_u1F600_pdfA2uBuildIsAcceptedByVeraPdf()
    {
        VeraPdf.EnsureAvailable();
        var font = LoadAsset("DejaVuSans.ttf");
        font = WithCmap(font, Subtables(font).Where(s => s.Platform == 3 && s.Format == 12));
        var pdf = DrawText(font, "A\U0001F600B", PdfConformance.PdfA2u);
        var path = Path.Combine(Path.GetTempPath(), $"vellum-cmap12-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            Assert.True(VeraPdf.Validate(path, "2u"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>The CFF path: Source Sans draws U+1F916 with glyph 2251 (0x08CB) and a ToUnicode
    /// pair for it.</summary>
    [Fact]
    public void EmbeddedSourceSans_drawsU1F916WithItsRealGlyph()
    {
        var font = LoadAsset("SourceSans3-ExtraLight.otf");
        font = WithCmap(font, Subtables(font).Where(s => s.Platform == 3 && s.Format == 12));

        var pdf = DrawText(font, "A\U0001F916", PdfConformance.None);

        var all = Encoding.Latin1.GetString(Inflated(pdf));
        Assert.Contains("D83EDD16>", all);   // U+1F916 as a surrogate pair
    }

    // -------------------------------------------------------- where the two subtables disagree

    /// <summary>
    /// A font whose format 12 subtable maps a BMP code point differently from its format 4 one
    /// keeps the format 4 glyph, as 2.3.3 drew it, and still gets its supplementary planes from
    /// format 12. The disagreement is made in memory: the format 12 group holding 'A' is shifted by
    /// 1000 glyphs, and the format 12 subtable alone is checked to map 'A' to 1036 first, so the
    /// test cannot pass because nothing disagreed.
    /// </summary>
    [Fact]
    public void FontWhoseFormat12DisagreesOnABmpCodePoint_keepsTheFormat4Glyph()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var subtables = Subtables(full);
        var format4 = subtables.First(s => s.Platform == 3 && s.Encoding == 1);
        var altered = ShiftGroupContaining(subtables.First(s => s.Platform == 3 && s.Format == 12), 'A', 1000);
        Assert.Equal(1036, Parse(WithCmap(full, [altered])).GetGlyphId('A'));

        var table = Parse(WithCmap(full, [format4, altered]));

        Assert.Equal(36, table.GetGlyphId('A'));
        Assert.Equal(5857, table.GetGlyphId(0x1F600));
        var format4Only = Parse(WithCmap(full, [format4]));
        for (var cp = 0; cp <= 0xFFFF; cp++)
        {
            if (format4Only.TryGetGlyphId(cp, out var expected) && table.GetGlyphId(cp) != expected)
                Assert.Fail($"U+{cp:X}: format 4 maps {expected}, the combined table {table.GetGlyphId(cp)}.");
        }
    }

    /// <summary>The subtable with every group that holds <paramref name="codePoint"/> moved up by
    /// <paramref name="shift"/> glyphs.</summary>
    internal static Subtable ShiftGroupContaining(Subtable format12, int codePoint, int shift)
    {
        var bytes = (byte[])format12.Bytes.Clone();
        var groups = (int)U32(bytes, 12);
        var shifted = 0;
        for (var i = 0; i < groups; i++)
        {
            var at = 16 + i * 12;
            if (U32(bytes, at) <= codePoint && codePoint <= U32(bytes, at + 4))
            {
                Put32(bytes, at + 8, U32(bytes, at + 8) + (uint)shift);
                shifted++;
            }
        }
        Assert.Equal(1, shifted);
        return format12 with { Bytes = bytes };
    }

    // ---------------------------------------------------------------- BMP-only output is unchanged

    /// <summary>
    /// A document that draws only BMP text must come out as it did on 2.3.3, whichever subtables
    /// the font has. The digest is SHA-256 over the inflated stream bodies of a document with the
    /// clock and document id pinned, taken from the 2.3.3 build (informational version
    /// <c>2.3.3+cbbf7d5136f640d0a5b2a9bc8b5ded298bc7d1f2</c>, a detached worktree at <c>v2.3.3</c>)
    /// and found equal on this build. It is over inflated bodies, not the file, so a different
    /// zlib on another platform cannot move it. A change to which glyph a BMP code point maps to,
    /// or to what is embedded for it, changes the digest.
    ///
    /// <para>Only the Source Sans row is here. It is an OpenType CFF font, whose program keeps its
    /// <c>cmap</c> table, so its stream bodies are the ones 2.3.3 wrote. The two DejaVu rows moved
    /// when a glyf program stopped carrying <c>cmap</c>; see
    /// <see cref="BmpOnlyDocument_dejaVu_hasTheStreamBodiesOfTheCurrentBuild"/>.</para>
    ///
    /// <para>2.3.3 wrote the ToUnicode map with the platform line end, so on Windows the digest
    /// matched only after folding CR LF to LF in that one stream. The map ends its lines with LF
    /// on every platform now, so the digest is over the raw inflated bytes and is the one 2.3.3
    /// gave on Linux.</para>
    /// </summary>
    [Theory]
    [InlineData("SourceSans3-ExtraLight.otf", "intact", "3F4D3BBA526BBC8396431AAB288AB9162C1C992D1AB5B911672F162CCE0E737B")]
    public void BmpOnlyDocument_hasTheStreamBodiesItHadOn233(string asset, string variant, string expectedSha256) =>
        AssertStreamBodyDigest(asset, variant, expectedSha256);

    /// <summary>
    /// The same document with DejaVu Sans, whole and with its <c>cmap</c> cut to the format 4
    /// subtable. The digests are no longer 2.3.3's: the embedded glyf program has no <c>cmap</c>
    /// table and the descriptor's <c>/FontBBox</c> is the font's own box (the descriptor is a
    /// dictionary, not a stream, so only the program moves the digest). They were taken from this
    /// build and are equal on Windows and on Linux. The two are equal to each other, since cutting
    /// the font's <c>cmap</c> to one subtable was the only difference between the two programs and
    /// the table is not embedded now. Beside them, <c>FontFile2</c> of the 2.3.3
    /// document and of this build differ by the <c>cmap</c> table and the <c>head</c> table's
    /// checksum adjustment, and by no other table.
    /// </summary>
    [Theory]
    [InlineData("DejaVuSans.ttf", "intact", "5D85E4D67046BE1A4AF0D2983057B2C16B7AAA525830249FBD1129DD2CB15220")]
    [InlineData("DejaVuSans.ttf", "format4Only", "5D85E4D67046BE1A4AF0D2983057B2C16B7AAA525830249FBD1129DD2CB15220")]
    public void BmpOnlyDocument_dejaVu_hasTheStreamBodiesOfTheCurrentBuild(string asset, string variant, string expectedSha256) =>
        AssertStreamBodyDigest(asset, variant, expectedSha256);

    private static void AssertStreamBodyDigest(string asset, string variant, string expectedSha256)
    {
        var font = LoadAsset(asset);
        if (variant == "format4Only")
            font = WithCmap(font, Subtables(font).Where(s => s.Platform == 3 && s.Encoding == 1));

        using var doc = new PdfDocument
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(font);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[5];
        var count = handle.GetGlyphIds("Hello", gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);

        var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(StreamBodies(ms.ToArray())));
        Assert.Equal(expectedSha256, digest);
    }

    // ----------------------------------------------------- a format-12-only font must be sound

    /// <summary>
    /// A font whose only Unicode subtable is format 12 was refused with NotSupportedException on
    /// 2.3.3, whatever else was wrong with it. It is accepted now only if it is sound, so damage
    /// elsewhere is still NotSupportedException (with the reason in the message), not the type the
    /// damaged read would raise. Each case damages one table of the format-12-only font and checks
    /// that the same damage on the full font (format 4 present) is not reported as
    /// NotSupportedException, so the test sees the damage and not a cmap refusal.
    /// </summary>
    [Theory]
    [InlineData("DejaVuSans.ttf", "hmtx", 2)]       // advance widths truncated
    [InlineData("DejaVuSans.ttf", "glyf", 100)]     // loca ranges lie outside glyf
    [InlineData("SourceSans3-ExtraLight.otf", "CFF ", 8)]   // CFF table does not parse
    public void FormatTwelveOnlyFont_damagedElsewhere_isRefusedAsNotSupported(string asset, string tag, int damagedLength)
    {
        var full = LoadAsset(asset);
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12));

        var ex = Assert.Throws<NotSupportedException>(
            () => new TrueTypeFontEmbedder(WithTableLength(only12, tag, damagedLength), "F1"));

        Assert.Contains("only Unicode cmap subtable is format 12 and the font is malformed elsewhere", ex.Message);
        Assert.NotNull(ex.InnerException);
        // The same damage on a font with a format 4 subtable never surfaces as
        // NotSupportedException.
        var control = Record.Exception(() => new TrueTypeFontEmbedder(WithTableLength(full, tag, damagedLength), "F1"));
        Assert.False(control is NotSupportedException, "the damage is not what is being refused");
    }

    /// <summary>
    /// head.unitsPerEm of 0 made a format-12-only font load and then fail at save with
    /// ArgumentException; a font that cannot be saved is not accepted, so load refuses it with the
    /// type 2.3.3 gave every such font. (On a font with format 4 the failure stays at save.)
    /// </summary>
    [Theory]
    [InlineData("DejaVuSans.ttf")]
    [InlineData("SourceSans3-ExtraLight.otf")]
    public void FormatTwelveOnlyFont_unitsPerEmZero_isRefusedAsNotSupported(string asset)
    {
        var full = LoadAsset(asset);
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12));
        var (_, head, _) = FindTable(only12, "head");
        var damaged = (byte[])only12.Clone();
        Put16(damaged, head + 18, 0);   // unitsPerEm

        var ex = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(damaged, "F1"));

        Assert.Contains("unitsPerEm is 0", ex.Message);
    }

    [Fact]
    public void FormatTwelveOnlyFont_missingTable_isRefusedAsNotSupported()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12));

        var ex = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(WithTagRenamed(only12, "post"), "F1"));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    /// <summary>A format 12 subtable that maps a glyph the font does not hold, alone in the font,
    /// is refused at load.</summary>
    [Fact]
    public void FormatTwelveOnlyFont_mappingAGlyphPastNumGlyphs_isRefusedAsNotSupported()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var format12 = Subtables(full).First(s => s.Format == 12 && s.Platform == 3);
        var shifted = ShiftGroupContaining(format12, 'A', 60000 - 36);

        var ex = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(WithCmap(full, [shifted]), "F1"));

        Assert.Contains("maps glyph", ex.Message);
        Assert.Contains("but the font has", ex.Message);
    }

    /// <summary>The same shifted subtable beside format 4 is ignored and the font loads, mapping
    /// 'A' through format 4.</summary>
    [Fact]
    public void FormatTwelveBesideFormat4_mappingAGlyphPastNumGlyphs_isIgnoredAndTheFontLoads()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var format4 = Subtables(full).First(s => s.Format == 4 && s.Platform == 3);
        var format12 = Subtables(full).First(s => s.Format == 12 && s.Platform == 3);
        var shifted = ShiftGroupContaining(format12, 'A', 60000 - 36);

        var embedder = new TrueTypeFontEmbedder(WithCmap(full, [format4, shifted]), "F1");

        Assert.Equal(36, embedder.GetGlyphId('A'));
        Assert.Equal(0, embedder.GetGlyphId(0x1F600));   // 2.3.3 read format 4 only: no supplementary plane
    }

    /// <summary>
    /// Platform 0 / encoding 10 is not an encoding the cmap chapter defines (its Unicode-platform
    /// IDs run 0 to 6), and 2.3.3 refused this font, so it stays refused when it is the only
    /// Unicode subtable. Beside the 16-bit subtable it is read, as DejaVu Sans carries it.
    /// </summary>
    [Fact]
    public void FontWithOnlyAPlatform0Encoding10Subtable_isRefusedButBesideFormat4ItLoads()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var only = WithCmap(full, Subtables(full).Where(s => s.Platform == 0 && s.Encoding == 10 && s.Format == 12));
        var beside = WithCmap(full, Subtables(full).Where(s => s.Format == 4 && s.Platform == 3 || s.Platform == 0 && s.Encoding == 10 && s.Format == 12));

        var ex = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(only, "F1"));
        Assert.Contains("platform 0 / encoding 10, which the OpenType cmap chapter does not define", ex.Message);
        Assert.Equal(5857, new TrueTypeFontEmbedder(beside, "F1").GetGlyphId(0x1F600));
    }

    /// <summary>
    /// The glyf chapter's composite record names a component glyph by index, so an index at or past
    /// numGlyphs names nothing. A component of U+00E9 is rewritten to numGlyphs (refused) and to
    /// numGlyphs - 1 (accepted), for the first component and for the second, which the walk reaches
    /// only by stepping over the first record's arguments and transform; the same rewrite on the
    /// full font (format 4 present) is not reported as NotSupportedException.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void FormatTwelveOnlyFont_compositeComponentPastNumGlyphs_isRefusedAsNotSupported(int componentIndex)
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12));
        var numGlyphs = U16(only12, FindTable(only12, "maxp").Offset + 4);
        var gid = new TrueTypeFontEmbedder(only12, "F1").GetGlyphId(0xE9);

        var atEnd = WithComponent(only12, gid, componentIndex, numGlyphs);
        var lastGlyph = WithComponent(only12, gid, componentIndex, numGlyphs - 1);

        var ex = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(atEnd, "F1"));
        Assert.Contains($"composite glyph {gid} names component glyph {numGlyphs}, but the font has {numGlyphs} glyph(s)", ex.Message);
        _ = new TrueTypeFontEmbedder(lastGlyph, "F1");
        var control = Record.Exception(() => new TrueTypeFontEmbedder(WithComponent(full, gid, componentIndex, numGlyphs), "F1"));
        Assert.Null(control);
    }

    /// <summary>
    /// A component record can carry word arguments and a scale, an x and y scale or a 2 by 2
    /// transform (glyf chapter: 2, 4 or 8 bytes after the arguments), and the walk must step over
    /// each to reach the next record. DejaVu Sans has no composite whose first record carries a
    /// transform, so the longest composite's data is rewritten as two records: the first with the
    /// given flags, naming glyph 1, and the second naming numGlyphs (refused) or numGlyphs - 1
    /// (accepted).
    /// </summary>
    [Theory]
    [InlineData(0x0000, 2)]    // byte arguments, no transform
    [InlineData(0x0001, 4)]    // ARG_1_AND_2_ARE_WORDS
    [InlineData(0x0008, 4)]    // WE_HAVE_A_SCALE: 2 bytes after 2 bytes of arguments
    [InlineData(0x0040, 6)]    // WE_HAVE_AN_X_AND_Y_SCALE: 4 bytes
    [InlineData(0x0080, 10)]   // WE_HAVE_A_TWO_BY_TWO: 8 bytes
    [InlineData(0x0009, 6)]    // words and a scale
    [InlineData(0x0081, 12)]   // words and a 2 by 2
    public void FormatTwelveOnlyFont_componentAfterATransformedRecord_isFound(int flags, int firstRecordBodyBytes)
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12));
        var numGlyphs = U16(only12, FindTable(only12, "maxp").Offset + 4);

        var atEnd = WithTwoRecords(only12, flags, firstRecordBodyBytes, numGlyphs);
        var lastGlyph = WithTwoRecords(only12, flags, firstRecordBodyBytes, numGlyphs - 1);

        var ex = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(atEnd, "F1"));
        Assert.Contains($"names component glyph {numGlyphs}, but the font has {numGlyphs} glyph(s)", ex.Message);
        _ = new TrueTypeFontEmbedder(lastGlyph, "F1");
    }

    /// <summary>
    /// The longest composite glyph rewritten as two component records: the first has
    /// <paramref name="flags"/> (plus MORE_COMPONENTS and ARGS_ARE_XY_VALUES) and
    /// <paramref name="firstRecordBodyBytes"/> bytes of arguments and transform after the glyph
    /// index, naming glyph 1; the second has byte arguments and names <paramref name="second"/>.
    /// </summary>
    private static byte[] WithTwoRecords(byte[] font, int flags, int firstRecordBodyBytes, int second)
    {
        var copy = (byte[])font.Clone();
        var head = FindTable(copy, "head").Offset;
        var loca = FindTable(copy, "loca").Offset;
        var glyf = FindTable(copy, "glyf").Offset;
        var numGlyphs = U16(copy, FindTable(copy, "maxp").Offset + 4);
        var shortOffsets = U16(copy, head + 50) == 0;
        int Offset(int g) => shortOffsets ? U16(copy, loca + 2 * g) * 2 : (int)U32(copy, loca + 4 * g);

        var best = -1;
        var bestLength = 0;
        for (var g = 0; g < numGlyphs; g++)
        {
            var length = Offset(g + 1) - Offset(g);
            if (length >= 10 && (short)U16(copy, glyf + Offset(g)) < 0 && length > bestLength)
                (best, bestLength) = (g, length);
        }

        var needed = 10 + 4 + firstRecordBodyBytes + 6;
        Assert.True(bestLength >= needed, $"the longest composite is {bestLength} bytes, {needed} are needed");

        var at = glyf + Offset(best) + 10;
        Put16(copy, at, flags | 0x0020 | 0x0002);   // MORE_COMPONENTS, ARGS_ARE_XY_VALUES
        Put16(copy, at + 2, 1);
        at += 4 + firstRecordBodyBytes;
        Put16(copy, at, 0x0002);
        Put16(copy, at + 2, second);
        return copy;
    }

    /// <summary>
    /// The glyf chapter: a composite glyph description "is a directed graph. This graph must be
    /// acyclic". A composite that names itself, and two composites that name each other, are
    /// refused for a format-12-only font with the rule in the message. A composite that names
    /// another composite which does not lead back (a nested composite) loads, and so does the same
    /// cycle beside a 16-bit subtable, which is not held to the chapter's other rules.
    /// </summary>
    [Fact]
    public void FormatTwelveOnlyFont_compositeCycle_isRefusedAsNotSupported()
    {
        var full = LoadAsset("DejaVuSans.ttf");
        var only12 = WithCmap(full, Subtables(full).Where(s => s.Platform == 3 && s.Format == 12));
        var probe = new TrueTypeFontEmbedder(only12, "F1");
        var a = probe.GetGlyphId(0xE9);
        var b = probe.GetGlyphId(0xE8);
        Assert.NotEqual(a, b);

        var self = WithComponent(only12, a, 0, a);
        var pair = WithComponent(WithComponent(only12, a, 0, b), b, 0, a);
        var nested = WithComponent(only12, a, 0, b);

        var selfEx = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(self, "F1"));
        Assert.Contains($"composite glyph {a} names component glyph {a}, which is already being expanded above it, so the composite glyphs form a cycle", selfEx.Message);
        var pairEx = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(pair, "F1"));
        Assert.Contains("so the composite glyphs form a cycle (OpenType glyf chapter: the graph of composite glyphs must be acyclic)", pairEx.Message);
        _ = new TrueTypeFontEmbedder(nested, "F1");
        // The cycle sits behind the second component, which the walk reaches only after the first.
        var viaSecond = WithComponent(only12, a, 1, a);
        var secondEx = Assert.Throws<NotSupportedException>(() => new TrueTypeFontEmbedder(viaSecond, "F1"));
        Assert.Contains($"composite glyph {a} names component glyph {a}, which is already being expanded above it", secondEx.Message);
        var control = Record.Exception(() => new TrueTypeFontEmbedder(WithComponent(full, a, 0, a), "F1"));
        Assert.Null(control);
    }

    /// <summary>
    /// The font with the glyphIndex of component <paramref name="componentIndex"/> of composite
    /// glyph <paramref name="gid"/> rewritten to <paramref name="component"/>. Written from the
    /// glyf chapter's record layout (flags, glyphIndex, two arguments of 2 or 4 bytes, an optional
    /// transform of 2, 4 or 8 bytes, repeated while MORE_COMPONENTS is set), independently of the
    /// walk in the embedder.
    /// </summary>
    private static byte[] WithComponent(byte[] font, int gid, int componentIndex, int component)
    {
        var copy = (byte[])font.Clone();
        var head = FindTable(copy, "head").Offset;
        var loca = FindTable(copy, "loca").Offset;
        var glyf = FindTable(copy, "glyf").Offset;
        var shortOffsets = U16(copy, head + 50) == 0;
        var start = shortOffsets ? U16(copy, loca + 2 * gid) * 2 : (int)U32(copy, loca + 4 * gid);
        var at = glyf + start;
        Assert.True((short)U16(copy, at) < 0, $"glyph {gid} is not composite");

        var record = at + 10;
        for (var i = 0; i < componentIndex; i++)
        {
            var flags = U16(copy, record);
            Assert.True((flags & 0x0020) != 0, $"glyph {gid} has no component {componentIndex}");
            record += 4 + ((flags & 0x0001) != 0 ? 4 : 2);
            record += (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
        }

        Put16(copy, record + 2, component);
        return copy;
    }

    // ---------------------------------------------------------------- fixtures

    private static CmapTable Parse(byte[] font) => CmapTable.Parse(SfntFont.Parse(font));

    /// <summary>(code points at or below U+FFFF, code points above) the table maps.</summary>
    private static (int Bmp, int Supplementary) Count(CmapTable table)
    {
        int bmp = 0, supplementary = 0;
        for (var cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (!table.TryGetGlyphId(cp, out _)) continue;
            if (cp <= 0xFFFF) bmp++; else supplementary++;
        }
        return (bmp, supplementary);
    }

    private static byte[] DrawText(byte[] font, string text, PdfConformance conformance)
    {
        using var doc = new PdfDocument { Conformance = conformance, Language = "en-US" };
        doc.Info.Title = "cmap format 12";
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(font);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[text.Length];
        var count = handle.GetGlyphIds(text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// The PDF with every FlateDecode stream inflated in place, as one Latin-1-safe byte array, so
    /// a test can search the ToUnicode map and the content stream for text.
    /// </summary>
    internal static byte[] Inflated(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var output = new MemoryStream();
        var pos = 0;
        while (true)
        {
            var s = text.IndexOf("stream\n", pos, StringComparison.Ordinal);
            if (s < 0) break;
            s += "stream\n".Length;
            var e = text.IndexOf("\nendstream", s, StringComparison.Ordinal);
            if (e < 0) break;
            output.Write(pdf, pos, s - pos);
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, s, e - s), CompressionMode.Decompress);
                z.CopyTo(output);
            }
            catch (InvalidDataException)
            {
                output.Write(pdf, s, e - s);   // not deflated; keep the raw bytes
            }
            pos = e + "\nendstream".Length;   // past the keyword, which itself contains "stream\n"
        }
        output.Write(pdf, pos, pdf.Length - pos);
        return output.ToArray();
    }

    /// <summary>
    /// Every stream body of the PDF, inflated where it is deflated, concatenated.
    /// </summary>
    private static byte[] StreamBodies(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var all = new MemoryStream();
        var pos = 0;
        while (true)
        {
            var s = text.IndexOf("stream\n", pos, StringComparison.Ordinal);
            if (s < 0) break;
            s += "stream\n".Length;
            var e = text.IndexOf("\nendstream", s, StringComparison.Ordinal);
            if (e < 0) break;
            byte[] body;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, s, e - s), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                z.CopyTo(inflated);
                body = inflated.ToArray();
            }
            catch (InvalidDataException)
            {
                body = pdf[s..e];
            }
            all.Write(body);
            pos = e + "\nendstream".Length;   // past the keyword, which itself contains "stream\n"
        }
        return all.ToArray();
    }

    /// <summary>The font with one table's directory length rewritten, so the table is cut
    /// short.</summary>
    private static byte[] WithTableLength(byte[] font, string tag, int length)
    {
        var copy = (byte[])font.Clone();
        var (entry, _, _) = FindTable(copy, tag);
        Put32(copy, entry + 12, (uint)length);
        return copy;
    }

    /// <summary>The font with one table's tag overwritten, so the table is missing from the
    /// directory.</summary>
    private static byte[] WithTagRenamed(byte[] font, string tag)
    {
        var copy = (byte[])font.Clone();
        var (entry, _, _) = FindTable(copy, tag);
        Encoding.ASCII.GetBytes("zzzz").CopyTo(copy, entry);
        return copy;
    }

    internal static byte[] LoadAsset(string logicalName)
    {
        using var s = typeof(CmapFormat12Tests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded asset '{logicalName}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    internal sealed record Subtable(int Platform, int Encoding, int Format, byte[] Bytes);

    private static ushort U16(byte[] d, int o) => (ushort)((d[o] << 8) | d[o + 1]);

    private static uint U32(byte[] d, int o) =>
        ((uint)d[o] << 24) | ((uint)d[o + 1] << 16) | ((uint)d[o + 2] << 8) | d[o + 3];

    private static void Put16(byte[] d, int o, int v) { d[o] = (byte)(v >> 8); d[o + 1] = (byte)v; }

    private static void Put32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }

    /// <summary>Finds a table's directory entry: (entry offset, table offset, table
    /// length).</summary>
    private static (int Entry, int Offset, int Length) FindTable(byte[] font, string tag)
    {
        var count = U16(font, 4);
        for (var i = 0; i < count; i++)
        {
            var e = 12 + i * 16;
            if (Encoding.ASCII.GetString(font, e, 4) == tag)
                return (e, (int)U32(font, e + 8), (int)U32(font, e + 12));
        }
        throw new InvalidOperationException($"no '{tag}' table");
    }

    /// <summary>The font's cmap subtables, each with the bytes the encoding record points
    /// at.</summary>
    internal static List<Subtable> Subtables(byte[] font)
    {
        var (_, cmap, _) = FindTable(font, "cmap");
        var count = U16(font, cmap + 2);
        var result = new List<Subtable>();
        for (var i = 0; i < count; i++)
        {
            var rec = cmap + 4 + i * 8;
            var at = cmap + (int)U32(font, rec + 4);
            var format = U16(font, at);
            // Formats 0, 4 and 6 carry a 16-bit length at +2; format 12 a 32-bit one at +4.
            var length = format == 12 ? (int)U32(font, at + 4) : U16(font, at + 2);
            result.Add(new Subtable(U16(font, rec), U16(font, rec + 2), format, font[at..(at + length)]));
        }
        return result;
    }

    /// <summary>
    /// The font with a new cmap table holding only <paramref name="keep"/>. The table is appended
    /// to the file and the directory entry repointed; the old bytes stay where they are,
    /// unreferenced. Table checksums are not recomputed, which this project's parser does not
    /// check.
    /// </summary>
    internal static byte[] WithCmap(byte[] font, IEnumerable<Subtable> keep)
    {
        var tables = keep.ToList();
        Assert.NotEmpty(tables);

        var headerSize = 4 + 8 * tables.Count;
        var cmap = new List<byte>();
        cmap.AddRange(new byte[headerSize]);
        var header = new byte[headerSize];
        Put16(header, 2, tables.Count);
        for (var i = 0; i < tables.Count; i++)
        {
            Put16(header, 4 + i * 8, tables[i].Platform);
            Put16(header, 6 + i * 8, tables[i].Encoding);
            Put32(header, 8 + i * 8, (uint)(cmap.Count));
            cmap.AddRange(tables[i].Bytes);
        }
        var body = cmap.ToArray();
        header.CopyTo(body, 0);

        var padded = (font.Length + 3) & ~3;
        var result = new byte[padded + body.Length];
        font.CopyTo(result, 0);
        body.CopyTo(result, padded);
        var (entry, _, _) = FindTable(font, "cmap");
        Put32(result, entry + 8, (uint)padded);
        Put32(result, entry + 12, (uint)body.Length);
        return result;
    }
}
