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
/// A list lays out its new way (the indent clamp of #476, the moved entries of #685) only where the
/// document addressed the call to it and decided the continuation limit (#686). These rows pin what
/// that means for the list: any other context, an entry that cannot be drawn, an entry no page holds
/// and a first box with no height all give the placement the list had before, which is what
/// <see cref="ListRenderer.Unclamped"/> lays out.
/// </summary>
public sealed class ListPlacementAdoptionTests
{
    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static byte[] Save(Document doc)
    {
        doc.Timestamp = Stamp;
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    private static string[] Shows(byte[] bytes) =>
        Regex.Matches(PdfTestUtil.DecompressAllFlatStreams(bytes), @"\(((?:\\.|[^\\)])*)\)\s*Tj")
            .Select(m => m.Groups[1].Value).ToArray();

    private static Document Page(double width, double height, int leadLines = 0, double lead = 10)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, width, height), Margins = new EdgeInsets(10) };
        for (var i = 0; i < leadLines; i++) doc.Add(new Paragraph("lead" + i, H(lead)) { Margins = EdgeInsets.Zero });
        return doc;
    }

    private static LayoutDiagnosticCode[] Codes(Document doc) => doc.Diagnostics.Select(d => d.Code).ToArray();

    /// <summary>The document the list is added to as a list, and the same document with its twin.</summary>
    private static (byte[] New, Document NewDoc, byte[] Twin, Document TwinDoc) Both(
        Func<Document> page, Func<ListElement> list)
    {
        var newDoc = page();
        newDoc.Add(list());
        var twinDoc = page();
        twinDoc.Add(new ListRenderer(list()).Unclamped());
        return (Save(newDoc), newDoc, Save(twinDoc), twinDoc);
    }

    // An entry the clamp widens, and one that does not fit what is left of a page.
    private static ListElement ClampAndMove() =>
        new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 }
            .Add(new ListItem("clamped text", H(10)))
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) });

    // ── A context the document did not build ─────────────────────────────────

    /// <summary>
    /// A renderer of the caller's that lays a list out through the context it was given, through
    /// <see cref="LayoutContext.WithArea"/>, or through the public constructor, is laid out as
    /// before the fixes: the output is the twin's, byte for byte, and the list is drawn.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AListALayoutRendererOfTheCallersLaysOut_isPlacedAsBefore(int how)
    {
        Document Wrapped(bool twin)
        {
            var doc = Page(200, 100, leadLines: 5);
            var inner = new ListRenderer(ClampAndMove());
            doc.Add(new PassOn(twin ? inner.Unclamped() : inner, how));
            return doc;
        }

        var wrapped = Wrapped(twin: false);
        var plain = Wrapped(twin: true);

        var bytes = Save(wrapped);

        Assert.Equal(Sha(Save(plain)), Sha(bytes));
        Assert.Contains("1.", Shows(bytes));
        Assert.DoesNotContain("clamped", string.Concat(Shows(bytes)), StringComparison.Ordinal);
        Assert.DoesNotContain(LayoutDiagnosticCode.ListIndentClamped, Codes(wrapped));
        Assert.DoesNotContain(LayoutDiagnosticCode.ListIndentClampSkipped, Codes(wrapped));
    }

    /// <summary>
    /// A caller's renderer that reserves 100 points above a list and lays the list out in the rest
    /// through <see cref="LayoutContext.WithArea"/> keeps the output the list had. Moving the first
    /// entry there answered Nothing for the list, and the document refused the wrapper as too tall
    /// where it had saved.
    /// </summary>
    [Fact]
    public void AListInANarrowedAreaAtAPageTop_isNotMovedAndNotRefused()
    {
        Document Build(bool twin)
        {
            var doc = Page(200, 200);
            var list = new ListElement(ListStyle.OrderedDecimal)
                .Add(new ListItem([new TextRun("big", H(100))]) { Style = H(10) })
                .Add(new ListItem("next", H(10)));
            var inner = new ListRenderer(list);
            doc.Add(new Inset(twin ? inner.Unclamped() : inner, 100));
            doc.Add(new Paragraph("after", H(10)));
            return doc;
        }

        var doc = Build(twin: false);
        var bytes = Save(doc);

        Assert.Equal(Sha(Save(Build(twin: true))), Sha(bytes));
        Assert.Equal(["1.", "2.", "next", "after"], Shows(bytes));
    }

    /// <summary>
    /// A twin a caller's renderer holds is not one the document chose: it reports nothing about the
    /// continuation limit, and the list it wraps saves or is refused as the twin does.
    /// </summary>
    [Fact]
    public void AListTheCallersRendererHolds_reportsNoFallbackAtTheContinuationLimit()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        list.Add(new ListItem(string.Join(" ", Enumerable.Repeat("W", 150)), H(10)));
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 40), new EdgeInsets(10)) { ContinuationLimit = 100 };
        renderer.Add(new PassOn(new ListRenderer(list), 0));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        Assert.DoesNotContain(renderer.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
        Assert.DoesNotContain(renderer.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    /// <summary>
    /// One renderer that a caller laid out under a context of its own, and the document then lays
    /// out itself, is the new layout for the document: what the first call left does not carry over.
    /// </summary>
    [Fact]
    public void AListLaidOutUnderAContextOfTheCallers_isLaidOutNewForTheDocumentAfterwards()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 }
            .Add(new ListItem("clamped text", H(10))));
        renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 200, 100)));
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var documentRenderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 100), new EdgeInsets(10));
        documentRenderer.Add(renderer);

        using var ms = new MemoryStream();
        documentRenderer.Render(ms);

        Assert.Contains("clamped", string.Concat(Shows(ms.ToArray())), StringComparison.Ordinal);
        Assert.Contains(documentRenderer.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    /// <summary>The document places the same object in the measuring pass and the drawing pass.</summary>
    [Fact]
    public void TheTwin_isTheSameObjectOnEveryCall()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal).Add("a"));

        Assert.Same(((IContinuationTwin)renderer).Twin(), ((IContinuationTwin)renderer).Twin());
        Assert.Same(renderer.Unclamped(), ((IContinuationTwin)renderer).Twin());
    }

    // ── A first box with no height ───────────────────────────────────────────

    /// <summary>
    /// A paragraph whose leading fills the 20-point area leaves the list a box with no height, which
    /// it answers Nothing for before it builds an item. The decision is made at the retry on the
    /// next page, where the list reports, so the list that would pass the limit is placed as it was.
    /// </summary>
    [Fact]
    public void AListFirstOfferedNoHeight_isDecidedAtTheRetry()
    {
        Run Place(bool twin)
        {
            var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
            list.Add(new ListItem(string.Join(" ", Enumerable.Repeat("W", 102)), H(10)));
            var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
            var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 40), new EdgeInsets(10)) { ContinuationLimit = 100 };
            renderer.Add(new ParagraphRenderer(
                new Paragraph("lead", new TextStyle { FontSize = 10, Leading = 20 }) { Margins = EdgeInsets.Zero }));
            renderer.Add(twin ? new ListRenderer(list).Unclamped() : new ListRenderer(list));
            using var ms = new MemoryStream();
            renderer.Render(ms);
            return new Run(ms.ToArray(), renderer.Diagnostics);
        }

        var run = Place(twin: false);
        var twin = Place(twin: true);

        Assert.Equal(Sha(twin.Bytes), Sha(run.Bytes));
        Assert.Contains(run.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
        Assert.DoesNotContain("W", Shows(run.Bytes));
    }

    // ── What cannot be drawn keeps its layout ────────────────────────────────

    private static EmbeddedFontHandle ForeignHandle() => new Document().UseTrueTypeFont(FontReportSupport.DejaVuBytes);

    private static TextStyle StyleOf(FontReference font, double size = 10) => new() { FontRef = font, FontSize = size };

    /// <summary>
    /// A run in an embedded font that another document returned would be refused at the save once its
    /// text is drawn, and the document never drew it. The entry is not clamped, so the save is the
    /// one the list had.
    /// </summary>
    [Theory]
    [InlineData("foreign-handle")]
    [InlineData("undefined-standard14")]
    public void AnEntryWithARunThatCannotBeDrawn_isNotClamped(string kind)
    {
        FontReference font = kind == "foreign-handle" ? ForeignHandle() : (Standard14)99;
        var (bytes, doc, twin, _) = Both(
            () => Page(200, 400),
            () => new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 }
                .Add(new ListItem([new TextRun("abc", StyleOf(font))]) { Style = H(10) }));

        Assert.Equal(Sha(twin), Sha(bytes));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// The same entry at a page bottom: main dropped the text mid-page and saved. The entry is not
    /// moved, because drawing it on the next page would refuse the save.
    /// </summary>
    [Theory]
    [InlineData("foreign-handle")]
    [InlineData("undefined-standard14")]
    public void AnEntryWithARunThatCannotBeDrawn_isNotMoved(string kind)
    {
        FontReference font = kind == "foreign-handle" ? ForeignHandle() : (Standard14)99;
        var (bytes, doc, twin, _) = Both(
            () => Page(200, 100, leadLines: 5),
            () => new ListElement(ListStyle.OrderedDecimal)
                .Add(new ListItem([new TextRun("dropped", StyleOf(font, 20))]) { Style = H(10) })
                .Add(new ListItem("next", H(10))));

        Assert.Equal(Sha(twin), Sha(bytes));
        Assert.Equal(["lead0", "lead1", "lead2", "lead3", "lead4", "1.", "2.", "next"], Shows(bytes));
        Assert.Equal([LayoutDiagnosticCode.ListEntryPartDropped], Codes(doc));
    }

    /// <summary>
    /// A marker in a font that cannot be drawn is not moved onto a page either, where the entry
    /// would otherwise move for it.
    /// </summary>
    [Fact]
    public void AnEntryWhoseMarkerStyleCannotBeDrawn_isNotMoved()
    {
        var (bytes, _, twin, _) = Both(
            () => Page(200, 100, leadLines: 2),
            () => new ListElement(ListStyle.OrderedDecimal)
                .Add(new ListItem([new TextRun("abc", H(10))]) { Style = StyleOf((Standard14)99, 60) }));

        Assert.Equal(Sha(twin), Sha(bytes));
    }

    /// <summary>The same two rules for a nested child, whose entry is its own.</summary>
    [Theory]
    [InlineData("clamp")]
    [InlineData("move")]
    public void AChildWithARunThatCannotBeDrawn_isNeitherClampedNorMoved(string rule)
    {
        var undefined = StyleOf((Standard14)99, rule == "move" ? 20 : 10);
        var (bytes, doc, twin, _) = Both(
            () => rule == "move" ? Page(200, 100, leadLines: 4) : Page(400, 400),
            () =>
            {
                var parent = new ListItem("p", H(10));
                parent.AddChild(new ListItem([new TextRun("abc", undefined)]) { Style = H(10) });
                return new ListElement(ListStyle.OrderedDecimal) { Indent = rule == "move" ? 20 : 200 }.Add(parent);
            });

        Assert.Equal(Sha(twin), Sha(bytes));
        Assert.DoesNotContain(LayoutDiagnosticCode.ListIndentClamped, Codes(doc));
    }

    /// <summary>The control: a font this document defines is drawn, so the entry is clamped and moved.</summary>
    [Fact]
    public void AnEntryWithARunThisDocumentDefines_isStillClampedAndMoved()
    {
        var doc = Page(200, 100, leadLines: 5);
        var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        doc.Add(new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 }
            .Add(new ListItem([new TextRun("dropped", StyleOf(font, 20))]) { Style = H(10) }));

        var bytes = Save(doc);

        Assert.Contains(LayoutDiagnosticCode.ListIndentClamped, Codes(doc));
        Assert.Contains("1.", Shows(bytes));
    }

    /// <summary>
    /// An entry at a page top is never moved: moving it answers Nothing for the list, and the retry
    /// offers the same top. Every entry fits a page of its own where the document lays the list out,
    /// so the document never offers a top that is smaller than that page, but a context that does
    /// leaves the entry where it falls.
    /// </summary>
    [Fact]
    public void AnEntryAtAPageTopThatDoesNotFitItsBox_isNotMoved()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) }));
        var context = new LayoutContext(new LayoutBox(0, 0, 200, 20))
        {
            FreshPageArea = new LayoutBox(0, 0, 200, 100),
            AtPageTop = true,
            Session = new ContinuationSession(ContinuationMode.New) { Called = renderer },
        };

        var result = renderer.Layout(context);

        Assert.Equal(LayoutResult.Outcome.Full, result.Status);
    }

    // ── An entry no page holds ───────────────────────────────────────────────

    // An entry the new layout would move, followed by one whose marker is taller than a page.
    private static ListElement MovesThenNoPageHolds() =>
        new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) })
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(500) });

    /// <summary>
    /// When any entry of a list fits no page, the whole list is placed as it was: the entry that
    /// would have moved is placed where it falls, and the output is the twin's.
    /// </summary>
    [Fact]
    public void AListWithAnEntryNoPageHolds_isPlacedAsBeforeWhole()
    {
        var (bytes, doc, twin, twinDoc) = Both(() => Page(200, 100, leadLines: 5), MovesThenNoPageHolds);

        Assert.Equal(Sha(twin), Sha(bytes));
        Assert.Equal([LayoutDiagnosticCode.ListEntryPartDropped], Codes(doc));
        Assert.Equal(2, Assert.Single(doc.Diagnostics).Count);
        Assert.Equal(Codes(twinDoc), Codes(doc));
    }

    /// <summary>
    /// The list the document calls answers with its twin's layout, and the document asks the
    /// instance it called for the placements of the list's anchors and of its items'. Both come from
    /// the twin, so the destinations are the ones the twin writes.
    /// </summary>
    [Fact]
    public void AListWithAnEntryNoPageHolds_keepsTheAnchorsItsTwinReports()
    {
        // A list that comes back Full is asked for its placements on the instance the document
        // called, which answers for the twin.
        var (bytes, _, twin, _) = Both(
            () => Page(400, 400),
            () => new ListElement(ListStyle.OrderedDecimal) { Indent = 300, Anchor = "whole-list" }
                .Add(new ListItem([new TextRun("big", H(500))]) { Style = H(10), Anchor = "first-item" }));

        Assert.Equal(Sha(twin), Sha(bytes));
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("whole-list", text, StringComparison.Ordinal);
        Assert.Contains("first-item", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A marker that wraps and gets only some of its lines on a page of its own is an entry no page
    /// holds, so an entry ahead of it that would move stays where it falls, and the list is placed as
    /// before.
    /// </summary>
    [Fact]
    public void AMarkerTallerThanAPageThatWraps_isAnEntryNoPageHolds()
    {
        var (bytes, doc, twin, _) = Both(
            () => Page(60, 100, leadLines: 5),
            () => new ListElement(ListStyle.OrderedDecimal)
                .Add(new ListItem([new TextRun("a", H(20))]) { Style = H(10) })
                .Add(new ListItem("x", H(10)) { Style = H(60) }));

        Assert.Equal(Sha(twin), Sha(bytes));
        Assert.Contains(LayoutDiagnosticCode.ListEntryPartDropped, Codes(doc));
    }

    /// <summary>
    /// A clamped entry whose clamped line is taller than a page fits no page, so the list keeps its
    /// earlier layout whole and reports no clamp.
    /// </summary>
    [Fact]
    public void AClampedLineTallerThanAPage_leavesTheListUnclamped()
    {
        var (bytes, doc, twin, _) = Both(
            () => Page(400, 400),
            () => new ListElement(ListStyle.OrderedDecimal) { Indent = 300 }
                .Add(new ListItem([new TextRun("big", H(500))]) { Style = H(10) }));

        Assert.Equal(Sha(twin), Sha(bytes));
        Assert.Equal([LayoutDiagnosticCode.ListEntryPartDropped], Codes(doc));
    }

    /// <summary>
    /// Only that list keeps its earlier layout. Another list in the document is still clamped and
    /// its entries are still moved.
    /// </summary>
    [Fact]
    public void AnotherListInTheDocument_keepsTheFixes()
    {
        var doc = Page(200, 100, leadLines: 5);
        doc.Add(MovesThenNoPageHolds());
        doc.Add(new PageBreak());
        for (var i = 0; i < 5; i++) doc.Add(new Paragraph("again" + i, H(10)) { Margins = EdgeInsets.Zero });
        doc.Add(new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("moved", H(20))]) { Style = H(10) }));

        var shows = Shows(Save(doc));

        Assert.Contains("moved", shows);
        Assert.DoesNotContain("dropped", shows);
        Assert.Equal([LayoutDiagnosticCode.ListEntryPartDropped], Codes(doc));
    }

    /// <summary>
    /// Text with no width is discarded, not unplaced: an entry whose marker wraps and gets some of its
    /// lines mid-page, beside text with no width, moves to the page that holds the marker whole.
    /// </summary>
    [Fact]
    public void AMarkerThatGetsSomeOfItsLines_besideTextWithNoWidth_moves()
    {
        var doc = Page(200, 100, leadLines: 3);
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 170 };
        var item = new ListItem("p", H(10));
        item.AddChild(new ListItem([new TextRun(" ", H(10))]) { Style = H(20) });
        list.Add(item);
        list.Add(new ListItem("q", H(10)));
        doc.Add(list);

        var bytes = Save(doc);

        Assert.Equal(["lead0", "lead1", "lead2", "1.", "p", "1", ".", "2.", "q"], Shows(bytes));
        Assert.DoesNotContain(LayoutDiagnosticCode.ListEntryPartDropped, Codes(doc));
    }

    // ── What the analysis reads early ────────────────────────────────────────

    /// <summary>
    /// A list reads every entry before it places the first, to find one no page holds. A refusal an
    /// entry raises is the layout's to raise where it reached it, so the refusal the list reports is
    /// the one it always did: an element too tall, before the colour of a later entry.
    /// </summary>
    [Fact]
    public void ARefusalALaterEntryRaises_doesNotReplaceTheOneAnEarlierEntryRaisesFirst()
    {
        ListElement List() => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("abc", H(500))]) { Style = H(500) })
            .Add(new ListItem([new TextRun("abc", new TextStyle { FontSize = 10, Color = new ColorRgb(double.NaN, 0, 0) })]) { Style = H(10) });

        var twin = Page(200, 100);
        twin.Add(new ListRenderer(List()).Unclamped());
        var expected = Assert.Throws<InvalidOperationException>(() => Save(twin));
        var doc = Page(200, 100);
        doc.Add(List());

        var actual = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Equal(expected.Message, actual.Message);
        Assert.StartsWith("An element is too tall", actual.Message, StringComparison.Ordinal);
    }

    // ── Mirrored margins ─────────────────────────────────────────────────────

    /// <summary>
    /// The verdict at the limit does not depend on x, so unequal mirrored margins decide the same
    /// list as equal ones do.
    /// </summary>
    [Theory]
    [InlineData(100, false)]
    [InlineData(102, true)]
    public void TheVerdictAtTheLimit_doesNotDependOnMirroredMargins(int words, bool skipped)
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        list.Add(new ListItem(string.Join(" ", Enumerable.Repeat("W", words)), H(10)));
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 40), new EdgeInsets(10, 30, 10, 10))
        {
            ContinuationLimit = 100,
            MirrorMargins = true,
        };
        renderer.Add(new ListRenderer(list));

        using var ms = new MemoryStream();
        renderer.Render(ms);

        Assert.Equal(skipped, renderer.Diagnostics.Any(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private sealed record Run(byte[] Bytes, IReadOnlyList<LayoutDiagnostic> Diagnostics);

    /// <summary>A renderer of a caller's that lays another renderer out, through the context or through one it makes.</summary>
    private sealed class PassOn(IRenderer inner, int how) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(how switch
        {
            0 => context,
            1 => context.WithArea(context.Area),
            _ => new LayoutContext(context.Area, context.ContentTop),
        });

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    /// <summary>Reserves a band above the renderer it wraps and lays that out in the rest through <c>WithArea</c>.</summary>
    private sealed class Inset(IRenderer inner, double top) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var area = context.Area;
            var rest = new LayoutBox(area.X, area.Y + top, area.Width, area.Height - top);
            var result = inner.Layout(context.WithArea(rest));
            return result.Status == LayoutResult.Outcome.Full
                ? LayoutResult.Full(new LayoutBox(area.X, area.Y, area.Width, top + result.OccupiedArea!.Value.Height))
                : result;
        }

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }
}
