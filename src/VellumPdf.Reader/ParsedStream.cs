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

    /// <summary>
    /// The byte offset, in the source file, at which <see cref="RawBody"/> begins — i.e. immediately
    /// after the EOL that follows the <c>stream</c> keyword. Used by byte-level conformance checks
    /// (§6.1.7.1) that inspect the bytes around the <c>stream</c>/<c>endstream</c> keywords. Zero when
    /// the stream did not come from a file position (e.g. an object-stream member).
    /// </summary>
    public int BodyOffset { get; }

    /// <summary>Creates a parsed stream from a dictionary, its raw body bytes, and the body's file offset.</summary>
    public ParsedStream(PdfDictionary dictionary, ReadOnlyMemory<byte> rawBody, int bodyOffset = 0)
    {
        Dictionary = dictionary;
        RawBody = rawBody;
        BodyOffset = bodyOffset;
    }
}
