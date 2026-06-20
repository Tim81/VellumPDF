// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.2 (Font dictionary structure). Every font dictionary in a conforming file —
/// including the descendant CIDFonts of a composite (Type 0) font — shall have a recognised
/// <c>/Subtype</c> (one of Type0, Type1, MMType1, Type3, TrueType, CIDFontType0, CIDFontType2).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.11.2 and ISO 32000-1:2008, 9.6–9.7. Clean-room: derived from
/// the specification text, not from any third-party validation profile. Cross-validated against
/// veraPDF (a real embedded font whose descendant CIDFont subtype is corrupted fails clause
/// 6.2.11.2-2).
/// <para>
/// This slice covers the font Subtype only. The remaining §6.2.11.2 structure requirements
/// (FirstChar/LastChar/Widths consistency, BaseFont presence) and the font-<em>program</em> checks —
/// glyph presence (§6.2.11.4.1), glyph-width consistency (§6.2.11.5), TrueType encoding and cmap
/// (§6.2.11.6) — need malformed <em>real-font</em> fixtures to cross-validate (the writer emits only
/// composite TrueType and embedded standard-14 fonts) and are a later slice. Embedding itself is
/// enforced by <see cref="FontEmbeddingRule"/>.
/// </para>
/// </remarks>
internal sealed class FontStructureRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.2-font-subtype";

    public string Clause => "ISO 19005-2:2011, 6.2.11.2";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");

    private static readonly HashSet<string> _validSubtypes = new(StringComparer.Ordinal)
    {
        "Type0", "Type1", "MMType1", "Type3", "TrueType", "CIDFontType0", "CIDFontType2",
    };

    public void Evaluate(PreflightContext context)
    {
        var checkedFonts = new HashSet<int>();

        foreach (var font in context.EnumerateFonts())
        {
            CheckSubtype(context, font);

            // A composite font's descendant CIDFont is itself a font dictionary (§6.2.11.2 applies).
            if (context.Resolve(font.Get(PdfName.Subtype)) is PdfName { Value: "Type0" }
                && context.Resolve(font.Get(_descendantFonts)) is PdfArray descendants)
            {
                for (var i = 0; i < descendants.Count; i++)
                {
                    if (descendants[i] is PdfIndirectReference r && !checkedFonts.Add(r.ObjectNumber))
                        continue;
                    if (context.Resolve(descendants[i]) is PdfDictionary cidFont)
                        CheckSubtype(context, cidFont);
                }
            }
        }
    }

    private void CheckSubtype(PreflightContext context, PdfDictionary font)
    {
        var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;
        if (subtype is null || !_validSubtypes.Contains(subtype))
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "A font dictionary has an invalid or missing /Subtype "
                + $"({(subtype is null ? "absent" : $"/{subtype}")}); it shall be one of the font subtypes "
                + "defined in ISO 32000-1.");
    }
}
