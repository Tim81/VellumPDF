// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Parses cross-reference tables and streams from a PDF byte buffer
/// (ISO 32000-2 §7.5.4, §7.5.5, and §7.5.8). Supports classic xref tables,
/// cross-reference streams, and hybrid (XRefStm) files.
/// </summary>
internal sealed class XrefParser
{
    private static readonly byte[] StartxrefBytes = "startxref"u8.ToArray();

    /// <summary>
    /// Parses the xref table/stream chain from <paramref name="data"/>.
    /// Returns the merged xref table (newer revisions win) and the newest trailer dictionary.
    /// Also outputs the byte offset of the xref from the last startxref.
    /// </summary>
    public static (Dictionary<int, XrefEntry> Xref, PdfDictionary Trailer, int StartXrefOffset) Parse(
        ReadOnlyMemory<byte> data)
    {
        var startxrefOffset = FindLastStartxref(data);
        var xref = new Dictionary<int, XrefEntry>();
        var trailer = ParseRevisionChain(data, startxrefOffset, xref);
        return (xref, trailer, startxrefOffset);
    }

    private static int FindLastStartxref(ReadOnlyMemory<byte> data)
    {
        var span = data.Span;
        // ISO 32000 does not bound the distance from EOF to the last 'startxref'; files with large
        // trailers or trailing content after %%EOF place it further back, so scan a generous tail.
        const int TailWindow = 2048;
        var searchStart = Math.Max(0, span.Length - TailWindow);
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
                $"Malformed PDF: 'startxref' not found in the last {TailWindow} bytes.");

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
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref)
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

            var trailer = ParseOneRevision(data, currentOffset, xref, seenOffsets);
            newestTrailer ??= trailer;

            // Check for unsupported features.
            if (trailer.Get(new PdfName("Encrypt")) is not null)
                throw new UnsupportedPdfFeatureException(
                    "Encryption is not supported yet (see VellumPdf issue #97).");

            if (trailer.TryGet(PdfName.Prev, out var prevObj) && prevObj is PdfInteger prevInt)
            {
                // Validate the full 64-bit value before narrowing: a value such as 0x1_0000_0005
                // would wrap to a small in-range int and bypass the range check if cast first.
                var prevValue = prevInt.Value;
                if (prevValue < 0 || prevValue >= data.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: /Prev offset {prevValue} is out of range.");
                currentOffset = (int)prevValue;
            }
            else
            {
                break;
            }
        }

        return newestTrailer!;
    }

    private static PdfDictionary ParseOneRevision(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref,
        HashSet<int> seenOffsets)
    {
        var span = data.Span;

        if (xrefOffset >= data.Length)
            throw new InvalidDataException(
                $"Malformed PDF: xref offset {xrefOffset} is out of range.");

        var b = span[xrefOffset];

        if (IsDigit(b))
        {
            // Cross-reference stream: "N G obj << ... >> stream ... endstream endobj"
            return ParseXrefStream(data, xrefOffset, xref);
        }

        // Classic xref table
        if (xrefOffset + 4 > data.Length ||
            !span[xrefOffset..].StartsWith("xref"u8))
            throw new InvalidDataException(
                $"Malformed PDF: expected 'xref' keyword at offset {xrefOffset}.");

        var trailer = ParseClassicXrefTable(data, xrefOffset, xref);

        // Hybrid: if the classic trailer has /XRefStm, also parse that xref stream.
        // Classic entries win, so we've already added them — the stream entries are added
        // with TryAdd and will be skipped if already present.
        if (trailer.TryGet(new PdfName("XRefStm"), out var xrefStmObj) && xrefStmObj is PdfInteger xrefStmInt)
        {
            // Validate as a 64-bit value before narrowing (see /Prev above): casting first would let
            // an offset like 0x1_0000_0005 wrap to a small in-range int and slip past the guard.
            var stmValue = xrefStmInt.Value;
            if (stmValue < 0 || stmValue >= data.Length)
                throw new InvalidDataException(
                    $"Malformed PDF: /XRefStm offset {stmValue} is out of range.");
            var stmOffset = (int)stmValue;
            // Avoid cycling into an already-processed offset, and record this one so a later /Prev
            // revision pointing at the same stream does not re-parse it.
            if (seenOffsets.Add(stmOffset))
                ParseXrefStream(data, stmOffset, xref);
        }

        return trailer;
    }

    // ── Classic xref table ───────────────────────────────────────────────────

    private static PdfDictionary ParseClassicXrefTable(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref)
    {
        var span = data.Span;
        var pos = xrefOffset + 4; // skip 'xref'

        while (true)
        {
            while (pos < span.Length && IsWhitespace(span[pos]))
                pos++;

            if (pos >= span.Length)
                throw new InvalidDataException("Malformed PDF: unexpected end of xref table.");

            if (pos + 7 <= span.Length && span[pos..].StartsWith("trailer"u8))
            {
                pos += 7;
                break;
            }

            var (firstObjNum, afterFirst) = ReadInt(span, pos);
            pos = afterFirst;

            while (pos < span.Length && IsWhitespace(span[pos]))
                pos++;

            var (count, afterCount) = ReadInt(span, pos);
            pos = afterCount;

            // A subsection cannot declare more 20-byte entries than the file could possibly hold;
            // reject a pathological count up front (also prevents firstObjNum + count overflow).
            if (count < 0 || firstObjNum < 0 || (long)count * 20 > span.Length || (long)firstObjNum + count > int.MaxValue)
                throw new InvalidDataException(
                    $"Malformed PDF: xref subsection ({firstObjNum} {count}) is out of range.");

            while (pos < span.Length && span[pos] is not 10 and not 13)
                pos++;
            if (pos < span.Length && span[pos] == 13) pos++;
            if (pos < span.Length && span[pos] == 10) pos++;

            for (var i = 0; i < count; i++)
            {
                if (pos + 20 > span.Length)
                    throw new InvalidDataException(
                        $"Malformed PDF: xref entry {i} in subsection starting at obj {firstObjNum} is truncated.");

                var entry = span.Slice(pos, 20);
                var objType = (char)entry[17];
                var objNum = firstObjNum + i;

                if (objType == 'n')
                {
                    var offsetStr = Encoding.ASCII.GetString(entry[..10]);
                    if (!int.TryParse(offsetStr, NumberStyles.None, CultureInfo.InvariantCulture,
                            out var objOffset))
                        throw new InvalidDataException(
                            $"Malformed PDF: bad xref entry offset '{offsetStr}' for obj {objNum}.");

                    xref.TryAdd(objNum, XrefEntry.Uncompressed(objOffset));
                }

                pos += 20;
            }
        }

        var parser = new PdfObjectParser(data, pos);
        var trailerObj = parser.ParseObject();
        if (trailerObj is not PdfDictionary trailerDict)
            throw new InvalidDataException(
                $"Malformed PDF: expected dictionary after 'trailer', got {trailerObj.GetType().Name}.");

        return trailerDict;
    }

    // ── Cross-reference stream ───────────────────────────────────────────────

    private static PdfDictionary ParseXrefStream(
        ReadOnlyMemory<byte> data, int xrefOffset, Dictionary<int, XrefEntry> xref)
    {
        var parser = new PdfObjectParser(data, xrefOffset);
        var result = parser.ParseIndirectObject();

        if (result.Stream is null)
            throw new InvalidDataException(
                $"Malformed PDF: expected xref stream object at offset {xrefOffset}.");

        var streamObj = result.Stream;
        var dict = streamObj.Dictionary;

        // Decode the stream body (typically FlateDecode, but use full chain for robustness)
        var decodeResult = PdfFilters.Decode(streamObj);
        if (decodeResult is null)
            throw new InvalidDataException(
                "Malformed PDF: xref stream uses an image filter that cannot be decoded.");
        var decoded = decodeResult;

        // /W [w1 w2 w3] — field widths
        if (dict.Get(new PdfName("W")) is not PdfArray wArr || wArr.Count != 3)
            throw new InvalidDataException("Malformed PDF: xref stream missing valid /W array.");

        // Validate each width as a 64-bit value BEFORE narrowing to int: a value like 0x1_0000_0008
        // would wrap to a valid-looking 8 if cast first. Each field is read big-endian into a long,
        // so a width must be 0..8; negative widths would produce silently-wrong offsets.
        var w1L = GetInt(wArr[0]);
        var w2L = GetInt(wArr[1]);
        var w3L = GetInt(wArr[2]);
        if (w1L is < 0 or > 8 || w2L is < 0 or > 8 || w3L is < 0 or > 8)
            throw new InvalidDataException("Malformed PDF: xref stream /W field width out of range.");
        int w1 = (int)w1L, w2 = (int)w2L, w3 = (int)w3L;
        var rowSize = w1 + w2 + w3;
        if (rowSize <= 0)
            throw new InvalidDataException("Malformed PDF: xref stream /W row size is zero.");

        // /Size
        if (dict.Get(PdfName.Size) is not PdfInteger sizeObj)
            throw new InvalidDataException("Malformed PDF: xref stream missing /Size.");
        if (sizeObj.Value is < 0 or > int.MaxValue)
            throw new InvalidDataException($"Malformed PDF: xref stream /Size {sizeObj.Value} is out of range.");
        var streamSize = (int)sizeObj.Value;

        // /Index — pairs of (firstObjNum, count); default is [0 Size]
        var indexPairs = new List<(int First, int Count)>();
        if (dict.Get(new PdfName("Index")) is PdfArray indexArr)
        {
            if (indexArr.Count % 2 != 0)
                throw new InvalidDataException("Malformed PDF: xref stream /Index array has odd element count.");
            for (var i = 0; i < indexArr.Count; i += 2)
            {
                // Validate as 64-bit before narrowing: a value such as 0x1_0000_0000 would wrap to a
                // small in-range int and slip past the guard (producing bogus object numbers).
                var first = GetInt(indexArr[i]);
                var count = GetInt(indexArr[i + 1]);
                if (first is < 0 or > int.MaxValue || count is < 0 or > int.MaxValue || first + count > int.MaxValue)
                    throw new InvalidDataException("Malformed PDF: xref stream /Index subsection is out of range.");
                indexPairs.Add(((int)first, (int)count));
            }
        }
        else
        {
            indexPairs.Add((0, streamSize));
        }

        var pos = 0;
        foreach (var (firstObj, count) in indexPairs)
        {
            for (var i = 0; i < count; i++)
            {
                if (pos + rowSize > decoded.Length)
                    throw new InvalidDataException(
                        "Malformed PDF: xref stream body is truncated.");

                var type = w1 > 0 ? ReadBigEndian(decoded, pos, w1) : 1; // default type is 1
                var field2 = w2 > 0 ? ReadBigEndian(decoded, pos + w1, w2) : 0;
                var field3 = w3 > 0 ? ReadBigEndian(decoded, pos + w1 + w2, w3) : 0;
                pos += rowSize;

                var objNum = firstObj + i;
                switch (type)
                {
                    case 1:
                        xref.TryAdd(objNum, XrefEntry.Uncompressed(field2));
                        break;
                    case 2:
                        // field2 = container object number, field3 = index within it; a /W width up
                        // to 8 bytes can exceed int range, so validate before narrowing.
                        if (field2 is < 0 or > int.MaxValue || field3 is < 0 or > int.MaxValue)
                            throw new InvalidDataException(
                                "Malformed PDF: xref stream type-2 entry field is out of range.");
                        xref.TryAdd(objNum, XrefEntry.InObjStm((int)field2, (int)field3));
                        break;
                    case 0:
                        // free entry — skip
                        break;
                    default:
                        // unknown type — ignore per spec (future compatibility)
                        break;
                }
            }
        }

        return dict;
    }

    private static long ReadBigEndian(byte[] data, int pos, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
            value = (value << 8) | data[pos + i];
        return value;
    }

    private static long GetInt(PdfObject obj)
    {
        if (obj is PdfInteger pi) return pi.Value;
        throw new InvalidDataException($"Expected integer in xref stream, got {obj.GetType().Name}.");
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
