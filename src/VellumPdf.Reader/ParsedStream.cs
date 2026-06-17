// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// A parsed PDF stream object (ISO 32000-2 §7.3.8): a dictionary plus opaque raw body bytes.
/// The body is stored verbatim (no decompression or re-encoding) to enable round-trip fidelity.
/// This type is intentionally separate from <see cref="PdfStream"/>; <see cref="PdfStream.WriteTo"/>
/// re-compresses, which is inappropriate when reading an existing file.
/// </summary>
internal sealed class ParsedStream
{
    /// <summary>The stream dictionary, including <c>/Length</c>, <c>/Filter</c>, etc.</summary>
    public PdfDictionary Dictionary { get; }

    /// <summary>
    /// The raw, opaque stream body exactly as it appears between the
    /// <c>stream</c> newline and <c>endstream</c> keyword.
    /// No decompression is applied.
    /// </summary>
    public ReadOnlyMemory<byte> RawBody { get; }

    /// <summary>Creates a parsed stream from a dictionary and its raw body bytes.</summary>
    public ParsedStream(PdfDictionary dictionary, ReadOnlyMemory<byte> rawBody)
    {
        Dictionary = dictionary;
        RawBody = rawBody;
    }
}
