// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

public sealed class PdfObjectParserTests
{
    private static PdfObjectParser Parser(string s) =>
        new(System.Text.Encoding.Latin1.GetBytes(s));

    // ── Boolean ────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesTrue()
    {
        var obj = Parser("true").ParseObject();
        var b = Assert.IsType<PdfBoolean>(obj);
        Assert.True(b.Value);
    }

    [Fact]
    public void ParsesFalse()
    {
        var obj = Parser("false").ParseObject();
        var b = Assert.IsType<PdfBoolean>(obj);
        Assert.False(b.Value);
    }

    // ── Hostile input: recursion bomb must throw, not stack-overflow ──────────

    [Fact]
    public void DeeplyNestedArray_throws_instead_of_crashing()
    {
        var ex = Assert.Throws<InvalidDataException>(
            () => Parser(new string('[', 100_000)).ParseObject());
        Assert.Contains("nesting", ex.Message);
    }

    [Fact]
    public void DeeplyNestedDictionary_throws_instead_of_crashing()
    {
        var deep = string.Concat(System.Linq.Enumerable.Repeat("<</a ", 100_000));
        var ex = Assert.Throws<InvalidDataException>(() => Parser(deep).ParseObject());
        Assert.Contains("nesting", ex.Message);
    }

    [Fact]
    public void TrueReturnsSingleton()
    {
        Assert.Same(PdfBoolean.True, Parser("true").ParseObject());
    }

    [Fact]
    public void FalseReturnsSingleton()
    {
        Assert.Same(PdfBoolean.False, Parser("false").ParseObject());
    }

    // ── Null ───────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesNull()
    {
        var obj = Parser("null").ParseObject();
        Assert.Same(PdfNull.Instance, obj);
    }

    // ── Integer ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0", 0L)]
    [InlineData("42", 42L)]
    [InlineData("-7", -7L)]
    [InlineData("+3", 3L)]
    [InlineData("9999999", 9999999L)]
    public void ParsesInteger(string input, long expected)
    {
        var obj = Parser(input).ParseObject();
        var n = Assert.IsType<PdfInteger>(obj);
        Assert.Equal(expected, n.Value);
    }

    // ── Real ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("3.14", 3.14)]
    [InlineData("-.5", -0.5)]
    [InlineData("+3.0", 3.0)]
    [InlineData("4.", 4.0)]
    [InlineData("0.0", 0.0)]
    [InlineData(".5", 0.5)]
    public void ParsesReal(string input, double expected)
    {
        var obj = Parser(input).ParseObject();
        var r = Assert.IsType<PdfReal>(obj);
        Assert.Equal(expected, r.Value, 6);
    }

    // ── Name ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/Type", "Type")]
    [InlineData("/FlateDecode", "FlateDecode")]
    [InlineData("/Foo", "Foo")]
    public void ParsesName(string input, string expected)
    {
        var obj = Parser(input).ParseObject();
        var n = Assert.IsType<PdfName>(obj);
        Assert.Equal(expected, n.Value);
    }

    [Fact]
    public void ParsesNameWithHashEscape()
    {
        // /F#23 → 'F' '#' → "F#" (0x23 = '#')
        var obj = Parser("/F#23").ParseObject();
        var n = Assert.IsType<PdfName>(obj);
        Assert.Equal("F#", n.Value);
    }

    [Fact]
    public void ParsesNameWithSpaceEscape()
    {
        // /Hello#20World → "Hello World" (0x20 = space)
        var obj = Parser("/Hello#20World").ParseObject();
        var n = Assert.IsType<PdfName>(obj);
        Assert.Equal("Hello World", n.Value);
    }

    [Fact]
    public void ParsesNameWithHexLowercase()
    {
        // /A#2f → 'A' '/' (0x2f = '/')
        var obj = Parser("/A#2f").ParseObject();
        var n = Assert.IsType<PdfName>(obj);
        Assert.Equal("A/", n.Value);
    }

    // ── Literal string ─────────────────────────────────────────────────────

    [Fact]
    public void ParsesSimpleLiteralString()
    {
        var obj = Parser("(Hello)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal("Hello", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void LiteralStringEscapeNewline()
    {
        var obj = Parser(@"(\n)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 0x0A }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeReturn()
    {
        var obj = Parser(@"(\r)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 0x0D }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeTab()
    {
        var obj = Parser(@"(\t)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 0x09 }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeBackspace()
    {
        var obj = Parser(@"(\b)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 0x08 }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeFormFeed()
    {
        var obj = Parser(@"(\f)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 0x0C }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeOpenParen()
    {
        var obj = Parser(@"(\()").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { (byte)'(' }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeCloseParen()
    {
        var obj = Parser(@"(\))").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { (byte)')' }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringEscapeBackslash()
    {
        var obj = Parser(@"(\\)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { (byte)'\\' }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringOctalOneDigit()
    {
        // \5 → 0x05
        var obj = Parser("(\\5)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 5 }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringOctalTwoDigits()
    {
        // \41 = 0o41 = 33 = '!'
        var obj = Parser("(\\41)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 33 }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringOctalThreeDigits()
    {
        // \101 = 0o101 = 65 = 'A'
        var obj = Parser("(\\101)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 65 }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringOctalOverflow()
    {
        // \377 = 0o377 = 255
        var obj = Parser("(\\377)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(new byte[] { 255 }, s.Bytes.ToArray());
    }

    [Fact]
    public void LiteralStringLineContinuationLf()
    {
        // backslash immediately followed by LF is ignored per spec
        var raw = "(Hel\\\nlo)";
        var obj = Parser(raw).ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal("Hello", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void LiteralStringLineContinuationCrLf()
    {
        var raw = "(Hel\\\r\nlo)";
        var obj = Parser(raw).ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal("Hello", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void LiteralStringBalancedNestedParens()
    {
        var obj = Parser("(Hello (world))").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal("Hello (world)", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void LiteralStringDeeplyNested()
    {
        var obj = Parser("(a (b (c) d) e)").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal("a (b (c) d) e", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void LiteralStringEmpty()
    {
        var obj = Parser("()").ParseObject();
        var s = Assert.IsType<PdfLiteralString>(obj);
        Assert.Equal(0, s.Bytes.Length);
    }

    // ── Hex string ─────────────────────────────────────────────────────────

    [Fact]
    public void ParsesHexString()
    {
        var obj = Parser("<48656C6C6F>").ParseObject();
        var s = Assert.IsType<PdfHexString>(obj);
        Assert.Equal("Hello", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void HexStringWithWhitespace()
    {
        var obj = Parser("<48 65 6C 6C 6F>").ParseObject();
        var s = Assert.IsType<PdfHexString>(obj);
        Assert.Equal("Hello", System.Text.Encoding.Latin1.GetString(s.Bytes.Span));
    }

    [Fact]
    public void HexStringOddLengthPadded()
    {
        // <9> → <90> → byte 0x90
        var obj = Parser("<9>").ParseObject();
        var s = Assert.IsType<PdfHexString>(obj);
        Assert.Equal(new byte[] { 0x90 }, s.Bytes.ToArray());
    }

    [Fact]
    public void HexStringLowercase()
    {
        var obj = Parser("<deadbeef>").ParseObject();
        var s = Assert.IsType<PdfHexString>(obj);
        Assert.Equal(new byte[] { 0xDE, 0xAD, 0xBE, 0xEF }, s.Bytes.ToArray());
    }

    [Fact]
    public void HexStringEmpty()
    {
        var obj = Parser("<>").ParseObject();
        var s = Assert.IsType<PdfHexString>(obj);
        Assert.Equal(0, s.Bytes.Length);
    }

    [Fact]
    public void HexStringInvalidDigitThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parser("<XY>").ParseObject());
        Assert.Contains("Invalid hex digit", ex.Message);
    }

    // ── Array ──────────────────────────────────────────────────────────────

    [Fact]
    public void ParsesEmptyArray()
    {
        var obj = Parser("[]").ParseObject();
        var arr = Assert.IsType<PdfArray>(obj);
        Assert.Equal(0, arr.Count);
    }

    [Fact]
    public void ParsesSimpleArray()
    {
        var obj = Parser("[1 2 3]").ParseObject();
        var arr = Assert.IsType<PdfArray>(obj);
        Assert.Equal(3, arr.Count);
        Assert.Equal(1L, ((PdfInteger)arr[0]).Value);
        Assert.Equal(2L, ((PdfInteger)arr[1]).Value);
        Assert.Equal(3L, ((PdfInteger)arr[2]).Value);
    }

    [Fact]
    public void ParsesMixedArray()
    {
        var obj = Parser("[true 3.14 /Name (str)]").ParseObject();
        var arr = Assert.IsType<PdfArray>(obj);
        Assert.Equal(4, arr.Count);
        Assert.IsType<PdfBoolean>(arr[0]);
        Assert.IsType<PdfReal>(arr[1]);
        Assert.IsType<PdfName>(arr[2]);
        Assert.IsType<PdfLiteralString>(arr[3]);
    }

    [Fact]
    public void ParsesNestedArray()
    {
        var obj = Parser("[[1 2] [3 4]]").ParseObject();
        var outer = Assert.IsType<PdfArray>(obj);
        Assert.Equal(2, outer.Count);
        var inner0 = Assert.IsType<PdfArray>(outer[0]);
        Assert.Equal(2, inner0.Count);
    }

    [Fact]
    public void UnterminatedArrayThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parser("[1 2").ParseObject());
        Assert.Contains("Unterminated array", ex.Message);
    }

    // ── Dictionary ─────────────────────────────────────────────────────────

    [Fact]
    public void ParsesEmptyDict()
    {
        var obj = Parser("<< >>").ParseObject();
        Assert.IsType<PdfDictionary>(obj);
    }

    [Fact]
    public void ParsesSimpleDict()
    {
        var obj = Parser("<< /Type /Page >>").ParseObject();
        var dict = Assert.IsType<PdfDictionary>(obj);
        var v = dict.Get(new PdfName("Type"));
        Assert.NotNull(v);
        var name = Assert.IsType<PdfName>(v);
        Assert.Equal("Page", name.Value);
    }

    [Fact]
    public void ParsesDictWithMultipleEntries()
    {
        var obj = Parser("<< /Width 100 /Height 200 /BitsPerComponent 8 >>").ParseObject();
        var dict = Assert.IsType<PdfDictionary>(obj);
        Assert.Equal(100L, ((PdfInteger)dict.Get(new PdfName("Width"))!).Value);
        Assert.Equal(200L, ((PdfInteger)dict.Get(new PdfName("Height"))!).Value);
        Assert.Equal(8L, ((PdfInteger)dict.Get(new PdfName("BitsPerComponent"))!).Value);
    }

    [Fact]
    public void ParsesNestedDict()
    {
        var obj = Parser("<< /Resources << /Font << >> >> >>").ParseObject();
        var outer = Assert.IsType<PdfDictionary>(obj);
        var res = Assert.IsType<PdfDictionary>(outer.Get(new PdfName("Resources")));
        Assert.IsType<PdfDictionary>(res.Get(new PdfName("Font")));
    }

    [Fact]
    public void UnterminatedDictThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parser("<< /Type /Page ").ParseObject());
        Assert.Contains("Unterminated dictionary", ex.Message);
    }

    [Fact]
    public void DictNonNameKeyThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parser("<< 42 /Page >>").ParseObject());
        Assert.Contains("Expected a name key in dictionary", ex.Message);
    }

    // ── Indirect reference ─────────────────────────────────────────────────

    [Fact]
    public void ParsesIndirectReference()
    {
        var obj = Parser("12 0 R").ParseObject();
        var r = Assert.IsType<PdfIndirectReference>(obj);
        Assert.Equal(12, r.ObjectNumber);
    }

    [Fact]
    public void ParsesIndirectReferenceWithGenerationIgnored()
    {
        // Generation is parsed but not stored (MVP: always 0)
        var obj = Parser("5 3 R").ParseObject();
        var r = Assert.IsType<PdfIndirectReference>(obj);
        Assert.Equal(5, r.ObjectNumber);
    }

    [Fact]
    public void StandaloneIntegerNotConfusedWithReference()
    {
        // "42" not followed by integer+R
        var obj = Parser("42").ParseObject();
        Assert.IsType<PdfInteger>(obj);
    }

    [Fact]
    public void TwoIntegersNotConfusedWithReference()
    {
        // "1 2" — not a reference (no R); first int parsed, parser at pos of '2'
        var parser = Parser("1 2");
        var obj = parser.ParseObject();
        Assert.IsType<PdfInteger>(obj);
        // second should still be parseable
        var obj2 = parser.ParseObject();
        Assert.IsType<PdfInteger>(obj2);
    }

    // ── Indirect object (N G obj…endobj) ───────────────────────────────────

    [Fact]
    public void ParsesIndirectObject()
    {
        var parser = Parser("5 0 obj\n42\nendobj");
        var result = parser.ParseIndirectObject();
        Assert.Equal(5, result.ObjectNumber);
        Assert.Equal(0, result.Generation);
        Assert.False(result.IsStream);
        var v = Assert.IsType<PdfInteger>(result.Value);
        Assert.Equal(42L, v.Value);
    }

    [Fact]
    public void ParsesIndirectObjectDictValue()
    {
        var parser = Parser("1 0 obj\n<< /Type /Catalog >>\nendobj");
        var result = parser.ParseIndirectObject();
        Assert.Equal(1, result.ObjectNumber);
        Assert.False(result.IsStream);
        Assert.IsType<PdfDictionary>(result.Value);
    }

    [Fact]
    public void ParsesIndirectObjectWithGeneration()
    {
        var parser = Parser("7 2 obj\nnull\nendobj");
        var result = parser.ParseIndirectObject();
        Assert.Equal(7, result.ObjectNumber);
        Assert.Equal(2, result.Generation);
    }

    [Fact]
    public void MissingEndobjThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Parser("1 0 obj\n42\n").ParseIndirectObject());
        Assert.Contains("endobj", ex.Message);
    }

    [Fact]
    public void MissingObjKeywordThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Parser("1 0 notobj\n42\nendobj").ParseIndirectObject());
        Assert.Contains("obj", ex.Message);
    }

    // ── Stream object ──────────────────────────────────────────────────────

    [Fact]
    public void ParsesStreamWithLength()
    {
        const string pdf = "1 0 obj\n<< /Length 5 >>\nstream\nHello\nendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.Equal(1, result.ObjectNumber);
        Assert.True(result.IsStream);
        Assert.Null(result.Value);

        var stream = result.Stream!;
        Assert.NotNull(stream.Dictionary.Get(new PdfName("Length")));

        // Verify raw body captured verbatim
        var body = System.Text.Encoding.Latin1.GetString(stream.RawBody.Span);
        Assert.Equal("Hello", body);
    }

    [Fact]
    public void ParsesStreamCrLfAfterKeyword()
    {
        const string pdf = "2 0 obj\n<< /Length 3 >>\nstream\r\nABC\nendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal("ABC", System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));
    }

    [Fact]
    public void ParsesStreamWithoutLength()
    {
        // No /Length — scan to endstream
        const string pdf = "3 0 obj\n<< >>\nstream\nDATA\nendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal("DATA", System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));
    }

    [Fact]
    public void ParsesStreamWithWrongLength_FallsBackToScan()
    {
        // A wrong /Length is a common producer bug (here declared 3 but the body is 11 bytes). The
        // parser must not truncate at /Length and then fail because 'endstream' isn't there: it falls
        // back to scanning for the marker and recovers the full body. Regression guard for round 4.
        const string pdf = "1 0 obj\n<< /Length 3 >>\nstream\nHello World\nendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal("Hello World", System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));
    }

    [Fact]
    public void ParsesStreamWithTooLargeLength_FallsBackToScan()
    {
        // /Length far exceeds the buffer; rather than throwing, scan for 'endstream'.
        const string pdf = "1 0 obj\n<< /Length 99999 >>\nstream\nABC\nendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal("ABC", System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));
    }

    [Fact]
    public void StreamBodyCapturedVerbatim()
    {
        // Binary body with all-bytes-range (simulated with a known pattern)
        var bodyBytes = new byte[] { 0x78, 0x9C, 0x00, 0x01, 0xFF };
        var header = "10 0 obj\n<< /Length 5 >>\nstream\n"u8.ToArray();
        var footer = "\nendstream\nendobj"u8.ToArray();
        var full = new byte[header.Length + bodyBytes.Length + footer.Length];
        header.CopyTo(full, 0);
        bodyBytes.CopyTo(full, header.Length);
        footer.CopyTo(full, header.Length + bodyBytes.Length);

        var parser = new PdfObjectParser(full);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal(bodyBytes, result.Stream!.RawBody.ToArray());
    }

    // ── Numeric edge cases ─────────────────────────────────────────────────

    [Fact]
    public void NegativeReal()
    {
        var obj = Parser("-0.5").ParseObject();
        var r = Assert.IsType<PdfReal>(obj);
        Assert.Equal(-0.5, r.Value, 6);
    }

    [Fact]
    public void LeadingDotReal()
    {
        var obj = Parser(".75").ParseObject();
        var r = Assert.IsType<PdfReal>(obj);
        Assert.Equal(0.75, r.Value, 6);
    }

    [Fact]
    public void TrailingDotReal()
    {
        var obj = Parser("4.").ParseObject();
        var r = Assert.IsType<PdfReal>(obj);
        Assert.Equal(4.0, r.Value, 6);
    }

    [Fact]
    public void ZeroInteger()
    {
        var obj = Parser("0").ParseObject();
        var n = Assert.IsType<PdfInteger>(obj);
        Assert.Equal(0L, n.Value);
    }

    [Fact]
    public void NegativeInteger()
    {
        var obj = Parser("-100").ParseObject();
        var n = Assert.IsType<PdfInteger>(obj);
        Assert.Equal(-100L, n.Value);
    }

    // ── Unexpected keyword as object ───────────────────────────────────────

    [Fact]
    public void UnknownKeywordAsObjectThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() =>
            Parser("garbage").ParseObject());
        Assert.Contains("Unexpected keyword", ex.Message);
    }

    // ── Decode helpers: direct static tests ────────────────────────────────

    [Fact]
    public void DecodeHexStringStaticMethod()
    {
        var raw = System.Text.Encoding.Latin1.GetBytes("<4142>");
        var hs = PdfObjectParser.DecodeHexString(raw);
        Assert.Equal(new byte[] { 0x41, 0x42 }, hs.Bytes.ToArray());
    }

    [Fact]
    public void DecodeLiteralStringStaticMethod()
    {
        var raw = System.Text.Encoding.Latin1.GetBytes("(AB)");
        var ls = PdfObjectParser.DecodeLiteralString(raw);
        Assert.Equal(new byte[] { 0x41, 0x42 }, ls.Bytes.ToArray());
    }

    // ── Empty / edge input ─────────────────────────────────────────────────

    [Fact]
    public void EmptyInputThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parser("").ParseObject());
        Assert.Contains("Unexpected end of input", ex.Message);
    }

    [Fact]
    public void WhitespaceOnlyInputThrows()
    {
        var ex = Assert.Throws<InvalidDataException>(() => Parser("   ").ParseObject());
        Assert.Contains("Unexpected end of input", ex.Message);
    }
}
