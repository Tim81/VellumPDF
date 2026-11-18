// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// A bounded reader for the <c>/ToUnicode</c> CMap of ISO 32000-2 §9.10.3, used by
/// <see cref="StructureDumper"/> to print the text a show operator draws.
/// </summary>
/// <remarks>
/// <b>Only <c>bfchar</c> and <c>bfrange</c> sections are read.</b> Everything else in the CMap
/// program (<c>codespacerange</c>, <c>usecmap</c>, <c>cidrange</c>, procedure bodies) is skipped. A
/// code is looked up by its numeric value and its byte length together, so a one-byte entry
/// <c>&lt;41&gt;</c> does not answer for the two-byte code <c>0x0041</c>; the codespace itself is not
/// consulted. Lookup is total: <see cref="TryMap"/> returns <see langword="false"/> for a code no
/// entry covers and never throws, whatever the range bounds. A section that stops making sense (a
/// token that is neither a hex string nor the expected keyword) ends that section, and parsing
/// resumes at the next section keyword.
/// <para>
/// <b>Not bounded by a count of its own.</b> The entry count is at most the number of tokens in the
/// stream, and the stream is the decoded <c>/ToUnicode</c> body the Reader already capped. A
/// <c>bfrange</c> is stored as one range and never expanded, so a range over four-byte codes costs
/// one entry, not 2^32; lookup is a binary search over ranges sorted by their lower bound.
/// </para>
/// <para>
/// <b>Do not rely on which of two overlapping definitions wins.</b> ISO 32000-2 does not say. Here a
/// repeated <c>bfchar</c> for one code is later-wins, a <c>bfchar</c> beats any range, and among
/// overlapping ranges the one that appears first in the file wins.
/// </para>
/// </remarks>
internal sealed class ToUnicodeMap
{
    private readonly Dictionary<(int Length, uint Code), string> _chars = [];
    private readonly List<RangeEntry> _ranges = [];
    private RangeEntry[]? _sorted;
    private uint[]? _prefixMaxHi;

    private readonly record struct RangeEntry(int Length, uint Lo, uint Hi, string Dst, string[]? Array, int Order);

    /// <summary>Reads <paramref name="data"/> as a CMap program. Never throws on malformed input.</summary>
    internal static ToUnicodeMap Parse(ReadOnlySpan<byte> data)
    {
        var map = new ToUnicodeMap();
        var tokens = Tokenize(Encoding.Latin1.GetString(data));
        var i = 0;
        while (i < tokens.Count)
        {
            var t = tokens[i++];
            if (t == "beginbfchar")
            {
                while (i + 1 < tokens.Count && IsHex(tokens[i]) && IsHex(tokens[i + 1]))
                {
                    if (TryCode(tokens[i], out var code, out var len))
                        map._chars[(len, code)] = Utf16(tokens[i + 1]);
                    i += 2;
                }
            }
            else if (t == "beginbfrange")
            {
                while (i + 2 < tokens.Count && IsHex(tokens[i]) && IsHex(tokens[i + 1]))
                {
                    var ok = TryCode(tokens[i], out var lo, out var len) & TryCode(tokens[i + 1], out var hi, out _);
                    var order = map._ranges.Count;
                    if (IsHex(tokens[i + 2]))
                    {
                        if (ok && lo <= hi)
                            map._ranges.Add(new RangeEntry(len, lo, hi, Utf16(tokens[i + 2]), null, order));
                        i += 3;
                    }
                    else if (tokens[i + 2] == "[")
                    {
                        var items = new List<string>();
                        var j = i + 3;
                        while (j < tokens.Count && IsHex(tokens[j]))
                            items.Add(Utf16(tokens[j++]));
                        if (ok && lo <= hi)
                            map._ranges.Add(new RangeEntry(len, lo, hi, "", [.. items], order));
                        i = j < tokens.Count && tokens[j] == "]" ? j + 1 : j;
                    }
                    else
                    {
                        break;
                    }
                }
            }
        }

        return map;
    }

    /// <summary>The Unicode text the <paramref name="byteLength"/>-byte <paramref name="code"/> maps to, if an entry covers it.</summary>
    internal bool TryMap(uint code, int byteLength, out string text)
    {
        if (_chars.TryGetValue((byteLength, code), out text!))
            return true;

        if (_sorted is null)
        {
            _sorted = [.. _ranges.OrderBy(r => r.Length).ThenBy(r => r.Lo).ThenBy(r => r.Order)];
            _prefixMaxHi = new uint[_sorted.Length];
            for (var k = 0; k < _sorted.Length; k++)
            {
                var prev = k > 0 && _sorted[k - 1].Length == _sorted[k].Length ? _prefixMaxHi[k - 1] : 0;
                _prefixMaxHi[k] = Math.Max(prev, _sorted[k].Hi);
            }
        }

        // Last range of this length whose lower bound is <= code.
        int lo = 0, hi = _sorted.Length - 1, at = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            var r = _sorted[mid];
            if (r.Length < byteLength || (r.Length == byteLength && r.Lo <= code))
            {
                at = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        // Walk back while some earlier range of the same length can still reach the code, and keep
        // the first-in-file one among those that cover it.
        RangeEntry? best = null;
        for (var k = at; k >= 0 && _sorted[k].Length == byteLength && _prefixMaxHi![k] >= code; k--)
        {
            var r = _sorted[k];
            if (code < r.Lo || code > r.Hi)
                continue;
            var offset = (long)code - r.Lo;
            if (r.Array is not null ? offset >= r.Array.Length : r.Dst.Length == 0)
                continue;
            if (best is null || r.Order < best.Value.Order)
                best = r;
        }

        if (best is { } b)
        {
            var offset = (long)code - b.Lo;
            text = b.Array is not null
                ? b.Array[offset]
                : b.Dst[..^1] + (char)((b.Dst[^1] + offset) & 0xFFFF);
            return true;
        }

        text = "";
        return false;
    }

    private static bool IsHex(string token) => token.Length >= 2 && token[0] == '<';

    private static bool TryCode(string hexToken, out uint code, out int length)
    {
        var bytes = HexBytes(hexToken);
        code = 0;
        length = bytes.Length;
        if (bytes.Length is 0 or > 4)
            return false;
        foreach (var b in bytes)
            code = (code << 8) | b;
        return true;
    }

    private static string Utf16(string hexToken)
    {
        var bytes = HexBytes(hexToken);
        var sb = new StringBuilder(bytes.Length / 2 + 1);
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            sb.Append((char)((bytes[i] << 8) | bytes[i + 1]));
        if (bytes.Length % 2 == 1)
            sb.Append('�'); // an odd trailing byte is not a UTF-16 code unit
        return sb.ToString();
    }

    private static byte[] HexBytes(string hexToken)
    {
        var digits = new List<int>();
        foreach (var c in hexToken)
        {
            if (Uri.IsHexDigit(c))
                digits.Add(int.Parse(c.ToString(), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        if (digits.Count % 2 == 1)
            digits.Add(0);
        var bytes = new byte[digits.Count / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = (byte)((digits[2 * i] << 4) | digits[2 * i + 1]);
        return bytes;
    }

    // Hex strings come back whole, "<...>", with their angle brackets; "[" and "]" are tokens of
    // their own; a "(...)" literal and a "%" comment are skipped; every other run is a bare word.
    private static List<string> Tokenize(string s)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == '\0')
            {
                i++;
            }
            else if (c == '%')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r')
                    i++;
            }
            else if (c == '<' && i + 1 < s.Length && s[i + 1] == '<')
            {
                i += 2; // dictionary open, e.g. "<</CIDSystemInfo ...>>": never a hex string
            }
            else if (c == '<' && i + 1 < s.Length)
            {
                var end = s.IndexOf('>', i);
                if (end < 0)
                    break;
                tokens.Add(s[i..(end + 1)]);
                i = end + 1;
            }
            else if (c == '(')
            {
                var depth = 0;
                while (i < s.Length)
                {
                    if (s[i] == '\\')
                        i++;
                    else if (s[i] == '(')
                        depth++;
                    else if (s[i] == ')' && --depth == 0)
                        break;
                    i++;
                }

                i++;
            }
            else if (c is '[' or ']')
            {
                tokens.Add(c.ToString());
                i++;
            }
            else
            {
                var start = i;
                while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('<' or '[' or ']' or '(' or '%'))
                    i++;
                if (i == start)
                    i++;
                else
                    tokens.Add(s[start..i]);
            }
        }

        return tokens;
    }
}
