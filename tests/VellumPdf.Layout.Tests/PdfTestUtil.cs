// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Shared PDF-inspection helpers for layout tests.
/// </summary>
internal static class PdfTestUtil
{
    /// <summary>
    /// Scans <paramref name="pdf"/> for FlateDecode stream data, decompresses each stream, and
    /// concatenates the results as Latin-1 text for assertion.
    /// Uses the <c>/Length N</c> field from each stream dict to slice exact bytes so compressed
    /// content bytes that happen to contain the "endstream" token don't confuse the parser.
    /// </summary>
    internal static string DecompressAllFlatStreams(byte[] pdf)
    {
        var sb = new StringBuilder();
        var pdfText = Encoding.Latin1.GetString(pdf);
        var pos = 0;

        while (pos < pdf.Length)
        {
            var streamKeyword = pdfText.IndexOf("\nstream\n", pos, StringComparison.Ordinal);
            if (streamKeyword < 0) break;
            var dataStart = streamKeyword + "\nstream\n".Length;

            var dictEnd = streamKeyword;
            var dictStart = pdfText.LastIndexOf("obj\n", dictEnd, StringComparison.Ordinal);
            if (dictStart < 0) { pos = dataStart; continue; }

            var lenIdx = pdfText.IndexOf("/Length ", dictStart, dictEnd - dictStart, StringComparison.Ordinal);
            if (lenIdx < 0) { pos = dataStart; continue; }

            var lenValStart = lenIdx + "/Length ".Length;
            var lenValEnd = lenValStart;
            while (lenValEnd < pdfText.Length && char.IsDigit(pdfText[lenValEnd])) lenValEnd++;
            if (!int.TryParse(pdfText[lenValStart..lenValEnd], out var streamLength))
            { pos = dataStart; continue; }

            if (dataStart + streamLength > pdf.Length) { pos = dataStart; continue; }

            var rawBytes = pdf[dataStart..(dataStart + streamLength)];
            try
            {
                using var input = new MemoryStream(rawBytes);
                using var output = new MemoryStream();
                using var z = new System.IO.Compression.ZLibStream(
                    input, System.IO.Compression.CompressionMode.Decompress);
                z.CopyTo(output);
                sb.Append(Encoding.Latin1.GetString(output.ToArray()));
            }
            catch (InvalidDataException)
            {
                // Not a zlib stream (e.g. DCTDecode/uncompressed XMP) — skip
            }

            pos = dataStart + streamLength;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Counts non-overlapping occurrences of <paramref name="needle"/> in <paramref name="haystack"/>.
    /// </summary>
    internal static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>Creates a minimal valid 2×2 opaque white RGB PNG (no alpha) for image tests.</summary>
    internal static byte[] CreateMinimalRgbPng()
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]); // PNG signature

        // IHDR: 2×2, 8-bit, colour type 2 (RGB)
        var ihdr = new byte[13];
        ihdr[3] = 2; // width = 2
        ihdr[7] = 2; // height = 2
        ihdr[8] = 8; // bit depth
        ihdr[9] = 2; // colour type: RGB
        WritePngChunk(ms, "IHDR", ihdr);

        // IDAT: two rows, each [filter=0][R G B][R G B] of white pixels
        byte[] rawScanlines =
        [
            0, 255, 255, 255, 255, 255, 255,
            0, 255, 255, 255, 255, 255, 255,
        ];
        WritePngChunk(ms, "IDAT", ZlibCompress(rawScanlines));
        WritePngChunk(ms, "IEND", []);

        return ms.ToArray();
    }

    private static void WritePngChunk(MemoryStream s, string type, byte[] data)
    {
        s.WriteByte((byte)(data.Length >> 24));
        s.WriteByte((byte)(data.Length >> 16));
        s.WriteByte((byte)(data.Length >> 8));
        s.WriteByte((byte)data.Length);
        foreach (var c in type) s.WriteByte((byte)c);
        s.Write(data);

        var crcInput = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) crcInput[i] = (byte)type[i];
        data.CopyTo(crcInput, 4);
        var crc = ComputePngCrc32(crcInput);
        s.WriteByte((byte)(crc >> 24));
        s.WriteByte((byte)((crc >> 16) & 0xFF));
        s.WriteByte((byte)((crc >> 8) & 0xFF));
        s.WriteByte((byte)(crc & 0xFF));
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(output, System.IO.Compression.CompressionLevel.Fastest))
            z.Write(data);
        return output.ToArray();
    }

    private static uint ComputePngCrc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        return crc ^ 0xFFFFFFFFu;
    }
}
