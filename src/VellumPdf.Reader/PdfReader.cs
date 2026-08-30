// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Entry point for opening existing PDF documents for reading.
/// </summary>
public static class PdfReader
{
    /// <summary>Opens a PDF document from a byte array, with no password.</summary>
    /// <inheritdoc cref="Open(byte[], PdfReaderOptions)" path="/exception"/>
    public static PdfDocumentReader Open(byte[] bytes) => Open(bytes, new PdfReaderOptions());

    /// <summary>Opens a PDF document from a byte array.</summary>
    /// <param name="bytes">The document's raw bytes.</param>
    /// <param name="options">
    /// Settings for this read. Pass <see cref="PdfReaderOptions.Password"/> for an encrypted
    /// document; leave it null for one that uses no password, or whose empty user password is
    /// enough.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="bytes"/> or
    /// <paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when the document's security handler
    /// is not the Standard one (e.g. a public-key handler), when <c>/V</c> is 3 — the one value that
    /// names a real but unpublished algorithm, where the other illegal values are malformed rather
    /// than unimplementable — or when <c>/StrF</c> names a crypt filter whose <c>/CFM</c> it
    /// does not implement. An unresolvable <c>/StmF</c> does NOT throw here: streams are not decoded
    /// until something asks for them, and that failure is an
    /// <see cref="System.IO.InvalidDataException"/> at the decode call.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and
    /// <see cref="PdfReaderOptions.Password"/> authenticates as neither the owner nor the user
    /// password.</exception>
    public static PdfDocumentReader Open(byte[] bytes, PdfReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(options);
        var data = new ReadOnlyMemory<byte>(bytes);
        var (xref, trailer, startXrefOffset, revisions, xrefStreamOffsets, droppedOrphanedObjectStreamMembers) = XrefParser.Parse(data);
        return new PdfDocumentReader(
            data, xref, trailer, startXrefOffset, revisions, options.Password, xrefStreamOffsets,
            droppedOrphanedObjectStreamMembers);
    }

    /// <summary>
    /// Opens a PDF document by reading all bytes from <paramref name="stream"/>, with no password.
    /// </summary>
    /// <inheritdoc cref="Open(Stream, PdfReaderOptions)" path="/exception"/>
    public static PdfDocumentReader Open(Stream stream) => Open(stream, new PdfReaderOptions());

    /// <summary>Opens a PDF document by reading all bytes from <paramref name="stream"/>.</summary>
    /// <param name="stream">The stream to read the document from.</param>
    /// <param name="options">
    /// Settings for this read. See <see cref="Open(byte[], PdfReaderOptions)"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> or
    /// <paramref name="options"/> is null.</exception>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown for the same unsupported features as
    /// <see cref="Open(byte[], PdfReaderOptions)"/>.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and
    /// <see cref="PdfReaderOptions.Password"/> authenticates as neither the owner nor the user
    /// password.</exception>
    public static PdfDocumentReader Open(Stream stream, PdfReaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Open(ms.ToArray(), options);
    }
}
