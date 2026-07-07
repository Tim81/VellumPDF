// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Aztec;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for the five Aztec Code encodation-mode tables (ISO/IEC 24778 clause 7.3.2, Table 3).</summary>
public sealed class AztecTablesTests
{
    [Theory]
    [InlineData(' ', 1)]
    [InlineData('A', 2)]
    [InlineData('N', 15)]
    [InlineData('O', 16)]
    [InlineData('Z', 27)]
    public void TryGetCode_upperLetters_matchesTable(char c, int expectedCode)
    {
        Assert.True(AztecTables.TryGetCode(AztecMode.Upper, (byte)c, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData(' ', 1)]
    [InlineData('a', 2)]
    [InlineData('z', 27)]
    public void TryGetCode_lowerLetters_matchesTable(char c, int expectedCode)
    {
        Assert.True(AztecTables.TryGetCode(AztecMode.Lower, (byte)c, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData('0', 2)]
    [InlineData('9', 11)]
    [InlineData(',', 12)]
    [InlineData('.', 13)]
    public void TryGetCode_digitMode_matchesTable(char c, int expectedCode)
    {
        Assert.True(AztecTables.TryGetCode(AztecMode.Digit, (byte)c, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData('!', 6)]
    [InlineData('/', 20)]
    [InlineData('[', 27)]
    [InlineData('}', 30)]
    public void TryGetCode_punctMode_matchesTable(char c, int expectedCode)
    {
        Assert.True(AztecTables.TryGetCode(AztecMode.Punct, (byte)c, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Theory]
    [InlineData(1, 2)]   // ^A
    [InlineData(9, 10)]  // ^I (tab)
    [InlineData(13, 14)] // ^M (CR)
    [InlineData(27, 15)] // ^[ (Esc)
    [InlineData(31, 19)] // ^_ (US)
    [InlineData('@', 20)]
    [InlineData('~', 26)]
    [InlineData(127, 27)] // DEL
    public void TryGetCode_mixedMode_matchesTable(int byteValue, int expectedCode)
    {
        Assert.True(AztecTables.TryGetCode(AztecMode.Mixed, (byte)byteValue, out var code));
        Assert.Equal(expectedCode, code);
    }

    [Fact]
    public void TryGetCode_nul_isNotRepresentableInAnyMode()
    {
        foreach (var mode in new[] { AztecMode.Upper, AztecMode.Lower, AztecMode.Mixed, AztecMode.Punct, AztecMode.Digit })
            Assert.False(AztecTables.TryGetCode(mode, 0, out _));
    }

    [Theory]
    [InlineData(0x0E)]
    [InlineData(0x15)]
    [InlineData(0x1A)]
    public void TryGetCode_midControlRange_isNotRepresentableInAnyMode(int byteValue)
    {
        // Ctrl-N through Ctrl-Z (0x0E-0x1A): Mixed mode jumps from CR (0x0D, code 14) straight to
        // Esc (0x1B, code 15), so this range has no text-mode code at all.
        foreach (var mode in new[] { AztecMode.Upper, AztecMode.Lower, AztecMode.Mixed, AztecMode.Punct, AztecMode.Digit })
            Assert.False(AztecTables.TryGetCode(mode, (byte)byteValue, out _));
    }

    [Fact]
    public void CodeBits_digitModeIsFourBits_othersAreFive()
    {
        Assert.Equal(4, AztecTables.CodeBits(AztecMode.Digit));
        Assert.Equal(5, AztecTables.CodeBits(AztecMode.Upper));
        Assert.Equal(5, AztecTables.CodeBits(AztecMode.Lower));
        Assert.Equal(5, AztecTables.CodeBits(AztecMode.Mixed));
        Assert.Equal(5, AztecTables.CodeBits(AztecMode.Punct));
    }

    // AztecMode is internal, and a public [Theory] method cannot declare an internal parameter
    // type even within a same-assembly-visible test project, so mode pairs are passed as their
    // enum names and parsed back here.
    private static AztecMode Mode(string name) => Enum.Parse<AztecMode>(name);

    [Theory]
    [InlineData("Upper", "Punct")]
    [InlineData("Lower", "Punct")]
    [InlineData("Mixed", "Punct")]
    [InlineData("Digit", "Punct")]
    [InlineData("Lower", "Upper")]
    [InlineData("Digit", "Upper")]
    public void GetShiftCode_definedTransitions_returnNonNegative(string from, string to)
    {
        Assert.True(AztecTables.GetShiftCode(Mode(from), Mode(to)) >= 0);
    }

    [Theory]
    [InlineData("Upper", "Upper")]
    [InlineData("Upper", "Lower")]
    [InlineData("Mixed", "Digit")]
    [InlineData("Punct", "Lower")]
    public void GetShiftCode_undefinedTransitions_returnNegative(string from, string to)
    {
        Assert.Equal(-1, AztecTables.GetShiftCode(Mode(from), Mode(to)));
    }

    [Theory]
    [InlineData("Upper", "Lower")]
    [InlineData("Upper", "Mixed")]
    [InlineData("Upper", "Digit")]
    [InlineData("Lower", "Mixed")]
    [InlineData("Lower", "Digit")]
    [InlineData("Mixed", "Lower")]
    [InlineData("Mixed", "Upper")]
    [InlineData("Mixed", "Punct")]
    [InlineData("Punct", "Upper")]
    [InlineData("Digit", "Upper")]
    public void GetDirectLatchCode_definedTransitions_returnNonNegative(string from, string to)
    {
        Assert.True(AztecTables.GetDirectLatchCode(Mode(from), Mode(to)) >= 0);
    }

    [Theory]
    [InlineData("Lower", "Upper")]   // shift only, no direct latch
    [InlineData("Lower", "Punct")]   // needs a hop through Mixed
    [InlineData("Mixed", "Digit")]   // needs a hop through Upper
    [InlineData("Punct", "Digit")]   // needs a hop through Upper
    [InlineData("Digit", "Punct")]   // needs two hops (Upper, then Mixed)
    public void GetDirectLatchCode_noSingleHop_returnsNegative(string from, string to)
    {
        Assert.Equal(-1, AztecTables.GetDirectLatchCode(Mode(from), Mode(to)));
    }

    [Fact]
    public void Every7BitAsciiByteExceptNulAndMidControlRange_isRepresentableInSomeMode()
    {
        for (var b = 1; b <= 127; b++)
        {
            if (b is >= 0x0E and <= 0x1A) continue;

            var representable =
                AztecTables.TryGetCode(AztecMode.Upper, (byte)b, out _) ||
                AztecTables.TryGetCode(AztecMode.Lower, (byte)b, out _) ||
                AztecTables.TryGetCode(AztecMode.Mixed, (byte)b, out _) ||
                AztecTables.TryGetCode(AztecMode.Punct, (byte)b, out _) ||
                AztecTables.TryGetCode(AztecMode.Digit, (byte)b, out _);

            Assert.True(representable, $"byte {b} ('{(char)b}') is not representable in any of the five modes.");
        }
    }
}
