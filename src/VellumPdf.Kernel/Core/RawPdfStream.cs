// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Core;

/// <summary>
/// A PDF stream that writes its data bytes unmodified (no FlateDecode applied).
/// Used for JPEG (DCTDecode) images where the compressed bytes are passed through as-is.
/// </summary>
internal sealed class RawPdfStream : PdfStream
{
    private readonly byte[] _rawData;
    private readonly PdfName _filter;

    public RawPdfStream(byte[] rawData, PdfName filter) : base()
    {
        _rawData = rawData;
        _filter = filter;
    }

    public override void WriteTo(PdfWriter writer)
    {
        byte[] body;
        int length;
        if (writer.Encryptor is { } enc)
        {
            body = enc.Encrypt(_rawData);
            length = body.Length;
        }
        else
        {
            body = _rawData;
            length = _rawData.Length;
        }

        // Write a serialisation-time copy so we never mutate the shared Dictionary.
        var serialDict = Dictionary.ShallowCopy()
            .Set(PdfName.Filter, _filter)
            .Set(PdfName.Length, length);

        serialDict.WriteTo(writer);
        writer.WriteAscii("\nstream\n"u8);
        writer.WriteRaw(body);
        writer.WriteAscii("\nendstream"u8);
    }
}
