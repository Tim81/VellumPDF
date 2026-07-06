// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Code128;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="Code128Encoder"/> against Wikipedia's Code 128 worked examples: the
/// "PJJ123C" check-character calculation and the "098x1234567y23" optimal subset-switching
/// example (both under Code_128 § Check digit calculation / Barcode length optimization).
/// </summary>
public sealed class Code128EncoderTests
{
    [Fact]
    public void CheckCharacter_forPJJ123C_is54()
    {
        var (startValue, dataSymbols, check) = Code128Encoder.EncodeSymbols(new Code128Barcode("PJJ123C"));

        Assert.Equal(103, startValue); // Start Code A: no lowercase/control char forces B
        Assert.Equal(54, check);

        // P J J 1 2 3 C, all encoded in Code Set A (values = char - 32 for the 32-95 range).
        Assert.Equal([48, 42, 42, 17, 18, 19, 35], dataSymbols);
    }

    [Fact]
    public void Encode_123456_isStartCPlusThreePairs()
    {
        var (startValue, dataSymbols, _) = Code128Encoder.EncodeSymbols(new Code128Barcode("123456"));

        Assert.Equal(105, startValue); // Start Code C
        Assert.Equal([12, 34, 56], dataSymbols);
    }

    [Fact]
    public void Encode_098x1234567y23_matchesWikipediaOptimalSequence()
    {
        // [Start B] 0 9 8 x [Code C] 12 34 56 [Code B] 7 y 2 3 [check] [Stop] = 16 symbols total.
        var (startValue, dataSymbols, _) = Code128Encoder.EncodeSymbols(new Code128Barcode("098x1234567y23"));

        Assert.Equal(104, startValue); // Start Code B: 'x' is lowercase, forcing B
        int[] expected =
        [
            16, 25, 24, 88, // 0, 9, 8, x (Code Set B values: char - 32)
            99,             // Code C
            12, 34, 56,
            100,            // Code B
            23, 89, 18, 19, // 7, y, 2, 3
        ];
        Assert.Equal(expected, dataSymbols);

        // start + data + check + stop = 1 + 12 + 1 + 1 = 16 symbols.
        Assert.Equal(16, 1 + dataSymbols.Count + 1 + 1);
    }

    [Fact]
    public void Gs1_emitsFnc1_immediatelyAfterStart()
    {
        var (_, dataSymbols, _) = Code128Encoder.EncodeSymbols(new Code128Barcode("4218402050") { Gs1 = true });
        Assert.Equal(102, dataSymbols[0]);
    }

    [Fact]
    public void GroupSeparatorCharacter_becomesFnc1()
    {
        var (_, dataSymbols, _) = Code128Encoder.EncodeSymbols(new Code128Barcode("A\u001DB"));
        Assert.Contains(102, dataSymbols);
    }

    [Fact]
    public void Validate_rejectsCharactersAboveAscii()
    {
        Assert.Throws<ArgumentException>(() => new Code128Barcode("café"));
    }

    [Fact]
    public void Gs1_hriLabel_isTheParenthesizedApplicationIdentifierForm()
    {
        // #155: the GS1-128 caption is the parenthesized-AI human-readable form, not the raw
        // concatenated digits. (01) is predefined-length, so its 14-digit GTIN follows directly.
        var encoded = Code128Encoder.Encode(new Code128Barcode("0100012345678905") { Gs1 = true });

        var group = Assert.Single(encoded.HriGroups);
        Assert.Equal("(01)00012345678905", group.Text);
    }

    [Fact]
    public void Gs1_hriLabel_showsSeparatorsBetweenVariableLengthElements()
    {
        // (01) GTIN then (10) batch/lot then (17) expiry: the parenthesized form makes the AI
        // boundaries explicit even though the encoded content carries an FNC1 after the batch.
        var content = "010001234567890510LOT99" + (char)0x1D + "17261231";
        var encoded = Code128Encoder.Encode(new Code128Barcode(content) { Gs1 = true });

        var group = Assert.Single(encoded.HriGroups);
        Assert.Equal("(01)00012345678905(10)LOT99(17)261231", group.Text);
    }

    [Fact]
    public void NonGs1_hriLabel_isTheContentVerbatim()
    {
        var encoded = Code128Encoder.Encode(new Code128Barcode("CODE128-GOLDEN"));
        var group = Assert.Single(encoded.HriGroups);
        Assert.Equal("CODE128-GOLDEN", group.Text);
    }

    [Fact]
    public void Gs1_hriLabel_fallsBackToStrippedContent_whenNotAWellFormedElementString()
    {
        // Content flagged GS1 but not a valid element string still encodes into bars; the caption
        // falls back to the content with FNC1 separators removed rather than throwing.
        var encoded = Code128Encoder.Encode(new Code128Barcode("A" + (char)0x1D + "B") { Gs1 = true });
        var group = Assert.Single(encoded.HriGroups);
        Assert.Equal("AB", group.Text);
    }

    [Fact]
    public void Encode_isDeterministic()
    {
        var a = Code128Encoder.EncodeSymbols(new Code128Barcode("Hello123"));
        var b = Code128Encoder.EncodeSymbols(new Code128Barcode("Hello123"));
        Assert.Equal(a.DataSymbols, b.DataSymbols);
        Assert.Equal(a.StartValue, b.StartValue);
        Assert.Equal(a.Check, b.Check);
    }

    [Fact]
    public void CheckCharacter_survivesVeryLongContent()
    {
        // The weighted check sum is reduced modulo 103 every step; before that it accumulated in
        // an int and overflowed to a negative symbol value at around forty thousand digits.
        var (_, _, check) = Code128Encoder.EncodeSymbols(new Code128Barcode(new string('1', 40_000)));
        Assert.InRange(check, 0, 102);
    }
}
