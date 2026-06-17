// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Token kinds produced by <see cref="PdfLexer"/> (ISO 32000-2 §7.2).
/// </summary>
internal enum TokenKind
{
    /// <summary>End of the input buffer.</summary>
    EndOfInput,

    /// <summary>A keyword token: true, false, null, obj, endobj, stream, endstream, R, xref, trailer, startxref.</summary>
    Keyword,

    /// <summary>An integer numeric literal.</summary>
    Integer,

    /// <summary>A real numeric literal (contains a decimal point or exponent).</summary>
    Real,

    /// <summary>A name object starting with /.</summary>
    Name,

    /// <summary>A literal string enclosed in ( ).</summary>
    LiteralString,

    /// <summary>A hex string enclosed in &lt; &gt;.</summary>
    HexString,

    /// <summary>The array-open delimiter [.</summary>
    ArrayBegin,

    /// <summary>The array-close delimiter ].</summary>
    ArrayEnd,

    /// <summary>The dictionary-open delimiter &lt;&lt;.</summary>
    DictBegin,

    /// <summary>The dictionary-close delimiter &gt;&gt;.</summary>
    DictEnd,
}

/// <summary>
/// A token returned by <see cref="PdfLexer"/>.
/// </summary>
internal readonly struct Token
{
    /// <summary>The token kind.</summary>
    public TokenKind Kind { get; }

    /// <summary>
    /// The raw byte span covering this token in the source buffer.
    /// For strings and names the span includes the delimiters.
    /// For numeric tokens it is the raw digit/sign/dot bytes.
    /// </summary>
    public ReadOnlyMemory<byte> Raw { get; }

    internal Token(TokenKind kind, ReadOnlyMemory<byte> raw)
    {
        Kind = kind;
        Raw = raw;
    }

    /// <inheritdoc />
    public override string ToString() =>
        $"{Kind}({System.Text.Encoding.Latin1.GetString(Raw.Span)})";
}

/// <summary>
/// Low-level tokeniser for PDF syntax per ISO 32000-2 §7.2.
/// Operates over a <see cref="ReadOnlyMemory{T}"/> of bytes; maintains a cursor
/// (position) that advances as tokens are consumed.
/// </summary>
internal sealed class PdfLexer
{
    private readonly ReadOnlyMemory<byte> _data;

    /// <summary>Current byte offset within <see cref="_data"/>.</summary>
    public int Position { get; private set; }

    /// <summary>Total length of the underlying buffer.</summary>
    public int Length => _data.Length;

    /// <summary>Whether the cursor is at or past the end of the buffer.</summary>
    public bool AtEnd => Position >= _data.Length;

    /// <summary>Creates a lexer over the supplied buffer starting at <paramref name="offset"/>.</summary>
    public PdfLexer(ReadOnlyMemory<byte> data, int offset = 0)
    {
        _data = data;
        Position = offset;
    }

    // ── ISO 32000-2 §7.2.2 — whitespace bytes ─────────────────────────────
    private static bool IsWhitespace(byte b) =>
        b is 0 or 9 or 10 or 12 or 13 or 32;

    // ── ISO 32000-2 §7.2.2 — delimiter bytes ──────────────────────────────
    private static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
          or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}'
          or (byte)'/' or (byte)'%';

    private static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    private ReadOnlySpan<byte> Span => _data.Span;

    // ── Skip whitespace and comments ───────────────────────────────────────

    /// <summary>Advances past whitespace bytes and <c>%</c>-to-EOL comments.</summary>
    public void SkipWhitespaceAndComments()
    {
        while (Position < _data.Length)
        {
            var b = Span[Position];
            if (b == (byte)'%')
            {
                // skip to end-of-line
                Position++;
                while (Position < _data.Length && Span[Position] is not 10 and not 13)
                    Position++;
            }
            else if (IsWhitespace(b))
            {
                Position++;
            }
            else
            {
                break;
            }
        }
    }

    // ── Peek ───────────────────────────────────────────────────────────────

    /// <summary>Returns the byte at the current position without advancing.</summary>
    public byte Peek()
    {
        Require(Position, 1, "Unexpected end of input while peeking.");
        return Span[Position];
    }

    /// <summary>
    /// Returns the byte at the current position without advancing,
    /// or -1 when at end of input.
    /// </summary>
    public int TryPeek() => Position < _data.Length ? Span[Position] : -1;

    // ── Core read helpers ─────────────────────────────────────────────────

    /// <summary>Reads and returns the byte at the current position, advancing by one.</summary>
    public byte ReadByte()
    {
        Require(Position, 1, "Unexpected end of input.");
        return Span[Position++];
    }

    /// <summary>
    /// Returns a memory slice of <paramref name="length"/> bytes starting at
    /// <paramref name="offset"/> without moving the cursor.
    /// </summary>
    public ReadOnlyMemory<byte> Slice(int offset, int length)
    {
        Require(offset, length, "Slice out of range.");
        return _data.Slice(offset, length);
    }

    /// <summary>
    /// Advances the cursor to <paramref name="position"/>.
    /// Must be &gt;= current <see cref="Position"/> and &lt;= <see cref="Length"/>.
    /// </summary>
    public void Seek(int position)
    {
        if (position < 0 || position > _data.Length)
            throw new InvalidDataException(
                $"Seek to {position} is out of range [0, {_data.Length}].");
        Position = position;
    }

    // ── Next token ─────────────────────────────────────────────────────────

    /// <summary>
    /// Skips whitespace/comments, then reads and returns the next token.
    /// Advances <see cref="Position"/> past the token.
    /// </summary>
    /// <exception cref="InvalidDataException">On malformed PDF syntax.</exception>
    public Token NextToken()
    {
        SkipWhitespaceAndComments();
        if (Position >= _data.Length)
            return new Token(TokenKind.EndOfInput, ReadOnlyMemory<byte>.Empty);

        var start = Position;
        var b = Span[Position];

        // Array delimiters
        if (b == (byte)'[') { Position++; return new Token(TokenKind.ArrayBegin, _data.Slice(start, 1)); }
        if (b == (byte)']') { Position++; return new Token(TokenKind.ArrayEnd, _data.Slice(start, 1)); }

        // Dict or hex-string
        if (b == (byte)'<')
        {
            if (Position + 1 < _data.Length && Span[Position + 1] == (byte)'<')
            {
                Position += 2;
                return new Token(TokenKind.DictBegin, _data.Slice(start, 2));
            }
            return ReadHexString(start);
        }

        if (b == (byte)'>')
        {
            if (Position + 1 < _data.Length && Span[Position + 1] == (byte)'>')
            {
                Position += 2;
                return new Token(TokenKind.DictEnd, _data.Slice(start, 2));
            }
            throw new InvalidDataException(
                $"Unexpected '>' at offset {Position} (not part of '>>'); malformed PDF.");
        }

        // Literal string
        if (b == (byte)'(')
            return ReadLiteralString(start);

        // Name
        if (b == (byte)'/')
            return ReadName(start);

        // Numeric: digits, sign, or leading dot
        if (b is (byte)'+' or (byte)'-' or (byte)'.' || (b >= (byte)'0' && b <= (byte)'9'))
            return ReadNumeric(start);

        // Keyword (true, false, null, obj, endobj, stream, endstream, R, xref, trailer, startxref)
        if (IsRegular(b))
            return ReadKeyword(start);

        throw new InvalidDataException(
            $"Unexpected byte 0x{b:X2} at offset {Position}.");
    }

    // ── Token readers ──────────────────────────────────────────────────────

    private Token ReadKeyword(int start)
    {
        while (Position < _data.Length && IsRegular(Span[Position]))
            Position++;
        return new Token(TokenKind.Keyword, _data.Slice(start, Position - start));
    }

    private Token ReadNumeric(int start)
    {
        var isReal = false;

        // optional sign
        if (Position < _data.Length && Span[Position] is (byte)'+' or (byte)'-')
            Position++;

        // leading digits
        while (Position < _data.Length && Span[Position] >= (byte)'0' && Span[Position] <= (byte)'9')
            Position++;

        // optional decimal point + fractional digits
        if (Position < _data.Length && Span[Position] == (byte)'.')
        {
            isReal = true;
            Position++;
            while (Position < _data.Length && Span[Position] >= (byte)'0' && Span[Position] <= (byte)'9')
                Position++;
        }

        var raw = _data.Slice(start, Position - start);
        return new Token(isReal ? TokenKind.Real : TokenKind.Integer, raw);
    }

    private Token ReadName(int start)
    {
        Position++; // consume '/'
        while (Position < _data.Length && IsRegular(Span[Position]))
            Position++;
        return new Token(TokenKind.Name, _data.Slice(start, Position - start));
    }

    private Token ReadLiteralString(int start)
    {
        Position++; // consume '('
        var depth = 1;
        while (Position < _data.Length)
        {
            var c = Span[Position++];
            if (c == (byte)'\\')
            {
                // skip one char (the escaped character or first octal digit, etc.)
                // full decode is done in PdfObjectParser; here we just balance parens
                if (Position < _data.Length)
                    Position++;
            }
            else if (c == (byte)'(')
            {
                depth++;
            }
            else if (c == (byte)')')
            {
                depth--;
                if (depth == 0)
                    break;
            }
        }
        if (depth != 0)
            throw new InvalidDataException(
                $"Unterminated literal string starting at offset {start}.");
        return new Token(TokenKind.LiteralString, _data.Slice(start, Position - start));
    }

    private Token ReadHexString(int start)
    {
        Position++; // consume '<'
        while (Position < _data.Length && Span[Position] != (byte)'>')
            Position++;
        if (Position >= _data.Length)
            throw new InvalidDataException(
                $"Unterminated hex string starting at offset {start}.");
        Position++; // consume '>'
        return new Token(TokenKind.HexString, _data.Slice(start, Position - start));
    }

    // ── Guard ──────────────────────────────────────────────────────────────

    private void Require(int offset, int count, string message)
    {
        if (offset < 0 || count < 0 || (long)offset + count > _data.Length)
            throw new InvalidDataException(
                $"{message} Needed {count} byte(s) at offset {offset}; buffer length is {_data.Length}.");
    }
}
