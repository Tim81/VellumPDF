// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.2 / §6.2.11.3.2 (Font dictionary structure). Every font dictionary in a
/// conforming file — including the descendant CIDFonts of a composite (Type 0) font — shall have a
/// recognised <c>/Subtype</c>; and an embedded Type 2 CIDFont (<c>/CIDFontType2</c>) shall carry a
/// <c>/CIDToGIDMap</c> entry.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.2.11.2 / 6.2.11.3.2 and ISO 32000-1:2008, 9.6–9.7. Clean-room:
/// derived from the specification text, not from any third-party validation profile. Both checks are
/// cross-validated against veraPDF on real embedded fonts: a corrupted descendant subtype fails
/// clause 6.2.11.2-2, and an embedded CIDFontType2 with its CIDToGIDMap removed fails 6.2.11.3.2-1.
/// <para>
/// These are the §6.2.11 checks reachable through the writer's composite fonts. The simple-font
/// requirements (FirstChar/LastChar/Widths §6.2.11.2, TrueType encoding §6.2.11.6) and the
/// font-<em>program</em> checks (glyph presence §6.2.11.4.1, glyph-width §6.2.11.5, embedded cmap
/// §6.2.11.6) need malformed <em>simple-font</em> fixtures to cross-validate — and the writer emits
/// only composite fonts, so they await a dedicated simple-font fixturing capability. Embedding itself
/// is enforced by <see cref="FontEmbeddingRule"/>.
/// </para>
/// </remarks>
internal sealed class FontStructureRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.2-font-subtype";

    public string Clause => "ISO 19005-2:2011, 6.2.11.2";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");

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
                    {
                        CheckSubtype(context, cidFont);
                        CheckCidToGidMap(context, cidFont);
                    }
                }
            }
        }
    }

    // §6.2.11.3.2: an embedded Type 2 CIDFont shall carry a /CIDToGIDMap (Identity or a stream).
    private void CheckCidToGidMap(PreflightContext context, PdfDictionary cidFont)
    {
        if (context.Resolve(cidFont.Get(PdfName.Subtype)) is not PdfName { Value: "CIDFontType2" })
            return;
        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor
            || context.ResolveStream(descriptor.Get(_fontFile2)) is null)
            return; // only embedded CIDFontType2 fonts are constrained here (embedding is §6.3).
        if (cidFont.Get(_cidToGidMap) is null)
            context.Report(
                "ISO19005-2:6.2.11.3.2-cidtogidmap",
                "ISO 19005-2:2011, 6.2.11.3.2",
                PreflightSeverity.Error,
                "An embedded CIDFontType2 is missing the required /CIDToGIDMap entry.");
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
