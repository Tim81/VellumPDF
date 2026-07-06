// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// Builds a full-size QR Code symbol's module grid: the function patterns (finders, separators,
/// timing patterns, alignment patterns and the fixed dark module, ISO/IEC 18004 §6.3, §7.7.2),
/// the two-column zig-zag data placement (§7.7.3), and the format/version information areas
/// (§7.9.1, §7.10). Micro QR has a simpler, single-finder layout of its own; see
/// <c>MicroQrEncoder</c>.
/// </summary>
internal static class QrMatrixBuilder
{
    /// <summary>Returns the module grid side length for QR Code <paramref name="version"/> (1-40).</summary>
    internal static int SizeForVersion(int version) => 17 + (4 * version);

    /// <summary>
    /// Builds the blank symbol with every function pattern in place and the format/version
    /// information areas reserved (but not yet filled in — that happens once the mask is chosen).
    /// </summary>
    internal static (BarcodeMatrix Matrix, bool[,] IsFunction) BuildFunctionPatterns(int version)
    {
        var size = SizeForVersion(version);
        var matrix = new BarcodeMatrix(size, size);
        var isFunction = new bool[size, size];

        DrawFinderAndSeparator(matrix, isFunction, size, 0, 0);
        DrawFinderAndSeparator(matrix, isFunction, size, 0, size - 7);
        DrawFinderAndSeparator(matrix, isFunction, size, size - 7, 0);

        DrawTimingPatterns(matrix, isFunction, size);

        var centres = QrTables.GetAlignmentCentres(version);
        for (var i = 0; i < centres.Count; i++)
        {
            for (var j = 0; j < centres.Count; j++)
            {
                if ((i == 0 && j == 0) || (i == 0 && j == centres.Count - 1) || (i == centres.Count - 1 && j == 0))
                    continue; // overlaps a finder pattern
                DrawAlignmentPattern(matrix, isFunction, centres[i], centres[j]);
            }
        }

        // The fixed dark module (§7.9.1): always at (row 4V+9, column 8).
        var darkRow = (4 * version) + 9;
        matrix.SetDark(8, darkRow, true);
        isFunction[darkRow, 8] = true;

        ReserveFormatInfo(isFunction, size);
        if (version >= 7) ReserveVersionInfo(isFunction, size);

        return (matrix, isFunction);
    }

    /// <summary>
    /// Places <paramref name="codewords"/> into the encoding region via the two-column zig-zag
    /// scan (§7.7.3): columns from right to left, alternating upward and downward starting upward
    /// at the rightmost pair, skipping every function module. Used by both QR (which has a
    /// vertical timing column, at <paramref name="skipColumn"/> = 6, to route around) and Micro QR
    /// (no such column, so <paramref name="skipColumn"/> is <c>null</c> — its single timing column
    /// already sits at the left edge, outside the scan's leftmost pair).
    ///
    /// <para>
    /// The up/down alternation is tracked by a plain per-pair counter rather than derived from the
    /// column index itself: a column-index-based shortcut (<c>((x + 1) &amp; 2) == 0</c>) only
    /// alternates correctly starting from the rightmost pair when <c>size mod 4 == 1</c>, which
    /// every full-size QR side length happens to satisfy (17 + 4 × version) but only half of Micro
    /// QR's four side lengths do (13 and 17, not 11 or 15) — so that shortcut silently reversed the
    /// scan direction for M1 and M3, corrupting every codeword from the second column-pair onwards
    /// even though each individual module was still written to a validly-reserved cell.
    /// </para>
    /// </summary>
    /// <param name="matrix">The symbol being built.</param>
    /// <param name="isFunction">The function-module map from <see cref="BuildFunctionPatterns"/> (or its Micro QR equivalent).</param>
    /// <param name="size">The symbol's side length.</param>
    /// <param name="codewords">The final interleaved codeword sequence.</param>
    /// <param name="skipColumn">The function-pattern column to route around (6 for QR; <c>null</c> for Micro QR).</param>
    /// <param name="halfWidthCodewordIndex">
    /// The index, if any, of a codeword that contributes only its high 4 bits (Micro QR versions
    /// M1 and M3's final data codeword) rather than the usual 8. The 4 real bits live in the high
    /// nibble because that is how <see cref="QrBitStreamBuilder.Finish"/> leaves them (byte-aligned,
    /// matching how Reed-Solomon treated the codeword) rather than shifted down to a compact value.
    /// </param>
    internal static void PlaceData(BarcodeMatrix matrix, bool[,] isFunction, int size, ReadOnlySpan<byte> codewords, int? skipColumn = 6, int? halfWidthCodewordIndex = null)
    {
        var bits = new List<bool>(codewords.Length * 8);
        for (var i = 0; i < codewords.Length; i++)
        {
            var width = i == halfWidthCodewordIndex ? 4 : 8;
            for (var b = 7; b >= 8 - width; b--)
                bits.Add(((codewords[i] >> b) & 1) != 0);
        }

        var bitIndex = 0;
        var pairIndex = 0;
        for (var x = size - 1; x >= 1; x -= 2, pairIndex++)
        {
            if (x == skipColumn) x--;

            var upward = pairIndex % 2 == 0;
            for (var vert = 0; vert < size; vert++)
            {
                var y = upward ? size - 1 - vert : vert;
                for (var j = 0; j < 2; j++)
                {
                    var xx = x - j;
                    if (isFunction[y, xx]) continue;

                    if (bitIndex < bits.Count)
                    {
                        matrix.SetDark(xx, y, bits[bitIndex]);
                        bitIndex++;
                    }
                    // Otherwise this is a remainder module: it stays light (the matrix's default).
                }
            }
        }
    }

    /// <summary>Writes the 15-bit masked format information into both reserved copies (Figure 25).</summary>
    internal static void PlaceFormatInfo(BarcodeMatrix matrix, int size, int bits)
    {
        for (var i = 0; i <= 5; i++) SetBit(matrix, 8, i, bits, i);
        SetBit(matrix, 8, 7, bits, 6);
        SetBit(matrix, 8, 8, bits, 7);
        SetBit(matrix, 7, 8, bits, 8);
        for (var i = 9; i < 15; i++) SetBit(matrix, 14 - i, 8, bits, i);

        for (var i = 0; i < 8; i++) SetBit(matrix, size - 1 - i, 8, bits, i);
        for (var i = 8; i < 15; i++) SetBit(matrix, 8, size - 15 + i, bits, i);
    }

    /// <summary>Writes the 18-bit version information into both reserved copies (v7+, Figure 27/28).</summary>
    internal static void PlaceVersionInfo(BarcodeMatrix matrix, int size, int bits)
    {
        for (var i = 0; i < 18; i++)
        {
            var bit = (bits >> i) & 1;
            var a = size - 11 + (i % 3);
            var b = i / 3;
            matrix.SetDark(a, b, bit != 0);
            matrix.SetDark(b, a, bit != 0);
        }
    }

    private static void SetBit(BarcodeMatrix matrix, int x, int y, int bits, int bitIndex) =>
        matrix.SetDark(x, y, ((bits >> bitIndex) & 1) != 0);

    private static void ReserveFormatInfo(bool[,] isFunction, int size)
    {
        for (var i = 0; i <= 8; i++)
        {
            isFunction[i, 8] = true; // vertical run near the top-left finder (row 6 already set by the timing pattern)
            isFunction[8, i] = true; // horizontal run near the top-left finder
        }

        for (var i = 0; i < 8; i++) isFunction[8, size - 1 - i] = true; // horizontal run near the top-right finder
        for (var i = 8; i < 15; i++) isFunction[size - 15 + i, 8] = true; // vertical run near the bottom-left finder
    }

    private static void ReserveVersionInfo(bool[,] isFunction, int size)
    {
        for (var i = 0; i < 18; i++)
        {
            var a = size - 11 + (i % 3);
            var b = i / 3;
            isFunction[b, a] = true;
            isFunction[a, b] = true;
        }
    }

    /// <summary>Draws a finder pattern (dark 7x7/light 5x5/dark 3x3, ISO/IEC 18004 §6.3.3) with a 1-module light separator, clipped to the matrix (so a corner placement naturally yields Micro QR's single-sided separator). Shared with <c>MicroQrEncoder</c>.</summary>
    internal static void DrawFinderAndSeparator(BarcodeMatrix matrix, bool[,] isFunction, int size, int topRow, int topCol)
    {
        for (var dr = -1; dr <= 7; dr++)
        {
            var row = topRow + dr;
            if (row < 0 || row >= size) continue;
            for (var dc = -1; dc <= 7; dc++)
            {
                var col = topCol + dc;
                if (col < 0 || col >= size) continue;

                isFunction[row, col] = true;
                if (dr is >= 0 and <= 6 && dc is >= 0 and <= 6)
                {
                    var dark = dr == 0 || dr == 6 || dc == 0 || dc == 6 || (dr is >= 2 and <= 4 && dc is >= 2 and <= 4);
                    matrix.SetDark(col, row, dark);
                }
                // Else: within the 1-module separator ring, which stays light.
            }
        }
    }

    private static void DrawTimingPatterns(BarcodeMatrix matrix, bool[,] isFunction, int size)
    {
        for (var i = 8; i <= size - 9; i++)
        {
            isFunction[6, i] = true;
            matrix.SetDark(i, 6, i % 2 == 0);
            isFunction[i, 6] = true;
            matrix.SetDark(6, i, i % 2 == 0);
        }
    }

    private static void DrawAlignmentPattern(BarcodeMatrix matrix, bool[,] isFunction, int centreRow, int centreCol)
    {
        for (var dr = -2; dr <= 2; dr++)
        {
            for (var dc = -2; dc <= 2; dc++)
            {
                var row = centreRow + dr;
                var col = centreCol + dc;
                isFunction[row, col] = true;
                matrix.SetDark(col, row, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }
        }
    }
}
