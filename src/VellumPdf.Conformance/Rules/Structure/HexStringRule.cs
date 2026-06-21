// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.6 (String objects). A hexadecimal string shall contain an even number of
/// non-white-space characters (§6.1.6-1) and shall be written using only the hexadecimal digits
/// <c>0–9</c>, <c>A–F</c>, and <c>a–f</c> (§6.1.6-2).
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.6 and ISO 32000-1:2008, 7.3.4.3. Clean-room: derived from the
/// specification text, not from any third-party validation profile. The parsed object graph does not
/// preserve a hexadecimal string's raw token (the reader pads an odd-length string), so this rule
/// scans the file bytes. To avoid misreading binary content, the scan masks out every stream body
/// (located by <c>ParsedStream.BodyOffset</c> and its length — e.g. an XMP packet, full of
/// <c>&lt;…&gt;</c> markup, is skipped), steps over literal strings <c>(…)</c> (so a <c>&lt;</c> inside
/// one is not taken for a hex string), and treats <c>&lt;&lt;</c> as a dictionary delimiter rather than
/// a hex string — so it inspects only genuine hexadecimal-string tokens.
/// </remarks>
internal sealed class HexStringRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.6-hex-string";

    public string Clause => "ISO 19005-2:2011, 6.1.6";

    public void Evaluate(PreflightContext context)
    {
        var bytes = context.FileBytes.Span;
        var streams = StreamRegions(bytes);
        var streamIndex = 0;

        var i = 0;
        while (i < bytes.Length)
        {
            // Skip over any stream body that begins at or before i.
            while (streamIndex < streams.Count && i >= streams[streamIndex].Start)
            {
                if (i < streams[streamIndex].End)
                {
                    i = streams[streamIndex].End;
                    goto continueOuter;
                }
                streamIndex++;
            }

            var c = bytes[i];
            if (c == (byte)'(')
            {
                i = SkipLiteralString(bytes, i);
                continue;
            }
            if (c == (byte)'<')
            {
                if (i + 1 < bytes.Length && bytes[i + 1] == (byte)'<')
                {
                    i += 2; // dictionary opener, not a hex string.
                    continue;
                }
                if (ScanHexString(context, bytes, i, out var next))
                    return; // reported; one finding suffices.
                i = next;
                continue;
            }
            i++;
        continueOuter:;
        }
    }

    // Reads a hexadecimal string starting at the '<' at <paramref name="open"/>. Sets
    // <paramref name="next"/> to the index after the closing '>'. Returns true when a violation was
    // reported.
    private bool ScanHexString(PreflightContext context, ReadOnlySpan<byte> bytes, int open, out int next)
    {
        var count = 0;
        var allHex = true;
        var j = open + 1;
        while (j < bytes.Length && bytes[j] != (byte)'>')
        {
            var b = bytes[j];
            if (!IsWhite(b))
            {
                count++;
                if (!IsHexDigit(b))
                    allHex = false;
            }
            j++;
        }

        next = j < bytes.Length ? j + 1 : j;
        if (j >= bytes.Length)
            return false; // no closing '>': not a complete hex-string token, leave it alone.

        if (!allHex)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A hexadecimal string contains a character that is not a hexadecimal digit (§6.1.6).");
            return true;
        }
        if (count % 2 != 0)
        {
            context.Report(RuleId, Clause, PreflightSeverity.Error,
                "A hexadecimal string has an odd number of non-white-space characters (§6.1.6).");
            return true;
        }
        return false;
    }

    // Returns the index just past the closing ')' of the literal string starting at <paramref
    // name="open"/>, honouring backslash escapes and balanced parentheses.
    private static int SkipLiteralString(ReadOnlySpan<byte> bytes, int open)
    {
        var depth = 0;
        var j = open;
        while (j < bytes.Length)
        {
            var c = bytes[j];
            if (c == (byte)'\\')
            {
                j += 2; // an escaped character (and its backslash) is literal.
                continue;
            }
            if (c == (byte)'(')
                depth++;
            else if (c == (byte)')')
            {
                depth--;
                if (depth == 0)
                    return j + 1;
            }
            j++;
        }
        return j;
    }

    private static readonly byte[] _streamKw = "stream"u8.ToArray();
    private static readonly byte[] _endstreamKw = "endstream"u8.ToArray();

    // Every stream body region in the file, found by scanning the 'stream'/'endstream' keyword pairs
    // directly in the bytes — not via the cross-reference table — so that bodies of superseded objects
    // left behind by an incremental update (whose markup must equally not be read as hex strings) are
    // masked too.
    private static List<(int Start, int End)> StreamRegions(ReadOnlySpan<byte> bytes)
    {
        var regions = new List<(int Start, int End)>();
        var i = 0;
        while (i <= bytes.Length - _streamKw.Length)
        {
            if (!IsStreamKeyword(bytes, i))
            {
                i++;
                continue;
            }
            // Body begins after the EOL that follows the keyword.
            var body = i + _streamKw.Length;
            if (body < bytes.Length && bytes[body] == (byte)'\r')
                body++;
            if (body < bytes.Length && bytes[body] == (byte)'\n')
                body++;
            var end = IndexOf(bytes, _endstreamKw, body);
            if (end < 0)
                break;
            regions.Add((body, end));
            i = end + _endstreamKw.Length;
        }
        return regions;
    }

    // A standalone 'stream' keyword (preceded by whitespace or '>' — not the tail of 'endstream' — and
    // followed by an EOL), which introduces a stream body.
    private static bool IsStreamKeyword(ReadOnlySpan<byte> bytes, int at)
    {
        if (!bytes.Slice(at, _streamKw.Length).SequenceEqual(_streamKw))
            return false;
        if (at > 0 && bytes[at - 1] is not ((byte)'>' or (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0))
            return false; // e.g. the 'stream' inside 'endstream'.
        var after = at + _streamKw.Length;
        return after < bytes.Length && bytes[after] is (byte)'\r' or (byte)'\n';
    }

    private static int IndexOf(ReadOnlySpan<byte> haystack, byte[] needle, int from)
    {
        var rel = haystack[from..].IndexOf(needle);
        return rel < 0 ? -1 : from + rel;
    }

    private static bool IsHexDigit(byte b)
        => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'F' or >= (byte)'a' and <= (byte)'f';

    private static bool IsWhite(byte b)
        => b is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or (byte)'\f' or 0;
}
