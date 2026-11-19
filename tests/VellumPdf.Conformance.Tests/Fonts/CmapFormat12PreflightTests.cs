// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Tests.Oracle;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Tests.Fonts;

/// <summary>
/// <c>GlyphPresenceRule</c> parses the cmap of a simple TrueType font program with the same
/// <c>CmapTable</c> the writer uses and skips its check when that parse throws. A font whose only
/// Unicode subtable is format 12 threw on 2.3.3, so the rule skipped such a font; it now parses and
/// the rule runs (#595). These tests pin what the rule then says, so the change in what preflight
/// checks is on record rather than implied.
///
/// Each fixture is the oracle corpus's simple-font PDF (one character, 'A', drawn with WinAnsi
/// encoding and a /Widths entry measured from the unmodified DejaVu Sans) with the embedded program
/// swapped for DejaVu Sans carrying a rewritten cmap. The 2.3.3 verdicts for the same three files
/// are in the pull request body and the CHANGELOG, measured by running <c>PdfPreflight</c> from a
/// detached <c>v2.3.3</c> build over these exact bytes.
/// </summary>
public sealed class CmapFormat12PreflightTests
{
    private const string GlyphPresent = "ISO19005-2:6.2.11.4.1-glyph-present";
    private const string GlyphWidth = "ISO19005-2:6.2.11.5-glyph-width";

    /// <summary>The program's own format 12 subtable, glyphs present: the rule runs and finds nothing.</summary>
    [Fact]
    public void FormatTwelveOnlyProgram_withTheGlyphPresent_raisesNoGlyphFinding()
    {
        var ids = RuleIds(FormatTwelveOnly(font => font));

        Assert.DoesNotContain(GlyphPresent, ids);
        Assert.DoesNotContain(GlyphWidth, ids);
    }

    /// <summary>
    /// The format 12 subtable sends 'A' to glyph 60000, past the program's glyph count: the rule
    /// reports the glyph as absent. On 2.3.3 the font did not parse and the rule was skipped.
    /// </summary>
    [Fact]
    public void FormatTwelveOnlyProgram_mappingTheCodePointPastTheGlyphCount_isReported()
    {
        var bytes = FormatTwelveOnly(font =>
        {
            var format12 = CmapFormat12Tests.Subtables(font).First(s => s.Format == 12 && s.Platform == 3);
            var shifted = CmapFormat12Tests.ShiftGroupContaining(format12, 'A', 60000 - 36);
            return CmapFormat12Tests.WithCmap(font, [shifted]);
        });

        Assert.Contains(GlyphPresent, RuleIds(bytes));
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

    // ---------------------------------------------------------------- fixtures

    /// <summary>Rule ids of every assertion preflight raises on the fixture, for PDF/A-2b.</summary>
    internal static List<string> RuleIds(byte[] pdf) =>
        PdfPreflight.Validate(pdf, PdfConformance.PdfA2B).Assertions.Select(a => a.RuleId).ToList();

    /// <summary>The corpus's simple-font PDF with the DejaVu program cut to its format 12 subtable, then transformed.</summary>
    internal static byte[] FormatTwelveOnly(Func<byte[], byte[]> transform)
    {
        var font = CmapFormat12Tests.LoadAsset("DejaVuSans.ttf");
        var twelve = CmapFormat12Tests.WithCmap(
            font, CmapFormat12Tests.Subtables(font).Where(s => s.Platform == 3 && s.Format == 12));
        return OracleCorpus.SimpleTrueTypeFont(_ => { }, encoding: new PdfName("WinAnsiEncoding"), fontProgram: transform(twelve));
    }

    /// <summary>A program whose only subtable is a format 12 one mapping a single code point.</summary>
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
