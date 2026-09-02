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
/// Annex I.2 lists conformance as a requirement on the FILE, not on every reader that opens one;
/// nothing in ISO 32000-2 obliges a processor to refuse a document merely because some part of it
/// is malformed, redundant, or unsupported. This channel is where the reader records that it took
/// the best-effort path instead — <see cref="PdfDocumentReader.WasReconstructed"/> is the one
/// precedent for this that already existed as its own boolean before this type did; every new
/// notify-and-continue condition since goes through here instead of growing another one.
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

    /// <summary>A human-readable description of what was observed.</summary>
    public string Message { get; }

    /// <summary>The object number the observation concerns, or <see langword="null"/> when it does
    /// not concern one specific indirect object (e.g. a document-wide condition).</summary>
    public int? ObjectNumber { get; }

    /// <summary>The generation of <see cref="ObjectNumber"/>, or <see langword="null"/> when
    /// <see cref="ObjectNumber"/> itself is <see langword="null"/> or the generation was not part
    /// of what triggered the observation.</summary>
    public int? Generation { get; }

    /// <summary>The zero-based page index the observation concerns, or <see langword="null"/> when
    /// it was not made during a per-page walk. No page walk exists yet as of this type shipping —
    /// every diagnostic reported today carries <see langword="null"/> here.</summary>
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
    /// Formats as <c>"{Severity} {Code} obj {ObjectNumber} {Generation}: {Message}"</c>, omitting
    /// the <c>obj</c> segment entirely when <see cref="ObjectNumber"/> is <see langword="null"/>.
    /// </summary>
    public override string ToString()
    {
        var objectPart = ObjectNumber is null
            ? string.Empty
            : Generation is null
                ? $" obj {ObjectNumber}"
                : $" obj {ObjectNumber} {Generation}";
        return $"{Severity} {Code}{objectPart}: {Message}";
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
    /// container while scanning the file, but the object could not actually be decoded as one.
    /// Best-effort recovery: the scan moves on to the next candidate rather than aborting.
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
    /// An element of a <c>/Filter</c> array did not resolve to a name. ISO 32000-2 §7.4 requires
    /// every element of a filter chain to be a filter name; the malformed element is dropped from
    /// the chain rather than applied.
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

    // ── 9xx: reserved ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> was reached; this is the one entry recorded in
    /// its place for every diagnostic dropped after it, with the drop count folded into its own
    /// message so the list stays bounded without silently going quiet about how much it left out.
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
        PdfReaderDiagnosticCode.DiagnosticsSuppressed => PdfReaderDiagnosticSeverity.Warning,
        _ => throw new UnreachableException($"No severity is mapped for {code}."),
    };
}
