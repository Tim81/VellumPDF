// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Entry point for opening existing PDF documents for reading.
/// </summary>
public static class PdfReader
{
    /// <summary>Opens a PDF document from a byte array.</summary>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when the document's security handler
    /// is not the Standard one (e.g. a public-key handler), or when a crypt filter names an
    /// unsupported method.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and no supplied
    /// password authenticates as either the owner or the user password.</exception>
    public static PdfDocumentReader Open(byte[] bytes) => Open(bytes, password: null);

    /// <summary>
    /// Opens a PDF document from a byte array, decrypting it with <paramref name="password"/> if it
    /// is encrypted. Pass <see langword="null"/> (or an empty string) for a document that uses no
    /// password, or whose empty user password is enough — most encrypted PDFs in the wild fall into
    /// that second case.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when the document's security handler
    /// is not the Standard one (e.g. a public-key handler), or when a crypt filter names an
    /// unsupported method.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and
    /// <paramref name="password"/> authenticates as neither the owner nor the user password.</exception>
    public static PdfDocumentReader Open(byte[] bytes, string? password)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var data = new ReadOnlyMemory<byte>(bytes);
        var (xref, trailer, startXrefOffset, revisions) = XrefParser.Parse(data);
        return new PdfDocumentReader(data, xref, trailer, startXrefOffset, revisions, password);
    }

    /// <summary>Opens a PDF document by reading all bytes from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when the document's security handler
    /// is not the Standard one (e.g. a public-key handler), or when a crypt filter names an
    /// unsupported method.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and no supplied
    /// password authenticates as either the owner or the user password.</exception>
    public static PdfDocumentReader Open(Stream stream) => Open(stream, password: null);

    /// <summary>
    /// Opens a PDF document by reading all bytes from <paramref name="stream"/>, decrypting it with
    /// <paramref name="password"/> if it is encrypted. See
    /// <see cref="Open(byte[], string?)"/> for the empty/null-password case.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when the document's security handler
    /// is not the Standard one (e.g. a public-key handler), or when a crypt filter names an
    /// unsupported method.</exception>
    /// <exception cref="PdfPasswordException">Thrown when the document is encrypted and
    /// <paramref name="password"/> authenticates as neither the owner nor the user password.</exception>
    public static PdfDocumentReader Open(Stream stream, string? password)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Open(ms.ToArray(), password);
    }
}
