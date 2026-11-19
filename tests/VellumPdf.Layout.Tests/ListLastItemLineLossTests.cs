// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>The last item a list draws on a page keeps every line, wherever the page break falls: the tests sweep the start position across more than one line height and check that every word the list holds is drawn, once.</summary>
public sealed class ListLastItemLineLossTests
{
    private static TextStyle Style(double size) =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    /// <summary>Distinct tokens, so a missing line shows as missing words rather than a changed count.</summary>
    private static string Words(int firstIndex, int count) =>
        string.Join(' ', Enumerable.Range(firstIndex, count).Select(i => $"w{i:D3}"));

    private static (string[] Expected, string[] Drawn) Run(double topMargin, double fontSize, int items, int wordsPerItem)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 150),
            Margins = new EdgeInsets(topMargin, 10, 10, 10),
        };
        var list = new ListElement(ListStyle.Unordered) { DefaultStyle = Style(fontSize) };
        var expected = new List<string>();
        for (var i = 0; i < items; i++)
        {
            list.Add(Words(i * wordsPerItem, wordsPerItem));
            expected.AddRange(Enumerable.Range(i * wordsPerItem, wordsPerItem).Select(n => $"w{n:D3}"));
        }
        doc.Add(list);

        using var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        var drawn = Regex.Matches(stream, @"\(([^)]*)\) Tj")
            .SelectMany(m => m.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(w => w.StartsWith('w') && w.Length == 4)
            .ToArray();
        return (expected.ToArray(), drawn);
    }

    /// <summary>The start position is swept in 0.01pt steps across 1.5 line heights, at three font sizes, so every phase of the page break against the line grid is visited.</summary>
    [Theory]
    [InlineData(10.0)]
    [InlineData(11.0)]
    [InlineData(9.7)]
    public void EveryWordIsDrawnOnce_whereverTheListStartsOnThePage(double fontSize)
    {
        var lineHeight = fontSize * 1.2;
        var steps = (int)(lineHeight * 1.5 / 0.01);
        var failures = new List<string>();

        for (var s = 0; s < steps; s++)
        {
            var top = 10 + (s * 0.01);
            var (expected, drawn) = Run(top, fontSize, items: 8, wordsPerItem: 14);
            var missing = expected.Except(drawn).ToArray();
            var repeated = drawn.GroupBy(w => w).Where(g => g.Count() > 1).Select(g => g.Key).ToArray();
            if (missing.Length > 0 || repeated.Length > 0)
                failures.Add($"top={top:F2}: missing [{string.Join(' ', missing)}] repeated [{string.Join(' ', repeated)}]");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} of {steps} start positions lost or repeated words. First: {failures.FirstOrDefault()}");
    }

    /// <summary>One 20-word item that wraps to two lines at 12pt at the top of an A4 page draws both lines.</summary>
    [Fact]
    public void SingleTwoLineItem_atTheTopOfAnA4Page_drawsBothLines()
    {
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.Unordered) { DefaultStyle = Style(12) }.Add(Words(0, 20)));

        using var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        var lines = Regex.Matches(stream, @"\(([^)]*)\) Tj").Count(m => m.Groups[1].Value.Contains("w0"));

        Assert.True(lines == 2, $"expected the item to wrap to two lines, drew {lines}");
        foreach (var word in Enumerable.Range(0, 20).Select(n => $"w{n:D3}"))
            Assert.Contains(word, stream, StringComparison.Ordinal);
    }
}
