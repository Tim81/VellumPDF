// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A list entry is placed when its marker and its text are both placed (#685). When only one of
/// them fits what is left of a page the entry moves to the next page; when no page holds both it
/// keeps the part that fits, in the bytes the code wrote before, and the document reports it. The
/// digests are those of the base commit, read with the timestamp and document id this class pins.
/// </summary>
public sealed class ListEntryPlacementTests
{
    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static byte[] Save(Document doc)
    {
        doc.Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static string[] Shows(byte[] bytes) =>
        Regex.Matches(PdfTestUtil.DecompressAllFlatStreams(bytes), @"\(((?:\\.|[^\\)])*)\)\s*Tj")
            .Select(m => m.Groups[1].Value).ToArray();

    private static Document Square(int leadLines)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 400), Margins = new EdgeInsets(10) };
        for (var i = 0; i < leadLines; i++) doc.Add(new Paragraph("lead" + i, H(10)) { Margins = EdgeInsets.Zero });
        return doc;
    }

    private static ListElement TallContent() =>
        new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("big", H(500))]) { Style = H(10) })
            .Add(new ListItem("next", H(10)));

    private static ListElement TallMarker() =>
        new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(500) })
            .Add(new ListItem("next", H(10)));

    /// <summary>
    /// Five lines of ten points use 60 of the 80 points of a page, and the first entry's marker
    /// fits the 20 that are left while its twenty-point text, 24 high, does not. The entry moves to
    /// the second page with its marker and its text; the base commit drew the marker and dropped
    /// the text.
    /// </summary>
    [Fact]
    public void AnEntryWhoseTextDoesNotFitWhatIsLeft_movesToTheNextPageWithItsMarker()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 100), Margins = new EdgeInsets(10) };
        for (var i = 0; i < 5; i++) doc.Add(new Paragraph("line" + i, H(10)) { Margins = EdgeInsets.Zero });
        doc.Add(new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) })
            .Add(new ListItem("next", H(10))));

        var bytes = Save(doc);

        Assert.Equal(["line0", "line1", "line2", "line3", "line4", "1.", "dropped", "2.", "next"], Shows(bytes));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// A sixty-point marker, 72 high, does not fit the 56 left under two lead lines. The base
    /// commit drew the text and no marker; the entry now moves to the second page with both.
    /// </summary>
    [Fact]
    public void AnEntryWhoseMarkerDoesNotFitWhatIsLeft_movesToTheNextPageWithItsText()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 100), Margins = new EdgeInsets(10) };
        for (var i = 0; i < 2; i++) doc.Add(new Paragraph("lead" + i, H(10)) { Margins = EdgeInsets.Zero });
        doc.Add(new ListElement(ListStyle.OrderedDecimal).Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(60) }));

        Assert.Equal(["lead0", "lead1", "1.", "abc"], Shows(Save(doc)));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// A marker 40 points high in an area 20 wide breaks into two lines of 48, and eight lead lines
    /// leave room for one. The base commit placed the entry with the marker's second line gone, a
    /// "1" with no stop; the entry now moves, and the marker is drawn whole.
    /// </summary>
    [Fact]
    public void AnEntryWhoseMarkerGetsSomeOfItsLines_movesToTheNextPage()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 300), Margins = new EdgeInsets(72, 90, 72, 90) };
        for (var i = 0; i < 8; i++) doc.Add(new Paragraph("l" + i, H(10)) { Margins = EdgeInsets.Zero });
        doc.Add(new ListElement(ListStyle.OrderedDecimal) { Indent = 5 }
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(40) }));

        var bytes = Save(doc);

        Assert.Equal(["l0", "l1", "l2", "l3", "l4", "l5", "l6", "l7", "1", ".", "ab", "c"], Shows(bytes));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// At the top of a page nothing is gained by moving an entry, so it is placed where it is even
    /// when a page of its own, as the context describes it, would hold both parts.
    /// </summary>
    [Fact]
    public void AnEntryAtThePageTop_isNeverMoved()
    {
        var list = new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) });
        var renderer = new ListRenderer(list);
        var context = new LayoutContext(new LayoutBox(10, 10, 180, 20))
        {
            AtPageTop = true,
            FreshPageArea = new LayoutBox(10, 10, 180, 80),
        };

        var result = renderer.Layout(context);

        Assert.Equal(LayoutResult.Outcome.Full, result.Status);
    }

    /// <summary>The same entry with no lead lines is drawn on the first page, as before.</summary>
    [Fact]
    public void TheSameEntryAtThePageTop_isDrawnWhereItStarts()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 100), Margins = new EdgeInsets(10) };
        doc.Add(new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) })
            .Add(new ListItem("next", H(10))));

        Assert.Equal("C21B6C8B3D63FDC0", Sha(Save(doc)));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// A line of 500 points fits no page of 380. The marker is drawn and the text is not, as it
    /// was, and the document says so, at the top of a page and below other content.
    /// </summary>
    [Theory]
    [InlineData(0, "DA0695640412B42C")]
    [InlineData(3, "0B84BBC7F9D7A1AF")]
    public void AnEntryWhoseTextIsTallerThanAPage_keepsTheBytesItHad_andIsReported(int lead, string digest)
    {
        using var doc = Square(lead);
        doc.Add(TallContent());

        Assert.Equal(digest, Sha(Save(doc)));
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.ListEntryPartDropped, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(1, item.Count);
    }

    [Theory]
    [InlineData(0, "AEC6FFBE7431AC57")]
    [InlineData(3, "D885F0FEB7A9EB25")]
    public void AnEntryWhoseMarkerIsTallerThanAPage_keepsTheBytesItHad_andIsReported(int lead, string digest)
    {
        using var doc = Square(lead);
        doc.Add(TallMarker());

        var bytes = Save(doc);

        Assert.Equal(digest, Sha(bytes));
        Assert.Contains("abc", Shows(bytes));
        Assert.DoesNotContain("1.", Shows(bytes));
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.ListEntryPartDropped, item.Code);
        Assert.Equal(1, item.Count);
    }

    /// <summary>StrictDiagnostics refuses the report, as it does the other list errors.</summary>
    [Fact]
    public void StrictDiagnostics_refusesAnEntryWithAPartDropped()
    {
        using var doc = Square(0);
        doc.StrictDiagnostics = true;
        doc.Add(TallContent());

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith("StrictDiagnostics refused the save: ListEntryPartDropped in List 0, page 1: ", ex.Message, StringComparison.Ordinal);
    }

    // ── The continuation limit ──────────────────────────────────────────────

    private static readonly PdfRectangle ThirtyHigh = new(0, 0, 200, 50);

    // Entries whose text is 16.8 high and whose marker is 12: on a page 30 high the second entry
    // of each page has room for its marker and none for its text, so it moves, and each page then
    // holds one entry where it held two.
    private static ListElement Staggered(int entries)
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < entries; i++)
            list.Add(new ListItem([new TextRun("t" + i, H(14))]) { Style = H(10) });

        return list;
    }

    private static (string Content, int Pages, IReadOnlyList<LayoutDiagnostic> Diagnostics, Exception? Error) Place(
        ListRenderer renderer, int limit)
    {
        var pdf = new PdfDocument { Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), DocumentId = new byte[16] };
        var documentRenderer = new DocumentRenderer(pdf, ThirtyHigh, new EdgeInsets(10)) { ContinuationLimit = limit };
        documentRenderer.Add(renderer);
        using var ms = new MemoryStream();
        try
        {
            documentRenderer.Render(ms);
        }
        catch (InvalidOperationException ex)
        {
            return ("", 0, documentRenderer.Diagnostics, ex);
        }

        return (PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()), pdf.Pages.Count, documentRenderer.Diagnostics, null);
    }

    /// <summary>
    /// One hundred entries take 49 continuations with the second entry of each page left where it
    /// falls, and 99 with it moved. Under a limit of 60 the move would refuse a list that saved, so
    /// the list is placed as before and the fallback is reported; under 200 the entries move.
    /// </summary>
    [Fact]
    public void MovedEntries_thatWouldPassTheLimit_fallBackToTheEarlierPlacement_andAreReported()
    {
        var main = Place(new ListRenderer(Staggered(100)).Unclamped(), limit: 60);
        var run = Place(new ListRenderer(Staggered(100)), limit: 60);

        Assert.Null(main.Error);
        Assert.Null(run.Error);
        Assert.Equal(main.Content, run.Content);
        Assert.Equal(main.Pages, run.Pages);
        Assert.Contains(run.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped && d.Count == 1);
    }

    [Fact]
    public void MovedEntries_thatStayUnderTheLimit_areMoved_andTheirTextIsDrawn()
    {
        var main = Place(new ListRenderer(Staggered(100)).Unclamped(), limit: 200);
        var run = Place(new ListRenderer(Staggered(100)), limit: 200);

        Assert.Null(run.Error);
        Assert.Equal(100, Regex.Matches(run.Content, @"\(t\d+\)\s*Tj").Count);
        Assert.Equal(50, Regex.Matches(main.Content, @"\(t\d+\)\s*Tj").Count);
        Assert.DoesNotContain(run.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
        Assert.Equal(100, run.Pages);
        Assert.Equal(50, main.Pages);
    }
}
