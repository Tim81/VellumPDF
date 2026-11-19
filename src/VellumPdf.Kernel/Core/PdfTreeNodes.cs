// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// The node layout that <see cref="NameTreeWriter"/> and <see cref="NumberTreeWriter"/> share. The two
/// trees differ only in key type, the name of the leaf array (<c>/Names</c> or <c>/Nums</c>) and how a
/// key is compared and validated; everything structural lives here so a rule is written once.
/// </summary>
/// <remarks>
/// <para>The rules this type enforces come from ISO 32000-2:2020, 7.9.6 (name trees) and 7.9.7 (number
/// trees), which restate ISO 32000-1:2008, 7.9.6 and 7.9.7 with one difference noted below:</para>
/// <list type="bullet">
/// <item>The root node "shall contain a single entry: either Kids or Names but not both". A root with
/// <c>/Names</c> "shall be the only node in the tree".</item>
/// <item>Every non-root node carries <c>/Limits</c>, the least and greatest key of the leaves below it;
/// leaves carry the entries array, intermediate nodes carry <c>/Kids</c>.</item>
/// <item>The root carries no <c>/Limits</c>. Table 36 of ISO 32000-2 says "not permitted in root nodes".
/// Table 36 of ISO 32000-1 says "Intermediate and leaf nodes only; required", and Table 37 of both
/// editions says "present in Intermediate and leaf nodes only". The editions agree on the rule; the later
/// one states the prohibition outright for name trees, and this type satisfies both by never writing it.</item>
/// <item>The key ranges of the leaves "shall not overlap; each Names entry shall contain a single
/// contiguous range of all the keys in the tree", so the sorted entries are cut into consecutive slices.</item>
/// </list>
/// <para><b>Shape.</b> Up to <see cref="MaxEntriesPerLeaf"/> entries are written as a root that holds
/// the entries array itself. More than that are cut into the fewest leaves of at most
/// <see cref="MaxEntriesPerLeaf"/> entries each, with the entries spread so leaf sizes differ by at most
/// one (no stub leaf at the end). Nodes are then grouped into parents of at most
/// <see cref="MaxKidsPerNode"/> kids the same way, level by level, until one level fits under the
/// root. Every leaf therefore sits at the same depth, and a node's array never exceeds 128 objects (64
/// key-value pairs) or 64 references. The figure 64 is a choice, not a clause: neither edition prescribes a
/// fan-out. It keeps one node small enough to parse without reading much a lookup does not need, and
/// depth grows with the base-64 logarithm of the entry count, so 100,000 entries need a root, one
/// intermediate level and the leaves.</para>
/// <para>The root is not registered: <see cref="Build"/> returns it so the caller decides whether it
/// lives inline in the catalog or in an indirect object of its own. Every other node is registered and
/// referenced from its parent, as Table 36 requires ("an array of indirect references").</para>
/// </remarks>
internal static class PdfTreeNodes
{
    /// <summary>The most key-value pairs a leaf, or a root that is itself the only node, holds.</summary>
    internal const int MaxEntriesPerLeaf = 64;

    /// <summary>The most children an intermediate node or the root holds.</summary>
    internal const int MaxKidsPerNode = 64;

    private static readonly PdfName LimitsName = new("Limits");

    private readonly record struct Node(PdfIndirectReference Reference, PdfObject First, PdfObject Last);

    /// <summary>
    /// Lays out <paramref name="keys"/> and <paramref name="values"/> (parallel, already sorted in the
    /// tree's key order with no duplicates) as a tree and returns its root.
    /// </summary>
    /// <param name="keys">The serialised keys; the first and last of each subtree are reused, not copied, as that subtree's <c>/Limits</c>.</param>
    /// <param name="values">The value for each key.</param>
    /// <param name="entriesKey"><c>Names</c> or <c>Nums</c>.</param>
    /// <param name="registry">Receives every non-root node.</param>
    internal static PdfDictionary Build(PdfObject[] keys, PdfObject[] values, PdfName entriesKey, PdfObjectRegistry registry)
    {
        var count = keys.Length;
        if (count <= MaxEntriesPerLeaf)
            return new PdfDictionary().Set(entriesKey, Interleave(keys, values, 0, count));

        var leafCount = CeilDiv(count, MaxEntriesPerLeaf);
        var level = new List<Node>(leafCount);
        for (var i = 0; i < leafCount; i++)
        {
            var start = (int)((long)i * count / leafCount);
            var end = (int)((long)(i + 1) * count / leafCount);
            var first = keys[start];
            var last = keys[end - 1];
            var leaf = new PdfDictionary()
                .Set(LimitsName, new PdfArray([first, last]))
                .Set(entriesKey, Interleave(keys, values, start, end));
            level.Add(new Node(registry.Add(leaf), first, last));
        }

        while (level.Count > MaxKidsPerNode)
        {
            var parentCount = CeilDiv(level.Count, MaxKidsPerNode);
            var next = new List<Node>(parentCount);
            for (var i = 0; i < parentCount; i++)
            {
                var start = (int)((long)i * level.Count / parentCount);
                var end = (int)((long)(i + 1) * level.Count / parentCount);
                var first = level[start].First;
                var last = level[end - 1].Last;
                var parent = new PdfDictionary()
                    .Set(LimitsName, new PdfArray([first, last]))
                    .Set(PdfName.Kids, KidsArray(level, start, end));
                next.Add(new Node(registry.Add(parent), first, last));
            }
            level = next;
        }

        return new PdfDictionary().Set(PdfName.Kids, KidsArray(level, 0, level.Count));
    }

    private static int CeilDiv(int value, int divisor) => (value + divisor - 1) / divisor;

    private static PdfArray Interleave(PdfObject[] keys, PdfObject[] values, int start, int end)
    {
        var array = new PdfArray();
        for (var i = start; i < end; i++)
            array.Add(keys[i]).Add(values[i]);
        return array;
    }

    private static PdfArray KidsArray(List<Node> nodes, int start, int end)
    {
        var array = new PdfArray();
        for (var i = start; i < end; i++)
            array.Add(nodes[i].Reference);
        return array;
    }

    /// <summary>
    /// Refuses a value a name or number tree entry cannot hold. A null value is refused outright. A
    /// stream must be indirect (ISO 32000-2, 7.9.6: "Stream objects shall be specified by indirect object
    /// references"), so a <see cref="PdfStream"/> passed directly is refused too; pass the reference the
    /// registry returned for it. A <see cref="PdfIndirectObject"/> is the "N G obj … endobj" wrapper, which
    /// cannot be written inside an array, so it is refused as well.
    /// </summary>
    internal static void ValidateValue(PdfObject? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value is PdfStream)
            throw new ArgumentException(
                "A stream value must be passed as an indirect reference: ISO 32000-2, 7.9.6 requires stream objects in a tree to be indirect.",
                parameterName);
        if (value is PdfIndirectObject)
            throw new ArgumentException(
                "A PdfIndirectObject wrapper cannot be written inside a tree node; pass its Reference instead.",
                parameterName);
    }
}
