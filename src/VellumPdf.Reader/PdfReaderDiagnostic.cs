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

    // ── 3xx: content streams ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The content-stream interpreter (ISO 32000-2 §7.8.2) hit an <see cref="InvalidDataException"/>
    /// from the lexer or object parser partway through a page's content, or a member of the page's
    /// <c>/Contents</c> array (§7.7.3.3 Table 31) did not resolve to a usable stream at all: a
    /// non-stream element, a reference that fails to resolve, or a stream whose filter chain could
    /// not be decoded (including one carrying an image filter, which this interpreter never
    /// attempts to decode as content). Interpretation of that stream stops at the point of failure;
    /// operators already reported to the caller's visitor before the failure are kept, and, for a
    /// multi-stream <c>/Contents</c> array specifically, interpretation resumes with the next
    /// stream in the array rather than abandoning the whole page.
    /// </summary>
    ContentStreamLexError = 300,

    /// <summary>
    /// A content-stream keyword token is not one of the 73 operators ISO 32000-2 Annex A Table A.1
    /// lists, and it did not appear inside a <c>BX</c>/<c>EX</c> compatibility section (§7.8.2). ISO
    /// 32000-2 says "an error shall occur" for this case outside such a section; this reader instead
    /// reports it and continues, the same notify-and-continue choice every other diagnostic in this
    /// channel makes. Reported at most once per page, because the sink's dedupe key is
    /// (code, object, page) and this report carries no object number: a second distinct
    /// unrecognised name on the same page is deduped away rather than reported. Silent inside a
    /// compatibility section, per Table 33's own text: "Unrecognised operators ... shall be ignored
    /// without error."
    /// </summary>
    UnknownOperator = 301,

    /// <summary>
    /// A content stream's operand-stack discipline broke down in one of several ways this
    /// interpreter groups under one code rather than one each, since every case has the same
    /// remedy: drop the offending operator (or, for an unbalanced <c>Q</c>/<c>EMC</c>, drop the
    /// pop) and keep interpreting. Every case here is a PRODUCER-side malformation, the document
    /// itself is wrong, not merely bigger than this reader is willing to process; see
    /// <see cref="ContentLimitExceeded"/> for the four cases that are this reader's own processing
    /// ceiling instead. Covers: a number token that does not parse, is not finite, or carries a
    /// second sign character (<c>--5</c>, <c>-+5</c>: §7.3.3 allows only "an optional sign"), a
    /// dictionary operand on an operator other than <c>BDC</c>/<c>DP</c> (§7.8.2: "Dictionaries
    /// shall be permitted as operands only by certain specific operators"), a known operator
    /// invoked with the wrong operand count for its own arity (Annex A Table A.1), an unbalanced
    /// <c>Q</c> with no matching <c>q</c> on the graphics-state stack, or an unbalanced <c>EMC</c>
    /// with no matching <c>BMC</c>/<c>BDC</c> (§14.6.1). An unbalanced <c>q</c> still open at the
    /// end of a content stream is not reported: nothing downstream of this interpreter needs the
    /// graphics state restored past the last operator it saw.
    /// </summary>
    OperandStackMalformed = 302,

    /// <summary>
    /// A Form XObject <c>Do</c> (ISO 32000-2 §8.10) recursed past
    /// <see cref="PdfReaderOptions.MaxFormXObjectDepth"/> levels deep. Descent into that subtree
    /// stops; the <c>Do</c> operator itself is still reported to the caller's visitor, only the
    /// recursive walk into the form's own content is skipped.
    /// </summary>
    FormXObjectDepthExceeded = 303,

    /// <summary>
    /// A Form XObject's own content, directly or through a chain of nested <c>Do</c> invocations,
    /// draws itself again: the same indirect object number already open on the interpreter's own
    /// recursion stack (ISO 32000-2 §8.10 describes no cycle of this kind as legal; a form's content
    /// is ordinary content that may invoke any XObject, so nothing in the format itself prevents a
    /// producer from writing one). Reported once per cycle found; the recursive invocation that
    /// would close the cycle is skipped rather than recursing forever.
    /// </summary>
    FormXObjectCycle = 304,

    /// <summary>
    /// A single page invoked Form XObjects (successful <c>Do</c> recursions, counted across the
    /// whole page, not per subtree) more than 4096 times. Descent into any further form stops for
    /// the rest of the page; operators already reported before the budget was reached are kept, and
    /// interpretation of the page's own (non-form) content continues past the point where the
    /// budget was hit. Reported through <c>DiagnosticSink.ReportRetained</c>: a condition that ends
    /// the page's own form recursion for good is worth surfacing even once
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> is spent on earlier, unrelated conditions.
    /// </summary>
    FormXObjectBudgetExceeded = 305,

    /// <summary>
    /// A <c>Do</c>, <c>gs</c>, <c>Tf</c>, <c>cs</c>/<c>CS</c>, or <c>sh</c> operator, or an inline
    /// image's <c>/CS</c> (<c>/ColorSpace</c>) entry, named a resource absent from the applicable
    /// <c>/Resources</c> subdictionary (ISO 32000-2 §7.8.3): the page's own, or, inside a Form
    /// XObject, that form's own <c>/Resources</c> falling back to its parent's when absent
    /// (§8.10.2). The operator is still reported to the caller's visitor; only the interpreter's own
    /// attempt to resolve the name failed.
    /// </summary>
    ResourceMissing = 306,

    /// <summary>
    /// An inline image (ISO 32000-2 §8.9.7) could not be delimited or decoded, or one of its
    /// dictionary entries was itself invalid: a filter this interpreter never applies to inline
    /// image data (<c>JBIG2Decode</c>, <c>JPXDecode</c>, or <c>Crypt</c>: §8.9.7 itself excludes
    /// all three from inline-image use), a missing <c>ID</c> or <c>EI</c> operator, a missing,
    /// non-integer, or non-positive <c>/W</c>, <c>/H</c>, or <c>/BPC</c> where the image's shape
    /// requires one to compute the data length (Table 87 types all three as positive integers), a
    /// negative <c>/L</c> (§8.9.7, Table 91; PDF 2.0), an <c>/L</c> or computed length past the end
    /// of the stream, or a computed length (from <c>/L</c> or from the image's own shape) that does
    /// not land on the following <c>EI</c> operator, in which case this reader retries the EI scan
    /// before giving up. The image is skipped (its data is still delimited well enough for
    /// interpretation of the rest of the content stream to continue), and no inline-image callback
    /// is raised for it.
    /// </summary>
    InlineImageMalformed = 307,

    /// <summary>
    /// This run's combined decoded-content budget, 64 MiB shared across the page's own
    /// <c>/Contents</c> (ISO 32000-2 §7.7.3.3 Table 31) and every Form XObject it draws (§8.10), was
    /// exceeded. <c>/Contents</c> is concatenated across every stream in its array with a newline
    /// inserted between streams so a token is never glued across a stream boundary; a Form XObject
    /// is counted again on every invocation, not once per distinct form object, since the
    /// interpretation work a repeatedly-drawn form costs scales with invocations, not with how many
    /// distinct form objects a page names. Interpretation proceeds up to the point the budget ran
    /// out and stops there; operators reported before that point are kept. Reported through
    /// <c>DiagnosticSink.ReportRetained</c>, and at most once per run: the truncation this reports
    /// also drives the run's own remaining budget to exactly zero, so no later stream in the same
    /// run can trigger a second report.
    /// </summary>
    ContentStreamTooLarge = 308,

    /// <summary>
    /// A content stream hit one of this reader's own processing ceilings rather than being
    /// malformed by its producer: more than 64 operands accumulated before an operator (§7.8.2
    /// gives an operator's own operand count no declared bound of its own), a <c>TJ</c> array
    /// (§9.4.3) with more than 8192 elements, more than 64 nested <c>q</c> saves, or marked-content
    /// nesting (§14.6.1) past the same 64-deep cap. Split out from <see cref="OperandStackMalformed"/>
    /// (#402) so a caller can tell "this file hit a limit of this reader" apart from "this file is
    /// malformed", a distinction the two codes sharing one value made impossible to draw. The
    /// offending operator, or push, is dropped and interpretation continues, the same recovery
    /// <see cref="OperandStackMalformed"/> uses.
    /// </summary>
    ContentLimitExceeded = 309,

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
        PdfReaderDiagnosticCode.ContentStreamLexError => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.UnknownOperator => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.OperandStackMalformed => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.FormXObjectDepthExceeded => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.FormXObjectCycle => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.FormXObjectBudgetExceeded => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.ResourceMissing => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.InlineImageMalformed => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.ContentStreamTooLarge => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.ContentLimitExceeded => PdfReaderDiagnosticSeverity.Warning,
        PdfReaderDiagnosticCode.DiagnosticsSuppressed => PdfReaderDiagnosticSeverity.Warning,
        _ => throw new UnreachableException($"No severity is mapped for {code}."),
    };
}
