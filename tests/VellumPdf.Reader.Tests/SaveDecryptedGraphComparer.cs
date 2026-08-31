// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Deep-compares two documents' object graphs by following references — not by object number,
/// since the two sides being compared are usually different files (a <c>SaveDecrypted</c> output
/// reopened against either the source document it came from, or an independent baseline file), and
/// #186's own object-stream and linearized fixtures legitimately renumber between the two. A
/// visited-set of (leftNumber, rightNumber) pairs guards cycles; stream bodies are compared decoded,
/// since the two sides can legitimately use different filter representations for identical content.
/// </summary>
internal static class SaveDecryptedGraphComparer
{
    // Below this many leaf-value comparisons, AssertCatalogsEqual refuses to report success — a
    // comparison of two (near-)empty graphs, or one that was accidentally handed the same reader
    // twice, or a comparer bug that stops descending early, all "pass" with zero actual content
    // checked otherwise (review round 2, defect 4). Set well under the smallest real fixture's
    // count (in the dozens), not tuned to any specific one.
    private const int MinimumComparedLeafCount = 5;

    private sealed class ComparisonState
    {
        public int ComparedLeaves;
    }

    /// <summary>
    /// Compares the two documents' catalogs (and everything reachable from them), plus — when
    /// <paramref name="compareTrailer"/> is left at its default — the trailer's <c>/Info</c> and
    /// <c>/ID</c>. <c>/Info</c> and <c>/ID</c> are compared by VALUE, not merely by presence: a
    /// comparison rooted only at <c>/Root</c> cannot see either, since neither is reachable from the
    /// catalog, so a mutation that garbled every <c>/Info</c> string passed every fixture test —
    /// including <c>qpdf --check</c>, which has no opinion on string content — before this existed
    /// (review round 2, defect 4).
    /// <para>
    /// Pass <paramref name="compareTrailer"/> <see langword="false"/> when <paramref name="right"/>
    /// is not actually derived from <paramref name="left"/> — comparing against an independent
    /// baseline file, say, where <c>/ID</c> legitimately differs (a fresh, unrelated file has its own
    /// random ID) even though the reachable content matches.
    /// </para>
    /// </summary>
    public static void AssertCatalogsEqual(PdfDocumentReader left, PdfDocumentReader right, bool compareTrailer = true)
    {
        var visited = new HashSet<(int, int)>();
        var state = new ComparisonState();
        AssertValuesEqual(left, left.Catalog, right, right.Catalog, visited, "/Root", state);

        if (compareTrailer)
            AssertTrailerInfoAndIdEqual(left, right, visited, state);

        Assert.True(
            state.ComparedLeaves >= MinimumComparedLeafCount,
            $"only {state.ComparedLeaves} leaf value(s) were actually compared — this comparison may "
            + "be hollow (near-empty graphs, or the same reader passed on both sides).");
    }

    private static void AssertTrailerInfoAndIdEqual(
        PdfDocumentReader leftReader, PdfDocumentReader rightReader, HashSet<(int, int)> visited, ComparisonState state)
    {
        var infoKey = new PdfName("Info");
        var leftInfo = leftReader.Trailer.Get(infoKey);
        var rightInfo = rightReader.Trailer.Get(infoKey);
        if (leftInfo is not null || rightInfo is not null)
            AssertValuesEqual(leftReader, leftInfo, rightReader, rightInfo, visited, "/Info", state);

        // ISO 32000-2 §7.6.2: /ID is never encrypted, and SaveDecrypted carries it forward verbatim
        // when present as an array — so for a source-vs-its-own-output comparison this must match
        // exactly, both elements, not merely be present on both sides.
        var idKey = PdfName.ID;
        var leftId = leftReader.Trailer.Get(idKey);
        var rightId = rightReader.Trailer.Get(idKey);
        if (leftId is PdfArray || rightId is PdfArray)
            AssertValuesEqual(leftReader, leftId, rightReader, rightId, visited, "/ID", state);
    }

    /// <summary>Compares two arbitrary reachable values (used for a single named sub-graph).</summary>
    private static void AssertValuesEqual(
        PdfDocumentReader leftReader, PdfObject? left,
        PdfDocumentReader rightReader, PdfObject? right,
        HashSet<(int, int)> visited, string path, ComparisonState state)
    {
        if (left is PdfIndirectReference leftRef && right is PdfIndirectReference rightRef)
        {
            var key = (leftRef.ObjectNumber, rightRef.ObjectNumber);
            if (!visited.Add(key))
                return; // Already comparing (or compared) this pair — cycle guard.

            var leftStream = leftReader.ResolveStream(leftRef);
            var rightStream = rightReader.ResolveStream(rightRef);
            if (leftStream is not null || rightStream is not null)
            {
                Assert.True(
                    leftStream is not null && rightStream is not null,
                    $"{path}: one side is a stream and the other is not ({leftRef} vs {rightRef}).");

                var leftBytes = leftReader.GetDecodedStreamData(leftStream!)
                    ?? leftReader.DecryptedStreamView(leftStream!).RawBody.ToArray();
                var rightBytes = rightReader.GetDecodedStreamData(rightStream!)
                    ?? rightReader.DecryptedStreamView(rightStream!).RawBody.ToArray();
                Assert.True(
                    leftBytes.AsSpan().SequenceEqual(rightBytes),
                    $"{path}: stream body mismatch ({leftRef} vs {rightRef}).");
                state.ComparedLeaves++;

                AssertValuesEqual(
                    leftReader, leftStream!.Dictionary, rightReader, rightStream!.Dictionary, visited, path, state);
                return;
            }

            AssertValuesEqual(
                leftReader, leftReader.ResolveValue(leftRef),
                rightReader, rightReader.ResolveValue(rightRef),
                visited, path, state);
            return;
        }

        if (left is PdfIndirectReference onlyLeftRef)
        {
            AssertValuesEqual(leftReader, leftReader.ResolveValue(onlyLeftRef), rightReader, right, visited, path, state);
            return;
        }

        if (right is PdfIndirectReference onlyRightRef)
        {
            AssertValuesEqual(leftReader, left, rightReader, rightReader.ResolveValue(onlyRightRef), visited, path, state);
            return;
        }

        switch (left)
        {
            case null:
            case PdfNull:
                Assert.True(right is null or PdfNull, $"{path}: expected null, got {Describe(right)}.");
                return;

            case PdfBoolean lb:
                var rb = Assert.IsType<PdfBoolean>(right);
                Assert.True(lb.Value == rb.Value, $"{path}: boolean mismatch.");
                state.ComparedLeaves++;
                return;

            case PdfInteger li:
                if (right is PdfReal riReal)
                    Assert.Equal(li.Value, riReal.Value, 0.0000001);
                else
                    Assert.Equal(li.Value, Assert.IsType<PdfInteger>(right).Value);
                state.ComparedLeaves++;
                return;

            case PdfReal lr:
                var rNum = right switch
                {
                    PdfReal rr => rr.Value,
                    PdfInteger ri => (double)ri.Value,
                    _ => throw new Xunit.Sdk.XunitException($"{path}: expected a number, got {Describe(right)}."),
                };
                Assert.Equal(lr.Value, rNum, 0.0000001);
                state.ComparedLeaves++;
                return;

            case PdfName ln:
                var rn = Assert.IsType<PdfName>(right);
                Assert.True(ln.Equals(rn), $"{path}: name mismatch (/{ln.Value} vs /{rn.Value}).");
                state.ComparedLeaves++;
                return;

            case PdfLiteralString or PdfHexString:
                var leftStringBytes = StringBytes(left);
                var rightStringBytes = right is PdfLiteralString or PdfHexString
                    ? StringBytes(right)
                    : throw new Xunit.Sdk.XunitException($"{path}: expected a string, got {Describe(right)}.");
                Assert.True(
                    leftStringBytes.Span.SequenceEqual(rightStringBytes.Span),
                    $"{path}: string content mismatch.");
                state.ComparedLeaves++;
                return;

            case PdfArray la:
                var ra = Assert.IsType<PdfArray>(right);
                Assert.True(la.Count == ra.Count, $"{path}: array length mismatch ({la.Count} vs {ra.Count}).");
                for (var i = 0; i < la.Count; i++)
                    AssertValuesEqual(leftReader, la[i], rightReader, ra[i], visited, $"{path}[{i}]", state);
                return;

            case PdfDictionary ld:
                var rd = Assert.IsType<PdfDictionary>(right);
                AssertDictionariesEqual(leftReader, ld, rightReader, rd, visited, path, state);
                return;

            default:
                throw new Xunit.Sdk.XunitException($"{path}: unhandled object type {left.GetType().Name}.");
        }
    }

    private static void AssertDictionariesEqual(
        PdfDocumentReader leftReader, PdfDictionary left,
        PdfDocumentReader rightReader, PdfDictionary right,
        HashSet<(int, int)> visited, string path, ComparisonState state)
    {
        // These four are compression/encoding metadata, not content: a rewrite (or an independent
        // producer like qpdf, when the comparison target is plaintext-baseline.pdf rather than the
        // source document itself) is free to represent identical decoded bytes at a different
        // encoded length or filter chain — e.g. qpdf recompressing a stream the baseline left
        // uncompressed. The stream-body comparison above already decodes past this.
        var excludedKeys = new HashSet<string>(["Length", "Filter", "DecodeParms", "DP"], StringComparer.Ordinal);
        var leftKeys = left.Entries.Select(kv => kv.Key.Value).Where(k => !excludedKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var rightKeys = right.Entries.Select(kv => kv.Key.Value).Where(k => !excludedKeys.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            leftKeys.SequenceEqual(rightKeys),
            $"{path}: key set mismatch. left=[{string.Join(",", leftKeys)}] right=[{string.Join(",", rightKeys)}]");

        foreach (var key in leftKeys)
        {
            var name = new PdfName(key);
            AssertValuesEqual(leftReader, left.Get(name), rightReader, right.Get(name), visited, $"{path}/{key}", state);
        }
    }

    private static ReadOnlyMemory<byte> StringBytes(PdfObject obj) => obj switch
    {
        PdfLiteralString s => s.Bytes,
        PdfHexString h => h.Bytes,
        _ => throw new ArgumentException("Not a PDF string.", nameof(obj)),
    };

    private static string Describe(PdfObject? obj) => obj?.GetType().Name ?? "null";
}
