// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.DataMatrix;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for the Data Matrix high-level encoder, symbol-size selection, and end-to-end orchestration.</summary>
public sealed class DataMatrixEncoderTests
{
    [Fact]
    public void EncodeText_wikipediaExample_matchesPublishedDataCodewords()
    {
        // The worked example from Wikipedia's "Data Matrix" article and the ISO/IEC 16022 spec's
        // own illustration: "Wikipedia" in a 16x16 symbol (12 data codewords, 12 EC codewords).
        var content = DataMatrixHighLevelEncoder.EncodeText("Wikipedia", gs1: false);

        int[] expectedContent = [88, 106, 108, 106, 113, 102, 101, 106, 98];
        Assert.Equal(expectedContent, content);

        var size = DataMatrixSymbolSizes.Resolve(content.Count, DataMatrixShape.Automatic);
        Assert.Equal(16, size.SymbolRows);
        Assert.Equal(16, size.SymbolColumns);
        Assert.Equal(12, size.DataCodewords);
        Assert.Equal(12, size.ErrorCodewords);

        var dataCodewords = new int[size.DataCodewords];
        content.CopyTo(dataCodewords);
        // Pad codewords: 129, then the 253-state algorithm.
        dataCodewords[9] = 129;
        for (var i = 10; i < 12; i++)
        {
            var position = i + 1;
            var r = ((149 * position) % 253) + 1;
            dataCodewords[i] = (129 + r) % 254;
        }

        int[] expectedPadded = [88, 106, 108, 106, 113, 102, 101, 106, 98, 129, 251, 147];
        Assert.Equal(expectedPadded, dataCodewords);

        var rs = new ReedSolomonBinary(GaloisField.Gf256, firstRoot: 1);
        var ec = rs.ComputeRemainder(dataCodewords, size.ErrorCodewords);
        int[] expectedEc = [104, 216, 88, 39, 233, 202, 71, 217, 26, 92, 25, 232];
        Assert.Equal(expectedEc, ec);
    }

    [Fact]
    public void EncodeText_asciiDigitPair_compactsTwoDigitsPerCodeword()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("12", gs1: false);
        Assert.Equal([130 + 12], content);
    }

    [Fact]
    public void EncodeText_oddDigitRun_lastDigitEncodedAlone()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("123", gs1: false);
        Assert.Equal([130 + 12, '3' + 1], content);
    }

    [Fact]
    public void EncodeText_shortUppercaseRun_staysAscii()
    {
        // Below the C40 compaction threshold: cheaper to leave as plain ASCII.
        var content = DataMatrixHighLevelEncoder.EncodeText("AB", gs1: false);
        Assert.Equal(['A' + 1, 'B' + 1], content);
    }

    [Fact]
    public void EncodeText_longUppercaseRun_selectsC40()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("ABCDEFGHI", gs1: false);
        Assert.Equal(230, content[0]); // Latch to C40
        Assert.Equal(254, content[^1]); // Unlatch
    }

    [Fact]
    public void EncodeText_c40NineUpper_matchesHandDerivedCodewords()
    {
        // 9 letters, so the value count is an exact multiple of 3 (remainder 0): all three
        // triples pack cleanly, then a plain unlatch. See
        // EncodeText_c40TenUpper_oneLeftoverValueUnlatchesThenAsciiEncodesIt below for the
        // remainder-1 case this contrasts with.
        var content = DataMatrixHighLevelEncoder.EncodeText("ABCDEFGHI", gs1: false);
        Assert.Equal([230, 89, 233, 109, 36, 128, 95, 254], content);
    }

    [Fact]
    public void EncodeText_c40TenUpper_oneLeftoverValueUnlatchesThenAsciiEncodesIt()
    {
        // The FIX-1 case: 10 letters, so the value count is one more than a multiple of 3
        // (remainder 1). Before the fix, this padded with two Shift1 zeros, which decoded as an
        // extra character plus a spurious trailing NUL. The fix packs only the 3 complete
        // triples, unlatches, then ASCII-encodes the run's last byte ('J' = 74, codeword 75)
        // directly.
        var content = DataMatrixHighLevelEncoder.EncodeText("ABCDEFGHIJ", gs1: false);
        Assert.Equal([230, 89, 233, 109, 36, 128, 95, 254, 75], content);
    }

    [Fact]
    public void EncodeText_longLowercaseRun_selectsText()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("abcdefghi", gs1: false);
        Assert.Equal(239, content[0]); // Latch to Text
        Assert.Equal(254, content[^1]); // Unlatch
    }

    [Fact]
    public void EncodeText_textNineLower_matchesHandDerivedCodewords()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("abcdefghi", gs1: false);
        Assert.Equal([239, 89, 233, 109, 36, 128, 95, 254], content);
    }

    [Fact]
    public void EncodeBytes_selectsBase256()
    {
        byte[] data = [0x01, 0x02, 0x03];
        var content = DataMatrixHighLevelEncoder.EncodeBytes(data, gs1: false);
        Assert.Equal(231, content[0]); // Latch to Base 256
        Assert.Equal(1 + 1 + data.Length, content.Count); // latch + length field + 3 data bytes
    }

    [Fact]
    public void EncodeBytes_base256_matchesHandDerivedCodewords()
    {
        byte[] data = [0x01, 0x02, 0x03];
        var content = DataMatrixHighLevelEncoder.EncodeBytes(data, gs1: false);
        Assert.Equal([231, 47, 194, 89, 239], content);
    }

    [Fact]
    public void EncodeText_upperShiftHighByte_matchesHandDerivedCodewords()
    {
        // U+00E9 ('é') is byte 233 in Latin-1: Upper Shift codeword 235, then 233 - 127 = 106.
        var content = DataMatrixHighLevelEncoder.EncodeText("é", gs1: false);
        Assert.Equal([235, 106], content);
    }

    [Fact]
    public void EncodeText_gs1AsciiModeSeparator_matchesHandDerivedCodewords()
    {
        // A short run stays ASCII (well under the C40/Text compaction threshold): "99" compacts
        // to one digit-pair codeword, 'A' is plain ASCII, the embedded GS becomes FNC1 (232)
        // rather than its literal control-code value, and 'B' is plain ASCII.
        var content = DataMatrixHighLevelEncoder.EncodeText("99A" + (char)0x1D + "B", gs1: true);
        Assert.Equal([232, 229, 66, 232, 67], content);
    }

    [Fact]
    public void EncodeText_longHighByteRun_selectsBase256()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("éèê", gs1: false);
        Assert.Equal(231, content[0]); // Latch to Base 256
    }

    [Fact]
    public void EncodeText_gs1_prependsFnc1Codeword()
    {
        var content = DataMatrixHighLevelEncoder.EncodeText("01", gs1: true);
        Assert.Equal(232, content[0]);
    }

    [Fact]
    public void EncodeBytes_gs1_prependsFnc1Codeword()
    {
        var content = DataMatrixHighLevelEncoder.EncodeBytes([0x01], gs1: true);
        Assert.Equal(232, content[0]);
    }

    [Theory]
    [InlineData(DataMatrixShape.Automatic)]
    [InlineData(DataMatrixShape.Square)]
    public void Resolve_smallContent_picksSmallestSquare(DataMatrixShape shape)
    {
        var size = DataMatrixSymbolSizes.Resolve(3, shape);
        Assert.Equal(10, size.SymbolRows);
        Assert.Equal(10, size.SymbolColumns);
    }

    [Fact]
    public void Resolve_rectangularShape_picksSmallestRectangle()
    {
        var size = DataMatrixSymbolSizes.Resolve(3, DataMatrixShape.Rectangular);
        Assert.Equal(8, size.SymbolRows);
        Assert.Equal(18, size.SymbolColumns);
    }

    [Fact]
    public void Resolve_exceedsLargestSize_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => DataMatrixSymbolSizes.Resolve(int.MaxValue, DataMatrixShape.Automatic));
    }

    [Fact]
    public void GetMatrix_wikipediaExample_producesA16x16Symbol()
    {
        var barcode = new DataMatrixBarcode("Wikipedia");
        var matrix = barcode.GetMatrix();
        Assert.Equal(16, matrix.Width);
        Assert.Equal(16, matrix.Height);
    }

    [Fact]
    public void GetMatrix_isDeterministic()
    {
        var a = new DataMatrixBarcode("VellumPdf Data Matrix").GetMatrix();
        var b = new DataMatrixBarcode("VellumPdf Data Matrix").GetMatrix();
        for (var y = 0; y < a.Height; y++)
            for (var x = 0; x < a.Width; x++)
                Assert.Equal(a.IsDark(x, y), b.IsDark(x, y));
    }

    [Fact]
    public void GetMatrix_rectangularShape_producesARectangularSymbol()
    {
        var barcode = new DataMatrixBarcode("12345") { Shape = DataMatrixShape.Rectangular };
        var matrix = barcode.GetMatrix();
        Assert.NotEqual(matrix.Width, matrix.Height);
    }

    [Fact]
    public void Constructor_nullText_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DataMatrixBarcode((string)null!));
    }

    [Fact]
    public void Constructor_nullBytes_throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DataMatrixBarcode((byte[])null!));
    }
}
