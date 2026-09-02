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

        if (reader.Catalog.Get(PdfName.Pages) is not { } rootRaw)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The document catalog has no /Pages entry (ISO 32000-2 §7.7.2); the document has no pages.");
            return pages;
        }

        var rootObjectNumber = (rootRaw as PdfIndirectReference)?.ObjectNumber ?? 0;
        if (!TryResolve(reader, rootRaw, out var rootResolved) || rootResolved is not PdfDictionary rootDict)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The document catalog's /Pages entry does not resolve to a dictionary "
                + "(ISO 32000-2 §7.7.3.2); the document has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        // The root itself is classified by the same /Type-and-structure rule as any other node
        // reached through /Kids (see ClassifyByType), not merely accepted on /Kids alone: §7.7.3.2
        // requires /Root/Pages to BE a page-tree node, so a root that is really a leaf (a stray
        // /Type /Page at the top) or something else entirely (the catalog dictionary reused as its
        // own /Pages, say) is not a page tree with zero children, it is not a page tree at all.
        var rootKidsRaw = rootDict.Get(PdfName.Kids);
        var rootKidsArrayObjectNumber = (rootKidsRaw as PdfIndirectReference)?.ObjectNumber ?? 0;
        var rootKidsArray = rootKidsRaw is not null && TryResolve(reader, rootKidsRaw, out var rootKidsResolved)
            ? rootKidsResolved as PdfArray
            : null;

        var rootKind = ClassifyByType(rootDict, rootKidsArray, out var rootType);
        if (rootKind != NodeKind.Node)
        {
            var found = rootType is not null ? $"/Type {rootType}" : "no /Type and no /Kids array";
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                $"The page tree root is not a page-tree node ({found}); ISO 32000-2 §7.7.3.2 "
                + "requires /Root/Pages to be one; the document has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
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

        // Object numbers seen as EITHER a page-tree node, a page object, or a /Kids array reached
        // through an indirect reference, anywhere in the walk. ISO 32000-2 §7.7.3.2 and §7.7.3.3
        // each forbid a repeated indirect reference to the same node or page object, and describe
        // /Kids as a tree, not a graph (nothing in the spec has two different nodes sharing one
        // /Kids array object), so a second occurrence of a number already in this set is always a
        // shape violation, regardless of whether it happens to loop back to an ancestor. This guard
        // cannot cheaply tell a genuine ancestor cycle apart from a merely-reused node, page, or
        // /Kids array object (two sibling nodes pointing at the same, even empty, /Kids array object
        // is not itself a cycle, only a reuse the spec forbids all the same), so every repeat is
        // reported under the one PageTreeCycle code below and skipped the same way, rather than
        // trying to classify which kind of repeat it is. Object numbers are unique within one file's
        // cross-reference table, so folding node, page, and /Kids-array identities into a single set
        // cannot false-positive one against another.
        var visited = new HashSet<int>();
        if (rootObjectNumber != 0)
            visited.Add(rootObjectNumber);
        if (rootKidsArrayObjectNumber != 0)
            visited.Add(rootKidsArrayObjectNumber);

        var rootEffective = ComputeEffectiveAttributes(reader, diagnostics, rootDict, rootObjectNumber, default);

        var stack = new Stack<Frame>();
        stack.Push(new Frame(rootKids, rootEffective));

        var kidsExamined = 0;

        while (stack.Count > 0)
        {
            var frame = stack.Peek();
            if (frame.NextIndex >= frame.Kids.Count)
            {
                stack.Pop();
                continue;
            }

            var kidRaw = frame.Kids[frame.NextIndex++];
            var kidObjectNumber = (kidRaw as PdfIndirectReference)?.ObjectNumber ?? 0;

            kidsExamined++;
            if (kidsExamined > MaxKidsExamined)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeNodeLimitExceeded,
                    $"The page tree examined more than {MaxKidsExamined} /Kids array elements; the "
                    + "walk stopped there.");
                return pages;
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

            if (!TryResolve(reader, kidRaw, out var kidResolved) || kidResolved is not PdfDictionary kidDict)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeKidNotDictionary,
                    "A /Kids array element did not resolve to a dictionary; it was skipped.",
                    NullIfZero(kidObjectNumber));
                continue;
            }

            var kind = ClassifyNode(
                reader, diagnostics, kidDict, kidObjectNumber, out var kidKids, out var kidKidsArrayObjectNumber);
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
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.PageTreeDepthExceeded,
                        $"The page tree nests more than {MaxDepth} levels deep; the walk stopped "
                        + "descending past that depth.",
                        NullIfZero(kidObjectNumber));
                    continue; // Skip this subtree; siblings already queued elsewhere still walk.
                }

                var kidEffective = ComputeEffectiveAttributes(reader, diagnostics, kidDict, kidObjectNumber, frame.Effective);
                stack.Push(new Frame(kidKids, kidEffective));
                continue;
            }

            // Leaf.
            if (pages.Count >= MaxLeaves)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeLeafLimitExceeded,
                    $"The page tree has more than {MaxLeaves} page leaves; the walk stopped there.");
                return pages;
            }

            pages.Add(BuildPage(reader, diagnostics, pages.Count, kidObjectNumber, kidDict, frame.Effective));
        }

        return pages;
    }

    private enum NodeKind { Node, Leaf, Skip }

    /// <summary>
    /// Classifies a page-tree dictionary by its own <c>/Type</c>, preferring it over the mere
    /// presence of <c>/Kids</c> when the two disagree (ISO 32000-2 §7.7.3.2 Table 30 and §7.7.3.3
    /// Table 31 each make the respective entry Required): <c>/Type /Page</c> is always a leaf,
    /// <c>/Type /Pages</c> is always a node, a <c>/Type</c> naming anything else is skipped
    /// outright, and no <c>/Type</c> at all falls back to the structural tell, a node when
    /// <paramref name="kidsArray"/> is non-null, a leaf otherwise. No diagnostics: this is the
    /// pure classification decision,
    /// shared by <see cref="ClassifyNode"/> (which adds the <c>/Kids</c>-shape diagnostics for a
    /// node reached through <c>/Kids</c>) and <see cref="Walk"/>'s own root check (which reports a
    /// non-node classification as <see cref="PdfReaderDiagnosticCode.PageTreeMissing"/> instead).
    /// </summary>
    private static NodeKind ClassifyByType(PdfDictionary dict, PdfArray? kidsArray, out PdfName? type)
    {
        type = dict.Get(PdfName.Type) as PdfName;

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
    /// its own, which reports <see cref="PdfReaderDiagnosticCode.PageTreeNodeMalformed"/> and
    /// contributes zero children, except a present-but-empty <c>/Kids []</c>, a legal empty subtree
    /// that stays silent.</description></item>
    /// <item><description><c>/Type /Page</c> is always a leaf; a stray <c>/Kids</c> alongside it
    /// reports <see cref="PdfReaderDiagnosticCode.PageTreeNodeMalformed"/> and is ignored:
    /// <c>/Type</c> wins.</description></item>
    /// <item><description><c>/Type</c> naming anything else (a stray <c>/Font</c>, the catalog
    /// itself) reports <see cref="PdfReaderDiagnosticCode.PageTreeNodeMalformed"/> and is skipped
    /// entirely; it becomes neither a node nor a page.</description></item>
    /// <item><description>No <c>/Type</c> at all falls back to the structural tell: a node when
    /// <c>/Kids</c> resolves to an array, a leaf otherwise. Silently either way: plenty of real
    /// producers omit <c>/Type /Page</c> on a genuine leaf, and nothing about that omission is
    /// ambiguous once the structural tell settles it.</description></item>
    /// </list>
    /// A <c>/Kids</c> entry that fails to resolve (an <see cref="InvalidDataException"/>-throwing
    /// indirect target) is treated the same as one that resolves to the wrong type or is absent
    /// outright. See <see cref="TryResolve"/>.
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
    private static NodeKind ClassifyNode(
        PdfDocumentReader reader, DiagnosticSink diagnostics, PdfDictionary dict, int objectNumber,
        out PdfArray kids, out int kidsArrayObjectNumber)
    {
        var kidsRaw = dict.Get(PdfName.Kids);
        kidsArrayObjectNumber = (kidsRaw as PdfIndirectReference)?.ObjectNumber ?? 0;
        var kidsArray = kidsRaw is not null && TryResolve(reader, kidsRaw, out var kidsResolved)
            ? kidsResolved as PdfArray
            : null;

        var kind = ClassifyByType(dict, kidsArray, out var type);

        switch (kind)
        {
            case NodeKind.Leaf:
                if (type is not null && kidsRaw is not null) // type here can only be /Type /Page
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                        "A /Type /Page object also carries a /Kids array (ISO 32000-2 §7.7.3.3 "
                        + "Table 31 requires /Type /Page on a leaf); the /Kids array was ignored.",
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
                if (kidsArray is null)
                {
                    diagnostics.Report(
                        PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                        "A /Type /Pages node has no usable /Kids array (ISO 32000-2 §7.7.3.2 Table "
                        + "30 makes it Required); it contributes no children.",
                        NullIfZero(objectNumber));
                    kids = EmptyKids;
                    kidsArrayObjectNumber = 0;
                    return NodeKind.Node;
                }
                kids = kidsArray;
                return NodeKind.Node;
        }
    }

    private static PdfReadPage BuildPage(
        PdfDocumentReader reader, DiagnosticSink diagnostics, int pageIndex, int objectNumber,
        PdfDictionary dict, EffectiveAttributes parent)
    {
        var mediaBoxRaw = dict.Get(PdfName.MediaBox);
        var mediaBoxOrNull = ResolveRectangleAttribute(
            reader, diagnostics, objectNumber, "MediaBox", mediaBoxRaw, parent.MediaBox, pageIndex);

        PdfRectangle mediaBox;
        if (mediaBoxOrNull is { } mb)
        {
            mediaBox = mb;
        }
        else
        {
            // Reported only when MediaBox was never even attempted anywhere in the chain (absent
            // here AND at every ancestor): a malformed attempt anywhere already reported its own
            // PageAttributeInvalid at the point it was made (this leaf, or an ancestor's own frame
            // push in ComputeEffectiveAttributes), so reporting a second one here for the same
            // underlying defect would double-count it.
            if (mediaBoxRaw is null && !parent.MediaBoxEverPresent)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageAttributeInvalid,
                    "MediaBox is missing (ISO 32000-2 §7.7.3.3 makes it Required); using the "
                    + "Letter default (this reader's own convention: the specification names no "
                    + "default).",
                    NullIfZero(objectNumber), null, pageIndex);
            }
            mediaBox = LetterFallback;
        }

        var cropBoxRaw = dict.Get(CropBoxKey);
        var cropBoxOrNull = ResolveRectangleAttribute(
            reader, diagnostics, objectNumber, "CropBox", cropBoxRaw, parent.CropBox, pageIndex);

        // ISO 32000-2 §14.11.2.1: "If the bounds of the crop, trim, bleed or art box extends outside
        // of the bounds of the media box, a processor shall treat the box as its intersection with
        // the media box." Skipped when nothing in the chain ever supplied a CropBox at all
        // (cropBoxOrNull null): the unclipped value already equals mediaBox in that case, and
        // intersecting it with itself risks a false "does not overlap" report if the media box
        // itself happens to be zero-area.
        var cropBoxUnclipped = cropBoxOrNull ?? mediaBox;
        var cropBox = cropBoxOrNull is not null
            ? IntersectWithMediaBox(cropBoxUnclipped, mediaBox, diagnostics, pageIndex)
            : cropBoxUnclipped;

        var rotateRaw = dict.Get(PdfName.Rotate);
        var rotate = ResolveRotateAttribute(
            reader, diagnostics, objectNumber, rotateRaw, parent.Rotate, pageIndex) ?? 0;

        var resourcesRaw = dict.Get(PdfName.Resources);
        var resources = ResolveResourcesAttribute(reader, resourcesRaw, parent.Resources);

        return new PdfReadPage(pageIndex, objectNumber, dict, mediaBox, cropBox, rotate, resources);
    }

    /// <summary>
    /// ISO 32000-2 §7.9.5's NOTE permits a zero-width or zero-height rectangle as written, so a
    /// crop box that touches the media box's edge, or sits entirely within it with no area of its
    /// own, is kept exactly as written rather than replaced. Only a crop box that shares NO overlap
    /// with the media box on either axis (a genuinely disjoint rectangle, which clips every page's
    /// content out of existence and is never what a producer intended even when the bytes
    /// technically allow it) is treated as malformed and falls back to the media box instead.
    /// </summary>
    private static PdfRectangle IntersectWithMediaBox(
        PdfRectangle cropBox, PdfRectangle mediaBox, DiagnosticSink diagnostics, int pageIndex)
    {
        var x0 = Math.Max(cropBox.LlX, mediaBox.LlX);
        var y0 = Math.Max(cropBox.LlY, mediaBox.LlY);
        var x1 = Math.Min(cropBox.UrX, mediaBox.UrX);
        var y1 = Math.Min(cropBox.UrY, mediaBox.UrY);

        if (x1 < x0 || y1 < y0)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid,
                "CropBox does not overlap MediaBox (ISO 32000-2 §14.11.2.1 requires their "
                + "intersection); using MediaBox instead.",
                null, null, pageIndex);
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
    /// caller (<see langword="null"/> when the level does not define the key at all). A present but
    /// unresolvable or wrong-shaped value (not a 4-element numeric array, ISO 32000-2 §7.9.5) reports
    /// <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/> naming the object that supplied it,
    /// and this level falls through to <paramref name="inherited"/>, the parent frame's own already-
    /// resolved effective value, exactly as an absent entry would. This is what makes the walk's
    /// attribute resolution O(nodes) rather than O(nodes × depth): each node computes its own
    /// effective value once, from its own entry and its parent's effective value, instead of every
    /// leaf below it re-scanning the whole ancestor chain (see <see cref="MaxKidsExamined"/>'s own
    /// remarks for why that difference matters). Deliberately never consults a node's own
    /// <c>/Parent</c> entry: a forged one must not be able to redirect inheritance away from the
    /// chain the walk actually descended.
    /// </summary>
    private static PdfRectangle? ResolveRectangleAttribute(
        PdfDocumentReader reader, DiagnosticSink diagnostics, int objectNumber, string keyName,
        PdfObject? raw, PdfRectangle? inherited, int? pageIndex)
    {
        if (raw is null)
            return inherited;

        var resolved = TryResolve(reader, raw, out var value) ? value : null;
        if (resolved is not null && TryReadRectangle(reader, resolved, out var rect))
            return rect;

        diagnostics.Report(
            PdfReaderDiagnosticCode.PageAttributeInvalid,
            $"{keyName} on {DescribeSource(objectNumber, pageIndex)} did not resolve to a "
            + "4-element numeric array (ISO 32000-2 §7.9.5); the nearest valid ancestor value, if "
            + "any, is used instead.",
            NullIfZero(objectNumber), null, pageIndex);

        return inherited;
    }

    private static bool TryReadRectangle(PdfDocumentReader reader, PdfObject raw, out PdfRectangle rect)
    {
        rect = LetterFallback;
        if (raw is not PdfArray arr || arr.Count != 4)
            return false;

        Span<double> values = stackalloc double[4];
        for (var i = 0; i < 4; i++)
        {
            var element = TryResolve(reader, arr[i], out var resolved) ? resolved : null;
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
    /// <paramref name="inherited"/> after reporting <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>.
    /// Unlike <c>/MediaBox</c>, <c>/Rotate</c> is optional (ISO 32000-2 Table 31), so an absent entry
    /// (<paramref name="raw"/> null) returns <paramref name="inherited"/> without any report, and a
    /// chain that never resolves at all is left for the caller to default to 0 silently.
    /// </summary>
    private static int? ResolveRotateAttribute(
        PdfDocumentReader reader, DiagnosticSink diagnostics, int objectNumber, PdfObject? raw,
        int? inherited, int? pageIndex)
    {
        if (raw is null)
            return inherited;

        var resolved = TryResolve(reader, raw, out var value) ? value : null;
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
        diagnostics.Report(
            PdfReaderDiagnosticCode.PageAttributeInvalid,
            $"{reason}; the nearest valid ancestor value, if any, is used instead.",
            NullIfZero(objectNumber), null, pageIndex);

        return inherited;
    }

    /// <summary>
    /// Resolves <c>/Resources</c> at a single level of the chain to <paramref name="raw"/>'s target
    /// when it is a dictionary, or <paramref name="inherited"/> otherwise. Unlike the geometry
    /// attributes, an unresolvable or wrong-typed candidate is skipped silently here rather than
    /// reported: <c>/Resources</c> is only Required on a page that actually draws (ISO 32000-2
    /// §7.7.3.3 Table 31), and a page with no <c>/Contents</c> legitimately has none, so this reader
    /// treats any absence, outright or because this level's own entry did not pan out, the same way,
    /// whereas <c>/MediaBox</c> is Required on every page and its absence is always worth a
    /// diagnostic.
    /// </summary>
    private static PdfDictionary? ResolveResourcesAttribute(
        PdfDocumentReader reader, PdfObject? raw, PdfDictionary? inherited)
    {
        if (raw is null)
            return inherited;

        return TryResolve(reader, raw, out var resolved) && resolved is PdfDictionary dict ? dict : inherited;
    }

    /// <summary>
    /// Computes the effective inherited attribute values for a node's <see cref="Frame"/> at the
    /// moment it is pushed: its own <c>/MediaBox</c>, <c>/CropBox</c>, <c>/Rotate</c>, and
    /// <c>/Resources</c> where each resolves validly, falling back to <paramref name="parent"/>'s
    /// own already-computed effective values otherwise. A malformed own entry is reported here,
    /// once, against this node (<see cref="PdfReaderDiagnostic.PageIndex"/> null, since no leaf has
    /// been reached yet), rather than once per leaf that happens to inherit through it, which is
    /// what keeps the whole walk's attribute work O(nodes). See <see cref="MaxKidsExamined"/>'s own
    /// remarks and <see cref="ResolveRectangleAttribute"/>'s doc for why that matters.
    /// </summary>
    private static EffectiveAttributes ComputeEffectiveAttributes(
        PdfDocumentReader reader, DiagnosticSink diagnostics, PdfDictionary dict, int objectNumber,
        EffectiveAttributes parent)
    {
        var mediaBoxRaw = dict.Get(PdfName.MediaBox);
        var mediaBox = ResolveRectangleAttribute(
            reader, diagnostics, objectNumber, "MediaBox", mediaBoxRaw, parent.MediaBox, pageIndex: null);
        var mediaBoxEverPresent = mediaBoxRaw is not null || parent.MediaBoxEverPresent;

        var cropBoxRaw = dict.Get(CropBoxKey);
        var cropBox = ResolveRectangleAttribute(
            reader, diagnostics, objectNumber, "CropBox", cropBoxRaw, parent.CropBox, pageIndex: null);

        var rotateRaw = dict.Get(PdfName.Rotate);
        var rotate = ResolveRotateAttribute(
            reader, diagnostics, objectNumber, rotateRaw, parent.Rotate, pageIndex: null);

        var resourcesRaw = dict.Get(PdfName.Resources);
        var resources = ResolveResourcesAttribute(reader, resourcesRaw, parent.Resources);

        return new EffectiveAttributes(mediaBox, mediaBoxEverPresent, cropBox, rotate, resources);
    }

    /// <summary>
    /// Resolves <paramref name="raw"/>, treating a target whose parse throws
    /// <see cref="InvalidDataException"/> (e.g. a numeral too large for this parser,
    /// <c>PdfObjectParser.ParseLong</c> or <c>ParseReal</c>) the same as any other value the walk
    /// cannot use: skipped and reported through the usual page-tree diagnostics rather than aborting
    /// the walk. Precedent: <c>PdfDocumentReader.cs</c>'s own object-stream member resolution and
    /// <c>PdfDocumentReader.SaveDecrypted.cs</c> already recover from this exception per object
    /// rather than per document.
    /// </summary>
    private static bool TryResolve(PdfDocumentReader reader, PdfObject raw, out PdfObject? resolved)
    {
        try
        {
            resolved = reader.ResolveValue(raw);
            return true;
        }
        catch (InvalidDataException)
        {
            resolved = null;
            return false;
        }
    }

    private static int? NullIfZero(int objectNumber) => objectNumber == 0 ? null : objectNumber;

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
