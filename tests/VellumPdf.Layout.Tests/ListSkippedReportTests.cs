// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A list the document lays out as its twin reports
/// <see cref="LayoutDiagnosticCode.ListIndentClampSkipped"/> only when the twin gave something up:
/// an entry whose gutter the clamp would have narrowed, or an entry the new layout would have
/// moved to the next page and the twin placed where it falls. A list with neither saves under
/// strict mode, whatever caused the twin.
/// </summary>
public sealed class ListSkippedReportTests
{
    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    // A renderer of the caller's that takes one point and draws nothing.
    private sealed class Opaque : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 1));

        public void Draw(DrawContext ctx)
        {
        }
    }

    private static Preformatted NearTheLimit() => new(new string('\t', 1_700), H(1)) { TabWidth = 32 };

    private static ListElement Undrawable(double indent = 0) =>
        new ListElement(ListStyle.Unordered) { Indent = indent }
            .Add(new ListItem([new TextRun("bad", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 })])
            { Style = H(10) });

    // The causes of a document-wide twin, each as the element that makes the document hold it.
    public static TheoryData<string> Causes => ["renderer", "font", "near"];

    private static void AddCause(Document doc, string cause)
    {
        switch (cause)
        {
            case "renderer": doc.Add(new Opaque()); break;
            case "font": doc.Add(Undrawable(1000)); break;
            default: doc.Add(NearTheLimit()); break;
        }
    }

    // A page whose content area is 160 wide and 30 high.
    private static Document Page(bool header)
    {
        // The header reserves its height from the content area, which the page grows by.
        var band = header ? new RunningBand("H {page}/{pages}", H(8)) : null;
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 70 + (band?.EffectiveHeight ?? 0)),
            Margins = new EdgeInsets(20),
            Header = band,
        };
        return doc;
    }

    private static byte[] Save(Document doc)
    {
        doc.Timestamp = Stamp;
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static LayoutDiagnostic[] Skipped(Document doc) =>
        doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();

    // Entries of 12 points whose text is 12 high: two fit a page of 30 and the third has no room.
    private static ListElement Fits(int entries)
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < entries; i++) list.Add(new ListItem("one line", H(10)));
        return list;
    }

    // The second entry of each page has room for its marker (12) and none for its text (16.8), so
    // the new layout moves it and each page then holds one entry where the twin holds two.
    private static ListElement Staggered(int entries)
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < entries; i++)
            list.Add(new ListItem([new TextRun("t" + i, H(14))]) { Style = H(10) });

        return list;
    }

    // Two entries that fill most of the first page and then one whose text is 16.8 high and one
    // that does not fit what is left: the only move is on the second page.
    private static ListElement MoveOnTheSecondPiece()
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        list.Add(new ListItem("a", H(10)));
        list.Add(new ListItem("b", H(10)));
        list.Add(new ListItem([new TextRun("c", H(14))]) { Style = H(10) });
        list.Add(new ListItem([new TextRun("d", H(14))]) { Style = H(10) });
        return list;
    }

    // An indent that leaves an entry's text no width, so the new layout clamps it.
    private static ListElement Clamp(int entries)
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        for (var i = 0; i < entries; i++) list.Add(new ListItem("clamped " + i, H(10)));
        return list;
    }

    // ── Nothing differs ──────────────────────────────────────────────────────

    /// <summary>
    /// A list nothing would clamp or move reports nothing, so the document saves under strict mode
    /// with the bytes it saves without it.
    /// </summary>
    [Theory]
    [MemberData(nameof(Causes))]
    public void AListThatLosesNothing_reportsNothing_andSavesUnderStrict(string cause)
    {
        Document Build(bool strict)
        {
            var doc = Page(header: false);
            doc.StrictDiagnostics = strict;
            doc.Add(new ListElement(ListStyle.OrderedDecimal).Add(new ListItem("plain entry", H(10))));
            AddCause(doc, cause);
            return doc;
        }

        var lenient = Build(strict: false);
        var bytes = Save(lenient);
        var strict = Build(strict: true);
        var strictBytes = Save(strict);

        Assert.Empty(Skipped(lenient));
        Assert.Empty(Skipped(strict));
        Assert.Equal(Sha(bytes), Sha(strictBytes));
    }

    /// <summary>
    /// A list that fills a page and goes on to the next, with every entry placed whole, reports
    /// nothing either: the twin and the new layout agree on where each entry falls.
    /// </summary>
    [Theory]
    [MemberData(nameof(Causes))]
    public void AListThatBreaksBetweenEntries_reportsNothing(string cause)
    {
        var doc = Page(header: false);
        doc.Add(Fits(5));
        AddCause(doc, cause);

        Save(doc);

        Assert.Empty(Skipped(doc));
    }

    // ── A clamp skipped ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="LayoutDiagnostic.Count"/> is the number of entries the clamp would have narrowed.
    /// Strict mode refuses the save.
    /// </summary>
    [Theory]
    [MemberData(nameof(Causes))]
    public void EachEntryTheClampWouldHaveNarrowed_isCounted(string cause)
    {
        var doc = Page(header: false);
        doc.Add(Clamp(3));
        AddCause(doc, cause);

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(3, item.Count);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(0, item.ElementIndex);

        var strict = Page(header: false);
        strict.StrictDiagnostics = true;
        strict.Add(Clamp(3));
        AddCause(strict, cause);
        var ex = Assert.Throws<InvalidOperationException>(() => Save(strict));
        Assert.StartsWith(
            "StrictDiagnostics refused the save: ListIndentClampSkipped in List 0, page 1: ", ex.Message, StringComparison.Ordinal);
    }

    // ── A move skipped ───────────────────────────────────────────────────────

    /// <summary>
    /// An entry that fits a page of its own but not what is left of this one is placed where it
    /// falls by the twin and counted, once per entry, from the piece it begins in. Strict mode
    /// refuses the save.
    /// </summary>
    [Theory]
    [MemberData(nameof(Causes))]
    public void EachEntryTheNewLayoutWouldHaveMoved_isCounted(string cause)
    {
        var doc = Page(header: false);
        doc.Add(Staggered(9));
        AddCause(doc, cause);

        Save(doc);

        // The second, fourth, sixth and eighth entries fall where they do not fit.
        var item = Assert.Single(Skipped(doc));
        Assert.Equal(4, item.Count);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(0, item.ElementIndex);

        var strict = Page(header: false);
        strict.StrictDiagnostics = true;
        strict.Add(Staggered(9));
        AddCause(strict, cause);
        Assert.Throws<InvalidOperationException>(() => Save(strict));
    }

    /// <summary>
    /// A move on the second piece of a list is found by that piece alone, in a document that lays
    /// out once and draws each piece before it lays out the next, and in one that lays out twice
    /// for its header. The count is one and the page is that of the piece.
    /// </summary>
    [Theory]
    [InlineData(false, "renderer")]
    [InlineData(true, "renderer")]
    [InlineData(false, "font")]
    [InlineData(true, "font")]
    [InlineData(false, "near")]
    [InlineData(true, "near")]
    public void AMoveOnALaterPiece_isCountedOnceAndKeepsItsPage(bool header, string cause)
    {
        var doc = Page(header);
        doc.Add(MoveOnTheSecondPiece());
        AddCause(doc, cause);

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(1, item.Count);
        Assert.Equal(2, item.PageNumber);
        Assert.Equal(0, item.ElementIndex);
    }

    /// <summary>
    /// A list that is laid out whole, with its one move on the first page, counts that move once
    /// whether the document lays out once or twice.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AMoveOnAListThatFitsOnePage_isCountedOncePerSave(bool header)
    {
        var doc = Page(header);
        doc.Add(Staggered(2));
        doc.Add(new Opaque());

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(1, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    /// <summary>
    /// A list that moves entries on several pieces reports once, with the sum of the pieces, in a
    /// document with a header and in one without.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheMovesOfSeveralPieces_addUpInOneReport(bool header)
    {
        var doc = Page(header);
        doc.Add(Staggered(9));
        doc.Add(new Opaque());

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(4, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    /// <summary>
    /// A renderer built to start at a later entry does not report this code for a skipped move, as
    /// it reports no clamp: the document never walked the entries before it. It can report 208.
    /// </summary>
    [Fact]
    public void ARendererBuiltToStartAtALaterEntry_reportsNoSkippedClampOrMove()
    {
        var doc = Page(header: false);
        doc.Add(new ListRenderer(Staggered(9), startItem: 2));
        doc.Add(new Opaque());

        Save(doc);

        Assert.Empty(Skipped(doc));
    }

    /// <summary>
    /// An entry whose text is taller than a page, placed at the top of a page, is not one the new
    /// layout moves: nothing is gained by moving it. It is reported as a part dropped, and the list
    /// reports no fallback.
    /// </summary>
    [Theory]
    [MemberData(nameof(Causes))]
    public void AnEntryNoPageHolds_atAPageTop_isNotCountedAsAMove(string cause)
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        list.Add(new ListItem([new TextRun("tall", H(60))]) { Style = H(10) });
        var doc = Page(header: false);
        doc.Add(list);
        AddCause(doc, cause);

        Save(doc);

        Assert.Empty(Skipped(doc));
        Assert.Contains(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListEntryPartDropped);
    }

    /// <summary>
    /// An entry the clamp would have narrowed whose marker wraps into two lines, only one of which
    /// fits at the page end, is counted once: as a clamp. Its marker is not placed whole, which
    /// <c>PartUnplaced</c> reads without looking at the text, so the move count must skip it.
    /// </summary>
    [Theory]
    [InlineData(100.0, 2, true)]
    [InlineData(105.0, 2, false)]
    public void AClampedEntryWhoseMarkerIsCutAtThePageEnd_isCountedOnce(
        double contentHeight, int count, bool partDropped)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 45, contentHeight + 20),
            Margins = new EdgeInsets(10),
        };
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        list.Add(new ListItem([new TextRun("a", H(5))]) { Style = H(5) });
        list.Add(new ListItem([new TextRun("a", H(5))]) { Style = H(40) });
        doc.Add(list);
        doc.Add(new Opaque());

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(count, item.Count);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(partDropped, doc.Diagnostics.Any(d => d.Code == LayoutDiagnosticCode.ListEntryPartDropped));
    }

    /// <summary>
    /// The same count for a child entry: its clamp is counted, and its marker, cut at the page
    /// end, is not counted again as a move.
    /// </summary>
    [Fact]
    public void AClampedChildWhoseMarkerIsCutAtThePageEnd_isCountedOnce()
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 45, 56),
            Margins = new EdgeInsets(10),
        };
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 13 };
        list.Add(new ListItem([new TextRun("a", H(5))]) { Style = H(5) });
        var parent = new ListItem([new TextRun("a", H(5))]) { Style = H(5) };
        parent.AddChild(new ListItem([new TextRun("a", H(5))]) { Style = H(20) });
        list.Add(parent);
        doc.Add(list);
        doc.Add(new Opaque());

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(1, item.Count);
    }

    /// <summary>
    /// A document that holds a renderer of yours and a list built to start at a later entry is
    /// refused under strict mode for the part of an entry that list drops (208), with no 207 on any
    /// list. Without the renderer the same list saves under strict mode.
    /// </summary>
    [Fact]
    public void AStrictSaveIsRefusedFor208_whenTheDocumentHoldsARendererOfYoursAndAListBuiltToStartLater()
    {
        Document Build(bool withRenderer)
        {
            var doc = Page(header: false);
            doc.StrictDiagnostics = true;
            doc.Add(new ListRenderer(Staggered(9), startItem: 2));
            if (withRenderer) doc.Add(new Opaque());
            return doc;
        }

        Save(Build(withRenderer: false));
        var refused = Build(withRenderer: true);
        var ex = Assert.Throws<InvalidOperationException>(() => Save(refused));

        Assert.StartsWith("StrictDiagnostics refused the save: ListEntryPartDropped", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Skipped(refused));
    }

    // ── Both ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// A list that holds entries the clamp would have narrowed and an entry the new layout would
    /// have moved reports both in one count. An entry is counted once: the text of a clamped entry
    /// has no width in the twin, and the move count skips an entry already counted as a clamp.
    /// </summary>
    [Fact]
    public void AClampAndAMoveInOneList_areCountedTogether_andNoEntryTwice()
    {
        // An indent of 100 on the 160 wide area leaves a parent's text 60 points, and a child's
        // gutter of 200 leaves its text none, so the new layout clamps both children. The second
        // parent is the entry that falls where its text does not fit.
        var list = new ListElement(ListStyle.Unordered) { Indent = 100 };
        list.Add(new ListItem([new TextRun("a", H(14))]) { Style = H(10) });
        var parent = new ListItem([new TextRun("b", H(14))]) { Style = H(10) };
        parent.AddChild(new ListItem("c1", H(10)));
        parent.AddChild(new ListItem("c2", H(10)));
        list.Add(parent);
        var doc = Page(header: false);
        doc.Add(list);
        doc.Add(new Opaque());

        Save(doc);

        var item = Assert.Single(Skipped(doc));
        Assert.Equal(3, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    // ── The continuation limit ───────────────────────────────────────────────

    private static (byte[]? Bytes, IReadOnlyList<LayoutDiagnostic> Diagnostics) Place(ListElement list, int limit)
    {
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 50), new EdgeInsets(10)) { ContinuationLimit = limit };
        renderer.Add(new ListRenderer(list));
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return (ms.ToArray(), renderer.Diagnostics);
    }

    /// <summary>
    /// A list the continuation limit turned into its twin reports the entries the twin placed
    /// where the new layout would have moved them, with the same count as under a document-wide
    /// cause, and the message of the limit.
    /// </summary>
    [Fact]
    public void ALimitTwin_countsTheEntriesTheNewLayoutWouldHaveMoved()
    {
        var (_, diagnostics) = Place(Staggered(100), limit: 60);

        var item = Assert.Single(diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
        Assert.Equal(50, item.Count);
        Assert.Contains("more page continuations than one element may use", item.Message, StringComparison.Ordinal);
    }

    /// <summary>The same under the limit for the entries the clamp would have narrowed.</summary>
    [Fact]
    public void ALimitTwin_countsTheEntriesTheClampWouldHaveNarrowed()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        for (var i = 0; i < 2; i++) list.Add(new ListItem(string.Join(" ", Enumerable.Repeat("W", 102)), H(10)));

        var (_, diagnostics) = Place(list, limit: 100);

        var item = Assert.Single(diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
        Assert.Equal(2, item.Count);
    }

    // ── The message ──────────────────────────────────────────────────────────

    /// <summary>
    /// The message names the causes the document read and no others: a list twinned only by the
    /// element near the limit does not name a font.
    /// </summary>
    [Theory]
    [InlineData("renderer", "a renderer of yours", "font it cannot draw", "element after a list")]
    [InlineData("font", "font it cannot draw", "a renderer of yours", "element after a list")]
    [InlineData("near", "element after a list", "a renderer of yours", "font it cannot draw")]
    public void TheMessage_namesTheCauseThatApplied_andNoOther(string cause, string named, string other1, string other2)
    {
        var doc = Page(header: false);
        doc.Add(Clamp(1));
        AddCause(doc, cause);

        Save(doc);

        var message = Skipped(doc)[0].Message;
        Assert.Contains(named, message, StringComparison.Ordinal);
        Assert.DoesNotContain(other1, message, StringComparison.Ordinal);
        Assert.DoesNotContain(other2, message, StringComparison.Ordinal);
    }
}
