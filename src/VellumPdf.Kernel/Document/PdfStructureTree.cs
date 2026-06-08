// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.IO;

namespace VellumPdf.Document;

/// <summary>
/// Represents a single structure element (/StructElem) in the PDF logical
/// structure tree (ISO 32000-2 §14.7).
/// </summary>
public sealed class PdfStructElem
{
    /// <summary>Structure type, e.g. "P", "H1", "H2", "Document".</summary>
    public string StructType { get; }

    /// <summary>The page that owns this element's marked content.</summary>
    public PdfPage? Page { get; set; }

    /// <summary>
    /// Marked-content identifier (MCID) on the page.
    /// -1 means this is a grouping element (e.g. the Document root) with no direct MCID.
    /// </summary>
    public int Mcid { get; set; } = -1;

    /// <summary>Child structure elements (for grouping elements).</summary>
    internal List<PdfStructElem> Children { get; } = [];

    public PdfStructElem(string structType) => StructType = structType;
}

/// <summary>
/// Accumulates structure elements during the layout/draw phase and, at save time,
/// writes the /StructTreeRoot indirect object graph and a /ParentTree number tree.
///
/// <para>
/// <strong>ParentTree status:</strong> a number tree mapping each page's
/// /StructParents integer to an array of its struct element refs is built and
/// written. Each page that contains tagged content receives a /StructParents
/// integer key. The ParentTree is attached to /StructTreeRoot as /ParentTree.
/// </para>
/// </summary>
internal sealed class PdfStructureTree
{
    // The top-level /Document struct elem that holds all other elems as children.
    private readonly PdfStructElem _documentRoot = new("Document");

    public void AddStructElem(PdfStructElem elem) => _documentRoot.Children.Add(elem);

    public bool IsEmpty => _documentRoot.Children.Count == 0;

    /// <summary>
    /// Writes all structure objects into <paramref name="registry"/> and returns
    /// the /StructTreeRoot indirect reference.
    /// Also fills <paramref name="pageStructParents"/> so each page can record
    /// its /StructParents integer in its page dictionary.
    /// </summary>
    internal PdfIndirectReference Build(
        PdfObjectRegistry registry,
        Dictionary<PdfPage, PdfIndirectReference> pageRefMap,
        out Dictionary<PdfPage, int> pageStructParents)
    {
        pageStructParents = [];

        // Collect distinct pages in insertion order and assign StructParents integers.
        var pageOrder = new List<PdfPage>();
        CollectPages(_documentRoot, pageOrder, pageStructParents);

        // Reserve refs for all struct elems (breadth-first to get stable numbering).
        var allElems = new List<PdfStructElem>();
        CollectElems(_documentRoot, allElems);

        // +1 for the document root itself
        var elemRefs = new Dictionary<PdfStructElem, PdfIndirectReference>(allElems.Count + 1);
        var docRootRef = registry.Reserve();
        elemRefs[_documentRoot] = docRootRef;
        foreach (var e in allElems)
            elemRefs[e] = registry.Reserve();

        // Build a parent map so each elem's true immediate parent is known at any depth.
        var parentMap = new Dictionary<PdfStructElem, PdfStructElem>(allElems.Count);
        BuildParentMap(_documentRoot, parentMap);

        // Reserve StructTreeRoot ref
        var structTreeRootRef = registry.Reserve();

        // Build ParentTree: structParentsKey → array of struct elem refs on that page.
        // For each page, gather all struct elems (leaf nodes) belonging to it.
        var parentTreeArrayRefs = new PdfIndirectReference[pageOrder.Count];
        for (var pi = 0; pi < pageOrder.Count; pi++)
        {
            var page = pageOrder[pi];
            // Collect leaf elems on this page (those with Mcid >= 0)
            var elemsOnPage = new List<PdfStructElem>();
            CollectElemsOnPage(_documentRoot, page, elemsOnPage);

            var refList = elemsOnPage
                .Where(e => elemRefs.ContainsKey(e))
                .Select(e => (PdfObject)elemRefs[e])
                .ToList();

            var arr = new PdfArray(refList);
            parentTreeArrayRefs[pi] = registry.Reserve();
            registry.SetValue(parentTreeArrayRefs[pi], arr);
        }

        // Write /Document root struct elem
        {
            var childRefs = _documentRoot.Children
                .Where(c => elemRefs.ContainsKey(c))
                .Select(c => (PdfObject)elemRefs[c])
                .ToList();

            var d = new PdfDictionary()
                .Set(PdfName.Type, new PdfName("StructElem"))
                .Set(new PdfName("S"), new PdfName("Document"))
                .Set(new PdfName("P"), structTreeRootRef)
                .Set(new PdfName("K"), new PdfArray(childRefs));
            registry.SetValue(docRootRef, d);
        }

        // Write leaf/child struct elems
        foreach (var elem in allElems)
        {
            var ref_ = elemRefs[elem];

            // Resolve the immediate parent using the parent map built above.
            PdfObject parentRef;
            if (parentMap.TryGetValue(elem, out var immediateParent) && elemRefs.TryGetValue(immediateParent, out var parentElemRef))
                parentRef = parentElemRef;
            else
                parentRef = structTreeRootRef;

            var d = new PdfDictionary()
                .Set(PdfName.Type, new PdfName("StructElem"))
                .Set(new PdfName("S"), new PdfName(elem.StructType))
                .Set(new PdfName("P"), parentRef);

            // /Pg — the page this element's content is on
            if (elem.Page is not null && pageRefMap.TryGetValue(elem.Page, out var pgRef))
                d.Set(new PdfName("Pg"), pgRef);

            // /K — the MCID integer
            if (elem.Mcid >= 0)
                d.Set(new PdfName("K"), new PdfInteger(elem.Mcid));

            // /StructParents is NOT set on individual struct elems; it belongs on the page.
            registry.SetValue(ref_, d);
        }

        // Build ParentTree number tree (flat; one entry per page with tagged content).
        // Number tree format: /Nums [key0 val0 key1 val1 …]
        var numsItems = new List<PdfObject>(pageOrder.Count * 2);
        for (var pi = 0; pi < pageOrder.Count; pi++)
        {
            numsItems.Add(new PdfInteger(pi));
            numsItems.Add(parentTreeArrayRefs[pi]);
        }
        var parentTreeDict = new PdfDictionary()
            .Set(new PdfName("Nums"), new PdfArray(numsItems));
        var parentTreeRef = registry.Reserve();
        registry.SetValue(parentTreeRef, parentTreeDict);

        // Write /StructTreeRoot
        var structTreeRoot = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("StructTreeRoot"))
            .Set(new PdfName("K"), docRootRef)
            .Set(new PdfName("ParentTree"), parentTreeRef)
            .Set(new PdfName("ParentTreeNextKey"), new PdfInteger(pageOrder.Count));
        registry.SetValue(structTreeRootRef, structTreeRoot);

        return structTreeRootRef;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void CollectElems(PdfStructElem parent, List<PdfStructElem> list)
    {
        foreach (var child in parent.Children)
        {
            list.Add(child);
            CollectElems(child, list);
        }
    }

    private static void CollectPages(
        PdfStructElem root,
        List<PdfPage> pageOrder,
        Dictionary<PdfPage, int> pageStructParents)
    {
        if (root.Page is not null && !pageStructParents.ContainsKey(root.Page))
        {
            pageStructParents[root.Page] = pageOrder.Count;
            pageOrder.Add(root.Page);
        }
        foreach (var child in root.Children)
            CollectPages(child, pageOrder, pageStructParents);
    }

    private static void CollectElemsOnPage(
        PdfStructElem parent,
        PdfPage target,
        List<PdfStructElem> result)
    {
        foreach (var child in parent.Children)
        {
            if (child.Page == target && child.Mcid >= 0)
                result.Add(child);
            CollectElemsOnPage(child, target, result);
        }
    }

    /// <summary>
    /// Recursively populates <paramref name="map"/> so that each child element maps to its
    /// immediate parent. The document root itself is the parent of its direct children.
    /// </summary>
    private static void BuildParentMap(PdfStructElem parent, Dictionary<PdfStructElem, PdfStructElem> map)
    {
        foreach (var child in parent.Children)
        {
            map[child] = parent;
            BuildParentMap(child, map);
        }
    }
}
