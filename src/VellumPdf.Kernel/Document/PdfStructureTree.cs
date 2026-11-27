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

    /// <summary>
    /// For table cell (<c>TH</c>/<c>TD</c>) elements — the number of rows the cell spans.
    /// Merged into the same <c>/A &lt;&lt; /O /Table ... &gt;&gt;</c> attribute dictionary as
    /// <see cref="TableHeaderScope"/>, per ISO 32000-1 14.8.5.7 Table 349. A value of 1 or less is
    /// never written: Table 349 defaults an absent <c>/RowSpan</c> to 1, so an unspanned cell keeps
    /// the exact bytes it had before this attribute existed. ISO 14289-1:2014 clause 7.5 requires a
    /// table header to be tagged according to Table 349, and a validator checks each row's column
    /// count against these two values; without them, a spanning cell's single structure element
    /// claims a one-by-one slot and the row it covers reads one column short.
    /// </summary>
    public int? TableRowSpan { get; set; }

    /// <summary>
    /// For table cell (<c>TH</c>/<c>TD</c>) elements — the number of columns the cell spans. See
    /// <see cref="TableRowSpan"/>: same attribute dictionary, same Table 349 default of 1, same
    /// row-width check.
    /// </summary>
    public int? TableColSpan { get; set; }

    private readonly List<PdfStructElem> _children = [];

    /// <summary>Child structure elements (for grouping elements such as Table, TR, L, LI).</summary>
    public IReadOnlyList<PdfStructElem> Children => _children;

    /// <summary>Creates a structure element of the given structure type.</summary>
    public PdfStructElem(string structType) => StructType = structType;

    /// <summary>Adds a child struct element and returns it (fluent helper).</summary>
    /// <remarks>
    /// <b>An element that is its own ancestor is refused when the structure tree is built, not here.</b>
    /// A child that is this element, or that already holds this element somewhere in its own subtree,
    /// closes a cycle, and a walk over a cycle would never end.
    /// <c>Save</c> therefore throws <see cref="InvalidOperationException"/> for a tagged document whose
    /// structure tree reaches such an element. This call accepts it, because the cycle is harmless to a save that never walks it: a document that is not tagged, or that was tagged after the element was registered. An element reachable by
    /// two routes without a cycle is not refused.
    /// <para><b>The depth of the tree is not limited by <c>Save</c>.</b> The walks that number and
    /// write the tree do not recurse, so a tree's depth does not depend on the stack of the
    /// calling thread. Where each element has one parent, the walks take time and memory in
    /// proportion to the number of elements; <c>Linearize</c> takes longer than that on a very large
    /// tree. An element added under two parents is walked once per path, so the time can grow
    /// exponentially with the depth before <c>Save</c> refuses the tree with
    /// <see cref="InvalidOperationException"/>.</para>
    /// <para>A null <paramref name="child"/> is stored and refused where the structure
    /// tree is built: a document that is not tagged never builds it, so such a document saves. It is
    /// not refused at this call. <b>Do not pass</b> a null child or a child that closes a cycle: a
    /// later major refuses both here.</para>
    /// <para><b>Do not build</b> a tree deeper than a reader can walk. ISO 32000-2 Annex C, Table
    /// C.1 (row "Nested objects"), warns that a PDF processor may implement recursive algorithms
    /// that fail on excessively nested constructs, so a file this library saves can be one that
    /// such a reader cannot open.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from <c>Save</c>, not from this call, when the document is tagged and the element is
    /// reachable from the structure tree and holds a null child. <c>ParamName</c> is <c>child</c>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <c>Save</c>, not from this call, when the document is tagged and the structure tree
    /// reaches an element that is its own ancestor.
    /// </exception>
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
    // Upper bound on a per-page MCID, used to cap the ParentTree array allocation against a
    // pathological hand-built value. Far above any real page's marked-content count.
    private const int MaxParentTreeIndex = 1_000_000;

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

    // IsEmpty was removed with #120: its only two callers were the Save-time gates that decided
    // whether to emit a structure tree at all, and both now emit one whenever the document is
    // Tagged. Detecting content that no structure element describes is the conformance engine's job
    // (A2aContentItemTaggingRule), not the writer's — the tree being empty was never the right
    // signal for it, since an empty tree is legal on a page with no content.

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
        _walkSteps = 0;

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

        // The first-leaf page of every element, computed once here (CollectPages has already
        // refused a cycle) and dropped with this call.
        var firstLeafPages = FirstLeafPages(_documentRoot, out var firstLeafSteps);
        _walkSteps += firstLeafSteps;

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
        var leavesByPage = CollectLeavesByPage(_documentRoot, pageOrder.Count, pageStructParents);
        for (var pi = 0; pi < pageOrder.Count; pi++)
        {
            var elemsOnPage = leavesByPage[pi];

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

            // The per-page ParentTree is an array indexed by MCID, sized by (max MCID + 1).
            // Guard a pathological MCID from a hand-built struct tree (PdfStructElem.Mcid is a
            // public setter): without a bound, a huge value would allocate gigabytes and
            // int.MaxValue would overflow (max MCID + 1) to a negative length. Real marked-content
            // sequences are bounded by the content stream, so this ceiling is far above any
            // legitimate page; it is a robustness guard, not a conformance limit.
            if (maxMcid > MaxParentTreeIndex)
                throw new InvalidOperationException(
                    $"Structure element on page {pi} has MCID {maxMcid}, which exceeds the maximum " +
                    $"supported value ({MaxParentTreeIndex}). This indicates a malformed structure tree.");

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
            var pg = elem.Page ?? (firstLeafPages.TryGetValue(elem, out var firstLeaf) ? firstLeaf : null);
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

            // /A — the table attribute dictionary (ISO 32000-1 14.8.5.7 Table 349). ISO
            // 14289-1:2014 clause 7.5 requires a table header to be tagged according to Table 337
            // and Table 349, which is where /Scope, /RowSpan and /ColSpan come from. The two spans
            // are written only when greater than 1, since Table 349 defaults an absent value to 1
            // and a validator checks each row's column count against exactly that default.
            var scope = elem.TableHeaderScope?.Trim();
            var rowSpan = elem.TableRowSpan;
            var colSpan = elem.TableColSpan;
            if (!string.IsNullOrEmpty(scope) || rowSpan > 1 || colSpan > 1)
            {
                var attrs = new PdfDictionary().Set(new PdfName("O"), new PdfName("Table"));
                if (!string.IsNullOrEmpty(scope))
                    attrs.Set(new PdfName("Scope"), new PdfName(scope));
                if (rowSpan > 1)
                    attrs.Set(new PdfName("RowSpan"), new PdfInteger(rowSpan.Value));
                if (colSpan > 1)
                    attrs.Set(new PdfName("ColSpan"), new PdfInteger(colSpan.Value));
                d.Set(new PdfName("A"), attrs);
            }

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

    // The walks below visit every element with an explicit stack, so the depth of a tree is not
    // limited by the stack of the thread that saves it. Where each element has one parent, each
    // takes time and memory in proportion to the number of elements; an element added under two
    // parents is walked once per path, so the time can grow exponentially with the depth before the
    // save refuses the tree. AddChild accepts a cycle (an untagged document never builds the tree).
    // A walk over a cycle would not stop until it ran out of memory, so the walks refuse it with
    // the cycle error first. Three of them, CollectElems, CollectPages and CollectLeavesByPage, keep
    // the elements on their current path and refuse to enter one twice. The other three,
    // BuildParentMap, FirstLeafPages and CollectStructTypes, keep no path: they are safe only
    // because each runs after CollectPages has walked the same tree from the document root and
    // refused a cycle. Reorder them and a graph put together some other way would run until memory
    // ran out instead of raising the cycle error.
    private static void EnterElement(HashSet<PdfStructElem> path, PdfStructElem element)
    {
        if (!path.Add(element))
            throw new InvalidOperationException(
                "The structure tree contains a cycle: an element is its own ancestor, so the tree cannot be walked.");
    }

    // Work the walks of the last Build call did: one for each element a pre-order walk entered,
    // and FirstLeafPages' own steps when Build adds them. FirstLeafPages is static and counts only
    // for a caller that adds its steps, so a call that discards them is not seen here. The tests
    // read it to assert that a save does not walk the tree once per page. It belongs to the tree,
    // so saves of other documents do not share it, and Build resets it on entry.
    private long _walkSteps;

    internal long LastBuildSteps => _walkSteps;

    // Pre-order walk. visit(node, parent) runs on entry, in the order a recursive walk would enter
    // nodes; the root is visited first with a null parent. The visit runs before the node is read,
    // so a walk that must refuse a null child does it there. With guard set, the nodes on the
    // current path are kept and an element met twice on one path raises; a node leaves the path
    // when its children are done.
    private void PreOrder(PdfStructElem root, bool guard, Action<PdfStructElem, PdfStructElem?> visit)
    {
        var path = guard ? new HashSet<PdfStructElem>(ReferenceEqualityComparer.Instance) : null;
        var stack = new List<(PdfStructElem Node, int Next)>();

        void Enter(PdfStructElem node, PdfStructElem? parent)
        {
            _walkSteps++;
            visit(node, parent);
            if (path is not null)
                EnterElement(path, node);
            stack.Add((node, 0));
        }

        Enter(root, null);
        while (stack.Count > 0)
        {
            var (node, next) = stack[^1];
            if (next < node.Children.Count)
            {
                stack[^1] = (node, next + 1);
                Enter(node.Children[next], node);
            }
            else
            {
                path?.Remove(node);
                stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    private void CollectElems(PdfStructElem parent, List<PdfStructElem> list)
        => PreOrder(parent, guard: true, (node, p) =>
        {
            if (p is not null)
                list.Add(node);
        });

    private void CollectPages(
        PdfStructElem root,
        List<PdfPage> pageOrder,
        Dictionary<PdfPage, int> pageStructParents)
        => PreOrder(root, guard: true, (node, _) =>
        {
            if (node is null)
                throw new ArgumentNullException("child", "A structure element has a null child.");
            if (node.Page is not null && !pageStructParents.ContainsKey(node.Page))
            {
                pageStructParents[node.Page] = pageOrder.Count;
                pageOrder.Add(node.Page);
            }
        });

    // One walk that sorts every leaf (a page and an MCID of 0 or more, under a parent) into the
    // list of its page, in the order the walk enters them. A walk per page would visit every
    // element once per page. pageStructParents maps each page CollectPages found to its index in
    // the page order, and every element with a page is under that walk, so the lookup cannot miss.
    private List<PdfStructElem>[] CollectLeavesByPage(
        PdfStructElem root,
        int pageCount,
        Dictionary<PdfPage, int> pageStructParents)
    {
        var byPage = new List<PdfStructElem>[pageCount];
        for (var i = 0; i < pageCount; i++)
            byPage[i] = [];
        if (pageCount == 0)
            return byPage;

        PreOrder(root, guard: true, (node, p) =>
        {
            if (p is not null && node.Page is { } page && node.Mcid >= 0)
                byPage[pageStructParents[page]].Add(node);
        });
        return byPage;
    }

    // Where an element is reached twice the last parent wins, as before; no saved file can show it,
    // because an element reached twice is refused (a duplicate MCID, or a reserved object that is
    // never assigned).
    private void BuildParentMap(PdfStructElem parent, Dictionary<PdfStructElem, PdfStructElem> map)
        => PreOrder(parent, guard: false, (node, p) =>
        {
            if (p is not null)
                map[node] = p;
        });

    /// <summary>
    /// Maps every element under <paramref name="root"/> (and <paramref name="root"/> itself) to the
    /// page of its first leaf descendant in document order, where a leaf is an element with a page
    /// and an MCID of 0 or more, or to null when it has none. Supplies the <c>/Pg</c> entry of a
    /// grouping element with no page of its own. <paramref name="steps"/> is the work done: one for
    /// each element entered and one for each parent-child edge followed, so a tree costs at most
    /// twice its elements, whatever its shape.
    /// </summary>
    internal static Dictionary<PdfStructElem, PdfPage?> FirstLeafPages(PdfStructElem root, out int steps)
    {
        var map = new Dictionary<PdfStructElem, PdfPage?>(ReferenceEqualityComparer.Instance);
        var stack = new List<(PdfStructElem Node, int Next, PdfPage? Found)>();
        steps = 0;
        var count = 0;
        if (root is null)
            return map;
        stack.Add((root, 0, null));
        count++;
        while (stack.Count > 0)
        {
            var (node, next, found) = stack[^1];
            if (next < node.Children.Count)
            {
                var child = node.Children[next];
                count++;
                stack[^1] = (node, next + 1, found);
                if (map.TryGetValue(child, out var known))
                {
                    // An element reached by a second route: its answer is already known.
                    if (found is null)
                        stack[^1] = (node, next + 1, IsLeaf(child) ? child.Page : known);
                }
                else
                {
                    count++;
                    stack.Add((child, 0, null));
                }
                continue;
            }

            map[node] = found;
            stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0)
            {
                var (parent, parentNext, parentFound) = stack[^1];
                if (parentFound is null)
                    stack[^1] = (parent, parentNext, IsLeaf(node) ? node.Page : found);
            }
        }
        steps = count;
        return map;
    }

    private static bool IsLeaf(PdfStructElem element) => element.Page is not null && element.Mcid >= 0;

    private void CollectStructTypes(PdfStructElem elem, HashSet<string> types)
        => PreOrder(elem, guard: false, (node, _) => types.Add(node.StructType));
}
