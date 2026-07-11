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
        var (_, dataSymbols, _) = Code128Encoder.EncodeSymbols(new Code128Barcode("AB"));
        Assert.Contains(102, dataSymbols);
    }

    [Fact]
    public void Validate_acceptsLatin1Content()
    {
        // #155: 128-255 is now valid, carried with FNC4; this used to throw.
        var barcode = new Code128Barcode("café");
        Assert.Equal("café", barcode.Content);
    }

    [Fact]
    public void Validate_rejectsCharactersAboveLatin1()
    {
        Assert.Throws<ArgumentException>(() => new Code128Barcode("cafĀ")); // U+0100 is 256, one past Latin-1
    }

    [Fact]
    public void Gs1_rejectsLatin1Content()
    {
        // The GS1 General Specifications reserve FNC4 for plain Code 128; a GS1-128 symbol
        // cannot carry it, so extended Latin-1 content throws rather than silently dropping FNC4.
        Assert.Throws<ArgumentException>(() =>
            Code128Encoder.EncodeSymbols(new Code128Barcode("café") { Gs1 = true }));
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
    public void Fnc4_singleHighChar_isALoneShift()
    {
        // e-acute (U+00E9) has low equivalent 'i' (0x69), a Code Set B character, so home mode
        // is B and Start B is chosen; a single Latin-1 character shifts rather than latching.
        var (startValue, dataSymbols, check) = Code128Encoder.EncodeSymbols(new Code128Barcode("\u00E9"));

        Assert.Equal(104, startValue); // Start Code B
        Assert.Equal([100, 73], dataSymbols); // FNC4 in B (100), then 'i' mapped in B (0x69 - 32)
        Assert.Equal(41, check);
    }

    [Fact]
    public void Fnc4_runOfTwoHighChars_latches()
    {
        // u-umlaut (0xFC, low '|' 0x7C) then sharp s (0xDF, low '_' 0x5F): two consecutive
        // Latin-1 characters latch FNC4 with a doubled code instead of shifting each one apart.
        var (startValue, dataSymbols, check) = Code128Encoder.EncodeSymbols(new Code128Barcode("\u00FC\u00DF"));

        Assert.Equal(104, startValue); // Start Code B
        Assert.Equal([100, 100, 92, 63], dataSymbols); // doubled FNC4 (latch on), then '|', '_' mapped in B
        Assert.Equal(5, check);
    }

    [Fact]
    public void Fnc4_lowHighLowMix_shiftsOnlyTheMiddleCharacter()
    {
        // A (low), e-acute (high, low 'i' still fits Code B), B (low): no A/B switching at all,
        // just a single FNC4 shift around the middle character.
        var (startValue, dataSymbols, check) = Code128Encoder.EncodeSymbols(new Code128Barcode("A\u00E9B"));

        Assert.Equal(104, startValue); // Start Code B: e-acute's low equivalent forces B
        Assert.Equal([33, 100, 73, 34], dataSymbols); // 'A', FNC4, 'i', 'B', all mapped/emitted in B
        Assert.Equal(74, check);
    }

    [Fact]
    public void Fnc4_controlRangeHighChar_needsCodeASwitch_notAShift()
    {
        // 'a' forces home mode B; U+0081 (low equivalent 0x01, a control character) only exists
        // in Code Set A, so unlike the AeB case above this needs an actual subset switch. A cheap
        // Shift cannot carry FNC4 too, since FNC4 has no data-value slot of its own for Shift to
        // read through Code A's table: it reuses the "switch to A"/"switch to B" function codes,
        // which sit outside the data range Shift's single-symbol scope covers. Confirmed against
        // zxing-cpp: emitting FNC4 as if it were Shift's target symbol decoded the control
        // character with no Latin-1 bit set. A real switch to Code A sidesteps this.
        var (startValue, dataSymbols, check) = Code128Encoder.EncodeSymbols(new Code128Barcode("a\u0081"));

        Assert.Equal(104, startValue); // Start Code B: 'a' forces it
        Assert.Equal([65, 101, 101, 65], dataSymbols); // 'a' in B, switch to A, FNC4 in A, control char in A
        Assert.Equal(7, check);
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
