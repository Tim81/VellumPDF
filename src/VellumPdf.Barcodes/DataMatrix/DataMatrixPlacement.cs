// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.DataMatrix;

/// <summary>
/// The ECC 200 default symbol-character placement algorithm (ISO/IEC 16022:2024 Annex F): maps a
/// sequence of codeword bits onto the combined data-region mapping matrix (every data region's
/// interior, tiled edge-to-edge — see <see cref="DataMatrixSymbolSizes"/>'s remarks on the
/// distinction between a data region and an interleaving block). Placement runs entirely on this
/// abstract module grid; region finder/timing patterns are drawn separately once the per-region
/// interiors are sliced back out (<c>DataMatrixEncoder</c>).
///
/// <para>
/// Codeword bits are placed 8 at a time in a repeating diagonal "utah" pattern (named for its
/// step-shaped footprint) that sweeps up-and-right, then down-and-left, alternating whenever it
/// runs off an edge. A shape cell that runs past the matrix's top or left edge reappears at the
/// opposite edge — but not via a plain modulus: Annex F's own wrap rule also shifts the *other*
/// coordinate by an offset that depends on the mapping matrix's own row/column count modulo 8 (see
/// <c>WrapAndPlace</c> below). Verified exactly against ISO/IEC 16022's own published 8x8
/// bit-placement figure (see <c>DataMatrixPlacementTests</c>), which reproduces it bit for bit —
/// though that smallest size's row/column counts happen to make the wrap offset zero, so it alone
/// cannot distinguish the correct wrap rule from a plain modulus. Every other size can, and does
/// (see the zxing-cpp decode-oracle round trips in <c>ZxingDecodeOracleTests</c>).
/// </para>
///
/// <para>
/// Four fixed corner patterns replace the regular utah shape at the handful of sweep positions
/// the wrap-around cannot reach without reusing a cell an earlier pass already claimed. Which
/// pattern (if any) applies is decided once per outer sweep pass, purely from the sweep's current
/// position and the mapping matrix's column count modulo 4 and 8 — never by detecting a collision
/// at placement time. (An earlier revision of this file guessed at the corner positions and
/// substituted one only when the regular sweep collided, which is not how Annex F actually
/// selects them; that produced a self-consistent but wrong grid for every size above 10x10.)
/// </para>
///
/// <para>
/// Four of the 24 square sizes — 12x12, 16x16, 20x20 and 24x24 — have a mapping matrix whose cell
/// count is not a multiple of 8 (their 10x10, 14x14, 18x18 and 22x22 interiors hold 100, 196, 324
/// and 484 cells against the 96, 192, 320 and 480 bits their codewords provide). ISO/IEC 16022
/// leaves the 2-cell shortfall unused (always light) at the same two positions, next to the
/// bottom-right corner, every time; <see cref="Place"/> asserts these are the *only* cells a
/// correctly-run placement can leave unfilled.
/// </para>
/// </summary>
internal static class DataMatrixPlacement
{
    /// <summary>
    /// Places <paramref name="codewords"/>' bits (most significant bit first, in codeword order)
    /// onto a <paramref name="rows"/> x <paramref name="columns"/> mapping matrix, returning which
    /// cells are dark. <paramref name="rows"/> x <paramref name="columns"/> must be exactly 8
    /// times <paramref name="codewords"/>'s length, or 8 times that length minus 2 for the four
    /// symbol sizes with the documented 2-cell shortfall above (true of every
    /// <see cref="DataMatrixSize"/> in this package's table).
    /// </summary>
    internal static bool[,] Place(int[] codewords, int rows, int columns)
    {
        var modules = new bool[rows, columns];
        var filled = new bool[rows, columns];
        var bitPosition = 0;

        // Annex F's wrap rule: a shape cell that runs past the mapping matrix's top or left edge
        // reappears at the opposite edge, offset along the *other* axis by an amount depending on
        // the matrix's own size modulo 8. Getting this offset right (rather than a plain modulus)
        // is what makes every symbol size place correctly, not just the smallest.
        void WrapAndPlace(int row, int column)
        {
            if (row < 0)
            {
                row += rows;
                column += 4 - ((rows + 4) % 8);
            }

            if (column < 0)
            {
                column += columns;
                row += 4 - ((columns + 4) % 8);
            }

            if (filled[row, column])
            {
                throw new InvalidOperationException(
                    $"Data Matrix placement tried to reuse cell ({row},{column}) for bit {bitPosition} of a {rows}x{columns} mapping matrix — an internal placement-algorithm defect.");
            }

            var codewordIndex = bitPosition / 8;
            var bitInCodeword = bitPosition % 8; // 0 = most significant
            var value = codewordIndex < codewords.Length ? codewords[codewordIndex] : 0;
            modules[row, column] = ((value >> (7 - bitInCodeword)) & 1) != 0;
            filled[row, column] = true;
            bitPosition++;
        }

        // The regular "utah" shape (Annex F): one codeword's 8 bits, most significant first,
        // anchored at (row, column) — its own bottom-right corner.
        void PlaceUtah(int row, int column)
        {
            WrapAndPlace(row - 2, column - 2);
            WrapAndPlace(row - 2, column - 1);
            WrapAndPlace(row - 1, column - 2);
            WrapAndPlace(row - 1, column - 1);
            WrapAndPlace(row - 1, column);
            WrapAndPlace(row, column - 2);
            WrapAndPlace(row, column - 1);
            WrapAndPlace(row, column);
        }

        // The four fixed corner patterns (Annex F), each one codeword's worth of bits at cells the
        // regular sweep's wrap-around cannot reach cleanly. At most one applies per symbol.
        void PlaceCorner1()
        {
            WrapAndPlace(rows - 1, 0);
            WrapAndPlace(rows - 1, 1);
            WrapAndPlace(rows - 1, 2);
            WrapAndPlace(0, columns - 2);
            WrapAndPlace(0, columns - 1);
            WrapAndPlace(1, columns - 1);
            WrapAndPlace(2, columns - 1);
            WrapAndPlace(3, columns - 1);
        }

        void PlaceCorner2()
        {
            WrapAndPlace(rows - 3, 0);
            WrapAndPlace(rows - 2, 0);
            WrapAndPlace(rows - 1, 0);
            WrapAndPlace(0, columns - 4);
            WrapAndPlace(0, columns - 3);
            WrapAndPlace(0, columns - 2);
            WrapAndPlace(0, columns - 1);
            WrapAndPlace(1, columns - 1);
        }

        void PlaceCorner3()
        {
            WrapAndPlace(rows - 3, 0);
            WrapAndPlace(rows - 2, 0);
            WrapAndPlace(rows - 1, 0);
            WrapAndPlace(0, columns - 2);
            WrapAndPlace(0, columns - 1);
            WrapAndPlace(1, columns - 1);
            WrapAndPlace(2, columns - 1);
            WrapAndPlace(3, columns - 1);
        }

        void PlaceCorner4()
        {
            WrapAndPlace(rows - 1, 0);
            WrapAndPlace(rows - 1, columns - 1);
            WrapAndPlace(0, columns - 3);
            WrapAndPlace(0, columns - 2);
            WrapAndPlace(0, columns - 1);
            WrapAndPlace(1, columns - 3);
            WrapAndPlace(1, columns - 2);
            WrapAndPlace(1, columns - 1);
        }

        var row = 4;
        var column = 0;
        do
        {
            // Which corner pattern (if any) substitutes for the regular sweep at this exact
            // position: Annex F ties each to the mapping matrix's column count modulo 4 and 8,
            // checked only here, once per outer pass.
            if (row == rows && column == 0) PlaceCorner1();
            if (row == rows - 2 && column == 0 && columns % 4 != 0) PlaceCorner2();
            if (row == rows - 2 && column == 0 && columns % 8 == 4) PlaceCorner3();
            if (row == rows + 4 && column == 2 && columns % 8 == 0) PlaceCorner4();

            // Up-and-right diagonal sweep.
            do
            {
                if (row < rows && column >= 0 && !filled[row, column]) PlaceUtah(row, column);
                row -= 2;
                column += 2;
            } while (row >= 0 && column < columns);

            row += 1;
            column += 3;

            // Down-and-left diagonal sweep.
            do
            {
                if (row >= 0 && column < columns && !filled[row, column]) PlaceUtah(row, column);
                row += 2;
                column -= 2;
            } while (row < rows && column >= 0);

            row += 3;
            column += 1;
        } while (row < rows || column < columns);

        // The one fixed pattern outside the sweep/corner system: Annex F always sets the very
        // last cell together with its immediate diagonal neighbour, unless an earlier pass already
        // reached the last cell first.
        if (!filled[rows - 1, columns - 1])
        {
            modules[rows - 1, columns - 1] = true;
            filled[rows - 1, columns - 1] = true;
            modules[rows - 2, columns - 2] = true;
            filled[rows - 2, columns - 2] = true;
        }

        AssertEveryCellAccountedFor(filled, rows, columns);
        return modules;
    }

    /// <summary>
    /// Confirms the sweep, its corner substitutions and the final fixed cell between them
    /// accounted for every mapping-matrix cell — except, for 12x12, 16x16, 20x20 and 24x24 only,
    /// the two specific cells ISO/IEC 16022 itself leaves unused (see this type's remarks). A
    /// leftover cell anywhere else means a real placement defect, not this documented exception,
    /// so this throws rather than silently leaving a light module where a codeword bit belongs.
    /// </summary>
    private static void AssertEveryCellAccountedFor(bool[,] filled, int rows, int columns)
    {
        for (var r = 0; r < rows; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                if (filled[r, c]) continue;
                if (r == rows - 2 && c == columns - 1) continue;
                if (r == rows - 1 && c == columns - 2) continue;

                throw new InvalidOperationException(
                    $"Data Matrix placement left cell ({r},{c}) of a {rows}x{columns} mapping matrix unfilled — an internal placement-algorithm defect.");
            }
        }
    }
}
