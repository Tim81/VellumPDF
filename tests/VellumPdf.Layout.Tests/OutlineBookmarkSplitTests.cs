// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #464: a heading that splits across pages has its bookmark at the split piece's position, not at
/// the page top. #609: <see cref="Heading.BookmarkExpanded"/> and the four-argument
/// <see cref="DrawContext.AddOutlineEntry(string, int, double, bool)"/>.
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

    private static string Build(string headingText, int fillerWords = 120, bool expanded = true)
    {
        using var doc = NewDoc();
        doc.Add(new Paragraph(Filler(fillerWords)));
        doc.Add(new Heading(headingText) { BookmarkExpanded = expanded });
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
    public void CollapsedParentWithChildren_writesNegativeCount_andRootCountsVisibleItems()
    {
        using var doc = NewDoc();
        doc.Add(new Heading("Parent") { Level = 0, BookmarkExpanded = false });
        doc.Add(new Heading("Child A") { Level = 1 });
        doc.Add(new Heading("Child B") { Level = 1 });
        var pdf = Save(doc);

        Assert.Contains("/Count -2", pdf, StringComparison.Ordinal);
        Assert.Matches(@"/Type /Outlines[^>]*/Count 1\b|/Count 1\b[^>]*/Type /Outlines", pdf);
    }

    [Fact]
    public void ExpandedParentWithChildren_writesPositiveCount()
    {
        using var doc = NewDoc();
        doc.Add(new Heading("Parent") { Level = 0 });
        doc.Add(new Heading("Child A") { Level = 1 });
        var pdf = Save(doc);

        Assert.Contains("/Count 1", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/Count -", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void CollapsedWithoutChildren_isByteIdenticalToExpanded()
    {
        Assert.Equal(Pinned(true, 1), Pinned(false, 1));
        // Only a level-2 heading follows a level-0 one, so it is a top-level entry, not a child.
        Assert.Equal(Pinned(true, 2), Pinned(false, 2));
        Assert.DoesNotContain("/Count -", Pinned(false, 2), StringComparison.Ordinal);

        static string Pinned(bool expanded, int followerLevel)
        {
            var pdf = new PdfDocument
            {
                Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                DocumentId = new byte[16],
            };
            var r = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, PageHeight), new EdgeInsets(Margin));
            r.Add(new HeadingRenderer(new Heading("Top") { Level = 0, BookmarkExpanded = expanded }));
            if (followerLevel == 2) r.Add(new HeadingRenderer(new Heading("Deep") { Level = 2 }));
            using var ms = new MemoryStream();
            r.Render(ms);
            return System.Text.Encoding.Latin1.GetString(ms.ToArray());
        }
    }

    [Fact]
    public void SplitHeading_collapsed_hasOneEntryAtTheSplitPiece()
    {
        using var doc = NewDoc();
        doc.Add(new Paragraph(Filler(120)));
        doc.Add(new Heading(string.Join(' ', Enumerable.Repeat("Heading", 40))) { Level = 0, BookmarkExpanded = false });
        doc.Add(new Heading("Child") { Level = 1 });
        var pdf = Save(doc);

        var dests = Dests(pdf);
        Assert.Equal(2, dests.Count);
        Assert.True(dests[0].Top < PageHeight - Margin);
        Assert.Contains("/Count -1", pdf, StringComparison.Ordinal);
    }

    private sealed class OutlineRenderer(Action<DrawContext> draw) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) => draw(context);
    }

    [Fact]
    public void ThreeArgumentOverload_staysExpanded_withChildren()
    {
        using var doc = NewDoc();
        doc.Add(new OutlineRenderer(c =>
        {
            c.AddOutlineEntry("P", 0, 20);
            c.AddOutlineEntry("C", 1, 30);
        }));
        var pdf = Save(doc);

        Assert.Contains("/Count 1", pdf, StringComparison.Ordinal);
        Assert.DoesNotContain("/Count -", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void FourArgumentOverload_collapsed_writesNegativeCount()
    {
        using var doc = NewDoc();
        doc.Add(new OutlineRenderer(c =>
        {
            c.AddOutlineEntry("P", 0, 20, expanded: false);
            c.AddOutlineEntry("C", 1, 30, expanded: true);
        }));
        var pdf = Save(doc);

        Assert.Contains("/Count -1", pdf, StringComparison.Ordinal);
    }

    [Fact]
    public void FourArgumentOverload_refusesNullTitle_andTheDocumentCannotBeSavedAgain()
    {
        using var doc = NewDoc();
        doc.Add(new OutlineRenderer(c => c.AddOutlineEntry(null!, 0, 20, expanded: true)));

        using var ms = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => doc.Save(ms));
        var again = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Contains("cannot be saved again", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FourArgumentOverload_refusesNonFiniteY()
    {
        using var doc = NewDoc();
        doc.Add(new OutlineRenderer(c => c.AddOutlineEntry("T", 0, double.NaN, expanded: true)));

        using var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.Save(ms));
    }

    [Fact]
    public void ThreeArgumentOverload_nullTitle_stillFailsLaterInTheSave()
    {
        using var doc = NewDoc();
        doc.Add(new OutlineRenderer(c => c.AddOutlineEntry(null!, 0, 20)));

        using var ms = new MemoryStream();
        Assert.Throws<NullReferenceException>(() => doc.Save(ms));
    }

    [Fact]
    public void HeadingAtANonFinitePosition_isRefusedByTheWriterWithArgumentException()
    {
        // A page height and a top margin at the extremes make the destination's Y overflow to
        // infinity. The heading draws through the unchecked core, so the exception is the writer's
        // ArgumentException itself, not the validating overload's ArgumentOutOfRangeException.
        var pdf = new PdfDocument();
        var r = new DocumentRenderer(
            pdf,
            new PdfRectangle(0, 0, 200, double.MaxValue),
            new EdgeInsets { Top = -double.MaxValue, Right = 10, Bottom = 10, Left = 10 });
        r.Add(new HeadingRenderer(new Heading("T")));

        var ex = Record.Exception(() => r.Render(new MemoryStream()));

        Assert.NotNull(ex);
        Assert.Equal(typeof(ArgumentException), ex.GetType());
        Assert.Contains("PDF does not support NaN or Infinity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SplitHeadingStartingAboveLayoutYZero_hasItsBookmarkAboveThePageHeight()
    {
        // A negative document top margin: the split piece starts at layout y -20.
        using (var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, PageHeight),
            Margins = new EdgeInsets { Top = -20, Right = Margin, Bottom = Margin, Left = Margin },
        })
        {
            doc.Add(new Heading(string.Join(' ', Enumerable.Repeat("Heading", 400))));
            var pdf = Save(doc);
            Assert.Equal(PageHeight + 20, Assert.Single(Dests(pdf)).Top);
            Assert.True(Regex.Matches(pdf, @"/Type /Page\b(?!s)").Count >= 2);
        }

        // A negative heading top margin: the split piece starts at layout y 10 - 30 = -20.
        using var doc2 = NewDoc();
        doc2.Add(new Heading(string.Join(' ', Enumerable.Repeat("Heading", 400)))
        {
            Margins = new EdgeInsets { Top = -30, Right = 0, Bottom = 0, Left = 0 },
        });
        var pdf2 = Save(doc2);
        Assert.Equal(PageHeight + 20, Assert.Single(Dests(pdf2)).Top);
        Assert.True(Regex.Matches(pdf2, @"/Type /Page\b(?!s)").Count >= 2);
    }

    [Fact]
    public void LevelMinusOneBookmark_neverGetsChildren_soCollapsedIsNotWritten()
    {
        using var doc = NewDoc();
        doc.Add(new Heading("Below") { Level = -1, BookmarkExpanded = false });
        doc.Add(new Heading("Top") { Level = 0 });
        var pdf = Save(doc);

        // Two top-level items and no /Count on either; the root counts both.
        Assert.DoesNotContain("/Count -", pdf, StringComparison.Ordinal);
        Assert.Matches(@"/Type /Outlines[^>]*/Count 2\b|/Count 2\b[^>]*/Type /Outlines", pdf);
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
