// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.2.4.4-1 (DeviceN Colorants dictionary). For any spot colour used in a DeviceN
/// or NChannel colour space, an entry in the <c>/Colorants</c> dictionary shall be present.
/// </summary>
/// <remarks>
/// A DeviceN colour space is the array <c>[/DeviceN names altSpace tintTransform attributes?]</c>
/// (4 or 5 elements). The optional fifth element <c>attributes</c> is a dictionary that may contain
/// a <c>/Colorants</c> sub-dictionary mapping each colourant name to a Separation colour-space
/// array. The clause requires that for each non-exempt colourant an entry exists in <c>/Colorants</c>.
/// <para>
/// <b>Exemptions (determined empirically via veraPDF 1.30.2 probing):</b>
/// The process colourant names <c>Cyan</c>, <c>Magenta</c>, <c>Yellow</c>, <c>Black</c> and the
/// special name <c>None</c> are exempt — veraPDF does not flag a missing entry for these. All other
/// names, including the special name <c>All</c>, require a <c>/Colorants</c> entry. A missing
/// attributes dictionary (4-element DeviceN) when spot colourants are present also triggers the rule.
/// </para>
/// <para>
/// Authored from ISO 19005-2:2011, §6.2.4.4 and ISO 32000-1:2008, §8.6.6.5 (DeviceN). Clean-room:
/// derived from the specification text and empirical oracle probing, not from any third-party
/// validation profile.
/// </para>
/// <para>
/// Scoped to colour spaces the page actually selects with the <c>cs</c>/<c>CS</c> operators:
/// veraPDF only flags a DeviceN colour space that is used by page content, not one that is merely
/// present in the resource dictionary (verified against the oracle), so checking presence alone would
/// over-reject conformant files. A colour space shared across pages is reported at most once
/// (deduplicated by object number).
/// </para>
/// <para>
/// Deferred edges: a DeviceN colour space used only via an image's <c>/ColorSpace</c>, a pattern, the
/// alternate space of another colour space, or a nested colour-space array is not yet detected (these
/// under-detect rather than risk a false positive) — only spaces selected by a page-content
/// <c>cs</c>/<c>CS</c> operator and resolved through <c>/Resources /ColorSpace</c> are checked.
/// </para>
/// </remarks>
internal sealed class DeviceNColorantsRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.4.4-1-colorants";

    public string Clause => "ISO 19005-2:2011, 6.2.4.4";

    private static readonly PdfName _colorSpace = new("ColorSpace");
    private static readonly PdfName _colorants = new("Colorants");

    /// <summary>
    /// Colourant names that are exempt from the /Colorants entry requirement. These are the four
    /// process colour names (ISO 32000-1 Table 64 footnote) and the special name <c>None</c>.
    /// Verified against veraPDF 1.30.2: <c>Cyan</c>, <c>Magenta</c>, <c>Yellow</c>, <c>Black</c>,
    /// and <c>None</c> are not flagged when absent from /Colorants; <c>All</c> IS flagged.
    /// </summary>
    private static readonly HashSet<string> _exemptNames = new(StringComparer.Ordinal)
    {
        "Cyan", "Magenta", "Yellow", "Black", "None",
    };

    public void Evaluate(PreflightContext context)
    {
        // Colour spaces may be shared across pages via indirect references; report each once.
        var reported = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_colorSpace)) is not PdfDictionary colorSpaces)
                continue;

            // Only colour spaces the page actually selects (cs/CS) are in scope — matching veraPDF,
            // which does not flag a DeviceN whose Colorants dict is incomplete when the space is
            // present but never painted.
            var selected = ContentStreamUsage.Analyze(context, page).SelectedColorSpaces;
            if (selected.Count == 0)
                continue;

            foreach (var entry in colorSpaces.Entries)
            {
                if (!selected.Contains(entry.Key.Value))
                    continue;

                // A DeviceN colour space is the array [/DeviceN names alt tint (attrs?)] — bounds are
                // checked before indexing so a malformed short array cannot throw.
                if (context.Resolve(entry.Value) is not PdfArray csArray || csArray.Count < 2)
                    continue;
                if (context.Resolve(csArray[0]) is not PdfName { Value: "DeviceN" })
                    continue;
                if (context.Resolve(csArray[1]) is not PdfArray names)
                    continue;

                // Deduplicate a colour space shared (by indirect reference) across pages.
                if (entry.Value is PdfIndirectReference r && !reported.Add(r.ObjectNumber))
                    continue;

                // Collect the non-exempt colourant names that need a /Colorants entry.
                var requiredNames = new List<string>(names.Count);
                for (var i = 0; i < names.Count; i++)
                {
                    if (context.Resolve(names[i]) is PdfName name && !_exemptNames.Contains(name.Value))
                        requiredNames.Add(name.Value);
                }

                // If all colourants are exempt there is nothing to check.
                if (requiredNames.Count == 0)
                    continue;

                // The attributes dict is the optional 5th element (index 4). When it is absent
                // and there are required (non-exempt) colourants veraPDF flags the rule — verified
                // empirically (probe E). When the attributes dict is present but has no /Colorants
                // sub-dict, or the /Colorants dict is missing a required name, also flag.
                PdfDictionary? colorantsDict = null;
                if (csArray.Count >= 5)
                {
                    if (context.Resolve(csArray[4]) is PdfDictionary attrs)
                        colorantsDict = context.Resolve(attrs.Get(_colorants)) as PdfDictionary;
                }

                foreach (var requiredName in requiredNames)
                {
                    if (colorantsDict is null || colorantsDict.Get(new PdfName(requiredName)) is null)
                    {
                        context.Report(
                            RuleId,
                            Clause,
                            PreflightSeverity.Error,
                            $"A colorant of the DeviceN or NChannel color space is not defined in the Colorants dictionary (colourant: /{requiredName}).");
                        break; // One violation per colour-space object is sufficient.
                    }
                }
            }
        }
    }
}
