// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.7.3.4 — structure-element type conformance for PDF/A-2a.
///
/// <para>Three sub-clauses, each evaluated on every walked <see cref="StructureTreeNode"/>:</para>
/// <list type="number">
///   <item>
///     <strong>testNumber 1</strong> — <c>isNotMappedToStandardType == false</c><br/>
///     Every StructElem whose <c>/S</c> is a non-standard type must be role-mapped (via the
///     <c>/RoleMap</c> chain) to one of the ISO 32000-1 Table 333 standard structure types.
///   </item>
///   <item>
///     <strong>testNumber 2</strong> — <c>circularMappingExist != true</c><br/>
///     The <c>/RoleMap</c> chain starting from any non-standard <c>/S</c> actually used by a
///     walked element must not revisit a name (a cycle). Evaluated only on elements whose
///     <c>/S</c> chain cycles — not on the <c>/RoleMap</c> dictionary alone.
///   </item>
///   <item>
///     <strong>testNumber 3</strong> — <c>remappedStandardType == null</c><br/>
///     No StructElem shall use a standard Table 333 structure type as its <c>/S</c> value when
///     that type is the key of a <c>/RoleMap</c> entry whose value is a <em>non-standard</em>
///     type (i.e. the standard type is remapped to something not in Table 333). A standard-to-
///     standard remap (e.g. <c>/P /Div</c>) is accepted by veraPDF and is therefore also
///     accepted here.
///   </item>
/// </list>
///
/// <para>FP-safety — all three sub-clauses are scoped to structure elements ACTUALLY USED in
/// the walked tree, NOT to the <c>/RoleMap</c> dictionary alone. veraPDF evaluates these
/// predicates on a <c>PDStructElem</c> that uses the type in its <c>/S</c>; a <c>/RoleMap</c>
/// entry that is never referenced by any element does not trigger these rules. This is
/// empirically confirmed against veraPDF 1.30.2:</para>
/// <list type="bullet">
///   <item>A <c>/RoleMap &lt;&lt; /Foo /Bar /Bar /Foo &gt;&gt;</c> with no element using Foo/Bar:
///   veraPDF passes — neither 6.7.3.4-1 nor 6.7.3.4-2 fires.</item>
///   <item>The same <c>/RoleMap</c> with an element <c>/S /Foo</c>: veraPDF fires 6.7.3.4-2.</item>
///   <item>A <c>/RoleMap &lt;&lt; /P /MyNonStd &gt;&gt;</c> with an element <c>/S /P</c>: veraPDF
///   fires 6.7.3.4-3. The same map with <c>/P /Div</c> (target is standard): veraPDF passes
///   (empirically confirmed).</item>
/// </list>
///
/// <para>Predicate difference from PDF/UA-1 §7.1-6/7.1-7: UA-1 7.1-7 fires for ANY standard-to-*
/// remap (including standard-to-standard); 6.7.3.4-3 fires only for standard-to-NON-STANDARD.
/// These clauses are therefore NOT equivalent and cannot be aliased.</para>
/// </summary>
internal sealed class A2aStructureTypeRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.7.3.4";

    public string Clause => "ISO 19005-2:2011, 6.7.3.4";

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return; // No structure elements to evaluate — LogicalStructureRule covers absent StructTreeRoot.

        foreach (var node in tree.AllNodes)
        {
            var rawType = node.RawType;
            if (rawType is null)
                continue; // Element with no /S — structural malformation caught elsewhere.

            if (StructureTree.StandardTypes.Contains(rawType))
            {
                // testNumber 3: standard type used as a /RoleMap key whose mapping chain never
                // resolves to a standard type. veraPDF resolves the FULL /RoleMap chain (empirically
                // confirmed against veraPDF 1.30.2): /P → /Div passes (immediate standard), and
                // /P → /Foo → /Span ALSO passes (a non-standard intermediate that eventually reaches
                // a standard type). Only /P → /MyNonStd, where the chain dead-ends (or cycles) at a
                // non-standard name, fires. Checking only the immediate target would over-reject the
                // multi-hop-to-standard case — a false positive veraPDF does not raise.
                if (tree.RoleMap is not null
                    && tree.RoleMap.TryGetValue(rawType, out var target)
                    && !ChainReachesStandard(rawType, tree.RoleMap))
                {
                    context.Report(
                        RuleId + "-3",
                        Clause,
                        PreflightSeverity.Error,
                        $"A structure element uses the standard structure type /{rawType}, which "
                        + $"the /RoleMap remaps to the non-standard type /{target}. "
                        + "ISO 19005-2 §6.7.3.4 requires that standard structure types are not "
                        + "remapped to non-standard types.");
                    return;
                }
            }
            else
            {
                // Non-standard /S: check testNumbers 1 and 2.
                // node.StandardType is null when the type is either unmapped or its chain cycles.
                // We must distinguish these two cases.
                if (ChainCycles(rawType, tree.RoleMap))
                {
                    // testNumber 2: circular mapping exists for this element's type.
                    context.Report(
                        RuleId + "-2",
                        Clause,
                        PreflightSeverity.Error,
                        $"A structure element's /S role-mapping chain for /{rawType} is circular. "
                        + "ISO 19005-2 §6.7.3.4 requires the /RoleMap to be acyclic.");
                    return;
                }

                if (node.StandardType is null)
                {
                    // testNumber 1: non-standard type is not role-mapped to any standard type.
                    context.Report(
                        RuleId + "-1",
                        Clause,
                        PreflightSeverity.Error,
                        $"A structure element uses the non-standard structure type /{rawType}, "
                        + "which is not role-mapped (via the StructTreeRoot /RoleMap) to one of "
                        + "the ISO 32000-1 Table 333 standard structure types. "
                        + "ISO 19005-2 §6.7.3.4 requires every non-standard structure type to be "
                        + "role-mapped to a standard type.");
                    return;
                }
            }
        }
    }

    // Follows the /RoleMap chain from <paramref name="start"/> and returns true when any hop lands
    // on a standard Table 333 type. Returns false when the chain dead-ends at a non-standard name
    // or cycles without reaching a standard type. Mirrors veraPDF's full-chain resolution for
    // remappedStandardType (§6.7.3.4-3): a standard type remapped through non-standard intermediates
    // that ultimately reach a standard type is NOT a violation.
    private static bool ChainReachesStandard(string start, IReadOnlyDictionary<string, string> roleMap)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = start;
        while (roleMap.TryGetValue(current, out var next))
        {
            if (!seen.Add(current))
                return false; // cycle — never resolves to a standard type
            if (StructureTree.StandardTypes.Contains(next))
                return true; // reached a standard type
            current = next;
        }
        return false; // dead-ended at a non-standard name
    }

    // Follows the /RoleMap chain from <paramref name="start"/> and returns true when it
    // revisits a name (a cycle). Terminates at a standard type or an unmapped name.
    private static bool ChainCycles(string start, IReadOnlyDictionary<string, string>? roleMap)
    {
        if (roleMap is null)
            return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = start;
        while (roleMap.TryGetValue(current, out var next))
        {
            if (!seen.Add(current))
                return true;
            if (StructureTree.StandardTypes.Contains(next))
                return false; // reached standard type — terminates cleanly
            current = next;
        }
        return false;
    }
}
