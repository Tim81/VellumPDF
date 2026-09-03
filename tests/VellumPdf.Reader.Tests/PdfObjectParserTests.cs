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

    [Fact]
    public void RealWithEnoughIntegerDigitsToOverflowDouble_throwsInvalidDataException()
    {
        // ISO 32000-2 §7.3.3 gives real numbers an implementation-limited range (Annex C.2 /
        // Table C.1). A literal with 310+ integer digits parses to +/-Infinity under
        // double.TryParse instead of failing outright, and PdfReal's own constructor rejects that
        // with ArgumentException, a type no other caller of this parser expects to see escape an
        // otherwise-successful parse.
        var huge = "1" + new string('0', 309) + ".0";

        var ex = Assert.Throws<InvalidDataException>(() => Parser(huge).ParseObject());

        Assert.Contains("out of range", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RealComfortablyBelowDoubleMaxValue_stillParses()
    {
        // Boundary companion to the overflow case above: a real of similar magnitude (around
        // 1e300, comfortably under double.MaxValue's ~1.7977e308) is not itself a range violation.
        var big = "1" + new string('0', 300) + ".0";

        var obj = Parser(big).ParseObject();

        var r = Assert.IsType<PdfReal>(obj);
        Assert.True(double.IsFinite(r.Value));
        Assert.True(r.Value > 1e299);
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

    /// <summary>
    /// A <c>/Length</c> past <see cref="int.MaxValue"/>. The sibling above uses 99999, which the range
    /// test accepts and the buffer-bounds test below it then rejects, so it reaches the scan either
    /// way and cannot show the upper bound doing anything.
    ///
    /// <para>This one can. 4294967299 narrows to 3, and the body carries a literal <c>endstream</c>
    /// exactly three bytes in — the case the scan's preference tiers exist for (#105), since the real
    /// terminator is the later one, the one followed by <c>endobj</c>. A wrapped cast therefore lands
    /// on a marker that IS present, passes the does-endstream-follow check, and truncates the body to
    /// two bytes without a word. The bound is the only thing standing between the parse and
    /// that.</para>
    /// </summary>
    [Fact]
    public void ParsesStreamWithLengthBeyondIntMaxValue_FallsBackToScan()
    {
        const string pdf =
            "1 0 obj\n<< /Length 4294967299 >>\nstream\nAB\nendstream\nXY\nendstream\nendobj";
        var parser = Parser(pdf);

        var result = parser.ParseIndirectObject();

        Assert.True(result.IsStream);
        Assert.Equal(
            "AB\nendstream\nXY",
            System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));
    }

    [Fact]
    public void ParsesStreamWithNoEolBeforeEndstream_FallsBackToScan()
    {
        // The producer omitted the EOL ISO 32000-2 §7.3.8.1 calls for between the stream data and
        // 'endstream' — a real-world producer bug, not hostile input. The scan must still find it
        // (matching the pre-#105 behaviour) rather than requiring the EOL and throwing. Regression
        // guard: an earlier version of the hardened scan made the EOL mandatory with no fallback.
        const string pdf = "1 0 obj\n<< /Length 999 >>\nstream\nBT (Hi) Tj ETendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal("BT (Hi) Tj ET", System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));
    }

    [Fact]
    public void ParsesStreamWithNoEolBeforeEndstream_DoesNotSwallowTheNextObject()
    {
        // Same missing-EOL body as above, but a second, well-formed stream object follows. An
        // earlier version of the hardened scan rejected the first (real) 'endstream' outright for
        // lacking a preceding EOL, then kept scanning and matched the SECOND object's 'endstream'
        // instead — silently absorbing the first object's 'endobj', the second object's header, its
        // dictionary, and its 'stream' keyword into the first object's body. What follows the
        // marker ('endobj' here) must be checked regardless of what precedes it, so the first,
        // correct 'endstream' wins.
        const string pdf =
            "1 0 obj\n<< /Length 999 >>\nstream\nBT (Hi) Tj ETendstream\nendobj\n" +
            "5 0 obj\n<< /Length 5 >>\nstream\nAAAAA\nendstream\nendobj";
        var parser = Parser(pdf);
        var result = parser.ParseIndirectObject();
        Assert.True(result.IsStream);
        Assert.Equal("BT (Hi) Tj ET", System.Text.Encoding.Latin1.GetString(result.Stream!.RawBody.Span));

        // The cursor must land right after the FIRST object's 'endstream', not somewhere inside
        // (or past) the second object.
        var expectedPos = pdf.IndexOf("endstream", StringComparison.Ordinal) + "endstream".Length;
        Assert.Equal(expectedPos, parser.Position);
    }

    // xUnit1069 wants TestContext.Current.CancellationToken threaded through so the Timeout can end
    // the test promptly; ParseIndirectObject takes no CancellationToken, and there is nothing to
    // thread it into. The Timeout itself is the #193 regression pin the test name describes — the
    // whole point is that a quadratic ScanToEndstream blows this budget — so it stays rather than
    // being dropped.
#pragma warning disable xUnit1069
    [Fact(Timeout = 10_000)]
    public void ManyStreamsWithNoEolBeforeTheirOwnEndstream_DoesNotBecomeQuadratic()
    {
        // Every one of many streams lacks the EOL before its own 'endstream'. A scan that rejects
        // an EOL-less candidate outright (see the two tests above) does not just pick the wrong
        // terminator for one stream — for EVERY stream it walks forward into all the streams that
        // follow before finding one whose own terminator happens to qualify, turning the whole
        // parse into O(streamCount x fileSize). This must stay fast: each stream's own terminator
        // should be found immediately since 'endobj' follows it directly. Each object is parsed
        // from a fresh parser at its known offset — the point under test is ScanToEndstream's own
        // cost, not whether ParseIndirectObject chains across an 'endobj' it doesn't consume.
        const int streamCount = 2000;
        var offsets = new int[streamCount];
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < streamCount; i++)
        {
            offsets[i] = sb.Length;
            sb.Append(i).Append(" 0 obj\n<< /Length 999999 >>\nstream\n");
            sb.Append("SOME BODY DATA HERE").Append(i); // no trailing EOL before 'endstream'
            sb.Append("endstream\nendobj\n");
        }
        var bytes = System.Text.Encoding.ASCII.GetBytes(sb.ToString());

        for (var i = 0; i < streamCount; i++)
        {
            var result = new PdfObjectParser(bytes, offsets[i]).ParseIndirectObject();
            Assert.True(result.IsStream);
            Assert.Equal($"SOME BODY DATA HERE{i}", System.Text.Encoding.ASCII.GetString(result.Stream!.RawBody.Span));
        }
    }
#pragma warning restore xUnit1069

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
