// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.9 Note ID rules:
/// <list type="bullet">
///   <item>7.9-1 (SENote) — every Note element must have a non-empty /ID byte string.</item>
///   <item>7.9-2 (SENote) — the /ID values of all Note elements must be unique (no duplicates).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>7.9-1 predicate:</strong> <c>noteID != null &amp;&amp; noteID != ''</c>.
/// An absent /ID and an empty /ID both fire. Confirmed by direct probing against veraPDF 1.30.2:
/// <list type="bullet">
///   <item><c>probe_note_good_id</c>: /ID (note1) → PASS (exit 0).</item>
///   <item><c>probe_note_empty_id</c>: /ID () → fires 7.9-1 (exit 1).</item>
///   <item><c>probe_note_no_id</c>: no /ID → fires 7.9-1 (exit 1).</item>
/// </list>
/// </para>
/// <para><strong>7.9-2 predicate:</strong> <c>hasDuplicateNoteID == false</c>.
/// Two Note elements sharing the same /ID bytes fire 7.9-2. Comparison is byte-exact.
/// Confirmed:
/// <list type="bullet">
///   <item><c>probe_note_duplicate_id</c>: two Notes with /ID (dup) → fires 7.9-2 (exit 1).</item>
///   <item><c>probe_note_unique_ids</c>: two Notes with distinct IDs → PASS (exit 0).</item>
/// </list>
/// </para>
/// <para><strong>FP-safety — role-mapped types.</strong> Checks key on
/// <see cref="StructureTreeNode.StandardType"/> (role-map-resolved); an element with /S /MyNote
/// role-mapped to Note is subject to both rules.</para>
/// <para><strong>FP-safety — null StandardType.</strong> Elements with an unknown/unmapped /S
/// are skipped.</para>
/// <para>Cross-validated against veraPDF 1.30.2 for each probe (violating + compliant fixture).</para>
/// </remarks>
internal sealed class UaNoteIdRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.9-1-2";
    public string Clause => "ISO 14289-1:2014, 7.9";

    private static readonly PdfName _id = new("ID");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        // Collect Note elements and their /ID bytes in one pass.
        // Track seen IDs for duplicate detection (7.9-2).
        var seenIds = new Dictionary<string, bool>(StringComparer.Ordinal);
        var firedDuplicate = false;

        foreach (var node in tree.AllNodes)
        {
            if (node.StandardType != "Note")
                continue;

            var idObj = context.Resolve(node.Dict.Get(_id));

            // 7.9-1: /ID must be present and non-empty.
            var idBytes = idObj switch
            {
                PdfLiteralString s => s.Bytes,
                PdfHexString s => s.Bytes,
                _ => (ReadOnlyMemory<byte>?)null,
            };

            if (idBytes is null || idBytes.Value.Length == 0)
            {
                context.Report(
                    "ISO14289-1:7.9-1",
                    "ISO 14289-1:2014, 7.9",
                    PreflightSeverity.Error,
                    "A Note structure element is missing a non-empty /ID entry (§7.9).");
                // Still try to check 7.9-2 for other Notes with valid IDs;
                // skip uniqueness check for this element (it has no valid ID).
                continue;
            }

            // 7.9-2: /ID must be unique across all Notes.
            if (!firedDuplicate)
            {
                // Build a string key from the raw bytes (ASCII / PDF-doc-encoding) for dictionary lookup.
                var keyStr = System.Text.Encoding.Latin1.GetString(idBytes.Value.Span);
                if (!seenIds.TryAdd(keyStr, true))
                {
                    firedDuplicate = true;
                    context.Report(
                        "ISO14289-1:7.9-2",
                        "ISO 14289-1:2014, 7.9",
                        PreflightSeverity.Error,
                        $"Two or more Note structure elements share the same /ID value \"{keyStr}\" (§7.9).");
                }
            }
        }
    }
}
