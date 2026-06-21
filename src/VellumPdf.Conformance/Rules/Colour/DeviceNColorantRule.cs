// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Colour;

/// <summary>
/// ISO 19005-2 §6.1.13-9 (DeviceN colourant limit). A conforming file shall not contain a
/// DeviceN colour space with more than 32 colourants. A DeviceN colour space is a PDF array
/// <c>[/DeviceN names alt tint (attrs?)]</c> where <c>names</c> is an array of colourant name
/// objects; the number of colourants is <c>names.Count</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.13 (item t9) and ISO 32000-1:2008, §8.6.6.5 (DeviceN).
/// Clean-room: derived from the specification text, not from any third-party validation profile.
/// <para>
/// Scoped to colour spaces the page actually selects with the <c>cs</c>/<c>CS</c> operators: veraPDF
/// only flags an oversized DeviceN that is painted, not one merely present in the resource dictionary
/// (verified against the oracle), so checking presence alone would over-reject conformant files. A
/// colour space shared across pages is reported at most once (deduplicated by object number).
/// </para>
/// <para>
/// Deferred edges: a DeviceN colour space used only via an image's <c>/ColorSpace</c>, a pattern, the
/// alternate space of another colour space, or a nested colour-space array is not yet detected (these
/// under-detect rather than risk a false positive) — only spaces selected by a page-content
/// <c>cs</c>/<c>CS</c> operator and resolved through <c>/Resources /ColorSpace</c> are checked.
/// </para>
/// </remarks>
internal sealed class DeviceNColorantRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.13-9-devicen";

    public string Clause => "ISO 19005-2:2011, 6.1.13";

    private static readonly PdfName _colorSpace = new("ColorSpace");
    private const int MaxColourants = 32;

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
            // which does not flag an oversized DeviceN that is present but never painted.
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
                if (context.Resolve(csArray[1]) is not PdfArray names || names.Count <= MaxColourants)
                    continue;

                // Deduplicate a colour space shared (by indirect reference) across pages.
                if (entry.Value is PdfIndirectReference r && !reported.Add(r.ObjectNumber))
                    continue;

                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"A DeviceN colour space defines {names.Count} colourants; PDF/A-2 permits at most {MaxColourants}.");
            }
        }
    }
}
