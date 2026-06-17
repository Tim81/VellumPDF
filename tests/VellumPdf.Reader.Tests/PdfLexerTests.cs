// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

public sealed class PdfLexerTests
{
    private static PdfLexer Lex(string s) =>
        new(System.Text.Encoding.Latin1.GetBytes(s));

    private static Token Next(string s)
    {
        var lex = Lex(s);
        return lex.NextToken();
    }

    // ── Whitespace and comment skipping ────────────────────────────────────

    [Fact]
    public void SkipsLeadingWhitespace()
    {
        var tok = Next("   42");
        Assert.Equal(TokenKind.Integer, tok.Kind);
    }

    [Fact]
    public void SkipsComment()
    {
        var tok = Next("% this is a comment\n99");
        Assert.Equal(TokenKind.Integer, tok.Kind);
        Assert.Equal("99", System.Text.Encoding.Latin1.GetString(tok.Raw.Span));
    }

    [Fact]
    public void SkipsMultipleComments()
    {
        var lex = Lex("% first\n% second\ntrue");
        var tok = lex.NextToken();
        Assert.Equal(TokenKind.Keyword, tok.Kind);
    }

    // ── EndOfInput ─────────────────────────────────────────────────────────

    [Fact]
    public void EndOfInputOnEmptyBuffer()
    {
        var tok = Next("");
        Assert.Equal(TokenKind.EndOfInput, tok.Kind);
    }

    [Fact]
    public void EndOfInputAfterWhitespace()
    {
        var tok = Next("   ");
        Assert.Equal(TokenKind.EndOfInput, tok.Kind);
    }

    // ── Integer tokens ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("0")]
    [InlineData("42")]
    [InlineData("-7")]
    [InlineData("+3")]
    [InlineData("123456")]
    public void RecognisesInteger(string input)
    {
        var tok = Next(input);
        Assert.Equal(TokenKind.Integer, tok.Kind);
        Assert.Equal(input, System.Text.Encoding.Latin1.GetString(tok.Raw.Span));
    }

    // ── Real tokens ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3.14")]
    [InlineData("-.5")]
    [InlineData("+3.0")]
    [InlineData("4.")]
    [InlineData("0.0")]
    [InlineData(".5")]
    public void RecognisesReal(string input)
    {
        var tok = Next(input);
        Assert.Equal(TokenKind.Real, tok.Kind);
    }

    // ── Keyword tokens ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("null")]
    [InlineData("obj")]
    [InlineData("endobj")]
    [InlineData("stream")]
    [InlineData("endstream")]
    [InlineData("R")]
    public void RecognisesKeyword(string input)
    {
        var tok = Next(input);
        Assert.Equal(TokenKind.Keyword, tok.Kind);
        Assert.Equal(input, System.Text.Encoding.Latin1.GetString(tok.Raw.Span));
    }

    // ── Name tokens ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/Type")]
    [InlineData("/FlateDecode")]
    [InlineData("/F#23")]
    public void RecognisesName(string input)
    {
        var tok = Next(input);
        Assert.Equal(TokenKind.Name, tok.Kind);
    }

    // ── Literal string tokens ──────────────────────────────────────────────

    [Fact]
    public void RecognisesLiteralString()
    {
        var tok = Next("(Hello)");
        Assert.Equal(TokenKind.LiteralString, tok.Kind);
    }

    [Fact]
    public void LiteralStringWithNestedParens()
    {
        var tok = Next("(Hello (world))");
        Assert.Equal(TokenKind.LiteralString, tok.Kind);
        Assert.Equal("(Hello (world))", System.Text.Encoding.Latin1.GetString(tok.Raw.Span));
    }

    [Fact]
    public void LiteralStringWithEscapedParen()
    {
        var tok = Next(@"(He said \(hi\))");
        Assert.Equal(TokenKind.LiteralString, tok.Kind);
    }

    // ── Hex string tokens ──────────────────────────────────────────────────

    [Fact]
    public void RecognisesHexString()
    {
        var tok = Next("<48656C6C6F>");
        Assert.Equal(TokenKind.HexString, tok.Kind);
    }

    [Fact]
    public void RecognisesEmptyHexString()
    {
        var tok = Next("<>");
        Assert.Equal(TokenKind.HexString, tok.Kind);
    }

    // ── Array / dict delimiters ────────────────────────────────────────────

    [Fact]
    public void RecognisesArrayBeginEnd()
    {
        var lex = Lex("[  ]");
        Assert.Equal(TokenKind.ArrayBegin, lex.NextToken().Kind);
        Assert.Equal(TokenKind.ArrayEnd, lex.NextToken().Kind);
    }

    [Fact]
    public void RecognisesDictBeginEnd()
    {
        var lex = Lex("<<  >>");
        Assert.Equal(TokenKind.DictBegin, lex.NextToken().Kind);
        Assert.Equal(TokenKind.DictEnd, lex.NextToken().Kind);
    }

    // ── Multi-token sequences ──────────────────────────────────────────────

    [Fact]
    public void TokenisesIndirectReference()
    {
        var lex = Lex("12 0 R");
        Assert.Equal(TokenKind.Integer, lex.NextToken().Kind);
        Assert.Equal(TokenKind.Integer, lex.NextToken().Kind);
        Assert.Equal(TokenKind.Keyword, lex.NextToken().Kind);
    }

    // ── Error cases ────────────────────────────────────────────────────────

    [Fact]
    public void UnterminatedLiteralStringThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Next("(unterminated"));
        Assert.Contains("Unterminated literal string", ex.Message);
    }

    [Fact]
    public void UnterminatedHexStringThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Next("<48"));
        Assert.Contains("Unterminated hex string", ex.Message);
    }

    [Fact]
    public void StrayClosingAngleThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Next("> garbage"));
        Assert.Contains("Unexpected '>'", ex.Message);
    }

    // ── Position tracking ──────────────────────────────────────────────────

    [Fact]
    public void PositionAdvancesCorrectly()
    {
        var lex = Lex("12 34");
        lex.NextToken();
        Assert.True(lex.Position > 0);
        lex.NextToken();
        Assert.True(lex.AtEnd);
    }
}
