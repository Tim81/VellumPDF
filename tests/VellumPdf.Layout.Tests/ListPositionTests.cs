// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
        var content = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

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
}
