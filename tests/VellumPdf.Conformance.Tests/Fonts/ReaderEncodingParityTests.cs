// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using ConformanceEncoding = VellumPdf.Conformance.Rules.Fonts.SimpleFontEncoding;
using ReaderEncodings = VellumPdf.Reader.Fonts.SimpleFontEncodings;

namespace VellumPdf.Conformance.Tests.Fonts;

/// <summary>
/// Proves the one claim <c>SimpleFontEncodings</c>' own class doc makes about
/// <c>src/VellumPdf.Conformance/Rules/Fonts/SimpleFontEncoding.cs</c>: that the two copies diverge
/// at exactly eight WinAnsi codes and seventeen MacRoman codes, and nowhere else. This project sees
/// both assemblies' internals (Reader's <c>AssemblyInfo.cs</c> grants
/// <c>VellumPdf.Conformance.Tests</c>; Conformance's grants its own tests), so it is the one place
/// that can compare them directly instead of trusting the class doc's own count.
/// </summary>
public sealed class ReaderEncodingParityTests
{
    private static List<int> DifferingCodes(string?[] conformance, System.ReadOnlySpan<string?> reader)
    {
        var codes = new List<int>();
        for (var code = 0; code < 256; code++)
        {
            if (conformance[code] != reader[code])
                codes.Add(code);
        }
        return codes;
    }

    [Fact]
    public void Standard_matchesExactly()
    {
        Assert.Empty(DifferingCodes(ConformanceEncoding.Standard, ReaderEncodings.Standard));
    }

    [Fact]
    public void WinAnsi_differsAtExactlyTheEightFootnoteCodes()
    {
        int[] expected = [0x7F, 0x81, 0x8D, 0x8F, 0x90, 0x9D, 0xA0, 0xAD];
        Assert.Equal(expected, DifferingCodes(ConformanceEncoding.WinAnsi, ReaderEncodings.WinAnsi).Order());
    }

    [Fact]
    public void MacRoman_differsAtExactlyTheSeventeenCodes()
    {
        int[] expected =
        [
            0xAD, 0xB0, 0xB2, 0xB3, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xBD,
            0xC3, 0xC5, 0xC6, 0xD7, 0xF0, 0xCA, 0xDB,
        ];
        Assert.Equal(expected.Order(), DifferingCodes(ConformanceEncoding.MacRoman, ReaderEncodings.MacRoman).Order());
    }
}
