// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Document;

/// <summary>
/// A PDF object that writes a fixed sequence of raw ASCII bytes verbatim,
/// bypassing encryption and any escaping. Used for the /ByteRange and /Contents
/// placeholders in the signature dictionary, which must be patchable in-place
/// after the PDF body has been written.
/// </summary>
internal sealed class PdfRawBytesObject : PdfObject
{
    private readonly byte[] _bytes;

    public PdfRawBytesObject(string asciiContent)
        => _bytes = System.Text.Encoding.ASCII.GetBytes(asciiContent);

    public override void WriteTo(PdfWriter writer)
        // WriteRaw does NOT touch the encryptor — it writes verbatim bytes.
        => writer.WriteRaw(_bytes);
}
