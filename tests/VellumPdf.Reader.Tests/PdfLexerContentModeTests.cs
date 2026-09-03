// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Pins <see cref="PdfLexer"/>'s content-stream mode (#98, part 3): the default constructor's
/// behaviour must stay byte-identical (it still throws on a lone <c>{</c>, <c>}</c>, or unmatched
/// <c>&gt;</c>), and the new <c>internal PdfLexer(ReadOnlyMemory&lt;byte&gt;, bool)</c> constructor
/// must accept those same three bytes as one-byte <see cref="TokenKind.Keyword"/> tokens (ISO
/// 32000-2 §7.8.2) while lexing every other token kind identically to the default constructor.
/// </summary>
public sealed class PdfLexerContentModeTests
{
    // ── Default constructor: unchanged behaviour ────────────────────────────────────────────────

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData(">")]
    public void DefaultConstructor_stillThrowsOnPostScriptHeritageBytes(string input)
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(input));

        Assert.Throws<InvalidDataException>(() => lexer.NextToken());
    }

    [Fact]
    public void DefaultConstructor_stillThrowsOnUnmatchedGreaterThan_evenMidStream()
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes("1 0 obj > endobj"));
        lexer.NextToken(); // 1
        lexer.NextToken(); // 0
        lexer.NextToken(); // obj

        Assert.Throws<InvalidDataException>(() => lexer.NextToken());
    }

    // ── Content-stream mode: the three bytes become one-byte Keyword tokens ────────────────────────

    [Theory]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData(">")]
    public void ContentStreamMode_lexesPostScriptHeritageBytesAsOneByteKeywordTokens(string input)
    {
        var bytes = Encoding.ASCII.GetBytes(input);
        var lexer = new PdfLexer(bytes, contentStreamMode: true);

        var token = lexer.NextToken();

        Assert.Equal(TokenKind.Keyword, token.Kind);
        Assert.Equal(1, token.Raw.Length);
        Assert.Equal(input[0], (char)token.Raw.Span[0]);
        Assert.Equal(1, lexer.Position);
    }

    [Fact]
    public void ContentStreamMode_lexesADictionaryEndImmediatelyAfterALoneGreaterThan()
    {
        // "> >>": a lone '>' keyword token, then a real dictionary-end token right after it.
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes("> >>"), contentStreamMode: true);

        var first = lexer.NextToken();
        var second = lexer.NextToken();

        Assert.Equal(TokenKind.Keyword, first.Kind);
        Assert.Equal(TokenKind.DictEnd, second.Kind);
    }

    [Fact]
    public void ContentStreamMode_stillLexesADoubleGreaterThanAsDictEnd_notTwoLoneKeywords()
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes(">>"), contentStreamMode: true);

        var token = lexer.NextToken();

        Assert.Equal(TokenKind.DictEnd, token.Kind);
        Assert.Equal(2, lexer.Position);
    }

    [Fact]
    public void ContentStreamMode_lexesTwoLoneGreaterThansSplitByWhitespace_asTwoOneByteKeywords()
    {
        // ">>" is one DictEnd token (above); "> >", with whitespace between the two bytes, is two
        // separate one-byte Keyword tokens instead.
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes("> >"), contentStreamMode: true);

        var first = lexer.NextToken();
        var second = lexer.NextToken();
        var third = lexer.NextToken();

        Assert.Equal(TokenKind.Keyword, first.Kind);
        Assert.Equal(1, first.Raw.Length);
        Assert.Equal(TokenKind.Keyword, second.Kind);
        Assert.Equal(1, second.Raw.Length);
        Assert.Equal(TokenKind.EndOfInput, third.Kind);
    }

    [Fact]
    public void ContentStreamMode_seekingPastAOneByteKeyword_lexesTheFollowingTokenNormally()
    {
        var lexer = new PdfLexer(Encoding.ASCII.GetBytes("{Tj"), contentStreamMode: true);

        var brace = lexer.NextToken();
        Assert.Equal(TokenKind.Keyword, brace.Kind);
        Assert.Equal(1, lexer.Position);

        lexer.Seek(1);
        var next = lexer.NextToken();

        Assert.Equal(TokenKind.Keyword, next.Kind);
        Assert.Equal("Tj", Encoding.ASCII.GetString(next.Raw.Span));
    }

    [Fact]
    public void ContentStreamMode_insideACompatibilitySection_lexesAWholeBxToExSequenceWithoutThrowing()
    {
        // A PostScript-heritage compatibility fragment ISO 32000-2 §7.8.2 permits inside BX/EX.
        var bytes = Encoding.ASCII.GetBytes("BX { pop } EX");
        var lexer = new PdfLexer(bytes, contentStreamMode: true);

        var kinds = new List<TokenKind>();
        Token tok;
        while ((tok = lexer.NextToken()).Kind != TokenKind.EndOfInput)
            kinds.Add(tok.Kind);

        Assert.Equal(
            [
                TokenKind.Keyword, // BX
                TokenKind.Keyword, // {
                TokenKind.Keyword, // pop
                TokenKind.Keyword, // }
                TokenKind.Keyword, // EX
            ],
            kinds);
    }

    // ── Every other token kind lexes identically in both modes ─────────────────────────────────────

    // MemberData parameters must be public-accessible types (CS0051), so TokenKind (internal, via
    // this test assembly's InternalsVisibleTo friendship) is named by string here and parsed back
    // inside the theory body instead of being the parameter type itself.
    public static IEnumerable<object[]> AllTokenKindFixtures()
    {
        yield return ["123", nameof(TokenKind.Integer)];
        yield return ["-.5", nameof(TokenKind.Real)];
        yield return ["6.", nameof(TokenKind.Real)];
        yield return ["/Name#20With#23Escape", nameof(TokenKind.Name)];
        yield return ["(literal (nested) string)", nameof(TokenKind.LiteralString)];
        yield return ["<48656C6C6F>", nameof(TokenKind.HexString)];
        yield return ["[", nameof(TokenKind.ArrayBegin)];
        yield return ["]", nameof(TokenKind.ArrayEnd)];
        yield return ["<<", nameof(TokenKind.DictBegin)];
        yield return ["true", nameof(TokenKind.Keyword)];
        yield return ["Tj", nameof(TokenKind.Keyword)];
    }

    [Theory]
    [MemberData(nameof(AllTokenKindFixtures))]
    public void EveryOtherTokenKind_lexesIdenticallyInBothModes(string input, string expectedKindName)
    {
        var expectedKind = Enum.Parse<TokenKind>(expectedKindName);
        var bytes = Encoding.ASCII.GetBytes(input);
        var defaultLexer = new PdfLexer(bytes);
        var contentLexer = new PdfLexer(bytes, contentStreamMode: true);

        var defaultToken = defaultLexer.NextToken();
        var contentToken = contentLexer.NextToken();

        Assert.Equal(expectedKind, defaultToken.Kind);
        Assert.Equal(defaultToken.Kind, contentToken.Kind);
        Assert.True(defaultToken.Raw.Span.SequenceEqual(contentToken.Raw.Span));
        Assert.Equal(defaultLexer.Position, contentLexer.Position);
    }

    [Fact]
    public void EveryTokenKind_overOneFixtureCoveringAllOfThem_lexesIdenticallyInBothModes()
    {
        const string fixture =
            "q 1 0 0 1 10 20 cm /F1 12 Tf (Hello) Tj <48656C6C6F> Tj [1 2 3] true false null Q";
        var bytes = Encoding.ASCII.GetBytes(fixture);
        var defaultLexer = new PdfLexer(bytes);
        var contentLexer = new PdfLexer(bytes, contentStreamMode: true);

        while (true)
        {
            var a = defaultLexer.NextToken();
            var b = contentLexer.NextToken();
            Assert.Equal(a.Kind, b.Kind);
            Assert.True(a.Raw.Span.SequenceEqual(b.Raw.Span));
            if (a.Kind == TokenKind.EndOfInput)
                break;
        }
        Assert.Equal(defaultLexer.Position, contentLexer.Position);
    }
}
