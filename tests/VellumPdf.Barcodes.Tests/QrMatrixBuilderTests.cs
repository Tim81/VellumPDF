// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Direct tests for <see cref="QrMatrixBuilder.PlaceData"/>'s two-column zig-zag scan
/// (ISO/IEC 18004 §7.7.3), isolating the placement direction and half-width-codeword handling
/// from the rest of the QR/Micro QR pipeline.
/// </summary>
public sealed class QrMatrixBuilderTests
{
    [Theory]
    [InlineData(11)] // Micro QR M1 — size mod 4 == 3, where a column-index-based shortcut got this backwards
    [InlineData(13)] // Micro QR M2 — size mod 4 == 1, where that same shortcut happened to be right
    [InlineData(15)] // Micro QR M3 — size mod 4 == 3
    [InlineData(17)] // Micro QR M4 — size mod 4 == 1
    [InlineData(21)] // full-size QR version 1 — every full-size QR side length is size mod 4 == 1
    public void PlaceData_firstColumnPair_alwaysScansUpwardFromTheBottomRightCorner(int size)
    {
        // ISO/IEC 18004 §7.7.3: the zig-zag scan starts at the bottom-right corner of the symbol
        // and moves upward for the first (rightmost) column pair, then alternates direction for
        // each subsequent pair. This must hold regardless of the symbol's side length: deriving
        // "upward" from the column index instead of the pair's position in the scan sequence only
        // reproduces it when size mod 4 == 1, which happens to be every full-size QR side length
        // but only two of Micro QR's four.
        var matrix = new BarcodeMatrix(size, size);
        var isFunction = new bool[size, size];
        byte[] codewords = [0b1000_0000]; // a single set bit: must land in the scan's very first cell

        QrMatrixBuilder.PlaceData(matrix, isFunction, size, codewords, skipColumn: null);

        Assert.True(matrix.IsDark(size - 1, size - 1)); // bit 0: bottom-right corner
        Assert.False(matrix.IsDark(size - 2, size - 1)); // bit 1: next cell in scan order, unset
    }

    [Fact]
    public void PlaceData_secondColumnPair_scansDownwardRegardlessOfSize()
    {
        // Complements the test above: the *second* pair must alternate to downward. Uses a size
        // (11) where the old column-index shortcut got the *first* pair wrong in a way that would
        // have made the second pair look right by coincidence, to make sure the fix does not just
        // shift the bug by one pair.
        const int size = 11;
        var matrix = new BarcodeMatrix(size, size);
        var isFunction = new bool[size, size];

        // Column pair 0 is (10, 9): 2 * size bits fills it completely, then pair 1 (8, 7) starts.
        var codewords = new byte[(2 * size / 8) + 1];
        var totalBits = 2 * size;
        codewords[totalBits / 8] = (byte)(0b1000_0000 >> (totalBits % 8)); // first bit of pair 1

        QrMatrixBuilder.PlaceData(matrix, isFunction, size, codewords, skipColumn: null);

        Assert.True(matrix.IsDark(8, 0)); // pair 1, bit 0: downward means starting at the top row
        Assert.False(matrix.IsDark(7, 0));
    }

    [Fact]
    public void PlaceData_halfWidthCodeword_usesTheHighNibbleNotTheLowNibble()
    {
        // QrBitStreamBuilder.Finish leaves the Micro QR M1/M3 half-width final data codeword
        // byte-aligned: the 4 real bits in the high nibble, zero padding in the low nibble
        // (because that is the byte value Reed-Solomon computed error correction against).
        // Placement must read the same 4 bits back out of the same (high) position.
        const int size = 11;
        var matrix = new BarcodeMatrix(size, size);
        var isFunction = new bool[size, size];
        byte[] codewords = [0b1101_0000]; // high nibble 1101 (the payload), low nibble zero padding

        QrMatrixBuilder.PlaceData(matrix, isFunction, size, codewords, skipColumn: null, halfWidthCodewordIndex: 0);

        // First column pair scans upward from the bottom-right corner (bit 0 at (10,10), bit 1 at
        // (9,10), bit 2 at (10,9), bit 3 at (9,9)); the high nibble's bits, MSB first, are 1,1,0,1.
        Assert.True(matrix.IsDark(size - 1, size - 1));  // bit 0 = 1
        Assert.True(matrix.IsDark(size - 2, size - 1));  // bit 1 = 1
        Assert.False(matrix.IsDark(size - 1, size - 2)); // bit 2 = 0
        Assert.True(matrix.IsDark(size - 2, size - 2));  // bit 3 = 1
    }
}
