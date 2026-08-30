// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
/// <c>init</c> accessors, matching every other options type in the library — see
/// <c>CcittOptions</c> for the reasoning. An instance a caller has handed to <c>Open</c> describes
/// one read; letting it change afterwards would describe nothing.
/// </para>
/// <para>
/// A class, not a record, because this options type carries <see cref="Password"/>: a synthesised
/// <c>ToString</c> would print the password in the clear into any log, exception message, or
/// debugger display that formats the instance, and synthesised <c>Equals</c>/<c>GetHashCode</c>
/// would compare and hash over it, making the options usable as a cache key that carries a
/// credential. <see cref="VellumPdf.Encryption.PdfEncryptionSettings"/> is the library's existing
/// precedent for a password-carrying options type and is a class for the same reason. Nothing
/// clones or equality-compares reader options, so nothing is lost by not synthesising those members.
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
    public string? Password { get; init; }
}
