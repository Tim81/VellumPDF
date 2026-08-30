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
}
