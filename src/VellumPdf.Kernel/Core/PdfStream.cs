// Copyright © Timothy van der Ham (@Tim81)
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
    /// <summary>The stream's dictionary, holding entries such as /Filter and /Length.</summary>
    public PdfDictionary Dictionary { get; } = new();

    private readonly byte[] _data;

    /// <summary>Creates a stream wrapping the given uncompressed data.</summary>
    public PdfStream(byte[] data) => _data = data;

    // private protected: blocks external subclassing while allowing the internal
    // RawPdfStream / UncompressedPdfStream subclasses in this assembly.
    private protected PdfStream() => _data = [];

    /// <inheritdoc />
    public override void WriteTo(PdfWriter writer)
    {
        byte[] compressed = Compress(_data);

        // When an encryptor is active the stream body is wrapped:
        // body = AES-IV(16) || AES-CBC-PKCS7(compressed). The /Length reflects
        // the encrypted length. The /Filter chain stays as FlateDecode because
        // PDF readers decrypt first, then decompress.
        byte[] body;
        int length;
        if (writer.Encryptor is { } enc)
        {
            body = enc.Encrypt(compressed);
            length = body.Length;
        }
        else
        {
            body = compressed;
            length = compressed.Length;
        }

        // Write a serialisation-time copy of the dictionary so we never mutate
        // the shared Dictionary (enables write-once, idempotent calls).
        var serialDict = Dictionary.ShallowCopy()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(PdfName.Length, length);

        serialDict.WriteTo(writer);
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
