// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Tests;

/// <summary>Helpers the preformatted-element tests share: fonts, saved bytes, and the page content a saved file holds.</summary>
internal static class PreformattedSupport
{
    internal static readonly string MonoPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationMono-Regular.ttf");
    internal static readonly string DejaVuPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    internal static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The glyph id a font gives a code point, read through a document of its own so the document under test is not touched.</summary>
    internal static ushort Gid(string fontPath, int codePoint)
    {
        using var scratch = new Document();
        return scratch.LoadTrueTypeFont(fontPath).GetGlyphId(codePoint);
    }

    /// <summary>The hex string a <c>Tj</c> holds for these code points in an embedded font.</summary>
    internal static string Hex(string fontPath, params int[] codePoints)
    {
        using var scratch = new Document();
        var font = scratch.LoadTrueTypeFont(fontPath);
        return "<" + string.Concat(codePoints.Select(cp => font.GetGlyphId(cp).ToString("X4", CultureInfo.InvariantCulture))) + ">";
    }

    internal static int[] CodePoints(string text) =>
        [.. text.EnumerateRunes().Select(r => r.Value)];

    internal static string HexOf(string fontPath, string text) => Hex(fontPath, CodePoints(text));

    internal static string Inflate(byte[] pdf, int objectNumber)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var head = Regex.Match(text, $@"(?m)^{objectNumber} 0 obj\n(?<d><<.*?>>)\nstream\n", RegexOptions.Singleline);
        Assert.True(head.Success, $"stream object {objectNumber}");
        var length = int.Parse(Regex.Match(head.Groups["d"].Value, @"/Length (\d+)").Groups[1].Value, CultureInfo.InvariantCulture);
        if (length == 0)
            return string.Empty;
        using var input = new MemoryStream(pdf, head.Index + head.Length, length);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.Latin1.GetString(output.ToArray());
    }

    /// <summary>The decoded content stream of each page, in page-tree order.</summary>
    internal static List<string> PageContents(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pages = new List<string>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
        {
            var number = int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture);
            var page = Regex.Match(text, $@"(?m)^{number} 0 obj\n<<(?<d>.*?)>>\nendobj", RegexOptions.Singleline);
            Assert.True(page.Success, $"page object {number}");
            var contents = Regex.Match(page.Groups["d"].Value, @"/Contents (\d+) 0 R");
            Assert.True(contents.Success, $"page object {number} has no /Contents reference");
            pages.Add(Inflate(pdf, int.Parse(contents.Groups[1].Value, CultureInfo.InvariantCulture)));
        }

        return pages;
    }

    /// <summary>The operators of a page's text object, from <c>BT</c> to <c>ET</c>, one per line.</summary>
    internal static string TextObject(string content)
    {
        var start = content.IndexOf("BT\n", StringComparison.Ordinal);
        var end = content.IndexOf("ET\n", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the content holds no text object:\n" + content);
        return content[start..(end + 3)];
    }

    /// <summary>The literal strings shown with <c>Tj</c> in a Standard-14 page, as Latin-1 text.</summary>
    internal static string[] Literals(string content) =>
        [.. Regex.Matches(content, @"^\((?<t>(?:\\.|[^\\)])*)\) Tj$", RegexOptions.Multiline).Select(m => m.Groups["t"].Value)];

    /// <summary>The hex strings shown with <c>Tj</c> in an embedded-font page.</summary>
    internal static string[] HexStrings(string content) =>
        [.. Regex.Matches(content, @"^<(?<h>[0-9A-F]*)> Tj$", RegexOptions.Multiline).Select(m => "<" + m.Groups["h"].Value + ">")];

    /// <summary>The y of every <c>Tm</c> in a page, as written.</summary>
    internal static string[] TmYs(string content) =>
        [.. Regex.Matches(content, @"^1 0 0 1 (?<x>\S+) (?<y>\S+) Tm$", RegexOptions.Multiline).Select(m => m.Groups["y"].Value)];

    internal static string Spaces(int n) => new(' ', n);

    internal static TextStyle Courier(double size = 10) => new() { Font = Standard14.Courier, FontSize = size };

    internal static TextStyle MonoStyle(Document doc, double size = 10) =>
        new() { FontRef = doc.LoadTrueTypeFont(MonoPath), FontSize = size };
}
