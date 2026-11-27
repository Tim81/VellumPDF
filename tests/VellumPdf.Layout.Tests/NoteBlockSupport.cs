// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>Documents and readbacks the note block tests share.</summary>
internal static class NoteBlockSupport
{
    /// <summary>Courier at 10 pt: a 12 pt line, and every character 6 pt wide.</summary>
    internal static readonly TextStyle Courier10 = new() { Font = Standard14.Courier, FontSize = 10 };

    internal static Document NewDoc(double width, double height, double margin = 20) => new()
    {
        PageSize = new PdfRectangle(0, 0, width, height),
        Margins = new EdgeInsets(margin),
    };

    /// <summary>Lines <c>L01</c>, <c>L02</c>, ... one per line of text.</summary>
    internal static string Lines(int count, int first = 1) =>
        string.Join('\n', Enumerable.Range(first, count).Select(i => "L" + i.ToString("00", CultureInfo.InvariantCulture)));

    /// <summary>The operands of every <c>re</c> operator, in the order written.</summary>
    internal static List<string> Rects(string content) =>
        [.. Regex.Matches(content, @"^(\S+ \S+ \S+ \S+) re$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];

    /// <summary>The operands of every <c>Tm</c>, as <c>x y</c>.</summary>
    internal static List<string> TmPositions(string content) =>
        [.. Regex.Matches(content, @"^1 0 0 1 (\S+ \S+) Tm$", RegexOptions.Multiline).Select(m => m.Groups[1].Value)];

    /// <summary>The text of each page, one string per <c>Tj</c>, in the order drawn.</summary>
    internal static List<string> Shown(string content) =>
        [.. Regex.Matches(content, @"^\((?<t>(?:\\.|[^\\)])*)\) Tj$", RegexOptions.Multiline).Select(m => m.Groups["t"].Value)];

    /// <summary>
    /// The structure tree as nested type names, read from the written objects: <c>Document(Aside(P,P))</c>.
    /// A leaf holds a marked-content id and no children.
    /// </summary>
    internal static string StructTree(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var elements = new Dictionary<int, (string Type, List<int> Kids)>();
        foreach (Match obj in Regex.Matches(text, @"(?m)^(\d+) 0 obj\n<<(.*?)>>\nendobj", RegexOptions.Singleline))
        {
            var dict = obj.Groups[2].Value;
            if (!dict.Contains("/Type /StructElem", StringComparison.Ordinal))
                continue;
            var type = Regex.Match(dict, @"/S /(\w+)").Groups[1].Value;
            var kids = new List<int>();
            var k = Regex.Match(dict, @"/K (\[[^\]]*\]|\d+ 0 R|\d+)(?![\w])");
            if (k.Success)
                foreach (Match r in Regex.Matches(k.Groups[1].Value, @"(\d+) 0 R"))
                    kids.Add(int.Parse(r.Groups[1].Value, CultureInfo.InvariantCulture));
            elements[int.Parse(obj.Groups[1].Value, CultureInfo.InvariantCulture)] = (type, kids);
        }

        var root = elements.Single(e => e.Value.Type == "Document").Key;
        return Print(root);

        string Print(int number)
        {
            var (type, kids) = elements[number];
            return kids.Count == 0 ? type : type + "(" + string.Join(",", kids.Select(Print)) + ")";
        }
    }

    internal sealed record Dest(int Page, double Top);

    /// <summary>The named destinations of the file, by name. Pages are 0-based in page-tree order.</summary>
    internal static Dictionary<string, Dest> Dests(byte[] pdf)
    {
        var text = Regex.Replace(Encoding.Latin1.GetString(pdf), @"\s+", " ");
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pageOf = new Dictionary<int, int>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
            pageOf[int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture)] = pageOf.Count;

        var result = new Dictionary<string, Dest>(StringComparer.Ordinal);
        var names = Regex.Match(text, @"/Dests << /Names \[(.*?)\]").Groups[1].Value;
        foreach (Match pair in Regex.Matches(names, @"\(([^)]*)\) (\d+) 0 R"))
        {
            var obj = Regex.Match(
                text, @"(?<![0-9])" + pair.Groups[2].Value + @" 0 obj \[(\d+) 0 R /XYZ (\S+) (\S+) null\] endobj");
            Assert.True(obj.Success, $"destination object {pair.Groups[2].Value}");
            result[pair.Groups[1].Value] = new Dest(
                pageOf[int.Parse(obj.Groups[1].Value, CultureInfo.InvariantCulture)],
                double.Parse(obj.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return result;
    }

    /// <summary>The <c>/Rect</c> of each link annotation, in the order written.</summary>
    internal static List<double[]> LinkRects(byte[] pdf) =>
        [.. Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Rect \[([^\]]*)\]")
            .Select(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(v => double.Parse(v, CultureInfo.InvariantCulture)).ToArray())];
}
