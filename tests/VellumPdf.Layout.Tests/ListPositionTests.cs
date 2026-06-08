// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Layout;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Regression test for the list marker/content alignment bug: the item content
/// was indented with a top margin instead of a left margin, so markers sat a line
/// above their text and consecutive items overlapped. Asserts each marker shares a
/// baseline with its content (same Tm Y) and the content is indented to the right.
/// </summary>
public sealed partial class ListPositionTests
{
    [Fact]
    public void OrderedList_marker_sharesBaselineWithContent_andContentIsIndented()
    {
        using var doc = new Document();
        var list = new ListElement(ListStyle.OrderedDecimal);
        list.Add("AlphaItem");
        list.Add("BetaItem");
        doc.Add(list);

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = DecompressContentStreams(ms.ToArray());

        var positions = ExtractTextPositions(content);

        var marker1 = positions.First(p => p.Text == "1.");
        var item1 = positions.First(p => p.Text == "AlphaItem");

        // Same baseline → marker is on the same line as its content (the bug put them on different lines).
        Assert.Equal(marker1.Y, item1.Y, precision: 1);
        // Content is indented to the right of the gutter marker.
        Assert.True(item1.X > marker1.X, $"content X {item1.X} should be right of marker X {marker1.X}");

        // Second item sits strictly below the first (no overlap).
        var item2 = positions.First(p => p.Text == "BetaItem");
        Assert.True(item2.Y < item1.Y, $"item 2 Y {item2.Y} should be below item 1 Y {item1.Y}");
    }

    private static List<(string Text, double X, double Y)> ExtractTextPositions(string content)
    {
        var result = new List<(string, double, double)>();
        foreach (Match m in TextMatrixShow().Matches(content))
        {
            var x = double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            var y = double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            result.Add((m.Groups[3].Value, x, y));
        }
        return result;
    }

    // Matches:  1 0 0 1 <x> <y> Tm (text) Tj
    [GeneratedRegex(@"1 0 0 1 (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) Tm\s*\(([^)]*)\) Tj")]
    private static partial Regex TextMatrixShow();

    private static string DecompressContentStreams(byte[] pdf)
    {
        var sb = new StringBuilder();
        var text = Encoding.Latin1.GetString(pdf);
        var pos = 0;
        while (true)
        {
            var s = text.IndexOf("\nstream\n", pos, StringComparison.Ordinal);
            if (s < 0) break;
            var dataStart = s + "\nstream\n".Length;
            var objStart = text.LastIndexOf("obj\n", s, StringComparison.Ordinal);
            var lenIdx = objStart >= 0 ? text.IndexOf("/Length ", objStart, s - objStart, StringComparison.Ordinal) : -1;
            if (lenIdx < 0) { pos = dataStart; continue; }
            var v = lenIdx + "/Length ".Length;
            var e = v;
            while (e < text.Length && char.IsDigit(text[e])) e++;
            if (!int.TryParse(text[v..e], out var len) || dataStart + len > pdf.Length) { pos = dataStart; continue; }
            try
            {
                using var input = new MemoryStream(pdf[dataStart..(dataStart + len)]);
                using var z = new ZLibStream(input, CompressionMode.Decompress);
                using var outp = new MemoryStream();
                z.CopyTo(outp);
                sb.Append(Encoding.Latin1.GetString(outp.ToArray()));
            }
            catch (InvalidDataException)
            {
                // Not a zlib stream (e.g. an uncompressed XMP /Metadata stream) — skip it.
            }
            pos = dataStart + len;
        }
        return sb.ToString();
    }
}
