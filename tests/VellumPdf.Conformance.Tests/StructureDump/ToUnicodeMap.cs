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
/// program (<c>codespacerange</c>, <c>usecmap</c>, <c>cidrange</c>, procedure bodies) is skipped, so a
/// source code is looked up by its numeric value alone and the codespace is not consulted. Lookup is
/// total: a code that no entry covers returns <see langword="false"/>, it does not throw. A section
/// that stops making sense (a token that is neither a hex string nor the expected keyword) ends that
/// section, and parsing resumes at the next section keyword.
/// <para>
/// <b>Not bounded by a count of its own.</b> The entry count is at most the number of tokens in the
/// stream, and the stream is the decoded <c>/ToUnicode</c> body the Reader already capped. A
/// <c>bfrange</c> is stored as one range and never expanded, so a range over four-byte codes costs
/// one entry, not 2^32.
/// </para>
/// <para>
/// <b>Do not rely on later-wins.</b> When two entries cover one code the single-code
/// <c>bfchar</c> entries are tried first, then ranges in file order; ISO 32000-2 does not say which
/// of two overlapping definitions wins.
/// </para>
/// </remarks>
internal sealed class ToUnicodeMap
{
    private readonly Dictionary<uint, string> _chars = [];
    private readonly List<(uint Lo, uint Hi, string Dst, string[]? Array)> _ranges = [];

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
                    if (TryCode(tokens[i], out var code))
                        map._chars[code] = Utf16(tokens[i + 1]);
                    i += 2;
                }
            }
            else if (t == "beginbfrange")
            {
                while (i + 2 < tokens.Count && IsHex(tokens[i]) && IsHex(tokens[i + 1]))
                {
                    var ok = TryCode(tokens[i], out var lo) & TryCode(tokens[i + 1], out var hi);
                    if (IsHex(tokens[i + 2]))
                    {
                        if (ok && lo <= hi)
                            map._ranges.Add((lo, hi, Utf16(tokens[i + 2]), null));
                        i += 3;
                    }
                    else if (tokens[i + 2] == "[")
                    {
                        var items = new List<string>();
                        var j = i + 3;
                        while (j < tokens.Count && IsHex(tokens[j]))
                            items.Add(Utf16(tokens[j++]));
                        if (ok && lo <= hi)
                            map._ranges.Add((lo, hi, "", [.. items]));
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

    /// <summary>The Unicode text <paramref name="code"/> maps to, if any entry covers it.</summary>
    internal bool TryMap(uint code, out string text)
    {
        if (_chars.TryGetValue(code, out text!))
            return true;

        foreach (var (lo, hi, dst, array) in _ranges)
        {
            if (code < lo || code > hi)
                continue;
            var offset = (int)(code - lo);
            if (array is not null)
            {
                if (offset >= array.Length)
                    continue;
                text = array[offset];
                return true;
            }

            if (dst.Length == 0)
                continue;
            text = dst[..^1] + (char)(ushort)(dst[^1] + offset);
            return true;
        }

        text = "";
        return false;
    }

    private static bool IsHex(string token) => token.Length >= 2 && token[0] == '<';

    private static bool TryCode(string hexToken, out uint code)
    {
        var bytes = HexBytes(hexToken);
        code = 0;
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
            sb.Append((char)bytes[^1]);
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
