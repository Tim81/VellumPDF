// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// Parses the PostScript CMap program embedded in a Type 0 font's /Encoding stream into a
/// lookup structure for byte→CID resolution. Shared by <see cref="CidRangeRule"/> (§6.1.13-10)
/// and <see cref="GlyphPresenceRule"/> (§6.2.11.4.1/§6.2.11.5 non-Identity paths).
/// </summary>
/// <remarks>
/// Authored from ISO 32000-1:2008, §9.7.5 and §9.7.6. Clean-room: derived from the specification
/// text. Returns null on any parse failure — never throws, never emits a spurious finding.
/// </remarks>
internal static class EmbeddedCMapParser
{
    /// <summary>
    /// Parses <paramref name="bytes"/> as a PostScript CMap program and returns a
    /// <see cref="ParsedCMap"/>, or <see langword="null"/> if the bytes are malformed.
    /// </summary>
    public static ParsedCMap? Parse(byte[] bytes)
    {
        var cidRanges = new List<CidRange>();
        var cidChars = new List<CidChar>();
        var codespaces = new List<Codespace>();

        try
        {
            var mem = new ReadOnlyMemory<byte>(bytes);
            var lexer = new PdfLexer(mem);

            while (!lexer.AtEnd)
            {
                var token = lexer.NextToken();
                if (token.Kind == TokenKind.EndOfInput)
                    break;
                if (token.Kind != TokenKind.Keyword)
                    continue;

                var kw = Encoding.Latin1.GetString(token.Raw.Span);

                if (kw == "begincodespacerange")
                    ParseCodespaces(lexer, codespaces);
                else if (kw == "begincidrange")
                    ParseCidRanges(lexer, cidRanges);
                else if (kw == "begincidchar")
                    ParseCidChars(lexer, cidChars);
            }
        }
        catch
        {
            return null;
        }

        return new ParsedCMap(cidRanges, cidChars, codespaces);
    }

    private static void ParseCodespaces(PdfLexer lexer, List<Codespace> codespaces)
    {
        while (!lexer.AtEnd)
        {
            var tok = lexer.NextToken();
            if (tok.Kind == TokenKind.EndOfInput)
                return;
            if (tok.Kind == TokenKind.Keyword
                && Encoding.Latin1.GetString(tok.Raw.Span) == "endcodespacerange")
                return;
            if (tok.Kind != TokenKind.HexString)
                return;

            var lo = HexStringToBytes(tok.Raw.Span);
            var t2 = lexer.NextToken();
            if (t2.Kind != TokenKind.HexString)
                return;
            var hi = HexStringToBytes(t2.Raw.Span);

            if (lo.Length > 0 && lo.Length == hi.Length)
                codespaces.Add(new Codespace(lo, hi));
        }
    }

    private static void ParseCidRanges(PdfLexer lexer, List<CidRange> ranges)
    {
        while (!lexer.AtEnd)
        {
            var tok = lexer.NextToken();
            if (tok.Kind == TokenKind.EndOfInput)
                return;
            if (tok.Kind == TokenKind.Keyword
                && Encoding.Latin1.GetString(tok.Raw.Span) == "endcidrange")
                return;
            if (tok.Kind != TokenKind.HexString)
                return;

            var srcLo = HexStringToInt(tok.Raw.Span);
            var t2 = lexer.NextToken();
            if (t2.Kind != TokenKind.HexString)
                return;
            var srcHi = HexStringToInt(t2.Raw.Span);
            var t3 = lexer.NextToken();
            if (t3.Kind != TokenKind.Integer)
                return;
            var dstStart = ParseInt(t3.Raw.Span);

            if (srcLo >= 0 && srcHi >= 0 && dstStart >= 0)
                ranges.Add(new CidRange(srcLo, srcHi, dstStart));
        }
    }

    private static void ParseCidChars(PdfLexer lexer, List<CidChar> chars)
    {
        while (!lexer.AtEnd)
        {
            var tok = lexer.NextToken();
            if (tok.Kind == TokenKind.EndOfInput)
                return;
            if (tok.Kind == TokenKind.Keyword
                && Encoding.Latin1.GetString(tok.Raw.Span) == "endcidchar")
                return;
            if (tok.Kind != TokenKind.HexString)
                return;

            var src = HexStringToInt(tok.Raw.Span);
            var t2 = lexer.NextToken();
            if (t2.Kind != TokenKind.Integer)
                return;
            var dst = ParseInt(t2.Raw.Span);

            if (src >= 0 && dst >= 0)
                chars.Add(new CidChar(src, dst));
        }
    }

    private static int HexStringToInt(ReadOnlySpan<byte> raw)
    {
        var result = 0;
        for (var i = 1; i < raw.Length && raw[i] != (byte)'>'; i++)
        {
            var v = Hex(raw[i]);
            if (v < 0)
                continue;
            result = (result << 4) | v;
            if (result > 0x1FFFF)
                return -1;
        }
        return result;
    }

    private static byte[] HexStringToBytes(ReadOnlySpan<byte> raw)
    {
        var bytes = new List<byte>(raw.Length / 2);
        var hi = -1;
        for (var i = 1; i < raw.Length && raw[i] != (byte)'>'; i++)
        {
            var v = Hex(raw[i]);
            if (v < 0)
                continue;
            if (hi < 0)
                hi = v;
            else
            {
                bytes.Add((byte)((hi << 4) | v));
                hi = -1;
            }
        }
        if (hi >= 0)
            bytes.Add((byte)(hi << 4));
        return bytes.ToArray();
    }

    private static int ParseInt(ReadOnlySpan<byte> raw)
    {
        if (!int.TryParse(Encoding.Latin1.GetString(raw), out var v))
            return -1;
        return v < 0 ? -1 : v;
    }

    private static int Hex(byte b) => b switch
    {
        >= (byte)'0' and <= (byte)'9' => b - '0',
        >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
        _ => -1,
    };
}

/// <summary>
/// A parsed subset of a CMap program: the codespace, cidrange, and cidchar sections.
/// </summary>
internal sealed class ParsedCMap(
    IReadOnlyList<CidRange> ranges, IReadOnlyList<CidChar> chars, IReadOnlyList<Codespace> codespaces)
{
    private readonly IReadOnlyList<CidRange> _ranges = ranges;
    private readonly IReadOnlyList<CidChar> _chars = chars;
    private readonly IReadOnlyList<Codespace> _codespaces = codespaces;

    /// <summary>
    /// Splits a CMap-encoded byte string into character-code integer values using the declared
    /// codespace ranges (ISO 32000-1 §9.7.6.2). An unmatched lead byte is skipped by the
    /// shortest codespace length without yielding a code — invalid bytes never fabricate codes.
    /// When no codespace is declared nothing is yielded (a malformed CMap is always safe).
    /// </summary>
    public IEnumerable<int> DecodeCodes(byte[] bytes)
    {
        if (_codespaces.Count == 0)
            yield break;

        var minLen = int.MaxValue;
        foreach (var cs in _codespaces)
            if (cs.Length < minLen)
                minLen = cs.Length;

        var i = 0;
        while (i < bytes.Length)
        {
            var matched = false;
            foreach (var cs in _codespaces)
            {
                if (i + cs.Length <= bytes.Length && cs.Matches(bytes, i))
                {
                    var code = 0;
                    for (var k = 0; k < cs.Length; k++)
                        code = (code << 8) | bytes[i + k];
                    yield return code;
                    i += cs.Length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
                i += minLen;
        }
    }

    /// <summary>
    /// Returns the CID for <paramref name="charCode"/> from the first matching cidrange or
    /// cidchar entry, or <see langword="false"/> when no mapping covers it. Ranges are checked
    /// before individual chars, matching PostScript CMap lookup order.
    /// </summary>
    public bool TryLookupCid(int charCode, out int cid)
    {
        foreach (var r in _ranges)
        {
            if (charCode >= r.SrcLo && charCode <= r.SrcHi)
            {
                cid = r.DstStart + (charCode - r.SrcLo);
                return true;
            }
        }
        foreach (var c in _chars)
        {
            if (c.Src == charCode)
            {
                cid = c.Dst;
                return true;
            }
        }
        cid = 0;
        return false;
    }
}

/// <summary>
/// One <c>begincodespacerange</c> entry: byte sequences of length Lo.Length whose every byte k
/// lies in [Lo[k], Hi[k]] are valid character codes of that length.
/// </summary>
internal sealed class Codespace(byte[] lo, byte[] hi)
{
    private readonly byte[] _lo = lo;
    private readonly byte[] _hi = hi;

    public int Length => _lo.Length;

    public bool Matches(byte[] bytes, int off)
    {
        for (var k = 0; k < _lo.Length; k++)
        {
            var b = bytes[off + k];
            if (b < _lo[k] || b > _hi[k])
                return false;
        }
        return true;
    }
}

/// <summary>One <c>begincidrange</c> entry.</summary>
internal readonly struct CidRange(int srcLo, int srcHi, int dstStart)
{
    public int SrcLo { get; } = srcLo;
    public int SrcHi { get; } = srcHi;
    public int DstStart { get; } = dstStart;
}

/// <summary>One <c>begincidchar</c> entry.</summary>
internal readonly struct CidChar(int src, int dst)
{
    public int Src { get; } = src;
    public int Dst { get; } = dst;
}
