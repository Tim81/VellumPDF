// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Settings for <see cref="PdfDocumentReader.SaveDecrypted(System.IO.Stream, PdfSaveDecryptedOptions)"/>
/// and its <see cref="System.IO.Stream"/>/async twins.
/// </summary>
/// <remarks>
/// <c>init</c> accessors, matching <see cref="PdfReaderOptions"/> and every other options type in the
/// library. An instance a caller has handed to <c>SaveDecrypted</c> describes one write; letting it
/// change afterwards would describe nothing.
/// </remarks>
public sealed class PdfSaveDecryptedOptions
{
    /// <summary>
    /// Opts into writing a decrypted copy of a signed document, even though re-serialising the object
    /// graph invalidates every digital signature it carries. Off by default: <c>SaveDecrypted</c>
    /// throws <see cref="InvalidOperationException"/> when the source document has one or more
    /// signatures and this is <see langword="false"/>, the same refusal
    /// <see cref="PdfDocumentReader"/>'s incremental-update path applies to a reconstructed document —
    /// producing an artifact whose signatures look present but do not verify is worse than refusing
    /// outright, since nothing about the output signals that it happened.
    /// <para>
    /// A rewritten <c>/ByteRange</c> no longer names the region the original signature was computed
    /// over, so every signature verifies as "document modified since signing" — the same verdict a
    /// verifier gives for genuine tampering, with no way to tell the two apart from the output alone.
    /// Setting this to <see langword="true"/> accepts that outcome; it does not fix it, and no option
    /// on this type can, since a signature's own signed bytes cannot survive a full rewrite by
    /// construction.
    /// </para>
    /// </summary>
    public bool AllowInvalidatingSignatures { get; init; }
}
