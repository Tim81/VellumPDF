// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.IO;

namespace VellumPdf.Document;

/// <summary>
/// Represents a single structure element (/StructElem) in the PDF logical
/// structure tree (ISO 32000-2 §14.7).
/// </summary>
public sealed class PdfStructElem
{
    /// <summary>Structure type, e.g. "P", "H1", "H2", "Table", "TR", "TD", "L", "Figure".</summary>
    public string StructType { get; }

    /// <summary>The page that owns this element's marked content.</summary>
    public PdfPage? Page { get; set; }

    /// <summary>
    /// Marked-content identifier (MCID) on the page.
    /// -1 means this is a grouping element (e.g. Table, TR) with no direct MCID.
    /// </summary>
    public int Mcid { get; set; } = -1;

    /// <summary>
    /// Optional alternate text. Written as the <c>/Alt</c> entry on the struct elem dict
    /// when non-null. Used primarily for <c>Figure</c> elements (PDF/UA and PDF/A-2a).
    /// </summary>
    public string? AltText { get; set; }

    /// <summary>
    /// Optional per-element language override (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>).
    /// Written as the <c>/Lang</c> entry on this struct element dict when non-null.
    /// Overrides the document-level <c>/Lang</c> for this element and its children.
    /// </summary>
    public string? Language { get; set; }

    /// <summary>
    /// For table header (<c>TH</c>) elements — the <c>/Scope</c> value (<c>"Column"</c> or
    /// <c>"Row"</c>). Emitted as <c>/A &lt;&lt; /O /Table /Scope /&lt;value&gt; &gt;&gt;</c>;
    /// required by PDF/UA-1 (ISO 14289-1 clause 7.5) so data cells can resolve their headers.
    /// </summary>
    public string? TableHeaderScope { get; set; }

    private readonly List<PdfStructElem> _children = [];

    /// <summary>Child structure elements (for grouping elements such as Table, TR, L, LI).</summary>
    public IReadOnlyList<PdfStructElem> Children => _children;

    /// <summary>Creates a structure element of the given structure type.</summary>
    public PdfStructElem(string structType) => StructType = structType;

    /// <summary>Adds a child struct element and returns it (fluent helper).</summary>
    public PdfStructElem AddChild(PdfStructElem child)
    {
        _children.Add(child);
        return child;
    }
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

    // ISO 32000-1 Table 333 standard structure types.
    private static readonly HashSet<string> StandardStructureTypes =
    [
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption",
        "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6",
        "L", "LI", "Lbl", "LBody",
        "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code",
        "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    ];

    public void AddStructElem(PdfStructElem elem) => _documentRoot.AddChild(elem);

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

        // Build ParentTree: structParentsKey → array of struct elem refs indexed by MCID.
        // For each page, gather leaf struct elems (Mcid >= 0) and place each at its MCID index.
        // The array is sized by (max MCID + 1) so non-contiguous or sparse MCID assignments
        // (e.g. when the MCID counter is shared with non-leaf container elements) produce a
        // valid sparse array rather than an out-of-range error. Gaps are left as null objects,
        // which is valid per ISO 32000-2 §14.7.4: the ParentTree maps each MCID to its struct
        // elem, and holes simply mean "no struct elem claims that MCID on this page".
        // The bijection that veraPDF validates (StructParent integer ↔ ParentTree entry ↔
        // struct elem /K MCID) is preserved because each elem is still placed at exactly
        // index == its own MCID.
        // Only negative MCIDs are rejected (they are unconditionally invalid).
        var parentTreeArrayRefs = new PdfIndirectReference[pageOrder.Count];
        for (var pi = 0; pi < pageOrder.Count; pi++)
        {
            var page = pageOrder[pi];
            var elemsOnPage = new List<PdfStructElem>();
            CollectElemsOnPage(_documentRoot, page, elemsOnPage);

            // Compute (max MCID + 1) as the array size; 0 when there are no leaf elems.
            var maxMcid = -1;
            foreach (var elem in elemsOnPage)
            {
                if (elem.Mcid < 0)
                    throw new InvalidOperationException(
                        $"Structure element on page {pi} has negative MCID {elem.Mcid}. " +
                        "MCIDs must be non-negative integers.");
                if (elem.Mcid > maxMcid)
                    maxMcid = elem.Mcid;
            }

            var n = maxMcid + 1; // 0 when elemsOnPage is empty
            var indexed = new PdfObject?[n];

            foreach (var elem in elemsOnPage)
            {
                if (indexed[elem.Mcid] is not null)
                    throw new InvalidOperationException(
                        $"Structure element on page {pi} has duplicate MCID {elem.Mcid}. " +
                        "Two structure elements claim the same MCID on the same page.");

                indexed[elem.Mcid] = elemRefs[elem];
            }

            // Build the array; null slots become PDF null objects (valid sparse holes).
            var refList = new List<PdfObject>(n);
            for (var i = 0; i < n; i++)
                refList.Add(indexed[i] ?? PdfNull.Instance);

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

            // /Pg — the page this element's content is on.
            // For grouping elements (Mcid == -1) without an explicit Page, derive it from
            // the first leaf descendant so the /Pg is still present.
            var pg = elem.Page ?? FindFirstLeafPage(elem);
            if (pg is not null && pageRefMap.TryGetValue(pg, out var pgRef))
                d.Set(new PdfName("Pg"), pgRef);

            // /K — either the MCID integer (leaf) or an array of child refs (grouping element).
            if (elem.Mcid >= 0)
            {
                d.Set(new PdfName("K"), new PdfInteger(elem.Mcid));
            }
            else if (elem.Children.Count > 0)
            {
                var childRefs = elem.Children
                    .Where(c => elemRefs.ContainsKey(c))
                    .Select(c => (PdfObject)elemRefs[c])
                    .ToList();
                if (childRefs.Count > 0)
                    d.Set(new PdfName("K"), new PdfArray(childRefs));
            }

            // /Alt — alternate text for Figure (and other elements that carry it).
            // Written as a UTF-16BE string with BOM per PDF §7.9.2.
            if (elem.AltText is not null)
                d.Set(new PdfName("Alt"), PdfLiteralString.FromUnicode(elem.AltText));

            // /Lang — per-element language override (BCP 47).
            var elemLang = elem.Language?.Trim();
            if (!string.IsNullOrEmpty(elemLang))
                d.Set(new PdfName("Lang"), PdfLiteralString.FromUnicode(elemLang));

            // /A — attribute dictionary for TH elements (PDF/UA-1 clause 7.5).
            // Scope identifies whether this header applies to a Column or Row.
            var scope = elem.TableHeaderScope?.Trim();
            if (!string.IsNullOrEmpty(scope))
                d.Set(new PdfName("A"), new PdfDictionary()
                    .Set(new PdfName("O"), new PdfName("Table"))
                    .Set(new PdfName("Scope"), new PdfName(scope)));

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

        // Validate that all used structure types are standard ISO 32000-1 types.
        // Standard types must NOT appear in /RoleMap — mapping a standard type to itself
        // is a circular mapping and fails ISO 14289-1 (PDF/UA-1) clause 7.1.
        // VellumPdf emits only standard types, so no /RoleMap is written at all.
        var usedTypes = new HashSet<string>();
        CollectStructTypes(_documentRoot, usedTypes);

        foreach (var type in usedTypes)
        {
            if (!StandardStructureTypes.Contains(type))
                throw new InvalidOperationException(
                    $"Structure type '{type}' is not a standard type and has no role mapping.");
        }

        // Write /StructTreeRoot. /RoleMap is intentionally omitted: all used types are
        // standard PDF structure types, and a standard type mapped to itself constitutes
        // a circular mapping that violates ISO 14289-1 clause 7.1.
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

    /// <summary>
    /// Returns the <see cref="PdfPage"/> of the first leaf descendant (Mcid &gt;= 0) of
    /// <paramref name="elem"/>, or null if none exists. Used to supply a /Pg entry for
    /// grouping elements that have no direct page assignment.
    /// </summary>
    private static PdfPage? FindFirstLeafPage(PdfStructElem elem)
    {
        foreach (var child in elem.Children)
        {
            if (child.Page is not null && child.Mcid >= 0) return child.Page;
            var found = FindFirstLeafPage(child);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>
    /// Recursively collects distinct structure-type strings from <paramref name="elem"/>
    /// and all its descendants (inclusive). The caller is responsible for seeding the
    /// set with the document root's type before the first call if needed.
    /// </summary>
    private static void CollectStructTypes(PdfStructElem elem, HashSet<string> types)
    {
        types.Add(elem.StructType);
        foreach (var child in elem.Children)
            CollectStructTypes(child, types);
    }
}
