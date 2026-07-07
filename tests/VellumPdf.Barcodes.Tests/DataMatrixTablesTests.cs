// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.DataMatrix;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for the C40/Text value tables (ISO/IEC 16022:2024 Table 2).</summary>
public sealed class DataMatrixTablesTests
{
    private static List<int> ValuesFor(byte b, bool isC40)
    {
        var values = new List<int>();
        DataMatrixTables.AppendValues(values, b, isC40);
        return values;
    }

    [Theory]
    [InlineData(' ', 3)]
    [InlineData('0', 4)]
    [InlineData('9', 13)]
    [InlineData('A', 14)]
    [InlineData('Z', 39)]
    public void AppendValues_c40BasicSetChars_singleValue(char c, int expected)
    {
        Assert.Equal([expected], ValuesFor((byte)c, isC40: true));
    }

    [Theory]
    [InlineData(' ', 3)]
    [InlineData('0', 4)]
    [InlineData('9', 13)]
    [InlineData('a', 14)]
    [InlineData('z', 39)]
    public void AppendValues_textBasicSetChars_singleValue(char c, int expected)
    {
        Assert.Equal([expected], ValuesFor((byte)c, isC40: false));
    }

    [Fact]
    public void AppendValues_c40LowercaseLetter_usesShift3()
    {
        // 'a' (97) is Shift-3 value 1 in C40 (basic set is upper-case there).
        Assert.Equal([DataMatrixTables.Shift3, 1], ValuesFor((byte)'a', isC40: true));
    }

    [Fact]
    public void AppendValues_textUppercaseLetter_usesShift3()
    {
        // 'A' (65) is Shift-3 value 1 in Text (basic set is lower-case there).
        Assert.Equal([DataMatrixTables.Shift3, 1], ValuesFor((byte)'A', isC40: false));
    }

    [Fact]
    public void AppendValues_controlChar_usesShift1WithDirectValue()
    {
        Assert.Equal([DataMatrixTables.Shift1, 5], ValuesFor(5, isC40: true));
    }

    [Fact]
    public void AppendValues_exclamationMark_usesShift2Value0()
    {
        Assert.Equal([DataMatrixTables.Shift2, 0], ValuesFor((byte)'!', isC40: true));
    }

    [Fact]
    public void AppendValues_underscore_usesShift2Value26()
    {
        Assert.Equal([DataMatrixTables.Shift2, 26], ValuesFor((byte)'_', isC40: true));
    }

    [Fact]
    public void AppendValues_backtick_usesShift3Value0_forBothModes()
    {
        Assert.Equal([DataMatrixTables.Shift3, 0], ValuesFor((byte)'`', isC40: true));
        Assert.Equal([DataMatrixTables.Shift3, 0], ValuesFor((byte)'`', isC40: false));
    }

    [Fact]
    public void AppendValues_del_usesShift3Value31()
    {
        Assert.Equal([DataMatrixTables.Shift3, 31], ValuesFor(127, isC40: true));
    }

    [Fact]
    public void AppendValues_groupSeparator_becomesFnc1InShift2()
    {
        Assert.Equal([DataMatrixTables.Shift2, DataMatrixTables.Fnc1InShift2], ValuesFor(0x1D, isC40: true));
    }

    [Fact]
    public void AppendValues_highByte_usesUpperShiftThenRecurses()
    {
        // 203 - 128 = 75 = 'K', basic C40 value 24 (ISO/IEC 16022's own worked example).
        Assert.Equal([DataMatrixTables.Shift2, DataMatrixTables.UpperShiftInShift2, 24], ValuesFor(203, isC40: true));
    }

    [Fact]
    public void AppendValues_highByteNeedingShift3_recursesThroughUpperShift()
    {
        // 235 - 128 = 107 = 'k', C40 shift-3 value 11 (ISO/IEC 16022's own worked example).
        Assert.Equal([DataMatrixTables.Shift2, DataMatrixTables.UpperShiftInShift2, DataMatrixTables.Shift3, 11], ValuesFor(235, isC40: true));
    }

    [Fact]
    public void AppendValues_everyByte0To127_isRepresentableInBothModes()
    {
        for (var b = 0; b <= 127; b++)
        {
            var c40 = ValuesFor((byte)b, isC40: true);
            var text = ValuesFor((byte)b, isC40: false);
            Assert.True(c40.Count is 1 or 2, $"byte {b} produced {c40.Count} C40 values.");
            Assert.True(text.Count is 1 or 2, $"byte {b} produced {text.Count} Text values.");
        }
    }
}
