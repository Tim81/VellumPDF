// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Tests for <see cref="Gs1ElementString"/>: separator placement follows the GS1 predefined-length
/// rule, both input conventions normalize to the same result, and malformed input is rejected.
/// </summary>
public sealed class Gs1ElementStringTests
{
    private const string Gs = "";

    [Fact]
    public void Parse_parenthesized_producesMatchingHriAndSeparatorFreePayload_forFixedLengthAis()
    {
        // (01) GTIN and (17) expiry are both predefined-length, so no separator sits between them
        // or after the final element.
        var result = Gs1ElementString.Parse("(01)09501101020917(17)261231");

        Assert.Equal("(01)09501101020917(17)261231", result.Hri);
        Assert.Equal("0109501101020917" + "17261231", result.EncoderPayload);
        Assert.False(result.EncoderPayload.Contains(Gs, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_variableLengthAi_followedByAnotherAi_insertsSeparator()
    {
        // (10) batch/lot is variable-length; a separator must precede the following (21) serial.
        var result = Gs1ElementString.Parse("(10)ABC123(21)SER456");

        Assert.Equal("10ABC123" + Gs + "21SER456", result.EncoderPayload);
        Assert.Equal("(10)ABC123(21)SER456", result.Hri);
    }

    [Fact]
    public void Parse_finalVariableLengthAi_getsNoTrailingSeparator()
    {
        var result = Gs1ElementString.Parse("(10)ABC123");

        Assert.Equal("10ABC123", result.EncoderPayload);
        Assert.EndsWith("3", result.EncoderPayload);
        Assert.False(result.EncoderPayload.Contains(Gs, StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_fixedThenVariableThenFixed_placesSeparatorOnlyAfterTheVariableValue()
    {
        // (01) fixed, (10) variable, (17) fixed → separator only after the (10) value.
        var result = Gs1ElementString.Parse("(01)09501101020917(10)LOT99(17)261231");

        Assert.Equal("0109501101020917" + "10LOT99" + Gs + "17261231", result.EncoderPayload);
        Assert.Equal("(01)09501101020917(10)LOT99(17)261231", result.Hri);
    }

    [Fact]
    public void Parse_bothInputForms_produceIdenticalOutput()
    {
        var fromParens = Gs1ElementString.Parse("(01)09501101020917(10)LOT99(17)261231");
        var fromRaw = Gs1ElementString.Parse("0109501101020917" + "10LOT99" + Gs + "17261231");

        Assert.Equal(fromParens.EncoderPayload, fromRaw.EncoderPayload);
        Assert.Equal(fromParens.Hri, fromRaw.Hri);
    }

    [Fact]
    public void Parse_rawFixedLengthOnly_needsNoSeparators()
    {
        var fromRaw = Gs1ElementString.Parse("0109501101020917" + "17261231");
        var fromParens = Gs1ElementString.Parse("(01)09501101020917(17)261231");

        Assert.Equal(fromParens.EncoderPayload, fromRaw.EncoderPayload);
        Assert.Equal(fromParens.Hri, fromRaw.Hri);
    }

    [Fact]
    public void Parse_threeDigitGlnReference_isFixedThirteenDigits()
    {
        // AI 410 (ship-to GLN) is a predefined 13-digit value: no separator before the next AI.
        var result = Gs1ElementString.Parse("(410)9501234567895(10)LOT1");

        Assert.Equal("4109501234567895" + "10LOT1", result.EncoderPayload);
        Assert.Equal("(410)9501234567895(10)LOT1", result.Hri);
    }

    [Fact]
    public void Parse_fourDigitWeightFamily_isFixedSixDigits()
    {
        // AI 3103 (net weight, kg, three decimals) is a predefined 6-digit value.
        var result = Gs1ElementString.Parse("(3103)000123(10)LOT1");

        Assert.Equal("3103000123" + "10LOT1", result.EncoderPayload);
        Assert.Equal("(3103)000123(10)LOT1", result.Hri);
    }

    [Fact]
    public void Parse_roundTrips_payloadBackToSameHri()
    {
        var first = Gs1ElementString.Parse("(01)09501101020917(10)LOT99(17)261231");
        var second = Gs1ElementString.Parse(first.EncoderPayload);
        Assert.Equal(first.Hri, second.Hri);
        Assert.Equal(first.EncoderPayload, second.EncoderPayload);
    }

    [Fact]
    public void Parse_elements_areInEncounterOrder()
    {
        var result = Gs1ElementString.Parse("(01)09501101020917(10)LOT99");
        Assert.Collection(
            result.Elements,
            e => { Assert.Equal("01", e.Ai); Assert.Equal("09501101020917", e.Value); },
            e => { Assert.Equal("10", e.Ai); Assert.Equal("LOT99", e.Value); });
    }

    [Theory]
    [InlineData("")]
    public void Parse_empty_throwsArgumentException(string input)
    {
        Assert.Throws<ArgumentException>(() => Gs1ElementString.Parse(input));
    }

    [Fact]
    public void Parse_null_throwsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => Gs1ElementString.Parse(null!));
    }

    [Fact]
    public void Parse_leadingSeparator_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse(Gs + "10ABC"));
    }

    [Fact]
    public void Parse_truncatedFixedLengthValue_throwsFormatException()
    {
        // AI 01 needs 14 digits; only 5 are present.
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("0112345"));
    }

    [Fact]
    public void Parse_parenthesizedFixedLengthWrongSize_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("(01)12345"));
    }

    [Fact]
    public void Parse_emptyAi_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("()12345"));
    }

    [Fact]
    public void Parse_emptyValue_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("(10)(21)ABC"));
    }

    [Fact]
    public void Parse_unterminatedParenthesis_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("(01"));
    }

    [Fact]
    public void Parse_nonNumericAi_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("(A1)12345"));
    }

    [Fact]
    public void Parse_controlCharacterInValue_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("(10)ABC"));
    }

    [Theory]
    [InlineData("240ABC123", "240", "ABC123")] // ADDITIONAL ID, variable
    [InlineData("400ORDER1", "400", "ORDER1")] // ORDER NUMBER, variable
    [InlineData("420STOP99", "420", "STOP99")] // SHIP TO POST, variable
    public void Parse_rawThreeDigitVariableAi_splitsAiAndValueCorrectly(string raw, string expectedAi, string expectedValue)
    {
        var result = Gs1ElementString.Parse(raw);

        Assert.Single(result.Elements);
        Assert.Equal(expectedAi, result.Elements[0].Ai);
        Assert.Equal(expectedValue, result.Elements[0].Value);
        Assert.Equal($"({expectedAi}){expectedValue}", result.Hri);
    }

    [Fact]
    public void Parse_rawFourDigitVariableAi_splitsAiAndValueCorrectly()
    {
        // Before the AI-length fix, "8013ABC123" misparsed as AI "80" + value "13ABC123".
        var result = Gs1ElementString.Parse("8013ABC123");

        Assert.Single(result.Elements);
        Assert.Equal("8013", result.Elements[0].Ai);
        Assert.Equal("ABC123", result.Elements[0].Value);
        Assert.Equal("(8013)ABC123", result.Hri);
    }

    [Theory]
    [InlineData("240ABC123", "(240)ABC123")]
    [InlineData("400ORDER1", "(400)ORDER1")]
    [InlineData("420STOP99", "(420)STOP99")]
    [InlineData("8013ABC123", "(8013)ABC123")]
    public void Parse_rawAndParenthesizedForms_ofVariableThreeOrFourDigitAi_normalizeIdentically(string raw, string parenthesized)
    {
        var fromRaw = Gs1ElementString.Parse(raw);
        var fromParenthesized = Gs1ElementString.Parse(parenthesized);

        Assert.Equal(fromParenthesized.Hri, fromRaw.Hri);
        Assert.Equal(fromParenthesized.Elements, fromRaw.Elements);
    }

    [Fact]
    public void Parse_parenthesizedGtinWithLetter_throwsFormatException()
    {
        // AI 01 (GTIN) is a predefined-fixed-length, purely numeric value; the trailing 'A' is
        // the 14th character, so the length check alone would not catch it.
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("(01)1234567890123A"));
    }

    [Fact]
    public void Parse_rawGtinWithLetter_throwsFormatException()
    {
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("011234567890123A"));
    }

    [Fact]
    public void Parse_rawUnrecognizedApplicationIdentifier_throwsFormatException()
    {
        // "66" is not an assigned application identifier at any length.
        Assert.Throws<FormatException>(() => Gs1ElementString.Parse("6612345"));
    }
}
