// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Parses a classic cross-reference table and trailer chain from a PDF byte buffer
/// (ISO 32000-2 §7.5.4 and §7.5.5). Does not support xref streams (#95) or encryption (#97).
/// </summary>
internal sealed class XrefParser
{
    private static readonly byte[] StartxrefBytes = "startxref"u8.ToArray();

    /// <summary>
    /// Parses the xref table and trailer chain from <paramref name="data"/>.
    /// Returns the merged xref table (newer revisions win) and the newest trailer dictionary.
    /// Also outputs the byte offset of the xref table from the last startxref.
    /// </summary>
    public static (Dictionary<int, int> Xref, PdfDictionary Trailer, int StartXrefOffset) Parse(
        ReadOnlyMemory<byte> data)
    {
        var startxrefOffset = FindLastStartxref(data);
        var xref = new Dictionary<int, int>();
        var trailer = ParseRevisionChain(data, startxrefOffset, xref);
        return (xref, trailer, startxrefOffset);
    }

    private static int FindLastStartxref(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        var searchStart = Math.Max(0, span.Length - 1024);
        var searchSpan = span[searchStart..];

        // Find the last occurrence of "startxref" in the tail of the file.
        var lastFound = -1;
        for (var i = 0; i <= searchSpan.Length - StartxrefBytes.Length; i++)
        {
            if (searchSpan[i..].StartsWith(StartxrefBytes))
                lastFound = i;
        }

        if (lastFound < 0)
            throw new InvalidDataException(
                "Malformed PDF: 'startxref' not found in the last 1024 bytes.");

        var absolutePos = searchStart + lastFound + StartxrefBytes.Length;

        // Skip whitespace after 'startxref', then read the integer offset.
        while (absolutePos < span.Length && IsWhitespace(span[absolutePos]))
            absolutePos++;

        if (absolutePos >= span.Length || !IsDigit(span[absolutePos]))
            throw new InvalidDataException(
                "Malformed PDF: expected integer offset after 'startxref'.");

        var numStart = absolutePos;
        while (absolutePos < span.Length && IsDigit(span[absolutePos]))
            absolutePos++;

        var offsetStr = Encoding.ASCII.GetString(span[numStart..absolutePos]);
        if (!int.TryParse(offsetStr, NumberStyles.None, CultureInfo.InvariantCulture, out var xrefOffset)
            || xrefOffset < 0)
            throw new InvalidDataException(
                $"Malformed PDF: invalid startxref offset '{offsetStr}'.");

        if (xrefOffset >= data.Length)
            throw new InvalidDataException(
                $"Malformed PDF: startxref offset {xrefOffset} is beyond end of file ({data.Length} bytes).");

        return xrefOffset;
    }

    private static PdfDictionary ParseRevisionChain(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, int> xref)
    {
        var seenOffsets = new HashSet<int>();
        PdfDictionary? newestTrailer = null;

        var currentOffset = xrefOffset;
        var revisionCount = 0;

        while (true)
        {
            if (!seenOffsets.Add(currentOffset))
                throw new InvalidDataException(
                    $"Malformed PDF: cycle detected in /Prev xref chain at offset {currentOffset}.");
            if (++revisionCount > 100)
                throw new InvalidDataException(
                    "Malformed PDF: xref chain exceeds 100 revisions; aborting to prevent infinite loop.");

            var trailer = ParseOneRevision(data, currentOffset, xref);
            newestTrailer ??= trailer;

            // Check for unsupported features in the trailer.
            if (trailer.Get(new PdfName("Encrypt")) is not null)
                throw new UnsupportedPdfFeatureException(
                    "Encryption is not supported yet (see VellumPdf issue #97).");
            if (trailer.Get(new PdfName("XRefStm")) is not null)
                throw new UnsupportedPdfFeatureException(
                    "Cross-reference streams are not supported yet (see VellumPdf issue #95).");

            if (trailer.TryGet(PdfName.Prev, out var prevObj) && prevObj is PdfInteger prevInt)
            {
                var prevOffset = (int)prevInt.Value;
                if (prevOffset < 0 || prevOffset >= data.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: /Prev offset {prevOffset} is out of range.");
                currentOffset = prevOffset;
            }
            else
            {
                break;
            }
        }

        return newestTrailer!;
    }

    private static PdfDictionary ParseOneRevision(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, int> xref)
    {
        var span = data.Span;

        // Check what's at xrefOffset: must be the 'xref' keyword.
        // If it looks like digits (object number), it's an xref stream — unsupported.
        if (xrefOffset >= data.Length)
            throw new InvalidDataException(
                $"Malformed PDF: xref offset {xrefOffset} is out of range.");

        var b = span[xrefOffset];
        if (IsDigit(b))
            throw new UnsupportedPdfFeatureException(
                "Cross-reference streams are not supported yet (see VellumPdf issue #95).");

        // Expect 'xref'
        if (xrefOffset + 4 > data.Length ||
            !span[xrefOffset..].StartsWith("xref"u8))
            throw new InvalidDataException(
                $"Malformed PDF: expected 'xref' keyword at offset {xrefOffset}.");

        var pos = xrefOffset + 4;

        // Parse subsections until we hit 'trailer'
        while (true)
        {
            // Skip whitespace
            while (pos < span.Length && IsWhitespace(span[pos]))
                pos++;

            if (pos >= span.Length)
                throw new InvalidDataException("Malformed PDF: unexpected end of xref table.");

            // Check for 'trailer' keyword
            if (pos + 7 <= span.Length && span[pos..].StartsWith("trailer"u8))
            {
                pos += 7;
                break;
            }

            // Parse subsection header: firstObjNum count
            var (firstObjNum, afterFirst) = ReadInt(span, pos);
            pos = afterFirst;

            while (pos < span.Length && IsWhitespace(span[pos]))
                pos++;

            var (count, afterCount) = ReadInt(span, pos);
            pos = afterCount;

            // Skip to end of line
            while (pos < span.Length && span[pos] is not 10 and not 13)
                pos++;
            if (pos < span.Length && span[pos] == 13) pos++;
            if (pos < span.Length && span[pos] == 10) pos++;

            // Parse 'count' entries of exactly 20 bytes each
            for (var i = 0; i < count; i++)
            {
                if (pos + 20 > span.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: xref entry {i} in subsection starting at obj {firstObjNum} is truncated.");

                var entry = span.Slice(pos, 20);
                // bytes 0-9: offset (10 digits), byte 10: space,
                // bytes 11-15: generation (5 digits), byte 16: space,
                // byte 17: 'n' or 'f', bytes 18-19: ' \r\n' or '  \n' etc.
                var objType = (char)entry[17];
                var objNum = firstObjNum + i;

                if (objType == 'n')
                {
                    var offsetStr = Encoding.ASCII.GetString(entry[..10]);
                    if (!int.TryParse(offsetStr, NumberStyles.None, CultureInfo.InvariantCulture,
                            out var objOffset))
                        throw new InvalidDataException(
                            $"Malformed PDF: bad xref entry offset '{offsetStr}' for obj {objNum}.");

                    // Newer revisions win: only add if not already present.
                    xref.TryAdd(objNum, objOffset);
                }
                // 'f' entries are ignored (free list).

                pos += 20;
            }
        }

        // Parse the trailer dictionary after the 'trailer' keyword.
        var parser = new PdfObjectParser(data, pos);
        var trailerObj = parser.ParseObject();
        if (trailerObj is not PdfDictionary trailerDict)
            throw new InvalidDataException(
                $"Malformed PDF: expected dictionary after 'trailer', got {trailerObj.GetType().Name}.");

        return trailerDict;
    }

    private static (int Value, int NextPos) ReadInt(ReadOnlySpan<byte> span, int pos)
    {
        if (pos >= span.Length || !IsDigit(span[pos]))
            throw new InvalidDataException(
                $"Malformed PDF: expected integer at offset {pos} in xref table.");

        var start = pos;
        while (pos < span.Length && IsDigit(span[pos]))
            pos++;

        var s = Encoding.ASCII.GetString(span[start..pos]);
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            throw new InvalidDataException($"Malformed PDF: could not parse integer '{s}'.");

        return (value, pos);
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';
}
