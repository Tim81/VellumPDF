// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;

namespace VellumPdf.Core;

/// <summary>
/// PDF stream object (ISO 32000-2 §7.3.8).
/// Data is stored uncompressed; WriteTo applies FlateDecode compression via
/// ZLibStream (RFC 1950 — includes the zlib wrapper PDF requires).
/// </summary>
public class PdfStream : PdfObject
{
    public PdfDictionary Dictionary { get; } = new();

    private readonly byte[] _data;

    public PdfStream(byte[] data) => _data = data;

    protected PdfStream() => _data = [];

    public override void WriteTo(PdfWriter writer)
    {
        byte[] compressed = Compress(_data);

        // When an encryptor is active the stream body is wrapped:
        // body = AES-IV(16) || AES-CBC-PKCS7(compressed). The /Length reflects
        // the encrypted length. The /Filter chain stays as FlateDecode because
        // PDF readers decrypt first, then decompress.
        byte[] body;
        if (writer.Encryptor is { } enc)
        {
            body = enc.Encrypt(compressed);
            Dictionary
                .Set(PdfName.Filter, PdfName.FlateDecode)
                .Set(PdfName.Length, body.Length);
        }
        else
        {
            body = compressed;
            Dictionary
                .Set(PdfName.Filter, PdfName.FlateDecode)
                .Set(PdfName.Length, compressed.Length);
        }

        Dictionary.WriteTo(writer);
        writer.WriteAscii("\nstream\n"u8);
        writer.WriteRaw(body);
        writer.WriteAscii("\nendstream"u8);
    }

    private static byte[] Compress(byte[] data)
    {
        var ms = new MemoryStream();
        // ZLibStream (not DeflateStream) — RFC 1950 includes the zlib header PDF requires.
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }
}
