// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.4.2 heading-nesting rule:
/// <list type="bullet">
///   <item>7.4.2-1 (SEHn) — heading levels must not skip: an H(n) element is non-conformant
///     when it follows a heading whose level is less than n−1 (hasCorrectNestingLevel == false).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Predicate — hasCorrectNestingLevel.</strong>
/// The check applies only to the strongly-structured heading types H1–H6. The weakly-structured
/// type H is handled by §7.4.4-2/-3 (UaHeadingRule) and is exempt from nesting-level requirements.
/// </para>
/// <para><strong>Algorithm (empirically derived from veraPDF 1.30.2 probes).</strong>
/// veraPDF evaluates headings in document order (depth-first pre-order). It maintains a running
/// "previous heading level" initialised to 0. For each Hn element encountered:
/// <list type="bullet">
///   <item>If n &gt; previousLevel + 1 → <c>hasCorrectNestingLevel == false</c> → fire 7.4.2-1.</item>
///   <item>Otherwise (n ≤ previousLevel + 1, i.e. same level, going up by exactly 1, or going
///     down to any level) → update previousLevel = n → continue.</item>
/// </list>
/// The "previousLevel starts at 0" rule means the very first heading must be H1 (n=1 satisfies
/// 1 ≤ 0+1=1); starting with H2 or higher fires.
/// </para>
/// <para><strong>Probe confirmations against veraPDF 1.30.2:</strong>
/// <list type="bullet">
///   <item><c>742-h1-only</c>: only H1 → PASS (exit 0).</item>
///   <item><c>742-h1-h2-h3-ok</c>: H1→H2→H3 → PASS (exit 0).</item>
///   <item><c>742-h1-h2-h2-h3-ok</c>: H1→H2→H2→H3 → PASS (exit 0); same level twice is OK.</item>
///   <item><c>742-h1-h2-h1-h2-h3-ok</c>: H1→H2→H1→H2→H3 → PASS (exit 0); going down to H1
///     then back up H1→H2→H3 is OK.</item>
///   <item><c>742-h1-h3-skip</c>: H1→H3 (skip H2) → fires 7.4.2-1 (exit 1).</item>
///   <item><c>742-h2-first</c>: H2 as first heading → fires 7.4.2-1 (exit 1); 2 &gt; 0+1.</item>
///   <item><c>742-h3-only</c>: H3 as only heading → fires 7.4.2-1 (exit 1); 3 &gt; 0+1.</item>
///   <item><c>742-h3-h1-down</c>: H3→H1 → fires 7.4.2-1 (exit 1); H3 itself is the violation.</item>
///   <item><c>742-h1-h2-h1-h3</c>: H1→H2→H1→H3 (back to H1 then jump to H3) → fires 7.4.2-1
///     (exit 1); after H1, H3 &gt; H1+1.</item>
///   <item><c>742-h2-h3-noh1</c>: H2→H3 (no H1) → fires 7.4.2-1 (exit 1).</item>
///   <item><c>742-h1-h2-h3-h1-h3</c>: H1→H2→H3→H1→H3 → fires (after H1, H3 &gt; H1+1).</item>
///   <item><c>742-h1-h2-h3-h2-h4-skip</c>: H1→H2→H3→H2→H4 → fires (H2→H4 skips H3).</item>
///   <item><c>742-h1-h2-h3-h2-h3-ok</c>: H1→H2→H3→H2→H3 → PASS (going back to H2, then H3 = H2+1).</item>
///   <item><c>742-h1-h2-h3-h1-down</c>: H1→H2→H3→H1 → PASS (going down is always OK).</item>
/// </list>
/// </para>
/// <para><strong>Not strongly-structured documents.</strong> §7.4.2 applies only to documents not
/// using the strongly-structured heading model (ISO 32000-1 14.8.4.3.5). veraPDF fires 7.4.2-1 for
/// any out-of-sequence H1–H6 heading regardless of whether the document uses strongly-structured
/// headings; the nesting rule applies to the Hn heading types unconditionally.</para>
/// <para><strong>FP-safety — null StandardType.</strong> Elements whose /S is non-standard and
/// unmapped (StandardType == null) are skipped, preserving the invariant that unknown structure
/// types do not trigger rules.</para>
/// <para>Cross-validated against veraPDF 1.30.2 for each probe (violating + compliant fixtures).</para>
/// </remarks>
internal sealed class UaHeadingNestingRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.4.2-1";
    public string Clause => "ISO 14289-1:2014, 7.4.2";

    // Maps heading standard type → integer level (H1=1 … H6=6).
    private static int? HnLevel(string? standardType) => standardType switch
    {
        "H1" => 1,
        "H2" => 2,
        "H3" => 3,
        "H4" => 4,
        "H5" => 5,
        "H6" => 6,
        _ => null,
    };

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        // Walk heading elements in document order (AllNodes is depth-first pre-order).
        // previousLevel starts at 0: the first heading must be H1 (level 1 = 0+1).
        var previousLevel = 0;

        foreach (var node in tree.AllNodes)
        {
            var level = HnLevel(node.StandardType);
            if (level is null)
                continue; // not an Hn heading — skip

            if (level.Value > previousLevel + 1)
            {
                context.Report(
                    "ISO14289-1:7.4.2-1",
                    "ISO 14289-1:2014, 7.4.2",
                    PreflightSeverity.Error,
                    $"Heading level H{level.Value} follows a heading at level {previousLevel} — " +
                    $"heading levels must not skip (expected at most H{previousLevel + 1} here, §7.4.2).");
                // veraPDF fires once per violation element; continue to catch any further violations.
            }

            // Update previousLevel regardless of whether this element fired.
            // veraPDF tracks the most-recently-seen heading level even for violating elements.
            previousLevel = level.Value;
        }
    }
}
