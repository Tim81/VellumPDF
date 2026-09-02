// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Text.Json.Serialization;

namespace VellumPdf.Reader;

/// <summary>
/// Settings for <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/> and its
/// <see cref="System.IO.Stream"/> twin.
/// </summary>
/// <remarks>
/// A single options object rather than one parameter per setting. The password used to be its own
/// <c>string?</c> parameter, and that shape could not be extended: an overload taking options
/// alongside it makes <c>Open(bytes, null)</c> a CS0121 ambiguity, because nullable annotations do
/// not participate in overload resolution and nothing else distinguishes the two candidates. Folding
/// the password in leaves one place for every later setting to go.
/// <para>
/// <c>init</c> accessors, matching every other options type in the library. An instance a caller has
/// handed to <c>Open</c> describes one read; letting it change afterwards would describe nothing.
/// </para>
/// <para>
/// A class, not a record, because this options type carries <see cref="Password"/>: a synthesised
/// <c>ToString</c> would print the password in the clear into any log, exception message, or
/// debugger display that formats the instance, and synthesised <c>Equals</c>/<c>GetHashCode</c>
/// would compare and hash over it, making the options usable as a cache key that carries a
/// credential. <see cref="VellumPdf.Encryption.PdfEncryptionSettings"/> is the library's other
/// password-carrying options type, and it is also a class. Nothing clones or equality-compares
/// reader options, so nothing is lost by not synthesising those members.
/// </para>
/// <para>
/// Not synthesising <c>ToString</c> only closes one route to the password. Reflection-based
/// serialisation reads <see cref="Password"/> directly regardless of how the type formats itself:
/// <c>JsonSerializer.Serialize(options)</c> emits it as plain text, and structured-logging
/// destructuring (Serilog's or <c>Microsoft.Extensions.Logging</c>'s <c>{@Options}</c>) does the
/// same. <see cref="Password"/> is marked <see cref="JsonIgnoreAttribute"/> to close the
/// serialisation route; destructuring has no equivalent attribute, so avoid logging this instance
/// with a destructuring operator.
/// </para>
/// </remarks>
public sealed class PdfReaderOptions
{
    /// <summary>
    /// The password to decrypt the document with, or <see langword="null"/> for a document that uses
    /// none. Leave it null for an encrypted document whose empty user password is enough — most
    /// encrypted PDFs in the wild are that shape, restricting permissions through the owner password
    /// while leaving the user password empty.
    /// </summary>
    [DebuggerBrowsable(DebuggerBrowsableState.Never)]
    [JsonIgnore]
    public string? Password { get; init; }

    /// <summary>
    /// Whether <see cref="PdfReader"/> may rebuild a document's cross-reference table by scanning
    /// the file for object headers (ISO 32000-2 Annex C.4, informative) when <c>startxref</c> is
    /// missing or unusable. Off by default: reconstruction is best-effort recovery over structure
    /// the file's own xref has already failed to describe correctly, and can infer the wrong
    /// document catalog for a layout it doesn't fully understand — a caller has to opt into that
    /// trade-off rather than receive it silently on every malformed file. A document opened this way
    /// reports it through <see cref="PdfDocumentReader.WasReconstructed"/> and refuses a later
    /// incremental update (<c>AppendRevision</c>): there is no real <c>startxref</c> chain left for
    /// <c>/Prev</c> to point at, and the recovered trailer's <c>/ID</c> is not reliable enough to
    /// carry into a new revision.
    /// </summary>
    public bool AllowReconstruction { get; init; }

    /// <summary>
    /// The ceiling, in bytes, on a single stream's decoded (post-filter) size, and on the aggregate
    /// raw object-stream bytes reconstruction's Phase B will decode while expanding a document whose
    /// cross-reference table it rebuilt. Defaults to 512 MiB. ISO 32000-2 Annex C.1 (informative)
    /// states that "a particular PDF processor running on a particular device and in a particular
    /// operating environment will always have practical limits", and Annex C.3 (informative) adds
    /// that available memory is "often much less in mobile devices than desktop computers" — 512 MiB
    /// is this library's own choice for a desktop host, not a spec requirement, so a caller on a more
    /// constrained device, or one hardening against a decompression bomb, may lower it. Raising it
    /// above the default is refused: nothing above 512 MiB has been exercised as a safe ceiling, so
    /// this option can only tighten it.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown by <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/> when set above the 512 MiB
    /// default or below the 1 MiB floor a normally compressed document needs to decode at all.
    /// </exception>
    public long MaxDecodedStreamBytes { get; init; } = ReaderLimits.DefaultMaxDecodedBytes;

    /// <summary>
    /// The multiplier in cross-reference reconstruction's (see <see cref="AllowReconstruction"/>,
    /// ISO 32000-2 Annex C.4, informative) <c>max(1 MiB, N × file length)</c> work budget, charged
    /// against every non-trivial operation while scanning a document for object headers. Defaults to
    /// 8. The budget's ceiling itself is left to the processor by Annex C.1/C.3 (informative), not by
    /// C.4 — the same rationale as <see cref="MaxDecodedStreamBytes"/> above; a caller hardening
    /// against a file engineered to burn CPU across many decoy candidates may lower it. Raising it
    /// above the default is refused for the same reason.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown by <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/> when set above the default of
    /// 8 or below the floor of 1, at which the budget would stop scaling with file size at all.
    /// </exception>
    public int ReconstructionBudgetMultiplier { get; init; } = ReaderLimits.DefaultReconstructionBudgetMultiplier;

    /// <summary>
    /// <see cref="PdfDocumentReader.Diagnostics"/> holds at most <see cref="MaxDiagnostics"/>
    /// ordinary entries plus one <see cref="PdfReaderDiagnosticCode.DiagnosticsSuppressed"/> entry
    /// recording how many further reports were dropped — reports dropped, not distinct conditions
    /// dropped; see that code's own doc for the exact rule. Defaults to 1000. Also bounds the
    /// reader's internal dedupe bookkeeping at the same point (see <c>DiagnosticSink.TryAccept</c>
    /// for why that matters), not just the visible list. Tighten-only, matching
    /// <see cref="MaxDecodedStreamBytes"/> and <see cref="ReconstructionBudgetMultiplier"/> above:
    /// nothing about this cap is a spec requirement, so a caller may lower it but not raise it
    /// past the shipped default.
    /// </summary>
    /// <remarks>
    /// Plus at most one entry each for the page-tree codes that say the page list found so far is
    /// incomplete: <see cref="PdfReaderDiagnosticCode.PageTreeLeafLimitExceeded"/>,
    /// <see cref="PdfReaderDiagnosticCode.PageTreeNodeLimitExceeded"/>, and the first
    /// <see cref="PdfReaderDiagnosticCode.PageTreeDepthExceeded"/> of a walk. Each of those is
    /// retained past this cap because each is reported at most once per document walk, so a caller
    /// can never learn the page list is incomplete on exactly the input this cap exists to bound.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown by <see cref="PdfReader.Open(byte[], PdfReaderOptions)"/> when set above the default
    /// of 1000 or below the floor of 1.
    /// </exception>
    public int MaxDiagnostics { get; init; } = ReaderLimits.DefaultMaxDiagnostics;
}
