// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="WinAnsiEncoding.TryGetByte"/>: identity range, the 0x80–0x9F punctuation
/// block, and rejection of chars WinAnsi does not cover.
/// </summary>
public sealed class WinAnsiEncodingTests
{
    // ── Identity range (U+0000–U+00FF) ───────────────────────────────────────

    [Theory]
    [InlineData(' ', (byte)0x20)]
    [InlineData('A', (byte)0x41)]
    [InlineData('~', (byte)0x7E)]
    [InlineData(' ', (byte)0xA0)] // nbspace
    [InlineData('°', (byte)0xB0)] // degree
    [InlineData('é', (byte)0xE9)] // eacute
    [InlineData('ÿ', (byte)0xFF)] // ydieresis
    public void TryGetByte_identityRange_returnsSameByte(char c, byte expected)
    {
        var ok = WinAnsiEncoding.TryGetByte(c, out var b);

        Assert.True(ok);
        Assert.Equal(expected, b);
    }

    // ── 0x80–0x9F punctuation block ──────────────────────────────────────────

    [Theory]
    [InlineData('€', (byte)0x80)] // Euro
    [InlineData('‚', (byte)0x82)] // quotesinglbase
    [InlineData('ƒ', (byte)0x83)] // florin
    [InlineData('„', (byte)0x84)] // quotedblbase
    [InlineData('…', (byte)0x85)] // ellipsis
    [InlineData('†', (byte)0x86)] // dagger
    [InlineData('‡', (byte)0x87)] // daggerdbl
    [InlineData('ˆ', (byte)0x88)] // circumflex
    [InlineData('‰', (byte)0x89)] // perthousand
    [InlineData('Š', (byte)0x8A)] // Scaron
    [InlineData('‹', (byte)0x8B)] // guilsinglleft
    [InlineData('Œ', (byte)0x8C)] // OE
    [InlineData('Ž', (byte)0x8E)] // Zcaron
    [InlineData('‘', (byte)0x91)] // quoteleft
    [InlineData('’', (byte)0x92)] // quoteright
    [InlineData('“', (byte)0x93)] // quotedblleft
    [InlineData('”', (byte)0x94)] // quotedblright
    [InlineData('•', (byte)0x95)] // bullet
    [InlineData('–', (byte)0x96)] // endash
    [InlineData('—', (byte)0x97)] // emdash
    [InlineData('˜', (byte)0x98)] // tilde
    [InlineData('™', (byte)0x99)] // trademark
    [InlineData('š', (byte)0x9A)] // scaron
    [InlineData('›', (byte)0x9B)] // guilsinglright
    [InlineData('œ', (byte)0x9C)] // oe
    [InlineData('ž', (byte)0x9E)] // zcaron
    [InlineData('Ÿ', (byte)0x9F)] // Ydieresis
    public void TryGetByte_highPunctuation_mapsToExpectedByte(char c, byte expected)
    {
        var ok = WinAnsiEncoding.TryGetByte(c, out var b);

        Assert.True(ok);
        Assert.Equal(expected, b);
    }

    // ── Rejection ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData('℞')] // PRESCRIPTION TAKE (undefined in WinAnsi)
    [InlineData('★')] // BLACK STAR
    [InlineData('中')] // CJK ideograph
    public void TryGetByte_outsideWinAnsi_returnsFalse(char c)
    {
        var ok = WinAnsiEncoding.TryGetByte(c, out var b);

        Assert.False(ok);
        Assert.Equal(0, b);
    }

    [Fact]
    public void TryGetByte_noHighPunctuationChar_mapsToUndefinedWinAnsiCodes()
    {
        // 0x81, 0x8D, 0x8F, 0x90, 0x9D have no Unicode assignment in WinAnsiEncoding. No
        // character above U+00FF (the high-punctuation block this encoder maps explicitly)
        // should ever produce one of these bytes. (Chars <= U+00FF map by identity per the
        // zero-regression design and are out of scope for this check: U+0081 identity-maps
        // to byte 0x81, matching prior Latin-1 behaviour, which is expected, not a WinAnsi glyph.)
        var mappedFromHighChars = new HashSet<byte>();
        for (var c = 0x100; c <= 0xFFFF; c++)
        {
            if (WinAnsiEncoding.TryGetByte((char)c, out var b))
                mappedFromHighChars.Add(b);
        }

        Assert.DoesNotContain((byte)0x81, mappedFromHighChars);
        Assert.DoesNotContain((byte)0x8D, mappedFromHighChars);
        Assert.DoesNotContain((byte)0x8F, mappedFromHighChars);
        Assert.DoesNotContain((byte)0x90, mappedFromHighChars);
        Assert.DoesNotContain((byte)0x9D, mappedFromHighChars);
    }
}
