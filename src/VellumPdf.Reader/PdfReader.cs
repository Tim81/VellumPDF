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
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when an unsupported feature (xref streams, encryption) is encountered.</exception>
    public static PdfDocumentReader Open(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var data = new ReadOnlyMemory<byte>(bytes);
        var (xref, trailer, startXrefOffset) = XrefParser.Parse(data);
        return new PdfDocumentReader(data, xref, trailer, startXrefOffset);
    }

    /// <summary>Opens a PDF document by reading all bytes from <paramref name="stream"/>.</summary>
    /// <exception cref="InvalidDataException">Thrown on malformed PDF structure.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">Thrown when an unsupported feature (xref streams, encryption) is encountered.</exception>
    public static PdfDocumentReader Open(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return Open(ms.ToArray());
    }
}
