// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader;

/// <summary>
/// Parses PDF objects from a raw byte buffer per ISO 32000-2 §7.3.
/// Works over a <see cref="PdfLexer"/>; can parse a single primitive object,
/// a top-level indirect object (<c>N G obj … endobj</c>), or a stream.
/// </summary>
internal sealed class PdfObjectParser
{
    private readonly PdfLexer _lexer;

    // Guards against unbounded recursion on hostile input (e.g. deeply nested
    // "[[[[…" or "<</a<</a…"). A StackOverflowException is uncatchable in .NET, so
    // we cap the array/dictionary nesting depth and throw a recoverable exception.
    private const int MaxNestingDepth = 256;
    private int _depth;

    /// <summary>Creates a parser backed by <paramref name="lexer"/>.</summary>
    public PdfObjectParser(PdfLexer lexer) => _lexer = lexer;

    /// <summary>
    /// Creates a parser over <paramref name="data"/> starting at <paramref name="offset"/>.
    /// </summary>
    public PdfObjectParser(ReadOnlyMemory<byte> data, int offset = 0)
        : this(new PdfLexer(data, offset)) { }

    /// <summary>Current byte position within the underlying buffer.</summary>
    public int Position => _lexer.Position;

    // ── Public API consumed by Phase 2 xref resolver ──────────────────────

    /// <summary>
    /// Parses one primitive PDF object from the current position.
    /// Handles: null, boolean, integer, real, name, literal string, hex string, array, dictionary.
    /// Does NOT handle indirect-object wrappers (<c>N G obj</c>) or streams;
    /// use <see cref="ParseIndirectObject"/> for those.
    /// </summary>
    /// <exception cref="InvalidDataException">On malformed PDF.</exception>
    public PdfObject ParseObject()
    {
        _lexer.SkipWhitespaceAndComments();
        if (_lexer.AtEnd)
            throw new InvalidDataException("Unexpected end of input; expected a PDF object.");

        var token = _lexer.NextToken();
        return token.Kind switch
        {
            TokenKind.Keyword => ParseKeywordObject(token),
            TokenKind.Integer => ParseIntegerOrReference(token),
            TokenKind.Real => ParseReal(token),
            TokenKind.Name => ParseName(token),
            TokenKind.LiteralString => DecodeLiteralString(token.Raw),
            TokenKind.HexString => DecodeHexString(token.Raw),
            TokenKind.ArrayBegin => ParseArray(),
            TokenKind.DictBegin => ParseDictionary(),
            TokenKind.EndOfInput => throw new InvalidDataException(
                "Unexpected end of input; expected a PDF object."),
            _ => throw new InvalidDataException(
                $"Unexpected token {token.Kind} at offset {_lexer.Position}."),
        };
    }

    /// <summary>
    /// Parses a top-level indirect object starting at the current position:
    /// <c>N G obj … endobj</c> (ISO 32000-2 §7.3.10).
    /// If the object value is a <see cref="PdfDictionary"/> immediately followed
    /// by the <c>stream</c> keyword, the stream body is captured as a
    /// <see cref="ParsedStream"/>; the returned <see cref="IndirectObjectResult.Value"/>
    /// will be <see langword="null"/> and <see cref="IndirectObjectResult.Stream"/> non-null.
    /// </summary>
    /// <exception cref="InvalidDataException">On malformed PDF.</exception>
    public IndirectObjectResult ParseIndirectObject()
    {
        // Expect: integer (obj-num), integer (gen), keyword "obj"
        var objNumTok = ExpectToken(TokenKind.Integer, "object number");
        var genTok = ExpectToken(TokenKind.Integer, "generation number");
        var objKw = ExpectToken(TokenKind.Keyword, "'obj' keyword");

        if (!IsKeyword(objKw.Raw, "obj"u8))
            throw new InvalidDataException(
                $"Expected 'obj' keyword, got '{Encoding.Latin1.GetString(objKw.Raw.Span)}' at offset {_lexer.Position}.");

        var objectNumber = ParseLong(objNumTok.Raw);
        var generation = (int)ParseLong(genTok.Raw);

        // Parse the value
        _lexer.SkipWhitespaceAndComments();

        // peek: is the value a dictionary that might be followed by 'stream'?
        if (_lexer.TryPeek() == (byte)'<' && PeekIsDict())
        {
            _lexer.NextToken(); // consumes <<, returns DictBegin
            var dict = ParseDictionary();

            // Check for 'stream' keyword after the dictionary
            _lexer.SkipWhitespaceAndComments();
            if (!_lexer.AtEnd && TryPeekKeyword("stream"u8))
            {
                var stream = ParseStreamBody(dict);
                return new IndirectObjectResult((int)objectNumber, generation, null, stream);
            }

            // Not a stream — check for an indirect reference inside the dict value
            // (already fully parsed as a dict); now expect endobj
            ExpectEndobj();
            return new IndirectObjectResult((int)objectNumber, generation, dict, null);
        }

        var value = ParseObject();
        ExpectEndobj();
        return new IndirectObjectResult((int)objectNumber, generation, value, null);
    }

    // ── Private helpers ────────────────────────────────────────────────────

    private bool PeekIsDict()
    {
        var pos = _lexer.Position;
        if (pos + 1 >= _lexer.Length) return false;
        var s = _lexer.Slice(pos, 2);
        return s.Span[0] == (byte)'<' && s.Span[1] == (byte)'<';
    }

    private bool TryPeekKeyword(ReadOnlySpan<byte> keyword)
    {
        var pos = _lexer.Position;
        var remaining = _lexer.Length - pos;
        if (remaining < keyword.Length) return false;
        var slice = _lexer.Slice(pos, keyword.Length).Span;
        if (!slice.SequenceEqual(keyword)) return false;
        // must be followed by whitespace or delimiter or end
        if (pos + keyword.Length < _lexer.Length)
        {
            var next = _lexer.Slice(pos + keyword.Length, 1).Span[0];
            if (!IsWhitespaceOrDelimiter(next)) return false;
        }
        return true;
    }

    private static bool IsWhitespaceOrDelimiter(byte b) =>
        b is 0 or 9 or 10 or 12 or 13 or 32
          or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
          or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
          or (byte)'/' or (byte)'%';

    private PdfObject ParseKeywordObject(Token token)
    {
        var raw = token.Raw.Span;
        if (raw.SequenceEqual("true"u8)) return PdfBoolean.True;
        if (raw.SequenceEqual("false"u8)) return PdfBoolean.False;
        if (raw.SequenceEqual("null"u8)) return PdfNull.Instance;
        throw new InvalidDataException(
            $"Unexpected keyword '{Encoding.Latin1.GetString(raw)}' where a PDF object was expected.");
    }

    /// <summary>
    /// Parses an integer token; peeks ahead to see if it forms an indirect reference
    /// (N G R) or just a plain integer.
    /// </summary>
    private PdfObject ParseIntegerOrReference(Token firstIntToken)
    {
        // Save position before peeking
        var savedPos = _lexer.Position;

        _lexer.SkipWhitespaceAndComments();
        if (_lexer.AtEnd)
            return new PdfInteger(ParseLong(firstIntToken.Raw));

        var peekTok = _lexer.NextToken();
        if (peekTok.Kind == TokenKind.Integer)
        {
            // Could be generation number — peek one more
            var savedPos2 = _lexer.Position;
            _lexer.SkipWhitespaceAndComments();
            if (!_lexer.AtEnd)
            {
                var peekTok2 = _lexer.NextToken();
                if (peekTok2.Kind == TokenKind.Keyword && IsKeyword(peekTok2.Raw, "R"u8))
                {
                    // It's an indirect reference
                    var objNum = (int)ParseLong(firstIntToken.Raw);
                    return new PdfIndirectReference(objNum);
                }
                // Back up — not an R keyword
                _lexer.Seek(savedPos2);
            }
            // Back up — not a reference
            _lexer.Seek(savedPos);
            return new PdfInteger(ParseLong(firstIntToken.Raw));
        }

        // Back up
        _lexer.Seek(savedPos);
        return new PdfInteger(ParseLong(firstIntToken.Raw));
    }

    private static PdfReal ParseReal(Token token)
    {
        var s = Encoding.Latin1.GetString(token.Raw.Span);
        if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            throw new InvalidDataException($"Malformed real number: '{s}'.");
        return new PdfReal(d);
    }

    private static PdfName ParseName(Token token)
    {
        // token.Raw includes the leading '/'
        var raw = token.Raw.Span[1..]; // skip '/'
        if (raw.IsEmpty)
            throw new InvalidDataException(
                "Malformed PDF: a name object must have at least one character after '/'.");

        // Decode #XX escapes
        var buf = new byte[raw.Length]; // at most as long as raw
        var len = 0;
        var i = 0;
        while (i < raw.Length)
        {
            var c = raw[i];
            if (c == (byte)'#' && i + 2 < raw.Length)
            {
                var hi = HexDigit(raw[i + 1]);
                var lo = HexDigit(raw[i + 2]);
                if (hi >= 0 && lo >= 0)
                {
                    buf[len++] = (byte)((hi << 4) | lo);
                    i += 3;
                    continue;
                }
            }
            buf[len++] = c;
            i++;
        }
        // Names are ISO Latin-1 bytes decoded as UTF-8 when possible; store as string
        return new PdfName(Encoding.Latin1.GetString(buf, 0, len));
    }

    private static int HexDigit(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };

    private PdfArray ParseArray()
    {
        if (++_depth > MaxNestingDepth)
            throw new InvalidDataException(
                $"PDF object nesting exceeds {MaxNestingDepth} levels; aborting to prevent stack overflow.");
        try
        {
            var items = new List<PdfObject>();
            while (true)
            {
                _lexer.SkipWhitespaceAndComments();
                if (_lexer.AtEnd)
                    throw new InvalidDataException("Unterminated array; missing ']'.");
                if (_lexer.TryPeek() == (byte)']')
                {
                    _lexer.NextToken(); // consume ArrayEnd
                    break;
                }
                items.Add(ParseObject());
            }
            return new PdfArray(items);
        }
        finally { _depth--; }
    }

    private PdfDictionary ParseDictionary()
    {
        if (++_depth > MaxNestingDepth)
            throw new InvalidDataException(
                $"PDF object nesting exceeds {MaxNestingDepth} levels; aborting to prevent stack overflow.");
        try
        {
            var dict = new PdfDictionary();
            while (true)
            {
                _lexer.SkipWhitespaceAndComments();
                if (_lexer.AtEnd)
                    throw new InvalidDataException("Unterminated dictionary; missing '>>'.");

                var tok = _lexer.NextToken();
                if (tok.Kind == TokenKind.DictEnd)
                    break;

                if (tok.Kind != TokenKind.Name)
                    throw new InvalidDataException(
                        $"Expected a name key in dictionary, got {tok.Kind} at offset {_lexer.Position}.");

                var key = ParseName(tok);
                var value = ParseObject();
                dict.Set(key, value);
            }
            return dict;
        }
        finally { _depth--; }
    }

    // ── Stream parsing ─────────────────────────────────────────────────────

    private ParsedStream ParseStreamBody(PdfDictionary dict)
    {
        // consume the 'stream' keyword token
        _lexer.NextToken();

        // Per spec: the keyword must be followed by CRLF or LF.
        // Skip exactly one line ending.
        SkipStreamNewline();

        var bodyStart = _lexer.Position;

        // Determine body length: prefer /Length when it's a direct, in-range integer.
        // A negative or > int.MaxValue value is malformed (the buffer is <= int.MaxValue);
        // fall back to the endstream scan rather than truncating on a wrapped cast.
        int bodyLen = -1;
        if (dict.TryGet(PdfName.Length, out var lenObj) && lenObj is PdfInteger pdfLen
            && pdfLen.Value >= 0 && pdfLen.Value <= int.MaxValue)
            bodyLen = (int)pdfLen.Value;

        if (bodyLen >= 0)
        {
            // Trust /Length
            if ((long)bodyStart + bodyLen > _lexer.Length)
                throw new InvalidDataException(
                    $"Stream /Length {bodyLen} exceeds buffer at offset {bodyStart}.");
            var body = _lexer.Slice(bodyStart, bodyLen);
            _lexer.Seek(bodyStart + bodyLen);

            // Expect optional whitespace then 'endstream'
            _lexer.SkipWhitespaceAndComments();
            var endTok = _lexer.NextToken();
            if (endTok.Kind != TokenKind.Keyword || !IsKeyword(endTok.Raw, "endstream"u8))
                throw new InvalidDataException(
                    $"Expected 'endstream' after stream body at offset {_lexer.Position}.");

            return new ParsedStream(dict, body);
        }
        else
        {
            // Scan for 'endstream' marker
            return ScanToEndstream(dict, bodyStart);
        }
    }

    private ParsedStream ScanToEndstream(PdfDictionary dict, int bodyStart)
    {
        // We search for the byte sequence "\nendstream" or "\r\nendstream"
        // and take everything before the newline as the body.
        var span = _lexer.Slice(bodyStart, _lexer.Length - bodyStart).Span;
        ReadOnlySpan<byte> marker = "endstream"u8;

        for (var i = 0; i <= span.Length - marker.Length; i++)
        {
            if (span[i..].StartsWith(marker))
            {
                // Check it's at a line boundary: preceded by LF or CRLF
                var bodyEnd = i;
                if (bodyEnd > 0 && span[bodyEnd - 1] == (byte)'\n')
                    bodyEnd--;
                if (bodyEnd > 0 && span[bodyEnd - 1] == (byte)'\r')
                    bodyEnd--;

                var body = _lexer.Slice(bodyStart, bodyEnd);
                _lexer.Seek(bodyStart + i + marker.Length);
                return new ParsedStream(dict, body);
            }
        }
        throw new InvalidDataException(
            $"Could not find 'endstream' marker after stream starting at offset {bodyStart}.");
    }

    private void SkipStreamNewline()
    {
        // Per ISO 32000-2 §7.3.8.1: the stream keyword must be followed by
        // either a CARRIAGE RETURN and a LINE FEED or just a LINE FEED.
        // Whitespace-only lines are not allowed between 'stream' and body.
        if (_lexer.AtEnd) return;
        var b = _lexer.TryPeek();
        if (b == '\r')
        {
            _lexer.ReadByte(); // consume CR
            if (_lexer.TryPeek() == '\n')
                _lexer.ReadByte(); // consume LF
        }
        else if (b == '\n')
        {
            _lexer.ReadByte(); // consume LF
        }
    }

    // ── String decoders ────────────────────────────────────────────────────

    /// <summary>Decodes the raw literal-string token bytes (including '(' and ')') into a <see cref="PdfLiteralString"/>.</summary>
    internal static PdfLiteralString DecodeLiteralString(ReadOnlyMemory<byte> raw)
    {
        // raw includes the outer ( and )
        var span = raw.Span[1..^1];
        var buf = new byte[span.Length]; // upper bound
        var len = 0;

        var i = 0;
        while (i < span.Length)
        {
            var c = span[i];
            if (c != (byte)'\\')
            {
                buf[len++] = c;
                i++;
                continue;
            }
            // backslash escape
            i++;
            if (i >= span.Length) break; // trailing backslash — ignore per spec
            var esc = span[i];
            switch (esc)
            {
                case (byte)'n': buf[len++] = (byte)'\n'; i++; break;
                case (byte)'r': buf[len++] = (byte)'\r'; i++; break;
                case (byte)'t': buf[len++] = (byte)'\t'; i++; break;
                case (byte)'b': buf[len++] = 0x08; i++; break;
                case (byte)'f': buf[len++] = 0x0C; i++; break;
                case (byte)'(': buf[len++] = (byte)'('; i++; break;
                case (byte)')': buf[len++] = (byte)')'; i++; break;
                case (byte)'\\': buf[len++] = (byte)'\\'; i++; break;
                case (byte)'\r':
                    // line continuation: backslash-CR or backslash-CRLF — discard the newline
                    i++;
                    if (i < span.Length && span[i] == (byte)'\n')
                        i++;
                    break;
                case (byte)'\n':
                    // line continuation: backslash-LF — discard
                    i++;
                    break;
                default:
                    // 1–3 digit octal
                    if (esc >= (byte)'0' && esc <= (byte)'7')
                    {
                        var octal = esc - '0';
                        i++;
                        if (i < span.Length && span[i] >= (byte)'0' && span[i] <= (byte)'7')
                        {
                            octal = octal * 8 + span[i] - '0';
                            i++;
                            if (i < span.Length && span[i] >= (byte)'0' && span[i] <= (byte)'7')
                            {
                                octal = octal * 8 + span[i] - '0';
                                i++;
                            }
                        }
                        buf[len++] = (byte)(octal & 0xFF);
                    }
                    else
                    {
                        // unrecognised escape — emit the character as-is per spec
                        buf[len++] = esc;
                        i++;
                    }
                    break;
            }
        }
        return new PdfLiteralString(new ReadOnlyMemory<byte>(buf, 0, len));
    }

    /// <summary>Decodes the raw hex-string token bytes (including '&lt;' and '&gt;') into a <see cref="PdfHexString"/>.</summary>
    internal static PdfHexString DecodeHexString(ReadOnlyMemory<byte> raw)
    {
        // raw includes < and >
        var span = raw.Span[1..^1];

        // collect hex digits, skip whitespace (+1 slot for an odd-length pad nibble).
        // The token length is attacker-controlled, so only stackalloc for small strings; a large
        // hex string would otherwise overflow the stack (an uncatchable crash).
        var maxNibbles = span.Length + 1;
        Span<byte> nibbles = maxNibbles <= 1024 ? stackalloc byte[maxNibbles] : new byte[maxNibbles];
        var count = 0;
        foreach (var b in span)
        {
            if (b is 0 or 9 or 10 or 12 or 13 or 32) continue;
            var d = HexDigit(b);
            if (d < 0)
                throw new InvalidDataException(
                    $"Invalid hex digit 0x{b:X2} in hex string.");
            nibbles[count++] = (byte)d;
        }

        // odd length — pad with 0
        if ((count & 1) != 0)
        {
            nibbles[count++] = 0;
        }

        var result = new byte[count / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)((nibbles[i * 2] << 4) | nibbles[i * 2 + 1]);

        return new PdfHexString(result);
    }

    // ── Utilities ──────────────────────────────────────────────────────────

    private Token ExpectToken(TokenKind kind, string what)
    {
        var tok = _lexer.NextToken();
        if (tok.Kind != kind)
            throw new InvalidDataException(
                $"Expected {what} ({kind}), got {tok.Kind} at offset {_lexer.Position}.");
        return tok;
    }

    private void ExpectEndobj()
    {
        _lexer.SkipWhitespaceAndComments();
        var tok = _lexer.NextToken();
        if (tok.Kind != TokenKind.Keyword || !IsKeyword(tok.Raw, "endobj"u8))
            throw new InvalidDataException(
                $"Expected 'endobj', got '{Encoding.Latin1.GetString(tok.Raw.Span)}' at offset {_lexer.Position}.");
    }

    private static bool IsKeyword(ReadOnlyMemory<byte> raw, ReadOnlySpan<byte> keyword) =>
        raw.Span.SequenceEqual(keyword);

    private static long ParseLong(ReadOnlyMemory<byte> raw)
    {
        var s = Encoding.Latin1.GetString(raw.Span);
        if (!long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v))
            throw new InvalidDataException($"Malformed integer: '{s}'.");
        return v;
    }
}

/// <summary>
/// The result of parsing a top-level indirect object
/// (<c>N G obj … endobj</c> or <c>N G obj &lt;&lt;…&gt;&gt; stream…endstream endobj</c>).
/// Exactly one of <see cref="Value"/> or <see cref="Stream"/> is non-null.
/// </summary>
internal sealed class IndirectObjectResult
{
    /// <summary>The object number.</summary>
    public int ObjectNumber { get; }

    /// <summary>The generation number (0 for freshly written documents).</summary>
    public int Generation { get; }

    /// <summary>The parsed object value, or <see langword="null"/> when this is a stream object.</summary>
    public PdfObject? Value { get; }

    /// <summary>The parsed stream, or <see langword="null"/> when this is a non-stream object.</summary>
    public ParsedStream? Stream { get; }

    /// <summary>True when the indirect object wraps a stream.</summary>
    public bool IsStream => Stream is not null;

    internal IndirectObjectResult(int objectNumber, int generation, PdfObject? value, ParsedStream? stream)
    {
        ObjectNumber = objectNumber;
        Generation = generation;
        Value = value;
        Stream = stream;
    }
}
