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
/// that means for the list: any other context, an entry that cannot be drawn, an entry no page
/// holds and a first box with no height all give the placement the list had before, which is what
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

    /// <summary>
    /// The document the list is added to as a list, and the same document with its twin.
    /// </summary>
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
    /// A renderer that a caller laid out before the save keeps the entries that call built, as it
    /// did on 2.3.3: the document does not build them again at its own width, and does not clamp
    /// them. The list here has an indent no width is left beyond, so a clamp would give the text a
    /// width and draw it.
    /// </summary>
    [Fact]
    public void AListLaidOutBeforeTheSave_keepsTheEntriesThatCallBuilt()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 }
            .Add(new ListItem("clamped text", H(10))));
        renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 200, 100)));
        var pdf = new PdfDocument { Timestamp = Stamp, DocumentId = new byte[16] };
        var documentRenderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 200, 100), new EdgeInsets(10));
        documentRenderer.Add(renderer);

        using var ms = new MemoryStream();
        documentRenderer.Render(ms);

        Assert.DoesNotContain("clamped", string.Concat(Shows(ms.ToArray())), StringComparison.Ordinal);
        Assert.DoesNotContain(documentRenderer.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    // Thirty entries whose gutter leaves the text five points at a width of 25, so a layout at that
    // width wraps each word to a letter a line, and the paragraphs keep that wrap.
    private static ListElement NarrowList()
    {
        var list = new ListElement(ListStyle.OrderedRoman) { Indent = 20 };
        for (var i = 0; i < 30; i++) list.Add(new ListItem("word", H(10)));
        return list;
    }

    private static Document NarrowPage() =>
        new() { PageSize = new PdfRectangle(0, 0, 200, 2000), Margins = new EdgeInsets(10) };

    /// <summary>Lays the list out through the context it is given, as a caller does.</summary>
    private sealed class Passes(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    /// <summary>
    /// A caller's renderer after the list, which makes the document lay out once more.
    /// </summary>
    private sealed class Counts : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 1));

        public void Draw(DrawContext ctx)
        {
        }
    }

    /// <summary>
    /// The entries a caller built at another width are the ones the save draws, whether the list is
    /// added to the document or held by a renderer of the caller's, and whether or not the document
    /// lays out once more before it draws. Each row has the same document with the twin, which
    /// holds those entries as 2.3.3 did, as its reference.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void AListLaidOutBeforeTheSaveAtAnotherWidth_isDrawnFromTheEntriesThatCallBuilt(bool held, bool afterAList)
    {
        Document Build(bool twin, bool laidOutBefore)
        {
            var doc = NarrowPage();
            if (afterAList) doc.Add(new ListElement().Add(new ListItem("first", H(10))));
            var renderer = new ListRenderer(NarrowList());
            IRenderer added = twin ? renderer.Unclamped() : renderer;
            if (laidOutBefore) added.Layout(new LayoutContext(new LayoutBox(0, 0, 25, 1000)));
            doc.Add(held ? new Passes(added) : added);
            doc.Add(new Counts());
            return doc;
        }

        var kept = Save(Build(twin: false, laidOutBefore: true));
        var reference = Save(Build(twin: true, laidOutBefore: true));
        var fresh = Save(Build(twin: false, laidOutBefore: false));

        // The call laid out the entries that fit its 1000 points, and each of those is wrapped at
        // five points of text; the entries after them were wrapped when the document drew them.
        Assert.Equal(Sha(reference), Sha(kept));
        Assert.Contains("w", Shows(kept));
        Assert.True(Shows(kept).Count(t => t == "word") < 30);
        Assert.Equal(30, Shows(fresh).Count(t => t == "word"));
    }

    // An indent that leaves the first document's 180-point area no text, so the clamp narrows it,
    // and leaves the second document's 380-point area 130 points.
    private static ListElement ClampedAtTheFirstWidth() => new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
        .Add(new ListItem(ClampedText, H(10)));

    private const string ClampedText = "reused text that wraps over several lines";

    // An indent that clamps nothing, and a text that wraps differently at 160 and at 360 points.
    private static ListElement WrapsAtTheFirstWidth()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 20 };
        for (var i = 0; i < 3; i++) list.Add(new ListItem(string.Join(" ", Enumerable.Repeat("reused", 14)), H(10)));
        return list;
    }

    private static Document Narrow(double width) =>
        new() { PageSize = new PdfRectangle(0, 0, width, 300), Margins = new EdgeInsets(10) };

    // The second document's bytes after one renderer served a first document of 200 points.
    private static byte[] SecondDocument(Func<ListElement> make, string shape, bool twin)
    {
        var renderer = new ListRenderer(make());
        IRenderer Use(bool held) => held ? new Passes(twin ? renderer.Unclamped() : renderer) : twin ? renderer.Unclamped() : renderer;

        var first = Narrow(200);
        first.Add(Use(shape.StartsWith("held", StringComparison.Ordinal)));
        Save(first);
        var second = Narrow(400);
        second.Add(Use(shape.EndsWith("held", StringComparison.Ordinal)));
        return Save(second);
    }

    private static byte[] FreshSecondDocument(Func<ListElement> make)
    {
        var second = Narrow(400);
        second.Add(new ListRenderer(make()));
        return Save(second);
    }

    /// <summary>
    /// A renderer that served one document keeps the entries it built there, at the width of that
    /// document, when it serves another, as 2.3.3 kept them, whether it was added to each
    /// document or held by a renderer of a caller's in either. The reference is the twin, which
    /// holds the entries as 2.3.3 built them, and a list built again at the second document's
    /// width differs from it.
    /// </summary>
    [Theory]
    [InlineData("direct-direct", false)]
    [InlineData("held-direct", true)]
    [InlineData("direct-held", true)]
    [InlineData("direct-direct", true)]
    public void AListRendererReusedForASecondDocument_keepsTheEntriesTheFirstBuilt(string shape, bool wraps)
    {
        Func<ListElement> make = wraps ? WrapsAtTheFirstWidth : ClampedAtTheFirstWidth;

        var kept = SecondDocument(make, shape, twin: false);

        Assert.Equal(Sha(SecondDocument(make, shape, twin: true)), Sha(kept));
        if (wraps)
            Assert.NotEqual(Sha(FreshSecondDocument(make)), Sha(kept));

        // The clamp narrowed the first document's gutter to the widest word, which would have put
        // the text on one line in the second. 2.3.3 kept the indent, so the text wraps at 130
        // points.
        else
            Assert.DoesNotContain(ClampedText, Shows(kept));
    }

    // The twenty-seventh roman marker, "xxvii.", is wider than the indent, so the gutter widens to
    // it when the entry's longest word still fits beside it, and stays at the indent when it does
    // not. The word is wider than the first document's area and fits the second's.
    private static ListElement GutterThatDependsOnTheWidth()
    {
        var list = new ListElement(ListStyle.OrderedRoman) { Indent = 20 };
        for (var i = 0; i < 26; i++) list.Add(new ListItem("a", H(10)));
        return list.Add(new ListItem(new string('w', 30), H(10)));
    }

    // The same with an indent that leaves the first document's area no text, so the clamp narrows
    // the entries there, and a marker of 85 points that is wider than the indent. The twin builds
    // the entries the clamp narrowed anew, and the width it builds them at decides their gutter.
    private static ListElement ClampedGutterThatDependsOnTheWidth()
    {
        var list = new ListElement(ListStyle.OrderedRoman) { Indent = 185 };
        for (var i = 0; i < 26; i++) list.Add(new ListItem("a", H(10)));
        return list.Add(new ListItem("w", H(85)));
    }

    /// <summary>
    /// The entries the second document keeps are built at the first document's width: a gutter the
    /// first width left at the indent stays there, though the second width would widen it, for an
    /// entry the clamp narrowed as for one it left alone.
    /// </summary>
    [Theory]
    [InlineData("direct-direct", false)]
    [InlineData("held-direct", false)]
    [InlineData("direct-held", false)]
    [InlineData("direct-direct", true)]
    [InlineData("held-direct", true)]
    [InlineData("direct-held", true)]
    public void AListRendererReusedForASecondDocument_keepsTheGutterTheFirstWidthGave(string shape, bool clamped)
    {
        Func<ListElement> make = clamped ? ClampedGutterThatDependsOnTheWidth : GutterThatDependsOnTheWidth;

        var kept = SecondDocument(make, shape, twin: false);

        Assert.Equal(Sha(SecondDocument(make, shape, twin: true)), Sha(kept));
        Assert.NotEqual(Sha(FreshSecondDocument(make)), Sha(kept));
    }

    private static ListElement TwoItems() => new ListElement(ListStyle.OrderedDecimal)
        .Add(new ListItem("one", H(10)))
        .Add(new ListItem("two", H(10)));

    // The list is changed after the first document; the second must not read what changed.
    private static void Change(ListElement list, string kind)
    {
        switch (kind)
        {
            case "plain": list.Add(new ListItem("added", H(10))); break;
            case "child": list.Items[0].AddChild("child", H(10)); break;
            case "undefined":
                list.Add(new ListItem("added", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 }));
                break;
            case "nan":
                list.Add(new ListItem("added", new TextStyle
                {
                    FontRef = new FontReference(Standard14.Helvetica),
                    FontSize = 10,
                    Color = new ColorRgb(double.NaN, 0, 0),
                }));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    // The second document's bytes after one renderer served a first document of 200 points and the
    // list was changed, or was not, before the second.
    private static byte[] SecondDocumentAfterChange(
        Func<ListElement> make, string? kind, bool firstHeld, bool secondHeld, bool twin, double secondWidth = 200)
    {
        var list = make();
        var renderer = new ListRenderer(list);
        IRenderer Use(bool held) => held ? new Passes(twin ? renderer.Unclamped() : renderer) : twin ? renderer.Unclamped() : renderer;

        var first = Page(200, 100, 1);
        first.Add(Use(firstHeld));
        Save(first);
        if (kind is not null) Change(list, kind);
        var second = Page(secondWidth, 100, 1);
        second.Add(Use(secondHeld));
        return Save(second);
    }

    /// <summary>
    /// A renderer whose list changed after the first document keeps the entries that document built
    /// and never reads the change, as 2.3.3 did: an added item, an added child, an item in a font
    /// the document cannot draw and an item with a colour that cannot be written leave the second
    /// document the one an unchanged list gives. The references are the twin, which keeps the
    /// entries it built, and the unchanged list.
    /// </summary>
    [Theory]
    [InlineData("plain", false, false)]
    [InlineData("plain", true, false)]
    [InlineData("plain", false, true)]
    [InlineData("child", false, false)]
    [InlineData("child", true, false)]
    [InlineData("undefined", false, false)]
    [InlineData("undefined", true, false)]
    [InlineData("undefined", false, true)]
    [InlineData("nan", false, false)]
    [InlineData("nan", true, false)]
    [InlineData("nan", false, true)]
    public void AListChangedBetweenTwoDocuments_isDrawnFromTheEntriesTheFirstBuilt(
        string kind, bool firstHeld, bool secondHeld)
    {
        var kept = SecondDocumentAfterChange(TwoItems, kind, firstHeld, secondHeld, twin: false);

        Assert.Equal(["lead0", "1.", "one", "2.", "two"], Shows(kept));
        Assert.Equal(Sha(SecondDocumentAfterChange(TwoItems, null, firstHeld, secondHeld, twin: false)), Sha(kept));
        Assert.Equal(Sha(SecondDocumentAfterChange(TwoItems, kind, firstHeld, secondHeld, twin: true)), Sha(kept));
    }

    /// <summary>
    /// An entry the first document's clamp narrowed is not an entry 2.3.3 built. The second
    /// document lays it out as 2.3.3 built it in the first, from the text it had then and without
    /// the clamp, and not from the list as it is now: a child added to the narrowed item moves
    /// every entry after it, and an item added after it is not there.
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("child")]
    public void AListChangedAfterTheClampNarrowedAnEntry_keepsTheEntryAsTheFirstDocumentBuiltItWithoutTheClamp(string kind)
    {
        ListElement Make() => ClampedAtTheFirstWidth().Add(new ListItem("second entry", H(10)));

        var kept = SecondDocumentAfterChange(Make, kind, false, false, twin: false, secondWidth: 400);

        Assert.Equal(Sha(SecondDocumentAfterChange(Make, null, false, false, twin: false, secondWidth: 400)), Sha(kept));
        Assert.Equal(Sha(SecondDocumentAfterChange(Make, kind, false, false, twin: true, secondWidth: 400)), Sha(kept));
        Assert.DoesNotContain(ClampedText, Shows(kept));
        Assert.DoesNotContain("added", Shows(kept));
        Assert.DoesNotContain("child", Shows(kept));
    }

    // A flat indent of 250 leaves the first document's 180-point area no text, so the clamp narrows
    // the entry there, and the marker is 20 points where the text is 10.
    private static ListElement ClampedWithATallMarker() => new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
        .Add(new ListItem([new TextRun("small text", H(10))]) { Style = H(20) });

    // The child's gutter is twice the indent, 200, which leaves the first document's 180-point
    // area no text for the child, and 200 points in the second document's 400.
    private static ListElement ClampedChild()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 100 };
        list.Add(new ListItem("parent", H(10)).AddChild(string.Join(" ", Enumerable.Repeat("reused", 14)), H(10)));
        return list;
    }

    private static byte[] SecondDocumentWithLeads(Func<ListElement> make, bool twin, int leads)
    {
        var renderer = new ListRenderer(make());
        IRenderer Use() => twin ? renderer.Unclamped() : renderer;

        var first = Narrow(200);
        first.Add(Use());
        Save(first);
        var second = leads == 0 ? Narrow(400) : Page(400, 100, leads);
        second.Add(Use());
        return Save(second);
    }

    /// <summary>
    /// The second document lays out an entry the first document's clamp narrowed as an entry no
    /// clamp touched. A marker that does not fit what is left of a page and text that does are
    /// drawn as the twin draws them, the text without the marker, and the clamp's rule that the
    /// text of an entry whose marker is refused is refused too does not apply to it.
    /// </summary>
    [Fact]
    public void AnEntryTheFirstDocumentsClampNarrowed_isNotHeldToTheClampsRuleInTheSecond()
    {
        var kept = SecondDocumentWithLeads(ClampedWithATallMarker, twin: false, leads: 5);

        Assert.Equal(Sha(SecondDocumentWithLeads(ClampedWithATallMarker, twin: true, leads: 5)), Sha(kept));
        Assert.Contains("small text", Shows(kept));
    }

    /// <summary>
    /// The text the second document lays out for an entry the first document's clamp narrowed
    /// carries the item's language into the structure tree, as the entry's text does where no clamp
    /// narrowed it.
    /// </summary>
    [Fact]
    public void AnEntryTheFirstDocumentsClampNarrowed_keepsItsLanguageInTheSecond()
    {
        byte[] Second(bool twin, string? language)
        {
            var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
                .Add(new ListItem(ClampedText, H(10)) { Language = language }));
            IRenderer Use() => twin ? renderer.Unclamped() : renderer;

            var first = Narrow(200);
            first.Add(Use());
            Save(first);
            var second = new Document { Tagged = true, PageSize = new PdfRectangle(0, 0, 400, 300), Margins = new EdgeInsets(10) };
            second.Add(Use());
            return Save(second);
        }

        var kept = Second(twin: false, "fr-FR");

        Assert.Equal(Sha(Second(twin: true, "fr-FR")), Sha(kept));
        Assert.NotEqual(Sha(Second(twin: false, null)), Sha(kept));
    }

    /// <summary>
    /// A reused list that the second document lays out as its twin because the document holds an
    /// entry it cannot draw reports the entries the first document's clamp narrowed: they are the
    /// entries the clamp would have narrowed, which the fallback reports as its count.
    /// </summary>
    [Fact]
    public void AReusedListInADocumentThatHoldsAnUndrawableEntry_reportsTheEntriesTheFirstClampNarrowed()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
            .Add(new ListItem("first clamped entry", H(10)))
            .Add(new ListItem("second clamped entry", H(10))));
        var first = Narrow(200);
        first.Add(renderer);
        Save(first);
        Assert.Equal(2, first.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClamped).Count);

        var second = Narrow(400);
        second.Add(renderer);
        second.Add(new ListElement(ListStyle.Unordered) { Indent = 1000 }
            .Add(new ListItem([new TextRun("bad", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 })])
            { Style = H(10) }));
        Save(second);

        var skipped = second.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
        Assert.Equal(2, skipped.Length);
        Assert.Equal(2, skipped[0].Count);
    }

    private static TextStyle Undefined() => new() { FontRef = new FontReference((Standard14)99), FontSize = 10 };

    /// <summary>
    /// A list reused for a second document keeps an entry in a font only the first document
    /// defines. The second document finds that in the entries it keeps, so every list of the
    /// second document is laid out as its twin, as 2.3.3 laid them out: a later list that would
    /// move an entry is not moved.
    /// </summary>
    [Fact]
    public void AReusedListWhoseKeptEntryUsesTheFirstDocumentsFont_makesEveryListOfTheSecondDocumentItsTwin()
    {
        byte[] Second(bool twinForTheSecondList)
        {
            var first = Page(200, 100, 4);
            var style = new TextStyle { FontRef = first.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 10 };
            var renderer = new ListRenderer(new ListElement(ListStyle.Unordered) { Indent = 1000 }
                .Add(new ListItem([new TextRun("embedded", style)]) { Style = H(10) }));
            first.Add(renderer);
            Save(first);
            var second = Page(200, 100, 4);
            second.Add(new Passes(renderer));
            var tall = new ListRenderer(TallTextEntry());
            second.Add(twinForTheSecondList ? tall.Unclamped() : tall);
            return Save(second);
        }

        var kept = Second(twinForTheSecondList: false);

        Assert.Equal(Sha(Second(twinForTheSecondList: true)), Sha(kept));
        Assert.DoesNotContain("dropped", Shows(kept));
    }

    /// <summary>
    /// What an entry already built can draw is read from its two paragraphs: its marker's style,
    /// and each run of its text that holds any. A run with no text is not read.
    /// </summary>
    [Fact]
    public void WhatAnEntryAlreadyBuiltCanDraw_isReadFromItsMarkerAndItsRunsThatHoldText()
    {
        ParagraphRenderer Para(params TextRun[] runs) => new(new Paragraph(runs));
        var ok = H(10);
        var run = ListLayoutRun.Begin(new PdfDocument(), out var previous);
        try
        {
            Assert.True(ListRenderer.CanDrawEntry(run, Para(new TextRun("1.", ok)), Para(new TextRun("text", ok))));
            Assert.False(ListRenderer.CanDrawEntry(run, Para(new TextRun("1.", Undefined())), Para(new TextRun("text", ok))));
            Assert.False(ListRenderer.CanDrawEntry(run, Para(new TextRun("1.", ok)), Para(new TextRun("text", Undefined()))));
            Assert.False(ListRenderer.CanDrawEntry(
                run, Para(new TextRun("1.", ok)), Para(new TextRun("a", ok), new TextRun("b", Undefined()))));
            Assert.True(ListRenderer.CanDrawEntry(
                run, Para(new TextRun("1.", ok)), Para(new TextRun("a", ok), new TextRun(string.Empty, Undefined()))));
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }

    /// <summary>
    /// A child the first document's clamp narrowed keeps the gutter it has without the clamp, twice
    /// the indent, in the second document, where that gutter leaves it text.
    /// </summary>
    [Fact]
    public void AChildTheFirstDocumentsClampNarrowed_keepsItsGutterInTheSecond()
    {
        var kept = SecondDocumentWithLeads(ClampedChild, twin: false, leads: 0);

        Assert.Equal(Sha(SecondDocumentWithLeads(ClampedChild, twin: true, leads: 0)), Sha(kept));
    }

    // An entry whose text is 20 points in a 10-point style: it fits a page top, and not what is
    // left of a short page after a few lines.
    private static ListElement TallTextEntry() => new ListElement(ListStyle.OrderedDecimal)
        .Add(new ListItem([new TextRun("dropped", H(20))]) { Style = H(10) })
        .Add(new ListItem("next", H(10)));

    /// <summary>
    /// A renderer added to a document and then held by a renderer of the caller's, in the same
    /// document, lays the second placement out from entries of its own. It does not take the ones
    /// the first placement built and laid out under this save, whose paragraphs keep where that
    /// placement put them. Here the second placement falls where the entry's text does not fit, so
    /// it draws what a renderer that placed nothing before draws there: the text is dropped.
    /// </summary>
    [Fact]
    public void ARendererAddedAndThenHeldInOneDocument_laysTheHeldOneOutFromEntriesOfItsOwn()
    {
        Document Build(bool reused)
        {
            var renderer = new ListRenderer(TallTextEntry());
            var doc = Page(200, 100);
            doc.Add(renderer);
            doc.Add(new Paragraph("between0", H(10)) { Margins = EdgeInsets.Zero });
            doc.Add(new Paragraph("between1", H(10)) { Margins = EdgeInsets.Zero });
            doc.Add(new Passes(reused ? renderer : new ListRenderer(TallTextEntry())));
            return doc;
        }

        var reused = Save(Build(reused: true));

        Assert.Equal(Sha(Save(Build(reused: false))), Sha(reused));
        Assert.Equal(1, Shows(reused).Count(t => t == "dropped"));
    }

    /// <summary>
    /// A list renderer reused for a second document reports nothing there, as one that a caller
    /// laid out before the save: the document did not place the list the way it placed it first.
    /// </summary>
    [Fact]
    public void AListRendererReusedForASecondDocument_reportsNothingThere()
    {
        var renderer = new ListRenderer(ClampedAtTheFirstWidth());
        var first = Narrow(200);
        first.Add(renderer);
        Save(first);
        Assert.Contains(LayoutDiagnosticCode.ListIndentClamped, Codes(first));

        var second = Narrow(400);
        second.Add(renderer);
        Save(second);

        Assert.DoesNotContain(second.Diagnostics, d => d.Code is LayoutDiagnosticCode.ListIndentClamped
            or LayoutDiagnosticCode.ListIndentClampSkipped or LayoutDiagnosticCode.ListEntryPartDropped);
    }

    /// <summary>
    /// The document places the same object in the measuring pass and the drawing pass.
    /// </summary>
    [Fact]
    public void TheTwin_isTheSameObjectOnEveryCall()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal).Add("a"));

        Assert.Same(((IContinuationTwin)renderer).Twin(), ((IContinuationTwin)renderer).Twin());
        Assert.Same(renderer.Unclamped(), ((IContinuationTwin)renderer).Twin());
    }

    // ── A first box with no height ───────────────────────────────────────────

    /// <summary>
    /// A paragraph whose leading fills the 20-point area leaves the list a box with no height,
    /// which it answers Nothing for before it builds an item. The decision is made at the retry on
    /// the next page, where the list reports, so the list that would pass the limit is placed as it
    /// was.
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
    /// A run in an embedded font that another document returned would be refused at the save once
    /// its text is drawn, and the document never drew it. The document holds an entry it cannot
    /// draw, so its list is laid out as its twin, the save is the one the list had, and the
    /// fallback is reported.
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
        Assert.Equal([LayoutDiagnosticCode.ListIndentClampSkipped], Codes(doc));
    }

    /// <summary>
    /// The same entry at a page bottom: main dropped the text mid-page and saved. The entry is not
    /// moved, because drawing it on the next page would refuse the save, and the part dropped is
    /// reported.
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
        Assert.Equal(
            [LayoutDiagnosticCode.ListIndentClampSkipped, LayoutDiagnosticCode.ListEntryPartDropped], Codes(doc));
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
        // An undefined Standard 14 value measures zero, which the clamp never narrows, so the clamp
        // row uses a handle another document returned, which measures.
        var undefined = rule == "move" ? StyleOf((Standard14)99, 20) : StyleOf(ForeignHandle());
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

    /// <summary>
    /// The control: a font this document defines is drawn, so the entry is clamped and moved.
    /// </summary>
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
    /// offers the same top. Every entry fits a page of its own where the document lays the list
    /// out, so the document never offers a top that is smaller than that page, but a context that
    /// does leaves the entry where it falls.
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
    /// instance it called for the placements of the list's anchors and of its items'. Both come
    /// from the twin, so the destinations are the ones the twin writes.
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
    /// holds, so an entry ahead of it that would move stays where it falls, and the list is placed
    /// as before.
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
    /// Text with no width is discarded, not unplaced: an entry whose marker wraps and gets some of
    /// its lines mid-page, beside text with no width, moves to the page that holds the marker
    /// whole.
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
    /// entry raises is the layout's to raise where it reached it, so the refusal the list reports
    /// is the one it always did: an element too tall, before the colour of a later entry.
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

    /// <summary>
    /// A renderer of a caller's that lays another renderer out, through the context or through one
    /// it makes.
    /// </summary>
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

    /// <summary>
    /// Reserves a band above the renderer it wraps and lays that out in the rest through
    /// <c>WithArea</c>.
    /// </summary>
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
