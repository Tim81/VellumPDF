// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Fonts.Sfnt;
using static VellumPdf.Kernel.Tests.ConformanceAuditG1Support;
using static VellumPdf.Kernel.Tests.ConformanceAuditG2Support;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// The ISO audit of text and fonts (group G2), one test per clause. The clause inventory, the
/// feature matrix and the verdicts are in <c>docs/conformance-v2.4.md</c>. Every fixture is
/// generated here with the fonts in the tree, and each test first checks that the construct is in
/// the file before it asserts values against the clause. A requirement the code violates has a test
/// that is skipped, with the gap's label in the reason, so the assertion is ready for the fix.
/// </summary>
/// <remarks>
/// ISO 19005-2 is not held in the repository: its clause numbers come from the maintainer's NEN
/// Connect reading, and the test names use them as written there. The other standards are the
/// copies in <c>docs/iso</c>. veraPDF has no rule for several requirements below, and it does not
/// see glyph 0 in an OpenType CFF font, so the tests assert the written keys, codes and bytes
/// directly and use the oracles as a second reading.
/// </remarks>
public sealed partial class ConformanceAuditG2Tests
{
    public enum Kind
    {
        DejaVuBmp,
        DejaVuAstral,
        DejaVuMany,
        SourceSansCff,
    }

    // The text each kind shows, one line per font. DejaVu has glyphs for all of these; Source Sans 3
    // Extra Light has one outside the BMP (U+1F4A9) and none for the DejaVu astral characters.
    private const string Bmp = "Hello, w\u00F6rld \u20AC \u03A9 \u2603";
    private const string Astral = "A\U0001F600B\U0001D538C\U0001D7D8";
    private const string SourceSansText = "Hello \u00E9 \U0001F4A9";

    private static string ManyText()
    {
        var sb = new StringBuilder();
        for (var cp = 0xC0; cp < 0xC0 + 250; cp++)
            sb.Append((char)cp);
        return sb.ToString();
    }

    private static (byte[] Font, string Text)[] Lines(Kind kind) => kind switch
    {
        Kind.DejaVuBmp => [(DejaVu, Bmp)],
        Kind.DejaVuAstral => [(DejaVu, Astral)],
        Kind.DejaVuMany => [(DejaVu, ManyText())],
        _ => [(SourceSans, SourceSansText)],
    };

    public static TheoryData<Kind, PdfConformance> KindsAndClaims()
    {
        var data = new TheoryData<Kind, PdfConformance>();
        foreach (var k in Enum.GetValues<Kind>())
            foreach (var c in Claims)
                data.Add(k, c);
        return data;
    }

    public static TheoryData<Kind> KindData()
    {
        var data = new TheoryData<Kind>();
        foreach (var k in Enum.GetValues<Kind>())
            data.Add(k);
        return data;
    }

    public static TheoryData<PdfConformance> ClaimData()
    {
        var data = new TheoryData<PdfConformance>();
        foreach (var c in Claims)
            data.Add(c);
        return data;
    }

    public static TheoryData<PdfConformance> OracleClaimData()
    {
        var data = new TheoryData<PdfConformance>();
        foreach (var c in ClaimsWithAnOracle)
            data.Add(c);
        return data;
    }

    private static readonly ConcurrentDictionary<(Kind, PdfConformance), byte[]> Cache = new();

    private static byte[] KernelFixture(Kind kind, PdfConformance claim) =>
        Cache.GetOrAdd((kind, claim), key => Save(KernelDocument(key.Item2, Lines(key.Item1))));

    private static byte[] FontBytes(Kind kind) => kind == Kind.SourceSansCff ? SourceSans : DejaVu;

    private static string PostScriptName(Kind kind) => kind == Kind.SourceSansCff ? "SourceSans3-ExtraLight"
        : "DejaVuSans";

    // ── ISO 32000-1, 9.7 and 9.8; ISO 19005-2, 6.2.11.2 to 6.2.11.4; ISO 14289-1, 7.21.2 to 7.21.4 ─────

    [Theory]
    [MemberData(nameof(KindsAndClaims))]
    public void Iso32000_1_9_7_AnEmbeddedFontIsAType0FontWithIdentityHAndOneCidFont(Kind kind, PdfConformance claim)
    {
        using var pdf = new Pdf(KernelFixture(kind, claim));
        var font = Assert.Single(EmbeddedFonts(pdf));
        Assert.Empty(SimpleFonts(pdf));

        // 9.7.6: a composite font is a Type0 dictionary with an Encoding CMap and one descendant. The
        // predefined Identity-H is not embedded (19005-2 6.2.11.3.3, 14289-1 7.21.3.3), so no CMap
        // stream, no WMode and no UseCMap are written.
        Assert.Equal("Font", Assert.IsType<PdfName>(font.Type0.Get(PdfName.Type)).Value);
        Assert.Equal("Type0", Assert.IsType<PdfName>(font.Type0.Get(PdfName.Subtype)).Value);
        Assert.Equal("Identity-H", Assert.IsType<PdfName>(font.Type0.Get(N("Encoding"))).Value);
        var descendants = Assert.IsType<PdfArray>(font.Type0.Get(N("DescendantFonts")));
        Assert.Equal(1, descendants.Count);

        // 9.8.1: a font descriptor is not used with a Type0 font; it belongs to the CIDFont.
        Assert.Null(font.Type0.Get(N("FontDescriptor")));
        Assert.IsType<PdfIndirectReference>(font.CidFont.Get(N("FontDescriptor")));

        // Table 117: Type, Subtype, BaseFont, CIDSystemInfo. Identity-H allows any CIDSystemInfo
        // (19005-2 6.2.11.3.1, 14289-1 7.21.3.1), and the three keys of Table 116 are all present.
        Assert.Equal("Font", Assert.IsType<PdfName>(font.CidFont.Get(PdfName.Type)).Value);
        var info = pdf.Dict(font.CidFont.Get(N("CIDSystemInfo")));
        Assert.Equal("Adobe",
            Encoding.ASCII.GetString(Assert.IsType<PdfLiteralString>(info.Get(N("Registry"))).Bytes.ToArray()));
        Assert.Equal("Identity",
            Encoding.ASCII.GetString(Assert.IsType<PdfLiteralString>(info.Get(N("Ordering"))).Bytes.ToArray()));
        Assert.Equal(0, Assert.IsType<PdfInteger>(info.Get(N("Supplement"))).Value);

        // 9.7.4.1 and 9.9: glyf outlines are a CIDFontType2 with an Identity CIDToGIDMap, CFF
        // outlines are a CIDFontType0 that has no CIDToGIDMap (a Type 2 key). 9.7.4.2: a CFF program
        // without CID operators takes its CIDs as glyph ids.
        if (kind == Kind.SourceSansCff)
        {
            Assert.Equal("CIDFontType0", Assert.IsType<PdfName>(font.CidFont.Get(PdfName.Subtype)).Value);
            Assert.Null(font.CidFont.Get(N("CIDToGIDMap")));
            Assert.NotNull(font.Descriptor.Get(N("FontFile3")));
            Assert.Null(font.Descriptor.Get(N("FontFile2")));
            Assert.Equal("OpenType", Assert.IsType<PdfName>(font.Program.Dictionary.Get(PdfName.Subtype)).Value);
        }
        else
        {
            Assert.Equal("CIDFontType2", Assert.IsType<PdfName>(font.CidFont.Get(PdfName.Subtype)).Value);
            Assert.Equal("Identity", Assert.IsType<PdfName>(font.CidFont.Get(N("CIDToGIDMap"))).Value);
            Assert.NotNull(font.Descriptor.Get(N("FontFile2")));
            Assert.Null(font.Descriptor.Get(N("FontFile3")));
        }

        // 9.8.1: at most one of FontFile, FontFile2 and FontFile3 is present.
        Assert.Single(new[] { "FontFile", "FontFile2", "FontFile3" }, k => font.Descriptor.Get(N(k)) is not null);

        // 9.7.4.1: the width entries are DW and W; neither vertical entry is written, and the
        // CIDSet of 6.2.11.4.2 / 7.21.4.2 is absent, so its "identifies all CIDs" rule is vacuous.
        Assert.Equal(1000, Assert.IsType<PdfInteger>(font.CidFont.Get(N("DW"))).Value);
        Assert.IsType<PdfArray>(font.CidFont.Get(N("W")));
        Assert.Null(font.CidFont.Get(N("W2")));
        Assert.Null(font.CidFont.Get(N("DW2")));
        Assert.Null(font.Descriptor.Get(N("CIDSet")));

        // 9.6.4 and 9.8.1: the CIDFont's BaseFont and the descriptor's FontName agree (shall). The Type0
        // BaseFont is a should (Table 121) that names the CIDFont for a Type 2 descendant and another
        // form for a Type 0 CIDFont, so it is asserted for the glyf kinds only.
        var baseFont = BaseFont(font.CidFont);
        Assert.Equal(baseFont, Assert.IsType<PdfName>(font.Descriptor.Get(N("FontName"))).Value);
        if (kind != Kind.SourceSansCff)
            Assert.Equal(baseFont, BaseFont(font.Type0));

        // 9.10.3: the Type0 font names its ToUnicode stream; the descriptor's program is a stream.
        Assert.NotEmpty(font.ToUnicodeBytes);
        Assert.NotEmpty(font.ProgramBytes);
    }

    [Theory]
    [MemberData(nameof(KindData))]
    public void Iso32000_1_9_6_4_ASubsetNameIsSixCapitalsAPlusAndThePostScriptNameOfTheSource(Kind kind)
    {
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.None));
        var font = Assert.Single(EmbeddedFonts(pdf));

        // 9.6.4 (and 32000-2 9.9.2): the tag is exactly six uppercase letters and a plus sign, then
        // the PostScript name of the font the subset was made from, in the BaseFont and in the
        // FontName of the descriptor (the Type0 font has no descriptor).
        var pattern = "^[A-Z]{6}\\+" + PostScriptName(kind) + "$";
        Assert.Matches(pattern, BaseFont(font.CidFont));
        Assert.Matches(pattern, Assert.IsType<PdfName>(font.Descriptor.Get(N("FontName"))).Value);
    }

    [Fact]
    public void Iso32000_2_9_9_2_TwoSubsetsOfOneFontInOneFileShareAnEmbeddedFontAndSoOneTag()
    {
        // 32000-2 9.9.2: different subsets of the same font in one file have different tags. Two
        // registrations of the same program return the one handle, so the file holds one subset.
        using var doc = new PdfDocument { Timestamp = Pinned, DocumentId = PinnedId };
        var first = doc.UseTrueTypeFont(DejaVu);
        var second = doc.UseTrueTypeFont(DejaVu);
        Assert.Same(first, second);

        // The 32000-1 sentence says "different subsets in the same PDF file"; 32000-2 narrows it to
        // subsets of the same font. A file with no claim is written as PDF 2.0, so 32000-2 governs it:
        // the regular and bold faces of one family share a tag and differ after the plus sign, which
        // that wording allows. The 32000-1 reading is the skipped test under #754.
        var names = FaceNames(PdfConformance.None, Standard14.Helvetica, Standard14.HelveticaBold);
        Assert.Equal(2, names.Count);
        Assert.Equal(names[0][..6], names[1][..6]);
        Assert.NotEqual(names[0], names[1]);
    }

    // ── 9.8: the descriptor ──────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindData))]
    public void Iso32000_1_9_8_FontBBoxIsTheHeadBoxScaledToAThousandAndRoundedOutward(Kind kind)
    {
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.None));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var box = pdf.Array(font.Descriptor.Get(N("FontBBox")));

        // Table 122: FontBBox is required and is a rectangle of four numbers in glyph space.
        Assert.Equal(4, box.Count);
        var written = box.Items().Select(Num).ToArray();
        Assert.All(written, v => Assert.Equal(Math.Floor(v), v));

        // The value is the head table's own box (OpenType head: xMin, yMin, xMax, yMax) at 1000
        // units per em, with the lower edges rounded down and the upper edges up, so it still
        // encloses every glyph of the font. The expected value is computed here from the source font
        // program, not from the embedder.
        var (xMin, yMin, xMax, yMax, upem) = HeadBox(FontBytes(kind));
        double Exact(int v) => v * 1000.0 / upem;
        Assert.Equal(Math.Floor(Exact(xMin)), written[0]);
        Assert.Equal(Math.Floor(Exact(yMin)), written[1]);
        Assert.Equal(Math.Ceiling(Exact(xMax)), written[2]);
        Assert.Equal(Math.Ceiling(Exact(yMax)), written[3]);
        Assert.True(written[0] <= Exact(xMin) && written[1] <= Exact(yMin));
        Assert.True(written[2] >= Exact(xMax) && written[3] >= Exact(yMax));

        // The embedded program is the subset and carries the same head table, so the box is the
        // whole font's, as the descriptor documents.
        var program = Sfnt(font.ProgramBytes);
        var (pxMin, pyMin, pxMax, pyMax, pUpem) = HeadBox(font.ProgramBytes);
        Assert.Equal((xMin, yMin, xMax, yMax, upem), (pxMin, pyMin, pxMax, pyMax, pUpem));
        Assert.True(program.HasTable(new Tag("head")));
    }

    [Theory]
    [MemberData(nameof(KindData))]
    public void Iso32000_1_9_8_TheDescriptorHoldsEveryRequiredKeyAndTheSignsTheTextGives(Kind kind)
    {
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.None));
        var d = Assert.Single(EmbeddedFonts(pdf)).Descriptor;

        // Table 122: Type, FontName, Flags, FontBBox, ItalicAngle, Ascent, Descent, CapHeight and
        // StemV are required for a font that is not Type 3.
        Assert.Equal("FontDescriptor", Assert.IsType<PdfName>(d.Get(PdfName.Type)).Value);
        foreach (var key in new[] { "FontName", "Flags", "FontBBox", "ItalicAngle", "Ascent", "Descent", "CapHeight",
            "StemV" })
            Assert.NotNull(d.Get(N(key)));

        // Ascent is above the baseline, Descent is a negative number (9.8.1), CapHeight is positive.
        Assert.True(Num(d.Get(N("Ascent"))) > 0);
        Assert.True(Num(d.Get(N("Descent"))) < 0);
        Assert.True(Num(d.Get(N("CapHeight"))) > 0);
        Assert.True(Num(d.Get(N("ItalicAngle"))) <= 0);
        // StemV is a heuristic constant; the clause only asks for it to be present and a number.
        Assert.Equal(80, Assert.IsType<PdfInteger>(d.Get(N("StemV"))).Value);
    }

    [Theory]
    [MemberData(nameof(KindsAndClaims))]
    public void Iso32000_1_9_8_2_FlagsHoldNoUndefinedBitAndExactlyOneOfSymbolicAndNonsymbolic(
        Kind kind, PdfConformance claim)
    {
        using var pdf = new Pdf(KernelFixture(kind, claim));
        var flags = Assert.IsType<PdfInteger>(Assert.Single(EmbeddedFonts(pdf)).Descriptor.Get(N("Flags"))).Value;

        // Table 123: bits 1 to 4, 6, 7 and 17 to 19 are defined; every other bit is 0.
        const long defined = 1 | 2 | 4 | 8 | 32 | 64 | (1 << 16) | (1 << 17) | (1 << 18);
        Assert.Equal(0, flags & ~defined);
        // Symbolic (bit 3) and Nonsymbolic (bit 6) are never both set and never both clear.
        Assert.NotEqual((flags & 4) != 0, (flags & 32) != 0);
    }

    [Fact(Skip = "Gap #753: the Nonsymbolic flag is written for every font, so a subset that shows characters " +
        "outside the Adobe standard Latin set (here Greek, a snowman and an emoji) is not marked " +
        "Symbolic, which 9.8.2 requires whenever the font holds such glyphs.")]
    public void Iso32000_1_9_8_2_ASubsetWithGlyphsOutsideTheLatinSetIsMarkedSymbolic()
    {
        using var pdf = new Pdf(KernelFixture(Kind.DejaVuBmp, PdfConformance.None));
        var flags = Assert.IsType<PdfInteger>(Assert.Single(EmbeddedFonts(pdf)).Descriptor.Get(N("Flags"))).Value;
        Assert.Equal(4, flags & 4);
        Assert.Equal(0, flags & 32);
    }

    [Fact]
    public void Iso32000_1_9_8_2_FixedPitchAndItalicFollowThePostTableOfTheProgram()
    {
        // Table 123: FixedPitch means every glyph has one width; Italic means slanted. The writer
        // derives both from the post table (isFixedPitch and italicAngle) of the source font. The
        // value is read back here from the file with the font set named by the clause.
        foreach (var (face, fixedPitch, italic) in new[]
        {
            (Standard14.Courier, true, false),
            (Standard14.CourierOblique, true, true),
            (Standard14.Helvetica, false, false),
            (Standard14.TimesItalic, false, true),
        })
        {
            using var doc = new PdfDocument { Timestamp = Pinned, DocumentId = PinnedId };
            var handle = doc.UseTrueTypeFont(Standard14Substitutes.GetFontProgram(face));
            var page = doc.AddPage();
            doc.RegisterEmbeddedFontUsage(page, handle);
            var glyphs = new ushort[2];
            handle.GetGlyphIds("Hi", glyphs);
            new VellumPdf.Canvas.PdfCanvas(page).BeginArtifactMarkedContent().BeginText()
                .SetFontByName(handle.ResourceName, 12).ShowGlyphs(glyphs).EndText().EndMarkedContent().Finish();
            using var pdf = new Pdf(Save(doc));
            var d = Assert.Single(EmbeddedFonts(pdf)).Descriptor;
            var flags = Assert.IsType<PdfInteger>(d.Get(N("Flags"))).Value;
            Assert.Equal(fixedPitch, (flags & 1) != 0);
            Assert.Equal(italic, (flags & 64) != 0);
            Assert.Equal(italic, Num(d.Get(N("ItalicAngle"))) != 0);
        }
    }

    // ── 9.7.4.3: widths ───────────────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindsAndClaims))]
    public void Iso19005_2_6_2_11_5_WidthsInTheWArrayAgreeWithTheProgramWithinOneThousandth(
        Kind kind, PdfConformance claim)
    {
        using var pdf = new Pdf(KernelFixture(kind, claim));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var w = pdf.Array(font.CidFont.Get(N("W")));

        // 9.7.4.3: the W array holds groups "c [w1 ... wn]" (or "cfirst clast w"). This writer uses
        // the first form only; a group of the second form fails the test, which then needs a reader.
        var widths = new Dictionary<int, double>();
        for (var i = 0; i < w.Count; i += 2)
        {
            var first = (int)Assert.IsType<PdfInteger>(w[i]).Value;
            var run = Assert.IsType<PdfArray>(w[i + 1]);
            for (var k = 0; k < run.Count; k++)
                widths.Add(first + k, Num(run[k]));
        }

        // 9.7.4.2: every CIDFont has a glyph for CID 0, and a code that is shown has a width.
        Assert.True(widths.ContainsKey(0));
        var shown = ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)).Distinct().ToList();
        Assert.NotEmpty(shown);
        Assert.All(shown, code => Assert.True(widths.ContainsKey(code), $"code {code:X4} has no width"));

        // 6.2.11.5 and 7.21.5: consistent means within 1/1000. The program's hmtx advance at 1000
        // units per em is read from the source font here; the written value is its rounding, so the
        // difference is at most one half.
        var source = FontBytes(kind);
        foreach (var (cid, written) in widths)
        {
            var exact = AdvanceInThousandths(source, cid);
            Assert.True(Math.Abs(written - exact) <= 0.5, $"CID {cid}: W {written}, hmtx {exact}");
        }
    }

    // ── 9.9: the embedded program ─────────────────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindData))]
    public void Iso32000_1_9_9_AGlyfProgramHasTheRequiredTablesAndNoCmapAndACffProgramKeepsIt(Kind kind)
    {
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.None));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var program = Sfnt(font.ProgramBytes);
        var source = Sfnt(FontBytes(kind));
        var tags = program.Tables.Keys.Select(t => t.ToString()).ToHashSet();

        if (kind == Kind.SourceSansCff)
        {
            // 32000-1 Table 126 and 9.9 (32000-2 Table 124 and 9.9.1): an OpenType program with CFF outlines holds
            // the CFF table and
            // the cmap table, Subtype is OpenType, and no Length1/2/3 key is written for it.
            Assert.Contains("CFF ", tags);
            Assert.Contains("cmap", tags);
            Assert.Equal("OpenType", Assert.IsType<PdfName>(font.Program.Dictionary.Get(PdfName.Subtype)).Value);
            Assert.All(new[] { "Length1", "Length2", "Length3" }, k => Assert.Null(font.Program.Dictionary.Get(N(k))));
            // 32000-1 9.9, Table 127: exactly one font in the CFF data, so the CFF Name INDEX holds one name.
            var cff = program.GetTableBytes(new Tag("CFF ")).ToArray();
            Assert.Equal(1, (cff[cff[2]] << 8) | cff[cff[2] + 1]);
        }
        else
        {
            // 32000-1 9.9 (32000-2 9.9.1): head, hhea, hmtx, loca, maxp and glyf; cvt, fpgm and prep when the
            // source has
            // them; and no cmap, because the descendant is a CIDFont whose CIDToGIDMap maps codes.
            foreach (var tag in new[] { "head", "hhea", "hmtx", "loca", "maxp", "glyf" })
                Assert.Contains(tag, tags);
            foreach (var tag in new[] { "cvt ", "fpgm", "prep" }.Where(t => source.HasTable(new Tag(t))))
                Assert.Contains(tag, tags);
            Assert.DoesNotContain("cmap", tags);
            // Table 127: Length1 is the length of the decoded TrueType program.
            Assert.Equal(font.ProgramBytes.Length,
                Assert.IsType<PdfInteger>(font.Program.Dictionary.Get(N("Length1"))).Value);
            Assert.Null(font.Program.Dictionary.Get(PdfName.Subtype));
        }

        // 32000-1 9.9: the vertical metric tables are never used, and a program holds a table directory
        // whose checksums are right (OpenType, "Table directory").
        Assert.Equal(source.HasTable(new Tag("vhea")), tags.Contains("vhea"));
        VerifyChecksums(font.ProgramBytes);
    }

    private static void VerifyChecksums(byte[] font)
    {
        static uint Sum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            for (var i = 0; i < data.Length; i += 4)
            {
                uint word = 0;
                for (var b = 0; b < 4; b++)
                    word = (word << 8) | (i + b < data.Length ? data[i + b] : (uint)0);
                sum += word;
            }

            return sum;
        }

        uint U32(int o) => (uint)((font[o] << 24) | (font[o + 1] << 16) | (font[o + 2] << 8) | font[o + 3]);
        var count = (font[4] << 8) | font[5];
        for (var i = 0; i < count; i++)
        {
            var record = 12 + (16 * i);
            var tag = Encoding.ASCII.GetString(font, record, 4);
            var offset = (int)U32(record + 8);
            var length = (int)U32(record + 12);
            var data = font.AsSpan(offset, length).ToArray();
            if (tag == "head")
                data[8] = data[9] = data[10] = data[11] = 0;
            Assert.Equal(U32(record + 4), Sum(data));
        }

        // head.checkSumAdjustment makes the whole file sum to 0xB1B0AFBA.
        Assert.Equal(0xB1B0AFBAu, Sum(font));
    }

    [Theory]
    [MemberData(nameof(KindData))]
    public void Iso32000_1_9_7_4_2_ASubsetDefinesGlyphZeroAndEveryGlyphItShows(Kind kind)
    {
        if (kind == Kind.SourceSansCff)
        {
            // The CFF subset keeps the glyph ids and blanks the glyphs nothing shows. Every shown glyph
            // and glyph 0 must hold the source's own charstring, not only a width: the bytes of each are
            // compared with the source program's CFF table (read with the library's CFF parser).
            using var cffPdf = new Pdf(KernelFixture(kind, PdfConformance.None));
            var cffFont = Assert.Single(EmbeddedFonts(cffPdf));
            var cffTag = new Tag("CFF ");
            var sourceCff = VellumPdf.Fonts.Cff.CffFont.Parse(Sfnt(SourceSans).GetTableBytes(cffTag).ToArray());
            var subsetCff = VellumPdf.Fonts.Cff.CffFont.Parse(
                Sfnt(cffFont.ProgramBytes).GetTableBytes(cffTag).ToArray());
            var shownCff = ShownStrings(Content(cffPdf)).SelectMany(s => Codes(s.Codes)).Distinct().Append(0).ToList();
            Assert.True(shownCff.Count > 5);
            var localSubrs = new HashSet<int>();
            var globalSubrs = new HashSet<int>();
            foreach (var gid in shownCff)
            {
                var expected = sourceCff.GetCharstring(gid).ToArray();
                Assert.True(expected.Length > 1, $"glyph {gid} has no outline in the source");
                Assert.Equal(expected, subsetCff.GetCharstring(gid).ToArray());
                var stems = 0;
                ReachSubrs(sourceCff, expected, [], ref stems, localSubrs, globalSubrs);
            }

            // A glyph's outline often sits in subroutines, so the top-level charstring can be a short call.
            // Every subroutine a shown glyph reaches, local and global, must hold the source's bytes.
            Assert.NotEmpty(localSubrs.Concat(globalSubrs));
            foreach (var index in localSubrs)
                Assert.Equal(sourceCff.GetLocalSubr(index).ToArray(), subsetCff.GetLocalSubr(index).ToArray());
            foreach (var index in globalSubrs)
                Assert.Equal(sourceCff.GetGlobalSubr(index).ToArray(), subsetCff.GetGlobalSubr(index).ToArray());
            return;
        }

        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.None));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var source = Sfnt(FontBytes(kind));
        var subset = Sfnt(font.ProgramBytes);
        var shown = ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)).Distinct().ToList();

        // 9.9.2 (32000-2) and 9.8.1: .notdef is defined in the subset, so glyph 0 has outline data.
        (int Start, int End) Range(SfntFont f, int gid)
        {
            var loca = f.GetTableBytes(new Tag("loca")).ToArray();
            var head = f.GetTableBytes(new Tag("head")).ToArray();
            var longFormat = ((head[50] << 8) | head[51]) == 1;
            int Entry(int i) => longFormat
                ? (loca[4 * i] << 24) | (loca[(4 * i) + 1] << 16) | (loca[(4 * i) + 2] << 8) | loca[(4 * i) + 3]
                : 2 * ((loca[2 * i] << 8) | loca[(2 * i) + 1]);
            return (Entry(gid), Entry(gid + 1));
        }

        var needed = new HashSet<int>(shown) { 0 };
        var queue = new Queue<int>(needed);
        var glyfSource = source.GetTableBytes(new Tag("glyf")).ToArray();
        var glyfSubset = subset.GetTableBytes(new Tag("glyf")).ToArray();
        while (queue.Count > 0)
        {
            var gid = queue.Dequeue();
            var (s0, s1) = Range(source, gid);
            var (t0, t1) = Range(subset, gid);
            // The subset holds the source's bytes for every glyph that is shown, and for the
            // components of a composite glyph (OpenType glyf: composite glyphs name component glyphs).
            Assert.Equal(glyfSource.AsSpan(s0, s1 - s0).ToArray(), glyfSubset.AsSpan(t0, t1 - t0).ToArray());
            if (s1 - s0 >= 10 && (short)((glyfSource[s0] << 8) | glyfSource[s0 + 1]) < 0)
            {
                var o = s0 + 10;
                int flags;
                do
                {
                    flags = (glyfSource[o] << 8) | glyfSource[o + 1];
                    var component = (glyfSource[o + 2] << 8) | glyfSource[o + 3];
                    if (needed.Add(component))
                        queue.Enqueue(component);
                    o += 4 + ((flags & 1) != 0 ? 4 : 2);
                    o += (flags & 8) != 0 ? 2 : (flags & 0x40) != 0 ? 4 : (flags & 0x80) != 0 ? 8 : 0;
                }
                while ((flags & 0x20) != 0);
            }
        }

        Assert.True(Range(subset, 0).End > Range(subset, 0).Start, ".notdef has no outline in the subset");
    }

    // ── 9.10 and 19005-2 6.2.11.7: ToUnicode ──────────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindsAndClaims))]
    public void Iso32000_1_9_10_3_AToUnicodeCMapCoversEveryShownCodeAndMapsItBackToTheText(
        Kind kind, PdfConformance claim)
    {
        using var pdf = new Pdf(KernelFixture(kind, claim));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var map = ParseToUnicode(font.ToUnicodeBytes);

        // 9.10.3: the CMap has a codespace range consistent with the font's encoding: Identity-H
        // codes are two bytes, so <0000> to <FFFF>. It maps with bfchar and bfrange only.
        Assert.Equal([(0x0000, 0xFFFF, 2)], map.CodeSpace);
        Assert.NotEmpty(map.Blocks);
        Assert.All(map.Blocks, b => Assert.Equal(b.Declared, b.Actual));
        // TN5014 caps one block at 100 entries.
        Assert.All(map.Blocks, b => Assert.InRange(b.Actual, 1, 100));

        // Every code the content shows has an entry, and reading the text back through the map
        // gives the text that was drawn.
        var shown = ShownStrings(Content(pdf));
        Assert.NotEmpty(shown);
        var codes = shown.SelectMany(s => Codes(s.Codes)).ToList();
        Assert.All(codes, c => Assert.True(map.Entries.ContainsKey(c), $"no ToUnicode entry for {c:X4}"));
        Assert.Equal(string.Concat(Lines(kind).Select(l => l.Text)), string.Concat(shown.Select(s => Decode(s, font))));
    }

    [Fact]
    public void Iso32000_1_9_10_3_AToUnicodeCMapOfMoreThanOneHundredGlyphsIsWrittenInBlocksOfAtMostOneHundred()
    {
        using var pdf = new Pdf(KernelFixture(Kind.DejaVuMany, PdfConformance.PdfA2u));
        var map = ParseToUnicode(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);

        // 250 distinct characters (U+00C0 to U+01B9) plus glyph 0's entry are 251 entries, in blocks of
        // 100, 100 and 51 as TN5014 limits them.
        Assert.True(map.Entries.Count >= 250);
        Assert.True(map.Blocks.Count >= 3);
        Assert.All(map.Blocks, b => Assert.Equal("bfchar", b.Kind));
        Assert.All(map.Blocks, b => Assert.InRange(b.Actual, 1, 100));
        Assert.Equal(map.Entries.Count, map.Blocks.Sum(b => b.Actual));
    }

    [Theory]
    [MemberData(nameof(KindData))]
    public void Issue647_EveryLineOfAToUnicodeCMapEndsWithOneLineFeed(Kind kind)
    {
        // #647's choice, not a clause: the stream is the same on every operating system, so it holds no
        // carriage return. The ASCII-only check rests on TN5014.
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.PdfA2u));
        var bytes = Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes;

        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.Equal((byte)'\n', bytes[^1]);
        Assert.True(bytes.Count(b => b == (byte)'\n') > 10);
        // The CMap is ASCII text in PostScript syntax (TN5014): no byte above 0x7E.
        Assert.All(bytes, b => Assert.True(b == 10 || (b >= 0x20 && b <= 0x7E)));
    }

    [Theory]
    [MemberData(nameof(KindsAndClaims))]
    public void Iso19005_2_6_2_11_7_2_ToUnicodeValuesAreAboveZeroAndNeitherFeffNorFffe(Kind kind, PdfConformance claim)
    {
        using var pdf = new Pdf(KernelFixture(kind, claim));
        var map = ParseToUnicode(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);

        // 6.2.11.7.2 and 14289-1 7.21.7: each value is greater than zero and is not U+FEFF or
        // U+FFFE. Read from the CMap's own UTF-16BE destinations, string by string.
        Assert.NotEmpty(map.Entries);
        foreach (var (code, values) in map.Entries)
        {
            foreach (var value in values)
            {
                Assert.NotEmpty(value);
                Assert.DoesNotContain('\0', value);
                Assert.DoesNotContain('\uFEFF', value);
                Assert.DoesNotContain('\uFFFE', value);
            }

            // A code with two entries is pinned in the shared-glyph test, not refused here.
        }
    }

    public enum Element
    {
        Kernel,
        Paragraph,
        Heading,
        ListItem,
        TableCell,
    }

    public static TheoryData<Element> Elements()
    {
        var data = new TheoryData<Element>();
        foreach (var e in Enum.GetValues<Element>())
            data.Add(e);
        return data;
    }

    public static TheoryData<Element, PdfConformance> ElementsWithoutAToUnicodeLevel()
    {
        var data = new TheoryData<Element, PdfConformance>();
        foreach (var e in Enum.GetValues<Element>())
        {
            data.Add(e, PdfConformance.None);
            data.Add(e, PdfConformance.PdfA2b);
        }

        return data;
    }

    public static TheoryData<Element, PdfConformance> ElementsUnderALevelThatNeedsToUnicode()
    {
        var data = new TheoryData<Element, PdfConformance>();
        foreach (var e in Enum.GetValues<Element>())
        {
            data.Add(e, PdfConformance.PdfA2u);
            data.Add(e, PdfConformance.PdfA2a);
            data.Add(e, PdfConformance.PdfUA1);
        }

        return data;
    }

    // Glyph 5367 is U+FEFF in DejaVu Sans; the first assertion that uses it reads the font's cmap.
    private const int DejaVuFeffGlyph = 5367;

    [Theory]
    [MemberData(nameof(ElementsWithoutAToUnicodeLevel))]
    public void Iso19005_2_6_2_11_7_2_ATextWithAZeroWidthNoBreakSpaceKeepsTheFontsOwnMappingWhereNoLevelNeedsToUnicode(
        Element element, PdfConformance claim)
    {
        // A file with no claim or PDF/A-2b has no ToUnicode value rule, and 2.x keeps writing what
        // it wrote: the code of the character maps to U+FEFF. Every Layout element that draws text
        // in an embedded font is covered, not only a paragraph.
        Assert.Equal(DejaVuFeffGlyph, CmapLookup(DejaVu)(0xFEFF));
        var bytes = WithFeff(element, claim);
        using var pdf = new Pdf(bytes);
        var font = Assert.Single(EmbeddedFonts(pdf));
        var map = ParseToUnicode(font.ToUnicodeBytes);

        Assert.Contains(map.Entries, e => e.Value.Contains(Feff));
        Assert.Contains("<14F7> <FEFF>", Encoding.ASCII.GetString(font.ToUnicodeBytes));
        Assert.Contains(Feff, string.Concat(ShownText(pdf)));
    }

    private static byte[] WithFeff(Element element, PdfConformance claim) =>
        WithText(element, claim, DejaVu, "A" + Feff + "B");

    private static byte[] WithText(Element element, PdfConformance claim, byte[] font, string text)
    {
        if (element == Element.Kernel)
            return Save(KernelDocument(claim, (font, text)));
        var doc = LayoutDocument(claim);
        var style = Style(doc, font);
        switch (element)
        {
            case Element.Paragraph:
                doc.Add(new VellumPdf.Layout.Elements.Paragraph(text, style));
                break;
            case Element.Heading:
                doc.Add(new VellumPdf.Layout.Elements.Heading(text, style) { Level = 1 });
                break;
            case Element.ListItem:
                doc.Add(new VellumPdf.Layout.Elements.ListElement().Add(new VellumPdf.Layout.Elements.ListItem(text,
                    style)));
                break;
            default:
                var table = new VellumPdf.Layout.Elements.Table.TableElement { DefaultCellStyle = style };
                table.AddRow().AddCell(text);
                doc.Add(table);
                break;
        }

        return Save(doc);
    }

    [Theory]
    [InlineData(false, PdfConformance.PdfA2u)]
    [InlineData(true, PdfConformance.PdfA2u)]
    [InlineData(true, PdfConformance.PdfA2a)]
    [InlineData(false, PdfConformance.PdfA2a)]
    [InlineData(true, PdfConformance.PdfUA1)]
    [InlineData(false, PdfConformance.PdfUA1)]
    public void Iso19005_2_6_2_11_7_2_ACharacterThatToUnicodeCannotNameIsNotWrittenUnderALevelThatNeedsToUnicode(
        bool layout, PdfConformance claim)
    {
        // The save does not refuse the text: it is drawn, and ToUnicode names its glyph by a value
        // the clause allows. A refusal would be another way to meet the clause, so none is caught.
        var bytes = WithFeff(layout ? Element.Paragraph : Element.Kernel, claim);
        using var pdf = new Pdf(bytes);
        Assert.Contains(Feff, string.Concat(ShownText(pdf)));
        var map = ParseToUnicode(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);
        Assert.DoesNotContain(map.Entries, e => e.Value.Any(v => v.Contains(Feff) || v.Contains('\uFFFE')
            || v.Contains('\0')));
    }

    [Theory]
    [MemberData(nameof(ElementsUnderALevelThatNeedsToUnicode))]
    public void Issue752_AZeroWidthNoBreakSpaceIsNamedAsAWordJoinerInToUnicodeAndStillDrawnAsItsOwnGlyph(
        Element element, PdfConformance claim)
    {
        var bytes = WithFeff(element, claim);
        using var pdf = new Pdf(bytes);
        var font = Assert.Single(EmbeddedFonts(pdf));

        // The glyph is shown by the code the cmap gives U+FEFF, and ToUnicode names that U+2060.
        Assert.Contains(DejaVuFeffGlyph, ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)));
        Assert.Equal("\u2060", ParseToUnicode(font.ToUnicodeBytes).Single(DejaVuFeffGlyph));
        var cmap = Encoding.ASCII.GetString(font.ToUnicodeBytes);
        Assert.Contains("<14F7> <2060>", cmap);
        Assert.DoesNotContain("<14F7> <FEFF>", cmap);
        Assert.Contains(Feff, string.Concat(ShownText(pdf)));
    }

    [Theory]
    [MemberData(nameof(ElementsUnderALevelThatNeedsToUnicode))]
    public void Issue752_AZeroWidthNoBreakSpaceInAnOpenTypeCffFontIsNamedAsAWordJoiner(
        Element element, PdfConformance claim)
    {
        Assert.Equal(2458, CmapLookup(SourceSans)(0xFEFF));
        var bytes = WithText(element, claim, SourceSans, "A" + Feff + "B");
        using var pdf = new Pdf(bytes);
        var font = Assert.Single(EmbeddedFonts(pdf));
        Assert.Contains(2458, ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)));
        Assert.Equal("\u2060", ParseToUnicode(font.ToUnicodeBytes).Single(2458));
        Assert.Contains("<099A> <2060>", Encoding.ASCII.GetString(font.ToUnicodeBytes));
    }

    [Theory]
    [InlineData(Element.Kernel, PdfConformance.PdfA2u, true)]
    [InlineData(Element.Kernel, PdfConformance.PdfA2a, true)]
    [InlineData(Element.Kernel, PdfConformance.PdfUA1, true)]
    [InlineData(Element.Paragraph, PdfConformance.PdfA2u, true)]
    [InlineData(Element.Paragraph, PdfConformance.PdfA2a, true)]
    [InlineData(Element.Paragraph, PdfConformance.PdfUA1, true)]
    [InlineData(Element.Kernel, PdfConformance.PdfA2u, false)]
    [InlineData(Element.Paragraph, PdfConformance.PdfUA1, false)]
    public void Issue752_VeraPdfAcceptsTheFileWithAZeroWidthNoBreakSpaceUnderALevelThatNeedsToUnicode(
        Element element, PdfConformance claim, bool dejaVu)
    {
        var bytes = WithText(element, claim, dejaVu ? DejaVu : SourceSans, "A" + Feff + "B");
        Assert.Empty(VeraPdfFailures(Flavour(claim), bytes));
    }

    [Fact]
    public void Issue752_PdftotextReadsAWordJoinerWhereTheTextHeldAZeroWidthNoBreakSpace()
    {
        var text = PopplerText(WithFeff(Element.Kernel, PdfConformance.PdfA2u));
        Assert.Contains("A\u2060B", text, StringComparison.Ordinal);
        Assert.DoesNotContain(Feff, text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0xFEFF, 0x2060)]
    [InlineData(0xFFFE, 0xFFFD)]
    [InlineData(0x0000, 0xFFFD)]
    public void Issue752_AGlyphReachedOnlyByAForbiddenCodePointIsNamedByTheSubstituteUnderAClaim(int codePoint,
        int expected)
    {
        var map = new Dictionary<int, ushort> { [0x41] = 3, [codePoint] = 9 };
        var none = new HashSet<int>();

        var claimed = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(map, none, true, null);
        Assert.Contains($"<0009> <{expected:X4}>", claimed);
        Assert.DoesNotContain($"<0009> <{codePoint:X4}>", claimed);

        // Without a claim the entry is what the cmap gave, as before.
        var unclaimed = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(map, none);
        Assert.Contains($"<0009> <{codePoint:X4}>", unclaimed);
    }

    [Fact]
    public void Issue752_AGlyphAnotherCodePointAlsoOwnsKeepsThatCodePointsEntryAndIsNotReported()
    {
        // U+FEFF and U+2060 on one glyph: the allowed code point speaks for it, one line, no
        // substitution to report.
        var map = new Dictionary<int, ushort> { [0xFEFF] = 9, [0x2060] = 9, [0x41] = 3 };
        var substitutions = new List<(int CodePoint, int Value)>();
        var cmap = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(map, new HashSet<int>(), true, substitutions);

        Assert.Single(Regex.Matches(cmap, "<0009> <"));
        Assert.Contains("<0009> <2060>", cmap);
        Assert.Empty(substitutions);
    }

    [Fact]
    public void Issue752_SeveralForbiddenCodePointsOnOneGlyphGiveOneEntryForTheLowest()
    {
        var map = new Dictionary<int, ushort> { [0xFFFE] = 9, [0xFEFF] = 9, [0x41] = 3 };
        var substitutions = new List<(int CodePoint, int Value)>();
        var cmap = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(map, new HashSet<int>(), true, substitutions);

        Assert.Single(Regex.Matches(cmap, "<0009> <"));
        Assert.Contains("<0009> <2060>", cmap);
        Assert.Equal([(0xFEFF, 0x2060)], substitutions);
    }

    [Fact]
    public void Issue752_AClaimThatNeedsToUnicodeListsEveryCodePointItSubstitutedOnce()
    {
        var handle = new PdfDocument().UseTrueTypeFont(DejaVu);
        var glyphs = new ushort[4];
        handle.GetGlyphIds("A" + Feff + Feff + "B", glyphs);

        Assert.Equal([(0xFEFF, 0x2060)], handle.Embedder.ToUnicodeSubstitutions(PdfConformance.PdfA2u));
        Assert.Equal([(0xFEFF, 0x2060)], handle.Embedder.ToUnicodeSubstitutions(PdfConformance.PdfUA1));
        Assert.Empty(handle.Embedder.ToUnicodeSubstitutions(PdfConformance.PdfA2b));
        Assert.Empty(handle.Embedder.ToUnicodeSubstitutions(PdfConformance.None));
    }

    [Fact]
    public void Issue752_AClaimWithoutAToUnicodeLevelWritesTheSameBytesAsNoClaimWhateverTheText()
    {
        // 120 generated texts, with and without the three characters. No claim and PDF/A-2b write
        // one CMap; PDF/A-2u, 2a and UA-1 write it too when the text holds no U+FEFF, and differ
        // only in that entry when it does.
        var pool = new[] { "A", "b", " ", "\u00E9", "\u00A0", "\u202F", "\u2007", "\u0000", Feff, "\uFFFE", "\U0001F600" };
        var random = new Random(752);
        var withFeff = 0;
        for (var n = 0; n < 120; n++)
        {
            var length = random.Next(1, 12);
            var text = string.Concat(Enumerable.Range(0, length).Select(_ => pool[random.Next(pool.Length)]));
            var plain = CMapOf(PdfConformance.None, text);
            Assert.Equal(plain, CMapOf(PdfConformance.PdfA2b, text));
            var holdsFeff = text.Contains(Feff, StringComparison.Ordinal);
            if (holdsFeff) withFeff++;
            foreach (var claim in new[] { PdfConformance.PdfA2u, PdfConformance.PdfA2a, PdfConformance.PdfUA1 })
            {
                var claimed = CMapOf(claim, text);
                if (holdsFeff)
                    Assert.Equal(plain.Replace("<14F7> <FEFF>", "<14F7> <2060>", StringComparison.Ordinal), claimed);
                else
                    Assert.Equal(plain, claimed);
            }
        }

        // The generator reaches both cases, so neither branch above is vacuous.
        Assert.InRange(withFeff, 20, 100);

        static string CMapOf(PdfConformance claim, string text)
        {
            using var pdf = new Pdf(Save(KernelDocument(claim, (DejaVu, text))));
            return Encoding.ASCII.GetString(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);
        }
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    [InlineData(PdfConformance.PdfA2b)]
    public void Issue752_APreformattedBlockRefusesExactlyTheCodePointsTheToUnicodeWriterWouldRename(PdfConformance claim)
    {
        // The block refuses what the writer would otherwise have to rename, and both ask
        // ToUnicodeValues. The candidates are the forbidden values and their neighbours; a code
        // point the font has no glyph for is refused for that reason and says nothing about the
        // predicate, so it is left out.
        var lookup = CmapLookup(DejaVu);
        var candidates = new[] { 0x41, 0x2060, 0x200B, 0xFEFE, 0xFEFF, 0xFF00, 0xFFFC, 0xFFFD, 0xFFFE, 0xFFFF };
        var checkedAny = new List<int>();
        foreach (var scalar in candidates.Where(c => lookup(c) > 0))
        {
            var doc = LayoutDocument(claim);
            var style = Style(doc, DejaVu);
            doc.Add(new VellumPdf.Layout.Elements.Preformatted("a" + char.ConvertFromUtf32(scalar), style));

            var expectRefused = ToUnicodeValues.ClaimNeedsToUnicode(claim) && ToUnicodeValues.IsForbidden(scalar);
            if (expectRefused)
                Assert.Throws<InvalidOperationException>(() => Save(doc));
            else
                Assert.NotEmpty(Save(doc));
            checkedAny.Add(scalar);
        }

        Assert.Contains(0xFEFF, checkedAny);
        Assert.Contains(0x41, checkedAny);
    }

    // ── 9.7.4.2 and #629: characters outside the BMP ────────────────────────────────────────

    [Theory]
    [InlineData(Kind.DejaVuAstral, 0x1F600)]
    [InlineData(Kind.DejaVuAstral, 0x1D538)]
    [InlineData(Kind.DejaVuAstral, 0x1D7D8)]
    [InlineData(Kind.SourceSansCff, 0x1F4A9)]
    public void Iso32000_1_9_7_4_2_ACharacterOutsideTheBmpIsShownByItsGlyphAndMapsBackAsASurrogatePair(
        Kind kind, int codePoint)
    {
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.PdfA2u));
        var font = Assert.Single(EmbeddedFonts(pdf));

        // The glyph the font's own cmap (format 12) gives the character is in the file, and its
        // code is not glyph 0. The lookup reads the source font program, not the embedder.
        var expected = CmapLookup(FontBytes(kind))(codePoint);
        Assert.True(expected > 0);
        var codes = ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)).ToList();
        Assert.Contains(expected, codes);
        Assert.DoesNotContain(0, codes);

        // ToUnicode names it as the UTF-16BE surrogate pair (9.10.3 and TN5014: destinations are
        // UTF-16BE), eight hex digits, and reading it back gives the code point.
        var map = ParseToUnicode(font.ToUnicodeBytes);
        var text = char.ConvertFromUtf32(codePoint);
        Assert.Equal(text, map.Single(expected));
        var pair = ((int)text[0]).ToString("X4", CultureInfo.InvariantCulture) + ((int)text[1]).ToString("X4",
            CultureInfo.InvariantCulture);
        Assert.Contains($"<{expected:X4}> <{pair}>", Encoding.ASCII.GetString(font.ToUnicodeBytes));
    }

    [Theory]
    [InlineData(Kind.DejaVuBmp)]
    [InlineData(Kind.DejaVuAstral)]
    [InlineData(Kind.SourceSansCff)]
    public void Iso32000_1_9_7_4_2_EveryShownCodeIsTheGlyphTheFontsCmapGivesItsCharacter(Kind kind)
    {
        using var pdf = new Pdf(KernelFixture(kind, PdfConformance.PdfA2u));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var lookup = CmapLookup(FontBytes(kind));
        var map = ParseToUnicode(font.ToUnicodeBytes);

        var codes = ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)).ToList();
        Assert.NotEmpty(codes);
        foreach (var code in codes)
        {
            var text = map.Single(code);
            var codePoint = char.ConvertToUtf32(text, 0);
            Assert.Equal(code, lookup(codePoint));
        }
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    [InlineData(PdfConformance.PdfUA1)]
    public void Iso19005_2_6_2_11_8_NoGlyphZeroIsShownForACharacterTheFontHas(PdfConformance claim)
    {
        foreach (var kind in Enum.GetValues<Kind>())
        {
            using var pdf = new Pdf(KernelFixture(kind, claim));
            Assert.DoesNotContain(0, ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)));
        }
    }

    // ── 19005-2 6.2.11.8 and 14289-1 7.21.8: glyph 0 (#657, #592) ─────────────────────────────

    private static byte[] MissingGlyphFixture(byte[] font, PdfConformance claim) =>
        Save(KernelDocument(claim, (font, "A\u0378B")));

    [Theory]
    [MemberData(nameof(OracleClaimData))]
    public void Iso19005_2_6_2_11_8_ACharacterTheFontLacksIsWrittenAsGlyphZeroAndToUnicodeNamesItReplacement(
        PdfConformance claim)
    {
        var bytes = MissingGlyphFixture(DejaVu, claim);
        using var pdf = new Pdf(bytes);
        var font = Assert.Single(EmbeddedFonts(pdf));

        // The construct: the content shows code 0000 between the codes of A and B.
        var codes = ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)).ToList();
        Assert.Equal(3, codes.Count);
        Assert.Equal(0, codes[1]);

        // #592: ToUnicode gives glyph 0 the one entry U+FFFD, which meets the value rule of 6.2.11.7.2
        // and 7.21.7, and no character is listed against code 0 by its own number.
        var map = ParseToUnicode(font.ToUnicodeBytes);
        Assert.Equal("\uFFFD", map.Single(0));
        Assert.DoesNotContain(map.Entries, e => e.Value.Contains("\u0378"));

        // 6.2.11.8-1 and 7.21.8-1 are the rules veraPDF has for this, and it fails them on a TrueType font.
        var failed = VeraPdfFailures(Flavour(claim), bytes);
        Assert.Contains(claim == PdfConformance.PdfUA1 ? "7.21.8-1" : "6.2.11.8-1", failed);
        Assert.DoesNotContain(failed, f => f is "6.2.11.7.2-2" or "7.21.7-2");
    }

    [Fact]
    public void Iso19005_2_6_2_11_8_VeraPdfDoesNotSeeGlyphZeroInAnOpenTypeCffFont()
    {
        // The same defect in the CFF font passes veraPDF 2u, which is why this audit asserts the
        // codes in the content stream and does not take the oracle's word. Source Sans 3 has no
        // glyph for U+1F600.
        var bytes = Save(KernelDocument(PdfConformance.PdfA2u, (SourceSans, "A\U0001F600B")));
        using var pdf = new Pdf(bytes);
        var codes = ShownStrings(Content(pdf)).SelectMany(s => Codes(s.Codes)).ToList();
        Assert.Equal(0, codes[1]);

        Assert.Empty(VeraPdfFailures("2u", bytes));
    }

    [Theory(Skip = "Gap #474: a PDF/A or PDF/UA claim is stamped on a document that shows glyph 0. The save " +
        "reports it (PdfDocument.NotdefGlyphs, Layout NotdefGlyph) and writes the file instead of refusing it.")]
    [MemberData(nameof(OracleClaimData))]
    public void Iso19005_2_6_2_11_8_ACharacterTheFontLacksIsRefusedUnderAClaim(PdfConformance claim)
    {
        Assert.Throws<InvalidOperationException>(() => MissingGlyphFixture(DejaVu, claim));
    }

    // ── 19005-2 6.2.11.7.3 and 14289-1 7.2: the private use area ────────────────────────────

    [Theory]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfUA1)]
    public void Iso14289_1_7_2_APrivateUseCharacterMapsToItsOwnCodePoint(PdfConformance claim)
    {
        // 7.2 allows the Unicode private use area for characters Unicode does not have, and ToUnicode
        // is how that mapping is carried. DejaVu Sans has a glyph for U+F000 and for U+F6C5.
        var bytes = Save(KernelDocument(claim, (DejaVu, "A\uF000B\uF6C5")));
        using var pdf = new Pdf(bytes);
        var map = ParseToUnicode(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);
        var lookup = CmapLookup(DejaVu);

        Assert.Equal("\uF000", map.Single(lookup(0xF000)));
        Assert.Equal("\uF6C5", map.Single(lookup(0xF6C5)));
        Assert.Empty(VeraPdfFailures(Flavour(claim), bytes));
    }

    [Fact]
    public void Iso19005_2_6_2_11_7_3_VeraPdfFailsAPrivateUseCharacterWithoutActualTextUnderLevelAOnly()
    {
        Assert.Contains("6.2.11.7.3-1", VeraPdfFailures("2a", PrivateUse(PdfConformance.PdfA2a)));
        Assert.Empty(VeraPdfFailures("2u", PrivateUse(PdfConformance.PdfA2u)));
    }

    private static byte[] PrivateUse(PdfConformance claim)
    {
        var doc = LayoutDocument(claim);
        doc.Add(new VellumPdf.Layout.Elements.Paragraph("A\uF000B", Style(doc, DejaVu)));
        return Save(doc);
    }

    [Fact(Skip = "Gap #602: neither the Kernel nor Layout writes /ActualText, so a character in the private " +
        "use area has none under PDF/A-2a (veraPDF 6.2.11.7.3-1 fails); 6.2.11.7.3 requires one.")]
    public void Iso19005_2_6_2_11_7_3_APrivateUseCharacterHasActualTextUnderLevelA()
    {
        using var pdf = new Pdf(PrivateUse(PdfConformance.PdfA2a));

        // The marked-content sequence that shows the private use character carries /ActualText in its
        // property list, or its MCID is the content of a structure element that carries it.
        var actual = ActualTextOfSequenceShowing(pdf, 0xF000);
        Assert.False(string.IsNullOrEmpty(actual));
    }

    // ── 19005-2 6.2.11.4.1 and 14289-1 7.21.4.1: embedding, Standard 14 and the Liberation program ──

    private static byte[] Standard14Fixture(PdfConformance claim)
    {
        var doc = new PdfDocument
        {
            Conformance = claim,
            Timestamp = Pinned,
            DocumentId = PinnedId,
            Language = "en-US"
        };
        doc.Info.Title = "G2 audit";
        var page = doc.AddPage();
        var canvas = new VellumPdf.Canvas.PdfCanvas(page).BeginArtifactMarkedContent().BeginText();
        var y = 750.0;
        foreach (var face in Enum.GetValues<Standard14>())
        {
            canvas.SetFont(doc.UseFont(face), 10).SetTextMatrix(1, 0, 0, 1, 72, y).ShowText("Hello");
            y -= 14;
        }

        canvas.EndText().EndMarkedContent().Finish();
        return Save(doc);
    }

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Iso19005_2_6_2_11_4_1_EveryStandard14FaceIsWrittenWithoutAFontProgramAndTheKernelDoesNotRefuseIt(
        PdfConformance claim)
    {
        using var pdf = new Pdf(Standard14Fixture(claim));
        var fonts = SimpleFonts(pdf);

        // The construct: fourteen Type1 dictionaries named for the faces, with no descriptor and no
        // font program, and no ToUnicode, as 9.6.2.2 allows for the standard 14.
        Assert.Equal(14, fonts.Count);
        Assert.Empty(EmbeddedFonts(pdf));
        Assert.All(fonts, f =>
        {
            Assert.Equal("Type1", Assert.IsType<PdfName>(f.Font.Get(PdfName.Subtype)).Value);
            Assert.Null(f.Font.Get(N("FontDescriptor")));
            Assert.Null(f.Font.Get(N("ToUnicode")));
            // Table 111: FirstChar, LastChar, Widths and FontDescriptor are all present or all absent.
            Assert.All(new[] { "FirstChar", "LastChar", "Widths" }, k => Assert.Null(f.Font.Get(N(k))));
        });
        Assert.Equal(
            Enum.GetValues<Standard14>().Length,
            fonts.Select(f => BaseFont(f.Font)).Distinct().Count());

        // The Kernel saves under every claim. 6.2.11.4.1 and 7.21.4.1 (whose note 5 says the standard
        // 14 are not exempt) are what veraPDF reports for each of the fourteen.
        if (claim is PdfConformance.PdfA2b or PdfConformance.PdfA2u or PdfConformance.PdfA2a)
            Assert.Contains("6.2.11.4.1-1", VeraPdfFailures(Flavour(claim), pdf.Bytes));
        else if (claim == PdfConformance.PdfUA1)
            Assert.Contains("7.21.4.1-1", VeraPdfFailures("ua1", pdf.Bytes));

        // The twelve WinAnsi faces are exempt from 7.21.7 by its first condition, and Symbol is by the
        // second, read literally (its glyph names are in the Symbol set of Annex D.5). veraPDF's rule
        // tests only whether a ToUnicode entry exists, so it fails Symbol and ZapfDingbats, which have
        // none, with 6.2.11.7.2-1 (2u, 2a) and 7.21.7-1 (UA-1), in the class of the embedding failure.
        if (claim is PdfConformance.PdfA2u or PdfConformance.PdfA2a)
            Assert.Contains("6.2.11.7.2-1", VeraPdfFailures(Flavour(claim), pdf.Bytes));
        else if (claim == PdfConformance.PdfUA1)
            Assert.Contains("7.21.7-1", VeraPdfFailures("ua1", pdf.Bytes));
    }

    [Theory(Skip = "Gap #711 and #474: a Standard 14 font is written unembedded under a PDF/A or PDF/UA claim, " +
        "in the Kernel without a report and in Layout as an Error diagnostic that does not stop the " +
        "save, so no embedded face and no refusal.")]
    [MemberData(nameof(OracleClaimData))]
    public void Iso19005_2_6_2_11_4_1_AStandard14FontIsEmbeddedOrRefusedUnderAClaim(PdfConformance claim)
    {
        using var pdf = new Pdf(Standard14Fixture(claim));
        Assert.Empty(SimpleFonts(pdf));
    }

    private static readonly Standard14[] LiberationFaces =
    [
        Standard14.Helvetica, Standard14.HelveticaBold, Standard14.HelveticaOblique, Standard14.HelveticaBoldOblique,
        Standard14.TimesRoman, Standard14.TimesBold, Standard14.TimesItalic, Standard14.TimesBoldItalic,
        Standard14.Courier, Standard14.CourierBold, Standard14.CourierOblique, Standard14.CourierBoldOblique,
    ];

    private static byte[] LiberationFixture(PdfConformance claim)
    {
        var doc = LayoutDocument(claim);
        foreach (var face in LiberationFaces)
        {
            var style = Style(doc, Standard14Substitutes.GetFontProgram(face), 10);
            doc.Add(new VellumPdf.Layout.Elements.Paragraph($"{face} Hello \u00E9 \u20AC", style));
        }

        return Save(doc);
    }

    [Theory]
    [MemberData(nameof(OracleClaimData))]
    public void Iso19005_2_6_2_11_4_1_TheTwelveLiberationProgramsEmbedAndPassTheOracleForEachClaim(PdfConformance claim)
    {
        var bytes = LiberationFixture(claim);
        using var pdf = new Pdf(bytes);
        var fonts = EmbeddedFonts(pdf);

        // The construct: twelve embedded subsets, one per face, each a CIDFontType2 with a program,
        // and no Standard 14 dictionary beside them.
        Assert.Equal(12, fonts.Count);
        Assert.Empty(SimpleFonts(pdf));
        Assert.All(fonts, f => Assert.Matches("^[A-Z]{6}\\+Liberation(Sans|Serif|Mono)(-[A-Za-z]+)?$",
            BaseFont(f.Type0)));
        Assert.Equal(12, fonts.Select(f => BaseFont(f.Type0)).Distinct().Count());
        Assert.All(fonts, f => Assert.NotNull(f.Descriptor.Get(N("FontFile2"))));

        // Each program's own text reads back, so the faces were not mixed up.
        var shown = ShownStrings(Content(pdf));
        var byResource = fonts.ToDictionary(f => f.ResourceName);
        Assert.Equal(12, shown.Count(s => Decode(s, byResource[s.Resource]).Contains("Hello",
            StringComparison.Ordinal)));

        Assert.Empty(VeraPdfFailures(Flavour(claim), bytes));
        QpdfChecks(bytes);
    }

    [Fact]
    public void Iso19005_2_6_2_11_4_1_ASymbolOrZapfDingbatsSubstituteIsNotBundledAndTheCallThrows()
    {
        // #708 and R4: the new call refuses what it cannot substitute, with NotSupportedException.
        Assert.Throws<NotSupportedException>(() => Standard14Substitutes.GetFontProgram(Standard14.Symbol));
        Assert.Throws<NotSupportedException>(() => Standard14Substitutes.GetFontProgram(Standard14.ZapfDingbats));
        Assert.Throws<NotSupportedException>(() => Standard14Substitutes.GetFontProgram((Standard14)99));
    }

    // ── 14289-1 7.21.4.1: a font that may be embedded ───────────────────────────────────────

    [Fact]
    public void Iso14289_1_7_21_4_1_EveryFontProgramInTheTreeAllowsEmbeddingAndThePermissionIsReadFromOs2()
    {
        // fsType 0 is "installable embedding" (OpenType OS/2). The fixtures here use only such fonts;
        // that is what makes the oracle runs above legal, and what the gap below is measured against.
        Assert.Equal(0, FsType(DejaVu) & 0xF);
        Assert.Equal(0, FsType(SourceSans) & 0xF);
        foreach (var face in LiberationFaces)
            Assert.Equal(0, FsType(Standard14Substitutes.GetFontProgram(face)) & 0xF);
    }

    private static byte[] RestrictedDejaVu()
    {
        // OS/2.fsType is at byte 8; 2 is "restricted license embedding" (OpenType OS/2): the font
        // must not be embedded. Only that field changes; the table directory keeps its checksum
        // here because the field is rebuilt through the test helper.
        var os2 = EmbeddedFontFixtures.Table(DejaVu, "OS/2");
        os2[8] = 0;
        os2[9] = 2;
        return EmbeddedFontFixtures.WithTables(DejaVu, add: new Dictionary<string, byte[]> { ["OS/2"] = os2 });
    }

    [Fact]
    public void Iso14289_1_7_21_4_1_AFontWhoseFsTypeRestrictsEmbeddingIsEmbeddedAnywayToday()
    {
        // The state #267 describes: the permission is parsed (Os2Table.FsType) and not enforced, as
        // the package READMEs say ("currently parses and ignores", #267).
        var font = RestrictedDejaVu();
        Assert.Equal(2, FsType(font) & 0xF);
        var bytes = Save(KernelDocument(PdfConformance.PdfUA1, (font, "Hello")));
        using var pdf = new Pdf(bytes);
        Assert.Single(EmbeddedFonts(pdf));
    }

    [Fact(Skip = "Gap #267: the OS/2 fsType permission is parsed and not enforced (package READMEs: 'enforcing" +
        " the fsType embedding permission the library currently parses and ignores')." +
        " A font whose fsType forbids embedding is embedded under PDF/UA-1, which 7.21.4.1 allows " +
        "only for fonts embeddable without restriction. The test expects InvalidOperationException, " +
        "as the other save-time refusals use.")]
    public void Iso14289_1_7_21_4_1_AFontWhoseFsTypeRestrictsEmbeddingIsRefusedUnderAClaim()
    {
        Assert.Throws<InvalidOperationException>(() => Save(KernelDocument(PdfConformance.PdfUA1,
            (RestrictedDejaVu(), "Hello"))));
    }

    // ── The oracles for the Kernel text fixtures ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(OracleClaimData))]
    public void Iso19005_2_TheKernelFontFixturesAreAcceptedByVeraPdfAndQpdf(PdfConformance claim)
    {
        foreach (var kind in new[] { Kind.DejaVuBmp, Kind.DejaVuAstral, Kind.DejaVuMany, Kind.SourceSansCff })
        {
            var bytes = KernelFixture(kind, claim);
            Assert.Empty(VeraPdfFailures(Flavour(claim), bytes));
            QpdfChecks(bytes);
        }
    }

    [Theory]
    [InlineData(Kind.DejaVuAstral, "\U0001F600")]
    [InlineData(Kind.DejaVuBmp, "w\u00F6rld")]
    [InlineData(Kind.SourceSansCff, "\U0001F4A9")]
    public void Iso32000_1_9_10_2_PdftotextReadsBackTheCharactersThroughToUnicode(Kind kind, string expected)
    {
        var text = PopplerText(KernelFixture(kind, PdfConformance.PdfA2u));
        Assert.Contains(expected, text, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ClaimData))]
    public void Iso32000_2_9_TheFontObjectsOfAFileWithoutAClaimAreThoseOfAPdf20File(PdfConformance claim)
    {
        // Under no claim the file is written as PDF 2.0 (G5), and 32000-2 9.7 to 9.10 are the same
        // keys and rules as the clauses above: the whole class runs under None in the theories.
        var bytes = KernelFixture(Kind.DejaVuBmp, claim);
        var header = Encoding.ASCII.GetString(bytes, 0, 8);
        Assert.Equal(claim == PdfConformance.None ? "%PDF-2.0" : "%PDF-1.7", header);
        using var pdf = new Pdf(bytes);
        Assert.Single(EmbeddedFonts(pdf));
    }

    private static string AllText(Pdf pdf) => string.Join("", ShownText(pdf));

    [Fact]
    public void Iso32000_1_9_10_2_TheTextShownIsTheTextDrawnForEveryKind()
    {
        foreach (var kind in Enum.GetValues<Kind>())
        {
            using var pdf = new Pdf(KernelFixture(kind, PdfConformance.PdfA2u));
            Assert.Equal(string.Concat(Lines(kind).Select(l => l.Text)), AllText(pdf));
        }
    }

    // ── Further clause tests ─────────────────────────────────────────────────────────────────

    private static List<string> FaceNames(PdfConformance claim, params Standard14[] faces)
    {
        using var doc = new PdfDocument
        {
            Conformance = claim,
            Timestamp = Pinned,
            DocumentId = PinnedId,
            Language = "en-US"
        };
        doc.Info.Title = "G2 audit";
        var page = doc.AddPage();
        var canvas = new VellumPdf.Canvas.PdfCanvas(page).BeginArtifactMarkedContent().BeginText();
        var y = 700.0;
        foreach (var face in faces)
        {
            var handle = doc.UseTrueTypeFont(Standard14Substitutes.GetFontProgram(face));
            doc.RegisterEmbeddedFontUsage(page, handle);
            var glyphs = new ushort[5];
            handle.GetGlyphIds("Hello", glyphs);
            canvas.SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, y).ShowGlyphs(glyphs);
            y -= 20;
        }

        canvas.EndText().EndMarkedContent().Finish();
        using var pdf = new Pdf(Save(doc));
        return [.. EmbeddedFonts(pdf).Select(f => BaseFont(f.Type0))];
    }

    private static readonly Standard14[] FourFaces =
        [Standard14.Helvetica, Standard14.HelveticaBold, Standard14.TimesRoman, Standard14.CourierBold];

    [Fact]
    public void Iso32000_1_9_6_4_FourFacesDrawingTheSameTextShareOneTagUnderAPdfAClaim()
    {
        // The state #754 describes, measured in a file written as PDF 1.7, which 32000-1 governs:
        // Liberation Sans, Sans Bold, Serif and Mono Bold drawing "Hello" share one tag and differ
        // after the plus sign.
        var names = FaceNames(PdfConformance.PdfA2b, FourFaces);

        Assert.Equal(4, names.Count);
        Assert.Single(names.Select(n => n[..6]).Distinct());
        Assert.Equal(4, names.Distinct().Count());
    }

    [Fact(Skip = "Gap #754: the subset tag hashes the glyph ids alone, so two different fonts that show the " +
        "same glyph ids share a tag. ISO 32000-1 9.6.4 says different subsets in one file have " +
        "different tags; ISO 32000-2 9.9.2 narrows that to subsets of the same font.")]
    public void Iso32000_1_9_6_4_TwoSubsetsOfDifferentFontsHaveDifferentTags()
    {
        // Saved under a PDF/A-2 claim, a PDF 1.7 file, which is the edition whose sentence is read.
        var names = FaceNames(PdfConformance.PdfA2b, FourFaces);

        Assert.Equal(4, names.Count);
        Assert.Equal(4, names.Select(n => n[..6]).Distinct().Count());
    }

    [Fact]
    public void Iso32000_1_9_7_4_1_ACffCidFontIsNamedLikeItsProgram()
    {
        // Table 117: for a Type 0 CIDFont the BaseFont is the CIDFontName entry of the program. The
        // program's own name is read here from the first entry of the Name INDEX of its CFF table.
        using var pdf = new Pdf(KernelFixture(Kind.SourceSansCff, PdfConformance.PdfA2u));
        var font = Assert.Single(EmbeddedFonts(pdf));
        var cff = Sfnt(font.ProgramBytes).GetTableBytes(new Tag("CFF ")).ToArray();
        var header = cff[2];
        var count = (cff[header] << 8) | cff[header + 1];
        var offSize = cff[header + 2];
        int Offset(int i)
        {
            var v = 0;
            for (var k = 0; k < offSize; k++)
                v = (v << 8) | cff[header + 3 + (offSize * i) + k];
            return v;
        }

        Assert.Equal(1, count);
        var dataStart = header + 3 + (offSize * (count + 1)) - 1;
        var name = Encoding.Latin1.GetString(cff, dataStart + Offset(0), Offset(1) - Offset(0));

        Assert.Equal(BaseFont(font.CidFont), name);
    }

    [Fact]
    public void Iso32000_1_AnnexD_Standard14TextIsEncodedWithWinAnsi()
    {
        // Annex D, table D.2, the WIN column in octal: the characters whose code is not their Latin-1
        // code point, and one that is.
        (int CodePoint, string Octal)[] table =
        [
            (0xE9, "351"), (0x20AC, "200"), (0x2018, "221"), (0x2019, "222"), (0x201C, "223"), (0x201D, "224"),
            (0x2022, "225"), (0x2013, "226"), (0x2014, "227"), (0x178, "237"), (0x152, "214"), (0x2122, "231"),
                (0x2026, "205"),
        ];
        var text = string.Concat(table.Select(r => char.ConvertFromUtf32(r.CodePoint)));
        using var doc = new PdfDocument { Timestamp = Pinned, DocumentId = PinnedId };
        var page = doc.AddPage();
        new VellumPdf.Canvas.PdfCanvas(page).BeginText().SetFont(doc.UseFont(Standard14.Helvetica), 12)
            .SetTextMatrix(1, 0, 0, 1, 72, 700).ShowText(text).EndText().Finish();
        using var pdf = new Pdf(Save(doc));

        var font = Assert.Single(SimpleFonts(pdf)).Font;
        Assert.Equal("WinAnsiEncoding", Assert.IsType<PdfName>(font.Get(N("Encoding"))).Value);
        var shown = Assert.Single(Operators(pdf.Page(0)), o => o.Name == "Tj");
        Assert.Equal(table.Select(r => (byte)Convert.ToInt32(r.Octal, 8)).ToArray(), StringBytes(shown.Operands[0]));
    }

    [Fact]
    public void Iso32000_1_9_10_3_TwoCharactersThatShareAGlyphGiveItsCodeTwoEntriesInTheOrderOfFirstUse()
    {
        // Source Sans 3 draws U+0020 and U+00A0 with glyph 1. Both entries are written for that code, in
        // the order the characters were first drawn. TN5014 lets a later code mapping supersede an
        // earlier one, but a measured reader (poppler 26.08) returned U+0020 for both orders, so readers
        // disagree on which entry counts. Pinned as the measured state; no clause forbids it.
        var nbsp = char.ConvertFromUtf32(0xA0);
        var bytes = Save(KernelDocument(PdfConformance.PdfA2u, (SourceSans, "A B" + nbsp + "C")));
        using var pdf = new Pdf(bytes);
        var map = ParseToUnicode(Assert.Single(EmbeddedFonts(pdf)).ToUnicodeBytes);
        var lookup = CmapLookup(SourceSans);

        Assert.Equal(lookup(0x20), lookup(0xA0));
        Assert.Equal([" ", nbsp], map.Entries[lookup(0x20)]);
        Assert.Empty(VeraPdfFailures("2u", bytes));
    }

    // ── Table 122 and the Liberation programs ────────────────────────────────────────────────

    private static readonly Standard14[] SlopedFaces =
    [
        Standard14.HelveticaOblique, Standard14.HelveticaBoldOblique, Standard14.TimesItalic,
        Standard14.TimesBoldItalic, Standard14.CourierOblique, Standard14.CourierBoldOblique,
    ];

    public static TheoryData<Standard14> LiberationFaceData()
    {
        var data = new TheoryData<Standard14>();
        foreach (var f in LiberationFaces)
            data.Add(f);
        return data;
    }

    private static (EmbeddedFont Font, byte[] Source) LiberationDescriptor(Standard14 face)
    {
        var source = Standard14Substitutes.GetFontProgram(face);
        using var pdf = new Pdf(Save(KernelDocument(PdfConformance.None, (source, "Hi"))));
        return (Assert.Single(EmbeddedFonts(pdf)), source);
    }

    [Theory]
    [MemberData(nameof(LiberationFaceData))]
    public void Iso32000_1_9_8_ItalicAngleIsNegativeForAFaceThatSlopesToTheRight(Standard14 face)
    {
        // Table 122: ItalicAngle is in degrees counterclockwise from the vertical, negative for a font that
        // slopes to the right. The program's own post table gives the angle (Fixed, 16.16) and decides
        // which faces slope.
        var (font, source) = LiberationDescriptor(face);
        var post = Sfnt(source).GetTableBytes(new Tag("post")).ToArray();
        var angle = ((post[4] << 24) | (post[5] << 16) | (post[6] << 8) | post[7]) / 65536.0;
        var written = Num(font.Descriptor.Get(N("ItalicAngle")));

        Assert.Equal(SlopedFaces.Contains(face), angle < 0);
        Assert.Equal(angle, written, 2);
        if (angle < 0)
            Assert.True(written < 0);
        else
            Assert.Equal(0, written);
    }

    [Theory]
    [MemberData(nameof(LiberationFaceData))]
    public void Iso32000_1_9_8_FontBBoxOfALiberationProgramIsItsHeadBoxRoundedOutward(Standard14 face)
    {
        // The twelve programs are the ones whose lower edges are not whole numbers at 1000 units per em,
        // so rounding to nearest would pull an edge inside the outline.
        var (font, source) = LiberationDescriptor(face);
        var (xMin, yMin, xMax, yMax, upem) = HeadBox(source);
        double Exact(int v) => v * 1000.0 / upem;
        var box = font.Descriptor.Get(N("FontBBox"));
        var written = Assert.IsType<PdfArray>(box).Items().Select(Num).ToArray();

        Assert.Equal([Math.Floor(Exact(xMin)), Math.Floor(Exact(yMin)), Math.Ceiling(Exact(xMax)),
            Math.Ceiling(Exact(yMax))], written);
    }
}
