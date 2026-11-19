// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Conformance.Rules.Fonts;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// <c>CidRangeRule</c> and <c>GlyphPresenceRule</c> each kept a private literal-string decoder
/// that stopped at the first closing parenthesis, so <c>(a(b)c)</c> was read as <c>a(b</c>, and
/// that kept a raw CR (#511). Both now call the reader's decode. Expectations are the exact bytes.
/// </summary>
public sealed class FontRuleLiteralStringDecodeTests
{
    public static TheoryData<string, byte[]> Literals => new()
    {
        { "(a(b)c)", [0x61, 0x28, 0x62, 0x29, 0x63] },
        { "((()))", [0x28, 0x28, 0x29, 0x29] },
        { "(a" + "\\" + ")b)", [0x61, 0x29, 0x62] },
        { "(a\r\nb)", [0x61, 0x0A, 0x62] },
        { "(a\rb)", [0x61, 0x0A, 0x62] },
        { "(a\nb)", [0x61, 0x0A, 0x62] },
        { "(a\\\r\nb)", [0x61, 0x62] },
        { "(\\101\\60)", [0x41, 0x30] },
        { "(a\\nb)", [0x61, 0x0A, 0x62] },
    };

    [Theory]
    [MemberData(nameof(Literals))]
    public void CidRangeRule_decodesLikeTheReader(string token, byte[] expected)
    {
        Assert.Equal(expected, CidRangeRule.DecodeString(Encoding.Latin1.GetBytes(token), hex: false));
    }

    [Theory]
    [MemberData(nameof(Literals))]
    public void GlyphPresenceRule_decodesLikeTheReader(string token, byte[] expected)
    {
        Assert.Equal(expected, GlyphPresenceRule.DecodeString(Encoding.Latin1.GetBytes(token), hex: false));
    }
}
