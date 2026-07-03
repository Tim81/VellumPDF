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
        PdfIndirectReference? metadataRef,
        PdfIndirectReference? outlinesRef = null,
        bool outlinesInFirstPage = false)
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

        // Catalog BFS: discover all objects reachable from the catalog (AcroForm root, /DR
        // fonts, form-field widgets and their appearance streams, non-terminal field nodes,
        // metadata, outlines root and items) stopping at every page dict boundary.
        var catalogReachable = new HashSet<int>();
        BfsFromPage(catalogRef.ObjectNumber, allObjects, catalogReachable, otherPageDicts);

        // Document-level objects: everything the catalog reaches except the page dicts and the
        // page tree. qpdf classifies these as part-4 document-level objects, never page-private,
        // even when a page's /Annots array also references them (form-field widgets are reachable
        // both from their page and from the catalog's AcroForm /Fields). They must not be counted
        // in any page's object total or its part-6/part-7 group, or qpdf's recomputed page object
        // count and /E offset will disagree with the hint table. Outline objects are handled
        // separately below (moved into part 6 when /UseOutlines is set).
        var documentLevel = new HashSet<int>(catalogReachable);
        documentLevel.Remove(catalogRef.ObjectNumber);
        documentLevel.Remove(pageTreeRef.ObjectNumber);
        foreach (var r in pageDictRefs) documentLevel.Remove(r.ObjectNumber);

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

        // ── Split the first-page set into document-level (part 4) and page objects (part 6) ─
        // Part 6 is the first page's own objects (its page dict, content, and resources):
        // a BFS from the page dict that also stops at the page tree, so document-level
        // structure is excluded. Part 4 is everything else in the first-page set (catalog,
        // page tree, info, metadata). qpdf places part 4 before the hint stream and part 6
        // after it, and counts only part 6 as the first page's objects.
        var part6Set = new HashSet<int>();
        var part6Boundary = new HashSet<int>(otherPageDicts) { pageTreeRef.ObjectNumber };
        BfsFromPage(pageDictRefs[0].ObjectNumber, allObjects, part6Set, part6Boundary);

        // Drop document-level objects (form-field widgets, appearance streams, /DR fonts) that the
        // BFS pulled in through the first page's /Annots. They belong in part 4, not part 6. The
        // page dict itself is never in documentLevel, so it is retained.
        part6Set.ExceptWith(documentLevel);

        var part6Ordered = new List<int> { pageDictRefs[0].ObjectNumber };
        if (part6Set.Contains(pageContentRefs[0].ObjectNumber))
            part6Ordered.Add(pageContentRefs[0].ObjectNumber);
        foreach (var n in part6Set.OrderBy(n => n))
            if (!part6Ordered.Contains(n))
                part6Ordered.Add(n);

        // ── Split the rest section into per-page private objects and shared objects ─
        // An object reachable from exactly one later page is that page's private object
        // (part 7). One reachable from two or more later pages is shared (part 8). The
        // remainder (reachable from none, e.g. AcroForm root, outlines) trails at the end (part 9).
        var pageCount = pageDictRefs.Length;
        var reachCount = new Dictionary<int, int>();
        foreach (var n in restSet)
        {
            var count = 0;
            for (var p = 1; p < pageCount; p++)
                if (pageReachable[p].Contains(n))
                    count++;
            reachCount[n] = count;
        }

        // Promote every document-level object (catalog-reachable, non-page) into part 4,
        // regardless of how many pages also reference it. A form-field widget on a later page,
        // its appearance streams, and the shared /DR fonts are all reachable from the catalog's
        // AcroForm, so qpdf treats them as document-level rather than that page's private objects.
        // Leaving them in a page-private (part 7) or shared (part 8) slot would inflate the page's
        // recomputed object count and shift /E.
        foreach (var n in documentLevel)
        {
            if (firstPageSet.Contains(n)) continue;  // already in first-page set
            if (!restSet.Contains(n)) continue;       // not in rest set (shouldn't happen)
            firstPageSet.Add(n);
        }

        // Outline group: BFS from the outlines root (all page dicts are hard boundaries so
        // outline /Dest refs don't drag pages into the group). When outlinesInFirstPage is
        // true (the catalog sets /PageMode /UseOutlines), the group is appended to part 6
        // after the first page's own objects. Otherwise the objects remain in part 4 (via
        // the catalog-BFS promotion above), which is correct but untested (VellumPdf always
        // sets /UseOutlines when outlines exist).
        var outlineGroupOld = new HashSet<int>();
        if (outlinesRef is not null)
        {
            var allPageBoundary = new HashSet<int>(pageDictRefs.Select(r => r.ObjectNumber));
            BfsFromPage(outlinesRef.ObjectNumber, allObjects, outlineGroupOld, allPageBoundary);

            if (outlinesInFirstPage)
            {
                // Move outline objects out of part 4 (firstPageSet) and into part 6 (part6Ordered),
                // appended after the first page's own objects. Root first, then items in order.
                var rootNum = outlinesRef.ObjectNumber;
                if (outlineGroupOld.Contains(rootNum) && !part6Ordered.Contains(rootNum))
                    part6Ordered.Add(rootNum);
                foreach (var n in outlineGroupOld.OrderBy(n => n))
                    if (n != rootNum && !part6Ordered.Contains(n))
                        part6Ordered.Add(n);
            }
        }

        // Compute part4Ordered after all promotions and outline placement are settled.
        // Catalog comes first; then remaining first-page-set objects that are not in part 6.
        var part4Ordered = new List<int>();
        if (firstPageSet.Contains(catalogRef.ObjectNumber))
            part4Ordered.Add(catalogRef.ObjectNumber);
        foreach (var n in firstPageSet.OrderBy(n => n))
            if (!part6Set.Contains(n) && !part6Ordered.Contains(n) && !part4Ordered.Contains(n))
                part4Ordered.Add(n);

        // A page's own dict and content stream are always private to that page, never shared,
        // so every page has at least its page object in its hint group (no zero-object pages).
        // This rests on each page having a distinct content-stream and dict object (reserved
        // per page in PdfDocument.Save); if that ever changes, revisit the part-8 classification.
        var pageOwned = new HashSet<int>();
        for (var p = 1; p < pageCount; p++)
        {
            pageOwned.Add(pageDictRefs[p].ObjectNumber);
            pageOwned.Add(pageContentRefs[p].ObjectNumber);
        }

        var pagePrivateOld = new List<List<int>>(pageCount) { new() }; // index 0 unused (first page)
        for (var p = 1; p < pageCount; p++)
        {
            var dictNum = pageDictRefs[p].ObjectNumber;
            var contentNum = pageContentRefs[p].ObjectNumber;
            // The page object comes first so qpdf measures the page from its page object; its
            // content follows, then any remaining private objects. The dict/content are forced in
            // even if the reachability count would otherwise classify them elsewhere.
            var privSet = restSet
                .Where(n => (reachCount[n] == 1 && pageReachable[p].Contains(n) && !documentLevel.Contains(n)) || n == dictNum || n == contentNum)
                .ToHashSet();
            var priv = new List<int>();
            if (privSet.Contains(dictNum)) priv.Add(dictNum);
            if (privSet.Contains(contentNum) && !priv.Contains(contentNum)) priv.Add(contentNum);
            foreach (var n in privSet.OrderBy(n => n))
                if (!priv.Contains(n))
                    priv.Add(n);
            pagePrivateOld.Add(priv);
        }
        // Shared objects (part 8) exclude document-level objects, which are now in part 4.
        var part8Old = restSet.Where(n => reachCount[n] >= 2 && !pageOwned.Contains(n) && !documentLevel.Contains(n)).OrderBy(n => n).ToList();
        // Exclude objects promoted to firstPageSet (part 4), including all document-level objects.
        var part9Old = restSet.Where(n => reachCount[n] == 0 && !pageOwned.Contains(n) && !firstPageSet.Contains(n)).OrderBy(n => n).ToList();

        // Rest write order: page 1 private, page 2 private, …, shared, then unreferenced.
        var restOrderedOld = new List<int>();
        for (var p = 1; p < pageCount; p++) restOrderedOld.AddRange(pagePrivateOld[p]);
        restOrderedOld.AddRange(part8Old);
        restOrderedOld.AddRange(part9Old);
        var restCount = restOrderedOld.Count;

        // ── Assign new object numbers ────────────────────────────────────────────
        //   1..restCount                       rest objects (page-grouped, then shared)
        //   restCount+1                        lin dict
        //   restCount+2 ..                     part 4 (document level)
        //   restCount+2+part4Count             hint stream
        //   restCount+3+part4Count ..          part 6 (first page's own objects)
        var linDictObjNum = restCount + 1;
        var part4Start = restCount + 2;
        var hintStreamObjNum = part4Start + part4Ordered.Count;
        var part6Start = hintStreamObjNum + 1;

        var oldToNew = new Dictionary<int, int>();
        for (var i = 0; i < restOrderedOld.Count; i++)
            oldToNew[restOrderedOld[i]] = i + 1;
        for (var i = 0; i < part4Ordered.Count; i++)
            oldToNew[part4Ordered[i]] = part4Start + i;
        for (var i = 0; i < part6Ordered.Count; i++)
            oldToNew[part6Ordered[i]] = part6Start + i;

        var totalSize = restCount + part4Ordered.Count + part6Ordered.Count + 3; // lin dict + hint + object 0

        // ── Apply remap to all objects ───────────────────────────────────────────
        // Remap each distinct object instance exactly once, keyed by reference identity.
        // Streams are remapped in place, so if the same instance were registered under two
        // numbers (e.g. a deduplicated image), remapping twice would double-apply the map and
        // corrupt its references. This dedup makes that safe regardless.
        var remapped = new Dictionary<int, PdfObject>();
        var remappedInstances = new Dictionary<PdfObject, PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var (oldNum, value) in allObjects)
        {
            if (!remappedInstances.TryGetValue(value, out var result))
            {
                result = PdfObjectRemapper.Remap(value, oldToNew);
                remappedInstances[value] = result;
            }
            remapped[oldNum] = result;
        }

        var restObjects = restOrderedOld.Select(o => (oldToNew[o], remapped[o])).ToList();
        var part4Objects = part4Ordered.Select(o => (oldToNew[o], remapped[o])).ToList();
        var part6Objects = part6Ordered.Select(o => (oldToNew[o], remapped[o])).ToList();

        var catalogObjNum = oldToNew[catalogRef.ObjectNumber];
        var firstPageObjNum = oldToNew[pageDictRefs[0].ObjectNumber];

        // ── Hint-table groupings ─────────────────────────────────────────────────
        // Page objects: first page = its own objects (part 6); later pages = their private
        // objects (part 7). Document-level part 4 is not counted per page.
        var pageObjectNums = new List<IReadOnlyList<int>>(pageCount)
        {
            part6Ordered.Select(o => oldToNew[o]).ToList(),
        };
        for (var p = 1; p < pageCount; p++)
            pageObjectNums.Add(pagePrivateOld[p].Select(o => oldToNew[o]).ToList());

        // Shared-object table: part 6 then part 8. Document-level part 4 is not shared.
        var sharedOld = new List<int>(part6Ordered);
        sharedOld.AddRange(part8Old);
        var sharedTableObjNums = sharedOld.Select(o => oldToNew[o]).ToList();
        var nsharedFirstPage = part6Ordered.Count;
        var sharedIndex = new Dictionary<int, int>();
        for (var i = 0; i < sharedOld.Count; i++)
            sharedIndex[sharedOld[i]] = i;

        // Per page (after the first): which shared objects it references.
        var pageSharedRefs = new List<IReadOnlyList<int>>(pageCount) { new List<int>() };
        for (var p = 1; p < pageCount; p++)
        {
            var refs = pageReachable[p]
                .Where(sharedIndex.ContainsKey)
                .Select(n => sharedIndex[n])
                .Distinct()
                .OrderBy(i => i)
                .ToList();
            pageSharedRefs.Add(refs);
        }

        // Outline object numbers in new numbering (root first, then items), for the hint table.
        IReadOnlyList<int> outlineObjNums = outlineGroupOld.Count > 0
            ? part6Ordered
                .Where(o => outlineGroupOld.Contains(o))
                .Select(o => oldToNew[o])
                .ToList()
            : [];

        return new LinearizedLayout(
            oldToNew,
            restObjects,
            part4Objects,
            part6Objects,
            linDictObjNum,
            hintStreamObjNum,
            catalogObjNum,
            firstPageObjNum,
            totalSize,
            pageObjectNums,
            sharedTableObjNums,
            nsharedFirstPage,
            pageSharedRefs,
            outlineObjNums,
            outlinesInFirstPage && outlineGroupOld.Count > 0);
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
