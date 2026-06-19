// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Applies PDF filter chains to stream bodies (ISO 32000-2 §7.4).
/// Handles FlateDecode, LZWDecode, ASCIIHexDecode, ASCII85Decode, RunLengthDecode
/// and their predictors. Image filters (DCTDecode, JPXDecode, JBIG2Decode,
/// CCITTFaxDecode) are recognised but left undecoded — callers receive a false
/// fullyDecoded flag.
/// </summary>
internal static class PdfFilters
{
    internal const long MaxDecodedBytes = 512L * 1024 * 1024;

    private static readonly PdfName _dp = new("DecodeParms");
    private static readonly PdfName _dp2 = new("DP");
    private static readonly PdfName _predictor = new("Predictor");
    private static readonly PdfName _columns = new("Columns");
    private static readonly PdfName _colors = new("Colors");
    private static readonly PdfName _bpc = new("BitsPerComponent");
    private static readonly PdfName _earlyChange = new("EarlyChange");

    private static readonly HashSet<string> _imageFilters =
    [
        "DCTDecode", "DCT",
        "JPXDecode",
        "JBIG2Decode",
        "CCITTFaxDecode", "CCF",
    ];

    /// <summary>
    /// Tries to decode the full filter chain for <paramref name="stream"/>.
    /// Returns true when fully decoded; false when an image filter terminates the chain
    /// (in which case <paramref name="decoded"/> contains the partially decoded bytes
    /// up to and not including the image filter).
    /// </summary>
    internal static bool TryDecode(ParsedStream stream, out byte[] decoded)
    {
        var filters = GetFilterList(stream.Dictionary);
        var parms = GetParmsList(stream.Dictionary, filters.Count);

        var data = stream.RawBody.ToArray();
        var fullyDecoded = true;

        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            var p = i < parms.Count ? parms[i] : null;

            if (_imageFilters.Contains(f.Value))
            {
                fullyDecoded = false;
                break;
            }

            data = ApplyFilter(f, p, data);
        }

        decoded = data;
        return fullyDecoded;
    }

    /// <summary>Returns decoded bytes or null if an image filter prevents full decode.</summary>
    internal static byte[]? Decode(ParsedStream stream)
    {
        if (!TryDecode(stream, out var decoded))
            return null;
        return decoded;
    }

    private static byte[] ApplyFilter(PdfName filter, PdfDictionary? parms, byte[] input)
    {
        if (filter.Value is "FlateDecode" or "Fl")
        {
            var raw = InflateFlate(input);
            return ApplyPredictor(parms, raw);
        }
        if (filter.Value is "LZWDecode" or "LZW")
        {
            var earlyChange = 1;
            if (parms?.Get(_earlyChange) is PdfInteger ec)
                earlyChange = (int)ec.Value;
            var raw = DecodeLzw(input, earlyChange);
            return ApplyPredictor(parms, raw);
        }
        if (filter.Value is "ASCIIHexDecode" or "AHx")
            return DecodeAsciiHex(input);
        if (filter.Value is "ASCII85Decode" or "A85")
            return DecodeAscii85(input);
        if (filter.Value is "RunLengthDecode" or "RL")
            return DecodeRunLength(input);

        throw new InvalidDataException($"Unknown PDF filter: /{filter.Value}");
    }

    // ── FlateDecode ──────────────────────────────────────────────────────────

    internal static byte[] InflateFlate(byte[] input)
    {
        // FlateDecode is zlib (RFC 1950), but some producers emit raw deflate. Use the 2-byte
        // header as a fast-path hint for which to try first, then fall back to the other on a
        // format error — so neither a header-less raw-deflate stream nor a zlib stream is rejected.
        // The fallback must NEVER swallow the decompression-size cap (that would mask a bomb and
        // double-decompress), so the cap is thrown as a distinct exception type that is re-thrown.
        var primaryIsZlib = LooksLikeZlib(input);
        try
        {
            return Inflate(MakeDecompressor(input, primaryIsZlib));
        }
        catch (DecompressionLimitExceededException ex)
        {
            // A decompression bomb: surface it, never retry.
            throw new InvalidDataException(ex.Message);
        }
        catch
        {
            // Format error on the primary decoder — retry with the other (handles header-less
            // raw deflate vs. zlib-wrapped). Still never swallow the size cap.
            try
            {
                return Inflate(MakeDecompressor(input, !primaryIsZlib));
            }
            catch (DecompressionLimitExceededException ex)
            {
                throw new InvalidDataException(ex.Message);
            }
            catch (Exception inner)
            {
                // Normalise any BCL decode failure (InvalidDataException, IOException, …) to a
                // single InvalidDataException so callers see a consistent malformed-input signal.
                throw new InvalidDataException("FlateDecode: failed to decompress stream body.", inner);
            }
        }
    }

    private static Stream MakeDecompressor(byte[] input, bool zlib) => zlib
        ? new ZLibStream(new MemoryStream(input), CompressionMode.Decompress)
        : new DeflateStream(new MemoryStream(input), CompressionMode.Decompress);

    private static bool LooksLikeZlib(byte[] input)
    {
        // RFC 1950: low nibble of CMF is the compression method (8 = deflate), and the 16-bit
        // CMF/FLG header is a multiple of 31.
        if (input.Length < 2)
            return false;
        var cmf = input[0];
        var flg = input[1];
        return (cmf & 0x0F) == 8 && (((cmf << 8) | flg) % 31) == 0;
    }

    private static byte[] Inflate(Stream decompressor)
    {
        using var decoStream = decompressor;
        var ms = new MemoryStream();
        var buf = new byte[65536];
        long total = 0;
        int read;
        while ((read = decoStream.Read(buf, 0, buf.Length)) > 0)
        {
            total += read;
            if (total > MaxDecodedBytes)
                throw new DecompressionLimitExceededException(
                    $"Decompressed stream size exceeds {MaxDecodedBytes / (1024 * 1024)} MB cap.");
            ms.Write(buf, 0, read);
        }
        return ms.ToArray();
    }

    /// <summary>
    /// Internal signal that decompression exceeded <see cref="MaxDecodedBytes"/>. A distinct type
    /// lets <see cref="InflateFlate"/> distinguish the bomb guard from an ordinary format error so
    /// it re-throws (as <see cref="InvalidDataException"/>) instead of retrying the other decoder.
    /// </summary>
    private sealed class DecompressionLimitExceededException(string message) : Exception(message);

    // ── Predictors ───────────────────────────────────────────────────────────

    private static byte[] ApplyPredictor(PdfDictionary? parms, byte[] data)
    {
        if (parms is null) return data;
        if (parms.Get(_predictor) is not PdfInteger predObj) return data;

        var predictor = (int)predObj.Value;
        if (predictor == 1) return data; // None

        var columns = parms.Get(_columns) is PdfInteger col ? col.Value : 1;
        var colors = parms.Get(_colors) is PdfInteger clr ? clr.Value : 1;
        var bpc = parms.Get(_bpc) is PdfInteger b ? b.Value : 8;

        // Guard untrusted predictor parameters: out-of-range values could overflow the row-size
        // computation to a negative/huge array length (an uncaught OverflowException or an
        // allocation-amplification DoS) instead of a clean InvalidDataException.
        // Cap columns so that columns*colors*bpc (max 1M*32*16 = 512M) cannot overflow a 32-bit int.
        if (columns is < 1 or > (1 << 20) || colors is < 1 or > 32 || bpc is not (1 or 2 or 4 or 8 or 16))
            throw new InvalidDataException(
                $"FlateDecode predictor: invalid Columns/Colors/BitsPerComponent ({columns}/{colors}/{bpc}).");

        if (predictor == 2)
            return ApplyTiffPredictor2(data, (int)columns, (int)colors, (int)bpc);

        if (predictor >= 10 && predictor <= 15)
            return ApplyPngPredictor(data, (int)columns, (int)colors, (int)bpc);

        return data;
    }

    private static byte[] ApplyTiffPredictor2(byte[] data, int columns, int colors, int bpc)
    {
        // TIFF predictor 2: horizontal differencing. Each sample undoes the delta.
        // Only 8-bit per component supported here; other BPC is uncommon in practice.
        var rowBytes = (columns * colors * bpc + 7) / 8;
        if (data.Length == 0 || rowBytes == 0) return data;
        var rows = data.Length / rowBytes;
        var result = new byte[rows * rowBytes];

        for (var row = 0; row < rows; row++)
        {
            var src = row * rowBytes;
            var dst = row * rowBytes;

            if (bpc == 8)
            {
                for (var i = 0; i < rowBytes; i++)
                {
                    var prev = i >= colors ? result[dst + i - colors] : (byte)0;
                    result[dst + i] = (byte)(data[src + i] + prev);
                }
            }
            else
            {
                // Non-8-bit: copy as-is (not commonly needed by the PDF writer)
                Array.Copy(data, src, result, dst, rowBytes);
            }
        }
        return result;
    }

    private static byte[] ApplyPngPredictor(byte[] data, int columns, int colors, int bpc)
    {
        var rowBytes = (columns * colors * bpc + 7) / 8;
        var stride = rowBytes + 1; // +1 for the per-row filter byte
        if (data.Length == 0 || rowBytes == 0) return data;

        var rows = data.Length / stride;
        var result = new byte[rows * rowBytes];
        var prev = new byte[rowBytes];

        var bpp = Math.Max(1, colors * bpc / 8);

        for (var row = 0; row < rows; row++)
        {
            var filterType = data[row * stride];
            var srcStart = row * stride + 1;
            var dstStart = row * rowBytes;

            var dst = result.AsSpan(dstStart, rowBytes);
            data.AsSpan(srcStart, rowBytes).CopyTo(dst);

            switch (filterType)
            {
                case 0: // None
                    break;
                case 1: // Sub
                    for (var x = bpp; x < rowBytes; x++)
                        dst[x] = (byte)(dst[x] + dst[x - bpp]);
                    break;
                case 2: // Up
                    for (var x = 0; x < rowBytes; x++)
                        dst[x] = (byte)(dst[x] + prev[x]);
                    break;
                case 3: // Average
                    for (var x = 0; x < rowBytes; x++)
                    {
                        var a = x >= bpp ? dst[x - bpp] : (byte)0;
                        dst[x] = (byte)(dst[x] + (a + prev[x]) / 2);
                    }
                    break;
                case 4: // Paeth
                    for (var x = 0; x < rowBytes; x++)
                    {
                        var a = x >= bpp ? dst[x - bpp] : (byte)0;
                        var b = prev[x];
                        var c = x >= bpp ? prev[x - bpp] : (byte)0;
                        dst[x] = (byte)(dst[x] + PaethPredictor(a, b, c));
                    }
                    break;
                default:
                    throw new InvalidDataException(
                        $"PNG predictor: unsupported row filter type {filterType}.");
            }

            dst.CopyTo(prev.AsSpan());
        }
        return result;
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        return pa <= pb && pa <= pc ? a : (pb <= pc ? b : c);
    }

    // ── LZWDecode ────────────────────────────────────────────────────────────

    private static byte[] DecodeLzw(byte[] input, int earlyChange)
    {
        const int ClearCode = 256;
        const int EoiCode = 257;

        var output = new MemoryStream();
        var table = new List<byte[]>(4096);
        int codeSize = 9;
        byte[]? prevEntry = null;

        void ResetTable()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
                table.Add([(byte)i]);
            table.Add([]); // 256 = clear
            table.Add([]); // 257 = EOI
            codeSize = 9;
            prevEntry = null;
        }

        ResetTable();

        var bitPos = 0L;
        var inputLen = (long)input.Length * 8;

        long ReadCode()
        {
            var code = 0L;
            for (var bit = 0; bit < codeSize; bit++)
            {
                if (bitPos >= inputLen) return EoiCode;
                var byteIdx = (int)(bitPos / 8);
                var bitIdx = 7 - (int)(bitPos % 8);
                if ((input[byteIdx] & (1 << bitIdx)) != 0)
                    code |= 1L << (codeSize - 1 - bit);
                bitPos++;
            }
            return code;
        }

        void MaybeGrow()
        {
            // EarlyChange=1: grow when table size equals (1<<codeSize)-1
            // EarlyChange=0: grow when table size equals (1<<codeSize)
            var threshold = earlyChange == 1
                ? (1 << codeSize) - 1
                : (1 << codeSize);

            if (table.Count >= threshold && codeSize < 12)
                codeSize++;
        }

        while (true)
        {
            MaybeGrow();
            var code = (int)ReadCode();

            if (code == EoiCode) break;
            if (code == ClearCode)
            {
                ResetTable();
                continue;
            }

            byte[] entry;
            if (code < table.Count)
            {
                entry = table[code];
            }
            else if (code == table.Count && prevEntry is not null)
            {
                // Special case: code not yet in table — entry = prevEntry + prevEntry[0]
                entry = [.. prevEntry, prevEntry[0]];
            }
            else
            {
                throw new InvalidDataException($"LZWDecode: invalid code {code} at table size {table.Count}.");
            }

            if (output.Length + entry.Length > MaxDecodedBytes)
                throw new InvalidDataException(
                    $"LZWDecode: decompressed size exceeds {MaxDecodedBytes / (1024 * 1024)} MB cap.");

            output.Write(entry);

            if (prevEntry is not null && table.Count < 4096)
                table.Add([.. prevEntry, entry[0]]);

            prevEntry = entry;
        }

        return output.ToArray();
    }

    // ── ASCIIHexDecode ───────────────────────────────────────────────────────

    private static byte[] DecodeAsciiHex(byte[] input)
    {
        var output = new MemoryStream();
        var i = 0;
        while (i < input.Length)
        {
            var b = input[i++];
            if (b == (byte)'>') break; // EOD
            if (IsWhitespace(b)) continue;
            var hi = HexDigit(b);
            if (hi < 0)
                throw new InvalidDataException($"ASCIIHexDecode: invalid hex byte 0x{b:X2}.");

            byte lo = 0;
            // Find next non-whitespace
            while (i < input.Length && IsWhitespace(input[i])) i++;
            if (i < input.Length && input[i] != (byte)'>')
            {
                var lb = input[i++];
                var ld = HexDigit(lb);
                if (ld < 0)
                    throw new InvalidDataException($"ASCIIHexDecode: invalid hex byte 0x{lb:X2}.");
                lo = (byte)ld;
            }
            output.WriteByte((byte)((hi << 4) | lo));
        }
        return output.ToArray();
    }

    // ── ASCII85Decode ────────────────────────────────────────────────────────

    private static byte[] DecodeAscii85(byte[] input)
    {
        var output = new MemoryStream();
        var i = 0;
        Span<byte> group = stackalloc byte[5];
        while (i < input.Length)
        {
            var b = input[i];
            if (IsWhitespace(b)) { i++; continue; }

            // EOD marker '~>'
            if (b == (byte)'~')
            {
                if (i + 1 < input.Length && input[i + 1] == (byte)'>') break;
                throw new InvalidDataException("ASCII85Decode: invalid '~' not followed by '>'.");
            }

            if (b == (byte)'z')
            {
                // 'z' encodes four zero bytes
                output.Write([0, 0, 0, 0]);
                i++;
                continue;
            }

            // Collect up to 5 chars in the range '!'(33) to 'u'(117)
            var count = 0;
            while (count < 5 && i < input.Length)
            {
                var cb = input[i];
                if (IsWhitespace(cb)) { i++; continue; }
                if (cb == (byte)'~') break;
                if (cb < 33 || cb > 117)
                    throw new InvalidDataException($"ASCII85Decode: invalid character 0x{cb:X2}.");
                group[count++] = (byte)(cb - 33);
                i++;
            }

            if (count == 0) continue;

            // A final group must hold 2..5 characters; a single trailing character is invalid and
            // would otherwise emit one spurious byte.
            if (count == 1)
                throw new InvalidDataException("ASCII85Decode: final group has a single character.");

            // Pad to 5 with 'u' value = 84
            for (var p = count; p < 5; p++)
                group[p] = 84;

            var val = (long)group[0] * 52200625L
                    + (long)group[1] * 614125L
                    + (long)group[2] * 7225L
                    + (long)group[3] * 85L
                    + group[4];

            if (val > 0xFFFFFFFFL)
                throw new InvalidDataException("ASCII85Decode: group value out of range.");

            // Emit count-1 bytes
            var bytesToEmit = count - 1;
            output.WriteByte((byte)((val >> 24) & 0xFF));
            if (bytesToEmit >= 2) output.WriteByte((byte)((val >> 16) & 0xFF));
            if (bytesToEmit >= 3) output.WriteByte((byte)((val >> 8) & 0xFF));
            if (bytesToEmit >= 4) output.WriteByte((byte)(val & 0xFF));
        }
        return output.ToArray();
    }

    // ── RunLengthDecode ──────────────────────────────────────────────────────

    private static byte[] DecodeRunLength(byte[] input)
    {
        var output = new MemoryStream();
        var i = 0;
        while (i < input.Length)
        {
            var length = input[i++];
            if (length == 128) break; // EOD
            if (length < 128)
            {
                // literal: copy (length+1) bytes
                var count = length + 1;
                if (i + count > input.Length)
                    throw new InvalidDataException("RunLengthDecode: literal run extends past end of input.");
                if (output.Length + count > MaxDecodedBytes)
                    throw new InvalidDataException(
                        $"RunLengthDecode: decompressed size exceeds {MaxDecodedBytes / (1024 * 1024)} MB cap.");
                output.Write(input, i, count);
                i += count;
            }
            else
            {
                // repeat: 257 - length copies of next byte
                var count = 257 - length;
                if (i >= input.Length)
                    throw new InvalidDataException("RunLengthDecode: repeat run missing data byte.");
                var b = input[i++];
                if (output.Length + count > MaxDecodedBytes)
                    throw new InvalidDataException(
                        $"RunLengthDecode: decompressed size exceeds {MaxDecodedBytes / (1024 * 1024)} MB cap.");
                for (var j = 0; j < count; j++)
                    output.WriteByte(b);
            }
        }
        return output.ToArray();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static List<PdfName> GetFilterList(PdfDictionary dict)
    {
        // /Filter only. /F in a stream dictionary is the (external) file specification,
        // not a filter abbreviation, so it must not be consulted here.
        var filterObj = dict.Get(PdfName.Filter);
        if (filterObj is null) return [];
        if (filterObj is PdfName n) return [n];
        if (filterObj is PdfArray arr)
        {
            var list = new List<PdfName>(arr.Count);
            for (var i = 0; i < arr.Count; i++)
            {
                if (arr[i] is PdfName fn) list.Add(fn);
            }
            return list;
        }
        return [];
    }

    private static List<PdfDictionary?> GetParmsList(PdfDictionary dict, int filterCount)
    {
        var pObj = dict.Get(_dp) ?? dict.Get(_dp2);
        if (pObj is null)
        {
            var list = new List<PdfDictionary?>(filterCount);
            for (var i = 0; i < filterCount; i++) list.Add(null);
            return list;
        }
        if (pObj is PdfDictionary pd) return [pd];
        if (pObj is PdfArray arr)
        {
            var list = new List<PdfDictionary?>(arr.Count);
            for (var i = 0; i < arr.Count; i++)
                list.Add(arr[i] is PdfDictionary d ? d : null);
            return list;
        }
        return [];
    }

    private static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}
