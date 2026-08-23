// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Known-answer tests for the PDFDocEncoding password fallback. The table shipped with no tests at
/// all, and it is the kind of table that fails silently: a wrong byte does not throw, it just makes
/// a correct password stop authenticating on documents whose producer used this encoding. Every
/// expected value below is the PDF column of the Latin character set table in ISO 32000-1 Annex D,
/// whose codes are octal — 030 breve, 201 dagger, 235 scaron, 240 Euro.
/// </summary>
public sealed class PdfDocEncodingTests
{
    // char, expected byte, glyph name as the Annex D table spells it.
    public static TheoryData<char, byte, string> ExceptionTable =>
        new()
        {
            { '˘', 0x18, "breve" },
            { 'ˇ', 0x19, "caron" },
            { 'ˆ', 0x1A, "circumflex" },
            { '˙', 0x1B, "dotaccent" },
            { '˝', 0x1C, "hungarumlaut" },
            { '˛', 0x1D, "ogonek" },
            { '˚', 0x1E, "ring" },
            { '˜', 0x1F, "tilde" },
            { '•', 0x80, "bullet" },
            { '†', 0x81, "dagger" },
            { '‡', 0x82, "daggerdbl" },
            { '…', 0x83, "ellipsis" },
            { '—', 0x84, "emdash" },
            { '–', 0x85, "endash" },
            { 'ƒ', 0x86, "florin" },
            { '⁄', 0x87, "fraction" },
            { '‹', 0x88, "guilsinglleft" },
            { '›', 0x89, "guilsinglright" },
            { '−', 0x8A, "minus" },
            { '‰', 0x8B, "perthousand" },
            { '„', 0x8C, "quotedblbase" },
            { '“', 0x8D, "quotedblleft" },
            { '”', 0x8E, "quotedblright" },
            { '‘', 0x8F, "quoteleft" },
            { '’', 0x90, "quoteright" },
            { '‚', 0x91, "quotesinglbase" },
            { '™', 0x92, "trademark" },
            { 'ﬁ', 0x93, "fi" },
            { 'ﬂ', 0x94, "fl" },
            { 'Ł', 0x95, "Lslash" },
            { 'Œ', 0x96, "OE" },
            { 'Š', 0x97, "Scaron" },
            { 'Ÿ', 0x98, "Ydieresis" },
            { 'Ž', 0x99, "Zcaron" },
            { 'ı', 0x9A, "dotlessi" },
            { 'ł', 0x9B, "lslash" },
            { 'œ', 0x9C, "oe" },
            { 'š', 0x9D, "scaron" },
            { 'ž', 0x9E, "zcaron" },
            { '€', 0xA0, "Euro" },
        };

    [Theory]
    [MemberData(nameof(ExceptionTable))]
    public void EachExceptionCharacter_encodesToItsAnnexDCode(char c, byte expected, string glyphName)
    {
        Assert.True(PdfDocEncoding.TryEncode(c.ToString(), out var bytes), glyphName);

        Assert.Equal(new[] { expected }, bytes);
    }

    /// <summary>
    /// Outside the exception blocks PDFDocEncoding agrees with Latin-1, which is what lets the
    /// implementation be a table of exceptions over an identity map rather than 256 entries.
    /// </summary>
    [Theory]
    [InlineData("password")]
    [InlineData("Sesam öffne dich")]
    [InlineData("~!@#$%^&*()_+`-=[]{};':\",./<>?")]
    public void CharactersOutsideTheExceptionBlocks_encodeAsLatin1(string password)
    {
        Assert.True(PdfDocEncoding.TryEncode(password, out var bytes));

        Assert.Equal(Encoding.Latin1.GetBytes(password), bytes);
    }

    /// <summary>
    /// U+00A0 is the one character the Latin-1 agreement does NOT extend to: PDFDocEncoding puts
    /// Euro at 0xA0, so NO-BREAK SPACE has no representation and the candidate must be refused
    /// rather than silently encoded as a Euro sign.
    /// </summary>
    [Fact]
    public void NoBreakSpace_hasNoRepresentation()
    {
        Assert.False(PdfDocEncoding.TryEncode("a b", out var bytes));
        Assert.Empty(bytes);
    }

    /// <summary>A character the encoding cannot represent is refused, not mangled.</summary>
    [Theory]
    [InlineData("你好")]      // CJK
    [InlineData("passאword")]    // Hebrew alef
    [InlineData("Ж")]            // Cyrillic Zhe
    // The C0 and C1 controls the two block guards exist to reject. Every character above is
    // beyond U+00FF and would be refused by the <= 0xFF test alone, so without these rows the
    // guards themselves are unpinned.
    [InlineData("\u0018")]
    [InlineData("\u001F")]
    [InlineData("\u0080")]
    [InlineData("\u009F")]
    public void UnrepresentableCharacters_areRefused(string password)
    {
        Assert.False(PdfDocEncoding.TryEncode(password, out var bytes));
        Assert.Empty(bytes);
    }

    /// <summary>
    /// Annex D marks 0x7F and 0xAD Undefined, and this encoding encodes them anyway. Refusing a
    /// character drops the whole candidate rather than substituting for it, so a document whose
    /// <c>/U</c> was derived from one of those bytes would stop opening under its correct password —
    /// and the same table marks two dozen more code points Undefined, so refusing a chosen few would
    /// be arbitrary as well as harmful.
    /// </summary>
    [Theory]
    [InlineData("\u007F", (byte)0x7F)]
    [InlineData("\u00AD", (byte)0xAD)]
    public void UndefinedCodePointsInsideTheIdentityRange_encodeAsTheirByte(string password, byte expected)
    {
        Assert.True(PdfDocEncoding.TryEncode(password, out var bytes));

        Assert.Equal(new[] { expected }, bytes);
    }

    /// <summary>
    /// An empty or absent password encodes to no bytes and succeeds — the empty user password is
    /// the shape most encrypted PDFs actually use, so this is the common path, not an edge case.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyPassword_encodesToNoBytes(string? password)
    {
        Assert.True(PdfDocEncoding.TryEncode(password, out var bytes));
        Assert.Empty(bytes);
    }

    /// <summary>
    /// Truncation at 127 bytes matches <c>StandardSecurityHandler.PasswordBytes</c>, whose own
    /// truncation is what a producer would have applied before hashing.
    /// </summary>
    [Fact]
    public void LongPassword_isTruncatedTo127Bytes()
    {
        Assert.True(PdfDocEncoding.TryEncode(new string('x', 200), out var bytes));

        Assert.Equal(127, bytes.Length);
    }
}
