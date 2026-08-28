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

    /// <summary>
    /// The object number from this stream's own <c>N G obj</c> header — a stream is always a
    /// top-level indirect object (ISO 32000-2 §7.3.8 requires a stream to be an indirect object, and
    /// §7.5.7 forbids a stream from being a compressed object inside an object stream), so this is
    /// always available. Needed at the decode layer
    /// (<see cref="PdfDocumentReader.GetDecodedStreamData"/>) to derive the per-object decryption key
    /// (ISO 32000-1 §7.6.2, Algorithm 1) without threading identity through every call site
    /// separately — see the design note on <see cref="RawBody"/> for why decryption happens there
    /// and not by mutating this stream's body in place.
    /// </summary>
    public int ObjectNumber { get; }

    /// <summary>The generation number from this stream's own <c>N G obj</c> header.</summary>
    public int Generation { get; }

    /// <summary>
    /// Creates a parsed stream from a dictionary, its raw body bytes, the body's file offset, and the
    /// identity of the indirect object it came from.
    /// </summary>
    /// <remarks>
    /// The identity is required, not defaulted. It carried defaults of 0 while decryption was being
    /// wired in, so that the pre-existing three-argument call sites kept compiling — but on an
    /// encrypted document a stream constructed at (0, 0) is decrypted under the wrong per-object key
    /// (ISO 32000-1 §7.6.2, Algorithm 1), and under RC4 that returns plausible bytes with no error at
    /// all. Every call site passes real values today; making them required is what keeps the next one
    /// from not.
    /// </remarks>
    public ParsedStream(
        PdfDictionary dictionary,
        ReadOnlyMemory<byte> rawBody,
        int bodyOffset,
        int objectNumber,
        int generation)
    {
        Dictionary = dictionary;
        RawBody = rawBody;
        BodyOffset = bodyOffset;
        ObjectNumber = objectNumber;
        Generation = generation;
    }
}
