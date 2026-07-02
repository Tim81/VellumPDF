// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts.Cff;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// Extracts advance widths from a CFF font program by interpreting each glyph's Type 2 charstring.
/// Per Adobe Technical Note 5177 §3.1, the advance width is the optional leading operand before the
/// first stack-clearing operator; absent means use defaultWidthX, present means nominalWidthX plus
/// that operand. For CID-keyed fonts the nominal/default pair is per-FD, resolved via FDSelect.
/// Returned widths are in PDF 1/1000 text-space units: the Top DICT FontMatrix (op 12 7) first
/// element is read and used to scale charstring-space widths (scale = matrix[0] * 1000). For the
/// standard 1000-unit em (matrix[0] = 0.001) the scale is exactly 1.0 and no scaling is applied.
/// For CID-keyed fonts a per-FD FontMatrix (also op 12 7 in each FD dict) composes with the
/// top-level matrix when present.
/// </summary>
/// <remarks>
/// Clean-room from Adobe TN 5176 (CFF) and TN 5177 (Type 2 charstrings). AOT-safe: no reflection.
/// Width extraction requires only the first few bytes of each charstring; subroutine bodies are not
/// followed (a subr call before any stack-clearing operator is treated as an absent width, using
/// defaultWidthX — conservatively correct for the vast majority of fonts).
/// </remarks>
internal sealed class CffWidths
{
    private readonly CffFont _font;
    private readonly double[] _nominalWidths;
    private readonly double[] _defaultWidths;
    // FDSelect: format 0 (gid-indexed byte array) or format 3 ranges. Null for non-CID fonts.
    private readonly byte[]? _fdSelect0;
    private readonly FdRange[]? _fdSelect3;
    // FontMatrix scaling: charstring-space widths * _topScale = PDF 1/1000 text-space widths.
    // For a standard 1000-unit em (matrix[0] = 0.001) this is 1.0.
    private readonly double _topScale;
    // Per-FD scales for CID-keyed fonts. When null, _topScale applies to all FDs.
    // When non-null, _fdScales[fd] already incorporates _topScale (top * fd composition).
    private readonly double[]? _fdScales;

    private CffWidths(CffFont font, double[] nominalWidths, double[] defaultWidths,
        byte[]? fdSelect0, FdRange[]? fdSelect3, double topScale, double[]? fdScales)
    {
        _font = font;
        _nominalWidths = nominalWidths;
        _defaultWidths = defaultWidths;
        _fdSelect0 = fdSelect0;
        _fdSelect3 = fdSelect3;
        _topScale = topScale;
        _fdScales = fdScales;
    }

    public int GlyphCount => _font.NumGlyphs;

    public bool TryGetWidth(int gid, out double widthX)
    {
        widthX = 0;
        if (gid < 0 || gid >= _font.NumGlyphs)
            return false;

        var fdIndex = GetFdIndex(gid);
        var nominalWidth = fdIndex < _nominalWidths.Length ? _nominalWidths[fdIndex] : 0;
        var defaultWidth = fdIndex < _defaultWidths.Length ? _defaultWidths[fdIndex] : 0;

        ReadOnlyMemory<byte> cs;
        try { cs = _font.GetCharstring(gid); }
        catch { return false; }

        var w = ExtractWidth(cs.Span, nominalWidth, defaultWidth);
        if (!w.HasValue)
            return false; // subroutine call before width op — width unknown, skip check

        // Resolve the effective scale for this glyph's FD (composed top * fd, or just top).
        var scale = _fdScales is not null && fdIndex < _fdScales.Length
            ? _fdScales[fdIndex]
            : _topScale;

        widthX = w.Value * scale;
        return true;
    }

    private int GetFdIndex(int gid)
    {
        if (_fdSelect0 is not null)
            return gid < _fdSelect0.Length ? _fdSelect0[gid] : 0;
        if (_fdSelect3 is not null)
        {
            foreach (var r in _fdSelect3)
                if (gid >= r.First && gid < r.Limit)
                    return r.Fd;
        }
        return 0;
    }

    // Reads the optional leading width from a Type2 charstring per TN 5177 §3.1.
    // Returns null when a subroutine call appears before any stack-clearing operator,
    // meaning the width cannot be determined without following the subroutine body.
    // Returns nominal + firstNumber when a number precedes the first stack-clearing op.
    // Returns defaultWidth when the first stack-clearing op has an empty stack.
    private static double? ExtractWidth(ReadOnlySpan<byte> cs, double nominal, double def)
    {
        var i = 0;
        var n = cs.Length;
        double? firstNumber = null;

        while (i < n)
        {
            var b0 = cs[i];

            // Number encodings:
            if (b0 >= 32 && b0 <= 246) { firstNumber ??= b0 - 139; i++; continue; }
            if (b0 >= 247 && b0 <= 250)
            {
                if (i + 1 >= n) return def;
                firstNumber ??= (b0 - 247) * 256 + cs[i + 1] + 108;
                i += 2; continue;
            }
            if (b0 >= 251 && b0 <= 254)
            {
                if (i + 1 >= n) return def;
                firstNumber ??= -(b0 - 251) * 256 - cs[i + 1] - 108;
                i += 2; continue;
            }
            if (b0 == 28) // shortint
            {
                if (i + 2 >= n) return def;
                firstNumber ??= (short)((cs[i + 1] << 8) | cs[i + 2]);
                i += 3; continue;
            }
            if (b0 == 255) // fixed 16.16
            {
                if (i + 4 >= n) return def;
                var intPart = (cs[i + 1] << 8) | cs[i + 2];
                var fracPart = (cs[i + 3] << 8) | cs[i + 4];
                firstNumber ??= intPart + fracPart / 65536.0;
                i += 5; continue;
            }

            // Two-byte escape (b0==12):
            if (b0 == 12) { i += 2; continue; } // not a width-bearing op

            // Stack-clearing operators where an optional leading width may precede the hints/moves:
            // 1=hstem, 3=vstem, 4=vmoveto, 18=hstemhm, 19=hintmask, 20=cntrmask,
            // 21=rmoveto, 22=hmoveto, 23=vstemhm, 14=endchar
            if (b0 is 1 or 3 or 4 or 14 or 18 or 19 or 20 or 21 or 22 or 23)
                return firstNumber.HasValue ? nominal + firstNumber.Value : def;

            // Subroutine calls: 10=callsubr, 29=callgsubr — cannot follow without the subr body;
            // return null so the caller skips the width check rather than comparing against defaultWidth.
            if (b0 is 10 or 29) return null;

            // 11=return (should not appear at top level), 15=vsindex — skip
            // Other drawing ops: 5,6,7,8,24,25,26,27,30,31 — these appear after the width is consumed
            i++;
        }
        return def;
    }

    /// <summary>
    /// Parses the CFF Private DICT at the given byte range to extract nominalWidthX (op 21)
    /// and defaultWidthX (op 20), per Adobe TN 5176 Table 23. Both default to 0 when their
    /// operator is absent. Always returns true (missing operators are not an error).
    /// </summary>
    private static bool TryParsePrivateDict(ReadOnlySpan<byte> data, out double nominalWidthX, out double defaultWidthX)
    {
        nominalWidthX = 0;
        defaultWidthX = 0;
        var operands = new double[8];
        var stackDepth = 0;
        var i = 0;

        while (i < data.Length)
        {
            var b0 = data[i];

            if (b0 >= 32 && b0 <= 246)
            {
                if (stackDepth < operands.Length) operands[stackDepth] = b0 - 139;
                stackDepth++;
                i++;
                continue;
            }
            if (b0 >= 247 && b0 <= 250)
            {
                if (i + 1 >= data.Length) return true;
                if (stackDepth < operands.Length) operands[stackDepth] = (b0 - 247) * 256 + data[i + 1] + 108;
                stackDepth++;
                i += 2;
                continue;
            }
            if (b0 >= 251 && b0 <= 254)
            {
                if (i + 1 >= data.Length) return true;
                if (stackDepth < operands.Length) operands[stackDepth] = -(b0 - 251) * 256 - data[i + 1] - 108;
                stackDepth++;
                i += 2;
                continue;
            }
            if (b0 == 28)
            {
                if (i + 2 >= data.Length) return true;
                if (stackDepth < operands.Length) operands[stackDepth] = (short)((data[i + 1] << 8) | data[i + 2]);
                stackDepth++;
                i += 3;
                continue;
            }
            if (b0 == 29)
            {
                if (i + 4 >= data.Length) return true;
                if (stackDepth < operands.Length)
                    operands[stackDepth] = (data[i + 1] << 24) | (data[i + 2] << 16) | (data[i + 3] << 8) | data[i + 4];
                stackDepth++;
                i += 5;
                continue;
            }
            if (b0 == 30)
            {
                // Real — skip nibble-pairs; we just push 0 (these are not width ops)
                i++;
                while (i < data.Length)
                {
                    var nb = data[i++];
                    if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F) break;
                }
                if (stackDepth < operands.Length) operands[stackDepth] = 0;
                stackDepth++;
                continue;
            }

            // Operators
            if (b0 == 12)
            {
                // Two-byte op — not related to widths in Private DICT
                i += 2;
                stackDepth = 0;
                continue;
            }

            // Single-byte operators (0–21 excluding 12, 28)
            // Per Adobe TN 5176 Table 23: op 20 = defaultWidthX, op 21 = nominalWidthX.
            if (b0 <= 21)
            {
                if (b0 == 20 && stackDepth > 0) defaultWidthX = operands[0]; // defaultWidthX
                if (b0 == 21 && stackDepth > 0) nominalWidthX = operands[0]; // nominalWidthX
                stackDepth = 0;
                i++;
                continue;
            }

            i++; // unknown byte, skip
        }
        return true;
    }

    /// <summary>
    /// Tries to create a <see cref="CffWidths"/> from a parsed <see cref="CffFont"/>.
    /// Returns false if the font cannot be processed (malformed offsets, etc.).
    /// </summary>
    public static bool TryCreate(CffFont font, out CffWidths? result)
    {
        result = null;
        var data = font.Data.Span;
        var len = font.Data.Length;

        // Parse the Top DICT FontMatrix (op 12 7) for the top-level scale.
        var topScale = ParseTopDictFontMatrixScale(font.TopDictBytes.Span);

        if (!font.IsCidKeyed)
        {
            // Non-CID: single Private DICT
            if (font.PrivateDictSize <= 0 || font.PrivateDictOffset <= 0
                || (long)font.PrivateDictOffset + font.PrivateDictSize > len)
            {
                // Missing Private DICT is valid (use defaults 0)
                result = new CffWidths(font, [0.0], [0.0], null, null, topScale, null);
                return true;
            }
            var privSpan = data.Slice(font.PrivateDictOffset, font.PrivateDictSize);
            TryParsePrivateDict(privSpan, out var nom, out var def);
            result = new CffWidths(font, [nom], [def], null, null, topScale, null);
            return true;
        }

        // CID-keyed: decode FDSelect and FDArray
        if (font.FdArrayOffset <= 0 || font.FdSelectOffset <= 0)
            return false;

        // Parse FDSelect at FdSelectOffset
        byte[]? fdSelect0 = null;
        FdRange[]? fdSelect3 = null;
        if (!TryParseFdSelect(data, len, font.FdSelectOffset, font.NumGlyphs, out fdSelect0, out fdSelect3))
            return false;

        // Parse FDArray at FdArrayOffset — INDEX of font dicts, each has a Private op (18)
        // and optionally a FontMatrix op (12 7) that composes with the top-level matrix.
        if (!TryParseFdArray(data, len, font.FdArrayOffset, topScale,
                out var nominalWidths, out var defaultWidths, out var fdScales))
            return false;

        result = new CffWidths(font, nominalWidths, defaultWidths, fdSelect0, fdSelect3, topScale, fdScales);
        return true;
    }

    private static bool TryParseFdSelect(ReadOnlySpan<byte> data, int len, int offset,
        int numGlyphs, out byte[]? fdSelect0, out FdRange[]? fdSelect3)
    {
        fdSelect0 = null;
        fdSelect3 = null;
        if (offset + 1 > len) return false;
        var format = data[offset];
        if (format == 0)
        {
            // Format 0: array of fd indices, one per glyph
            if (offset + 1 + numGlyphs > len) return false;
            fdSelect0 = data.Slice(offset + 1, numGlyphs).ToArray();
            return true;
        }
        if (format == 3)
        {
            // Format 3: nRanges (2 bytes) + nRanges * (first:2 + fd:1) + sentinel:2
            if (offset + 3 > len) return false;
            var nRanges = (data[offset + 1] << 8) | data[offset + 2];
            if (offset + 3 + nRanges * 3 + 2 > len) return false;
            var ranges = new FdRange[nRanges];
            for (var i = 0; i < nRanges; i++)
            {
                var p = offset + 3 + i * 3;
                var first = (data[p] << 8) | data[p + 1];
                var fd = data[p + 2];
                // Limit is the 'first' of the next range (or sentinel)
                int limit;
                if (i + 1 < nRanges)
                    limit = (data[offset + 3 + (i + 1) * 3] << 8) | data[offset + 3 + (i + 1) * 3 + 1];
                else
                {
                    var sentPos = offset + 3 + nRanges * 3;
                    limit = (data[sentPos] << 8) | data[sentPos + 1];
                }
                ranges[i] = new FdRange(first, limit, fd);
            }
            fdSelect3 = ranges;
            return true;
        }
        return false; // unknown format
    }

    private static bool TryParseFdArray(ReadOnlySpan<byte> data, int len, int offset, double topScale,
        out double[] nominalWidths, out double[] defaultWidths, out double[]? fdScales)
    {
        nominalWidths = [];
        defaultWidths = [];
        fdScales = null;
        try
        {
            var (count, offsets, dataBase) = CffFont.ReadIndexOffsets(data, offset, len);
            if (count == 0) return false;
            nominalWidths = new double[count];
            defaultWidths = new double[count];
            // Collect per-FD scales; if every FD has no FontMatrix (all 1.0) we leave fdScales null.
            var perFdScales = new double[count];
            var anyNonIdentity = false;
            for (var fd = 0; fd < count; fd++)
            {
                var fdStart = (int)offsets[fd];
                var fdEnd = (int)offsets[fd + 1];
                var fdLen = fdEnd - fdStart;
                if (fdLen < 0 || dataBase + fdStart - 1 < 0 || dataBase + fdEnd - 1 > len)
                {
                    perFdScales[fd] = topScale;
                    continue;
                }
                var fdDictSpan = data.Slice(dataBase + fdStart - 1, fdLen);
                var (privSize, privOffset) = ParseFdPrivateRef(fdDictSpan);
                if (privSize <= 0 || privOffset <= 0 || (long)privOffset + privSize > len)
                {
                    perFdScales[fd] = topScale;
                    continue;
                }
                var privSpan = data.Slice(privOffset, privSize);
                TryParsePrivateDict(privSpan, out nominalWidths[fd], out defaultWidths[fd]);

                // Parse per-FD FontMatrix (op 12 7) from the FD dict. If present it composes with
                // the top-level matrix: composed scale = topScale * fdMatrix[0] * 1000 / 1.0
                // (topScale already includes the * 1000 factor from the top-level matrix).
                // When absent (returns 1.0), the FD inherits topScale unchanged.
                var fdMatrixScale = ParseTopDictFontMatrixScale(fdDictSpan);
                if (Math.Abs(fdMatrixScale - 1.0) < 1e-9)
                {
                    // No per-FD FontMatrix: inherit top-level scale.
                    perFdScales[fd] = topScale;
                }
                else
                {
                    // Per-FD FontMatrix present: compose with top-level.
                    // topScale = topMatrix[0] * 1000; fdMatrixScale = fdMatrix[0] * 1000.
                    // Composed: topMatrix[0] * fdMatrix[0] * 1000 = topScale/1000 * fdMatrixScale.
                    perFdScales[fd] = topScale / 1000.0 * fdMatrixScale;
                    anyNonIdentity = true;
                }
                if (Math.Abs(perFdScales[fd] - 1.0) >= 1e-9)
                    anyNonIdentity = true;
            }
            // Only materialise the per-FD array when at least one FD differs from the identity scale,
            // so the common case (all 1.0) avoids an allocation.
            if (anyNonIdentity)
                fdScales = perFdScales;
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Parses a CFF DICT body (Top DICT or FD dict) for the FontMatrix operator (op 12 7) and returns
    // the scale factor matrix[0] * 1000 that converts charstring-space advance widths to PDF 1/1000
    // text-space units. Returns 1.0 (the standard 1000-unit em value) when the operator is absent or
    // the matrix cannot be parsed, so a missing or malformed FontMatrix never produces a wrong scale.
    //
    // CFF FontMatrix operands are real numbers encoded in the nibble format (byte 30); the six elements
    // are pushed as consecutive real operands before the two-byte operator 12 7. Only the first element
    // (index 0, the x-scale 'a') is needed.
    private static double ParseTopDictFontMatrixScale(ReadOnlySpan<byte> dict)
    {
        // We need to track operands to find the first one when op 12 7 appears.
        // Operands are pushed in order; we capture up to 6 (the full matrix) but only use [0].
        var operands = new double[6];
        var stackDepth = 0;
        var i = 0;

        while (i < dict.Length)
        {
            var b0 = dict[i];

            // Operand encodings (identical to CFF DICT encoding per TN 5176 §4)
            if (b0 >= 32 && b0 <= 246)
            {
                if (stackDepth < 6) operands[stackDepth] = b0 - 139;
                stackDepth++;
                i++;
                continue;
            }
            if (b0 >= 247 && b0 <= 250)
            {
                if (i + 1 >= dict.Length) break;
                if (stackDepth < 6) operands[stackDepth] = (b0 - 247) * 256 + dict[i + 1] + 108;
                stackDepth++;
                i += 2;
                continue;
            }
            if (b0 >= 251 && b0 <= 254)
            {
                if (i + 1 >= dict.Length) break;
                if (stackDepth < 6) operands[stackDepth] = -(b0 - 251) * 256 - dict[i + 1] - 108;
                stackDepth++;
                i += 2;
                continue;
            }
            if (b0 == 28)
            {
                if (i + 2 >= dict.Length) break;
                if (stackDepth < 6) operands[stackDepth] = (short)((dict[i + 1] << 8) | dict[i + 2]);
                stackDepth++;
                i += 3;
                continue;
            }
            if (b0 == 29)
            {
                if (i + 4 >= dict.Length) break;
                if (stackDepth < 6)
                    operands[stackDepth] = (dict[i + 1] << 24) | (dict[i + 2] << 16) | (dict[i + 3] << 8) | dict[i + 4];
                stackDepth++;
                i += 5;
                continue;
            }
            if (b0 == 30)
            {
                // Real number: nibble-encoded. Parse the value so we capture matrix[0] accurately.
                i++;
                var realVal = ParseCffReal(dict, ref i);
                if (stackDepth < 6) operands[stackDepth] = realVal;
                stackDepth++;
                continue;
            }

            // Operators
            if (b0 == 12)
            {
                i++;
                if (i >= dict.Length) break;
                var b1 = dict[i];
                i++;
                if (b1 == 7 && stackDepth > 0)
                {
                    // FontMatrix operator found. operands[0] is the 'a' element.
                    var a = operands[0];
                    // Guard against degenerate values (zero, negative, or absurdly large).
                    if (a > 0 && double.IsFinite(a))
                        return a * 1000.0;
                    return 1.0;
                }
                stackDepth = 0;
                continue;
            }
            if (b0 <= 21)
            {
                stackDepth = 0;
                i++;
                continue;
            }
            i++;
        }
        return 1.0; // absent or unparseable — default 1000-unit em scale
    }

    // Decodes a CFF real number (byte 30 nibble encoding, TN 5176 §4). On entry i points to the
    // first nibble byte (the byte after the 30 lead byte). On return i points past the last nibble byte.
    // Returns 0.0 on any decode failure. The nibble alphabet:
    //   0–9 → digits '0'–'9', 0xA → '.', 0xB → 'E', 0xC → 'E-', 0xD → reserved, 0xE → '-', 0xF → end.
    private static double ParseCffReal(ReadOnlySpan<byte> dict, ref int i)
    {
        // Build the decimal string from nibbles, bounded to avoid runaway on malformed data.
        Span<char> buf = stackalloc char[32];
        var len = 0;
        var done = false;

        while (i < dict.Length && !done && len < buf.Length - 4)
        {
            var nb = dict[i++];
            var hi = nb >> 4;
            var lo = nb & 0x0F;

            foreach (var nib in (ReadOnlySpan<int>)[hi, lo])
            {
                switch (nib)
                {
                    case >= 0 and <= 9:
                        if (len < buf.Length) buf[len++] = (char)('0' + nib);
                        break;
                    case 0xA: // decimal point
                        if (len < buf.Length) buf[len++] = '.';
                        break;
                    case 0xB: // E (positive exponent)
                        if (len < buf.Length) buf[len++] = 'E';
                        break;
                    case 0xC: // E- (negative exponent)
                        if (len + 2 <= buf.Length) { buf[len++] = 'E'; buf[len++] = '-'; }
                        break;
                    case 0xE: // minus sign
                        if (len < buf.Length) buf[len++] = '-';
                        break;
                    case 0xF: // end-of-real
                        done = true;
                        break;
                    default: // 0xD reserved — treat as end
                        done = true;
                        break;
                }
                if (done) break;
            }
        }

        if (len == 0) return 0.0;
        if (double.TryParse(buf[..len], System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var val))
            return val;
        return 0.0;
    }

    // Parses an FD's font dict (a DICT body) for the Private op (18): [size, offset].
    private static (int size, int offset) ParseFdPrivateRef(ReadOnlySpan<byte> dict)
    {
        var operands = new double[8];
        var stackDepth = 0;
        var i = 0;
        while (i < dict.Length)
        {
            var b0 = dict[i];
            if (b0 >= 32 && b0 <= 246) { if (stackDepth < 8) operands[stackDepth] = b0 - 139; stackDepth++; i++; continue; }
            if (b0 >= 247 && b0 <= 250) { if (i + 1 >= dict.Length) break; if (stackDepth < 8) operands[stackDepth] = (b0 - 247) * 256 + dict[i + 1] + 108; stackDepth++; i += 2; continue; }
            if (b0 >= 251 && b0 <= 254) { if (i + 1 >= dict.Length) break; if (stackDepth < 8) operands[stackDepth] = -(b0 - 251) * 256 - dict[i + 1] - 108; stackDepth++; i += 2; continue; }
            if (b0 == 28) { if (i + 2 >= dict.Length) break; if (stackDepth < 8) operands[stackDepth] = (short)((dict[i + 1] << 8) | dict[i + 2]); stackDepth++; i += 3; continue; }
            if (b0 == 29) { if (i + 4 >= dict.Length) break; if (stackDepth < 8) operands[stackDepth] = (dict[i + 1] << 24) | (dict[i + 2] << 16) | (dict[i + 3] << 8) | dict[i + 4]; stackDepth++; i += 5; continue; }
            if (b0 == 30) { i++; while (i < dict.Length) { var nb = dict[i++]; if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F) break; } if (stackDepth < 8) operands[stackDepth] = 0; stackDepth++; continue; }
            if (b0 == 12) { i++; if (i >= dict.Length) break; i++; stackDepth = 0; continue; }
            if (b0 <= 21)
            {
                if (b0 == 18 && stackDepth >= 2) // Private: [size, offset]
                    return ((int)operands[0], (int)operands[1]);
                stackDepth = 0; i++; continue;
            }
            i++;
        }
        return (0, 0);
    }

    /// <summary>
    /// Builds a CID-to-GID map from the CFF charset for a CID-keyed font.
    /// The charset maps GID → CID; this inverts it to CID → GID.
    /// Returns null on any parse error.
    /// </summary>
    public static Dictionary<int, int>? TryBuildCidToGidMap(CffFont font)
    {
        if (!font.IsCidKeyed) return null;
        var data = font.Data.Span;
        var len = font.Data.Length;
        var offset = font.CharsetOffset;
        if (offset <= 2) return null; // predefined charset (0/1/2) — not valid for CID-keyed
        if (offset >= len) return null;

        var numGlyphs = font.NumGlyphs;
        var map = new Dictionary<int, int>(numGlyphs);
        map[0] = 0; // GID 0 → CID 0 (.notdef)

        var format = data[offset];
        if (format == 0)
        {
            // Format 0: array of CIDs, one per GID (starting at GID 1)
            var pos = offset + 1;
            for (var gid = 1; gid < numGlyphs; gid++)
            {
                if (pos + 1 >= len) break;
                var cid = (data[pos] << 8) | data[pos + 1];
                map[cid] = gid;
                pos += 2;
            }
        }
        else if (format == 1)
        {
            // Format 1: array of ranges (first:2, nLeft:1)
            var pos = offset + 1;
            var gid = 1;
            while (gid < numGlyphs && pos + 2 < len)
            {
                var first = (data[pos] << 8) | data[pos + 1];
                var nLeft = data[pos + 2];
                pos += 3;
                for (var k = 0; k <= nLeft && gid < numGlyphs; k++, gid++)
                    map[first + k] = gid;
            }
        }
        else if (format == 2)
        {
            // Format 2: array of ranges (first:2, nLeft:2)
            var pos = offset + 1;
            var gid = 1;
            while (gid < numGlyphs && pos + 3 < len)
            {
                var first = (data[pos] << 8) | data[pos + 1];
                var nLeft = (data[pos + 2] << 8) | data[pos + 3];
                pos += 4;
                for (var k = 0; k <= nLeft && gid < numGlyphs; k++, gid++)
                    map[first + k] = gid;
            }
        }
        else
        {
            return null; // unknown format
        }

        return map;
    }

    private readonly struct FdRange(int first, int limit, int fd)
    {
        public int First { get; } = first;
        public int Limit { get; } = limit;
        public int Fd { get; } = fd;
    }
}
