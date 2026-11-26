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
/// A document that holds an entry in a font it cannot draw (a handle another document returned, a
/// <see cref="Standard14"/> value the enumeration does not name) saves where that entry's text
/// falls where it does not fit and is dropped, and throws where the text is drawn. Clamping or
/// moving an entry ahead of it changes which, so every list of such a document is laid out as its
/// twin. The rows compare each document with the same document whose lists are replaced by their
/// twins, which is how the lists were laid out before the clamp and the moving of entries (#476,
/// #685).
/// </summary>
public sealed class ListDocumentFontRuleTests
{
    private static TextStyle H(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    // How a list is placed. A visible list is one the document holds itself; the others are lists
    // that a renderer of the caller's lays out, through the context, through WithArea and through
    // the public constructor.
    private const int Visible = -1;

    private static EmbeddedFontHandle ForeignHandle() => new Document().UseTrueTypeFont(FontReportSupport.DejaVuBytes);

    // A handle whose resource name is TT2, which a document that embedded one font does not define.
    private static EmbeddedFontHandle SecondForeignHandle()
    {
        var other = new Document();
        other.UseTrueTypeFont(FontReportSupport.LiberationMonoBytes);
        return other.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
    }

    // "ok" is a font the document draws, for the controls.
    private static TextStyle Bad(string kind, double size) => new()
    {
        FontRef = kind switch
        {
            "foreign" => ForeignHandle(),
            "ok" => new FontReference(Standard14.Helvetica),
            _ => new FontReference((Standard14)99),
        },
        FontSize = size,
    };

    private static Document Page(double width, double height, int leadLines, bool header)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, width, height), Margins = new EdgeInsets(10) };
        if (header) doc.Header = new RunningBand("H {page}/{pages}", H(8));
        for (var i = 0; i < leadLines; i++) doc.Add(new Paragraph("lead" + i, H(10)) { Margins = EdgeInsets.Zero });
        return doc;
    }

    private static void Place(Document doc, ListElement list, int how, bool twin)
    {
        var renderer = new ListRenderer(list);
        if (how == Visible)
        {
            if (twin) doc.Add(renderer.Unclamped());
            else doc.Add(list);
        }
        else
        {
            doc.Add(new Holder(twin ? renderer.Unclamped() : renderer, how));
        }
    }

    // What a save gives, as one string: the SHA of the file, or the exception's type and message.
    private static string Outcome(Document doc)
    {
        doc.Timestamp = Stamp;
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        try
        {
            using var ms = new MemoryStream();
            doc.Save(ms);
            return "OK " + Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16];
        }
        catch (Exception ex)
        {
            return "EX " + ex.GetType().Name + " " + ex.Message;
        }
    }

    private static string[] Shows(Document doc)
    {
        doc.Timestamp = Stamp;
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return Regex.Matches(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()), @"\(((?:\\.|[^\\)])*)\)\s*Tj")
            .Select(m => m.Groups[1].Value).ToArray();
    }

    private static LayoutDiagnosticCode[] Codes(Document doc) => doc.Diagnostics.Select(d => d.Code).ToArray();

    // ── The scenarios ────────────────────────────────────────────────────────

    // An entry that moves whole to the next page, then one whose text fits where it falls on the
    // first page and does not on the second, or the reverse. Page 200x100, margins 10.
    private static Document ShiftedByAMove(string kind, int how, bool header, bool twin, bool badFirst)
    {
        var doc = Page(200, 100, 4, header);
        var a = new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("a", H(10)))
            .Add(new ListItem([new TextRun("mover", H(20))]) { Style = H(10) });
        var b = new ListElement(ListStyle.Unordered)
            .Add(new ListItem([new TextRun("bad", Bad(kind, 10))]) { Style = H(6) });
        if (badFirst)
        {
            Place(doc, b, how, twin);
            Place(doc, a, Visible, twin);
        }
        else
        {
            Place(doc, a, Visible, twin);
            Place(doc, b, how, twin);
        }

        return doc;
    }

    // A clamped nested child grows, pushing the entry after it down. Indent 100 on a 180 pt area.
    private static Document ShiftedByAClamp(string kind, int how, bool header, bool twin, double child)
    {
        var doc = Page(200, 100, 0, header);
        var parent = new ListItem("p", H(10));
        parent.AddChild(new ListItem([new TextRun("kid", H(child))]) { Style = H(10) });
        var a = new ListElement(ListStyle.OrderedDecimal) { Indent = 100 }.Add(parent);
        var b = new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("bad", Bad(kind, 30))]) { Style = H(10) });
        Place(doc, a, Visible, twin);
        Place(doc, b, how, twin);
        return doc;
    }

    // The list that holds the font comes first and drops its text at the bottom of page 1, so it
    // saves. The list after it starts page 2 with an entry that moves where it is laid out the new
    // way, so only a document that lays it out as its twin is the document 2.3.3 saved.
    // The font sits in the text of the entry, in its marker, or in the text of a nested child.
    private static Document BadListFirst(string kind, int how, bool header, bool twin, string where = "text")
    {
        var doc = Page(200, 100, where == "child" ? 5 : header ? 5 : 6, header);
        var b = new ListElement(ListStyle.Unordered);
        switch (where)
        {
            case "text":
                b.Add(new ListItem([new TextRun("bad", Bad(kind, 10))]) { Style = H(5) });
                break;
            case "marker":
                // The marker is too tall for the room left and is dropped; the text fits.
                b.Add(new ListItem([new TextRun("t", H(5))]) { Style = Bad(kind, 30) });
                break;
            default:
                var parent = new ListItem([new TextRun("p", H(10))]) { Style = H(10) };
                parent.AddChild(new ListItem([new TextRun("bad", Bad(kind, 10))]) { Style = H(5) });
                b.Add(parent);
                break;
        }

        var a = new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("a", H(10)))
            .Add(new ListItem([new TextRun("mover", H(header ? 50 : 60))]) { Style = H(10) });
        Place(doc, b, how, twin);
        Place(doc, a, Visible, twin);
        return doc;
    }

    // The entry that holds the font follows the entry that moves in the same list.
    private static Document SameListAfterAMove(string kind, bool header, bool twin)
    {
        var doc = Page(200, 100, 5, header);
        var list = new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) })
            .Add(new ListItem([new TextRun("bad", Bad(kind, 10))]) { Style = H(6) });
        Place(doc, list, Visible, twin);
        return doc;
    }

    // ── The rule ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An entry that follows a moved entry in its own list, which the first version of the check
    /// left on the new layout: main dropped its text at the page bottom and saved.
    /// </summary>
    [Theory]
    [InlineData("undefined", false)]
    [InlineData("undefined", true)]
    [InlineData("foreign", false)]
    [InlineData("foreign", true)]
    public void AnUndrawableEntryAfterAMovedEntryInItsOwnList_keepsTheOutcomeItHad(string kind, bool header)
    {
        var twin = Outcome(SameListAfterAMove(kind, header, twin: true));
        var doc = SameListAfterAMove(kind, header, twin: false);

        Assert.Equal(twin, Outcome(doc));
        if (!header)
        {
            Assert.StartsWith("OK ", twin);
            Assert.Equal(["lead0", "lead1", "lead2", "lead3", "lead4", "1.", "2."], Shows(SameListAfterAMove(kind, header, twin: false)));
        }
    }

    /// <summary>
    /// The entry sits in another list, which the first list's moved entry shifts. The other list is
    /// laid out as its twin would be, so nothing of it changes by itself.
    /// </summary>
    [Theory]
    [InlineData("undefined", Visible, false)]
    [InlineData("undefined", Visible, true)]
    [InlineData("undefined", 0, false)]
    [InlineData("undefined", 0, true)]
    [InlineData("undefined", 1, false)]
    [InlineData("undefined", 2, false)]
    [InlineData("undefined", 2, true)]
    [InlineData("undefined", 3, false)]
    [InlineData("foreign", 3, false)]
    [InlineData("foreign", Visible, false)]
    [InlineData("foreign", 0, false)]
    [InlineData("foreign", 0, true)]
    [InlineData("foreign", 1, false)]
    [InlineData("foreign", 2, false)]
    [InlineData("undefined", 4, false)]
    [InlineData("undefined", 4, true)]
    [InlineData("undefined", 5, false)]
    [InlineData("foreign", 4, false)]
    [InlineData("foreign", 5, true)]
    public void AnUndrawableListAfterAMovingList_keepsTheOutcomeItHad(string kind, int how, bool header)
    {
        var twin = Outcome(ShiftedByAMove(kind, how, header, twin: true, badFirst: false));

        Assert.Equal(twin, Outcome(ShiftedByAMove(kind, how, header, twin: false, badFirst: false)));
    }

    [Theory]
    [InlineData("undefined", 0, false)]
    [InlineData("undefined", 2, false)]
    [InlineData("foreign", 1, false)]
    [InlineData("foreign", 1, true)]
    public void AnUndrawableListBeforeAMovingList_keepsTheOutcomeItHad(string kind, int how, bool header)
    {
        var twin = Outcome(ShiftedByAMove(kind, how, header, twin: true, badFirst: true));

        Assert.Equal(twin, Outcome(ShiftedByAMove(kind, how, header, twin: false, badFirst: true)));
    }

    /// <summary>The font is in the marker's style, or in a nested child.</summary>
    [Theory]
    [InlineData("marker", "undefined", Visible)]
    [InlineData("marker", "foreign", 0)]
    [InlineData("marker", "undefined", 3)]
    [InlineData("child", "undefined", Visible)]
    [InlineData("child", "foreign", 1)]
    [InlineData("child", "undefined", 2)]
    public void AnUndrawableMarkerOrChild_turnsTheListsAfterItIntoTheirTwins(string where, string kind, int how)
    {
        var twin = Outcome(BadListFirst(kind, how, header: false, twin: true, where));

        Assert.StartsWith("OK ", twin);
        Assert.Equal(twin, Outcome(BadListFirst(kind, how, header: false, twin: false, where)));
        AssertControl(how, header: false, twin: false, ok => BadListFirst("ok", how, false, ok, where));
    }

    // The control, with a font the document draws. A list the document holds itself is laid out the
    // new way and so is not its twin; a document that holds a renderer of the caller's lays every
    // list out as its twin whatever the font, which is the rule these rows depend on.
    private static void AssertControl(int how, bool header, bool twin, Func<bool, Document> build)
    {
        var control = Outcome(build(false));
        if (how == Visible) Assert.NotEqual(Outcome(build(true)), control);
        else Assert.Equal(Outcome(build(true)), control);
    }

    /// <summary>
    /// A document that holds a renderer of the caller's lays the list after it out as its twin,
    /// whether or not the font in the held list can be drawn. Here the first list saves, with its
    /// text dropped, so only the lists after it tell the two layouts apart.
    /// </summary>
    [Theory]
    [InlineData("undefined", 0, false)]
    [InlineData("undefined", 1, false)]
    [InlineData("undefined", 2, true)]
    [InlineData("undefined", 3, false)]
    [InlineData("foreign", 0, false)]
    [InlineData("foreign", 2, false)]
    [InlineData("foreign", 3, false)]
    [InlineData("foreign", 1, true)]
    [InlineData("undefined", 4, false)]
    [InlineData("foreign", 5, false)]
    public void AHeldListThatSavesWithItsTextDropped_turnsTheListsAfterItIntoTheirTwins(string kind, int how, bool header)
    {
        var twin = Outcome(BadListFirst(kind, how, header, twin: true));

        Assert.StartsWith("OK ", twin);
        Assert.Equal(twin, Outcome(BadListFirst(kind, how, header, twin: false)));
        AssertControl(how, header, twin: false, ok => BadListFirst("ok", how, header, ok));
    }

    /// <summary>
    /// A clamp ahead of the entry pushes it down past the room its text needs. Main drew the text
    /// and threw, and the document still throws.
    /// </summary>
    [Theory]
    [InlineData("undefined", Visible, 50.0, false)]
    [InlineData("undefined", Visible, 60.0, true)]
    [InlineData("undefined", 0, 50.0, false)]
    [InlineData("undefined", 1, 60.0, false)]
    [InlineData("undefined", 2, 50.0, true)]
    [InlineData("foreign", Visible, 50.0, false)]
    [InlineData("foreign", 0, 60.0, false)]
    [InlineData("foreign", 2, 50.0, false)]
    public void AnUndrawableEntryAfterAClampedChild_keepsTheOutcomeItHad(string kind, int how, double child, bool header)
    {
        var twin = Outcome(ShiftedByAClamp(kind, how, header, twin: true, child));

        Assert.StartsWith("EX ", twin);
        Assert.Equal(twin, Outcome(ShiftedByAClamp(kind, how, header, twin: false, child)));

        // The clamp is what makes the entry's text not fit: with a font the document draws, the
        // document saves, and not as the twin does.
        Assert.StartsWith("OK ", Outcome(ShiftedByAClamp("ok", how, header, twin: false, child)));
    }

    /// <summary>
    /// The fixture holds the change: with a font the document draws and no renderer of the
    /// caller's, the same documents are not their twins, so the rows above compare two layouts that
    /// differ where the font allows it. With a renderer of the caller's holding the second list,
    /// they are their twins, which is the rule that renderer brings.
    /// </summary>
    [Theory]
    [InlineData(Visible, false)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void AControlWithAFontTheDocumentDraws_isItsTwinOnlyWhenARendererOfTheCallersIsHeld(int how, bool header)
    {
        AssertControl(how, header, twin: false, twin => ShiftedByAMove("ok", how, header, twin, badFirst: false));
    }

    [Fact]
    public void AControlWithAFontTheDocumentDraws_isClampedAndMovedAfterAMovedEntry()
    {
        var doc = SameListAfterAMove("ok", header: false, twin: false);

        Assert.Equal(["lead0", "lead1", "lead2", "lead3", "lead4", "1.", "dropped", "2.", "bad"], Shows(doc));
        Assert.Empty(doc.Diagnostics);
    }

    // ── What is reported ─────────────────────────────────────────────────────

    /// <summary>
    /// Every list the document placed reports the fallback once, whether or not it holds the entry,
    /// and a list a renderer of the caller's holds reports nothing, 208 included.
    /// </summary>
    [Fact]
    public void EveryListTheDocumentPlaces_reportsTheFallbackOnce_andAHeldListReportsNothing()
    {
        var doc = ShiftedByAMove("undefined", 0, header: false, twin: false, badFirst: false);

        Outcome(doc);

        // The list the document holds reports 207 and the part of its entry that is dropped.
        Assert.Equal(
            [LayoutDiagnosticCode.ListIndentClampSkipped, LayoutDiagnosticCode.ListEntryPartDropped],
            Codes(doc));
        Assert.All(doc.Diagnostics, d => Assert.Equal(LayoutElementKind.List, d.ElementKind));
    }

    [Fact]
    public void BothVisibleLists_reportTheFallback()
    {
        var doc = ShiftedByAMove("foreign", Visible, header: false, twin: false, badFirst: false);

        Outcome(doc);

        Assert.Equal(2, doc.Diagnostics.Count(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped));
    }

    /// <summary>
    /// The count is the entries the clamp would have narrowed, from the same gutters the clamp
    /// reads, and 1 when it would have narrowed none.
    /// </summary>
    [Fact]
    public void TheFallbackCount_isTheEntriesTheClampWouldHaveNarrowed()
    {
        var doc = Page(200, 400, 0, header: false);
        var clamped = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 }
            .Add(new ListItem("one", H(10)))
            .Add(new ListItem("two", H(10)))
            .Add(new ListItem([new TextRun("bad", Bad("undefined", 10))]) { Style = H(10) });
        doc.Add(clamped);
        // Indent 100 leaves a child's gutter, 200, no room on the 180 pt area, and its parent some.
        var parent = new ListItem("p", H(10));
        parent.AddChild(new ListItem("c1", H(10)));
        parent.AddChild(new ListItem("c2", H(10)));
        doc.Add(new ListElement(ListStyle.Unordered) { Indent = 100 }.Add(parent));
        var untouched = new ListElement(ListStyle.Unordered).Add(new ListItem("x", H(10)));
        doc.Add(untouched);

        Outcome(doc);

        var skipped = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
        Assert.Equal(3, skipped.Length);
        Assert.Equal(2, skipped[0].Count);
        Assert.Equal(2, skipped[1].Count);
        Assert.Equal(1, skipped[2].Count);
    }

    /// <summary>A list with no items has nothing to report.</summary>
    [Fact]
    public void AnEmptyList_reportsNothingInADocumentThatHoldsTheFont()
    {
        var doc = Page(200, 100, 0, header: false);
        doc.Add(new ListElement(ListStyle.Unordered));
        doc.Add(new ListElement(ListStyle.Unordered) { Indent = 1000 }
            .Add(new ListItem([new TextRun("bad", Bad("undefined", 10))])));

        Outcome(doc);

        Assert.Single(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);
    }

    /// <summary>
    /// A twin used only because a caller's renderer holds the list reports nothing. A part of its
    /// entry is dropped at the page bottom, as it was, and no diagnostic says so.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void AListACallersRendererHolds_reportsNoPartDropped(int how)
    {
        var doc = Page(200, 100, 5, header: false);
        var list = new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) });
        doc.Add(new Holder(new ListRenderer(list), how));

        var outcome = Outcome(doc);

        Assert.StartsWith("OK ", outcome);
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>The same list, added to the document itself, is reported as before.</summary>
    [Fact]
    public void AListTheDocumentPlacesAsItsTwin_stillReportsThePartDropped()
    {
        var doc = Page(200, 100, 5, header: false);
        doc.Add(new ListRenderer(new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) })).Unclamped());

        Outcome(doc);

        Assert.Equal([LayoutDiagnosticCode.ListEntryPartDropped], Codes(doc));
    }

    // ── The passes ───────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static (DocumentRenderer Renderer, byte[] Bytes) Render(
        Action<DocumentRenderer> add, bool header = false, PdfDocument? pdf = null)
    {
        pdf ??= new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 100), new EdgeInsets(10));
        if (header) renderer.Header = new RunningBand("H {page}/{pages}", H(8));
        add(renderer);
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return (renderer, ms.ToArray());
    }

    // The indent leaves the text no width where it is asked for, so a font the document cannot
    // draw is held and its text is never drawn, which is what lets the document save.
    private static ListElement OneEntry(string text, TextStyle style, double indent = 20) =>
        new ListElement(ListStyle.Unordered) { Indent = indent }
            .Add(new ListItem([new TextRun(text, style)]) { Style = H(10) });

    /// <summary>
    /// A list ahead of a renderer of the caller's is laid out once, in the pass that draws, as the
    /// renderer is: the document decides before any pass whether the lists are their twins, so no
    /// pass lays out to find out. It still reports that it measured nothing, as one with no header
    /// or dependent does.
    /// </summary>
    [Fact]
    public void AListAheadOfACallersRenderer_isLaidOutOncePerPass()
    {
        var (renderer, _) = Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", H(10))));
            r.Add(new Holder(new ListRenderer(OneEntry("b", H(10))), 0));
        });

        Assert.Equal(0, renderer.MeasureRuns);
        Assert.Null(renderer.MeasurePlan);
        Assert.Equal(2, renderer.LayoutCalls);
    }

    [Fact]
    public void ADocumentWithOnlyBuiltInRenderers_isLaidOutOncePerPass()
    {
        var (renderer, _) = Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", H(10))));
            r.Add(new ParagraphRenderer(new Paragraph("x", H(10))));
        });

        Assert.Equal(2, renderer.LayoutCalls);
    }

    [Fact]
    public void ACallersRendererAheadOfTheLists_isLaidOutOncePerPass()
    {
        var (renderer, _) = Render(r =>
        {
            r.Add(new Holder(new ListRenderer(OneEntry("b", H(10))), 0));
            r.Add(new ListRenderer(OneEntry("a", H(10))));
        });

        Assert.Equal(2, renderer.LayoutCalls);
    }

    /// <summary>
    /// A document that holds a renderer of the caller's lays its lists out as their twins from the
    /// first pass. A list laid out the new way measures text that 2.3.3 never measured, here the
    /// space between two words that the clamp gives a width, which adds a glyph to the subset of an
    /// embedded font, so the saved file would differ from the one 2.3.3 wrote. The document
    /// compared here holds the renderer, so it is that file.
    /// </summary>
    [Fact]
    public void ADocumentThatHoldsACallersRenderer_addsNoGlyphToAFontSubset()
    {
        Document Build(bool twin)
        {
            var doc = Page(200, 400, 0, header: false);
            var own = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
            var ahead = new ListElement(ListStyle.Unordered) { Indent = 1000 }
                .Add(new ListItem([new TextRun("ab cd", new TextStyle { FontRef = own, FontSize = 10 })]) { Style = H(10) });
            Place(doc, ahead, Visible, twin);
            Place(doc, OneEntry("b", Bad("undefined", 10), 1000), 0, twin);
            return doc;
        }

        Assert.Equal(Outcome(Build(twin: true)), Outcome(Build(twin: false)));
    }

    /// <summary>
    /// A list builds its entries once for each layout, however many pages it spans: the pieces of
    /// a list that starts over carry the generation, and do not build the entries again for each
    /// page. Two builds are two lists in the one layout that draws.
    /// </summary>
    [Fact]
    public void AListThatSpansManyPages_buildsItsEntriesOncePerLayout()
    {
        var many = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < 200; i++) many.Add(new ListItem("w", H(10)));
        var doc = Page(200, 40, 0, header: false);
        doc.Add(many);
        doc.Add(new Holder(new ListRenderer(OneEntry("b", H(10))), 0));
        ListRenderer.BuildCalls = 0;

        Assert.StartsWith("OK ", Outcome(doc));

        Assert.Equal(2, ListRenderer.BuildCalls);
    }

    /// <summary>
    /// A header's page-counting pass is the one the document reports, whether or not a list holds
    /// the font.
    /// </summary>
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void AHeaderMeasure_runsOnceWhetherOrNotAListHoldsTheFont(bool bad, int measureRuns)
    {
        var (renderer, _) = Render(
            r =>
            {
                r.Add(new ListRenderer(OneEntry("a", H(10))));
                r.Add(new Holder(new ListRenderer(OneEntry("b", bad ? Bad("undefined", 10) : H(10), 1000)), 0));
            },
            header: true);

        Assert.Equal(measureRuns, renderer.MeasureRuns);
    }

    /// <summary>
    /// A list ahead of a held list leaves nothing behind. A paragraph keeps where its last layout
    /// placed it, and a list's Draw reads that when its own layout answers Nothing, so a held list
    /// whose entry text was placed on a page where it fits, and the drawing layout drops, would
    /// show the text. The reference has five paragraphs as tall as the entries of the list ahead
    /// of it; what the held list shows is the same.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public void AListAheadOfAHeldList_doesNotChangeWhatTheHeldListShows(int how)
    {
        Document Build(bool listAhead)
        {
            var doc = new Document { PageSize = new PdfRectangle(0, 0, 100, 150), Margins = EdgeInsets.Zero };
            if (listAhead)
            {
                var ahead = new ListElement(ListStyle.Unordered);
                for (var i = 0; i < 5; i++) ahead.Add(new ListItem("a", H(10)));
                doc.Add(ahead);
            }
            else
            {
                for (var i = 0; i < 5; i++) doc.Add(new Paragraph("a", H(10)) { Margins = EdgeInsets.Zero });
            }

            var held = new ListElement(ListStyle.OrderedDecimal);
            for (var i = 0; i < 3; i++)
            {
                held.Add(new ListItem([new TextRun("wd wd wd wd wd wd", H(14))]) { Style = H(20) });
            }

            doc.Add(new Holder(new ListRenderer(held), how));
            return doc;
        }

        var withList = Shows(Build(listAhead: true));
        var reference = Shows(Build(listAhead: false));

        // Each entry of the list ahead shows its marker and text; each paragraph shows its text.
        Assert.Equal(reference.Skip(5).ToArray(), withList.Skip(10).ToArray());
        Assert.Contains("1.", withList.Skip(10));
    }

    /// <summary>
    /// A document whose text depends on the plan measures until it settles, in two passes here,
    /// whether or not a list holds the font.
    /// </summary>
    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    public void ADependentThatSettles_isMeasuredTwice_whetherOrNotAListHoldsTheFont(bool bad, int measureRuns)
    {
        var (renderer, _) = Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", H(10))));
            r.Add(new Holder(new ListRenderer(OneEntry("b", bad ? Bad("undefined", 10) : H(10), 1000)), 0));
            r.Add(new Settled());
        });

        Assert.Equal(measureRuns, renderer.MeasureRuns);
    }

    /// <summary>
    /// The run belongs to the layout in progress. A layout started from inside a renderer of the
    /// caller's has a run of its own, and the outer one is as it was afterwards.
    /// </summary>
    [Fact]
    public void ALayoutInsideALayout_leavesTheOuterRunAsItWas()
    {
        Assert.Null(ListLayoutRun.Current);
        var inner = new Document();
        inner.Add(OneEntry("bad", Bad("undefined", 10)));
        var seen = new Capture();
        inner.Add(seen);
        var probe = new RunProbe(inner);
        var (renderer, _) = Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", H(10))));
            r.Add(probe);
        });

        Assert.NotNull(probe.Outer);
        Assert.Same(probe.Outer, probe.AfterInner);
        Assert.NotNull(seen.Run);
        Assert.NotSame(probe.Outer, seen.Run);
        Assert.False(probe.Outer.FoundUndrawable);
        Assert.True(seen.Run.FoundUndrawable);
        Assert.Null(ListLayoutRun.Current);
        Assert.Equal(0, renderer.MeasureRuns);
    }

    [Fact]
    public void TheRun_isGoneAfterALayoutThatThrows()
    {
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 100), new EdgeInsets(10));
        renderer.Add(new Throws());

        using var ms = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() => renderer.Render(ms));

        Assert.Null(ListLayoutRun.Current);
    }

    /// <summary>
    /// The run flows with the execution context: a list laid out through a task or on a thread
    /// started from the layout sees it. The rows with modes 4 and 5 above compare the outcomes.
    /// This one reads the run where those lists are laid out.
    /// </summary>
    [Fact]
    public void TheRun_isSeenOnAPoolThreadAndOnANewThread()
    {
        var seenOnPool = new Capture();
        var seenOnThread = new Capture();
        var (renderer, _) = Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", H(10))));
            r.Add(new Holder(seenOnPool, 4));
            r.Add(new Holder(seenOnThread, 5));
        });

        Assert.NotNull(seenOnPool.Run);
        Assert.NotNull(seenOnThread.Run);
        Assert.Same(seenOnPool.Run, seenOnThread.Run);
        Assert.Equal(0, renderer.MeasureRuns);
        Assert.Null(ListLayoutRun.Current);
    }

    /// <summary>
    /// Work queued with the execution context suppressed does not see the run, so a list laid out
    /// there reads none. The document's decision does not depend on it: it is taken from the
    /// elements added, before anything is laid out.
    /// </summary>
    [Fact]
    public void WorkQueuedWithoutTheExecutionContext_doesNotSeeTheRun()
    {
        var seen = new Capture();
        var queued = new QueuedWithoutContext(seen);

        Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", H(10))));
            r.Add(queued);
        });

        Assert.True(queued.Ran);
        Assert.Null(seen.Run);
    }

    /// <summary>
    /// Saves that run side by side, each on its own thread, keep the run of their own layout:
    /// every outcome is the one the same document gives alone, and no run is left behind.
    /// </summary>
    [Fact]
    public void SavesThatRunInParallel_eachKeepTheirOwnRun()
    {
        Document Build(int i) => ShiftedByAMove(i % 2 == 0 ? "undefined" : "ok", Visible, header: i % 3 == 0, twin: false, badFirst: false);
        var expected = Enumerable.Range(0, 12).Select(i => Outcome(Build(i))).ToArray();
        Assert.Contains(expected, o => o.StartsWith("EX ", StringComparison.Ordinal) || o.StartsWith("OK ", StringComparison.Ordinal));

        var actual = new string[expected.Length * 8];
        Parallel.For(0, actual.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 },
            k => actual[k] = Outcome(Build(k % expected.Length)));

        for (var k = 0; k < actual.Length; k++) Assert.Equal(expected[k % expected.Length], actual[k]);
        Assert.Null(ListLayoutRun.Current);
    }

    /// <summary>
    /// The run is set in the task SaveAsync starts, and none is left on the caller.
    /// </summary>
    [Fact]
    public async Task SaveAsync_givesTheOutcomeSaveGives()
    {
        var expected = Outcome(ShiftedByAMove("undefined", 4, header: false, twin: false, badFirst: false));
        var doc = ShiftedByAMove("undefined", 4, header: false, twin: false, badFirst: false);
        doc.Timestamp = Stamp;
        doc.DocumentId = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        using var ms = new MemoryStream();

        await doc.SaveAsync(ms, TestContext.Current.CancellationToken);

        Assert.Equal(expected, "OK " + Convert.ToHexString(SHA256.HashData(ms.ToArray()))[..16]);
        Assert.Null(ListLayoutRun.Current);
    }

    // ── What the document is asked about ─────────────────────────────────────

    private static bool Holds(ListElement list, PdfDocument? pdf = null)
    {
        var run = ListLayoutRun.Begin(pdf ?? new PdfDocument(), out var previous);
        try
        {
            return ListRenderer.HoldsUndrawableEntry(list, run);
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }

    [Fact]
    public void ARunInTheMarkersStyle_isRead()
    {
        var list = new ListElement(ListStyle.Unordered)
            .Add(new ListItem([new TextRun("ok", H(10))]) { Style = Bad("undefined", 10) });

        Assert.True(Holds(list));
    }

    [Fact]
    public void ACharacterlessRun_isNotRead()
    {
        var list = new ListElement(ListStyle.Unordered)
            .Add(new ListItem([new TextRun("", Bad("undefined", 10)), new TextRun("ok", H(10))]) { Style = H(10) });

        Assert.False(Holds(list));
    }

    [Fact]
    public void AnItemBuiltFromAString_isReadInTheStyleItInherits()
    {
        var inherits = new ListElement(ListStyle.Unordered) { DefaultStyle = Bad("undefined", 10) }
            .Add(new ListItem("text"));
        var own = new ListElement(ListStyle.Unordered) { DefaultStyle = Bad("undefined", 10) }
            .Add(new ListItem("text", H(10)));

        Assert.True(Holds(inherits));
        Assert.False(Holds(own));
    }

    [Fact]
    public void AChildsRuns_areRead_andAChildInheritsItsParentsStyle()
    {
        var run = new ListItem("p", H(10));
        run.AddChild(new ListItem([new TextRun("c", Bad("undefined", 10))]));
        var inherited = new ListItem("p", Bad("undefined", 10));
        inherited.AddChild(new ListItem("c"));
        var fine = new ListItem("p", H(10));
        fine.AddChild(new ListItem("c"));

        Assert.True(Holds(new ListElement(ListStyle.Unordered).Add(run)));
        Assert.True(Holds(new ListElement(ListStyle.Unordered).Add(inherited)));
        Assert.False(Holds(new ListElement(ListStyle.Unordered).Add(fine)));
    }

    [Fact]
    public void AHandleTheDocumentDefines_isDrawable_andOneItDoesNot_isNot()
    {
        var pdf = new PdfDocument();
        var own = pdf.UseTrueTypeFont(FontReportSupport.DejaVuBytes);

        Assert.False(Holds(OneEntry("x", new TextStyle { FontRef = own, FontSize = 10 }), pdf));
        Assert.True(Holds(OneEntry("x", new TextStyle { FontRef = ForeignHandle() }), new PdfDocument()));
    }

    /// <summary>
    /// With no document to ask, an embedded font cannot be said to be defined, and a Standard 14
    /// value is read from the enumeration.
    /// </summary>
    [Fact]
    public void WithNoRun_anEmbeddedFontIsNotDrawable_andAStandard14ValueIsRead()
    {
        Assert.False(ListLayoutRun.CanDraw(null, new TextStyle { FontRef = ForeignHandle() }));
        Assert.False(ListLayoutRun.CanDraw(null, new TextStyle { FontRef = (Standard14)99 }));
        Assert.True(ListLayoutRun.CanDraw(null, H(10)));
    }

    /// <summary>
    /// A font is asked about once however many runs use it, and asking about the next font gives
    /// that font's own answer, so the memo never answers for the wrong one.
    /// </summary>
    [Fact]
    public void TheMemoOfTheLastFont_neverAnswersForAnother()
    {
        var pdf = new PdfDocument();
        var own = pdf.UseTrueTypeFont(FontReportSupport.LiberationMonoBytes);
        var foreign = SecondForeignHandle();
        var run = ListLayoutRun.Begin(pdf, out var previous);
        try
        {
            var styles = new[]
            {
                new TextStyle { FontRef = own },
                new TextStyle { FontRef = own },
                new TextStyle { FontRef = foreign },
                new TextStyle { FontRef = foreign },
                new TextStyle { FontRef = own },
                H(10),
                new TextStyle { FontRef = (Standard14)99 },
                new TextStyle { FontRef = (Standard14)99 },
                H(10),
            };

            Assert.Equal(
                [true, true, false, false, true, true, false, false, true],
                styles.Select(s => ListLayoutRun.CanDraw(run, s)).ToArray());
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }

    /// <summary>
    /// Threads that ask about two Standard 14 values at once each get their own value's answer. The
    /// memo holds one value and its answer as a unit, so no thread reads one value with the
    /// other's answer. A race needs the threads to interleave a write, so the loop cannot force
    /// it; it fails on a memo that pairs a value with another's answer only when that happens.
    /// </summary>
    [Fact]
    public void TheMemoOfTheLastStandard14Value_answersEachThreadForItsOwnValue()
    {
        var run = ListLayoutRun.Begin(new PdfDocument(), out var previous);
        try
        {
            var defined = H(10);
            var undefined = new TextStyle { FontRef = (Standard14)99 };
            var wrong = 0;

            Parallel.For(0, 4, new ParallelOptions { MaxDegreeOfParallelism = 4 }, worker =>
            {
                for (var i = 0; i < 200_000; i++)
                {
                    var useDefined = ((i + worker) & 1) == 0;
                    if (ListLayoutRun.CanDraw(run, useDefined ? defined : undefined) != useDefined)
                        Interlocked.Increment(ref wrong);
                }
            });

            Assert.Equal(0, wrong);
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }

    /// <summary>
    /// A document that holds an entry it cannot draw lays every list out as its twin, and a
    /// caller's renderer after the list adds nothing to that. The two layouts are one for each
    /// renderer.
    /// </summary>
    [Fact]
    public void AnUndrawableEntryAheadOfACallersRenderer_isLaidOutOncePerPass()
    {
        var (renderer, _) = Render(r =>
        {
            r.Add(new ListRenderer(OneEntry("a", Bad("undefined", 10), indent: 1000)));
            r.Add(new Holder(new ListRenderer(OneEntry("b", H(10))), 0));
        });

        Assert.Equal(2, renderer.LayoutCalls);
    }

    /// <summary>
    /// Runs that alternate between two embedded fonts ask the document once for each font, not once
    /// for each change of font, and a font the document cannot draw is asked about again.
    /// </summary>
    [Fact]
    public void RunsThatAlternateTwoFonts_askTheDocumentOncePerFont()
    {
        var pdf = new PdfDocument();
        var mono = pdf.UseTrueTypeFont(FontReportSupport.LiberationMonoBytes);
        var sans = pdf.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        var run = ListLayoutRun.Begin(pdf, out var previous);
        try
        {
            for (var i = 0; i < 50; i++)
            {
                Assert.True(ListLayoutRun.CanDraw(run, new TextStyle { FontRef = mono }));
                Assert.True(ListLayoutRun.CanDraw(run, new TextStyle { FontRef = sans }));
            }

            Assert.Equal(2, run.DocumentQuestions);

            // A font the document cannot draw is asked about each time: a renderer of a caller's
            // may embed it while the layout runs, which turns the answer.
            var empty = ListLayoutRun.Begin(new PdfDocument(), out var outer);
            try
            {
                var foreign = new TextStyle { FontRef = ForeignHandle() };
                Assert.False(ListLayoutRun.CanDraw(empty, foreign));
                Assert.False(ListLayoutRun.CanDraw(empty, foreign));
                Assert.Equal(2, empty.DocumentQuestions);
            }
            finally
            {
                ListLayoutRun.End(outer);
            }
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// A renderer of a caller's that lays a list out through the context it was given, through
    /// <see cref="LayoutContext.WithArea"/> or through the public constructor.
    /// </summary>
    private sealed class Holder(IRenderer inner, int how) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            // Modes 4 and 5 lay the list out on a pool thread and on a thread of its own, through
            // the context, while this thread waits.
            if (how == 4) return Task.Run(() => inner.Layout(context)).GetAwaiter().GetResult();
            if (how == 5) return OnNewThread(() => inner.Layout(context));

            // Mode 3 measures the list at a narrower width first and lays it out through the
            // context afterwards, so the list sees two calls in a row at two widths.
            if (how == 3)
            {
                var area = context.Area;
                inner.Layout(context.WithArea(new LayoutBox(area.X, area.Y, area.Width * 0.75, area.Height)));
                return inner.Layout(context);
            }

            return inner.Layout(how switch
            {
                0 => context,
                1 => context.WithArea(context.Area),
                _ => new LayoutContext(context.Area, context.ContentTop),
            });
        }

        public void Draw(DrawContext ctx) => inner.Draw(ctx);

        private static LayoutResult OnNewThread(Func<LayoutResult> layout)
        {
            LayoutResult result = default!;
            Exception? error = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = layout();
                }
                catch (Exception ex)
                {
                    error = ex;
                }
            });
            thread.Start();
            thread.Join();
            if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
            return result;
        }
    }

    /// <summary>A renderer whose text depends on the plan and never changes.</summary>
    private sealed class Settled : IRenderer, IPlanDependent
    {
        public string TargetName => "settled";

        public string ResolvedText => "x";

        public bool Resolve(PaginationPlan plan) => false;

        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 10, 10));

        public void Draw(DrawContext ctx)
        {
        }
    }

    private sealed class Throws : IRenderer
    {
        private int _calls;

        public LayoutResult Layout(LayoutContext context) =>
            throw new InvalidOperationException("layout " + ++_calls);

        public void Draw(DrawContext ctx)
        {
        }
    }

    /// <summary>Runs its work on a pool thread the execution context does not flow to.</summary>
    private sealed class QueuedWithoutContext(Capture inner) : IRenderer
    {
        public bool Ran { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            using var done = new ManualResetEventSlim();
            LayoutResult result = default!;
            ThreadPool.UnsafeQueueUserWorkItem(
                _ =>
                {
                    result = inner.Layout(context);
                    Ran = true;
                    done.Set();
                },
                null);
            done.Wait();
            return result;
        }

        public void Draw(DrawContext ctx)
        {
        }
    }

    /// <summary>Notes the run it is laid out under.</summary>
    private sealed class Capture : IRenderer
    {
        public ListLayoutRun? Run { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            Run = ListLayoutRun.Current;
            return LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 1));
        }

        public void Draw(DrawContext ctx)
        {
        }
    }

    /// <summary>Saves another document from inside its layout and notes the run.</summary>
    private sealed class RunProbe(Document inner) : IRenderer
    {
        private bool _done;

        public ListLayoutRun? Outer { get; private set; }

        public ListLayoutRun? AfterInner { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            if (!_done)
            {
                _done = true;
                Outer = ListLayoutRun.Current;
                using var ms = new MemoryStream();
                try
                {
                    inner.Save(ms);
                }
                catch (IndexOutOfRangeException)
                {
                    // The font is the point of the document, not a failure of the test.
                }

                AfterInner = ListLayoutRun.Current;
            }

            return LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 10, 10));
        }

        public void Draw(DrawContext ctx)
        {
        }
    }
}
