// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;

namespace VellumPdf.Reader;

/// <summary>
/// Walks a document's page tree (ISO 32000-2 §7.7.3) from <c>/Root</c> → <c>/Pages</c> →
/// <c>/Kids</c>, producing the ordered page list <see cref="PdfDocumentReader.Pages"/> exposes.
/// </summary>
/// <remarks>
/// Iterative: an explicit stack rather than recursion, because tree depth is attacker-controlled
/// input; a hostile <c>/Kids</c> chain built to recurse one C# stack frame per level would otherwise
/// risk an uncatchable <see cref="StackOverflowException"/>, the same class of defect
/// <see cref="PdfDocumentReader"/>'s own <c>MaxResolveDepth</c> guards against for indirect-reference
/// chains. <see cref="MaxDepth"/> mirrors the depth cap <c>PreflightContext.WalkPages</c> already
/// uses for the Conformance package's own page-tree walk.
/// <para>
/// <c>/Count</c> is never consulted. §7.7.3.2 Table 30 requires a writer to keep it "consistent with
/// the number of entries in the Kids array and its descendants which definitively determines the
/// number of descendant pages", the tree itself, not the redundant integer beside it. Real
/// producers disagree with their own <c>/Count</c> often enough (off by the pages a later edit added
/// or removed without updating it) that trusting it would misreport
/// <see cref="PdfDocumentReader.PageCount"/> on ordinary files, not just adversarial ones.
/// </para>
/// <para>
/// Retains at most TWO diagnostics past <see cref="PdfReaderOptions.MaxDiagnostics"/> per walk
/// (see <see cref="DiagnosticSink.ReportRetained"/>), not three:
/// <see cref="PdfReaderDiagnosticCode.PageTreeLeafLimitExceeded"/> and
/// <see cref="PdfReaderDiagnosticCode.PageTreeNodeLimitExceeded"/> each end the walk the instant
/// they are reported, so at most one of that pair can ever fire in a given walk; the FIRST
/// <see cref="PdfReaderDiagnosticCode.PageTreeDepthExceeded"/> of the walk is the other.
/// </para>
/// </remarks>
internal static class PageTreeWalker
{
    /// <summary>
    /// Hard cap on page-tree nesting depth. ISO 32000-2 places no limit on this, so the cap is this
    /// processor's own choice (Annex C.1, informative, on practical processing limits); see the
    /// type doc's own remarks for why it needs one at all.
    /// </summary>
    internal const int MaxDepth = 256;

    /// <summary>
    /// Hard cap on the number of page leaves one walk collects, for the same Annex C.1 reason as
    /// <see cref="MaxDepth"/>: a flat <c>/Kids</c> array large enough to matter is otherwise an
    /// unbounded-allocation vector with no depth to cap it.
    /// </summary>
    internal const int MaxLeaves = 100_000;

    /// <summary>
    /// Hard cap on the total number of <c>/Kids</c> array elements one walk examines, across every
    /// node combined. <see cref="MaxDepth"/> alone does not bound this: with a branching factor of
    /// two or more, every additional level of depth the cap still permits doubles the node count
    /// below it, so the work a full-depth tree demands is exponential in <see cref="MaxDepth"/>, not
    /// linear in it: a depth cap generous enough for any real document is nowhere near tight enough
    /// to also bound the work an adversarial one can force. This budget bounds the walk's own work
    /// directly instead of relying on the depth and leaf caps to do it as a side effect.
    /// <para>
    /// The same amplification argument applies to inherited-attribute resolution, not just to
    /// walking <c>/Kids</c> itself: a depth cap generous enough for real documents (256 levels) does
    /// not bound the WORK an attribute lookup does once it multiplies against the leaf cap (100,000):
    /// a chain that re-scanned every ancestor for every leaf would cost up to depth × leaves
    /// candidate checks, per attribute, per leaf. <see cref="EffectiveAttributes"/> is computed once
    /// per node instead, when its <see cref="Frame"/> is pushed, so a leaf's own resolution is O(1)
    /// and the whole walk's attribute work is O(nodes) rather than O(nodes × depth).
    /// </para>
    /// </summary>
    internal const int MaxKidsExamined = 1_000_000;

    /// <summary>
    /// Hard cap on how many hops one indirect-reference chain follows before this reader gives up
    /// on it. ISO 32000-2 §7.3.10's 2020 NOTE permits such chains with no length limit of its own
    /// ("Any object outside of an object stream (see 7.5.7, "Object streams") can consist solely
    /// of an object reference. PDF syntax thus permits chains of such objects."), and a later
    /// paragraph of the same subclause, after EXAMPLE 2, adds that following one all the way is
    /// required, not optional ("Except where documented to the contrary, any object value may be
    /// a direct or an indirect reference; the semantics are equivalent."), so this bound, like
    /// <see cref="MaxDepth"/> and <see cref="MaxKidsExamined"/> above, is this reader's own choice
    /// against adversarial input, not a spec requirement.
    /// </summary>
    internal const int MaxReferenceChainHops = 32;

    private static readonly PdfRectangle LetterFallback = new(0, 0, 612, 792);

    // Not one of PdfName's well-known statics (src/VellumPdf.Kernel/Core/PdfName.cs): nothing
    // outside the reader's page-tree walk needs a shared instance of this one yet.
    private static readonly PdfName CropBoxKey = new("CropBox");

    // Shared, read-only placeholder for a node classified as a page-tree node by its /Type alone,
    // with no usable /Kids of its own: contributes zero children.
    private static readonly PdfArray EmptyKids = new();

    /// <summary>Walks <paramref name="reader"/>'s page tree, reporting shape problems to
    /// <paramref name="diagnostics"/> along the way, and returns the pages found in tree order.</summary>
    internal static List<PdfReadPage> Walk(PdfDocumentReader reader, DiagnosticSink diagnostics)
    {
        var pages = new List<PdfReadPage>();
        var cache = new FailedResolveCache();

        var rootRaw = reader.Catalog.Get(PdfName.Pages);
        var rootResolved = ResolveOrAbsent(
            reader, cache, rootRaw, out var rootAbsent, out var rootObjectNumber);

        // A genuinely omitted /Pages entry and one that IS there but is null, directly or through
        // a chain of references that resolves to null (ISO 32000-2 §7.3.9, §7.3.10), are the same
        // "no /Pages entry" condition; a value that resolves to something else entirely reaches the
        // "does not resolve to a dictionary" report below. The two null-equivalent cases still get
        // different message text: a direct entry names no object to report, but a reference that
        // resolves to nothing is worth naming, since the file plainly has SOME /Pages entry, just
        // not a usable one. rootObjectNumber is the LAST reference this chain actually followed
        // (see TryResolve), not necessarily the one /Pages names directly, so a two-hop chain to a
        // free object names the free object here, not the intermediate one.
        if (rootAbsent)
        {
            if (rootRaw is PdfIndirectReference)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeMissing,
                    $"The document catalog's /Pages entry resolves to object {rootObjectNumber}, "
                    + "which does not exist or is the null object (ISO 32000-2 §7.3.9, §7.3.10); "
                    + "the document has no pages.",
                    rootObjectNumber);
            }
            else
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeMissing,
                    "The document catalog has no /Pages entry (ISO 32000-2 §7.7.2); the document has "
                    + "no pages.");
            }
            return pages;
        }

        if (rootResolved is not PdfDictionary rootDict)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The document catalog's /Pages entry does not resolve to a dictionary "
                + "(ISO 32000-2 §7.7.3.2); the document has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        // The root itself is classified by the same /Type-and-structure rule as any other node
        // reached through /Kids (see ClassifyByType), not merely accepted on /Kids alone: §7.7.2
        // Table 29's Pages row requires /Root/Pages to BE the page tree's root node, and §7.7.3.2
        // supplies the /Type /Pages rule that decides whether a dictionary qualifies as one, so a
        // root that is really a leaf (a stray /Type /Page at the top) or something else entirely
        // (the catalog dictionary reused as its own /Pages, say) is not a page tree with zero
        // children, it is not a page tree at all.
        var rootKidsRaw = rootDict.Get(PdfName.Kids);
        var rootKidsArray = ResolveOrAbsent(
            reader, cache, rootKidsRaw, out _, out var rootKidsArrayObjectNumber) as PdfArray;

        var rootKind = ClassifyByType(reader, cache, rootDict, rootKidsArray, out var rootType);
        if (rootKind != NodeKind.Node)
        {
            var found = rootType is not null ? $"/Type {rootType}" : "no /Type and no /Kids array";
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                $"The page tree root is not a page-tree node ({found}); ISO 32000-2 §7.7.2 Table "
                + "29 requires /Root/Pages to be the root of the page tree, and §7.7.3.2 defines "
                + "what counts as one; the document has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        // The root is a node reached WITHOUT going through ClassifyNode (it never sits in some
        // other node's /Kids), so it needs its own copy of ClassifyNode's stray-/Contents check;
        // see ContentsOnNodeProblem and this method's own doc for why /Contents on a node is
        // flagged at all. Reported on its own rather than merged with the missing-/Kids case below:
        // the two are different codes (PageTreeNodeMalformed here, PageTreeMissing there), so
        // DiagnosticSink's dedupe key never collides between them and both can fire independently.
        _ = ResolveOrAbsent(
            reader, cache, rootDict.Get(PdfName.Contents), out var rootContentsAbsent);
        if (!rootContentsAbsent)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                ContentsOnNodeProblem + ".",
                NullIfZero(rootObjectNumber));
        }

        if (rootKidsArray is null)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The page tree root has no usable /Kids array (ISO 32000-2 §7.7.3.2); the document "
                + "has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        var rootKids = rootKidsArray;

        // Object numbers seen as EITHER a page-tree node, a page object, or a /Kids array, anywhere
        // in the walk. Every number added here is a TERMINAL object identity, the last reference
        // TryResolve actually followed to reach the dictionary or array in question, not
        // necessarily the object number some /Kids element or /Kids entry names directly: two
        // different aliases that both, one or more hops later, resolve to the same object are the
        // same entry in this set, so the second one is caught here even though the two /Kids
        // elements that led to it never shared an object number of their own. ISO 32000-2 §7.7.3.2
        // forbids multiple indirect references to the same page tree node, and §7.7.3.3 forbids
        // multiple indirect references to the same page object, whether that repeat is written as
        // the same reference twice or as two different aliases of it, and describe /Kids as a
        // tree, not a graph, so a second occurrence of a NODE or PAGE object number already in
        // this set is always a shape violation, regardless of whether it happens to loop back to
        // an ancestor. A repeated /Kids ARRAY object is a different case: those two clauses name
        // node and page objects specifically, not the array sitting between them, so two sibling
        // nodes sharing one, even empty, /Kids array object is not itself a spec violation. This
        // reader folds that case into the same visited set and the same PageTreeCycle report
        // anyway, as this reader's own rule rather than a spec requirement: it is the fix, kept
        // since round 1, for the exponential walk a shared array can otherwise force (see
        // SharedKidsArrayObject_isDetectedAsACycle_beforeExhaustingAnyBudget in PageTreeTests), and
        // this guard cannot cheaply tell a genuine ancestor cycle apart from a merely-reused node,
        // page, or /Kids array object anyway, so every repeat is reported the same way rather than
        // trying to classify which kind it is. Object numbers are unique within one file's
        // cross-reference table, so folding node, page, and /Kids-array identities into a single set
        // cannot false-positive one against another.
        var visited = new HashSet<int>();
        if (rootObjectNumber != 0)
            visited.Add(rootObjectNumber);
        if (rootKidsArrayObjectNumber != 0)
            visited.Add(rootKidsArrayObjectNumber);

        var rootEffective = ComputeEffectiveAttributes(
            reader, cache, diagnostics, rootDict, rootObjectNumber, default);

        var stack = new Stack<Frame>();
        stack.Push(new Frame(rootKids, rootEffective));

        var kidsExamined = 0;

        // The FIRST PageTreeDepthExceeded report of this walk uses ReportRetained (see below); a
        // second or later one against a different node is an ordinary Report, since only one entry
        // per walk is needed to tell a caller the page list may be incomplete for this reason.
        var depthLimitReported = false;

        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            if (frame.NextIndex >= frame.Kids.Count)
            {
                stack.Pop();
                continue;
            }

            var kidRaw = frame.Kids[frame.NextIndex++];

            kidsExamined++;
            if (kidsExamined > MaxKidsExamined)
            {
                // ReportRetained, not Report: the walk ends here, so this is the only chance this
                // condition ever has to reach a caller, on exactly the document large enough to
                // have already exhausted MaxDiagnostics on something else first.
                diagnostics.ReportRetained(
                    PdfReaderDiagnosticCode.PageTreeNodeLimitExceeded,
                    $"The page tree examined more than {MaxKidsExamined} /Kids array elements; the "
                    + "walk stopped there.");
                return pages;
            }

            // Resolved BEFORE the visited/cycle check below, not the raw element's own single-hop
            // object number: with TryResolve now following chains, two different /Kids entries can
            // each be a reference to a DIFFERENT alias object that both, one or more hops later,
            // resolve to the SAME page or node. kidObjectNumber is that terminal identity (the last
            // reference actually followed, see TryResolve), so the second alias is caught as a
            // repeat here instead of being walked (and, for a node, expanded) a second time under a
            // different object number.
            if (!TryResolve(reader, cache, kidRaw, out var kidResolved, out var kidObjectNumber)
                || kidResolved is not PdfDictionary kidDict)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeKidNotDictionary,
                    "A /Kids array element did not resolve to a dictionary; it was skipped.",
                    NullIfZero(kidObjectNumber));
                continue;
            }

            if (kidObjectNumber != 0 && !visited.Add(kidObjectNumber))
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeCycle,
                    $"Object {kidObjectNumber} appears more than once in the page tree (ISO 32000-2 "
                    + "§7.7.3.2 and §7.7.3.3 each forbid a repeated node or page object); the repeat "
                    + "was skipped.",
                    kidObjectNumber);
                continue;
            }

            var kind = ClassifyNode(
                reader, cache, diagnostics, kidDict, kidObjectNumber, out var kidKids,
                out var kidKidsArrayObjectNumber);
            if (kind == NodeKind.Skip)
                continue;

            if (kind == NodeKind.Node)
            {
                if (kidKidsArrayObjectNumber != 0 && !visited.Add(kidKidsArrayObjectNumber))
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.PageTreeCycle,
                        $"The /Kids array object {kidKidsArrayObjectNumber} was already used "
                        + "elsewhere in the page tree; the repeat was skipped.",
                        kidKidsArrayObjectNumber);
                    continue;
                }

                if (stack.Count >= MaxDepth)
                {
                    var message = $"The page tree nests more than {MaxDepth} levels deep; the "
                        + "walk stopped descending past that depth.";
                    var depthObjectNumber = NullIfZero(kidObjectNumber);
                    var depthCode = PdfReaderDiagnosticCode.PageTreeDepthExceeded;
                    if (depthLimitReported)
                    {
                        diagnostics.Report(depthCode, message, depthObjectNumber);
                    }
                    else
                    {
                        diagnostics.ReportRetained(depthCode, message, depthObjectNumber);
                        depthLimitReported = true;
                    }
                    continue; // Skip this subtree; siblings already queued elsewhere still walk.
                }

                var kidEffective = ComputeEffectiveAttributes(
                    reader, cache, diagnostics, kidDict, kidObjectNumber, frame.Effective);
                stack.Push(new Frame(kidKids, kidEffective));
                continue;
            }

            // Leaf.
            if (pages.Count >= MaxLeaves)
            {
                // ReportRetained for the same reason as PageTreeNodeLimitExceeded above: the walk
                // ends right here, so this is the one chance this condition gets to reach a caller.
                diagnostics.ReportRetained(
                    PdfReaderDiagnosticCode.PageTreeLeafLimitExceeded,
                    $"The page tree has more than {MaxLeaves} page leaves; the walk stopped there.");
                return pages;
            }

            pages.Add(BuildPage(
                reader, cache, diagnostics, pages.Count, kidObjectNumber, kidDict, frame.Effective));
        }

        return pages;
    }

    private enum NodeKind { Node, Leaf, Skip }

    /// <summary>
    /// Classifies a page-tree dictionary by its own <c>/Type</c>, preferring it over the mere
    /// presence of <c>/Kids</c> when the two disagree (ISO 32000-2 §7.7.3.2 Table 30 and §7.7.3.3
    /// Table 31 each make the respective entry Required): <c>/Type /Page</c> is always a leaf,
    /// <c>/Type /Pages</c> is always a node, a <c>/Type</c> naming anything else, including
    /// <c>/Type /Template</c>, is skipped outright, and no <c>/Type</c> at all falls back to the
    /// structural tell, a node when <paramref name="kidsArray"/> is non-null, a leaf otherwise.
    /// <c>/Type</c> resolves through the same reference-chain-following path (
    /// <see cref="ResolveOrAbsent(PdfDocumentReader, FailedResolveCache, PdfObject?, out bool)"/>)
    /// as everything else in this walker: ISO 32000-2 §7.3.7 makes only a dictionary's KEYS
    /// direct, not its values, so a <c>/Type</c> reached through one or
    /// more indirect references is classified exactly the same as one written inline.
    /// <c>/Type /Template</c> is skipped rather than treated as a leaf even though Table 31's own
    /// Type row admits it ("shall be Page for a page object or Template for an invisible Template
    /// page", ISO 32000-2 §7.7.3.3): that same Table 31, in its Parent row, exempts Template
    /// outright ("Objects of Type Template shall have no Parent key"), a rule §12.7.7 repeats, so
    /// a Template page has no parent and can never legitimately sit in any node's <c>/Kids</c> in
    /// the first place. No diagnostics: this is the pure classification decision, shared by
    /// <see cref="ClassifyNode"/> (which adds the <c>/Kids</c>-shape diagnostics for a node
    /// reached through <c>/Kids</c>) and
    /// <see cref="Walk"/>'s own root check (which reports a non-node classification as
    /// <see cref="PdfReaderDiagnosticCode.PageTreeMissing"/> instead).
    /// </summary>
    private static NodeKind ClassifyByType(
        PdfDocumentReader reader, FailedResolveCache cache, PdfDictionary dict, PdfArray? kidsArray,
        out PdfName? type)
    {
        type = ResolveOrAbsent(reader, cache, dict.Get(PdfName.Type), out _) as PdfName;

        if (type is not null && type.Equals(PdfName.Page))
            return NodeKind.Leaf;

        if (type is not null && !type.Equals(PdfName.Pages))
            return NodeKind.Skip;

        if (type is not null) // /Type /Pages
            return NodeKind.Node;

        // No /Type at all: fall back to the structural tell, tolerated silently either way.
        return kidsArray is not null ? NodeKind.Node : NodeKind.Leaf;
    }

    /// <summary>
    /// Classifies a dictionary reached through <c>/Kids</c> as an intermediate page-tree node or a
    /// page leaf, using <see cref="ClassifyByType"/>, and reports the shape problems that
    /// classification alone cannot express:
    /// <list type="bullet">
    /// <item><description><c>/Type /Pages</c> is always a node, even with no usable <c>/Kids</c> of
    /// its own, or with a stray <c>/Contents</c> entry, a Table 31 page-object key that describes
    /// nothing on a node since §7.7.3.4 only carries INHERITABLE attributes down to descendants:
    /// either one reports <see cref="PdfReaderDiagnosticCode.PageTreeNodeMalformed"/>, and a
    /// missing <c>/Kids</c> contributes zero children (a present-but-empty <c>/Kids []</c> is a
    /// legal empty subtree that stays silent); a stray <c>/Contents</c> is simply never treated as
    /// a page. <see cref="Walk"/>'s own root check applies the same <c>/Contents</c> rule to the
    /// root node, which never reaches this method.</description></item>
    /// <item><description><c>/Type /Page</c> is always a leaf; a stray <c>/Kids</c> alongside it
    /// reports <see cref="PdfReaderDiagnosticCode.PageTreeNodeMalformed"/> and is ignored:
    /// <c>/Type</c> wins.</description></item>
    /// <item><description><c>/Type</c> naming anything else (a stray <c>/Font</c>, the catalog
    /// itself, or <c>/Type /Template</c>, admitted by Table 31's own Type row but never legally
    /// reachable through <c>/Kids</c>, see <see cref="ClassifyByType"/>) reports
    /// <see cref="PdfReaderDiagnosticCode.PageTreeNodeMalformed"/> and is skipped entirely; it
    /// becomes neither a node nor a page.</description></item>
    /// <item><description>No <c>/Type</c> at all falls back to the structural tell: a node when
    /// <c>/Kids</c> resolves to an array, a leaf otherwise. Silently either way: plenty of real
    /// producers omit <c>/Type /Page</c> on a genuine leaf, and nothing about that omission is
    /// ambiguous once the structural tell settles it. An untyped node classified this way still
    /// gets the stray-<c>/Contents</c> check the first bullet above describes, run in the same
    /// code path regardless of whether <c>/Type</c> named <c>/Pages</c> explicitly or the
    /// structural tell decided it; the no-usable-<c>/Kids</c> half of that bullet cannot apply
    /// here, since a non-null <c>/Kids</c> array is exactly what made this dictionary classify as
    /// a node in the first place.</description></item>
    /// </list>
    /// A <c>/Kids</c> entry that fails to resolve (an <see cref="InvalidDataException"/>-throwing
    /// indirect target) is treated the same as one that resolves to the wrong type or is absent
    /// outright. See
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?)"/>.
    /// <para>
    /// A <c>/Kids</c> element that resolves to a stream object reaches this method as that stream's
    /// OWN dictionary, since <see cref="PdfDocumentReader.Resolve(PdfIndirectReference)"/> returns a
    /// stream object's dictionary rather than the stream itself, and is classified by exactly the
    /// same rules above: nothing here distinguishes a stream's dictionary from a plain one, so a
    /// content stream reached through <c>/Kids</c> with no <c>/Type</c> and no <c>/Kids</c> array of
    /// its own is a leaf under the "no /Type, no /Kids" rule, consistent with how every other
    /// untyped dictionary is treated.
    /// </para>
    /// </summary>
    // Shared with Walk's own root-node /Contents check, so both report identically worded
    // PageTreeNodeMalformed diagnostics for the same condition. /Contents is a Table 31
    // page-object entry with no row in Table 30's node listing, and ISO 32000-2 §7.7.3.4 carries
    // only INHERITABLE attributes down to descendants, so on a node this key describes nothing at
    // all: not a conformance violation on its own, just a meaningless entry worth flagging.
    private const string ContentsOnNodeProblem =
        "the node also carries /Contents, a Table 31 page-object entry with no row in Table 30's "
        + "node listing; ISO 32000-2 §7.7.3.4 carries only inheritable attributes down to "
        + "descendants, so it describes nothing here; the content was not treated as a page";

    private static NodeKind ClassifyNode(
        PdfDocumentReader reader, FailedResolveCache cache, DiagnosticSink diagnostics,
        PdfDictionary dict, int objectNumber, out PdfArray kids, out int kidsArrayObjectNumber)
    {
        var kidsRaw = dict.Get(PdfName.Kids);
        var kidsResolved = ResolveOrAbsent(
            reader, cache, kidsRaw, out var kidsAbsent, out kidsArrayObjectNumber);
        var kidsArray = kidsResolved as PdfArray;

        var kind = ClassifyByType(reader, cache, dict, kidsArray, out var type);

        switch (kind)
        {
            case NodeKind.Leaf:
                if (type is not null && !kidsAbsent) // type here can only be /Type /Page
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                        "A /Type /Page object also carries a /Kids array (ISO 32000-2 §7.7.3.3 "
                        + "Table 31 lists no /Kids entry for a page object; /Kids belongs to a "
                        + "page-tree node, not a page object); the /Kids array was ignored.",
                        NullIfZero(objectNumber));
                }
                kids = EmptyKids;
                kidsArrayObjectNumber = 0;
                return NodeKind.Leaf;

            case NodeKind.Skip:
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                    $"Object declares {type} where a page-tree node or page object was expected "
                    + "(ISO 32000-2 §7.7.3.2 Table 30, §7.7.3.3 Table 31); it was skipped.",
                    NullIfZero(objectNumber));
                kids = EmptyKids;
                kidsArrayObjectNumber = 0;
                return NodeKind.Skip;

            default: // Node
                {
                    // Both problems below are collected into ONE report rather than two:
                    // DiagnosticSink dedupes by (code, objectNumber, pageIndex), so a second Report
                    // call for the same object and code here would be silently dropped, not
                    // appended.
                    List<string>? problems = null;

                    _ = ResolveOrAbsent(
                        reader, cache, dict.Get(PdfName.Contents), out var contentsAbsent);
                    if (!contentsAbsent)
                        (problems ??= []).Add(ContentsOnNodeProblem);

                    if (kidsArray is null)
                    {
                        (problems ??= []).Add(
                            "the node has no usable /Kids array (ISO 32000-2 §7.7.3.2 Table 30 "
                            + "makes it Required); it contributes no children");
                    }

                    if (problems is not null)
                    {
                        diagnostics.Report(
                            PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                            string.Join("; ", problems) + ".",
                            NullIfZero(objectNumber));
                    }

                    kids = kidsArray ?? EmptyKids;
                    if (kidsArray is null)
                        kidsArrayObjectNumber = 0;
                    return NodeKind.Node;
                }
        }
    }

    /// <summary>
    /// Builds one <see cref="PdfReadPage"/> from a leaf's own dictionary, resolving each of its
    /// four attributes against <paramref name="parent"/>'s already-effective chain and reporting
    /// every failure found on THIS object as one
    /// <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/> entry, not one per attribute:
    /// <see cref="DiagnosticSink"/> dedupes by (code, objectNumber, pageIndex), so a second
    /// <c>Report</c> call for this same leaf and code would be silently dropped rather than
    /// appended, losing every failure but the first one found. Collecting every failure first and
    /// reporting once is what lets a caller see all of them.
    /// </summary>
    private static PdfReadPage BuildPage(
        PdfDocumentReader reader, FailedResolveCache cache, DiagnosticSink diagnostics,
        int pageIndex, int objectNumber, PdfDictionary dict, EffectiveAttributes parent)
    {
        var failures = new List<string>();

        var mediaBoxRaw = dict.Get(PdfName.MediaBox);
        var mediaBoxOrNull = ResolveRectangleAttribute(
            reader, cache, objectNumber, "MediaBox", mediaBoxRaw, parent.MediaBox, pageIndex,
            failures, out var mediaBoxPresent);

        PdfRectangle mediaBox;
        if (mediaBoxOrNull is { } mb)
        {
            mediaBox = mb;
        }
        else
        {
            // Added only when MediaBox was never even attempted anywhere in the chain (absent here
            // AND at every ancestor): a malformed attempt anywhere already reported its own failure
            // at the point it was made (this leaf, just above, or an ancestor's own frame push in
            // ComputeEffectiveAttributes), so adding a second one here for the same underlying
            // defect would double-count it.
            if (!mediaBoxPresent && !parent.MediaBoxEverPresent)
            {
                failures.Add(
                    "MediaBox is missing (ISO 32000-2 §7.7.3.3 makes it Required); using the "
                    + "Letter default (this reader's own convention: the specification names no "
                    + "default).");
            }
            mediaBox = LetterFallback;
        }

        var cropBoxRaw = dict.Get(CropBoxKey);
        var cropBoxOrNull = ResolveRectangleAttribute(
            reader, cache, objectNumber, "CropBox", cropBoxRaw, parent.CropBox, pageIndex,
            failures, out _);

        // ISO 32000-2 §14.11.2.1: "If the bounds of the crop, trim, bleed or art box extends outside
        // of the bounds of the media box, a processor shall treat the box as its intersection with
        // the media box." Skipped when nothing in the chain ever supplied a CropBox at all
        // (cropBoxOrNull null): the unclipped value already equals mediaBox in that case, so
        // intersecting it with itself would just recompute the same rectangle. A plain shortcut
        // now, not a correctness fix: IntersectWithMediaBox's strict '<' already makes a
        // self-intersection safe on its own (a < a is false), so skipping this call risks nothing.
        var cropBoxUnclipped = cropBoxOrNull ?? mediaBox;
        var cropBox = cropBoxOrNull is not null
            ? IntersectWithMediaBox(cropBoxUnclipped, mediaBox, failures)
            : cropBoxUnclipped;

        var rotateRaw = dict.Get(PdfName.Rotate);
        var rotate = ResolveRotateAttribute(
            reader, cache, objectNumber, rotateRaw, parent.Rotate, pageIndex, failures) ?? 0;

        if (failures.Count > 0)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid, string.Join(" ", failures),
                NullIfZero(objectNumber), null, pageIndex);
        }

        var resourcesRaw = dict.Get(PdfName.Resources);
        var resources = ResolveResourcesAttribute(reader, cache, resourcesRaw, parent.Resources);

        return new PdfReadPage(pageIndex, objectNumber, dict, mediaBox, cropBox, rotate, resources);
    }

    /// <summary>
    /// ISO 32000-2 §7.9.5's NOTE records that rectangles can have zero width or height, so a crop
    /// box that touches the media box's edge, or sits entirely within it with no area of its own,
    /// is kept as the zero-width (or zero-height) intersection rather than replaced with the media
    /// box: the intersection collapses to that shape, which is not necessarily the crop box's own
    /// bytes verbatim (one only touching the media box at a single edge is clipped down to that
    /// edge, the same as any other intersection). Only a crop box that shares NO overlap with the
    /// media box on either axis (a genuinely disjoint rectangle, which clips every page's content
    /// out of existence and is never what a producer intended even when the bytes technically
    /// allow it) is treated as malformed and falls back to the media box instead, appending its
    /// own failure text to <paramref name="failures"/> rather than reporting it directly, see
    /// <see cref="BuildPage"/>.
    /// </summary>
    private static PdfRectangle IntersectWithMediaBox(
        PdfRectangle cropBox, PdfRectangle mediaBox, List<string> failures)
    {
        var x0 = Math.Max(cropBox.LlX, mediaBox.LlX);
        var y0 = Math.Max(cropBox.LlY, mediaBox.LlY);
        var x1 = Math.Min(cropBox.UrX, mediaBox.UrX);
        var y1 = Math.Min(cropBox.UrY, mediaBox.UrY);

        if (x1 < x0 || y1 < y0)
        {
            failures.Add(
                "CropBox does not overlap MediaBox; ISO 32000-2 §14.11.2.1's intersection would "
                + "be empty, so this reader falls back to MediaBox (its own convention).");
            return mediaBox;
        }

        return new PdfRectangle(x0, y0, x1, y1);
    }

    /// <summary>
    /// Names <paramref name="objectNumber"/> for a diagnostic message, without ever printing "object
    /// 0": object number 0 here means the dictionary was reached as a direct value rather than
    /// through an indirect reference (a direct <c>/Kids</c> element, or a catalog's <c>/Pages</c>
    /// entry embedded inline), and 0 is otherwise reserved as the free-object-list head (ISO 32000-2
    /// §7.5.4), so printing it as an object number would misleadingly suggest a real one.
    /// <paramref name="pageIndex"/> distinguishes the two direct cases: <see langword="null"/> for a
    /// page-tree node (as seen by <see cref="ComputeEffectiveAttributes"/>), non-null for a page's
    /// own dictionary (as seen by <see cref="BuildPage"/>).
    /// </summary>
    private static string DescribeSource(int objectNumber, int? pageIndex) =>
        objectNumber != 0
            ? $"object {objectNumber}"
            : pageIndex is null
                ? "a direct page-tree node"
                : "the page dictionary";

    /// <summary>
    /// Resolves one rectangle-valued inheritable attribute (ISO 32000-2 §7.7.3.4) at a single level
    /// of the chain: <paramref name="raw"/> is that level's own entry, already looked up by the
    /// caller (<see langword="null"/> when the level does not define the key at all).
    /// <paramref name="present"/> reports whether <paramref name="raw"/> counted as present at
    /// all (see
    /// <see cref="ResolveOrAbsent(PdfDocumentReader, FailedResolveCache, PdfObject?, out bool)"/>);
    /// a present but unresolvable or wrong-shaped value
    /// (not a 4-element numeric array, ISO 32000-2 §7.9.5) appends its own failure text to
    /// <paramref name="failures"/>, naming the object that supplied it, for the caller to report
    /// as one <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/> entry alongside whatever
    /// else failed on the same object (see <see cref="BuildPage"/> and
    /// <see cref="ComputeEffectiveAttributes"/>), and this level falls through to
    /// <paramref name="inherited"/>, the parent frame's own already-resolved effective value,
    /// exactly as an absent entry would. This is what makes the walk's attribute resolution
    /// O(nodes) rather than O(nodes × depth): each node computes its own effective value once,
    /// from its own entry and its parent's effective value, instead of every leaf below it
    /// re-scanning the whole chain (see <see cref="MaxKidsExamined"/>'s own remarks for why that
    /// difference matters).
    /// Deliberately never consults a node's own <c>/Parent</c> entry: a forged one must not be able
    /// to redirect inheritance away from the chain the walk actually descended.
    /// </summary>
    private static PdfRectangle? ResolveRectangleAttribute(
        PdfDocumentReader reader, FailedResolveCache cache, int objectNumber, string keyName,
        PdfObject? raw, PdfRectangle? inherited, int? pageIndex, List<string> failures,
        out bool present)
    {
        var resolved = ResolveOrAbsent(reader, cache, raw, out var absent);
        present = !absent;
        if (absent)
            return inherited;

        if (resolved is not null && TryReadRectangle(reader, cache, resolved, out var rect))
            return rect;

        // A raw entry present but unresolvable at all (a cycle, too many chained references, or a
        // target whose own parse threw) reads differently from one that resolved cleanly to a value
        // of the wrong shape, so the message distinguishes them rather than blaming "did not resolve
        // to a 4-element numeric array" on a chain that never produced any value to check the shape
        // of in the first place.
        var reason = resolved is null
            ? "names a reference this reader could not resolve at all (a cycle, too many chained "
              + "references, or an object that failed to parse)"
            : "did not resolve to a 4-element numeric array (ISO 32000-2 §7.9.5)";
        failures.Add(
            $"{keyName} on {DescribeSource(objectNumber, pageIndex)} {reason}; the nearest valid "
            + "ancestor value, if any, is used instead.");

        return inherited;
    }

    private static bool TryReadRectangle(
        PdfDocumentReader reader, FailedResolveCache cache, PdfObject raw, out PdfRectangle rect)
    {
        rect = LetterFallback;
        if (raw is not PdfArray arr || arr.Count != 4)
            return false;

        Span<double> values = stackalloc double[4];
        for (var i = 0; i < 4; i++)
        {
            var element = TryResolve(reader, cache, arr[i], out var resolved) ? resolved : null;
            if (element is null || !TryReadNumber(element, out values[i]))
                return false;
        }

        // ISO 32000-2 §7.9.5 defines a rectangle by its corners without requiring lower-left to
        // precede upper-right numerically: a writer that emits [urx ury llx lly] is unusual but not
        // itself a violation, so this normalises rather than treating it as malformed.
        var x0 = Math.Min(values[0], values[2]);
        var x1 = Math.Max(values[0], values[2]);
        var y0 = Math.Min(values[1], values[3]);
        var y1 = Math.Max(values[1], values[3]);
        rect = new PdfRectangle(x0, y0, x1, y1);
        return true;
    }

    /// <summary>
    /// Resolves <c>/Rotate</c> at a single level of the chain, the same nearest-defined-level-wins
    /// way as <see cref="ResolveRectangleAttribute"/>: <paramref name="raw"/> is this level's own
    /// entry, and a present value that is not a multiple of 90 falls through to
    /// <paramref name="inherited"/> after appending its own failure text to
    /// <paramref name="failures"/> (see <see cref="ResolveRectangleAttribute"/>'s doc for why this
    /// does not report directly). Unlike <c>/MediaBox</c>, <c>/Rotate</c> is optional (ISO 32000-2
    /// Table 31), so an entry
    /// <see cref="ResolveOrAbsent(PdfDocumentReader, FailedResolveCache, PdfObject?, out bool)"/>
    /// counts as absent (omitted outright,
    /// null, or a reference chain that resolves to either of those, ISO 32000-2 §7.3.9) falls
    /// through to <paramref name="inherited"/> without any failure. A chain that is present but
    /// unusable (a cycle, too many hops, or a target that failed to parse) is not silent the same
    /// way: <c>resolved</c> comes back <see langword="null"/> without <c>isNumber</c> being true,
    /// so it takes the same "did not resolve to a number" failure branch below as any other
    /// wrong-typed value, and <paramref name="inherited"/> only ends up defaulted to 0 by
    /// <see cref="BuildPage"/>'s own <c>?? 0</c> when there is no ancestor value to fall back to.
    /// </summary>
    private static int? ResolveRotateAttribute(
        PdfDocumentReader reader, FailedResolveCache cache, int objectNumber, PdfObject? raw,
        int? inherited, int? pageIndex, List<string> failures)
    {
        var resolved = ResolveOrAbsent(reader, cache, raw, out var absent);
        if (absent)
            return inherited;

        double number = 0;
        var isNumber = resolved is not null && TryReadNumber(resolved, out number);

        // Checked in double space before any cast, so a huge (but still 90-divisible) value cannot
        // overflow int on the way to being folded below (ISO 32000-2 §7.7.3.3: "The value shall be
        // a multiple of 90.").
        if (isNumber && number % 90 == 0)
            return (int)((number % 360 + 360) % 360);

        var source = DescribeSource(objectNumber, pageIndex);
        var reason = isNumber
            ? $"Rotate {number} on {source} is not a multiple of 90 (ISO 32000-2 §7.7.3.3)"
            : $"Rotate on {source} did not resolve to a number";
        failures.Add($"{reason}; the nearest valid ancestor value, if any, is used instead.");

        return inherited;
    }

    /// <summary>
    /// Resolves <c>/Resources</c> at a single level of the chain to <paramref name="raw"/>'s target
    /// when it is a dictionary, or <paramref name="inherited"/> otherwise. Unlike the geometry
    /// attributes, an unresolvable or wrong-typed candidate is skipped silently here rather than
    /// reported: Table 31 makes <c>/Resources</c> Required unconditionally, and its own
    /// accommodation for a page that draws nothing is an empty dictionary, not an absent key ("If
    /// the page requires no resources, the value of this entry shall be an empty dictionary"). A
    /// <see langword="null"/> result here is this reader's own leniency toward a producer that
    /// omitted the key outright instead of writing that empty dictionary, not a case the spec
    /// itself sanctions, which is why it stays silent rather than being treated the way a missing
    /// <c>/MediaBox</c> is, always worth a diagnostic since nothing about that one is optional.
    /// </summary>
    private static PdfDictionary? ResolveResourcesAttribute(
        PdfDocumentReader reader, FailedResolveCache cache, PdfObject? raw, PdfDictionary? inherited)
    {
        var resolved = ResolveOrAbsent(reader, cache, raw, out var absent);
        return !absent && resolved is PdfDictionary dict ? dict : inherited;
    }

    /// <summary>
    /// Computes the effective inherited attribute values for a node's <see cref="Frame"/> at the
    /// moment it is pushed: its own <c>/MediaBox</c>, <c>/CropBox</c>, <c>/Rotate</c>, and
    /// <c>/Resources</c> where each resolves validly, falling back to <paramref name="parent"/>'s
    /// own already-computed effective values otherwise. Every malformed own entry is collected
    /// first and reported here as ONE <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>
    /// entry against this node (<see cref="PdfReaderDiagnostic.PageIndex"/> null, since no leaf has
    /// been reached yet) naming every failing attribute, not once per leaf that happens to inherit
    /// through it (which is what keeps the whole walk's attribute work O(nodes)) and not once per
    /// attribute either (<see cref="DiagnosticSink"/> dedupes by (code, objectNumber, pageIndex),
    /// so a second <c>Report</c> call for this node and code would be silently dropped, not
    /// appended).
    /// See <see cref="MaxKidsExamined"/>'s own remarks and
    /// <see cref="ResolveRectangleAttribute"/>'s doc for why the per-leaf part matters, and
    /// <see cref="BuildPage"/> for the leaf-side counterpart of the per-attribute part.
    /// </summary>
    private static EffectiveAttributes ComputeEffectiveAttributes(
        PdfDocumentReader reader, FailedResolveCache cache, DiagnosticSink diagnostics,
        PdfDictionary dict, int objectNumber, EffectiveAttributes parent)
    {
        var failures = new List<string>();

        var mediaBoxRaw = dict.Get(PdfName.MediaBox);
        var mediaBox = ResolveRectangleAttribute(
            reader, cache, objectNumber, "MediaBox", mediaBoxRaw, parent.MediaBox, null, failures,
            out var mediaBoxPresent);
        var mediaBoxEverPresent = mediaBoxPresent || parent.MediaBoxEverPresent;

        var cropBoxRaw = dict.Get(CropBoxKey);
        var cropBox = ResolveRectangleAttribute(
            reader, cache, objectNumber, "CropBox", cropBoxRaw, parent.CropBox, null, failures,
            out _);

        var rotateRaw = dict.Get(PdfName.Rotate);
        var rotate = ResolveRotateAttribute(
            reader, cache, objectNumber, rotateRaw, parent.Rotate, null, failures);

        if (failures.Count > 0)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid, string.Join(" ", failures),
                NullIfZero(objectNumber), null, null);
        }

        var resourcesRaw = dict.Get(PdfName.Resources);
        var resources = ResolveResourcesAttribute(reader, cache, resourcesRaw, parent.Resources);

        return new EffectiveAttributes(mediaBox, mediaBoxEverPresent, cropBox, rotate, resources);
    }

    /// <summary>
    /// Resolves <paramref name="raw"/>, following a chain of indirect references rather than
    /// stopping at the first one: ISO 32000-2 §7.3.10's 2020 NOTE says such chains are permitted
    /// ("PDF syntax thus permits chains of such objects."), and a later paragraph of the same
    /// subclause says following one all the way is required, not optional ("Except where
    /// documented to the contrary, any object value may be a direct or an indirect reference; the
    /// semantics are equivalent"), so a dictionary entry that names a reference to a reference
    /// (to a reference...) is resolved the same as if it named the final value directly. A cycle
    /// (a chain that returns to an object number already seen earlier in the SAME chain, including
    /// a self-reference) or a chain longer than <see cref="MaxReferenceChainHops"/> returns
    /// <see langword="false"/> with <paramref name="resolved"/> <see langword="null"/>: the
    /// reference itself is real syntax, so this is "present but unusable", the same outcome a target
    /// that throws while parsing already gets below, not a new condition that needs its own
    /// diagnostic. <paramref name="cache"/> is consulted before every hop that would otherwise repeat
    /// a resolution this walk already knows fails, and updated after every hop that fails, so a chain
    /// sharing a failing object with an earlier resolve in this walk costs one real parse, not one
    /// per caller; see <see cref="FailedResolveCache"/>. Still treats a target whose own parse throws
    /// <see cref="InvalidDataException"/> (e.g. a numeral too large for this parser,
    /// <c>PdfObjectParser.ParseLong</c> or <c>ParseReal</c>) the same as any other value the walk
    /// cannot use: skipped and reported through the usual page-tree diagnostics rather than aborting
    /// the walk. Precedent: reconstruction already recovers from this exception per object rather
    /// than per document, both in <c>PdfDocumentReader.TryParseObjectStreamMemberDirect</c> (a
    /// direct object-stream member parse that fails returns null instead of throwing) and in
    /// <c>XrefReconstructor.cs</c>'s own scan (a probe that fails to parse one candidate is charged
    /// and the scan resumes past it, rather than aborting).
    /// </summary>
    private static bool TryResolve(
        PdfDocumentReader reader, FailedResolveCache cache, PdfObject raw, out PdfObject? resolved) =>
        TryResolve(reader, cache, raw, out resolved, out _);

    /// <summary>
    /// The same as
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?)"/>,
    /// additionally reporting <paramref name="lastObjectNumber"/>: the object number of the LAST
    /// indirect reference this call actually followed, 0 when <paramref name="raw"/> is a direct
    /// value with no indirection at all. For a chain of two or more references, that is the
    /// reference whose OWN value is <paramref name="resolved"/> (or, on failure, the last one this
    /// call attempted before giving up), not necessarily the object <paramref name="raw"/> itself
    /// names: a caller tracking object identity (a repeat guard, a diagnostic naming which object
    /// is at fault) needs THIS number, not a raw single-hop read of <paramref name="raw"/>, since
    /// two different aliases can both, one or more hops later, resolve to the same object.
    /// </summary>
    private static bool TryResolve(
        PdfDocumentReader reader, FailedResolveCache cache, PdfObject raw, out PdfObject? resolved,
        out int lastObjectNumber)
    {
        PdfObject? current = raw;
        HashSet<(int ObjectNumber, int Generation)>? chainVisited = null;
        lastObjectNumber = 0;

        for (var hop = 0; hop < MaxReferenceChainHops; hop++)
        {
            if (current is not PdfIndirectReference reference)
            {
                resolved = current;
                return true;
            }

            var key = (reference.ObjectNumber, reference.Generation);
            lastObjectNumber = reference.ObjectNumber;

            if (cache.TryGet(key, out var cachedUnusable))
            {
                resolved = null;
                return !cachedUnusable;
            }

            if (!(chainVisited ??= []).Add(key))
            {
                // Cycle: this exact (object number, generation) pair already appeared earlier in
                // the same chain. Keyed on the pair, not the object number alone, so a hop from
                // (n, 0) to (n, 1) is never mistaken for a cycle: ISO 32000-2 §7.3.10 identifies an
                // object by number AND generation together, and a hop like that is a genuine
                // generation mismatch, PdfDocumentReader's own ObjectGenerationMismatch to report,
                // not a repeat of the same object. Present but unusable, the same outcome a parse
                // failure gets below, so the caller's own "did not resolve" report fires instead of
                // this method inventing a new diagnostic for it.
                cache.Add(key, unusable: true);
                resolved = null;
                return false;
            }

            try
            {
                current = reader.ResolveValue(reference);
            }
            catch (InvalidDataException)
            {
                cache.Add(key, unusable: true);
                resolved = null;
                return false;
            }

            if (current is null)
                cache.Add(key, unusable: false);
        }

        // MaxReferenceChainHops resolves have now run. The loop above checks the CURRENT value
        // before resolving, not after, so without this inspection a chain of EXACTLY
        // MaxReferenceChainHops references would wrongly fail here even though its last resolve,
        // the one made on the final iteration, already landed on a usable value; only a chain that
        // is STILL a reference after that many resolves has exceeded the cap.
        if (current is not PdfIndirectReference)
        {
            resolved = current;
            return true;
        }

        resolved = null;
        return false;
    }

    private static int? NullIfZero(int objectNumber) => objectNumber == 0 ? null : objectNumber;

    /// <summary>
    /// Resolves <paramref name="raw"/> once, tolerating <paramref name="raw"/> itself being C#
    /// <see langword="null"/> (an absent dictionary key), and reports via <paramref name="absent"/>
    /// whether the key counts as ISO 32000-2 §7.3.9 "not present". That clause states two rules that
    /// both collapse to the same outcome here: "An indirect object reference (see 7.3.10) to a
    /// nonexistent object shall be treated the same as a null object[, and s]pecifying the null
    /// object as the value of a dictionary entry shall be equivalent to omitting the entry entirely."
    /// So <paramref name="absent"/> is <see langword="true"/> when the key is genuinely omitted, when
    /// its own value is a direct <see cref="PdfNull"/>, or when it names a chain of one or more
    /// indirect references (§7.3.10's own NOTE permits chains; see
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?)"/>)
    /// that resolves to either of those.
    /// <para>
    /// A reference this parser could not resolve at all (see
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?)"/>,
    /// catching an
    /// <see cref="InvalidDataException"/>, or finding a cycle, or exceeding
    /// <see cref="MaxReferenceChainHops"/>) is NOT absent: the entry syntactically exists and names
    /// something, even if this parser cannot make sense of what it names, a different condition from
    /// §7.3.9's "nonexistent object". A reference that resolves cleanly to <see langword="null"/>
    /// WITHOUT throwing (a generation mismatch or an object-header mismatch, which
    /// <see cref="PdfDocumentReader"/> already reports its own way) lands on the absent side
    /// instead: ISO 32000-2 §7.3.10 identifies an object by its number AND generation together, so a
    /// mismatch on either names nothing, the same as a reference to an object number the
    /// cross-reference table has no entry for at all.
    /// </para>
    /// <para>
    /// One known gap: a reference into an object stream whose own member is missing currently throws
    /// <see cref="InvalidDataException"/> inside <c>PdfDocumentReader.ResolveFromObjectStream</c>
    /// rather than resolving cleanly to null the way a missing top-level object does, so this walker
    /// treats it as present rather than absent. That is a <see cref="PdfDocumentReader"/>-wide gap,
    /// not specific to the page tree, and is left for a follow-up rather than special-cased here.
    /// </para>
    /// Every presence check in this walker resolves through here instead of a bare
    /// <c>raw is null</c> check, which <see cref="PdfDictionary.Get"/> defeats for a direct null
    /// value (it hands back the <see cref="PdfNull"/> singleton, not a C# <see langword="null"/>) and
    /// which cannot see through a reference at all. <c>Filters.cs</c>'s own <c>/Filter</c> lookup
    /// already covers both the direct and the indirect case, for the same reason; where it differs is
    /// that it does not catch <see cref="InvalidDataException"/> at all, so an unparseable
    /// <c>/Filter</c> target simply propagates as an exception rather than being weighed against
    /// absence. This walker cannot afford that (a malformed <c>/Contents</c> deep in the tree must
    /// not abort the whole walk), which is why
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?)"/>
    /// catches the exception and this method treats the result as present rather than absent.
    /// Resolves <paramref name="raw"/>
    /// at most once, so a caller that also needs the resolved value gets it back from this same call
    /// rather than resolving it a second time.
    /// </summary>
    private static PdfObject? ResolveOrAbsent(
        PdfDocumentReader reader, FailedResolveCache cache, PdfObject? raw, out bool absent) =>
        ResolveOrAbsent(reader, cache, raw, out absent, out _);

    /// <summary>
    /// The same as
    /// <see cref="ResolveOrAbsent(PdfDocumentReader, FailedResolveCache, PdfObject?, out bool)"/>,
    /// additionally reporting <paramref name="lastObjectNumber"/> the way
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?, out int)"/>
    /// does: the terminal object identity a caller needs for a repeat guard or for naming which
    /// object is at fault, 0 when <paramref name="raw"/> is direct.
    /// </summary>
    private static PdfObject? ResolveOrAbsent(
        PdfDocumentReader reader, FailedResolveCache cache, PdfObject? raw, out bool absent,
        out int lastObjectNumber)
    {
        lastObjectNumber = 0;

        if (raw is null or PdfNull)
        {
            absent = true;
            return null;
        }

        if (!TryResolve(reader, cache, raw, out var resolved, out lastObjectNumber))
        {
            absent = false;
            return null;
        }

        absent = resolved is null or PdfNull;
        return resolved;
    }

    /// <summary>
    /// Remembers, for the rest of one <see cref="Walk"/> call, which (object number, generation)
    /// pairs already failed to resolve to anything usable, whether by resolving cleanly to nothing
    /// (a nonexistent object, a generation or header mismatch) or by throwing
    /// <see cref="InvalidDataException"/> while parsing, or by taking part in a cycle.
    /// <see cref="PdfDocumentReader"/>'s own resolve cache only remembers a SUCCESSFUL parse, so
    /// without this, every node whose <c>/Contents</c> or <c>/MediaBox</c> points at the same large,
    /// unparseable object would force a fresh parse attempt of it; consulting this cache first turns
    /// that into one real attempt per walk. The two failure kinds are kept apart (<see cref="Add"/>'s
    /// <c>unusable</c> flag) rather than merged into one "failed" bit: a target that
    /// threw, or cycled, is present but unusable, so the walker's normal "did not resolve to X"
    /// diagnostic should keep firing on every later hit; a target that resolved cleanly to nothing is
    /// genuinely absent (ISO 32000-2 §7.3.9), so it should keep falling through to an inherited
    /// value in silence, on the first hit and on every cached repeat alike. NOT bounded by
    /// <see cref="MaxKidsExamined"/> itself: one examined kid can drive several distinct
    /// <see cref="TryResolve(PdfDocumentReader, FailedResolveCache, PdfObject, out PdfObject?)"/>
    /// targets in the same pass (<c>/Type</c>, <c>/Kids</c>,
    /// <c>/Contents</c>, and, resolving its own effective attributes, <c>/MediaBox</c>,
    /// <c>/CropBox</c>, <c>/Rotate</c>, <c>/Resources</c>, and each rectangle's array elements), so
    /// this cache's size is a small multiple of <see cref="MaxKidsExamined"/>, not that ceiling
    /// itself, since a given (object number, generation) pair only occupies one entry here no
    /// matter how many kids point at it: the population is every distinct pair the walk fails to
    /// resolve, not every pair it resolves at all (a target that resolves successfully never
    /// reaches <see cref="Add"/>). A reference to an object the xref has no entry for adds one key
    /// here the same as a reference to an object the xref does list but whose own body fails to
    /// parse; whether the xref names the object decides nothing about whether this cache holds an
    /// entry for it. Lives only as long as one <see cref="Walk"/> call, since
    /// <see cref="PdfDocumentReader.Pages"/> walks the tree once and caches the result rather than
    /// calling <see cref="Walk"/> again.
    /// </summary>
    private sealed class FailedResolveCache
    {
        private readonly Dictionary<(int ObjectNumber, int Generation), bool> _failed = [];

        internal bool TryGet((int ObjectNumber, int Generation) key, out bool unusable) =>
            _failed.TryGetValue(key, out unusable);

        internal void Add((int ObjectNumber, int Generation) key, bool unusable) =>
            _failed[key] = unusable;
    }

    private static bool TryReadNumber(PdfObject? obj, out double value)
    {
        switch (obj)
        {
            case PdfInteger i: value = i.Value; return true;
            case PdfReal r: value = r.Value; return true;
            default: value = 0; return false;
        }
    }

    // ── Frame ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>One open page-tree node on the walk's own stack: its <c>/Kids</c>, an iteration
    /// cursor into that array, and the already-resolved <see cref="EffectiveAttributes"/> a
    /// descendant leaf inherits from it.</summary>
    private sealed class Frame(PdfArray kids, EffectiveAttributes effective)
    {
        internal PdfArray Kids { get; } = kids;
        internal EffectiveAttributes Effective { get; } = effective;
        internal int NextIndex;
    }

    /// <summary>
    /// A node's effective inheritable attribute values (ISO 32000-2 §7.7.3.4 Table 31): its own
    /// entries where each resolves validly, falling back to its parent's own effective values
    /// otherwise (never a node's own <c>/Parent</c> entry, see <see cref="ResolveRectangleAttribute"/>).
    /// <see cref="MediaBoxEverPresent"/> tracks whether <c>/MediaBox</c> was defined ANYWHERE from
    /// the root down to this node, valid or not, so a leaf that inherits a <see langword="null"/>
    /// <see cref="MediaBox"/> can tell "truly never defined" (Required, worth its own diagnostic)
    /// apart from "defined but malformed somewhere higher up" (already reported once, at the point
    /// it happened).
    /// </summary>
    private readonly record struct EffectiveAttributes(
        PdfRectangle? MediaBox, bool MediaBoxEverPresent, PdfRectangle? CropBox, int? Rotate,
        PdfDictionary? Resources);
}
