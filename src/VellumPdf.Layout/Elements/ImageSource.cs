// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Images;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// Reads an image's format from its first bytes and hands the data to the loader a caller would
/// have chosen by hand. The signatures are the ones the loaders themselves check first, so a
/// file this class names as a format is one that loader starts to read, and a file it refuses
/// is one no loader would have taken as that format.
/// </summary>
internal static class ImageSource
{
    /// <summary>The most a stream or a file may hold, in bytes (256 MiB).</summary>
    internal const long MaxBytes = 256L * 1024 * 1024;

    private const int InitialChunk = 81_920;

    private enum Format { Png, Jpeg, Tiff, Bmp, Gif, Jbig2, Jpx }

    private readonly record struct Signature(Format Format, string Name, byte[] Bytes);

    // Each row is what the loader checks before it reads anything else. PNG: the PNG signature,
    // W3C PNG 3rd edition section 5.2. JPEG: the start-of-image marker every interchange stream
    // begins with, ITU-T T.81 Annex B. TIFF: byte order "II" or "MM", then 42, TIFF 6.0 section 2.
    // BMP: "BM". GIF: "GIF" and a version, GIF89a specification section 17. JBIG2: the 8-byte file
    // ID string, ITU-T T.88 D.4.1. JPEG 2000: the 12-byte signature box and the start-of-codestream
    // marker, exactly as JpxImageLoader tests them; ISO/IEC 15444-1 is not among the held texts.
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
            RefuseUnreadMembers(format, imageOptions);
        if (gifOptions is not null && format != Format.Gif && gifOptions != GifLoadOptions.Default)
            throw new ArgumentException(
                $"The data is {NameOf(format)}, which does not read GifLoadOptions, so " +
                "PlaceOnLogicalScreen would be ignored. Pass the default GIF options or none.",
                "options");

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

    // A member the chosen loader never reads would be dropped without a word, so a value other
    // than the default is refused. The default is what the loader uses anyway.
    private static void RefuseUnreadMembers(Format format, ImageLoadOptions options)
    {
        var readsBitDepth = format is Format.Png or Format.Tiff;
        var readsDecodeMode = format is Format.Tiff or Format.Jbig2 or Format.Jpx;
        if (!readsBitDepth && options.BitDepth != ImageBitDepth.Preserve)
            throw new ArgumentException(
                $"The data is {NameOf(format)}, whose loader never reads ImageLoadOptions.BitDepth, " +
                $"so {options.BitDepth} would be ignored. Pass the default or no options.",
                nameof(options));
        if (!readsDecodeMode && options.DecodeMode != ImageDecodeMode.Passthrough)
            throw new ArgumentException(
                $"The data is {NameOf(format)}, whose loader never reads " +
                $"ImageLoadOptions.DecodeMode, so {options.DecodeMode} would be ignored. " +
                "Pass the default or no options.",
                nameof(options));
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

    // Reads from the stream's position to its end. A seekable stream says how much is left, which
    // sizes the buffer once and refuses an oversized one before a byte is read. Any other stream
    // grows a buffer by doubling, and the read stops with a refusal the moment one byte past the
    // bound has arrived, so an endless stream costs at most the bound.
    internal static byte[] ReadAll(Stream stream, long maxBytes, string paramName, string what)
    {
        if (!stream.CanRead)
            throw new ArgumentException($"The {what} cannot be read.", paramName);

        long initial = InitialChunk;
        if (stream.CanSeek)
        {
            var remaining = stream.Length - stream.Position;
            if (remaining > maxBytes) throw TooLarge(what, maxBytes);
            initial = Math.Max(remaining, 0);
        }

        var buffer = new byte[Math.Min(initial, maxBytes)];
        var total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                var probe = stream.ReadByte();
                if (probe < 0) break;
                if (buffer.Length >= maxBytes) throw TooLarge(what, maxBytes);
                Array.Resize(ref buffer,
                    (int)Math.Min(maxBytes, Math.Max(InitialChunk, (long)buffer.Length * 2)));
                buffer[total++] = (byte)probe;
                continue;
            }
            var n = stream.Read(buffer, total, buffer.Length - total);
            if (n == 0) break;
            total += n;
        }

        if (total == 0)
            throw new ArgumentException($"The {what} has no bytes to read.", paramName);
        if (total != buffer.Length)
            Array.Resize(ref buffer, total);
        return buffer;
    }

    private static InvalidDataException TooLarge(string what, long maxBytes) =>
        new($"The {what} holds more than {maxBytes} bytes, which is the most this method reads. " +
            "Read the bytes yourself and pass them to the byte-array overload.");
}
