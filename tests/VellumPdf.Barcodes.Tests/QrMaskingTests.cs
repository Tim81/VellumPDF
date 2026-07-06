// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Qr;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for <see cref="QrMasking"/>'s eight mask conditions and Table 11 penalty rules, on small crafted matrices.</summary>
public sealed class QrMaskingTests
{
    [Theory]
    [InlineData(0, 0, 0, true)]
    [InlineData(0, 0, 1, false)]
    [InlineData(1, 1, 0, false)]
    [InlineData(1, 2, 5, true)]
    [InlineData(2, 0, 3, true)]
    [InlineData(2, 0, 4, false)]
    [InlineData(3, 1, 2, true)]
    [InlineData(4, 2, 3, true)] // (2/2 + 3/3) = 1+1 = 2, even
    [InlineData(4, 0, 0, true)]
    public void Condition_matchesTable10Formulas(int maskId, int row, int column, bool expected) =>
        Assert.Equal(expected, QrMasking.Condition(maskId, row, column));

    [Fact]
    public void ApplyMask_appliedTwice_restoresOriginalModules()
    {
        var matrix = new BarcodeMatrix(9, 9);
        var isFunction = new bool[9, 9];
        matrix.SetDark(3, 3, true);
        matrix.SetDark(5, 1, true);

        var before = Snapshot(matrix, 9);
        QrMasking.ApplyMask(matrix, isFunction, 9, 3);
        QrMasking.ApplyMask(matrix, isFunction, 9, 3);
        Assert.Equal(before, Snapshot(matrix, 9));
    }

    [Fact]
    public void ApplyMask_skipsFunctionModules()
    {
        var matrix = new BarcodeMatrix(5, 5);
        var isFunction = new bool[5, 5];
        isFunction[0, 0] = true; // row 0, column 0: mask 0 condition (0+0)%2==0 is true, but it's a function module

        QrMasking.ApplyMask(matrix, isFunction, 5, 0);
        Assert.False(matrix.IsDark(0, 0));
    }

    [Fact]
    public void RunPenalty_fiveInARow_scoresThree()
    {
        var matrix = new BarcodeMatrix(9, 1);
        for (var x = 0; x < 5; x++) matrix.SetDark(x, 0, true);
        Assert.Equal(3, QrMasking.RunPenalty(matrix));
    }

    [Fact]
    public void RunPenalty_sevenInARow_scoresFive()
    {
        var matrix = new BarcodeMatrix(9, 1);
        for (var x = 0; x < 7; x++) matrix.SetDark(x, 0, true);
        Assert.Equal(5, QrMasking.RunPenalty(matrix)); // 3 + (7-5) = 5, not 3+4+5=12 (ISO Note 1)
    }

    [Fact]
    public void RunPenalty_fourInARow_scoresZero()
    {
        // Exactly 4 wide so there is no trailing light run long enough to add its own penalty.
        var matrix = new BarcodeMatrix(4, 1);
        for (var x = 0; x < 4; x++) matrix.SetDark(x, 0, true);
        Assert.Equal(0, QrMasking.RunPenalty(matrix));
    }

    [Fact]
    public void BlockPenalty_twoByTwoDarkBlock_scoresThree()
    {
        var matrix = new BarcodeMatrix(4, 4);
        matrix.SetDark(1, 1, true);
        matrix.SetDark(2, 1, true);
        matrix.SetDark(1, 2, true);
        matrix.SetDark(2, 2, true);
        Assert.Equal(3, QrMasking.BlockPenalty(matrix));
    }

    [Fact]
    public void BlockPenalty_threeByThreeDarkBlock_scoresTwelve()
    {
        // ISO/IEC 18004 Table 11 Note 2's own worked example: a 3x3 dark block contains four
        // overlapping 2x2 blocks, penalised as 4 x 3 = 12 points. Sized exactly 3x3 (rather than
        // set within a larger matrix) so no surrounding light area contributes its own blocks.
        var matrix = new BarcodeMatrix(3, 3);
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 3; x++)
                matrix.SetDark(x, y, true);
        Assert.Equal(12, QrMasking.BlockPenalty(matrix));
    }

    [Fact]
    public void FinderLikePenalty_darkLightDarkDarkDarkLightDarkFlankedByFourLight_scoresForty()
    {
        // dark,light,dark,dark,dark,light,dark then four light modules.
        var matrix = new BarcodeMatrix(11, 1);
        bool[] pattern = [true, false, true, true, true, false, true, false, false, false, false];
        for (var x = 0; x < pattern.Length; x++) matrix.SetDark(x, 0, pattern[x]);
        Assert.Equal(40, QrMasking.FinderLikePenalty(matrix));
    }

    [Fact]
    public void FinderLikePenalty_fourLightModulesThenTheCorePattern_scoresForty()
    {
        var matrix = new BarcodeMatrix(11, 1);
        bool[] pattern = [false, false, false, false, true, false, true, true, true, false, true];
        for (var x = 0; x < pattern.Length; x++) matrix.SetDark(x, 0, pattern[x]);
        Assert.Equal(40, QrMasking.FinderLikePenalty(matrix));
    }

    [Fact]
    public void FinderLikePenalty_noPattern_scoresZero()
    {
        var matrix = new BarcodeMatrix(11, 1);
        Assert.Equal(0, QrMasking.FinderLikePenalty(matrix));
    }

    [Fact]
    public void DarkRatioPenalty_exactlyHalfDark_scoresZero()
    {
        var matrix = new BarcodeMatrix(10, 10);
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 5; x++)
                matrix.SetDark(x, y, true);
        Assert.Equal(0, QrMasking.DarkRatioPenalty(matrix));
    }

    [Fact]
    public void DarkRatioPenalty_allDark_scoresOneHundred()
    {
        var matrix = new BarcodeMatrix(10, 10);
        for (var y = 0; y < 10; y++)
            for (var x = 0; x < 10; x++)
                matrix.SetDark(x, y, true);
        // 100% dark deviates 50 points from the 50% reference: k = 50/5 = 10 steps, so 10*10 = 100.
        Assert.Equal(100, QrMasking.DarkRatioPenalty(matrix));
    }

    private static bool[] Snapshot(BarcodeMatrix matrix, int size)
    {
        var result = new bool[size * size];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                result[(y * size) + x] = matrix.IsDark(x, y);
        return result;
    }
}
