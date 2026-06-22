// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// A single node in the walked structure tree. Represents one StructElem dictionary.
/// </summary>
internal sealed class StructureTreeNode
{
    /// <summary>The StructElem dictionary for this node.</summary>
    public PdfDictionary Dict { get; }

    /// <summary>The object number used to identify / de-duplicate this node. -1 for inline dicts.</summary>
    public int ObjectNumber { get; }

    /// <summary>
    /// The raw /S name from the dictionary (e.g. "H1", "Sect", "MyCustomRole").
    /// Null if /S is absent (malformed — node is still walked but /S is not present).
    /// </summary>
    public string? RawType { get; }

    /// <summary>
    /// The ISO 32000-1 Table 333 standard structure type reached by following the /RoleMap chain
    /// from <see cref="RawType"/>. Null when <see cref="RawType"/> is itself non-standard and is
    /// either unmapped or its mapping chain does not terminate at a standard type (including cycles).
    /// </summary>
    public string? StandardType { get; }

    /// <summary>The parent node; null at the root of the walked tree (direct StructTreeRoot children).</summary>
    public StructureTreeNode? Parent { get; }

    /// <summary>Child StructElem nodes (not MCIDs, MCR refs, or OBJR refs).</summary>
    public IReadOnlyList<StructureTreeNode> Children => _children;

    private readonly List<StructureTreeNode> _children;

    /// <summary>
    /// True when this node's /K array contains at least one non-StructElem kid
    /// (an integer MCID, a /Type /MCR marked-content ref, or a /Type /OBJR object ref).
    /// </summary>
    public bool HasNonElementKids { get; }

    /// <summary>The /Pg page reference (raw, unresolved) if present.</summary>
    public PdfObject? PageRef { get; }

    internal StructureTreeNode(
        PdfDictionary dict,
        int objectNumber,
        string? rawType,
        string? standardType,
        StructureTreeNode? parent,
        List<StructureTreeNode> children,
        bool hasNonElementKids,
        PdfObject? pageRef)
    {
        Dict = dict;
        ObjectNumber = objectNumber;
        RawType = rawType;
        StandardType = standardType;
        Parent = parent;
        _children = children;
        HasNonElementKids = hasNonElementKids;
        PageRef = pageRef;
    }
}

/// <summary>
/// Walks the PDF document catalog's /StructTreeRoot → /K → StructElem hierarchy and exposes
/// the result as a flat list and tree of <see cref="StructureTreeNode"/> objects.
///
/// <para>
/// Built once per validation pass (lazily on first use via <see cref="Analyze"/>). Consumers
/// may share the cached result; the walker is immutable after construction.
/// </para>
///
/// <para>
/// Guards:
/// <list type="bullet">
/// <item>A visited-set (by object number) prevents following cycles in the graph.</item>
/// <item>A depth cap (256) prevents stack overflow on pathologically-deep trees.</item>
/// <item>Malformed/missing nodes are skipped rather than thrown.</item>
/// </list>
/// </para>
/// </summary>
/// <remarks>
/// ISO 32000-1:2008 §14.7 defines the StructTreeRoot, StructElem, and role-map structure.
/// The Table 333 standard structure types are embedded verbatim and are the canonical list
/// for role-map resolution. Authored clean-room from the specification text.
/// </remarks>
internal sealed class StructureTree
{
    private const int MaxDepth = 256;

    // ISO 32000-1:2008 Table 333 — complete set of standard structure types.
    // This set drives both role-map terminal-type detection (7.1-6/7.1-7) and
    // StandardType resolution (used by containment rules in later batches).
    internal static readonly HashSet<string> StandardTypes = new(StringComparer.Ordinal)
    {
        "Document", "Part", "Art", "Sect", "Div", "BlockQuote", "Caption",
        "TOC", "TOCI", "Index", "NonStruct", "Private",
        "P", "H", "H1", "H2", "H3", "H4", "H5", "H6",
        "L", "LI", "Lbl", "LBody",
        "Table", "TR", "TH", "TD", "THead", "TBody", "TFoot",
        "Span", "Quote", "Note", "Reference", "BibEntry", "Code",
        "Link", "Annot", "Ruby", "RB", "RT", "RP", "Warichu", "WT", "WP",
        "Figure", "Formula", "Form",
    };

    // Cache keyed on PreflightContext so we build once per document.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Rules.PreflightContext, StructureTree>
        _cache = new();

    private static readonly PdfName _structTreeRoot = new("StructTreeRoot");
    private static readonly PdfName _roleMap = new("RoleMap");
    private static readonly PdfName _k = new("K");
    private static readonly PdfName _s = new("S");
    private static readonly PdfName _pg = new("Pg");
    private static readonly PdfName _parentTree = new("ParentTree");
    private static readonly PdfName _nums = new("Nums");
    private static readonly PdfName _kids = new("Kids");
    private static readonly PdfName _limits = new("Limits");

    // Maximum depth when recursing into /ParentTree /Kids nodes (number sub-trees).
    private const int MaxParentTreeDepth = 64;

    /// <summary>
    /// The StructTreeRoot dictionary, or null if the catalog has no /StructTreeRoot or it is
    /// not a dictionary (malformed).
    /// </summary>
    public PdfDictionary? StructTreeRoot { get; }

    /// <summary>
    /// The /RoleMap name→name dictionary, or null if absent. Resolved as a flat string map.
    /// Values are the direct name targets (may themselves be non-standard; chain resolution
    /// happens per-node in <see cref="StructureTreeNode.StandardType"/>).
    /// </summary>
    public IReadOnlyDictionary<string, string>? RoleMap { get; }

    /// <summary>
    /// All StructElem nodes in the walked tree, in depth-first pre-order. Does NOT include
    /// the StructTreeRoot itself (which is not a StructElem). Each root-level element
    /// (direct /K child of the StructTreeRoot) has <see cref="StructureTreeNode.Parent"/> null.
    /// </summary>
    public IReadOnlyList<StructureTreeNode> AllNodes { get; }

    /// <summary>
    /// Root-level StructElem nodes (direct /K children of the StructTreeRoot, not of another StructElem).
    /// </summary>
    public IReadOnlyList<StructureTreeNode> RootElements { get; }

    // ParentTree reverse index: /StructParent integer key → StructureTreeNode.
    // Built lazily from the StructTreeRoot's /ParentTree NUMBER tree. Only entries whose
    // value is a SINGLE StructElem dict (not an array of MCID→elem pairs) are indexed here —
    // those are annotation/OBJR parents (ISO 32000-1 §14.7.4.4). A /Nums value that is an
    // array is a page's MCID map; those are ignored for this batch.
    // The index is null when the StructTreeRoot has no /ParentTree, and empty when the tree
    // exists but contains no single-elem values. Lookup by key returns null for missing keys.
    private readonly Dictionary<int, StructureTreeNode>? _parentTreeIndex;

    /// <summary>
    /// Returns the <see cref="StructureTreeNode"/> whose StructElem is the parent of the
    /// annotation (or XObject) identified by <paramref name="structParentKey"/> — the integer
    /// value of an annotation's <c>/StructParent</c> entry.
    ///
    /// <para>
    /// Returns <see langword="null"/> when:
    /// <list type="bullet">
    ///   <item>The document has no <c>/ParentTree</c>.</item>
    ///   <item><paramref name="structParentKey"/> is not present in the tree.</item>
    ///   <item>The value at the key is a page-MCID array (not an annotation parent).</item>
    ///   <item>The referenced StructElem dict cannot be matched to a walked node.</item>
    ///   <item>The <c>/ParentTree</c> is malformed in a way that prevents safe resolution.</item>
    /// </list>
    /// Null is the FP-safe result: the caller cannot distinguish "no struct binding" from
    /// "struct binding present but unresolvable" and must treat them identically (do not fire
    /// rules that require a confirmed-null parent).
    /// </para>
    /// </summary>
    public StructureTreeNode? StructParentOf(int structParentKey)
    {
        if (_parentTreeIndex is null)
            return null;
        _parentTreeIndex.TryGetValue(structParentKey, out var node);
        return node;
    }

    private StructureTree(Rules.PreflightContext context)
    {
        var structTreeRootObj = context.Resolve(context.Catalog.Get(_structTreeRoot));
        if (structTreeRootObj is not PdfDictionary structTreeRoot)
        {
            AllNodes = [];
            RootElements = [];
            return;
        }

        StructTreeRoot = structTreeRoot;

        // Build the role map (name→name, one hop at a time; multi-hop + cycle resolved per node).
        if (context.Resolve(structTreeRoot.Get(_roleMap)) is PdfDictionary rawRoleMap)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var entry in rawRoleMap.Entries)
            {
                if (context.Resolve(entry.Value) is PdfName target)
                    map[entry.Key.Value] = target.Value;
                // non-name values silently skipped (malformed)
            }
            RoleMap = map;
        }

        // Walk /K from the StructTreeRoot.
        var allNodes = new List<StructureTreeNode>();
        var rootElements = new List<StructureTreeNode>();
        // The visited set is shared across the entire walk to prevent graph cycles.
        var visited = new HashSet<int>();

        var kObj = structTreeRoot.Get(_k);
        if (kObj is not null)
        {
            foreach (var kidObj in FlattenK(context, kObj))
            {
                var node = WalkElement(context, kidObj, parentNode: null, visited, depth: 0, allNodes);
                if (node is not null)
                    rootElements.Add(node);
            }
        }

        AllNodes = allNodes;
        RootElements = rootElements;

        // Build the /ParentTree reverse index after allNodes is complete so dict-identity
        // matching can find nodes by their PdfDictionary reference.
        if (context.Resolve(structTreeRoot.Get(_parentTree)) is PdfDictionary parentTreeDict)
        {
            // Build a dict-identity→node map so we can match a /Nums value to the walked node.
            var dictToNode = new Dictionary<PdfDictionary, StructureTreeNode>(
                ReferenceEqualityComparer.Instance);
            foreach (var node in allNodes)
                dictToNode.TryAdd(node.Dict, node);

            _parentTreeIndex = new Dictionary<int, StructureTreeNode>();
            WalkParentTree(context, parentTreeDict, _parentTreeIndex, dictToNode, depth: 0);
        }
    }

    // ── ParentTree walker ────────────────────────────────────────────────────────────────────────

    // Recursively walks a NUMBER tree node (the StructTreeRoot's /ParentTree or a /Kids sub-node).
    // For each /Nums entry whose value is a SINGLE StructElem dict (not an array), resolves the
    // dict to a StructureTreeNode and records key→node in the index.
    // /Nums entries whose values are arrays are page MCID maps — ignored for annotation lookup.
    private static void WalkParentTree(
        Rules.PreflightContext context,
        PdfDictionary node,
        Dictionary<int, StructureTreeNode> index,
        Dictionary<PdfDictionary, StructureTreeNode> dictToNode,
        int depth)
    {
        if (depth >= MaxParentTreeDepth)
            return;

        // Process /Nums entries on this node.
        if (context.Resolve(node.Get(_nums)) is PdfArray nums)
            IndexNumsArray(context, nums, index, dictToNode);

        // Recurse into /Kids sub-nodes.
        if (context.Resolve(node.Get(_kids)) is PdfArray kids)
        {
            for (var i = 0; i < kids.Count; i++)
            {
                if (context.Resolve(kids[i]) is PdfDictionary child)
                    WalkParentTree(context, child, index, dictToNode, depth + 1);
            }
        }
    }

    // Processes one /Nums array: [key1 val1 key2 val2 …]. Each pair is: integer key → value.
    // When the value resolves to a single StructElem dict (not an array), look it up in dictToNode
    // and index it. When the value is an array it is a page's MCID→elem map — skip it.
    private static void IndexNumsArray(
        Rules.PreflightContext context,
        PdfArray nums,
        Dictionary<int, StructureTreeNode> index,
        Dictionary<PdfDictionary, StructureTreeNode> dictToNode)
    {
        // /Nums is always an even-length [key val key val …] sequence.
        for (var i = 0; i + 1 < nums.Count; i += 2)
        {
            var keyObj = context.Resolve(nums[i]);
            if (keyObj is not PdfInteger keyInt)
                continue; // malformed key — skip
            var key = (int)keyInt.Value;

            var valObj = context.Resolve(nums[i + 1]);
            if (valObj is PdfArray)
                continue; // page MCID map — not an annotation parent

            if (valObj is not PdfDictionary valDict)
                continue; // unexpected value type — skip defensively

            // Match the dict to a walked node by identity.
            if (dictToNode.TryGetValue(valDict, out var matched))
                index.TryAdd(key, matched);
            // If the dict is not in dictToNode (e.g. it was skipped during the walk due to
            // cycles or malformed structure) we simply do not index it. A lookup will return
            // null, which is the FP-safe result (caller treats as "unknown").
        }
    }

    /// <summary>
    /// Returns the cached <see cref="StructureTree"/> for <paramref name="context"/>, building
    /// it on first call. Thread-safe via <see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>.
    /// </summary>
    public static StructureTree Analyze(Rules.PreflightContext context)
        => _cache.GetValue(context, static ctx => new StructureTree(ctx));

    // ── Private walk implementation ──────────────────────────────────────────

    // Flattens a /K value into a sequence of its constituent objects.
    // /K may be a single object (dict, integer, indirect ref) or a PdfArray.
    private static IEnumerable<PdfObject> FlattenK(Rules.PreflightContext context, PdfObject kObj)
    {
        var resolved = context.Resolve(kObj);
        if (resolved is PdfArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                yield return arr[i];
        }
        else if (resolved is not null)
        {
            // Single kid: yield the original (unresolved) so IndirectReference object
            // numbers are preserved for cycle detection.
            yield return kObj;
        }
    }

    // Recursively walks one element from a /K array.  Returns a StructureTreeNode if the
    // object is (or resolves to) a StructElem dictionary; null for MCIDs, MCR, OBJR, or errors.
    private StructureTreeNode? WalkElement(
        Rules.PreflightContext context,
        PdfObject obj,
        StructureTreeNode? parentNode,
        HashSet<int> visited,
        int depth,
        List<StructureTreeNode> allNodes)
    {
        if (depth >= MaxDepth)
            return null;

        // Bare integer in /K → MCID, not a StructElem.
        if (obj is PdfInteger)
            return null;

        // Resolve indirect reference for cycle detection.
        int objNum = -1;
        PdfObject candidate = obj;
        if (obj is PdfIndirectReference iref)
        {
            objNum = iref.ObjectNumber;
            // Cycle guard for indirect refs.
            if (!visited.Add(objNum))
                return null;
            candidate = context.Resolve(obj) ?? obj;
        }

        if (candidate is not PdfDictionary dict)
        {
            // Could be a resolved integer (bare MCID after resolution), or something else.
            if (objNum >= 0)
                visited.Remove(objNum); // not a node — give back the slot so the int can be reused
            return null;
        }

        // Check /Type: skip MCR and OBJR; accept StructElem or absent /Type.
        var typeVal = (context.Resolve(dict.Get(PdfName.Type)) as PdfName)?.Value;
        if (typeVal is "MCR" or "OBJR")
        {
            if (objNum >= 0) visited.Remove(objNum);
            return null;
        }
        if (typeVal is not null and not "StructElem")
        {
            // Unknown /Type on a /K child — skip defensively.
            if (objNum >= 0) visited.Remove(objNum);
            return null;
        }

        // For inline (direct) dictionaries, use a synthetic object number so we don't double-visit.
        // We can't distinguish two different inline dicts at the same depth from the same array,
        // so we skip cycle detection for those — depth cap is sufficient.

        var rawType = (context.Resolve(dict.Get(_s)) as PdfName)?.Value;
        var standardType = ResolveStandardType(rawType);
        var pageRef = dict.Get(_pg);

        // Partition /K into element kids (recursed into) and non-element kids (MCID integers, /MCR,
        // /OBJR) in a single pass, so the node is created ONCE with the correct hasNonElementKids
        // flag and its element children receive THIS node as their parent. (The earlier
        // placeholder-then-rebuild approach left children pointing at a discarded node, corrupting
        // StructureTreeNode.Parent for later-batch containment rules.)
        var childList = new List<StructureTreeNode>();
        var elementKids = new List<PdfObject>();
        var hasNonElementKids = false;
        if (dict.Get(_k) is { } kObj)
        {
            foreach (var kidObj in FlattenK(context, kObj))
            {
                if (IsNonElementKid(context, kidObj))
                    hasNonElementKids = true;
                else
                    elementKids.Add(kidObj);
            }
        }

        var node = new StructureTreeNode(
            dict, objNum, rawType, standardType, parentNode, childList, hasNonElementKids, pageRef);
        allNodes.Add(node);

        foreach (var kidObj in elementKids)
        {
            var child = WalkElement(context, kidObj, node, visited, depth + 1, allNodes);
            if (child is not null)
                childList.Add(child);
            // A null result here is an unresolvable / cycle-skipped / malformed element reference —
            // not a marked-content kid, so it does not affect hasNonElementKids.
        }

        return node;
    }

    // Returns true when a kid object in a /K array is a non-StructElem entry
    // (bare MCID integer, /Type /MCR dict, or /Type /OBJR dict).
    private static bool IsNonElementKid(Rules.PreflightContext context, PdfObject kidObj)
    {
        if (kidObj is PdfInteger)
            return true;

        var resolved = context.Resolve(kidObj);
        if (resolved is PdfInteger)
            return true;
        if (resolved is PdfDictionary d)
        {
            var t = (context.Resolve(d.Get(PdfName.Type)) as PdfName)?.Value;
            return t is "MCR" or "OBJR";
        }
        return false;
    }

    // Resolves rawType to the standard Table-333 type via the /RoleMap chain.
    // Multi-hop: A→B→C→standard. Cycle guard (up to 50 hops max).
    private string? ResolveStandardType(string? rawType)
    {
        if (rawType is null)
            return null;
        if (StandardTypes.Contains(rawType))
            return rawType;
        if (RoleMap is null)
            return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = rawType;
        while (!StandardTypes.Contains(current))
        {
            if (!seen.Add(current))
                return null; // cycle
            if (!RoleMap.TryGetValue(current, out var next))
                return null; // unmapped
            current = next;
        }
        return current;
    }
}
