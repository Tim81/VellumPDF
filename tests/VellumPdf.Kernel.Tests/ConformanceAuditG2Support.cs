// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Fonts.Sfnt;
using VellumPdf.Layout.Core;
using static VellumPdf.Kernel.Tests.ConformanceAuditG1Support;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// What the text and font audit (G2) reads back from a saved file: the font objects of every page,
/// the show operators of its content streams decoded through each font's own ToUnicode map, and a
/// reader for ToUnicode CMaps that does not share code with the writer. Fixtures use only the fonts
/// in the tree.
/// </summary>
internal static class ConformanceAuditG2Support
{
    internal static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    internal static readonly PdfConformance[] Claims =
        [PdfConformance.None, PdfConformance.PdfA2b, PdfConformance.PdfA2u, PdfConformance.PdfA2a, PdfConformance.PdfUA1];

    internal static readonly PdfConformance[] ClaimsWithAnOracle =
        [PdfConformance.PdfA2b, PdfConformance.PdfA2u, PdfConformance.PdfA2a, PdfConformance.PdfUA1];

    internal const string Feff = "\uFEFF";

    internal static byte[] DejaVu => EmbeddedFontFixtures.Load("DejaVuSans.ttf");

    internal static byte[] SourceSans => EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf");

    internal static bool Tagged(PdfConformance claim) => claim is PdfConformance.PdfA2a or PdfConformance.PdfUA1;

    internal static string Flavour(PdfConformance claim) => claim switch
    {
        PdfConformance.PdfA2b => "2b",
        PdfConformance.PdfA2u => "2u",
        PdfConformance.PdfA2a => "2a",
        PdfConformance.PdfUA1 => "ua1",
        _ => throw new ArgumentOutOfRangeException(nameof(claim), claim, "No veraPDF flavour for this claim."),
    };

    // ── Documents ───────────────────────────────────────────────────────────────────────────

    /// <summary>A Kernel document whose one page shows <paramref name="text"/> in an embedded font, inside an artifact.</summary>
    internal static PdfDocument KernelDocument(PdfConformance claim, params (byte[] Font, string Text)[] lines)
    {
        var doc = new PdfDocument { Conformance = claim, Timestamp = Pinned, DocumentId = PinnedId, Language = "en-US" };
        doc.Info.Title = "G2 audit";
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page).BeginArtifactMarkedContent().BeginText();
        var y = 720.0;
        foreach (var (font, text) in lines)
        {
            var handle = doc.UseTrueTypeFont(font);
            doc.RegisterEmbeddedFontUsage(page, handle);
            var glyphs = new ushort[text.Length];
            var count = handle.GetGlyphIds(text, glyphs);
            canvas.SetFontByName(handle.ResourceName, 12)
                .SetTextMatrix(1, 0, 0, 1, 72, y)
                .ShowGlyphs(glyphs.AsSpan(0, count));
            y -= 20;
        }

        canvas.EndText().EndMarkedContent().Finish();
        return doc;
    }

    internal static LayoutDocument LayoutDocument(PdfConformance claim)
    {
        var doc = new LayoutDocument
        {
            Conformance = claim,
            Timestamp = Pinned,
            DocumentId = PinnedId,
            Language = "en-US",
            Tagged = Tagged(claim),
        };
        doc.Info.Title = "G2 audit";
        return doc;
    }

    internal static TextStyle Style(LayoutDocument doc, byte[] font, double size = 11) =>
        new() { FontRef = doc.UseTrueTypeFont(font), FontSize = size };

    // ── The font objects of a file ──────────────────────────────────────────────────────────

    /// <summary>One embedded font as the file holds it: the Type 0 dictionary, its one descendant and the objects they name.</summary>
    internal sealed record EmbeddedFont(
        string ResourceName,
        PdfDictionary Type0,
        PdfDictionary CidFont,
        PdfDictionary Descriptor,
        VellumPdf.Reader.ParsedStream Program,
        byte[] ProgramBytes,
        VellumPdf.Reader.ParsedStream ToUnicodeStream,
        byte[] ToUnicodeBytes);

    internal static List<EmbeddedFont> EmbeddedFonts(Pdf pdf)
    {
        var result = new List<EmbeddedFont>();
        var seen = new HashSet<string>();
        for (var i = 0; i < pdf.Reader.PageCount; i++)
        {
            var resources = pdf.Dict(pdf.Reader.GetPage(i).Dictionary.Get(N("Resources")));
            if (resources.Get(N("Font")) is not { } fontEntry)
                continue;
            foreach (var (key, value) in pdf.Dict(fontEntry).Entries)
            {
                var font = pdf.Dict(value);
                if (font.Get(PdfName.Subtype) is not PdfName { Value: "Type0" } || !seen.Add(key.Value + "/" + BaseFont(font)))
                    continue;
                var cid = pdf.Dict(Assert.Single(pdf.Array(font.Get(N("DescendantFonts"))).Items()));
                var descriptor = pdf.Dict(cid.Get(N("FontDescriptor")));
                var programRef = descriptor.Get(N("FontFile2")) ?? descriptor.Get(N("FontFile3"))
                    ?? throw new InvalidOperationException("The descriptor names no font program.");
                var program = pdf.Reader.ResolveStream(Assert.IsType<PdfIndirectReference>(programRef))!;
                var toUnicode = pdf.Reader.ResolveStream(Assert.IsType<PdfIndirectReference>(font.Get(N("ToUnicode"))))!;
                result.Add(new EmbeddedFont(
                    key.Value, font, cid, descriptor, program, pdf.Reader.GetDecodedStreamData(program)!,
                    toUnicode, pdf.Reader.GetDecodedStreamData(toUnicode)!));
            }
        }

        return result;
    }

    internal static string BaseFont(PdfDictionary font) => Assert.IsType<PdfName>(font.Get(PdfName.BaseFont)).Value;

    /// <summary>The Standard 14 and other non-composite font dictionaries of every page, by resource name.</summary>
    internal static List<(string Resource, PdfDictionary Font)> SimpleFonts(Pdf pdf)
    {
        var result = new List<(string, PdfDictionary)>();
        for (var i = 0; i < pdf.Reader.PageCount; i++)
        {
            var resources = pdf.Dict(pdf.Reader.GetPage(i).Dictionary.Get(N("Resources")));
            if (resources.Get(N("Font")) is not { } fontEntry)
                continue;
            foreach (var (key, value) in pdf.Dict(fontEntry).Entries)
            {
                var font = pdf.Dict(value);
                if (font.Get(PdfName.Subtype) is PdfName { Value: not "Type0" })
                    result.Add((key.Value, font));
            }
        }

        return result;
    }

    internal static double Num(PdfObject? value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => throw new InvalidOperationException("Not a number: " + value),
    };

    internal static SfntFont Sfnt(byte[] program) => SfntFont.Parse(program);

    // ── ToUnicode CMaps ─────────────────────────────────────────────────────────────────────

    internal sealed record ToUnicodeMap(
        Dictionary<int, List<string>> Entries,
        List<(int Low, int High, int Bytes)> CodeSpace,
        List<(int Declared, int Actual, string Kind)> Blocks)
    {
        /// <summary>The one string a code maps to; throws when the CMap maps it twice.</summary>
        internal string Single(int code) => Assert.Single(Entries[code]);
    }

    /// <summary>
    /// Reads a ToUnicode CMap with the operators ISO 32000-1, 9.10.3, names: codespace ranges, bfchar
    /// and bfrange (both forms of a range). A destination is UTF-16BE, so a pair of surrogates is
    /// one character.
    /// </summary>
    internal static ToUnicodeMap ParseToUnicode(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        var entries = new Dictionary<int, List<string>>();
        var codeSpace = new List<(int, int, int)>();
        var blocks = new List<(int, int, string)>();

        static int Hex(string h) => int.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        static string Utf16(string h)
        {
            var sb = new StringBuilder();
            for (var i = 0; i < h.Length; i += 4)
                sb.Append((char)Hex(h.Substring(i, 4)));
            return sb.ToString();
        }

        void Add(int code, string value)
        {
            if (!entries.TryGetValue(code, out var list))
                entries[code] = list = [];
            list.Add(value);
        }

        foreach (Match m in Regex.Matches(text, @"(\d+) begincodespacerange\s+(.*?)endcodespacerange", RegexOptions.Singleline))
        {
            var ranges = Regex.Matches(m.Groups[2].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>");
            Assert.Equal(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), ranges.Count);
            foreach (Match r in ranges)
                codeSpace.Add((Hex(r.Groups[1].Value), Hex(r.Groups[2].Value), r.Groups[1].Value.Length / 2));
        }

        foreach (Match m in Regex.Matches(text, @"(\d+) beginbfchar\s+(.*?)endbfchar", RegexOptions.Singleline))
        {
            var pairs = Regex.Matches(m.Groups[2].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>");
            blocks.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), pairs.Count, "bfchar"));
            foreach (Match p in pairs)
                Add(Hex(p.Groups[1].Value), Utf16(p.Groups[2].Value));
        }

        foreach (Match m in Regex.Matches(text, @"(\d+) beginbfrange\s+(.*?)endbfrange", RegexOptions.Singleline))
        {
            var count = 0;
            foreach (Match r in Regex.Matches(m.Groups[2].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*(<[0-9A-Fa-f]+>|\[[^\]]*\])"))
            {
                count++;
                var low = Hex(r.Groups[1].Value);
                var high = Hex(r.Groups[2].Value);
                var dst = r.Groups[3].Value;
                if (dst.StartsWith('['))
                {
                    var each = Regex.Matches(dst, "<([0-9A-Fa-f]+)>");
                    for (var c = low; c <= high; c++)
                        Add(c, Utf16(each[c - low].Groups[1].Value));
                }
                else
                {
                    var first = Utf16(dst.Trim('<', '>'));
                    for (var c = low; c <= high; c++)
                        Add(c, first[..^1] + (char)(first[^1] + (c - low)));
                }
            }

            blocks.Add((int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), count, "bfrange"));
        }

        return new ToUnicodeMap(entries, codeSpace, blocks);
    }

    // ── Content streams ─────────────────────────────────────────────────────────────────────

    /// <summary>One operator of a content stream with its operands as written.</summary>
    internal sealed record Op(string Name, List<string> Operands);

    private static readonly Regex ContentToken = new(
        @"\((?:\\.|[^\\()])*\)|<<|>>|<[0-9A-Fa-f\s]*>|\[|\]|/[^\s\[\]<>()/%]*|[-+]?\d*\.?\d+|[A-Za-z'""*][A-Za-z0-9'""*]*",
        RegexOptions.Compiled);

    internal static List<Op> Operators(string content)
    {
        var ops = new List<Op>();
        var operands = new List<string>();
        foreach (Match m in ContentToken.Matches(content))
        {
            var t = m.Value;
            var isOperator = char.IsLetter(t[0]) || t[0] is '\'' or '"' or '*';
            if (isOperator && t is not ("true" or "false" or "null"))
            {
                ops.Add(new Op(t, operands));
                operands = [];
            }
            else
            {
                operands.Add(t);
            }
        }

        return ops;
    }

    /// <summary>A string shown by Tj, with the font and text matrix in force and the marked content around it.</summary>
    internal sealed record Shown(string Resource, double Size, byte[] Codes, double X, double Y, string[] Marked);

    /// <summary>The Tj and TJ strings of every page, in order, with the font each used and the marked-content tags open.</summary>
    internal static List<Shown> ShownStrings(string content)
    {
        var shown = new List<Shown>();
        var font = "";
        var size = 0.0;
        double x = 0, y = 0;
        var marked = new Stack<string>();
        foreach (var op in Operators(content))
        {
            switch (op.Name)
            {
                case "Tf":
                    font = op.Operands[0][1..];
                    size = double.Parse(op.Operands[1], CultureInfo.InvariantCulture);
                    break;
                case "Tm":
                    x = double.Parse(op.Operands[4], CultureInfo.InvariantCulture);
                    y = double.Parse(op.Operands[5], CultureInfo.InvariantCulture);
                    break;
                case "BMC":
                case "BDC":
                    marked.Push(op.Operands[0][1..]);
                    break;
                case "EMC":
                    marked.Pop();
                    break;
                case "Tj":
                    shown.Add(new Shown(font, size, StringBytes(op.Operands[0]), x, y, [.. marked]));
                    break;
                case "TJ":
                    foreach (var piece in op.Operands.Where(o => o.StartsWith('<') || o.StartsWith('(')))
                        shown.Add(new Shown(font, size, StringBytes(piece), x, y, [.. marked]));
                    break;
            }
        }

        return shown;
    }

    internal static byte[] StringBytes(string token)
    {
        if (token.StartsWith('<'))
            return Convert.FromHexString(Regex.Replace(token.Trim('<', '>'), @"\s", ""));
        var body = token[1..^1];
        var bytes = new List<byte>();
        for (var i = 0; i < body.Length; i++)
        {
            if (body[i] != '\\')
            {
                bytes.Add((byte)body[i]);
                continue;
            }

            i++;
            switch (body[i])
            {
                case 'n': bytes.Add((byte)'\n'); break;
                case 'r': bytes.Add((byte)'\r'); break;
                case 't': bytes.Add((byte)'\t'); break;
                case 'b': bytes.Add((byte)'\b'); break;
                case 'f': bytes.Add((byte)'\f'); break;
                case >= '0' and <= '7':
                    var octal = 0;
                    var digits = 0;
                    while (digits < 3 && i < body.Length && body[i] is >= '0' and <= '7')
                    {
                        octal = octal * 8 + (body[i] - '0');
                        i++;
                        digits++;
                    }

                    i--;
                    bytes.Add((byte)octal);
                    break;
                default: bytes.Add((byte)body[i]); break;
            }
        }

        return [.. bytes];
    }

    /// <summary>The two-byte codes of a string shown with an Identity-H font, high byte first.</summary>
    internal static List<int> Codes(byte[] bytes)
    {
        Assert.Equal(0, bytes.Length % 2);
        var codes = new List<int>();
        for (var i = 0; i < bytes.Length; i += 2)
            codes.Add((bytes[i] << 8) | bytes[i + 1]);
        return codes;
    }

    /// <summary>
    /// The text a string shows, read the way a consumer reads it: each two-byte code through the
    /// font's own ToUnicode map. A code the map lacks fails the test, since ISO 32000-1, 9.10.3,
    /// would leave the consumer to guess.
    /// </summary>
    internal static string Decode(Shown shown, EmbeddedFont font)
    {
        var map = ParseToUnicode(font.ToUnicodeBytes);
        var sb = new StringBuilder();
        foreach (var code in Codes(shown.Codes))
        {
            Assert.True(map.Entries.ContainsKey(code), $"ToUnicode has no entry for code {code:X4} of {shown.Resource}.");
            sb.Append(map.Single(code));
        }

        return sb.ToString();
    }

    /// <summary>The decoded content stream of page <paramref name="index"/>.</summary>
    internal static string Content(Pdf pdf, int index = 0) => pdf.Page(index);

    /// <summary>Every string shown on page <paramref name="index"/> through an embedded font, as text.</summary>
    internal static List<string> ShownText(Pdf pdf, int index = 0)
    {
        var fonts = EmbeddedFonts(pdf).ToDictionary(f => f.ResourceName);
        return [.. ShownStrings(Content(pdf, index)).Select(s => Decode(s, fonts[s.Resource]))];
    }

    // ── What the font program says ──────────────────────────────────────────────────────────

    /// <summary>The advance width of a glyph from the source font's own hmtx table, in 1/1000 em.</summary>
    internal static double AdvanceInThousandths(byte[] fontBytes, int glyph)
    {
        var sfnt = SfntFont.Parse(fontBytes);
        var head = sfnt.GetTableBytes(new Tag("head")).ToArray();
        var unitsPerEm = (head[18] << 8) | head[19];
        var hhea = sfnt.GetTableBytes(new Tag("hhea")).ToArray();
        var metrics = (hhea[34] << 8) | hhea[35];
        var hmtx = sfnt.GetTableBytes(new Tag("hmtx")).ToArray();
        var index = Math.Min(glyph, metrics - 1);
        var advance = (hmtx[index * 4] << 8) | hmtx[index * 4 + 1];
        return advance * 1000.0 / unitsPerEm;
    }

    /// <summary>The head table's box as (xMin, yMin, xMax, yMax, unitsPerEm).</summary>
    internal static (int XMin, int YMin, int XMax, int YMax, int UnitsPerEm) HeadBox(byte[] fontBytes)
    {
        var head = SfntFont.Parse(fontBytes).GetTableBytes(new Tag("head")).ToArray();
        short S(int o) => (short)((head[o] << 8) | head[o + 1]);
        return (S(36), S(38), S(40), S(42), (head[18] << 8) | head[19]);
    }

    /// <summary>The fsType field of the OS/2 table.</summary>
    internal static int FsType(byte[] fontBytes)
    {
        var os2 = SfntFont.Parse(fontBytes).GetTableBytes(new Tag("OS/2")).ToArray();
        return (os2[8] << 8) | os2[9];
    }


    /// <summary>
    /// A code point to glyph lookup read from the font's own cmap table, without the library's
    /// reader: the format 12 subtable of platform 3 / encoding 10 or platform 0 / encoding 4 when
    /// there is one, otherwise the format 4 subtable of platform 3 / encoding 1 or platform 0.
    /// </summary>
    internal static Func<int, int> CmapLookup(byte[] fontBytes)
    {
        var cmap = Sfnt(fontBytes).GetTableBytes(new Tag("cmap")).ToArray();
        int U16(int o) => (cmap[o] << 8) | cmap[o + 1];
        uint U32(int o) => ((uint)U16(o) << 16) | (uint)U16(o + 2);
        var tables = Enumerable.Range(0, U16(2))
            .Select(i => (Platform: U16(4 + (8 * i)), Encoding: U16(6 + (8 * i)), Offset: (int)U32(8 + (8 * i))))
            .Select(r => (r.Platform, r.Encoding, r.Offset, Format: U16(r.Offset)))
            .ToList();

        var wide = tables.FirstOrDefault(t => t.Format == 12 && ((t.Platform == 3 && t.Encoding == 10) || (t.Platform == 0 && t.Encoding == 4)));
        if (wide.Format == 12)
        {
            var groups = (int)U32(wide.Offset + 12);
            return codePoint =>
            {
                for (var g = 0; g < groups; g++)
                {
                    var o = wide.Offset + 16 + (12 * g);
                    if (codePoint >= U32(o) && codePoint <= U32(o + 4))
                        return (int)(U32(o + 8) + (uint)codePoint - U32(o));
                }

                return 0;
            };
        }

        var narrow = tables.First(t => t.Format == 4 && ((t.Platform == 3 && t.Encoding == 1) || t.Platform == 0));
        var segments = U16(narrow.Offset + 6) / 2;
        return codePoint =>
        {
            if (codePoint > 0xFFFF)
                return 0;
            var end = narrow.Offset + 14;
            var start = end + (2 * segments) + 2;
            var delta = start + (2 * segments);
            var range = delta + (2 * segments);
            for (var s = 0; s < segments; s++)
            {
                if (codePoint > U16(end + (2 * s)))
                    continue;
                if (codePoint < U16(start + (2 * s)))
                    return 0;
                var offset = U16(range + (2 * s));
                if (offset == 0)
                    return (codePoint + U16(delta + (2 * s))) & 0xFFFF;
                var glyph = U16(range + (2 * s) + offset + (2 * (codePoint - U16(start + (2 * s)))));
                return glyph == 0 ? 0 : (glyph + U16(delta + (2 * s))) & 0xFFFF;
            }

            return 0;
        };
    }

    /// <summary>The decoded bytes of stream object <paramref name="number"/> as Latin-1 text, or empty when it is not a stream.</summary>
    internal static string DecodedObject(Pdf pdf, int number)
    {
        try
        {
            var stream = pdf.Reader.ResolveStream(number);
            return stream is null ? "" : Encoding.Latin1.GetString(pdf.Reader.GetDecodedStreamData(stream)!);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
