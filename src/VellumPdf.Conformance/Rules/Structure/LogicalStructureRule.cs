// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.7 (PDF/A-2a — logical structure; the rule reports under 6.8, see the remarks). A
/// Level A file shall be tagged: the catalog's <c>/MarkInfo</c> dictionary shall set <c>/Marked
/// true</c>, the catalog shall reference a <c>/StructTreeRoot</c>, and the structure tree's
/// <c>/RoleMap</c> (if present) shall map roles without circular references.
/// </summary>
/// <remarks>
/// Cites ISO 19005-2:2011, 6.8 and ISO 32000-1:2008, 14.7–14.8, and reports the id
/// <c>6.8-logical-structure</c>. 6.8 is the embedded-files clause. The requirements checked are in
/// 6.7.2.2 (the <c>/MarkInfo</c> dictionary), 6.7.3.3 (the structure tree root) and 6.7.3.4 (a role
/// mapping that terminates), so the reported clause and id are wrong; correcting them changes what
/// the rule reports and is tracked in #418. Its behaviour was checked against veraPDF's PDF/A-2
/// profile, and ISO 19005-2 was not held when it was written. The ParentTree ↔ MCID bijection
/// requires marked-content parsing of page content streams and is validated in a later slice.
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
                    $"The structure tree /RoleMap entry "
                    + $"/{DiagnosticExcerpt.Quote(entry.Key.Value)} shall map to a name.");
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
