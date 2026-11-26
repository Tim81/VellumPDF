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
/// A list builds its entries from the items it holds in the save that lays it out, once for the
/// document's own calls and once for the calls a renderer of the caller's makes (#691). A layout
/// that came first in another save, before the save, or from a renderer of the caller's that holds
/// the list leaves nothing the document reads. The reference for every row is the same document
/// built over a renderer for each kind of call, none of which was laid out before.
/// </summary>
public sealed class ListEarlierLayoutTests
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

    private const double PageWidth = 110;
    private const double PageHeight = 100;

    private static Document Page(double width) =>
        new() { PageSize = new PdfRectangle(0, 0, width, PageHeight), Margins = new EdgeInsets(10) };

    private static ListElement Make(string kind) => kind switch
    {
        // Text that wraps at the width it is laid out at.
        "wrap" => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("WWW ab cd ef gh ij", H(10)))
            .Add(new ListItem("kl mn op qr st uv wx", H(10))),

        // An indent no page width leaves any text, so the new layout clamps both entries.
        "clamp" => new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
            .Add(new ListItem("clamped text", H(10)))
            .Add(new ListItem("second one", H(10))),

        // A 30 pt marker beside 10 pt text: the gutter is the marker's width where the area leaves
        // the widest word room, and the indent where it does not.
        _ => new ListElement(ListStyle.OrderedDecimal) { Indent = 10 }
            .Add(new ListItem([new TextRun("WWWWW ab", H(10))]) { Style = H(30) })
            .Add(new ListItem([new TextRun("WWWWW cd", H(10))]) { Style = H(30) }),
    };

    /// <summary>
    /// A renderer of the caller's. <c>how</c> 0 lays the inner renderer out through the context
    /// it was given, 1 through <see cref="LayoutContext.WithArea"/> at the width factor, 2 through
    /// the public constructor at the factor, and 3 through <see cref="LayoutContext.WithArea"/> at
    /// three quarters of the width first and then through the context.
    /// </summary>
    private sealed class Caller(IRenderer inner, int how, double factor) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var a = context.Area;
            var box = new LayoutBox(a.X, a.Y, a.Width * factor, a.Height);
            LayoutResult result;
            switch (how)
            {
                case 1: result = inner.Layout(context.WithArea(box)); break;
                case 2: result = inner.Layout(new LayoutContext(box)); break;
                case 3:
                    inner.Layout(context.WithArea(new LayoutBox(a.X, a.Y, a.Width * 0.75, a.Height)));
                    result = inner.Layout(context);
                    break;
                default: result = inner.Layout(context); break;
            }

            return result.Status switch
            {
                LayoutResult.Outcome.Full => LayoutResult.Full(
                    new LayoutBox(a.X, a.Y, a.Width, result.OccupiedArea!.Value.Height)),
                LayoutResult.Outcome.Partial => LayoutResult.Partial(
                    new LayoutBox(a.X, a.Y, a.Width, result.OccupiedArea!.Value.Height),
                    new Caller(result.SplitRenderer!, 0, 1),
                    new Caller(result.OverflowRenderer!, how, factor)),
                _ => LayoutResult.Nothing(),
            };
        }

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    private static IRenderer Held(string how, IRenderer renderer) => how switch
    {
        "ctx" => new Caller(renderer, 0, 1),
        "wa75" => new Caller(renderer, 1, 0.75),
        "wa150" => new Caller(renderer, 1, 1.5),
        "ctor75" => new Caller(renderer, 2, 0.75),
        _ => new Caller(renderer, 3, 1),
    };

    private static ListRenderer Renderer(string kind, out ListElement list)
    {
        list = Make(kind);
        return new ListRenderer(list);
    }

    /// <summary>
    /// Runs one scenario and returns the last document's bytes. <paramref name="earlier"/> is the
    /// layout that comes first and is not the document's own call: a renderer of the caller's that
    /// holds the list in the same document, a layout before the save, or another document's save.
    /// <paramref name="current"/> is the placement that follows, in the document saved last. With
    /// <paramref name="fresh"/>, each layout reads a renderer of its own kind: the layouts before
    /// the save a renderer that nothing reads again, a held layout in the document the renderer
    /// for the caller's calls, and the document's own placement the renderer for its calls.
    /// </summary>
    private static byte[] Scenario(string earlier, string current, string kind, bool fresh)
    {
        var renderer = Renderer(kind, out var list);
        var other = new ListRenderer(list);
        var callers = fresh ? new ListRenderer(list) : renderer;
        var width = PageWidth;
        var doc = Page(width);

        if (earlier.StartsWith("pre"))
        {
            var factor = earlier == "pre75" ? 0.75 : 1.5;
            (fresh ? other : renderer).Layout(new LayoutContext(new LayoutBox(10, 10, width * factor - 20, PageHeight - 20)));
        }
        else if (earlier.StartsWith("doc"))
        {
            var factor = earlier.EndsWith("75") ? 0.75 : 1.5;
            var first = Page(width * factor);
            var served = fresh ? other : renderer;
            first.Add(earlier.StartsWith("doc-held") ? Held("ctx", served) : served);

            // An element near the continuation limit makes that document lay every list out as its
            // twin, so the entries the list kept are built by the twin and not by the list.
            if (earlier.StartsWith("doc-near")) first.Add(new Preformatted(new string('	', 1_700), H(1)) { TabWidth = 32 });
            Save(first);
        }
        else
        {
            doc.Add(Held(earlier, callers));
            doc.Add(new Paragraph("between", H(10)) { Margins = EdgeInsets.Zero });
        }

        doc.Add(current == "direct" ? renderer : Held(current, callers));
        return Save(doc);
    }

    /// <summary>
    /// Whichever shape of layout came first, and whichever shape comes after it, the document lays
    /// the list out from the items it holds in this save: the bytes are those of a renderer for each
    /// kind of call that was never laid out before, for a list that wraps, one that clamps and one
    /// whose gutter depends on the width, at an equal, a narrower and a wider first width.
    /// </summary>
    [Theory]
    [InlineData("ctx", "direct")]
    [InlineData("wa75", "direct")]
    [InlineData("wa150", "direct")]
    [InlineData("ctor75", "direct")]
    [InlineData("measure", "direct")]
    [InlineData("pre75", "direct")]
    [InlineData("pre150", "direct")]
    [InlineData("doc-held75", "direct")]
    [InlineData("doc-direct75", "direct")]
    [InlineData("doc-direct150", "direct")]
    [InlineData("doc-near75", "direct")]
    [InlineData("doc-near150", "direct")]
    [InlineData("doc-near75", "wa150")]
    [InlineData("ctx", "wa150")]
    [InlineData("wa75", "wa150")]
    [InlineData("measure", "wa150")]
    [InlineData("pre75", "wa150")]
    [InlineData("doc-direct75", "wa150")]
    [InlineData("doc-direct150", "ctx")]
    public void WhateverLaidTheListOutFirst_theDocumentLaysItOutFromWhatTheListHoldsNow(string earlier, string current)
    {
        foreach (var kind in new[] { "wrap", "clamp", "gutter" })
        {
            var reference = Sha(Scenario(earlier, current, kind, fresh: true));
            Assert.True(reference == Sha(Scenario(earlier, current, kind, fresh: false)), $"{kind}: {earlier} then {current}");
        }
    }

    /// <summary>
    /// A renderer of the caller's lays the list out at three quarters of the width, and the
    /// document then places the same renderer itself: the document draws that placement from
    /// entries built at the page's own width, where the gutter is the marker's and the text wraps
    /// once, and the caller's entries at three quarters of the width are not read.
    /// </summary>
    [Fact]
    public void AListHeldByACallersRendererAndThenAddedToTheDocument_isDrawnFromEntriesBuiltAtThePagesWidth()
    {
        var shown = Shows(Scenario("measure", "direct", "gutter", fresh: false));

        Assert.Equal(Shows(Scenario("measure", "direct", "gutter", fresh: true)), shown);

        // The first placement, the paragraph and the second placement: the second keeps "WWWWW ab"
        // on one line, as the entries built at the page's own width do. The held renderer makes the
        // document lay every list out as it was, so the second entry falls where its 30 pt marker
        // does not fit and keeps the text that does.
        var second = shown.Skip(shown.ToList().IndexOf("between")).ToArray();
        Assert.Equal(["between", "1.", "WWWWW ab", "WWWWW cd"], second);
    }

    /// <summary>
    /// A list the document placed first and a renderer of the caller's holds afterwards keeps the
    /// entries the document built. The renderer makes the document lay every list out as it was, so
    /// the two placements the document makes report that, naming the renderer, and the placement
    /// the renderer holds reports nothing.
    /// </summary>
    [Fact]
    public void AListThePlacementBuiltAndACallersRendererThenHolds_isReportedAtEachPlacementTheDocumentMakes()
    {
        var renderer = new ListRenderer(Make("clamp"));
        var doc = Page(PageWidth);
        doc.Add(renderer);
        doc.Add(Held("ctx", renderer));
        doc.Add(renderer);
        Save(doc);

        var skipped = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
        Assert.Equal([0, 2], skipped.Select(d => d.ElementIndex).Order().ToArray());
        Assert.All(skipped, d => Assert.Contains("a renderer of yours", d.Message, StringComparison.Ordinal));
        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListIndentClamped);
    }

    /// <summary>
    /// A list with an entry no page holds is placed as it was whole, and the document reports it
    /// each time it places the list. The entries the twin built for the first placement are the
    /// document's own, so they do not turn the second placement into a held one that reports
    /// nothing.
    /// </summary>
    [Fact]
    public void AListWithAnEntryNoPageHolds_isReportedAtEachPlacementOfTheSameRenderer()
    {
        var renderer = new ListRenderer(new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(500) }));
        var doc = Page(200);
        doc.Add(renderer);
        doc.Add(new Paragraph("between", H(10)) { Margins = EdgeInsets.Zero });
        doc.Add(renderer);
        Save(doc);

        Assert.Equal(
            [0, 2],
            doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListEntryPartDropped)
                .Select(d => d.ElementIndex).Order().ToArray());
    }

    // A renderer of the caller's that returns the list's own result, so the document calls the
    // pieces of the list itself.
    private sealed class PassThrough(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    // Six entries of a 30 pt marker beside 10 pt text on a page that holds two of them, so the
    // list splits into three pieces and the document calls the second and third itself.
    private static ListRenderer SplitList(bool twin, bool midItem)
    {
        // The mid-item list holds two entries whose text runs over several pages, so the document
        // calls a piece that resumes inside the entry.
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 10 };
        for (var i = 0; i < (midItem ? 2 : 6); i++)
        {
            list.Add(new ListItem([new TextRun(midItem ? string.Join(' ', Enumerable.Repeat("WWWWW", 40)) : "WWWWW ab", H(10))])
            { Style = H(30) });
        }

        var renderer = new ListRenderer(list);
        return twin ? renderer.Unclamped() : renderer;
    }

    // Draws nothing and takes no height.
    private sealed class Empty : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext ctx)
        {
        }
    }

    // A renderer of the caller's that hands the document the first piece of the list as the
    // overflow, with nothing of its own in front of it, so the document lays that first piece out
    // itself on the next page. It is one way a first piece reaches the document without a
    // downcast; the other is a piece the caller adds itself (SplitDocument, "addPiece").
    private sealed class Defer(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var result = inner.Layout(context);
            return result.Status == LayoutResult.Outcome.Partial
                ? LayoutResult.Partial(context.Area.WithHeight(0), new Empty(), result.SplitRenderer!)
                : result;
        }

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    private static Document SplitDocument(string shape, bool twin, double height = 80, int leads = 0)
    {
        var renderer = SplitList(twin, shape.EndsWith("Mid"));
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, height),
            Margins = new EdgeInsets(10),
            // The reference is the list as it was, which the document placing it directly reports.
            // A document that holds a renderer of the caller's reports every list it places, the
            // one over the entries that renderer built included, and the rows check that report.
            StrictDiagnostics = !twin && !shape.StartsWith("heldThenDirect"),
        };

        for (var i = 0; i < leads; i++) doc.Add(new Paragraph("lead" + i, H(10)) { Margins = EdgeInsets.Zero });

        if (shape.StartsWith("passThrough"))
        {
            doc.Add(new PassThrough(renderer));
        }
        else if (shape.StartsWith("deferred"))
        {
            doc.Add(new Defer(renderer));
        }
        else if (shape.StartsWith("addPiece"))
        {
            // The caller lays the list out before the save through the public constructor and adds
            // the first piece, a renderer of the library's own, to the document.
            var result = renderer.Layout(new LayoutContext(new LayoutBox(10, 10, 180, height - 20)));
            doc.Add(result.Status == LayoutResult.Outcome.Partial ? result.SplitRenderer! : renderer);
        }
        else
        {
            // Held by a caller's renderer first, which builds the entries, and then placed by the
            // document over them.
            doc.Add(Held("ctx", renderer));
            doc.Add(new PageBreak());
            doc.Add(renderer);
        }

        return doc;
    }

    /// <summary>
    /// The pieces of a list a renderer of the caller's holds take that placement, so the document
    /// calling a piece itself does not make the piece one it placed. A caller that returns the
    /// list's own result save with the bytes of the list as it was and report neither 207 nor
    /// 208, with <see cref="Document.StrictDiagnostics"/> set. So does a caller that hands the
    /// document the first piece as its overflow, so that the document lays the first piece out
    /// itself, and a caller that adds the first piece to the document itself after laying the list
    /// out, for a list that splits between entries and for one that splits inside an entry, on
    /// several page heights and with lines ahead of the list. A list the document places over
    /// entries a caller's renderer built has the bytes of the list as it was too, and reports the
    /// fallback naming that renderer, because the document then holds a renderer of the caller's.
    /// </summary>
    [Theory]
    [InlineData("passThrough")]
    [InlineData("heldThenDirect")]
    [InlineData("passThroughMid")]
    [InlineData("heldThenDirectMid")]
    [InlineData("deferred")]
    [InlineData("deferredMid")]
    [InlineData("addPiece")]
    [InlineData("addPieceMid")]
    public void ThePiecesOfAHeldList_reportNothingUnlessTheDocumentPlacesThem(string shape)
    {
        var deferred = shape.StartsWith("deferred", StringComparison.Ordinal)
            || shape.StartsWith("addPiece", StringComparison.Ordinal);
        foreach (var height in deferred ? new[] { 60.0, 70, 80, 90, 100, 110, 120 } : [80.0])
        {
            foreach (var leads in deferred ? new[] { 0, 1, 2 } : [0])
            {
                var reference = Save(SplitDocument(shape, twin: true, height, leads));
                var doc = SplitDocument(shape, twin: false, height, leads);
                var bytes = Save(doc);

                Assert.True(Sha(reference) == Sha(bytes), $"{shape} height {height} leads {leads}");
                // A document that holds a renderer of the caller's reports the list it places over
                // the entries that renderer built, and says why, and 208 where the twin drops a
                // part; any other reports nothing.
                if (!shape.StartsWith("heldThenDirect", StringComparison.Ordinal))
                    Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.ListEntryPartDropped);
                var skipped = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
                if (shape.StartsWith("heldThenDirect", StringComparison.Ordinal))
                {
                    var one = Assert.Single(skipped);
                    Assert.Contains("a renderer of yours", one.Message, StringComparison.Ordinal);
                }
                else
                {
                    Assert.Empty(skipped);
                }
            }
        }
    }

    private static ListElement OneEntry(string text) =>
        new ListElement(ListStyle.OrderedDecimal).Add(new ListItem(text, H(10)));

    /// <summary>
    /// A reused list in a document that lays every list out as it was reports what any list does
    /// there: the fallback, once, whichever of the two rules made the document do so. A reused list
    /// in a document that does not reports the clamp, as a new renderer does.
    /// </summary>
    [Theory]
    [InlineData("font", true)]
    [InlineData("limit", true)]
    [InlineData("none", false)]
    public void AReusedList_reportsTheFallbackOnlyWhereTheDocumentLaysEveryListOutAsItWas(string rule, bool reports)
    {
        var renderer = new ListRenderer(Make("clamp"));
        var first = Page(PageWidth);
        first.Add(renderer);
        Save(first);

        var second = Page(PageWidth);
        second.Add(renderer);
        if (rule == "font")
        {
            // Its text has no width at this indent, so it is dropped and the save does not draw it.
            second.Add(new ListElement(ListStyle.Unordered) { Indent = 1000 }
                .Add(new ListItem([new TextRun("bad", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 })])
                { Style = H(10) }));
        }
        else if (rule == "limit")
        {
            second.Add(new Preformatted(new string('\t', 1_700), H(1)) { TabWidth = 32 });
        }

        Save(second);

        var skipped = second.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
        Assert.Equal(reports, skipped.Length > 0);

        // A document that keeps every list as it was clamps none. Any other lays the reused list
        // out as a new renderer, and reports the clamp.
        Assert.Equal(!reports, second.Diagnostics.Any(d => d.Code == LayoutDiagnosticCode.ListIndentClamped));
        if (reports) Assert.Contains(skipped, d => d.ElementIndex == 0);
    }

    private static ListElement UndrawableList() =>
        new ListElement(ListStyle.Unordered) { Indent = 1000 }
            .Add(new ListItem([new TextRun("bad", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 })])
            { Style = H(10) });

    private static Preformatted NearTheLimit() => new(new string('\t', 1_700), H(1)) { TabWidth = 32 };

    // What each cause says that the other causes' messages do not.
    private const string FontCause = "font it cannot draw";
    private const string NearCause = "element after a list";

    /// <summary>
    /// The 207 of a list the document lays out as it was names the rule that applied: the font the
    /// document cannot draw, the element after a list near the limit, or both. Each message holds
    /// the words of its causes and not the words of the other, and the order of the elements does
    /// not change what the document found: a near-limit element ahead of the list in the font gives
    /// the same message as one after it, whether that element is preformatted text or a list.
    /// </summary>
    [Theory]
    [InlineData("font", true, false)]
    [InlineData("near", false, true)]
    [InlineData("font, near", true, true)]
    [InlineData("near, font", true, true)]
    [InlineData("font, nearlist", true, true)]
    public void TheFallbackOfADocumentWideRule_namesTheRuleThatApplied(string order, bool namesFont, bool namesNear)
    {
        var doc = Page(PageWidth);
        doc.Add(new ListRenderer(Make("clamp")));
        foreach (var cause in order.Split(", "))
        {
            if (cause == "font") doc.Add(UndrawableList());
            else if (cause == "nearlist") doc.Add(new ListElement(ListStyle.Unordered).Add(new ListItem(new string('a', 50_000), H(1))));
            else doc.Add(NearTheLimit());
        }

        doc.Add(new ListRenderer(Make("clamp")));
        Save(doc);

        var skipped = doc.Diagnostics.Where(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).ToArray();
        Assert.Contains(skipped, d => d.ElementIndex == 0);
        Assert.All(skipped, d =>
        {
            Assert.Equal(namesFont, d.Message.Contains(FontCause, StringComparison.Ordinal));
            Assert.Equal(namesNear, d.Message.Contains(NearCause, StringComparison.Ordinal));
        });
    }

    // Lays a list out only from its Draw, at the area Layout reserved, and draws it.
    private sealed class LaysOutFromDraw(IRenderer inner) : IRenderer
    {
        private LayoutBox _box;

        public LayoutResult Layout(LayoutContext context)
        {
            _box = context.Area.WithHeight(Math.Min(20, context.Area.Height));
            return LayoutResult.Full(_box);
        }

        public void Draw(DrawContext ctx)
        {
            inner.Layout(new LayoutContext(_box));
            inner.Draw(ctx);
        }
    }

    /// <summary>
    /// A document that holds a renderer of the caller's lays every list out as it was before the
    /// fixes of #476 and #685, whether that renderer lays its list out from <c>Layout</c> or only
    /// from <c>Draw</c>: the document decides from the elements added, before any pass, so the
    /// list ahead of it is not clamped. Both lists report why, naming the renderer.
    /// </summary>
    [Fact]
    public void AListLaidOutOnlyFromDraw_isLaidOutAsItWas_likeEveryListOfTheDocument()
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 300), Margins = new EdgeInsets(10) };
        ListElement Clamp() => new ListElement(ListStyle.OrderedDecimal) { Indent = 300 }
            .Add(new ListItem("clamped text", H(10)));

        doc.Add(Clamp());
        doc.Add(new LaysOutFromDraw(new ListRenderer(UndrawableList())));
        doc.Add(Clamp());
        var bytes = Save(doc);

        // The same document before the fixes of #476 and #685 saves as 6D5D02C94C0F2E65.
        Assert.Equal("6D5D02C94C0F2E65", Sha(bytes));
        Assert.Equal(
            ["207x1@List0p1", "207x1@List2p1"],
            doc.Diagnostics.Select(d => $"{(int)d.Code}x{d.Count}@{d.ElementKind}{d.ElementIndex}p{d.PageNumber}").ToArray());
        Assert.All(
            doc.Diagnostics,
            d => Assert.Contains("a renderer of yours", d.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// The answer for a Standard 14 value is its own, whichever value was asked about before it:
    /// the memo packs the value and its answer in one word, and no two values can share it.
    /// </summary>
    [Fact]
    public void TheMemoOfTheLastStandard14Value_neverAnswersOneValueForAnother()
    {
        var run = ListLayoutRun.Begin(new PdfDocument(), out var previous);
        try
        {
            var values = Enumerable.Range(-40, 100).Append(int.MinValue).Append(int.MaxValue).ToArray();
            foreach (var first in values)
            {
                foreach (var second in values)
                {
                    // The memo holds the last value asked, so the first is asked again before each
                    // second: no other question may sit between the two.
                    ListLayoutRun.CanDraw(run, new TextStyle { FontRef = (Standard14)first });
                    var style = new TextStyle { FontRef = (Standard14)second };
                    Assert.True(
                        Enum.IsDefined((Standard14)second) == ListLayoutRun.CanDraw(run, style),
                        $"{second} asked after {first}");
                }
            }
        }
        finally
        {
            ListLayoutRun.End(previous);
        }
    }
}
