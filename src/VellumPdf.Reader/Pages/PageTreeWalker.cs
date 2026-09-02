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
/// <c>/Count</c> is never consulted. ISO 32000-2 §7.7.3.2's own NOTE calls it "redundant" with the
/// tree structure the <c>/Kids</c> arrays already encode, and real producers disagree with their own
/// <c>/Count</c> often enough — off by the pages a later edit added or removed without updating it —
/// that trusting it would misreport <see cref="PdfDocumentReader.PageCount"/> on ordinary files, not
/// just adversarial ones.
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

    private static readonly PdfRectangle LetterFallback = new(0, 0, 612, 792);

    // Not one of PdfName's well-known statics (src/VellumPdf.Kernel/Core/PdfName.cs) — nothing
    // outside the reader's page-tree walk needs a shared instance of this one yet.
    private static readonly PdfName CropBoxKey = new("CropBox");

    // Shared, read-only placeholder for a node classified as a page-tree node (ISO 32000-2 §7.7.3.2)
    // by its /Type alone, with no usable /Kids of its own — contributes zero children rather than
    // reporting a diagnostic, since an intermediate node with no descendants is not itself malformed.
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
        if (reader.ResolveValue(rootRaw) is not PdfDictionary rootDict)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The document catalog's /Pages entry does not resolve to a dictionary "
                + "(ISO 32000-2 §7.7.3.2); the document has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        if (ResolveOwn(reader, rootDict, PdfName.Kids) is not PdfArray rootKids)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageTreeMissing,
                "The page tree root has no usable /Kids array (ISO 32000-2 §7.7.3.2); the document "
                + "has no pages.",
                NullIfZero(rootObjectNumber));
            return pages;
        }

        // Object numbers seen as EITHER a page-tree node or a page object, anywhere in the walk.
        // ISO 32000-2 §7.7.3.2 and §7.7.3.3 each forbid a repeated indirect reference to the same
        // node or page object, so a second occurrence of a number already in this set is always a
        // shape violation — whether it forms a genuine ancestor cycle or merely a redundant sibling
        // reference — and both are reported and skipped the same way (see the PageTreeCycle report
        // below).
        var visited = new HashSet<int>();
        if (rootObjectNumber != 0)
            visited.Add(rootObjectNumber);

        var stack = new Stack<Frame>();
        stack.Push(new Frame(rootKids, rootObjectNumber, ReadOwnAttributes(reader, rootDict)));

        var depthExceededReported = false;

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

            if (reader.ResolveValue(kidRaw) is not PdfDictionary kidDict)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageTreeKidNotDictionary,
                    "A /Kids array element did not resolve to a dictionary; it was skipped.",
                    NullIfZero(kidObjectNumber));
                continue;
            }

            if (IsPagesNode(reader, kidDict, out var kidKids))
            {
                if (stack.Count >= MaxDepth)
                {
                    if (!depthExceededReported)
                    {
                        diagnostics.Report(
                            PdfReaderDiagnosticCode.PageTreeDepthExceeded,
                            $"The page tree nests more than {MaxDepth} levels deep; the walk stopped "
                            + "descending past that depth.",
                            NullIfZero(kidObjectNumber));
                        depthExceededReported = true;
                    }
                    continue; // Skip this subtree; siblings already queued elsewhere still walk.
                }

                stack.Push(new Frame(kidKids, kidObjectNumber, ReadOwnAttributes(reader, kidDict)));
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

    /// <summary>
    /// Whether <paramref name="dict"/> is an intermediate page-tree node rather than a page leaf: it
    /// has a <c>/Kids</c> array of its own, or declares <c>/Type /Pages</c> even without a usable
    /// one. A leaf that omits <c>/Type</c> entirely and has no <c>/Kids</c> is tolerated silently —
    /// §7.7.3.3 requires <c>/Type /Page</c>, but plenty of real files leave it off a genuine leaf,
    /// and nothing about that omission is ambiguous once <c>/Kids</c> is absent too.
    /// </summary>
    private static bool IsPagesNode(PdfDocumentReader reader, PdfDictionary dict, out PdfArray kids)
    {
        if (ResolveOwn(reader, dict, PdfName.Kids) is PdfArray ownKids)
        {
            kids = ownKids;
            return true;
        }

        if (dict.Get(PdfName.Type) is PdfName type && type.Equals(PdfName.Pages))
        {
            kids = EmptyKids;
            return true;
        }

        kids = EmptyKids;
        return false;
    }

    private static PdfReadPage BuildPage(
        PdfDocumentReader reader, DiagnosticSink diagnostics, int pageIndex, int objectNumber,
        PdfDictionary dict, Stack<Frame> ancestors)
    {
        var (mediaRaw, mediaSource) = FindInherited(reader, dict, objectNumber, ancestors, PdfName.MediaBox, static f => f.Attributes.MediaBox);
        var mediaBox = NormalizeRectangle(
            reader, mediaRaw, LetterFallback, requiredMissingDiagnostic: true, "MediaBox", mediaSource, pageIndex, diagnostics);

        var (cropRaw, cropSource) = FindInherited(reader, dict, objectNumber, ancestors, CropBoxKey, static f => f.Attributes.CropBox);
        var cropBox = NormalizeRectangle(
            reader, cropRaw, mediaBox, requiredMissingDiagnostic: false, "CropBox", cropSource, pageIndex, diagnostics);

        var (rotateRaw, rotateSource) = FindInherited(reader, dict, objectNumber, ancestors, PdfName.Rotate, static f => f.Attributes.Rotate);
        var rotate = NormalizeRotate(rotateRaw, rotateSource, pageIndex, diagnostics);

        var (resourcesRaw, _) = FindInherited(reader, dict, objectNumber, ancestors, PdfName.Resources, static f => f.Attributes.Resources);

        return new PdfReadPage(pageIndex, objectNumber, dict, mediaBox, cropBox, rotate, resourcesRaw as PdfDictionary);
    }

    /// <summary>
    /// Resolves one inheritable attribute (ISO 32000-2 §7.7.3.4) for a page: the page's own entry
    /// wins outright; otherwise the nearest ancestor that defines it wins. <paramref name="ancestors"/>
    /// is the walk's own frame stack, and a <see cref="Stack{T}"/> enumerates top-first — the frame
    /// pushed most recently, i.e. the page's immediate parent — so walking it in enumeration order
    /// already visits nearest ancestor first, with no separate distance bookkeeping needed.
    /// Deliberately never follows a page's own <c>/Parent</c> entry: a forged one must not be able to
    /// redirect inheritance away from the chain the walk actually descended.
    /// </summary>
    private static (PdfObject? Value, int SourceObjectNumber) FindInherited(
        PdfDocumentReader reader, PdfDictionary leaf, int leafObjectNumber, Stack<Frame> ancestors,
        PdfName key, Func<Frame, PdfObject?> selector)
    {
        if (ResolveOwn(reader, leaf, key) is { } own)
            return (own, leafObjectNumber);

        foreach (var frame in ancestors)
        {
            if (selector(frame) is { } value)
                return (value, frame.ObjectNumber);
        }

        return (null, 0);
    }

    private static Attributes ReadOwnAttributes(PdfDocumentReader reader, PdfDictionary dict) => new(
        Resources: ResolveOwn(reader, dict, PdfName.Resources),
        MediaBox: ResolveOwn(reader, dict, PdfName.MediaBox),
        CropBox: ResolveOwn(reader, dict, CropBoxKey),
        Rotate: ResolveOwn(reader, dict, PdfName.Rotate));

    private static PdfObject? ResolveOwn(PdfDocumentReader reader, PdfDictionary dict, PdfName key) =>
        dict.Get(key) is { } raw ? reader.ResolveValue(raw) : null;

    private static int? NullIfZero(int objectNumber) => objectNumber == 0 ? null : objectNumber;

    // ── Attribute normalisation ──────────────────────────────────────────────────────────────────

    private static PdfRectangle NormalizeRectangle(
        PdfDocumentReader reader, PdfObject? raw, PdfRectangle fallback, bool requiredMissingDiagnostic,
        string keyName, int sourceObjectNumber, int pageIndex, DiagnosticSink diagnostics)
    {
        if (raw is null)
        {
            if (requiredMissingDiagnostic)
            {
                diagnostics.Report(
                    PdfReaderDiagnosticCode.PageAttributeInvalid,
                    $"{keyName} is missing (ISO 32000-2 §7.7.3.3 makes it Required); using the "
                    + "Letter default.",
                    null, null, pageIndex);
            }
            return fallback;
        }

        if (TryReadRectangle(reader, raw, out var rect))
            return rect;

        diagnostics.Report(
            PdfReaderDiagnosticCode.PageAttributeInvalid,
            $"{keyName} did not resolve to a 4-element numeric array (ISO 32000-2 §7.9.5); using "
            + "the default instead.",
            NullIfZero(sourceObjectNumber), null, pageIndex);
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
            if (!TryReadNumber(reader.ResolveValue(arr[i]), out values[i]))
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

    private static int NormalizeRotate(PdfObject? raw, int sourceObjectNumber, int pageIndex, DiagnosticSink diagnostics)
    {
        if (raw is null)
            return 0;

        if (!TryReadNumber(raw, out var value))
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid,
                "Rotate did not resolve to a number; using 0.",
                NullIfZero(sourceObjectNumber), null, pageIndex);
            return 0;
        }

        // ISO 32000-2 §7.7.3.3: "The value shall be a multiple of 90." Checked in double space
        // before any cast, so a huge (but still 90-divisible) value cannot overflow int on the way
        // to being folded below.
        if (value % 90 != 0)
        {
            diagnostics.Report(
                PdfReaderDiagnosticCode.PageAttributeInvalid,
                $"Rotate {value} is not a multiple of 90 (ISO 32000-2 §7.7.3.3); using 0.",
                NullIfZero(sourceObjectNumber), null, pageIndex);
            return 0;
        }

        // Folds a negative value or one past 360 into [0, 360) — e.g. -90 becomes 270, 450 becomes
        // 90 — while the modulo above already confirmed the result lands on one of 0/90/180/270.
        var folded = (value % 360 + 360) % 360;
        return (int)folded;
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

    /// <summary>One open page-tree node on the walk's own stack — its <c>/Kids</c>, an iteration
    /// cursor into that array, and the raw (not yet normalised) inheritable attributes it defines
    /// itself, for <see cref="FindInherited"/> to consult on behalf of every descendant leaf.</summary>
    private sealed class Frame(PdfArray kids, int objectNumber, Attributes attributes)
    {
        internal PdfArray Kids { get; } = kids;
        internal int ObjectNumber { get; } = objectNumber;
        internal Attributes Attributes { get; } = attributes;
        internal int NextIndex;
    }

    /// <summary>
    /// A node's OWN inheritable attribute values (ISO 32000-2 §7.7.3.4 Table 31), already resolved
    /// through one indirect reference if the entry was one, but not yet normalised — normalisation
    /// happens once, in <see cref="BuildPage"/>, against whichever node in the ancestor chain
    /// actually supplied the value a given leaf inherits.
    /// </summary>
    private readonly record struct Attributes(PdfObject? Resources, PdfObject? MediaBox, PdfObject? CropBox, PdfObject? Rotate);
}
