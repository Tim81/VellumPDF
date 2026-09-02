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
/// Iterative — an explicit stack rather than recursion, because tree depth is attacker-controlled
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
    /// processor's own choice (Annex C.1, informative, on practical processing limits) — see the
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
    /// </summary>
    internal const int MaxKidsExamined = 1_000_000;

    private static readonly PdfRectangle LetterFallback = new(0, 0, 612, 792);

    // Not one of PdfName's well-known statics (src/VellumPdf.Kernel/Core/PdfName.cs) — nothing
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

        var rootKidsRaw = rootDict.Get(PdfName.Kids);
        var rootKidsArrayObjectNumber = (rootKidsRaw as PdfIndirectReference)?.ObjectNumber ?? 0;
        if (rootKidsRaw is null || !TryResolve(reader, rootKidsRaw, out var rootKidsResolved)
            || rootKidsResolved is not PdfArray rootKids)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The page tree root has no usable /Kids array (ISO 32000-2 §7.7.3.2); the document "
                + "has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        // Object numbers seen as EITHER a page-tree node, a page object, or a /Kids array reached
        // through an indirect reference, anywhere in the walk. ISO 32000-2 §7.7.3.2 and §7.7.3.3
        // each forbid a repeated indirect reference to the same node or page object, and describe
        // /Kids as a tree, not a graph (nothing in the spec has two different nodes sharing one
        // /Kids array object), so a second occurrence of a number already in this set is always a
        // shape violation, whether it forms a genuine ancestor cycle, a redundant sibling reference,
        // or two parents claiming the same children by reusing one /Kids array object; all three are
        // reported and skipped the same way (see the PageTreeCycle reports below). Object numbers
        // are unique within one file's cross-reference table, so folding node, page, and
        // /Kids-array identities into a single set cannot false-positive one against another.
        var visited = new HashSet<int>();
        if (rootObjectNumber != 0)
            visited.Add(rootObjectNumber);
        if (rootKidsArrayObjectNumber != 0)
            visited.Add(rootKidsArrayObjectNumber);

        var stack = new Stack<Frame>();
        stack.Push(new Frame(rootKids, rootObjectNumber, ReadOwnAttributes(rootDict)));

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

                stack.Push(new Frame(kidKids, kidObjectNumber, ReadOwnAttributes(kidDict)));
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

            pages.Add(BuildPage(reader, diagnostics, pages.Count, kidObjectNumber, kidDict, stack));
        }

        return pages;
    }

    private enum NodeKind { Node, Leaf, Skip }

    /// <summary>
    /// Classifies <paramref name="dict"/> as an intermediate page-tree node or a page leaf, preferring
    /// its own <c>/Type</c> over the mere presence of <c>/Kids</c> when the two disagree (ISO 32000-2
    /// §7.7.3.2 Table 30 and §7.7.3.3 Table 31 each make the respective entry Required):
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

        var type = dict.Get(PdfName.Type) as PdfName;

        if (type is not null && type.Equals(PdfName.Page))
        {
            if (kidsRaw is not null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                    "A /Type /Page object also carries a /Kids array (ISO 32000-2 §7.7.3.3 Table 31 "
                    + "requires /Type /Page on a leaf); the /Kids array was ignored.",
                    NullIfZero(objectNumber));
            }
            kids = EmptyKids;
            kidsArrayObjectNumber = 0;
            return NodeKind.Leaf;
        }

        if (type is not null && !type.Equals(PdfName.Pages))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                $"Object declares {type} where a page-tree node or page object was expected "
                + "(ISO 32000-2 §7.7.3.2 Table 30, §7.7.3.3 Table 31); it was skipped.",
                NullIfZero(objectNumber));
            kids = EmptyKids;
            kidsArrayObjectNumber = 0;
            return NodeKind.Skip;
        }

        if (type is not null) // /Type /Pages
        {
            if (kidsArray is null)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeNodeMalformed,
                    "A /Type /Pages node has no usable /Kids array (ISO 32000-2 §7.7.3.2 Table 30 "
                    + "makes it Required); it contributes no children.",
                    NullIfZero(objectNumber));
                kids = EmptyKids;
                kidsArrayObjectNumber = 0;
                return NodeKind.Node;
            }
            kids = kidsArray;
            return NodeKind.Node;
        }

        // No /Type at all: fall back to the structural tell, tolerated silently either way.
        if (kidsArray is not null)
        {
            kids = kidsArray;
            return NodeKind.Node;
        }

        kids = EmptyKids;
        kidsArrayObjectNumber = 0;
        return NodeKind.Leaf;
    }

    private static PdfReadPage BuildPage(
        PdfDocumentReader reader, DiagnosticSink diagnostics, int pageIndex, int objectNumber,
        PdfDictionary dict, Stack<Frame> ancestors)
    {
        var mediaBox = ResolveRectangleAttribute(
            reader, diagnostics, dict, objectNumber, ancestors, PdfName.MediaBox,
            static f => f.Attributes.MediaBox, "MediaBox", LetterFallback,
            "the Letter default (this reader's own convention: the specification names no default)",
            required: true, pageIndex, out _);

        var cropBoxRaw = ResolveRectangleAttribute(
            reader, diagnostics, dict, objectNumber, ancestors, CropBoxKey,
            static f => f.Attributes.CropBox, "CropBox", mediaBox, "the page's own MediaBox",
            required: false, pageIndex, out var cropBoxFound);

        // ISO 32000-2 §14.11.2.1: "If the bounds of the crop, trim, bleed or art box extends outside
        // of the bounds of the media box, a processor shall treat the box as its intersection with
        // the media box." Skipped when nothing in the chain ever supplied a CropBox at all
        // (cropBoxFound false): cropBoxRaw already equals mediaBox in that case, and intersecting it
        // with itself risks a false "does not overlap" report if the media box itself happens to be
        // zero-area.
        var cropBox = cropBoxFound ? IntersectWithMediaBox(cropBoxRaw, mediaBox, diagnostics, pageIndex) : cropBoxRaw;

        var rotate = ResolveRotateAttribute(reader, diagnostics, dict, objectNumber, ancestors, pageIndex);

        var resources = ResolveResourcesAttribute(reader, dict, objectNumber, ancestors);

        return new PdfReadPage(pageIndex, objectNumber, dict, mediaBox, cropBox, rotate, resources);
    }

    /// <summary>
    /// ISO 32000-2 §7.9.5 permits a zero-area rectangle as written, but a zero-area crop box clips
    /// every page's content out of existence, which is never what a producer intended even when the
    /// bytes technically allow it, so an intersection that collapses to nothing (no overlap on
    /// either axis) is treated as malformed rather than honoured literally.
    /// </summary>
    private static PdfRectangle IntersectWithMediaBox(
        PdfRectangle cropBox, PdfRectangle mediaBox, DiagnosticSink diagnostics, int pageIndex)
    {
        var x0 = Math.Max(cropBox.LlX, mediaBox.LlX);
        var y0 = Math.Max(cropBox.LlY, mediaBox.LlY);
        var x1 = Math.Min(cropBox.UrX, mediaBox.UrX);
        var y1 = Math.Min(cropBox.UrY, mediaBox.UrY);

        if (x1 <= x0 || y1 <= y0)
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
    /// One inheritable attribute's candidate values (ISO 32000-2 §7.7.3.4), nearest first: the leaf's
    /// own entry (if it has one) followed by each ancestor frame's own entry that defines the key;
    /// a level that does not define it at all is skipped, not counted as a candidate. Deliberately
    /// never follows a page's own <c>/Parent</c> entry: a forged one must not be able to redirect
    /// inheritance away from the chain the walk actually descended. <paramref name="ancestors"/> is
    /// the walk's own frame stack, and a <see cref="Stack{T}"/> enumerates top-first: the frame
    /// pushed most recently, i.e. the immediate parent, so walking it in enumeration order already
    /// visits nearest ancestor first, with no separate distance bookkeeping needed.
    /// </summary>
    private static List<(PdfObject Raw, int SourceObjectNumber)> AttributeChain(
        PdfDictionary leaf, int leafObjectNumber, Stack<Frame> ancestors, PdfName key, Func<Frame, PdfObject?> selector)
    {
        var chain = new List<(PdfObject, int)>();
        if (leaf.Get(key) is { } ownRaw)
            chain.Add((ownRaw, leafObjectNumber));

        foreach (var frame in ancestors)
        {
            if (selector(frame) is { } raw)
                chain.Add((raw, frame.ObjectNumber));
        }

        return chain;
    }

    /// <summary>
    /// Resolves one rectangle-valued inheritable attribute by trying each candidate in
    /// <see cref="AttributeChain"/> order: the first one that resolves to a valid rectangle wins,
    /// outright, even over a well-formed ancestor value further up, matching §7.7.3.4's "nearest
    /// ancestor" rule. A candidate that fails to resolve, or does not resolve to a 4-element numeric
    /// array (ISO 32000-2 §7.9.5), reports <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/>
    /// naming the object that supplied it and is skipped rather than treated as final, so a
    /// malformed override does not hide a well-formed value further up the chain behind it. Only when
    /// no candidate at all resolves does <paramref name="fallback"/> apply.
    /// </summary>
    private static PdfRectangle ResolveRectangleAttribute(
        PdfDocumentReader reader, DiagnosticSink diagnostics, PdfDictionary leaf, int leafObjectNumber,
        Stack<Frame> ancestors, PdfName key, Func<Frame, PdfObject?> selector, string keyName,
        PdfRectangle fallback, string fallbackDescription, bool required, int pageIndex, out bool found)
    {
        var candidates = AttributeChain(leaf, leafObjectNumber, ancestors, key, selector);
        found = false;

        if (candidates.Count == 0)
        {
            if (required)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageAttributeInvalid,
                    $"{keyName} is missing (ISO 32000-2 §7.7.3.3 makes it Required); using "
                    + $"{fallbackDescription}.",
                    NullIfZero(leafObjectNumber), null, pageIndex);
            }
            return fallback;
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var (raw, sourceObjectNumber) = candidates[i];
            var resolved = TryResolve(reader, raw, out var value) ? value : null;
            if (resolved is not null && TryReadRectangle(reader, resolved, out var rect))
            {
                found = true;
                return rect;
            }

            var isLast = i == candidates.Count - 1;
            var trailer = isLast
                ? $"using {fallbackDescription}."
                : "continuing up the inheritance chain (ISO 32000-2 §7.7.3.4).";
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid,
                $"{keyName} on object {sourceObjectNumber} did not resolve to a 4-element numeric "
                + $"array (ISO 32000-2 §7.9.5); {trailer}",
                NullIfZero(sourceObjectNumber), null, pageIndex);
        }

        return fallback;
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
        // precede upper-right numerically — a writer that emits [urx ury llx lly] is unusual but not
        // itself a violation, so this normalises rather than treating it as malformed.
        var x0 = Math.Min(values[0], values[2]);
        var x1 = Math.Max(values[0], values[2]);
        var y0 = Math.Min(values[1], values[3]);
        var y1 = Math.Max(values[1], values[3]);
        rect = new PdfRectangle(x0, y0, x1, y1);
        return true;
    }

    /// <summary>Resolves <c>/Rotate</c> the same nearest-candidate-wins way as
    /// <see cref="ResolveRectangleAttribute"/>, defaulting to 0 when nothing in the chain ever
    /// resolves to a multiple of 90. Unlike <c>/MediaBox</c>, <c>/Rotate</c> is optional (ISO
    /// 32000-2 Table 31), so a chain with no candidate at all (as opposed to one whose candidates
    /// were all malformed) stays silent rather than reporting a diagnostic for the default.</summary>
    private static int ResolveRotateAttribute(
        PdfDocumentReader reader, DiagnosticSink diagnostics, PdfDictionary leaf, int leafObjectNumber,
        Stack<Frame> ancestors, int pageIndex)
    {
        var candidates = AttributeChain(leaf, leafObjectNumber, ancestors, PdfName.Rotate, static f => f.Attributes.Rotate);

        for (var i = 0; i < candidates.Count; i++)
        {
            var (raw, sourceObjectNumber) = candidates[i];
            var resolved = TryResolve(reader, raw, out var value) ? value : null;
            double number = 0;
            var isNumber = resolved is not null && TryReadNumber(resolved, out number);

            // Checked in double space before any cast, so a huge (but still 90-divisible) value
            // cannot overflow int on the way to being folded below (ISO 32000-2 §7.7.3.3: "The value
            // shall be a multiple of 90.").
            if (isNumber && number % 90 == 0)
                return (int)((number % 360 + 360) % 360);

            var isLast = i == candidates.Count - 1;
            var trailer = isLast ? "using 0." : "continuing up the inheritance chain (ISO 32000-2 §7.7.3.4).";
            var reason = isNumber
                ? $"Rotate {number} on object {sourceObjectNumber} is not a multiple of 90 (ISO 32000-2 §7.7.3.3)"
                : $"Rotate on object {sourceObjectNumber} did not resolve to a number";
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid, $"{reason}; {trailer}",
                NullIfZero(sourceObjectNumber), null, pageIndex);
        }

        return 0;
    }

    /// <summary>
    /// Resolves <c>/Resources</c> to the nearest candidate that resolves to a dictionary, or
    /// <see langword="null"/> when nothing in the chain does. Unlike the three geometry attributes,
    /// an unresolvable or wrong-typed candidate is skipped silently here rather than reported:
    /// <c>/Resources</c> is only Required on a page that actually draws (ISO 32000-2 §7.7.3.3 Table
    /// 31), and a page with no <c>/Contents</c> legitimately has none, so this reader treats any
    /// absence, outright or because a candidate did not pan out, the same way, whereas
    /// <c>/MediaBox</c> is Required on every page and its absence is always worth a diagnostic.
    /// </summary>
    private static PdfDictionary? ResolveResourcesAttribute(
        PdfDocumentReader reader, PdfDictionary leaf, int leafObjectNumber, Stack<Frame> ancestors)
    {
        foreach (var (raw, _) in AttributeChain(leaf, leafObjectNumber, ancestors, PdfName.Resources, static f => f.Attributes.Resources))
        {
            if (TryResolve(reader, raw, out var resolved) && resolved is PdfDictionary dict)
                return dict;
        }
        return null;
    }

    private static Attributes ReadOwnAttributes(PdfDictionary dict) => new(
        Resources: dict.Get(PdfName.Resources),
        MediaBox: dict.Get(PdfName.MediaBox),
        CropBox: dict.Get(CropBoxKey),
        Rotate: dict.Get(PdfName.Rotate));

    /// <summary>
    /// Resolves <paramref name="raw"/>, treating a target whose parse throws
    /// <see cref="InvalidDataException"/> (e.g. a numeral too large for this parser,
    /// <c>PdfObjectParser.ParseLong</c>) the same as any other value the walk cannot use: skipped
    /// and reported through the usual page-tree diagnostics rather than aborting the walk. Precedent:
    /// <c>PdfDocumentReader.cs</c>'s own object-stream member resolution and
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

    /// <summary>One open page-tree node on the walk's own stack — its <c>/Kids</c>, an iteration
    /// cursor into that array, and the raw (not yet resolved or normalised) inheritable attributes it
    /// defines itself, for the leaf-side resolvers to consult on behalf of every descendant.</summary>
    private sealed class Frame(PdfArray kids, int objectNumber, Attributes attributes)
    {
        internal PdfArray Kids { get; } = kids;
        internal int ObjectNumber { get; } = objectNumber;
        internal Attributes Attributes { get; } = attributes;
        internal int NextIndex;
    }

    /// <summary>
    /// A node's OWN inheritable attribute entries (ISO 32000-2 §7.7.3.4 Table 31) exactly as
    /// <c>dict.Get</c> returns them, not yet resolved through an indirect reference, and not yet
    /// normalised. Kept raw rather than resolved at frame-push time so a malformed indirect target
    /// is only ever reported against the specific leaf whose lookup reached it, with that leaf's page
    /// index attached, instead of aborting the walk before any leaf asks for it at all.
    /// </summary>
    private readonly record struct Attributes(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate);
}
