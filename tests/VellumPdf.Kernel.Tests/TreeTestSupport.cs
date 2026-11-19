// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;
using VellumPdf.Reader;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Builds a one-page PDF whose catalog carries a name or number tree, and walks the tree back out
/// through <see cref="PdfReader"/>. The tree is attached to the catalog here, in the test, because the
/// writers are internal and no document member wires them in yet.
/// </summary>
internal static class TreeTestSupport
{
    /// <summary>What a walk of one tree found, and how it was shaped.</summary>
    internal sealed record Walked(List<(object Key, PdfObject Value)> Entries, int MaxLeafDepth, int MinLeafDepth, int Nodes, int Leaves, int MaxPairsInLeaf, int MaxKidsInNode, int RootKids);

    /// <summary>
    /// Writes a PDF whose catalog holds the tree rooted at <paramref name="root"/> under
    /// <c>/Names /Dests</c> (name tree) or <c>/PageLabels</c> (number tree). <paramref name="registry"/>
    /// must be the one the tree writer registered its nodes in.
    /// </summary>
    internal static byte[] BuildPdf(PdfObjectRegistry registry, PdfDictionary root, bool nameTree)
    {
        var pageRef = registry.Reserve();
        var pagesRef = registry.Reserve();
        var catalogRef = registry.Reserve();
        registry.SetValue(pageRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page)
            .Set(new PdfName("Parent"), pagesRef)
            .Set(new PdfName("Resources"), new PdfDictionary())
            .Set(new PdfName("MediaBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(200), new PdfInteger(200)])));
        registry.SetValue(pagesRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, new PdfArray([pageRef]))
            .Set(PdfName.Count, 1L));
        var catalog = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pagesRef);
        if (nameTree)
            catalog.Set(new PdfName("Names"), new PdfDictionary().Set(new PdfName("Dests"), root));
        else
            catalog.Set(new PdfName("PageLabels"), root);
        registry.SetValue(catalogRef, catalog);

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteAscii("%PDF-1.7\n"u8);
        var xref = new CrossReferenceBuilder();
        registry.WriteAll(writer, xref);
        xref.WriteXrefAndTrailer(writer, catalogRef, null);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes a PDF with one 200 x 200 page per entry of <paramref name="pageRefs"/>, whose catalog holds
    /// <paramref name="destsRoot"/> under <c>/Names /Dests</c> and <paramref name="labelsRoot"/> under
    /// <c>/PageLabels</c>, each when not <see langword="null"/>. The pages are reserved by the caller, before
    /// the tables are built, because a destination's array names its page; <see cref="BuildPdf"/> reserves its
    /// one page after the tree and so cannot carry destinations.
    /// </summary>
    internal static byte[] BuildPdfWithPages(PdfObjectRegistry registry, IReadOnlyList<PdfIndirectReference> pageRefs, PdfDictionary? destsRoot, PdfDictionary? labelsRoot)
    {
        var pagesRef = registry.Reserve();
        var catalogRef = registry.Reserve();
        foreach (var pageRef in pageRefs)
        {
            registry.SetValue(pageRef, new PdfDictionary()
                .Set(PdfName.Type, PdfName.Page)
                .Set(new PdfName("Parent"), pagesRef)
                .Set(new PdfName("Resources"), new PdfDictionary())
                .Set(new PdfName("MediaBox"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(200), new PdfInteger(200)])));
        }
        registry.SetValue(pagesRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, new PdfArray([.. pageRefs]))
            .Set(PdfName.Count, pageRefs.Count));
        var catalog = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pagesRef);
        if (destsRoot is not null)
            catalog.Set(new PdfName("Names"), new PdfDictionary().Set(new PdfName("Dests"), destsRoot));
        if (labelsRoot is not null)
            catalog.Set(new PdfName("PageLabels"), labelsRoot);
        registry.SetValue(catalogRef, catalog);

        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteAscii("%PDF-1.7\n"u8);
        var xref = new CrossReferenceBuilder();
        registry.WriteAll(writer, xref);
        xref.WriteXrefAndTrailer(writer, catalogRef, null);
        return ms.ToArray();
    }

    /// <summary>Reads the tree root out of a PDF made by <see cref="BuildPdf"/>.</summary>
    internal static (PdfDocumentReader Reader, PdfDictionary Root) OpenRoot(byte[] pdf, bool nameTree)
    {
        var reader = PdfReader.Open(pdf);
        var catalog = reader.Catalog;
        PdfObject? root = nameTree
            ? ((PdfDictionary)reader.ResolveValue(catalog.Get(new PdfName("Names"))!)!).Get(new PdfName("Dests"))
            : catalog.Get(new PdfName("PageLabels"));
        return (reader, (PdfDictionary)reader.ResolveValue(root!)!);
    }

    /// <summary>
    /// Walks <paramref name="root"/> through /Kids and /Names (/Nums), asserting every structural rule
    /// the clauses state: one of Kids or entries on the root and no Limits there, Limits on every other node and
    /// equal to the first and last key actually found below it, kids as references, keys strictly
    /// increasing across the whole walk (so leaf ranges are contiguous and disjoint), all leaves at one depth.
    /// </summary>
    internal static Walked Walk(PdfDocumentReader reader, PdfDictionary root, bool nameTree)
    {
        var entries = new List<(object, PdfObject)>();
        var leafDepths = new List<int>();
        var stats = new int[4]; // nodes, leaves, maxPairs, maxKids
        var entriesKey = new PdfName(nameTree ? "Names" : "Nums");
        var kidsKey = new PdfName("Kids");
        var limitsKey = new PdfName("Limits");

        (object First, object Last)? Visit(PdfDictionary node, int depth, bool isRoot)
        {
            stats[0]++;
            var hasKids = node.Get(kidsKey) is not null;
            var hasEntries = node.Get(entriesKey) is not null;
            Assert.True(hasKids ^ hasEntries, "A node holds Kids or the entries array, never both and never neither.");
            if (isRoot)
                Assert.Null(node.Get(limitsKey));
            else
                Assert.NotNull(node.Get(limitsKey));

            (object First, object Last)? range = null;
            if (hasEntries)
            {
                stats[1]++;
                leafDepths.Add(depth);
                var array = (PdfArray)reader.ResolveValue(node.Get(entriesKey)!)!;
                Assert.Equal(0, array.Count % 2);
                stats[2] = Math.Max(stats[2], array.Count / 2);
                for (var i = 0; i < array.Count; i += 2)
                {
                    var key = KeyOf(array[i], nameTree);
                    var value = array[i + 1];
                    if (entries.Count > 0)
                        Assert.True(Compare(entries[^1].Item1, key, nameTree) < 0, "Keys are strictly increasing across the walk.");
                    entries.Add((key, value));
                    range = (range?.First ?? key, key);
                }
            }
            else
            {
                var kids = (PdfArray)reader.ResolveValue(node.Get(kidsKey)!)!;
                Assert.True(kids.Count > 0);
                stats[3] = Math.Max(stats[3], kids.Count);
                for (var i = 0; i < kids.Count; i++)
                {
                    Assert.IsType<PdfIndirectReference>(kids[i]);
                    var child = (PdfDictionary)reader.ResolveValue(kids[i])!;
                    var childRange = Visit(child, depth + 1, isRoot: false)!.Value;
                    range = (range?.First ?? childRange.First, childRange.Last);
                }
            }

            if (!isRoot)
            {
                var limits = (PdfArray)reader.ResolveValue(node.Get(limitsKey)!)!;
                Assert.Equal(2, limits.Count);
                Assert.Equal(0, Compare(KeyOf(limits[0], nameTree), range!.Value.First, nameTree));
                Assert.Equal(0, Compare(KeyOf(limits[1], nameTree), range!.Value.Last, nameTree));
            }
            return range;
        }

        Visit(root, 0, isRoot: true);
        var rootKids = root.Get(kidsKey) is { } k ? ((PdfArray)reader.ResolveValue(k)!).Count : 0;
        return new Walked(entries, leafDepths.Max(), leafDepths.Min(), stats[0], stats[1], stats[2], stats[3], rootKids);
    }

    internal static object KeyOf(PdfObject key, bool nameTree)
    {
        if (!nameTree)
            return Assert.IsType<PdfInteger>(key).Value;
        return key switch
        {
            PdfLiteralString s => s.Bytes.ToArray(),
            PdfHexString h => h.Bytes.ToArray(),
            _ => throw new InvalidOperationException($"A name-tree key must be a string, found {key.GetType().Name}."),
        };
    }

    /// <summary>
    /// The reference ordering the clauses define: unsigned bytes compared one at a time, a prefix before
    /// its extension; integers numerically. Written as a plain loop so it does not share an implementation
    /// with the writer.
    /// </summary>
    internal static int Compare(object a, object b, bool nameTree)
    {
        if (!nameTree)
            return ((long)a).CompareTo((long)b);
        var x = (byte[])a;
        var y = (byte[])b;
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            if (x[i] != y[i])
                return x[i] < y[i] ? -1 : 1;
        }
        return x.Length.CompareTo(y.Length);
    }
}
