// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// A small, independent reader for the files the conformance audit saves. It does not use the
/// library's own Reader: the audit asserts what the writer put in the bytes, so the scanner reads
/// only the syntax of ISO 32000-1, clause 7, and keeps each token as it was written (a number's
/// text, a name before and after <c>#</c> expansion, a string's length in bytes). It handles the
/// classic cross-reference table, a cross-reference stream with object streams, and the linearized
/// layout, which are the shapes <c>PdfDocument.Save</c> and <c>PrepareForSigning</c> write.
/// </summary>
internal static class AuditScanner
{
    internal sealed record PName(string Raw, byte[] Bytes)
    {
        internal string Text => Encoding.Latin1.GetString(Bytes);
    }

    internal sealed record PNum(string Raw, bool IsReal);

    internal sealed record PStr(byte[] Bytes, bool IsHex, int HexDigits);

    internal sealed record PRef(int Number, int Generation);

    internal sealed record PKeyword(string Text);

    internal sealed class PDict
    {
        internal List<(string Key, object? Value)> Entries { get; } = [];
        internal List<string> DuplicateKeys { get; } = [];

        internal bool Has(string key) => Entries.Any(e => e.Key == key);

        internal object? this[string key] => Entries.FirstOrDefault(e => e.Key == key).Value;

        internal string? Name(string key) => this[key] is PName n ? n.Text : null;

        internal long? Int(string key) =>
            this[key] is PNum { IsReal: false } n ? long.Parse(n.Raw, System.Globalization.CultureInfo.InvariantCulture) : null;

        internal PDict? Dict(string key) => this[key] as PDict;

        internal List<object?>? Array(string key) => this[key] as List<object?>;
    }

    /// <summary>One token as written: kind N name, I integer, R real, S string, K keyword or delimiter.</summary>
    internal readonly record struct Tok(char Kind, string Raw, int Length, int HexDigits = 0);

    internal sealed class Lexer(byte[] bytes, int pos)
    {
        internal int Pos = pos;
        internal List<Tok>? Inventory;

        private static bool IsWs(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;

        private static bool IsDelim(byte c) =>
            c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

        internal bool AtEnd => Pos >= bytes.Length;

        internal int Length => bytes.Length;

        internal byte ByteAt(int i) => i < bytes.Length ? bytes[i] : (byte)0;

        internal byte[] Slice(int start, int length) => bytes.AsSpan(start, length).ToArray();

        internal int IndexOf(string s, int from) => bytes.AsSpan(from).IndexOf(Encoding.ASCII.GetBytes(s)) + from;

        internal void SkipWsNoComment()
        {
            while (Pos < bytes.Length && IsWs(bytes[Pos]))
                Pos++;
        }

        internal void SkipWs()
        {
            while (Pos < bytes.Length)
            {
                if (IsWs(bytes[Pos]))
                    Pos++;
                else if (bytes[Pos] == (byte)'%')
                {
                    while (Pos < bytes.Length && bytes[Pos] is not (10 or 13))
                        Pos++;
                }
                else
                    break;
            }
        }

        internal bool StartsWith(string s)
        {
            if (Pos + s.Length > bytes.Length)
                return false;
            for (var i = 0; i < s.Length; i++)
                if (bytes[Pos + i] != s[i])
                    return false;
            return true;
        }

        /// <summary>The next token. A name, number, string or keyword is one token; <c>&lt;&lt;</c>, <c>&gt;&gt;</c>, <c>[</c> and <c>]</c> are keywords.</summary>
        internal object ReadToken()
        {
            SkipWs();
            var c = bytes[Pos];
            if (c == (byte)'/')
            {
                var start = ++Pos;
                var expanded = new List<byte>();
                while (Pos < bytes.Length && !IsWs(bytes[Pos]) && !IsDelim(bytes[Pos]))
                {
                    if (bytes[Pos] == (byte)'#')
                    {
                        expanded.Add(Convert.ToByte(Encoding.ASCII.GetString(bytes, Pos + 1, 2), 16));
                        Pos += 3;
                    }
                    else
                        expanded.Add(bytes[Pos++]);
                }
                var raw = Encoding.Latin1.GetString(bytes, start, Pos - start);
                Inventory?.Add(new Tok('N', raw, expanded.Count));
                return new PName(raw, [.. expanded]);
            }
            if (c == (byte)'(')
            {
                var s = ReadLiteral();
                Inventory?.Add(new Tok('S', "(", s.Bytes.Length));
                return s;
            }
            if (c == (byte)'<' && Pos + 1 < bytes.Length && bytes[Pos + 1] == (byte)'<')
            {
                Pos += 2;
                return new PKeyword("<<");
            }
            if (c == (byte)'>' && Pos + 1 < bytes.Length && bytes[Pos + 1] == (byte)'>')
            {
                Pos += 2;
                return new PKeyword(">>");
            }
            if (c == (byte)'<')
            {
                var close = Array.IndexOf(bytes, (byte)'>', Pos);
                var digits = new StringBuilder();
                for (var i = Pos + 1; i < close; i++)
                    if (!IsWs(bytes[i]))
                        digits.Append((char)bytes[i]);
                Pos = close + 1;
                var d = digits.ToString();
                var padded = d.Length % 2 == 0 ? d : d + "0";
                var s = new PStr(Convert.FromHexString(padded), true, d.Length);
                Inventory?.Add(new Tok('S', "<", s.Bytes.Length, d.Length));
                return s;
            }
            if (c is (byte)'[' or (byte)']' or (byte)'{' or (byte)'}')
            {
                Pos++;
                return new PKeyword(((char)c).ToString());
            }
            if (c == (byte)')' || c == (byte)'>')
            {
                Pos++;
                return new PKeyword(((char)c).ToString());
            }
            var begin = Pos;
            while (Pos < bytes.Length && !IsWs(bytes[Pos]) && !IsDelim(bytes[Pos]))
                Pos++;
            var text = Encoding.Latin1.GetString(bytes, begin, Pos - begin);
            if (Regex.IsMatch(text, @"^[+-]?\d+$"))
            {
                Inventory?.Add(new Tok('I', text, 0));
                return new PNum(text, false);
            }
            if (Regex.IsMatch(text, @"^[+-]?(\d+\.\d*|\.\d+)$"))
            {
                Inventory?.Add(new Tok('R', text, 0));
                return new PNum(text, true);
            }
            return new PKeyword(text);
        }

        private PStr ReadLiteral()
        {
            Pos++;
            var depth = 1;
            var o = new List<byte>();
            while (Pos < bytes.Length)
            {
                var b = bytes[Pos++];
                if (b == (byte)'\\')
                {
                    var e = bytes[Pos++];
                    switch (e)
                    {
                        case (byte)'n': o.Add(10); break;
                        case (byte)'r': o.Add(13); break;
                        case (byte)'t': o.Add(9); break;
                        case (byte)'b': o.Add(8); break;
                        case (byte)'f': o.Add(12); break;
                        case (byte)'(' or (byte)')' or (byte)'\\': o.Add(e); break;
                        case 13:
                            if (Pos < bytes.Length && bytes[Pos] == 10)
                                Pos++;
                            break;
                        case 10: break;
                        case >= (byte)'0' and <= (byte)'7':
                            var v = e - '0';
                            for (var k = 0; k < 2 && Pos < bytes.Length && bytes[Pos] is >= (byte)'0' and <= (byte)'7'; k++)
                                v = v * 8 + (bytes[Pos++] - '0');
                            o.Add((byte)v);
                            break;
                        default: o.Add(e); break;
                    }
                }
                else if (b == (byte)'(')
                {
                    depth++;
                    o.Add(b);
                }
                else if (b == (byte)')')
                {
                    if (--depth == 0)
                        break;
                    o.Add(b);
                }
                else if (b == 13)
                {
                    if (Pos < bytes.Length && bytes[Pos] == 10)
                        Pos++;
                    o.Add(10);
                }
                else
                    o.Add(b);
            }
            return new PStr([.. o], false, 0);
        }

        internal object? ReadValue()
        {
            var t = ReadToken();
            switch (t)
            {
                case PKeyword { Text: "<<" }:
                    var d = new PDict();
                    while (true)
                    {
                        SkipWs();
                        if (StartsWith(">>"))
                        {
                            Pos += 2;
                            return d;
                        }
                        var k = ReadToken();
                        var key = ((PName)k).Text;
                        var v = ReadValue();
                        if (d.Has(key))
                            d.DuplicateKeys.Add(key);
                        d.Entries.Add((key, v));
                    }
                case PKeyword { Text: "[" }:
                    var a = new List<object?>();
                    while (true)
                    {
                        SkipWs();
                        if (bytes[Pos] == (byte)']')
                        {
                            Pos++;
                            return a;
                        }
                        a.Add(ReadValue());
                    }
                case PNum { IsReal: false } n:
                    var save = Pos;
                    var invSave = Inventory?.Count ?? 0;
                    SkipWs();
                    if (Pos < bytes.Length && bytes[Pos] is >= (byte)'0' and <= (byte)'9')
                    {
                        var second = ReadToken();
                        SkipWs();
                        if (second is PNum { IsReal: false } g && Pos < bytes.Length && bytes[Pos] == (byte)'R'
                            && (Pos + 1 >= bytes.Length || IsWs(bytes[Pos + 1]) || IsDelim(bytes[Pos + 1])))
                        {
                            Pos++;
                            if (Inventory is not null)
                                Inventory.RemoveRange(invSave - 1, Inventory.Count - invSave + 1);
                            return new PRef(int.Parse(n.Raw), int.Parse(g.Raw));
                        }
                    }
                    Pos = save;
                    if (Inventory is not null && Inventory.Count > invSave)
                        Inventory.RemoveRange(invSave, Inventory.Count - invSave);
                    return n;
                case PKeyword { Text: "null" }:
                    return null;
                case PKeyword { Text: "true" }:
                    return true;
                case PKeyword { Text: "false" }:
                    return false;
                default:
                    return t;
            }
        }
    }

    internal sealed class Obj
    {
        internal int Number;
        internal int Generation;
        internal long Offset;
        internal long KeywordEnd;
        internal object? Value;
        internal byte[]? StreamData;
        internal long StreamOffset;
        internal bool InObjectStream;
        internal string Raw = "";
        internal PDict? Dict => Value as PDict;
    }

    internal sealed class XrefEntry
    {
        internal int Number;
        internal long Offset;
        internal int Generation;
        internal bool InUse;
        internal string Raw = "";
    }

    internal sealed class XrefSection
    {
        internal long Offset;
        internal List<(int First, int Count, string HeaderLine)> Subsections = [];
        internal List<XrefEntry> Entries = [];
        internal PDict? Trailer;
        internal string KeywordLine = "";
    }

    internal sealed class Parsed
    {
        internal byte[] Bytes = [];
        internal string Text = "";
        internal List<Obj> Objects = [];
        internal List<XrefSection> Xrefs = [];
        internal List<long> StartXrefs = [];
        internal List<string> Problems = [];
        internal int ObjStmCount;
        internal Dictionary<int, Obj> ByNumber = [];

        internal Obj Get(int n) => ByNumber[n];

        /// <summary>The trailer that carries /Root: the only one in a classic file, the first-page one in a linearized file, the XRef stream dictionary otherwise.</summary>
        internal PDict Trailer => Xrefs.Count > 0
            ? Xrefs.First(x => x.Trailer?.Has("Root") == true).Trailer!
            : Objects.Last(o => o.Dict?.Name("Type") == "XRef").Dict!;

        /// <summary>Every trailer in file order: each classic section's, and each cross-reference stream's dictionary.</summary>
        internal IEnumerable<PDict> AllTrailers => Xrefs.Where(x => x.Trailer is not null).Select(x => x.Trailer!)
            .Concat(Objects.Where(o => o.Dict?.Name("Type") == "XRef").Select(o => o.Dict!));

        internal PDict Catalog => (PDict)Resolve(Trailer["Root"])!;

        internal object? Resolve(object? v) => v is PRef r && ByNumber.TryGetValue(r.Number, out var o) ? o.Value : v;

        internal PDict? Info => Trailer["Info"] is PRef r ? Get(r.Number).Dict : null;

        internal byte[] MetadataXml
        {
            get
            {
                var cat = Catalog;
                var m = Get(((PRef)cat["Metadata"]!).Number);
                return m.StreamData!;
            }
        }

        internal string Header => Text[..Math.Min(Text.Length, 9)];

        /// <summary>The decoded bytes of each page's content stream, in object order.</summary>
        internal List<byte[]> PageContents()
        {
            var result = new List<byte[]>();
            foreach (var o in Objects.Where(o => o.Dict?.Name("Type") == "Page"))
            {
                var c = o.Dict!["Contents"];
                var refs = c is List<object?> l ? l.OfType<PRef>().ToList() : c is PRef single ? [single] : [];
                foreach (var r in refs)
                {
                    var s = Get(r.Number);
                    result.Add(s.Dict?.Name("Filter") == "FlateDecode" ? Inflate(s.StreamData!) : s.StreamData!);
                }
            }
            return result;
        }

        /// <summary>Every token of every object body and decoded content stream, in file order.</summary>
        internal List<Tok> Tokens = [];
    }

    internal static byte[] Inflate(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    internal static List<Tok> TokenizeContent(byte[] content)
    {
        var lex = new Lexer(content, 0) { Inventory = [] };
        while (true)
        {
            lex.SkipWs();
            if (lex.AtEnd)
                break;
            lex.ReadToken();
        }
        return lex.Inventory!;
    }

    internal static Parsed Parse(byte[] bytes)
    {
        var p = new Parsed { Bytes = bytes, Text = Encoding.Latin1.GetString(bytes) };
        var lex = new Lexer(bytes, 0);
        // The header line and the comment line: skipped as bytes, because the comment holds bytes above 127.
        var eol = Array.IndexOf(bytes, (byte)'\n');
        lex.Pos = eol + 1;
        var eol2 = Array.IndexOf(bytes, (byte)'\n', lex.Pos);
        lex.Pos = eol2 + 1;
        lex.Inventory = p.Tokens;
        while (true)
        {
            lex.SkipWs();
            if (lex.AtEnd)
                break;
            if (lex.StartsWith("xref"))
            {
                ParseXref(p, lex);
                continue;
            }
            if (lex.StartsWith("startxref"))
            {
                lex.Pos += "startxref".Length;
                var n = lex.ReadToken();
                p.StartXrefs.Add(long.Parse(((PNum)n).Raw));
                continue;
            }
            if (lex.StartsWith("%%EOF"))
            {
                lex.Pos += 5;
                continue;
            }
            if (lex.StartsWith("trailer"))
            {
                lex.Pos += 7;
                if (p.Xrefs.Count > 0)
                    p.Xrefs[^1].Trailer = (PDict?)lex.ReadValue();
                continue;
            }
            ParseObject(p, lex);
        }
        foreach (var o in p.Objects.ToList())
        {
            if (o.Dict?.Name("Type") == "ObjStm" && o.StreamData is { } d)
            {
                p.ObjStmCount++;
                ParseObjStm(p, o, Inflate(d));
            }
        }
        foreach (var o in p.Objects)
            p.ByNumber[o.Number] = o;
        // An encrypted file's streams are ciphertext; only the structure is read from it.
        if (!p.AllTrailers.Any(t => t.Has("Encrypt")))
        {
            foreach (var content in p.PageContents())
                p.Tokens.AddRange(TokenizeContent(content));
        }
        return p;
    }

    private static void ParseXref(Parsed p, Lexer lex)
    {
        var text = p.Text;
        var pos = lex.Pos;
        var sec = new XrefSection { Offset = pos };
        var m0 = new Regex(@"\Gxref(\r\n|\n|\r)").Match(text, pos);
        sec.KeywordLine = m0.Value;
        pos += m0.Length;
        var sub = new Regex(@"\G(\d+) (\d+)(\r\n|\n|\r)");
        var entry = new Regex(@"^(\d{10}) (\d{5}) ([nf])(?: \n| \r|\r\n)$");
        while (true)
        {
            var m = sub.Match(text, pos);
            if (!m.Success)
                break;
            var first = int.Parse(m.Groups[1].Value);
            var count = int.Parse(m.Groups[2].Value);
            sec.Subsections.Add((first, count, m.Value));
            pos += m.Length;
            for (var i = 0; i < count; i++)
            {
                var raw = text.Substring(pos, 20);
                var em = entry.Match(raw);
                var e = new XrefEntry { Number = first + i, Raw = raw };
                if (em.Success)
                {
                    e.Offset = long.Parse(em.Groups[1].Value);
                    e.Generation = int.Parse(em.Groups[2].Value);
                    e.InUse = em.Groups[3].Value == "n";
                }
                else
                    p.Problems.Add($"xref entry {first + i} is not 20 bytes of the required form");
                sec.Entries.Add(e);
                pos += 20;
            }
        }
        lex.Pos = pos;
        p.Xrefs.Add(sec);
    }

    private static void ParseObject(Parsed p, Lexer lex)
    {
        var start = lex.Pos;
        var num = lex.ReadToken();
        var gen = lex.ReadToken();
        var kw = lex.ReadToken();
        if (num is not PNum n || gen is not PNum g || kw is not PKeyword { Text: "obj" })
        {
            p.Problems.Add($"expected an indirect object at byte {start}");
            lex.Pos = int.MaxValue / 2;
            return;
        }
        // The three tokens above are not part of the object body's inventory.
        var inv = lex.Inventory!;
        inv.RemoveRange(inv.Count - 2, 2);
        var o = new Obj { Number = int.Parse(n.Raw), Generation = int.Parse(g.Raw), Offset = start };
        o.KeywordEnd = lex.Pos;
        o.Value = lex.ReadValue();
        var afterValue = lex.Pos;
        lex.SkipWsNoComment();
        if (lex.StartsWith("stream"))
        {
            lex.Pos += 6;
            o.StreamOffset = lex.Pos;
            var len = (int)(o.Dict!.Int("Length") ?? -1);
            if (len < 0)
            {
                p.Problems.Add($"object {o.Number} stream has no direct /Length");
                len = 0;
            }
            // CRLF or LF after the keyword.
            if (lex.ByteAt(lex.Pos) == 13 && lex.ByteAt(lex.Pos + 1) == 10)
                lex.Pos += 2;
            else if (lex.ByteAt(lex.Pos) == 10)
                lex.Pos += 1;
            else
                p.Problems.Add($"object {o.Number}: stream keyword is not followed by CRLF or LF");
            o.StreamOffset = lex.Pos;
            o.StreamData = lex.Slice(lex.Pos, len);
            lex.Pos += len;
            var tail = lex.Slice(lex.Pos, Math.Min(20, lex.Length - lex.Pos));
            var tailText = Encoding.Latin1.GetString(tail);
            if (!Regex.IsMatch(tailText, @"\A(\r\n|\n|\r)endstream(\r\n|\n|\r)endobj(\r\n|\n|\r)"))
                p.Problems.Add($"object {o.Number}: endstream is not preceded by an EOL, or is not followed by endobj and an EOL: {tailText.Replace("\r", "\\r").Replace("\n", "\\n")}");
            lex.Pos = lex.IndexOf("endobj", lex.Pos) + 6;
            if (o.Dict!.Has("Filter") && o.Dict.Name("Filter") == "FlateDecode" && o.Dict.Name("Type") is not ("XRef" or "ObjStm"))
            {
                // Content and font-like streams: decode and take their tokens when they look like content.
            }
        }
        else
        {
            var tailStart = afterValue;
            var tailText = Encoding.Latin1.GetString(lex.Slice(tailStart, Math.Min(12, lex.Length - tailStart)));
            if (!Regex.IsMatch(tailText, @"\A(\r\n|\n|\r)endobj(\r\n|\n|\r)"))
                p.Problems.Add($"object {o.Number}: endobj is not preceded and followed by an EOL: {tailText.Replace("\r", "\\r").Replace("\n", "\\n")}");
            lex.Pos = lex.IndexOf("endobj", lex.Pos) + 6;
        }
        o.Raw = Encoding.Latin1.GetString(lex.Slice((int)o.Offset, lex.Pos - (int)o.Offset));
        p.Objects.Add(o);
    }

    private static void ParseObjStm(Parsed p, Obj stm, byte[] body)
    {
        var n = (int)stm.Dict!.Int("N")!.Value;
        var first = (int)stm.Dict.Int("First")!.Value;
        var head = new Lexer(body, 0);
        var pairs = new List<(int Num, int Off)>();
        for (var i = 0; i < n; i++)
            pairs.Add((int.Parse(((PNum)head.ReadToken()).Raw), int.Parse(((PNum)head.ReadToken()).Raw)));
        foreach (var (num, off) in pairs)
        {
            var lex = new Lexer(body, first + off) { Inventory = p.Tokens };
            var value = lex.ReadValue();
            p.Objects.Add(new Obj { Number = num, Value = value, InObjectStream = true });
        }
    }
}

