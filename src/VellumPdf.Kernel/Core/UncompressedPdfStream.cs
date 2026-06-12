// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// A PDF stream that writes its payload uncompressed and with no /Filter entry.
/// Required by PDF/A for metadata streams (ISO 19005-2 §6.7.3): the XMP packet
/// must be readable without decompression so conforming readers can scan it.
/// </summary>
internal sealed class UncompressedPdfStream : PdfStream
{
    private readonly byte[] _data;

    public UncompressedPdfStream(byte[] data) : base()
    {
        _data = data;
    }

    public override void WriteTo(PdfWriter writer)
    {
        // No /Filter — plain uncompressed bytes (or encrypted body when active).
        byte[] body;
        int length;
        if (writer.Encryptor is { } enc)
        {
            body = enc.Encrypt(_data);
            length = body.Length;
        }
        else
        {
            body = _data;
            length = _data.Length;
        }

        // Write a serialisation-time copy so we never mutate the shared Dictionary.
        var serialDict = Dictionary.ShallowCopy().Set(PdfName.Length, length);

        serialDict.WriteTo(writer);
        writer.WriteAscii("\nstream\n"u8);
        writer.WriteRaw(body);
        writer.WriteAscii("\nendstream"u8);
    }
}
