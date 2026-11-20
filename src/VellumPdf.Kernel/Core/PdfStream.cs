// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;

namespace VellumPdf.Core;

/// <summary>
/// PDF stream object (ISO 32000-2 §7.3.8).
/// Data is stored uncompressed; WriteTo applies FlateDecode compression via
/// ZLibStream (RFC 1950 — includes the zlib wrapper PDF requires). An empty body is written
/// as the eight-byte empty zlib stream on every runtime, because an empty FlateDecode body has
/// no zlib header or Adler-32 and so is not an RFC 1950 stream.
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

        // The copy above is a fresh dictionary on every call, so the writer's guard never sees the
        // same one twice when the dictionary holds this stream; guard the stream itself.
        writer.EnterContainer(this);
        try
        {
            serialDict.WriteTo(writer);
        }
        finally
        {
            writer.LeaveContainer(this);
        }
        writer.WriteAscii("\nstream\n"u8);
        writer.WriteRaw(body);
        writer.WriteAscii("\nendstream"u8);
    }

    // RFC 1950 2.2: CMF 0x78 (deflate, 32K window), FLG 0x9C (CMF*256+FLG is a multiple of 31), one
    // final fixed-Huffman block holding only end-of-block (03 00), then the Adler-32 of no data,
    // which is 1 (00 00 00 01). Empty input gets these bytes on every runtime because an empty body
    // has no header or checksum, so it is not an RFC 1950 stream; 10.0.15's ZLibStream writes
    // nothing for it and 11.0.0's writes exactly these bytes.
    private static readonly byte[] EmptyZlibStream = [0x78, 0x9C, 0x03, 0x00, 0x00, 0x00, 0x00, 0x01];

    private static byte[] Compress(byte[] data)
    {
        if (data.Length == 0)
            return (byte[])EmptyZlibStream.Clone();
        var ms = new MemoryStream();
        // ZLibStream (not DeflateStream) — RFC 1950 includes the zlib header PDF requires.
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }
}
