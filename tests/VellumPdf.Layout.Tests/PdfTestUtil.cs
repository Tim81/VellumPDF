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
}
