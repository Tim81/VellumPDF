// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Tests.Oracle;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Tests.Fonts;

/// <summary>
/// <c>GlyphPresenceRule</c> resolves a simple TrueType character through the 16-bit cmap subtable,
/// which is the lookup ISO 32000-2 9.6.5.4 specifies ((3,1), then post), and skips a font whose
/// cmap parse throws or that has no 16-bit subtable. Reading format 12 (#595) therefore changes
/// nothing the rule says: a font whose only Unicode subtable is format 12 is skipped, as it was on
/// 2.3.3 when its parse threw, and a code point only format 12 maps is not looked up. These tests
/// pin that.
///
/// Each fixture is the oracle corpus's simple-font PDF (one character, 'A', drawn with WinAnsi
/// encoding and a /Widths entry measured from the unmodified DejaVu Sans) with the embedded program
/// swapped for DejaVu Sans carrying a rewritten cmap.
/// </summary>
public sealed class CmapFormat12PreflightTests
{
    private const string GlyphPresent = "ISO19005-2:6.2.11.4.1-glyph-present";
    private const string GlyphWidth = "ISO19005-2:6.2.11.5-glyph-width";

    /// <summary>The program's own format 12 subtable, glyphs present: the rule skips the font and
    /// finds nothing.</summary>
    [Fact]
    public void FormatTwelveOnlyProgram_withTheGlyphPresent_raisesNoGlyphFinding()
    {
        var ids = RuleIds(FormatTwelveOnly(font => font));

        Assert.DoesNotContain(GlyphPresent, ids);
        Assert.DoesNotContain(GlyphWidth, ids);
    }

    /// <summary>
    /// The format 12 subtable sends 'A' to glyph 60000, past the program's glyph count. That makes
    /// the subtable malformed (a glyph ID at or beyond numGlyphs), the font is refused as a
    /// format-12-only font, and the rule skips it as it skipped every such font on 2.3.3: no
    /// finding on either build. The rule is not asked to judge a program the cmap parser refuses.
    /// </summary>
    [Fact]
    public void FormatTwelveOnlyProgram_mappingTheCodePointPastTheGlyphCount_isSkippedAsMalformed()
    {
        var bytes = FormatTwelveOnly(font =>
        {
            var format12 = CmapFormat12Tests.Subtables(font).First(s => s.Format == 12 && s.Platform == 3);
            var shifted = CmapFormat12Tests.ShiftGroupContaining(format12, 'A', 60000 - 36);
            return CmapFormat12Tests.WithCmap(font, [shifted]);
        });

        Assert.DoesNotContain(GlyphPresent, RuleIds(bytes));
    }

    /// <summary>
    /// A program whose format 12 subtable has no entry for the code point the content draws: the
    /// rule's own comment says it cannot determine a glyph and skips it, so there is no finding.
    /// Included because "a code point the font lacks" reads as something that would be reported,
    /// and the rule does not.
    /// </summary>
    [Fact]
    public void FormatTwelveOnlyProgram_withoutTheCodePoint_raisesNoGlyphFinding()
    {
        var ids = RuleIds(FormatTwelveOnlyKeeping(0x42));

        Assert.DoesNotContain(GlyphPresent, ids);
    }

    /// <summary>
    /// ISO 32000-2 9.6.5.4 resolves a non-symbolic TrueType character through the (3,1) cmap, then
    /// the post table. A font with a 16-bit subtable that lacks the code point and a format 12
    /// subtable that maps it was skipped on 2.3.3 (the 16-bit subtable was the only one read), and
    /// the rule keeps reading only the 16-bit subtable for such a font: it would otherwise judge
    /// the declared width against a glyph the specified lookup never reaches. Here 'A' is in format
    /// 12 only, mapped to a glyph whose advance differs from the /Widths entry, and the rule says
    /// nothing, as it did on 2.3.3.
    /// </summary>
    [Fact]
    public void FontWithAFormat12SubtableBesideA16BitOneThatLacksTheCodePoint_isCheckedAgainstTheSixteenBitSubtableOnly()
    {
        var font = CmapFormat12Tests.LoadAsset("DejaVuSans.ttf");
        var format12 = CmapFormat12Tests.Subtables(font).First(s => s.Format == 12 && s.Platform == 3);
        var shifted = CmapFormat12Tests.ShiftGroupContaining(format12, 'A', 100);   // 'A' draws another glyph
        var format6 = new byte[12];
        format6[1] = 6;
        format6[3] = (byte)format6.Length;
        format6[7] = 0x42;                                                           // firstCode 'B'
        format6[9] = 1;                                                              // one entry
        format6[11] = 37;                                                            // 'B' in DejaVu Sans
        var program = CmapFormat12Tests.WithCmap(
            font, [new CmapFormat12Tests.Subtable(3, 1, 6, format6), shifted]);
        var pdf = OracleCorpus.SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding"), fontProgram: program);

        var ids = RuleIds(pdf);

        Assert.DoesNotContain(GlyphWidth, ids);
        Assert.DoesNotContain(GlyphPresent, ids);
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>Rule ids of every assertion preflight raises on the fixture, for
    /// PDF/A-2b.</summary>
    internal static List<string> RuleIds(byte[] pdf) =>
        PdfPreflight.Validate(pdf, PdfConformance.PdfA2B).Assertions.Select(a => a.RuleId).ToList();

    /// <summary>The corpus's simple-font PDF with the DejaVu program cut to its format 12 subtable,
    /// then transformed.</summary>
    internal static byte[] FormatTwelveOnly(Func<byte[], byte[]> transform)
    {
        var font = CmapFormat12Tests.LoadAsset("DejaVuSans.ttf");
        var twelve = CmapFormat12Tests.WithCmap(
            font, CmapFormat12Tests.Subtables(font).Where(s => s.Platform == 3 && s.Format == 12));
        return OracleCorpus.SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding"), fontProgram: transform(twelve));
    }

    /// <summary>A program whose only subtable is a format 12 one mapping a single code
    /// point.</summary>
    internal static byte[] FormatTwelveOnlyKeeping(int codePoint)
    {
        var font = CmapFormat12Tests.LoadAsset("DejaVuSans.ttf");
        var subtable = new byte[16 + 12];
        subtable[1] = 12;
        PutU32(subtable, 4, (uint)subtable.Length);
        PutU32(subtable, 12, 1);
        PutU32(subtable, 16, (uint)codePoint);
        PutU32(subtable, 20, (uint)codePoint);
        PutU32(subtable, 24, 36);
        var program = CmapFormat12Tests.WithCmap(font, [new CmapFormat12Tests.Subtable(3, 10, 12, subtable)]);
        return OracleCorpus.SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding"), fontProgram: program);
    }

    private static void PutU32(byte[] d, int o, uint v)
    {
        d[o] = (byte)(v >> 24); d[o + 1] = (byte)(v >> 16); d[o + 2] = (byte)(v >> 8); d[o + 3] = (byte)v;
    }
}
