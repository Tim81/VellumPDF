// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.1 testNumber 6 (PDF/UA-1: PDStructElem — circular mapping exists in the
/// /RoleMap) and testNumber 7 (PDF/UA-1: PDStructElem — a standard structure type is remapped).
/// </summary>
/// <remarks>
/// <strong>7.1-6</strong> — the veraPDF predicate <c>circularMappingExist != true</c>:
/// the /RoleMap shall not contain a circular chain of role mappings (e.g. /Foo → /Bar, /Bar → /Foo).
/// This is logically identical to the check already performed by the PDF/A-2a §6.8 rule
/// (<see cref="LogicalStructureRule"/>), re-issued here under the ISO 14289-1 rule ID.
///
/// <strong>7.1-7</strong> — the veraPDF predicate <c>remappedStandardType == null</c>:
/// no entry in the /RoleMap shall have a KEY that is one of the ISO 32000-1 Table 333
/// standard structure types. Remapping a standard type (e.g. <c>/Table /Div</c>) is forbidden.
/// Under-detection is safe: a missing entry in the standard-type set → we simply don't flag
/// its remap, which is preferable to over-rejecting.
///
/// <para>FP-safety — both rules are scoped to the structure types ACTUALLY USED on walked
/// elements, NOT to the /RoleMap dictionary alone. veraPDF evaluates these predicates on a
/// PDStructElem (an element that uses the role-mapped type in its /S); a /RoleMap that remaps a
/// standard type or contains a cycle is ACCEPTED by veraPDF when no element uses the offending
/// type. Firing on the /RoleMap contents alone would over-reject such conformant documents
/// (empirically confirmed against veraPDF 1.30.2). So:</para>
/// <para>
/// (1) Both rules return early when there is no /RoleMap or no structure elements.
/// </para>
/// <para>
/// (2) 7.1-7 fires only when a walked element's raw /S is a standard Table 333 type that is also a
///     /RoleMap key (the element uses a remapped standard type).
/// </para>
/// <para>
/// (3) 7.1-6 fires only when a walked element's raw /S role-map chain revisits a name (a cycle).
///     An element whose /S is standard or unmapped, or maps acyclically, does not fire.
/// </para>
///
/// <para>
/// Cross-validated against veraPDF 1.30.2:
/// (a) /RoleMap &lt;&lt; /Foo /Bar /Bar /Foo &gt;&gt; WITH an element /S /Foo fires 7.1-6; the same
///     /RoleMap with no element using Foo/Bar is accepted (no fire);
/// (b) /RoleMap &lt;&lt; /Table /Div &gt;&gt; WITH an element /S /Table fires 7.1-7; the same /RoleMap
///     with only a /Document element is accepted (no fire);
/// (c) the normal UA-1 tagged baseline (no /RoleMap) fires neither.
/// </para>
/// </remarks>
internal sealed class UaRoleMapRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.1-6-7"; // covers both testNumbers

    public string Clause => "ISO 14289-1:2014, 7.1";

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.RoleMap is not { } roleMap || tree.AllNodes.Count == 0)
            return; // no /RoleMap, or no structure elements to evaluate → both rules satisfied

        // veraPDF evaluates circularMappingExist / remappedStandardType on a PDStructElem that
        // ACTUALLY USES the role-mapped type in its /S — NOT on the /RoleMap dictionary alone.
        // Empirically confirmed against veraPDF 1.30.2: a /RoleMap that remaps a standard type, or
        // contains a cycle, is accepted when NO structure element uses the offending type. So both
        // rules are scoped to the /S types actually present on walked elements.

        // ── 7.1-7: an element uses a standard structure type that the /RoleMap remaps. ──────────
        foreach (var node in tree.AllNodes)
        {
            if (node.RawType is { } s && StructureTree.StandardTypes.Contains(s) && roleMap.ContainsKey(s))
            {
                context.Report(
                    "ISO14289-1:7.1-7",
                    Clause,
                    PreflightSeverity.Error,
                    $"A structure element uses the standard structure type /{s}, which the /RoleMap "
                    + "remaps. PDF/UA-1 §7.1 forbids remapping ISO 32000-1 Table 333 standard "
                    + "structure types.");
                break; // report at most once per document
            }
        }

        // ── 7.1-6: an element's /S role-map chain contains a cycle. ────────────────────────────
        foreach (var node in tree.AllNodes)
        {
            if (node.RawType is { } s && ChainCycles(s, roleMap))
            {
                context.Report(
                    "ISO14289-1:7.1-6",
                    Clause,
                    PreflightSeverity.Error,
                    "A structure element's /S role-mapping chain is circular. "
                    + "PDF/UA-1 §7.1 requires the /RoleMap to be acyclic.");
                break; // report at most once per document
            }
        }
    }

    // Follows the /RoleMap chain from <paramref name="start"/> and returns true when it revisits a
    // name (a cycle). A standard type that is not a /RoleMap key simply terminates the chain.
    private static bool ChainCycles(string start, IReadOnlyDictionary<string, string> roleMap)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = start;
        while (roleMap.TryGetValue(current, out var next))
        {
            if (!seen.Add(current))
                return true;
            current = next;
        }
        return false;
    }
}
