// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.IO.Linearization;

/// <summary>
/// Computes the linearized object numbering and write order from a fully-populated
/// <see cref="PdfObjectRegistry"/>. See ISO 32000-2 Annex F.
/// </summary>
internal static class LinearizedLayoutPlanner
{
    /// <summary>
    /// Produces a <see cref="LinearizedLayout"/> that partitions the object graph into
    /// a first-page section and a rest section, each with contiguous new object numbers.
    ///
    /// Numbering scheme (matching the qpdf convention):
    ///   1 .. restCount          — rest objects (written first in the file)
    ///   restCount+1 .. total-1  — first-page section: lin dict, catalog, hint-stream placeholder,
    ///                             then first-page objects in reachability order
    ///
    /// Two extra object numbers are reserved at the TOP of the first-page block:
    ///   linDictObjNum  — the linearization parameter dictionary
    ///   hintStreamObjNum — the placeholder hint stream (Step 1: minimal empty stream)
    /// </summary>
    public static LinearizedLayout Plan(
        PdfObjectRegistry registry,
        PdfIndirectReference catalogRef,
        PdfIndirectReference pageTreeRef,
        PdfIndirectReference[] pageDictRefs,
        PdfIndirectReference[] pageContentRefs,
        PdfIndirectReference? infoRef,
        PdfIndirectReference? metadataRef)
    {
        if (pageDictRefs.Length == 0)
            throw new InvalidOperationException("Cannot linearize a document with no pages.");

        // Build a lookup: old object number → value, for BFS traversal.
        var allObjects = new Dictionary<int, PdfObject>();
        foreach (var (n, v) in registry.Entries())
            allObjects[n] = v;

        // ── Compute per-page reachable sets ─────────────────────────────────────
        // We need to know which objects are reachable from each page so we can
        // find shared objects (reachable from >1 page).
        //
        // Every page dict is reachable from every other via the shared page tree
        // (page /Parent → page tree → /Kids → all pages) and via cross-page links.
        // Treat the other pages' dicts as boundaries so a page's reachable set is
        // its own objects plus shared objects — not the whole document.
        var otherPageDicts = new HashSet<int>(pageDictRefs.Select(r => r.ObjectNumber));

        var pageReachable = new List<HashSet<int>>(pageDictRefs.Length);
        for (var p = 0; p < pageDictRefs.Length; p++)
        {
            var reachable = new HashSet<int>();
            BfsFromPage(pageDictRefs[p].ObjectNumber, allObjects, reachable, otherPageDicts);
            pageReachable.Add(reachable);
        }

        // ── First-page reachable set ─────────────────────────────────────────────
        // Always include catalog, page tree root, info, metadata.
        var firstPageSet = new HashSet<int>(pageReachable[0]);
        if (infoRef is not null) firstPageSet.Add(infoRef.ObjectNumber);
        if (metadataRef is not null) firstPageSet.Add(metadataRef.ObjectNumber);
        firstPageSet.Add(catalogRef.ObjectNumber);
        firstPageSet.Add(pageTreeRef.ObjectNumber);

        // Shared objects: reachable from page 0 AND at least one other page.
        // These stay in the first-page section per the spec (they benefit the first-page render).
        // Objects only reachable from pages 1+ go into the rest section.

        // Rest set: objects reachable from pages 1+ but NOT in the first-page set.
        var restSet = new HashSet<int>();
        for (var p = 1; p < pageDictRefs.Length; p++)
        {
            foreach (var n in pageReachable[p])
            {
                if (!firstPageSet.Contains(n))
                    restSet.Add(n);
            }
        }

        // Every object not in either set (e.g. AcroForm root, outlines) goes into rest.
        foreach (var n in allObjects.Keys)
        {
            if (!firstPageSet.Contains(n) && !restSet.Contains(n))
                restSet.Add(n);
        }

        // ── Assign new object numbers ────────────────────────────────────────────
        // Rest objects: 1 .. restCount (sorted for determinism).
        var restObjNums = restSet.OrderBy(n => n).ToList();
        var restCount = restObjNums.Count;

        // First-page section starts at restCount + 1.
        // Layout: [linDict] [catalog] [hintStream] [first-page objects in BFS order]
        var fpObjNums = firstPageSet.OrderBy(n => n).ToList();

        // Prioritise order: catalog first, then first-page page dict, then others.
        // Put catalog right after the lin dict and hint stream so the reader finds it fast.
        var fpOrdered = new List<int>();
        if (firstPageSet.Contains(catalogRef.ObjectNumber))
            fpOrdered.Add(catalogRef.ObjectNumber);
        if (firstPageSet.Contains(pageDictRefs[0].ObjectNumber) && !fpOrdered.Contains(pageDictRefs[0].ObjectNumber))
            fpOrdered.Add(pageDictRefs[0].ObjectNumber);
        if (firstPageSet.Contains(pageContentRefs[0].ObjectNumber) && !fpOrdered.Contains(pageContentRefs[0].ObjectNumber))
            fpOrdered.Add(pageContentRefs[0].ObjectNumber);
        foreach (var n in fpObjNums)
        {
            if (!fpOrdered.Contains(n))
                fpOrdered.Add(n);
        }

        // New object numbers:
        //   1..restCount                rest objects
        //   restCount+1                 lin dict placeholder
        //   restCount+2                 hint stream placeholder
        //   restCount+3 .. restCount+2+fpOrdered.Count   first-page objects
        var linDictObjNum = restCount + 1;
        var hintStreamObjNum = restCount + 2;

        var oldToNew = new Dictionary<int, int>();
        for (var i = 0; i < restObjNums.Count; i++)
            oldToNew[restObjNums[i]] = i + 1;
        for (var i = 0; i < fpOrdered.Count; i++)
            oldToNew[fpOrdered[i]] = restCount + 3 + i;

        var totalSize = restCount + 2 + fpOrdered.Count + 1; // +1 for object 0 free-head

        // ── Apply remap to all objects ───────────────────────────────────────────
        // Streams: Remap mutates the dictionary in place and returns the same stream.
        // Non-streams (dicts, arrays): Remap returns a new copy with updated references.
        var remapped = new Dictionary<int, PdfObject>();
        foreach (var (oldNum, value) in allObjects)
            remapped[oldNum] = PdfObjectRemapper.Remap(value, oldToNew);

        // ── Build ordered output lists ───────────────────────────────────────────
        var restObjects = new List<(int, PdfObject)>(restObjNums.Count);
        foreach (var oldNum in restObjNums)
            restObjects.Add((oldToNew[oldNum], remapped[oldNum]));

        // lin dict and hint stream are synthetic — added by the caller.
        var firstPageObjects = new List<(int, PdfObject)>(fpOrdered.Count);
        foreach (var oldNum in fpOrdered)
            firstPageObjects.Add((oldToNew[oldNum], remapped[oldNum]));

        var catalogObjNum = oldToNew[catalogRef.ObjectNumber];
        var firstPageObjNum = oldToNew[pageDictRefs[0].ObjectNumber];

        // ── Per-page object groups (for Step 2 hint tables) ──────────────────────
        var pageGroups = new List<IReadOnlyList<int>>(pageDictRefs.Length);
        for (var p = 0; p < pageDictRefs.Length; p++)
        {
            var group = new List<int>();
            foreach (var oldNum in pageReachable[p])
            {
                if (oldToNew.TryGetValue(oldNum, out var newNum))
                    group.Add(newNum);
            }
            group.Sort();
            pageGroups.Add(group);
        }

        return new LinearizedLayout(
            oldToNew,
            restObjects,
            firstPageObjects,
            linDictObjNum,
            hintStreamObjNum,
            catalogObjNum,
            firstPageObjNum,
            totalSize,
            pageGroups);
    }

    // BFS over the object graph starting from a single root object number,
    // collecting all reachable object numbers into <paramref name="visited"/>.
    // References into <paramref name="otherPageDicts"/> (any page dict other than the
    // root) are not followed, so the traversal stops at the page-tree boundary instead
    // of fanning out into every other page.
    private static void BfsFromPage(
        int rootObjNum,
        IReadOnlyDictionary<int, PdfObject> allObjects,
        HashSet<int> visited,
        HashSet<int> otherPageDicts)
    {
        var queue = new Queue<int>();
        if (visited.Add(rootObjNum))
            queue.Enqueue(rootObjNum);

        while (queue.Count > 0)
        {
            var num = queue.Dequeue();
            if (!allObjects.TryGetValue(num, out var obj))
                continue;

            foreach (var refNum in CollectRefs(obj))
            {
                // Never descend into a different page's dict — that path leads back
                // through the page tree to the entire document.
                if (refNum != rootObjNum && otherPageDicts.Contains(refNum))
                    continue;
                if (visited.Add(refNum))
                    queue.Enqueue(refNum);
            }
        }
    }

    // Yields all indirect reference object numbers directly contained in obj
    // (one level deep — BFS handles the recursion).
    private static IEnumerable<int> CollectRefs(PdfObject obj)
    {
        return obj switch
        {
            PdfIndirectReference r => [r.ObjectNumber],
            PdfDictionary d => d.Entries.SelectMany(kv => CollectRefs(kv.Value)),
            PdfArray a => Enumerable.Range(0, a.Count).SelectMany(i => CollectRefs(a[i])),
            PdfStream s => s.Dictionary.Entries.SelectMany(kv => CollectRefs(kv.Value)),
            _ => [],
        };
    }
}
