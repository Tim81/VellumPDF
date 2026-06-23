// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.6 testNumbers 1, 2, and 4 — TrueType cmap and encoding requirements.
/// <list type="bullet">
///   <item><strong>7.21.6-1</strong> (TrueTypeFontProgram): For non-symbolic TrueType fonts used
///   for rendering, the embedded program shall contain at least one non-symbolic cmap entry (i.e.
///   at least one subtable when no Symbol (3,0) subtable is present, or more than one when the
///   Symbol subtable is present).</item>
///   <item><strong>7.21.6-2</strong> (PDTrueTypeFont): Non-symbolic TrueType fonts shall use
///   MacRomanEncoding or WinAnsiEncoding; if a /Differences array is defined, all glyph names
///   shall be in the Adobe Glyph List and the embedded program shall contain at least the
///   Microsoft Unicode (3,1) cmap encoding.</item>
///   <item><strong>7.21.6-4</strong> (TrueTypeFontProgram): The embedded program of a symbolic
///   TrueType font shall contain exactly one cmap subtable, or shall contain at least the
///   Microsoft Symbol (3,0) cmap subtable.</item>
/// </list>
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.6 and cross-validated against veraPDF 1.30.2.
/// veraPDF predicates (verbatim from PDFUA-1.xml):
/// <list type="bullet">
///   <item>7.21.6-1 (TrueTypeFontProgram): <c>isSymbolic == true || (cmap30Present == true ? nrCmaps &gt; 1 : nrCmaps &gt; 0)</c></item>
///   <item>7.21.6-2 (PDTrueTypeFont): <c>isSymbolic == true || ((Encoding == "MacRomanEncoding" || Encoding == "WinAnsiEncoding") &amp;&amp; (containsDifferences == false || differencesAreUnicodeCompliant == true))</c></item>
///   <item>7.21.6-4 (TrueTypeFontProgram): <c>isSymbolic == false || nrCmaps == 1 || cmap30Present == true</c></item>
/// </list>
/// <para>
/// Key empirical findings (probed against veraPDF 1.30.2):
/// </para>
/// <list type="bullet">
///   <item><c>differencesAreUnicodeCompliant</c> requires ALL /Differences glyph names to be
///   verbatim entries in the Adobe Glyph List (raw table lookup only — <c>uniXXXX</c>, <c>u0041</c>,
///   and period-suffix forms such as <c>Alpha.alt</c> all fail) AND the embedded program must
///   contain a Microsoft Unicode (3,1) cmap subtable.</item>
///   <item>7.21.6-2 fires on USED fonts only (font not selected via Tf → silent).</item>
///   <item>When <c>containsDifferences == false</c> (no /Differences array), 7.21.6-2 does NOT
///   check AGL names or the (3,1) cmap; the encoding-name check still applies.</item>
///   <item>7.21.6-4 fires when the embedded cmap has more than one subtable and none is (3,0).
///   It does NOT fire when the program is absent (FP-safe direction).</item>
/// </list>
/// <para>
/// Symbolic status is determined from /FontDescriptor /Flags: the Symbolic bit (bit 3, value 4)
/// must be set AND the NonSymbolic bit (bit 6, value 32) must be clear. Ambiguous fonts (both
/// bits set) are not checked (FP-safe).
/// </para>
/// <para>
/// Only fonts actually selected via <c>Tf</c> in page content are evaluated.
/// </para>
/// </remarks>
internal sealed class UaTrueTypeCmapRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.6-1";

    public string Clause => "ISO 14289-1:2014, 7.21.6";

    private const string RuleId1 = "ISO14289-1:7.21.6-1";
    private const string RuleId2 = "ISO14289-1:7.21.6-2";
    private const string RuleId4 = "ISO14289-1:7.21.6-4";

    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _flags = new("Flags");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _baseEncoding = new("BaseEncoding");
    private static readonly PdfName _differences = new("Differences");
    private static readonly PdfName _fontFile2 = new("FontFile2");

    // ISO 32000-1 Table 121: bit 3 (1-indexed) = bit value 4 = Symbolic; bit 6 = value 32 = NonSymbolic.
    private const int SymbolicFlag = 1 << 2;
    private const int NonSymbolicFlag = 1 << 5;

    private static readonly HashSet<string> _validBaseEncodings = new(StringComparer.Ordinal)
    {
        "MacRomanEncoding", "WinAnsiEncoding",
    };

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
        {
            if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "TrueType")
                continue;

            var isSymbolic = DetermineSymbolic(context, font);
            if (isSymbolic is null)
                continue; // ambiguous or no descriptor — skip, FP-safe

            if (isSymbolic.Value)
            {
                // §7.21.6-4: symbolic TrueType embedded program must have exactly 1 cmap subtable
                // or include the Microsoft Symbol (3,0) encoding.
                if (EmbeddedProgram(context, font) is { } prog4)
                {
                    if (prog4.CmapSubtableCount != 1 && !prog4.HasSymbolCmap)
                    {
                        var name4 = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
                        context.Report(
                            RuleId4, Clause, PreflightSeverity.Error,
                            $"The embedded font program for {(name4 is null ? "a symbolic TrueType font" : $"the symbolic TrueType font /{name4}")} "
                            + $"contains {prog4.CmapSubtableCount} cmap subtable(s) and does not contain "
                            + "the Microsoft Symbol (3,0) encoding. PDF/UA-1 §7.21.6 requires the program "
                            + "to have exactly one subtable or include the (3,0) Symbol encoding.");
                    }
                }
            }
            else
            {
                // Non-symbolic: §7.21.6-1 and §7.21.6-2.

                // §7.21.6-1: embedded program's cmap must provide non-symbolic subtables.
                if (EmbeddedProgram(context, font) is { } prog1)
                {
                    var needsMoreThanOne = prog1.HasSymbolCmap;
                    if (needsMoreThanOne ? prog1.CmapSubtableCount <= 1 : prog1.CmapSubtableCount <= 0)
                    {
                        var name1 = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
                        context.Report(
                            RuleId1, Clause, PreflightSeverity.Error,
                            $"The embedded font program for {(name1 is null ? "a non-symbolic TrueType font" : $"the non-symbolic TrueType font /{name1}")} "
                            + "does not contain non-symbolic cmap entries required for glyph lookups "
                            + "(§7.21.6). The cmap must contain at least one non-symbol-only subtable.");
                    }
                }

                // §7.21.6-2: encoding must be MacRoman/WinAnsi; if Differences present, all names
                // must be in the AGL AND the embedded program must have a Microsoft Unicode (3,1) cmap.
                CheckNonSymbolicEncoding(context, font);
            }
        }
    }

    private void CheckNonSymbolicEncoding(PreflightContext context, PdfDictionary font)
    {
        var encodingObj = context.Resolve(font.Get(_encoding));

        // Determine the base encoding name.
        var encodingName = encodingObj switch
        {
            PdfName n => n.Value,
            PdfDictionary d => (context.Resolve(d.Get(_baseEncoding)) as PdfName)?.Value,
            _ => null,
        };

        if (encodingName is null || !_validBaseEncodings.Contains(encodingName))
        {
            // Encoding is missing or not MacRoman/WinAnsi — report 7.21.6-2.
            var fontName = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
            context.Report(
                RuleId2, Clause, PreflightSeverity.Error,
                $"A non-symbolic TrueType font{(fontName is null ? "" : $" (/{fontName})")} does not use "
                + "MacRomanEncoding or WinAnsiEncoding as its /Encoding (or /BaseEncoding). "
                + "PDF/UA-1 §7.21.6 requires non-symbolic TrueType fonts to use one of these encodings.");
            return;
        }

        // Encoding name is valid. Check Differences if present.
        if (encodingObj is not PdfDictionary encDict)
            return; // bare name, no Differences possible

        var diffsObj = context.Resolve(encDict.Get(_differences));
        if (diffsObj is not PdfArray diffs || diffs.Count == 0)
            return; // containsDifferences == false → no further check

        // containsDifferences == true: check differencesAreUnicodeCompliant.
        // differencesAreUnicodeCompliant = ALL glyph names in AGL AND embedded program has (3,1) cmap.
        var allInAgl = true;
        string? firstBadName = null;
        for (var i = 0; i < diffs.Count; i++)
        {
            if (context.Resolve(diffs[i]) is PdfName glyphName)
            {
                if (!AdobeGlyphList.Contains(glyphName.Value))
                {
                    allInAgl = false;
                    firstBadName = glyphName.Value;
                    break;
                }
            }
        }

        if (!allInAgl)
        {
            var fontName = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
            context.Report(
                RuleId2, Clause, PreflightSeverity.Error,
                $"A non-symbolic TrueType font{(fontName is null ? "" : $" (/{fontName})")} has a "
                + $"/Differences array containing /{firstBadName}, which is not in the Adobe Glyph List. "
                + "PDF/UA-1 §7.21.6 requires all Differences glyph names to be listed in the AGL.");
            return;
        }

        // All AGL names pass. If the font program is embedded, it must have the Microsoft Unicode (3,1) cmap.
        if (EmbeddedProgram(context, font) is { } prog && !prog.HasUnicodeCmap)
        {
            var fontName = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
            context.Report(
                RuleId2, Clause, PreflightSeverity.Error,
                $"A non-symbolic TrueType font{(fontName is null ? "" : $" (/{fontName})")} has a "
                + "/Differences array but the embedded font program does not contain the Microsoft "
                + "Unicode (3,1) cmap encoding. PDF/UA-1 §7.21.6 requires this encoding when "
                + "a Differences array is present.");
        }
    }

    // Returns null when symbolic status cannot be determined (ambiguous flags, missing descriptor).
    private static bool? DetermineSymbolic(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (context.Resolve(descriptor.Get(_flags)) is not PdfInteger flagsObj)
            return null;
        var v = (int)flagsObj.Value;
        var symbolic = (v & SymbolicFlag) != 0;
        var nonSymbolic = (v & NonSymbolicFlag) != 0;
        if (symbolic && nonSymbolic)
            return null; // both set — ambiguous, skip
        if (!symbolic && !nonSymbolic)
            return null; // neither set — cannot determine, skip
        return symbolic;
    }

    // Returns SfntMetrics for the embedded /FontFile2 program, or null when absent/unparseable.
    private static SfntMetrics? EmbeddedProgram(PreflightContext context, PdfDictionary font)
    {
        if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return null;
        if (context.ResolveStream(descriptor.Get(_fontFile2)) is not { } stream)
            return null;
        if (context.DecodeStream(stream) is not { } bytes)
            return null;
        return SfntMetrics.TryParse(bytes);
    }
}
