// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.5 table cell connected-header rules:
/// <list type="bullet">
///   <item>7.5-1 (SETD) — every TD cell must have a connected header; fires when
///     <c>hasConnectedHeader == false</c> AND <c>unknownHeaders == ''</c>.</item>
///   <item>7.5-2 (SETD) — fires when <c>hasConnectedHeader == false</c> AND
///     <c>unknownHeaders != ''</c> (the TD's /Headers array references IDs not present in any TH).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>Algorithm (empirically derived from veraPDF 1.30.2 probes).</strong>
/// A TD cell has a "connected header" when one of the following is true:
/// <list type="bullet">
///   <item><strong>Scoped TH in the same Table:</strong> any TH in the same containing Table has a
///     <c>/Scope</c> attribute (in its <c>/A</c> entry with <c>/O = Table</c>). The scope value
///     (Column/Row/Both) is not checked; any non-null /Scope satisfies. Probe-confirmed: a TH with
///     <c>/Scope /Column</c> in a different TR from the TD still satisfies — the scope is table-wide.</item>
///   <item><strong>Explicit /Headers resolving to valid THs:</strong> the TD's <c>/A</c> entry
///     contains a <c>/Headers</c> byte-string array (with <c>/O = Table</c>), and every ID in that
///     array matches the <c>/ID</c> entry of some TH element anywhere in the document.</item>
/// </list>
/// <strong>Special cases (FP-safe, probe-confirmed):</strong>
/// <list type="bullet">
///   <item>A Table with no TH at all: veraPDF does NOT fire 7.5-1/-2. The predicate
///     <c>hasConnectedHeader</c> is undefined (not false) when there is no TH to connect to.
///     Probe: <c>75-td-only-no-th</c> (Table → TR → [TD, TD], no TH) → PASS (exit 0).</item>
///   <item>A TH with /Scope overrides a TD with invalid /Headers: probe <c>75-scope-and-bad-headers</c>
///     (TH /Scope /Column + TD /Headers pointing to nonexistent ID) → PASS (exit 0). The scoped
///     TH satisfies the requirement even when the explicit /Headers on the TD is invalid.</item>
/// </list>
/// </para>
/// <para><strong>Probe series against veraPDF 1.30.2:</strong>
/// <list type="bullet">
///   <item><c>75-scoped-col-no-headers</c>: TH /Scope /Column, TD no /Headers → PASS.</item>
///   <item><c>75-scoped-row-no-headers</c>: TH /Scope /Row, TD no /Headers → PASS.</item>
///   <item><c>75-scoped-both-no-headers</c>: TH /Scope /Both, TD no /Headers → PASS.</item>
///   <item><c>75-no-scope-valid-headers</c>: TH no scope, TD /Headers = valid TH /ID → PASS.</item>
///   <item><c>75-no-scope-no-headers</c>: TH no scope, TD no /Headers → fires 7.5-1.</item>
///   <item><c>75-invalid-headers</c>: TD /Headers = nonexistent ID → fires 7.5-2.</item>
///   <item><c>75-scope-and-headers</c>: TH /Scope + TD /Headers valid → PASS.</item>
///   <item><c>75-td-only-no-th</c>: no TH in table → PASS.</item>
///   <item><c>75-th-no-scope-no-id-td-no-headers</c>: TH no scope no ID, TD no headers → fires 7.5-1.</item>
///   <item><c>75-scope-and-bad-headers</c>: TH /Scope + TD /Headers nonexistent → PASS.</item>
///   <item><c>75-multirow-scope-col</c>: TH /Scope /Column in row1, TD in row2 → PASS.</item>
///   <item><c>75-2x2-row-scope</c>: 2x2 table, THs /Scope /Row → PASS.</item>
///   <item><c>75-th-row-scope-cross-row</c>: TH /Scope /Row in row1, TD in row2 → PASS.</item>
///   <item><c>75-2col-scope-col</c>: two rows each TH+TD, TH /Scope /Column → PASS.</item>
///   <item><c>75-header-row-scope</c>: header row THs /Scope /Row, body row TDs → PASS.</item>
/// </list>
/// </para>
/// <para><strong>FP-safety — /A attribute forms.</strong> The /A key on a StructElem may be a
/// single attribute dictionary or an array of attribute dictionaries (possibly interleaved with
/// integer revision numbers). Only dictionaries with <c>/O = Table</c> are considered. This matches
/// the pattern established for ColSpan/RowSpan in <see cref="UaTableGridRule"/>.</para>
/// <para><strong>FP-safety — null StandardType.</strong> Elements with unknown/unmapped /S are
/// skipped.</para>
/// <para>Cross-validated against veraPDF 1.30.2 for each probe listed above.</para>
/// </remarks>
internal sealed class UaTableHeaderRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.5-1-2";
    public string Clause => "ISO 14289-1:2014, 7.5";

    private static readonly PdfName _a = new("A");
    private static readonly PdfName _o = new("O");
    private static readonly PdfName _scope = new("Scope");
    private static readonly PdfName _headers = new("Headers");
    private static readonly PdfName _id = new("ID");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);
        if (tree.AllNodes.Count == 0)
            return;

        // ── Pass 1: Build a document-wide TH /ID → node map (byte-exact key). ──────────────────
        // Used for /Headers resolution: TD /Headers entries are byte strings that should match
        // /ID entries on TH elements anywhere in the document.
        var thIdMap = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var node in tree.AllNodes)
        {
            if (node.StandardType != "TH")
                continue;
            var idBytes = GetByteString(context, node.Dict.Get(_id));
            if (idBytes is { Length: > 0 })
            {
                // Key on Latin-1 interpretation (PDF byte strings are raw bytes; Latin-1 is the
                // identity mapping for bytes 0–255, giving us a comparable dictionary key).
                var key = System.Text.Encoding.Latin1.GetString(idBytes);
                thIdMap.TryAdd(key, true);
            }
        }

        // ── Pass 2: Evaluate each TD. ─────────────────────────────────────────────────────────
        foreach (var tdNode in tree.AllNodes)
        {
            if (tdNode.StandardType != "TD")
                continue;

            // Find the containing Table by walking up the parent chain.
            // veraPDF's predicate is evaluated in the context of the cell's enclosing Table.
            var tableNode = FindContainingTable(tdNode);
            if (tableNode is null)
                continue; // TD not within a Table (structural anomaly) — skip defensively

            // Collect all TH nodes within this Table.
            var ths = CollectThs(tableNode);

            // No TH in the table → hasConnectedHeader is undefined (not false) → skip.
            if (ths.Count == 0)
                continue;

            // If any TH in the table has a /Scope attribute → all TDs are connected.
            if (AnyThHasScope(context, ths))
                continue;

            // Check the TD's own /Headers attribute.
            var headersIds = GetHeadersIds(context, tdNode);

            if (headersIds is null)
            {
                // No /Headers attribute → hasConnectedHeader=false, unknownHeaders='' → 7.5-1.
                context.Report(
                    "ISO14289-1:7.5-1",
                    "ISO 14289-1:2014, 7.5",
                    PreflightSeverity.Error,
                    "A TD structure element has no connected header: the containing Table has no TH " +
                    "with a /Scope attribute and the TD has no /Headers attribute (§7.5).");
                continue;
            }

            // /Headers present: check each ID against the document-wide TH /ID map.
            var hasUnknown = false;
            var hasKnown = false;
            foreach (var idKey in headersIds)
            {
                if (thIdMap.ContainsKey(idKey))
                    hasKnown = true;
                else
                    hasUnknown = true;
            }

            if (hasUnknown)
            {
                // Some /Headers IDs do not resolve to a TH → 7.5-2 (unknownHeaders != '').
                context.Report(
                    "ISO14289-1:7.5-2",
                    "ISO 14289-1:2014, 7.5",
                    PreflightSeverity.Error,
                    "A TD structure element's /Headers array references one or more IDs that do not " +
                    "correspond to any TH element's /ID (§7.5).");
            }
            // All known (hasKnown && !hasUnknown) → connected → no fire.
            // Edge: headersIds was an empty list (no IDs after parsing) — treat as no headers (7.5-1).
            else if (!hasKnown)
            {
                context.Report(
                    "ISO14289-1:7.5-1",
                    "ISO 14289-1:2014, 7.5",
                    PreflightSeverity.Error,
                    "A TD structure element has no connected header: the /Headers attribute is present " +
                    "but contains no valid TH references (§7.5).");
            }
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>Walks up the parent chain to find the nearest ancestor with StandardType "Table".</summary>
    private static StructureTreeNode? FindContainingTable(StructureTreeNode node)
    {
        var current = node.Parent;
        while (current is not null)
        {
            if (current.StandardType == "Table")
                return current;
            current = current.Parent;
        }
        return null;
    }

    /// <summary>
    /// Recursively collects all TH nodes within a Table subtree.
    /// Stops recursing at nested Table elements so TH elements of inner tables are not included.
    /// </summary>
    private static List<StructureTreeNode> CollectThs(StructureTreeNode tableNode)
    {
        var result = new List<StructureTreeNode>();
        CollectThsRecursive(tableNode, result, isRoot: true);
        return result;
    }

    private static void CollectThsRecursive(StructureTreeNode node, List<StructureTreeNode> result, bool isRoot)
    {
        foreach (var child in node.Children)
        {
            if (child.StandardType == "TH")
                result.Add(child);
            // Do not recurse into nested Tables — their TH elements belong to their own scope.
            if (child.StandardType != "Table")
                CollectThsRecursive(child, result, isRoot: false);
        }
    }

    /// <summary>Returns true when any TH in <paramref name="ths"/> has a /Scope attribute.</summary>
    private static bool AnyThHasScope(PreflightContext context, List<StructureTreeNode> ths)
    {
        foreach (var th in ths)
        {
            if (HasScopeAttribute(context, th))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Returns true when the node's /A entry contains a Table-owner attribute dict with a non-null /Scope.
    /// Handles both single-dict and array-of-dicts forms of /A, per ISO 32000-1 §14.7.5.
    /// </summary>
    private static bool HasScopeAttribute(PreflightContext context, StructureTreeNode node)
    {
        var aObj = node.Dict.Get(_a);
        if (aObj is null)
            return false;

        var aResolved = context.Resolve(aObj);
        if (aResolved is PdfDictionary singleDict)
            return IsScopedTableDict(context, singleDict);

        if (aResolved is PdfArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var elem = context.Resolve(arr[i]);
                if (elem is PdfDictionary dict && IsScopedTableDict(context, dict))
                    return true;
                // integer revision-number entries in /A arrays are skipped silently
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true when <paramref name="dict"/> is a Table-owner attribute dict with a non-null /Scope.
    /// </summary>
    private static bool IsScopedTableDict(PreflightContext context, PdfDictionary dict)
    {
        var owner = (context.Resolve(dict.Get(_o)) as PdfName)?.Value;
        if (owner != "Table")
            return false;
        // Any non-null /Scope value satisfies (Column, Row, Both — or even an unusual name).
        return context.Resolve(dict.Get(_scope)) is not null;
    }

    /// <summary>
    /// Returns the list of Latin-1 ID keys from the TD's /A /Headers byte-string array,
    /// or null when the TD has no /Headers attribute (in any of its /A dicts).
    /// Returns an empty list when /Headers is present but contains no entries.
    /// </summary>
    private static List<string>? GetHeadersIds(PreflightContext context, StructureTreeNode tdNode)
    {
        var aObj = tdNode.Dict.Get(_a);
        if (aObj is null)
            return null;

        var aResolved = context.Resolve(aObj);
        if (aResolved is PdfDictionary singleDict)
            return ReadHeadersFromDict(context, singleDict);

        if (aResolved is PdfArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var elem = context.Resolve(arr[i]);
                if (elem is PdfDictionary dict)
                {
                    var result = ReadHeadersFromDict(context, dict);
                    if (result is not null)
                        return result; // use the first Table-owner dict that has /Headers
                }
            }
        }

        return null; // /A present but no Table-owner dict with /Headers
    }

    /// <summary>
    /// Reads /Headers from a single attribute dict (only if /O = Table).
    /// Returns null if this is not a Table-owner dict or has no /Headers.
    /// </summary>
    private static List<string>? ReadHeadersFromDict(PreflightContext context, PdfDictionary dict)
    {
        var owner = (context.Resolve(dict.Get(_o)) as PdfName)?.Value;
        if (owner != "Table")
            return null;

        var headersObj = context.Resolve(dict.Get(_headers));
        if (headersObj is null)
            return null;

        var result = new List<string>();
        if (headersObj is PdfArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var elem = context.Resolve(arr[i]);
                var bytes = GetByteString(context, elem);
                if (bytes is not null)
                    result.Add(System.Text.Encoding.Latin1.GetString(bytes));
            }
        }
        // /Headers could theoretically be a single string, though the spec says array.
        // Handle it defensively to avoid missing a valid single-string case.
        else
        {
            var bytes = GetByteString(context, headersObj);
            if (bytes is not null)
                result.Add(System.Text.Encoding.Latin1.GetString(bytes));
        }

        return result;
    }

    /// <summary>
    /// Extracts raw bytes from a PDF string object (literal or hex string).
    /// Returns null for non-string objects.
    /// </summary>
    private static byte[]? GetByteString(PreflightContext context, PdfObject? obj)
    {
        var resolved = obj is null ? null : context.Resolve(obj);
        return resolved switch
        {
            PdfLiteralString s => s.Bytes.ToArray(),
            PdfHexString s => s.Bytes.ToArray(),
            _ => null,
        };
    }
}
