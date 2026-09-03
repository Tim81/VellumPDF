// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;

namespace VellumPdf.Reader;

/// <summary>
/// How seriously <see cref="PdfDocumentReader.Diagnostics"/> rates one observation.
/// </summary>
public enum PdfReaderDiagnosticSeverity
{
    /// <summary>The reader recovered or normalised something and produced correct output — worth
    /// knowing about, not worth acting on.</summary>
    Info = 0,

    /// <summary>The document deviates from what it declares, and the reader's best-effort reading
    /// of it may not match what the producing application intended.</summary>
    Warning = 1,

    /// <summary>The reader could not make sense of the object at all and gave up on it.</summary>
    Error = 2,
}

/// <summary>
/// A condition the reader noticed while opening or decoding a document — a self-contradictory
/// stream, a filter it does not implement, a cross-reference table it had to repair — surfaced
/// through <see cref="PdfDocumentReader.Diagnostics"/> instead of an exception, so a caller can see
/// what the reader had to work around without every one of those conditions aborting the read.
/// </summary>
/// <remarks>
/// ISO 32000-2 Annex I.2 states the norm this channel exists to satisfy: "Upon the first error
/// that is caused by encountering an unrecognised feature, the PDF processor should notify the
/// user that an error has occurred but that no further errors will be reported. … Processing
/// should continue if possible." Annex I.1 frames the choice behind it: the PDF processor is free
/// "to ignore or inform the user about objects not understood[, and] the decision … is made on a
/// feature-by-feature basis, at the discretion of the PDF processor." Separately, §6.2 puts the
/// conformance obligation on the FILE ("Conforming PDF files shall adhere to all requirements of
/// this document"), not on every reader that opens one, and §6.3.2.1 lets a processor choose which
/// subsets of PDF functionality it supports at all — so nothing in ISO 32000-2 obliges a reader to
/// refuse a document merely because some part of it is malformed, redundant, or unsupported. This
/// channel is where the reader records that it took the best-effort path instead —
/// <see cref="PdfDocumentReader.WasReconstructed"/> is the one precedent for this that already
/// existed as its own boolean before this type did; every new notify-and-continue condition since
/// goes through here instead of growing another one.
/// <para>
/// Numeric values in <see cref="PdfReaderDiagnosticCode"/> are allocated in one hundred-wide block
/// per subsystem — see that enum's own remarks — precisely so a later milestone's codes can append
/// without renumbering an already-shipped one: the value is what a caller persists across the read
/// that produced it, and renumbering it out from under them is a breaking change even while this
/// package is still Preview and its surface can otherwise move freely.
/// </para>
/// </remarks>
public sealed class PdfReaderDiagnostic
{
    /// <summary>The specific condition observed.</summary>
    public PdfReaderDiagnosticCode Code { get; }

    /// <summary>How seriously to take <see cref="Code"/> — looked up from a fixed table keyed on
    /// the code, not chosen per call site, so the same code always reports the same severity.</summary>
    public PdfReaderDiagnosticSeverity Severity { get; }

    /// <summary>
    /// A human-readable description of what was observed. Not a compatibility contract: the
    /// wording may change across releases, so a caller that needs to branch on the condition
    /// should switch on <see cref="Code"/> instead of matching text here.
    /// </summary>
    public string Message { get; }

    /// <summary>The object number the observation concerns, or <see langword="null"/> when it does
    /// not concern one specific indirect object (e.g. a document-wide condition).</summary>
    public int? ObjectNumber { get; }

    /// <summary>The generation of <see cref="ObjectNumber"/>, or <see langword="null"/> when
    /// <see cref="ObjectNumber"/> itself is <see langword="null"/> or the generation was not part
    /// of what triggered the observation. For <see cref="PdfReaderDiagnosticCode.ObjectGenerationMismatch"/>
    /// specifically, this is the generation the REQUEST asked for, not the one the cross-reference
    /// table records for the object — the message names that one.</summary>
    public int? Generation { get; }

    /// <summary>The zero-based page index the observation concerns, or <see langword="null"/> when
    /// it was not made during a per-page walk. Populated by the page-tree walker's own
    /// <see cref="PdfReaderDiagnosticCode.PageAttributeInvalid"/> reports against a LEAF's own
    /// dictionary. A malformed attribute found on an ancestor page-tree node instead reports the
    /// same code once against that node, with <see langword="null"/> here, since no leaf has been
    /// reached yet at that point in the walk. A caller filtering diagnostics by page index alone
    /// misses those and must also look at reports with a null page index. Every other code either
    /// concerns no specific page (a document-wide condition) or is reported before a page index is
    /// known (a page-tree shape problem found while locating the pages in the first place), so it
    /// carries <see langword="null"/> here too.</summary>
    public int? PageIndex { get; }

    internal PdfReaderDiagnostic(
        PdfReaderDiagnosticCode code, string message, int? objectNumber, int? generation, int? pageIndex)
    {
        Code = code;
        Severity = PdfReaderDiagnosticSeverities.Of(code);
        Message = message;
        ObjectNumber = objectNumber;
        Generation = generation;
        PageIndex = pageIndex;
    }

    /// <summary>
    /// Formats as <c>"{Severity} {Code} page {PageIndex} obj {ObjectNumber} {Generation}:
    /// {Message}"</c>, for display and logging — not a parsing contract, so the exact spacing and
    /// segment order may still change across releases. The <c>page</c> segment is omitted when
    /// <see cref="PageIndex"/> is <see langword="null"/>; the <c>obj</c> segment is omitted
    /// entirely when <see cref="ObjectNumber"/> is <see langword="null"/>, and printed without
    /// <c>{Generation}</c> when <see cref="ObjectNumber"/> is set but <see cref="Generation"/> is
    /// not. The two segments are independent — a diagnostic can carry a page with no object, an
    /// object with no page, both, or neither.
    /// </summary>
    public override string ToString()
    {
        var pagePart = PageIndex is null ? string.Empty : $" page {PageIndex}";
        var objectPart = ObjectNumber is null
            ? string.Empty
            : Generation is null
                ? $" obj {ObjectNumber}"
                : $" obj {ObjectNumber} {Generation}";
        return $"{Severity} {Code}{pagePart}{objectPart}: {Message}";
    }
}

/// <summary>
/// Every condition <see cref="PdfDocumentReader"/> can report through
/// <see cref="PdfDocumentReader.Diagnostics"/>. Values are explicit and append-only — never
/// renumbered, since a value is what a caller persists — and allocated in a fixed hundred-wide
/// block per subsystem, so each milestone's own PR appends to its own block without a numbering
/// collision against a lane developed in parallel:
/// <list type="bullet">
/// <item><description><c>1xx</c> — file structure and streams (cross-reference recovery, object
/// resolution, filter decoding).</description></item>
/// <item><description><c>2xx</c> — page tree.</description></item>
/// <item><description><c>3xx</c> — content streams.</description></item>
/// <item><description><c>4xx</c> — fonts and Unicode mapping.</description></item>
/// <item><description><c>5xx</c> — images.</description></item>
/// <item><description><c>9xx</c> — reserved for the channel's own bookkeeping (currently just
/// <see cref="DiagnosticsSuppressed"/>).</description></item>
/// </list>
/// </summary>
public enum PdfReaderDiagnosticCode
{
    // ── 1xx: file structure and streams ────────────────────────────────────────────────────────

    /// <summary>
    /// The cross-reference table was rebuilt by scanning the file (ISO 32000-2 Annex C.4,
    /// informative) instead of read from its own <c>startxref</c> chain. Reported alongside
    /// <see cref="PdfDocumentReader.WasReconstructed"/>, which predates this channel and stays for
    /// source compatibility.
    /// </summary>
    XrefReconstructed = 100,

    /// <summary>
    /// A compressed-object-stream member was dropped from the merged cross-reference table because
    /// its container had no live entry — see
    /// <see cref="PdfDocumentReader.DroppedOrphanedObjectStreamMembers"/>.
    /// </summary>
    OrphanedObjectStreamMembersDropped = 101,

    /// <summary>
    /// Reconstruction (ISO 32000-2 Annex C.4, informative) found what looked like an object-stream
    /// container while scanning the file, but the container could not actually be decoded. The
    /// decode itself happens later, in Phase B's container expansion in
    /// <see cref="PdfDocumentReader"/> — Phase A's scan only located the candidate. Best-effort
    /// recovery: expansion moves on to the next candidate rather than aborting.
    /// </summary>
    ObjectStreamContainerUnreadable = 102,

    /// <summary>
    /// An object's own <c>"N G obj"</c> header names a different object number than the one the
    /// cross-reference table declared for the offset it pointed at. The object resolves to
    /// <see langword="null"/> rather than the header's actual content.
    /// </summary>
    ObjectHeaderMismatch = 103,

    /// <summary>
    /// An indirect reference's generation did not match the cross-reference table's record for
    /// that object number (ISO 32000-2 §7.3.10) — e.g. <c>10 2 R</c> against a table that holds
    /// object 10 at generation 0. The reference resolves to <see langword="null"/>, not the
    /// mismatched generation's content.
    /// </summary>
    ObjectGenerationMismatch = 104,

    /// <summary>
    /// A stream's <c>/Filter</c> entry resolved to the null object. ISO 32000-2 §7.3.9 makes a
    /// null-valued dictionary entry equivalent to the entry being absent, so this is not an error —
    /// the stream is handed back unfiltered, exactly as if <c>/Filter</c> had been omitted.
    /// </summary>
    FilterNull = 105,

    /// <summary>
    /// An element of a <c>/Filter</c> array did not resolve to a name. ISO 32000-2 §7.3.8 Table 5
    /// defines <c>/Filter</c> as "the name, or an array of zero, one or several names, of
    /// filter(s)"; §7.4 defines the cascade those names form. The malformed element is dropped
    /// from the chain rather than applied.
    /// </summary>
    FilterArrayElementNotName = 106,

    /// <summary>
    /// <c>/Filter</c> resolved to something other than a name, an array, or the null object. The
    /// stream is treated as carrying no filter.
    /// </summary>
    FilterValueMalformed = 107,

    /// <summary>
    /// <c>/DecodeParms</c> (or <c>/DP</c>) resolved to something other than a dictionary, an array,
    /// or the null object, or an array element did not resolve to a dictionary. The malformed
    /// entry is treated as supplying no parameters for its filter.
    /// </summary>
    DecodeParmsMalformed = 108,

    /// <summary>
    /// A TIFF predictor (ISO 32000-2 §7.4.4.4, predictor value 2) was applied to a stream whose
    /// <c>/BitsPerComponent</c> is not 8. The decoder copies the row through unmodified rather than
    /// undoing the horizontal difference at that bit depth, so the decoded samples are wrong at any
    /// other <c>/BitsPerComponent</c> until that case is implemented.
    /// </summary>
    UnsupportedPredictor = 109,

    /// <summary>
    /// A stream declared a <c>/Filter</c> name this library does not implement. Decoding that
    /// stream throws rather than returning partial or incorrect output.
    /// </summary>
    UnknownFilter = 110,

    /// <summary>
    /// A stream's decoded size exceeded <see cref="PdfReaderOptions.MaxDecodedStreamBytes"/>.
    /// Decoding that stream throws rather than returning a truncated result.
    /// </summary>
    DecodedStreamLimitExceeded = 111,

    // ── 2xx: page tree ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The document has no usable page tree: the catalog's <c>/Pages</c> entry is absent, does not
    /// resolve to a dictionary, or that dictionary has no usable <c>/Kids</c> array (ISO 32000-2
    /// §7.7.2, §7.7.3.2). <see cref="PdfDocumentReader.PageCount"/> is 0 rather than the walk
    /// throwing, but unlike a truncated walk this is a total loss, not a partial one (there is no
    /// "less" to continue with), so it reports at <see cref="PdfReaderDiagnosticSeverity.Error"/>,
    /// the same severity a stream the reader abandons outright already uses.
    /// </summary>
    PageTreeMissing = 200,

    /// <summary>
    /// The same object number, the one a chain of indirect references ultimately resolves to
    /// rather than any single reference along the way, was reached twice while walking the page
    /// tree: as a page-tree node, a page object, or a <c>/Kids</c> array reached through an
    /// indirect reference, in any combination. ISO 32000-2 §7.7.3.2 forbids multiple indirect
    /// references to the same page tree node, and §7.7.3.3 forbids multiple indirect references
    /// to the same page object; both describe <c>/Kids</c> as a tree rather than a graph, so this
    /// is always a shape violation, whether it forms a genuine ancestor cycle, a redundant sibling
    /// reference, or two nodes sharing one <c>/Kids</c> array object; either way the repeat is
    /// skipped and the walk continues.
    /// </summary>
    PageTreeCycle = 201,

    /// <summary>
    /// The page tree nested deeper than <c>PageTreeWalker.MaxDepth</c> (256) levels. The walk stops
    /// descending into the subtree past that depth (its pages are not found) while siblings
    /// already queued elsewhere in the walk continue normally. Only the FIRST occurrence in a walk
    /// is retained past <see cref="PdfReaderOptions.MaxDiagnostics"/> (see that option's own doc);
    /// a later occurrence against a different node is an ordinary report and can still be dropped
    /// once the cap is reached.
    /// </summary>
    PageTreeDepthExceeded = 202,

    /// <summary>
    /// The page tree contains more than <c>PageTreeWalker.MaxLeaves</c> (100,000) page leaves. The
    /// walk stops entirely at that point; pages found up to the cap are still returned. Reported at
    /// most once per walk (the walk stops immediately after), so it is retained past
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> rather than risk going silent on exactly the
    /// document where a caller most needs to know the page list is incomplete.
    /// </summary>
    PageTreeLeafLimitExceeded = 203,

    /// <summary>
    /// An element of a <c>/Kids</c> array did not resolve to a dictionary: a name, a number, an
    /// array, or a dangling indirect reference, none of which ISO 32000-2 §7.7.3.2 permits as a
    /// page-tree node or page object. The element is skipped.
    /// </summary>
    PageTreeKidNotDictionary = 204,

    /// <summary>
    /// A page's <c>/MediaBox</c>, <c>/CropBox</c>, or <c>/Rotate</c>, its own or the value it
    /// would otherwise inherit (ISO 32000-2 §7.7.3.4), failed one of these:
    /// <list type="bullet">
    /// <item><description><c>/MediaBox</c> or <c>/CropBox</c> did not resolve to the shape a
    /// rectangle requires under §7.9.5 (a 4-element numeric array).</description></item>
    /// <item><description><c>/Rotate</c> did not resolve to a number at all (Table 31 types the
    /// entry as integer), or resolved to one that is not a multiple of 90, the rule §7.7.3.3
    /// Table 31 also sets for it.</description></item>
    /// <item><description><c>/MediaBox</c> specifically was absent everywhere in the chain even
    /// though Table 31 makes it Required.</description></item>
    /// <item><description>A resolved <c>/CropBox</c> shares no overlap at all with
    /// <c>/MediaBox</c> (ISO 32000-2 §14.11.2.1).</description></item>
    /// </list>
    /// <see cref="PdfReaderDiagnostic.Message"/> names which key and which condition. The reader
    /// substitutes a default (US Letter for a missing or malformed <c>/MediaBox</c>, the page's
    /// own <see cref="PdfReadPage.MediaBox"/> for a malformed or non-overlapping <c>/CropBox</c>,
    /// 0 for <c>/Rotate</c>) rather than leaving the attribute unset.
    /// </summary>
    PageAttributeInvalid = 205,

    /// <summary>
    /// A dictionary reached through <c>/Kids</c>, or the root named by the catalog's own
    /// <c>/Pages</c> entry, failed one of these shape rules (ISO 32000-2 §7.7.3.2 Table 30,
    /// §7.7.3.3 Table 31):
    /// <list type="bullet">
    /// <item><description>A <c>/Type /Pages</c> node has no usable <c>/Kids</c> of its own. It
    /// still counts as a node and contributes no children.</description></item>
    /// <item><description>A node (typed <c>/Pages</c> or untyped) also carries a
    /// <c>/Contents</c> entry: a Table 31 page-object key with no row in Table 30's node listing
    /// and, per §7.7.3.4, no inheritance path to any descendant either, so it describes nothing
    /// on a node, not a conformance violation on its own, just a meaningless entry the reader
    /// flags. It still counts as a node, and the content is never treated as a
    /// page.</description></item>
    /// <item><description>A <c>/Type /Page</c> object also carries a <c>/Kids</c> array. It is
    /// still used as a leaf, and the <c>/Kids</c> array is ignored.</description></item>
    /// <item><description>A <c>/Type</c> names neither a node nor a page object. It is skipped
    /// outright: neither a node nor a page.</description></item>
    /// </list>
    /// Only the stray-<c>/Contents</c> case applies to the root: the other three all fire inside
    /// <c>PageTreeWalker.ClassifyNode</c>, which classifies a dictionary reached through someone
    /// else's <c>/Kids</c>, a method the root never runs, since nothing points at it through
    /// <c>/Kids</c> in the first place. The root does share <c>ClassifyNode</c>'s own
    /// <c>/Type</c>-driven half, <c>PageTreeWalker.ClassifyByType</c> (both call it, so
    /// <c>/Type /Template</c> is skipped and an untyped dictionary falls back to its <c>/Kids</c>
    /// shape the same way at the root as anywhere else); the root's own stray-<c>/Contents</c>
    /// check runs separately, right after that shared classification, rather than inside
    /// <c>ClassifyNode</c>. A root whose own <c>/Type</c> does not classify it as a node at all
    /// never reaches that check and reports <see cref="PageTreeMissing"/> on its own instead; a
    /// root that DOES classify as a node but has no usable <c>/Kids</c> also reports
    /// <see cref="PageTreeMissing"/>, separately and not exclusively, since a stray
    /// <c>/Contents</c> on that same root can still fire this code alongside it.
    /// </summary>
    PageTreeNodeMalformed = 206,

    /// <summary>
    /// The walk examined more than <c>PageTreeWalker.MaxKidsExamined</c> (1,000,000) <c>/Kids</c>
    /// array elements in total. <see cref="PageTreeDepthExceeded"/> and
    /// <see cref="PageTreeLeafLimitExceeded"/> each bound one dimension of the tree; this bounds the
    /// walk's total work directly, since a branching factor of two or more makes the work a depth
    /// cap alone permits grow exponentially rather than linearly. The walk stops entirely at that
    /// point; pages found up to the cap are still returned. Reported at most once per walk for the
    /// same reason as <see cref="PageTreeLeafLimitExceeded"/>, and retained past
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> for the same reason too.
    /// </summary>
    PageTreeNodeLimitExceeded = 207,

    // ── 9xx: reserved ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> was reached; this is the one entry recorded in
    /// its place for every report dropped after it, with the drop count folded into its own
    /// message so the list stays bounded without silently going quiet about how much it left out.
    /// The count is reports dropped, not distinct conditions dropped: past the cap the reader
    /// stops tracking which (code, object, page) triples it has already seen, so a triple first
    /// encountered there is counted again on every recurrence, while one already seen below the
    /// cap keeps being deduped and never adds to the count at all. A handful of page-tree codes
    /// (<see cref="PageTreeLeafLimitExceeded"/>, <see cref="PageTreeNodeLimitExceeded"/>, and the
    /// first <see cref="PageTreeDepthExceeded"/> of a walk) are exempt from the cap entirely and
    /// never reach this sentinel path at all; see <see cref="PdfReaderOptions.MaxDiagnostics"/>.
    /// </summary>
    DiagnosticsSuppressed = 900,
}

/// <summary>
/// The single source of truth for which <see cref="PdfReaderDiagnosticSeverity"/> each
/// <see cref="PdfReaderDiagnosticCode"/> reports. A <c>switch</c> with no <c>default</c> arm on
/// purpose: appending a code here without adding its case is a compile error (CS8509, and this
/// project treats warnings as errors), not a runtime gap discovered later.
/// </summary>
internal static class PdfReaderDiagnosticSeverities
{
    // No `default`/discard arm: every arm below names one PdfReaderDiagnosticCode member. The C#
    // compiler cannot treat this as exhaustive on its own — a plain enum's underlying int admits
    // values no member names, so a switch expression over one is never provably complete, with or
    // without full member coverage — which is why the trailing arm below still has to exist. What
    // it buys instead is that the trailing arm is UNREACHABLE for every currently defined code: the
    // PdfReaderDiagnosticCodeTests KAT calls this for every value Enum.GetValues reports and asserts
    // none of them throws, so a future PR that appends a code here without adding its arm fails that
    // test immediately rather than shipping a code with no severity.
    internal static PdfReaderDiagnosticSeverity Of(PdfReaderDiagnosticCode code) => code switch
    {
        PdfReaderDiagnosticCode.XrefReconstructed => PdfReaderDiagnosticSeverity.Info,
        PdfReaderDiagnosticCode.OrphanedObjectStreamMembersDropped => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.ObjectStreamContainerUnreadable => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.ObjectHeaderMismatch => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.ObjectGenerationMismatch => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.FilterNull => PdfReaderDiagnosticSeverity.Info,
        PdfReaderDiagnosticCode.FilterArrayElementNotName => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.FilterValueMalformed => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.DecodeParmsMalformed => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.UnsupportedPredictor => PdfReaderDiagnosticSeverity.Warning,
        // Both throw InvalidDataException today — the reader abandons the object entirely rather
        // than handing back partial or wrong bytes, which is the Error condition this severity is
        // reserved for (as opposed to a condition where decoding continues on known-wrong content).
        PdfReaderDiagnosticCode.UnknownFilter => PdfReaderDiagnosticSeverity.Error,
        PdfReaderDiagnosticCode.DecodedStreamLimitExceeded => PdfReaderDiagnosticSeverity.Error,
        // No "less" to continue with: see the code's own doc comment.
        PdfReaderDiagnosticCode.PageTreeMissing => PdfReaderDiagnosticSeverity.Error,
        PdfReaderDiagnosticCode.PageTreeCycle => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.PageTreeDepthExceeded => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.PageTreeLeafLimitExceeded => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.PageTreeKidNotDictionary => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.PageAttributeInvalid => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.PageTreeNodeMalformed => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.PageTreeNodeLimitExceeded => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.DiagnosticsSuppressed => PdfReaderDiagnosticSeverity.Warning,
        _ => throw new UnreachableException($"No severity is mapped for {code}."),
    };
}
