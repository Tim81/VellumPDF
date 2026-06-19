// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.8 (PDF/A-2a — logical structure). A Level A file shall be tagged: the catalog's
/// <c>/MarkInfo</c> dictionary shall set <c>/Marked true</c>, the catalog shall reference a
/// <c>/StructTreeRoot</c>, and the structure tree's <c>/RoleMap</c> (if present) shall map roles
/// without circular references.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.8 and ISO 32000-1:2008, 14.7–14.8. Clean-room: derived from
/// the specification text, not from any third-party validation profile. The ParentTree ↔ MCID
/// bijection requires marked-content parsing of page content streams and is validated in a later
/// slice.
/// </remarks>
internal sealed class LogicalStructureRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.8-logical-structure";

    public string Clause => "ISO 19005-2:2011, 6.8";

    private static readonly PdfName _markInfo = new("MarkInfo");
    private static readonly PdfName _marked = new("Marked");
    private static readonly PdfName _structTreeRoot = new("StructTreeRoot");
    private static readonly PdfName _roleMap = new("RoleMap");

    public void Evaluate(PreflightContext context)
    {
        var markInfo = context.Resolve(context.Catalog.Get(_markInfo)) as PdfDictionary;
        if (context.Resolve(markInfo?.Get(_marked)) is not PdfBoolean { Value: true })
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "A PDF/A-2a file shall set the document catalog /MarkInfo /Marked entry to true.");
        }

        if (context.Resolve(context.Catalog.Get(_structTreeRoot)) is not PdfDictionary structTreeRoot)
        {
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "A PDF/A-2a file shall contain a document structure tree (/StructTreeRoot).");
            return;
        }

        if (context.Resolve(structTreeRoot.Get(_roleMap)) is PdfDictionary roleMap)
            CheckRoleMapAcyclic(context, roleMap);
    }

    private void CheckRoleMapAcyclic(PreflightContext context, PdfDictionary roleMap)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in roleMap.Entries)
        {
            if (context.Resolve(entry.Value) is PdfName target)
            {
                map[entry.Key.Value] = target.Value;
            }
            else
            {
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"The structure tree /RoleMap entry /{entry.Key.Value} shall map to a name.");
            }
        }

        foreach (var start in map.Keys)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var current = start;
            while (map.TryGetValue(current, out var next))
            {
                if (!visited.Add(current))
                {
                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        "The structure tree /RoleMap shall not contain circular role mappings.");
                    return;
                }
                current = next;
            }
        }
    }
}
