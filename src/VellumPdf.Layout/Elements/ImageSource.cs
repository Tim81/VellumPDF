// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Text;
using VellumPdf.Images;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// Reads an image's format from its first bytes and hands the data to that format's loader. A
/// byte string that matches a signature below goes to the loader and everything after that is
/// the loader's; one that matches none is refused here. Some data a loader would read has no
/// signature row and is refused here for that reason: JBIG2 in its embedded form, and a JP2 file
/// whose signature box is longer than 12 bytes and holds them.
/// </summary>
internal static class ImageSource
{
    /// <summary>The most a stream or a file may hold, in bytes (256 MiB).</summary>
    internal const long MaxBytes = 256L * 1024 * 1024;

    private const int FirstChunk = 64 * 1024;
    private const int LastChunk = 1024 * 1024;

    private enum Format { Png, Jpeg, Tiff, Bmp, Gif, Jbig2, Jpx }

    private readonly record struct Signature(Format Format, string Name, byte[] Bytes);

    // PNG: the PNG signature, W3C PNG 3rd edition section 5.2. JPEG: the start-of-image marker
    // every interchange stream begins with, ITU-T T.81 Annex B. TIFF: byte order "II" or "MM",
    // then 42, TIFF 6.0 section 2. BMP: "BM". GIF: "GIF" and a version, GIF89a specification
    // section 17. JBIG2: the 8-byte file ID string, ITU-T T.88 D.4.1. JPEG 2000: the 12-byte JP2
    // signature box, and the start-of-codestream marker. ISO/IEC 15444-1 is not among the held
    // texts, so those two rows were not read against it. The signature box row also pins its
    // length field to 12. JpxImageLoader does not require 12: it follows the length to the next
    // box, so a box longer than 12 that holds its bytes loads there and is refused here.
    private static readonly Signature[] Signatures =
    [
        new(Format.Png, "PNG", [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        new(Format.Jpeg, "JPEG", [0xFF, 0xD8]),
        new(Format.Tiff, "TIFF", [0x49, 0x49, 0x2A, 0x00]),
        new(Format.Tiff, "TIFF", [0x4D, 0x4D, 0x00, 0x2A]),
        new(Format.Bmp, "BMP", [0x42, 0x4D]),
        new(Format.Gif, "GIF", "GIF87a"u8.ToArray()),
        new(Format.Gif, "GIF", "GIF89a"u8.ToArray()),
        new(Format.Jbig2, "JBIG2", [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A]),
        new(Format.Jpx, "JPEG 2000",
            [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]),
        new(Format.Jpx, "JPEG 2000", [0xFF, 0x4F]),
    ];

    internal static PdfImageXObject Load(
        byte[] data, ImageLoadOptions? imageOptions, GifLoadOptions? gifOptions)
    {
        var format = Sniff(data);
        if (imageOptions is not null)
        {
            // New API refuses an enum value that names nothing, whichever loader gets the options.
            if (!Enum.IsDefined(imageOptions.BitDepth))
                throw new ArgumentOutOfRangeException("options", imageOptions.BitDepth,
                    "ImageLoadOptions.BitDepth is not a defined ImageBitDepth.");
            if (!Enum.IsDefined(imageOptions.DecodeMode))
                throw new ArgumentOutOfRangeException("options", imageOptions.DecodeMode,
                    "ImageLoadOptions.DecodeMode is not a defined ImageDecodeMode.");
        }

        // The rule depends on the format alone: a loader that takes no options of this type is
        // given none, and a request that differs from the default is refused rather than dropped.
        // What a loader that does take them does with each member is that loader's own business.
        var takesImageOptions = format is Format.Png or Format.Tiff or Format.Jbig2 or Format.Jpx;
        if (imageOptions is not null && !takesImageOptions && imageOptions != ImageLoadOptions.Default)
            throw new ArgumentException(
                $"The data is {NameOf(format)}, whose loader takes no ImageLoadOptions. " +
                "Pass ImageLoadOptions.Default or no options.", "options");
        if (gifOptions is not null && format != Format.Gif && gifOptions != GifLoadOptions.Default)
            throw new ArgumentException(
                $"The data is {NameOf(format)}, whose loader takes no GifLoadOptions. " +
                "Pass GifLoadOptions.Default or no options.", "options");

        return format switch
        {
            Format.Png => imageOptions is null
                ? PngImageLoader.Load(data) : PngImageLoader.Load(data, imageOptions),
            Format.Jpeg => JpegImageLoader.Load(data),
            Format.Tiff => imageOptions is null
                ? TiffImageLoader.Load(data) : TiffImageLoader.Load(data, imageOptions),
            Format.Bmp => BmpImageLoader.Load(data),
            Format.Gif => gifOptions is null
                ? GifImageLoader.Load(data) : GifImageLoader.Load(data, gifOptions),
            Format.Jbig2 => imageOptions is null
                ? Jbig2ImageLoader.Load(data) : Jbig2ImageLoader.Load(data, imageOptions),
            _ => imageOptions is null
                ? JpxImageLoader.Load(data) : JpxImageLoader.Load(data, imageOptions),
        };
    }

    private static string NameOf(Format format)
    {
        foreach (var s in Signatures)
            if (s.Format == format) return s.Name;
        return format.ToString();
    }

    private static Format Sniff(byte[] data)
    {
        foreach (var s in Signatures)
            if (data.AsSpan().StartsWith(s.Bytes))
                return s.Format;

        var found = Hex(data);
        // Fewer bytes than a signature, all of which agree with it: say so, since "unknown" would
        // send a caller with a cut-off download looking for the wrong fault.
        var cutOff = new List<string>();
        foreach (var s in Signatures)
            if (data.Length < s.Bytes.Length && s.Bytes.AsSpan().StartsWith(data)
                && !cutOff.Contains(s.Name))
                cutOff.Add(s.Name);
        if (cutOff.Count > 0)
            throw new InvalidDataException(
                $"The image data ends inside a signature: its {data.Length} byte(s), {found}, " +
                $"begin the signature of {string.Join(" or ", cutOff)}, which is longer.");

        throw new InvalidDataException(
            $"The image data is not a format with a signature this library reads: it begins {found}. " +
            "Recognised are PNG, JPEG, TIFF, BMP, GIF, JBIG2 with its file header and JPEG 2000.");
    }

    // At most the first eight bytes, as upper-case hex pairs separated by spaces.
    private static string Hex(byte[] data)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Math.Min(8, data.Length); i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append(data[i].ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    internal static byte[] ReadFile(string path, long maxBytes)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
            throw new ArgumentException("The path is empty.", nameof(path));
        using var file = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 0,
            FileOptions.SequentialScan);
        return ReadAll(file, maxBytes, nameof(path), "file");
    }

    // Reads from the stream's position to its end, for every kind of stream the same way: in
    // chunks rented from the shared array pool, growing from 64 KiB to 1 MiB (the largest array
    // the pool keeps), never asking for more than one byte past the bound, and then one copy
    // into an array of the exact length. The chunks go back to the pool after the copy, so an
    // endless stream stops after bound + 1 bytes. At the end of the read the chunks and the
    // result are live together, so the peak is about twice the result. What the call allocates
    // depends on what the pool already holds. Measured on net10.0, one process per case, first
    // call, zero-filled input of the stated size, bytes allocated over bytes read: 64 KiB 4.1,
    // 1 MB 3.0, 4 MB 2.0, 10 MB 2.0, 100 MiB 2.0, the bound 2.0; a stream refused at bound + 1
    // by reading, 1.0. The same call repeated in one process allocated the result alone, for each
    // of those sizes up to 100 MiB, because the pool already held the chunks. The stream's Length is never used to size a buffer, since a stream can report
    // anything (a growing file, a /proc file or a device that reports 0). It is used for one
    // thing, to refuse early: a seekable stream that says more than the bound is left is refused
    // before a byte is read, and so keeps its position.
    internal static byte[] ReadAll(Stream stream, long maxBytes, string paramName, string what)
    {
        if (!stream.CanRead)
            throw new ArgumentException($"The {what} cannot be read.", paramName);
        if (stream.CanSeek && stream.Length - stream.Position > maxBytes)
            throw TooLarge(what, maxBytes);

        var pool = ArrayPool<byte>.Shared;
        var chunks = new List<byte[]>();
        var counts = new List<int>();
        try
        {
            long total = 0;
            var size = FirstChunk;
            while (true)
            {
                var want = (int)Math.Min(size, maxBytes + 1 - total);
                var buffer = pool.Rent(want);
                chunks.Add(buffer);
                counts.Add(0);
                var filled = 0;
                while (filled < want)
                {
                    var n = stream.Read(buffer, filled, want - filled);
                    if (n == 0) break;
                    filled += n;
                }
                counts[^1] = filled;
                total += filled;
                if (total > maxBytes) throw TooLarge(what, maxBytes);
                if (filled < want) break;
                size = Math.Min(size * 2, LastChunk);
            }

            if (total == 0)
                throw new ArgumentException($"The {what} has no bytes to read.", paramName);
            var all = new byte[total];
            var at = 0;
            for (var i = 0; i < chunks.Count; i++)
            {
                Buffer.BlockCopy(chunks[i], 0, all, at, counts[i]);
                at += counts[i];
            }
            return all;
        }
        finally
        {
            foreach (var chunk in chunks)
                pool.Return(chunk);
        }
    }

    private static InvalidDataException TooLarge(string what, long maxBytes) =>
        new($"The {what} holds more than {maxBytes} bytes, which is the most this method reads. " +
            "Read the bytes yourself and pass them to the byte-array overload.");
}
