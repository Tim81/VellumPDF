// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #464: a heading that splits across pages has its bookmark at the split piece's position, not at
/// the page top.
/// </summary>
public sealed class OutlineBookmarkSplitTests
{
    private const double PageHeight = 300;
    private const double Margin = 10;

    private static readonly Regex DestPattern = new(
        @"/Dest \[(\d+) 0 R /XYZ (\S+) (\S+) null\]", RegexOptions.CultureInvariant);

    private static Document NewDoc() => new()
    {
        PageSize = new PdfRectangle(0, 0, 200, PageHeight),
        Margins = new EdgeInsets(Margin),
    };

    private static string Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    private static string Filler(int words) => string.Join(' ', Enumerable.Repeat("filler", words));

    private static List<(int PageObject, double Top)> Dests(string pdf) =>
        DestPattern.Matches(pdf)
            .Select(m => (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture)))
            .ToList();

    private static int FirstPageObject(string pdf) =>
        int.Parse(Regex.Match(pdf, @"/Kids \[(\d+) 0 R").Groups[1].Value, CultureInfo.InvariantCulture);

    private static string Build(string headingText, int fillerWords = 120)
    {
        using var doc = NewDoc();
        doc.Add(new Paragraph(Filler(fillerWords)));
        doc.Add(new Heading(headingText));
        return Save(doc);
    }

    [Fact]
    public void SplitHeading_bookmarkIsAtTheSplitPieceNotThePageTop()
    {
        var control = Build("Short");
        var split = Build(string.Join(' ', Enumerable.Repeat("Heading", 40)));

        var c = Assert.Single(Dests(control));
        var s = Assert.Single(Dests(split));

        // The split heading starts where the control heading starts: same filler, same page.
        Assert.Equal(c.Top, s.Top);
        Assert.True(s.Top < PageHeight - Margin, $"bookmark top {s.Top} is at or above the top margin");
        Assert.Equal(FirstPageObject(split), s.PageObject);
        Assert.Contains("/Type /Page", split, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Matches(split, @"/Type /Page\b(?!s)").Count);
    }

    [Fact]
    public void SplitHeading_startingAtLayoutYZero_keepsThePageTop()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, PageHeight), Margins = EdgeInsets.Zero };
        doc.Add(new Heading(string.Join(' ', Enumerable.Repeat("Heading", 400))));
        var pdf = Save(doc);

        var d = Assert.Single(Dests(pdf));
        Assert.Equal(PageHeight, d.Top);
        Assert.True(Regex.Matches(pdf, @"/Type /Page\b(?!s)").Count >= 2);
    }

    [Fact]
    public void Heading_movedToTheNextPageOrSplit_neverTargetsAboveTheTopMargin()
    {
        var onSecondPage = 0;
        var onFirstWithMorePages = 0;
        for (var n = 1; n <= 40; n++)
        {
            using var doc = NewDoc();
            for (var i = 0; i < n; i++) doc.Add(new Paragraph("line " + i));
            doc.Add(new Heading(string.Join(' ', Enumerable.Repeat("Heading", 40))));
            var pdf = Save(doc);

            var d = Assert.Single(Dests(pdf));
            Assert.True(d.Top <= PageHeight - Margin, $"n={n}: top {d.Top}");
            Assert.True(d.Top >= Margin, $"n={n}: top {d.Top}");
            if (d.PageObject == FirstPageObject(pdf)) { if (Regex.Matches(pdf, @"/Type /Page\b(?!s)").Count > 1) onFirstWithMorePages++; }
            else onSecondPage++;
        }

        // The sweep reaches both shapes: split on page 1, and moved to page 2.
        Assert.True(onFirstWithMorePages > 0);
        Assert.True(onSecondPage > 0);
    }

    [Fact]
    public void SplitHeadingBookmark_oracles_agreeWithTheParsedDest()
    {
        var pdf = Build(string.Join(' ', Enumerable.Repeat("Heading", 40)));
        var path = Path.Combine(Path.GetTempPath(), "split-heading-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(pdf));
            var parsed = Assert.Single(Dests(pdf));

            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, stdout + stderr);
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

            ExternalTool.TryRun("qpdf", ["--json", "--json-key=outlines", path], out exit, out stdout, out stderr, out timedOut);
            Assert.False(timedOut);
            Assert.Equal(0, exit);
            using var json = System.Text.Json.JsonDocument.Parse(stdout);
            var item = json.RootElement.GetProperty("outlines")[0];
            Assert.Equal(1, item.GetProperty("destpageposfrom1").GetInt32());
            var dest = item.GetProperty("dest");
            Assert.Equal("/XYZ", dest[1].GetString());
            Assert.Equal(parsed.Top, dest[3].GetDouble(), 3);

            ExternalTool.TryRun("pdftotext", ["-layout", "-f", "1", "-l", "1", path, "-"], out exit, out stdout, out stderr, out timedOut);
            Assert.Equal(0, exit);
            Assert.Contains("Heading", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
