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
/// A list is laid out from the items it holds in the save that lays it out (#691). A
/// <see cref="ListRenderer"/> that served another save, was laid out by a caller before the save, or
/// holds a list that changed since gives the document a fresh renderer for the same list would give,
/// and within one save each kind of call reads entries of its own. The reference for every row is
/// the same document built over new renderers, compared by outcome: the status, the bytes and the
/// diagnostics, or the exception the save throws.
/// </summary>
public sealed class ListReuseTests
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

    /// <summary>
    /// Each shown string with the position it is shown at, in file order, as <c>text@x,y</c>. A
    /// line that moved to the next page differs from one left where it was in its y.
    /// </summary>
    private static string[] Positioned(byte[] bytes)
    {
        var result = new List<string>();
        var position = "";
        foreach (Match m in Regex.Matches(
            PdfTestUtil.DecompressAllFlatStreams(bytes),
            @"1 0 0 1 (\S+) (\S+) Tm|\(((?:\\.|[^\\)])*)\)\s*Tj"))
        {
            if (m.Groups[1].Success) position = m.Groups[1].Value + "," + m.Groups[2].Value;
            else result.Add(m.Groups[3].Value + "@" + position);
        }

        return result.ToArray();
    }

    private static string Diagnostics(Document doc) => string.Join(
        ";", doc.Diagnostics.Select(d => $"{(int)d.Code}x{d.Count}@{d.ElementKind}{d.ElementIndex}p{d.PageNumber}"));

    /// <summary>
    /// What a save gives: the status, the bytes and the diagnostics, or the exception and its
    /// message.
    /// </summary>
    private static string Outcome(Document doc)
    {
        try
        {
            var bytes = Save(doc);
            return $"OK {Sha(bytes)} {Diagnostics(doc)}";
        }
        catch (Exception ex)
        {
            return $"EX {ex.GetType().Name} {ex.Message}";
        }
    }

    private static Document Page(double width, double height, int leads, bool strict = false)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, width, height),
            Margins = new EdgeInsets(10),
            StrictDiagnostics = strict,
        };
        for (var i = 0; i < leads; i++) doc.Add(new Paragraph("lead" + i, H(10)) { Margins = EdgeInsets.Zero });
        return doc;
    }

    // ── The lists ────────────────────────────────────────────────────────────

    // Each kind is a list whose entries a page bottom, a width or an indent treats differently.
    private static ListElement Kind(string kind) => kind switch
    {
        // Text that does not fit what is left of a page, beside a marker that does.
        "textfit" => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("ab", H(40))]) { Style = H(10) })
            .Add(new ListItem([new TextRun("cd", H(40))]) { Style = H(10) }),

        // A 20 pt marker beside 10 pt text, so the text fits where the marker does not.
        "markerfit" => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem([new TextRun("ab", H(10))]) { Style = H(20) })
            .Add(new ListItem([new TextRun("cd", H(10))]) { Style = H(20) }),

        // Text that wraps at the width it is laid out at.
        "wrap" => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("WWW ab cd ef gh ij", H(10)))
            .Add(new ListItem("kl mn op qr st uv wx", H(10)))
            .Add(new ListItem("yz aa bb cc dd ee", H(10))),

        // An indent that leaves no width for the text, so the clamp narrows both entries.
        "clamp" => new ListElement(ListStyle.OrderedDecimal) { Indent = 250 }
            .Add(new ListItem("clamped text", H(10)))
            .Add(new ListItem("second one", H(10))),

        // A 30 pt marker beside 10 pt text: the gutter is the marker's width where the area leaves
        // the widest word room, and the indent where it does not.
        "gutter" => new ListElement(ListStyle.OrderedDecimal) { Indent = 10 }
            .Add(new ListItem([new TextRun("WWWWW ab", H(10))]) { Style = H(30) })
            .Add(new ListItem([new TextRun("WWWWW cd", H(10))]) { Style = H(30) }),

        // An entry no page holds: a line of its marker is taller than the 80 pt of a page here.
        "nopage" => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("before", H(10)))
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(500) })
            .Add(new ListItem("after", H(10))),

        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // What happens to the list between the earlier layout and the save under test.
    private static void Change(ListElement list, string change)
    {
        switch (change)
        {
            case "none": break;
            case "text": list.Add(new ListItem("added item", H(10))); break;
            case "child": list.Items[0].AddChild("child one", H(10)); break;
            case "anchor": list.Add(new ListItem("anchored", H(10)) { Anchor = "anc" }); break;
            case "tall": list.Add(new ListItem("tall", H(120))); break;
            case "nopage": list.Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(500) }); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    // ── Callers' renderers ───────────────────────────────────────────────────

    /// <summary>
    /// A renderer of the caller's that holds another. <c>how</c> 0 lays the inner renderer out
    /// through the context it was given, 1 through <see cref="LayoutContext.WithArea"/> at the width
    /// factor, 2 through the public constructor at the factor, and 3 through
    /// <see cref="LayoutContext.WithArea"/> at three quarters of the width first and then through
    /// the context.
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
        "measure" => new Caller(renderer, 3, 1),
        _ => throw new ArgumentOutOfRangeException(nameof(how)),
    };

    /// <summary>A caller's renderer that takes no room, which makes a document lay every list out as it was.</summary>
    private sealed class TakesNoRoom : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 1, 0));

        public void Draw(DrawContext ctx)
        {
        }
    }

    // Lays a list out only from its Draw, at the area its Layout reserved, and draws it.
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

    // Keeps the context the document built for it, as a renderer of the caller's can.
    private sealed class KeepsContext : IRenderer
    {
        internal LayoutContext? Kept { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            Kept = context;
            return LayoutResult.Full(context.Area.WithHeight(1));
        }

        public void Draw(DrawContext ctx)
        {
        }
    }

    // Lays the inner renderer out through the context another renderer kept from an earlier save,
    // when it holds one.
    private sealed class ReplaysContext(IRenderer inner, KeepsContext keeper) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            // The kept context is used as it is: it names the list and the save that built it.
            var result = inner.Layout(keeper.Kept ?? context);
            var a = context.Area;
            return result.Status == LayoutResult.Outcome.Full
                ? LayoutResult.Full(new LayoutBox(a.X, a.Y, a.Width, result.OccupiedArea!.Value.Height))
                : LayoutResult.Nothing();
        }

        public void Draw(DrawContext ctx) => inner.Draw(ctx);
    }

    private static Preformatted NearTheLimit() => new(new string('\t', 1_700), H(1)) { TabWidth = 32 };

    // ── The issue: a reused renderer draws what a fresh one draws ────────────

    /// <summary>
    /// One renderer is added to two documents of the same page. The first places the list at a page
    /// top; in the second, lines of text come first, so the entry falls where its text, or its
    /// marker, does not fit. A paragraph that answers Nothing keeps where its last layout put its
    /// lines, and the second document drew them there. It now draws what a new renderer draws.
    /// </summary>
    [Theory]
    [InlineData("textfit", false)]
    [InlineData("textfit", true)]
    [InlineData("markerfit", false)]
    [InlineData("markerfit", true)]
    public void ASecondDocument_drawsWhatAFreshRendererDraws_whereAnEntryDoesNotFitAPageBottom(string kind, bool held)
    {
        for (var leads = 0; leads <= 9; leads++)
        {
            var list = Kind(kind);
            var renderer = new ListRenderer(list);
            var first = Page(200, 100, 0);
            first.Add(held ? Held("ctx", renderer) : renderer);
            Save(first);

            var second = Page(200, 100, leads);
            second.Add(renderer);
            var fresh = Page(200, 100, leads);
            fresh.Add(new ListRenderer(list));

            Assert.True(Outcome(fresh) == Outcome(second), $"{kind} held {held} leads {leads}");
        }
    }

    /// <summary>
    /// The two rows with their drawn text pinned: the entry that does not fit moves to the next
    /// page whole in the reused renderer, as in a new one, and its lines are drawn once.
    /// </summary>
    [Theory]
    [InlineData("textfit", 4, "lead3@10,44|1.@10,80|ab@30,50|2.@10,80|cd@30,50")]
    [InlineData("markerfit", 5, "lead4@10,32|1.@10,70|ab@30,80|2.@10,46|cd@30,56")]
    public void AnEntryThatDoesNotFitAPageBottom_isDrawnOnTheNextPageOnly_inTheReusedRendererToo(
        string kind, int leads, string expectedTail)
    {
        var renderer = new ListRenderer(Kind(kind));
        var first = Page(200, 100, 0);
        first.Add(renderer);
        Save(first);

        var second = Page(200, 100, leads);
        second.Add(renderer);

        // The last line of the first page, then the entry at the top of the next one, where the
        // y of its marker and its text is that of a page top.
        Assert.Equal(expectedTail, string.Join("|", Positioned(Save(second)).Skip(leads - 1)));
    }

    // ── Reuse across saves ───────────────────────────────────────────────────

    // The earlier layout of the list, before the save under test.
    private static void Earlier(string shape, ListRenderer renderer, double pageHeight)
    {
        switch (shape)
        {
            case "none":
                break;
            case "direct":
            case "held":
            case "near":
            case "held-near":
            case "direct-wide":
            case "direct-narrow":
                {
                    var width = shape == "direct-wide" ? 300 : shape == "direct-narrow" ? 120 : 200;
                    var doc = Page(width, pageHeight, 0);
                    doc.Add(shape.StartsWith("held", StringComparison.Ordinal) ? Held("ctx", renderer) : renderer);
                    if (shape.EndsWith("near", StringComparison.Ordinal)) doc.Add(NearTheLimit());
                    Save(doc);
                    break;
                }
            case "pre-wide":
                renderer.Layout(new LayoutContext(new LayoutBox(10, 10, 280, pageHeight - 20)));
                break;
            case "pre-narrow":
                renderer.Layout(new LayoutContext(new LayoutBox(10, 10, 100, pageHeight - 20)));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }
    }

    public static TheoryData<string, string, string, int, bool> AcrossSaves()
    {
        var rows = new TheoryData<string, string, string, int, bool>();
        foreach (var kind in new[] { "textfit", "markerfit", "wrap", "clamp", "gutter", "nopage" })
            foreach (var shape in new[] { "direct", "held", "near", "held-near", "direct-wide", "direct-narrow", "pre-wide", "pre-narrow" })
                foreach (var change in new[] { "none", "text", "child", "anchor", "tall" })
                    foreach (var leads in new[] { 0, 3, 6 })
                    {
                        rows.Add(kind, shape, change, leads, false);
                    }

        foreach (var kind in new[] { "textfit", "clamp", "nopage" })
            foreach (var shape in new[] { "direct", "held", "near" })
                foreach (var change in new[] { "none", "text", "anchor" })
                {
                    rows.Add(kind, shape, change, 3, true);
                }

        return rows;
    }

    /// <summary>
    /// Another save first, a layout of the caller's before the save, or a held use, then a change to
    /// the list: an item added, a child added, an item with an anchor, an item taller than a page.
    /// The save that follows gives the outcome a new renderer for the same list gives, with
    /// <see cref="Document.StrictDiagnostics"/> on and off, so the status, the bytes and the
    /// diagnostics of the reused renderer are a new one's.
    /// </summary>
    [Theory]
    [MemberData(nameof(AcrossSaves))]
    public void AReusedRenderer_givesTheOutcomeAFreshRendererGives(
        string kind, string shape, string change, int leads, bool strict)
    {
        var list = Kind(kind);
        var renderer = new ListRenderer(list);
        Earlier(shape, renderer, 100);
        Change(list, change);

        var reused = Page(200, 100, leads, strict);
        reused.Add(renderer);
        var fresh = Page(200, 100, leads, strict);
        fresh.Add(new ListRenderer(list));

        Assert.True(Outcome(fresh) == Outcome(reused), $"{kind} after {shape}, {change}, leads {leads}, strict {strict}");
    }

    /// <summary>
    /// An item with an anchor added after the list was laid out is placed in the next save, where
    /// the entries of the earlier layout held no entry for it and the save was refused with "never
    /// placed".
    /// </summary>
    [Fact]
    public void AnAnchoredItemAddedSinceTheLastSave_isPlacedInTheNextOne()
    {
        var list = Kind("wrap");
        var renderer = new ListRenderer(list);
        var first = Page(200, 100, 0);
        first.Add(renderer);
        Save(first);
        list.Add(new ListItem("anchored", H(10)) { Anchor = "anc" });

        var second = Page(200, 100, 0);
        second.Add(renderer);
        var shown = Shows(Save(second));

        Assert.Contains("anchored", shown);
        Assert.StartsWith("OK ", Outcome(Page(200, 100, 0).With(new ListRenderer(list))));
    }

    /// <summary>
    /// A renderer that served an earlier save is refused for what its list holds now, as a new
    /// renderer is: an item that would throw from the save throws from this one, with the same
    /// exception and message.
    /// </summary>
    [Theory]
    [InlineData("undefined font")]
    [InlineData("nan size")]
    [InlineData("nan colour")]
    [InlineData("null text")]
    [InlineData("null child")]
    [InlineData("tall")]
    public void AReusedRenderer_isRefusedForWhatItsListHoldsNow_asAFreshRendererIs(string defect)
    {
        var list = Kind("wrap");
        var renderer = new ListRenderer(list);
        var first = Page(200, 100, 0);
        first.Add(renderer);
        Save(first);

        switch (defect)
        {
            case "undefined font":
                list.Add(new ListItem("u", new TextStyle { FontRef = new FontReference((Standard14)99), FontSize = 10 }));
                break;
            case "nan size":
                list.Add(new ListItem("n", new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = double.NaN }));
                break;
            case "nan colour":
                list.Add(new ListItem("c", new TextStyle { FontSize = 10, Color = new ColorRgb(double.NaN, 0, 0) }));
                break;
            case "null text":
                list.Add(new ListItem(null!, H(10)));
                break;
            case "null child":
                list.Items[0].AddChild((ListItem)null!);
                break;
            default:
                list.Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(500) });
                break;
        }

        var reused = Page(200, 100, 2);
        reused.Add(renderer);
        var fresh = Page(200, 100, 2);
        fresh.Add(new ListRenderer(list));

        var expected = Outcome(fresh);
        Assert.Equal(expected, Outcome(reused));
        if (defect is "nan size" or "nan colour" or "null text" or "null child")
            Assert.StartsWith("EX ", expected);
    }

    // ── Within one save ──────────────────────────────────────────────────────

    /// <summary>
    /// One renderer added to a document twice, at a page bottom and after a page break, and held by
    /// a caller's renderer once, draws what the same document built with a renderer for each
    /// placement draws. The entry that fits no page is read by the second placement too.
    /// </summary>
    [Theory]
    [InlineData("textfit", "none", 0)]
    [InlineData("textfit", "none", 3)]
    [InlineData("textfit", "none", 5)]
    [InlineData("markerfit", "none", 5)]
    [InlineData("markerfit", "none", 7)]
    [InlineData("textfit", "second", 4)]
    [InlineData("textfit", "first", 4)]
    [InlineData("markerfit", "second", 6)]
    [InlineData("markerfit", "first", 6)]
    [InlineData("nopage", "none", 0)]
    [InlineData("nopage", "second", 2)]
    [InlineData("gutter", "second", 1)]
    public void OneRendererPlacedTwice_drawsWhatARendererForEachPlacementDraws(string kind, string held, int leads)
    {
        Document Build(Func<ListRenderer> make)
        {
            var doc = Page(200, 100, leads);
            doc.Add(held == "first" ? Held("ctx", make()) : make());
            doc.Add(new Paragraph("between", H(10)) { Margins = EdgeInsets.Zero });
            doc.Add(held == "second" ? Held("ctx", make()) : make());
            return doc;
        }

        var list = Kind(kind);
        var shared = new ListRenderer(list);

        Assert.Equal(Outcome(Build(() => new ListRenderer(list))), Outcome(Build(() => shared)));
    }

    /// <summary>
    /// A renderer the document places and a caller's renderer holds at three quarters of the width,
    /// in a document that lays every list out as it was, read two sets of entries: the document's
    /// calls and the caller's calls each build their own, as two renderers would.
    /// </summary>
    [Theory]
    [InlineData("wrap", "wa75")]
    [InlineData("gutter", "wa75")]
    [InlineData("gutter", "measure")]
    [InlineData("wrap", "measure")]
    [InlineData("clamp", "wa75")]
    [InlineData("gutter", "ctor75")]
    [InlineData("textfit", "ctx")]
    public void ARendererHeldAndPlacedInADocumentThatKeepsEveryListAsItWas_readsEntriesForEachKindOfCall(
        string kind, string how)
    {
        Document Build(Func<ListRenderer> direct, Func<ListRenderer> held, bool heldFirst)
        {
            var doc = Page(200, 100, 2);
            if (heldFirst)
            {
                doc.Add(Held(how, held()));
                doc.Add(new Paragraph("between", H(10)) { Margins = EdgeInsets.Zero });
            }

            doc.Add(direct());
            if (!heldFirst) doc.Add(Held(how, held()));
            doc.Add(new TakesNoRoom());
            return doc;
        }

        foreach (var heldFirst in new[] { false, true })
        {
            var list = Kind(kind);
            var shared = new ListRenderer(list);
            var forDocument = new ListRenderer(list);
            var forCaller = new ListRenderer(list);

            Assert.True(
                Outcome(Build(() => forDocument, () => forCaller, heldFirst))
                    == Outcome(Build(() => shared, () => shared, heldFirst)),
                $"{kind} {how} held first {heldFirst}");
        }
    }

    /// <summary>
    /// A caller's renderer that lays the list out at three quarters of the width and then at the
    /// width it was given, in one use, and the document placing the same renderer: the caller's
    /// entries are built at the first width of that use, and the document's at the page's own.
    /// </summary>
    [Theory]
    [InlineData("gutter")]
    [InlineData("wrap")]
    public void ACallerThatLaysAListOutAtTwoWidthsInOneUse_keepsTheFirstWidth(string kind)
    {
        Document Build(ListRenderer direct, ListRenderer held)
        {
            var doc = Page(200, 100, 0);
            doc.Add(Held("measure", held));
            doc.Add(new Paragraph("between", H(10)) { Margins = EdgeInsets.Zero });
            doc.Add(direct);
            return doc;
        }

        var list = Kind(kind);
        var shared = new ListRenderer(list);

        Assert.Equal(
            Outcome(Build(new ListRenderer(list), new ListRenderer(list))),
            Outcome(Build(shared, shared)));
    }

    /// <summary>
    /// A list laid out only from a caller's <c>Draw</c>, and placed by the document too, is two
    /// kinds of call as well.
    /// </summary>
    [Fact]
    public void AListLaidOutFromACallersDraw_andPlacedByTheDocument_readsEntriesForEachKindOfCall()
    {
        Document Build(ListRenderer direct, ListRenderer drawn)
        {
            var doc = Page(200, 100, 3);
            doc.Add(direct);
            doc.Add(new LaysOutFromDraw(drawn));
            return doc;
        }

        var list = Kind("markerfit");
        var shared = new ListRenderer(list);

        Assert.Equal(
            Outcome(Build(new ListRenderer(list), new ListRenderer(list))),
            Outcome(Build(shared, shared)));
    }

    /// <summary>
    /// A context a renderer of the caller's kept from an earlier save still names the list it was
    /// built for. It is not the document's call in this save: the list reads entries of the
    /// caller's kind, as it does for any other context the document did not build.
    /// </summary>
    [Theory]
    [InlineData("wrap")]
    [InlineData("gutter")]
    [InlineData("textfit")]
    public void AContextKeptFromAnEarlierSave_isACallersCallInTheNextOne(string kind)
    {
        Document Build(ListRenderer direct, ListRenderer replayed, KeepsContext keeper, bool replayFirst)
        {
            var doc = Page(150, 100, 1);
            if (replayFirst) doc.Add(new ReplaysContext(replayed, keeper));
            doc.Add(direct);
            if (!replayFirst) doc.Add(new ReplaysContext(replayed, keeper));
            return doc;
        }

        foreach (var replayFirst in new[] { false, true })
        {
            var list = Kind(kind);
            var shared = new ListRenderer(list);
            var keeperShared = new KeepsContext();
            var earlier = Page(150, 100, 0);
            earlier.Add(keeperShared);
            earlier.Add(shared);
            Save(earlier);

            var keeperFresh = new KeepsContext();
            var earlierFresh = Page(150, 100, 0);
            earlierFresh.Add(keeperFresh);
            earlierFresh.Add(new ListRenderer(list));
            Save(earlierFresh);

            Assert.True(
                Outcome(Build(new ListRenderer(list), new ListRenderer(list), keeperFresh, replayFirst))
                    == Outcome(Build(shared, shared, keeperShared, replayFirst)),
                $"{kind}, replay first {replayFirst}");
        }
    }

    /// <summary>
    /// One renderer held by a caller's renderer twice, at a page bottom and after a page break, draws
    /// what a renderer for each placement draws. Both placements are the caller's kind, so they share
    /// entries, and the second falls where the first placement's lines would be drawn if a layout
    /// that placed nothing left them.
    /// </summary>
    [Theory]
    [InlineData("textfit", 4)]
    [InlineData("textfit", 5)]
    [InlineData("markerfit", 5)]
    [InlineData("markerfit", 6)]
    [InlineData("gutter", 3)]
    public void OneRendererHeldTwice_drawsWhatARendererForEachPlacementDraws(string kind, int leads)
    {
        Document Build(Func<ListRenderer> make)
        {
            var doc = Page(200, 100, 0);
            doc.Add(Held("ctx", make()));
            for (var i = 0; i < leads; i++) doc.Add(new Paragraph("mid" + i, H(10)) { Margins = EdgeInsets.Zero });
            doc.Add(Held("ctx", make()));
            return doc;
        }

        var list = Kind(kind);
        var shared = new ListRenderer(list);

        Assert.Equal(Outcome(Build(() => new ListRenderer(list))), Outcome(Build(() => shared)));
    }

    /// <summary>
    /// An entry that no page of the first save held is not reported by the second, whose pages hold
    /// it. A list laid out as it was reports the entries no page holds that it found in the save in
    /// progress.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEntryNoPageHeldInTheFirstSave_isNotReportedWhereASecondSaveHoldsIt(bool strict)
    {
        ListElement Make() => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("before", H(10)))
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(50) })
            .Add(new ListItem("after", H(10)));

        Document Second(ListRenderer renderer, double height)
        {
            var doc = Page(200, height, 1, strict);
            doc.Add(renderer);
            doc.Add(new TakesNoRoom());
            return doc;
        }

        var list = Make();
        var reused = new ListRenderer(list);

        // The first save lays the list out itself, so it finds the entry no 60 pt page holds.
        var first = Page(200, 60, 1, strict);
        first.Add(reused);
        Outcome(first);

        Assert.Equal(Outcome(Second(new ListRenderer(list), 120)), Outcome(Second(reused, 120)));
    }

    // Twelve one-line entries, of which a page of the document here holds seven or eight.
    private static ListElement TwelveEntries()
    {
        var list = new ListElement(ListStyle.Unordered);
        for (var i = 1; i <= 12; i++) list.Add(new ListItem("e" + i, H(10)));
        return list;
    }

    /// <summary>
    /// A piece a split returned keeps the entries of the layout that made it, whichever save lays it
    /// out: a piece made by a layout outside any save, added to a document after an item was added
    /// to the list, draws the entries of that layout and not the new item.
    /// </summary>
    [Fact]
    public void APieceOfALayoutMadeOutsideAnySave_keepsItsEntriesAfterTheListChanged()
    {
        var list = TwelveEntries();
        var result = new ListRenderer(list).Layout(new LayoutContext(new LayoutBox(10, 10, 180, 40)));
        var piece = result.OverflowRenderer!;
        list.Add(new ListItem("ADDED", H(10)));

        var doc = Page(200, 100, 0);
        doc.Add(piece);
        var shown = Shows(Save(doc));

        Assert.Contains("e12", shown);
        Assert.DoesNotContain("ADDED", shown);
        Assert.DoesNotContain("e1", shown);
    }

    // Keeps the overflow piece of the list's first layout and draws nothing.
    private sealed class KeepsOverflow(IRenderer inner) : IRenderer
    {
        internal IRenderer? Piece { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            Piece = inner.Layout(context).OverflowRenderer;
            return LayoutResult.Full(context.Area.WithHeight(1));
        }

        public void Draw(DrawContext ctx)
        {
        }
    }

    // Lays out and draws the piece another renderer kept.
    private sealed class ReplaysPiece(KeepsOverflow keeper) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var result = keeper.Piece!.Layout(context);
            return result.Status == LayoutResult.Outcome.Full
                ? LayoutResult.Full(context.Area.WithHeight(result.OccupiedArea!.Value.Height))
                : LayoutResult.Nothing();
        }

        public void Draw(DrawContext ctx) => keeper.Piece!.Draw(ctx);
    }

    /// <summary>
    /// A renderer of the caller's that keeps a piece from one save and lays it out in the next, after
    /// an item was added, draws the entries of the first save. A new renderer draws the added item.
    /// </summary>
    [Fact]
    public void APieceACallerKeepsAcrossSaves_keepsTheEntriesOfTheSaveThatMadeIt()
    {
        var list = TwelveEntries();
        var keeper = new KeepsOverflow(new ListRenderer(list));
        var first = Page(200, 60, 0);
        first.Add(keeper);
        Save(first);
        list.Add(new ListItem("ADDED", H(10)));

        var second = Page(200, 400, 0);
        second.Add(new ReplaysPiece(keeper));
        var kept = Shows(Save(second));

        var fresh = Page(200, 400, 0);
        fresh.Add(new ListRenderer(list));

        Assert.Contains("e12", kept);
        Assert.DoesNotContain("ADDED", kept);
        Assert.Contains("ADDED", Shows(Save(fresh)));
    }

    // ── Per-save state ───────────────────────────────────────────────────────

    /// <summary>
    /// An entry no page holds is found per save. A list that held one on a short page and sits on a
    /// taller page now holds none, and the save places it as a new renderer does: with the entries
    /// moved, and without the report the short page gave.
    /// </summary>
    [Theory]
    [InlineData(60, 120)]
    [InlineData(120, 60)]
    [InlineData(100, 100)]
    public void APageHeightThatChangesBetweenSaves_changesWhichEntriesNoPageHolds(double firstHeight, double secondHeight)
    {
        ListElement Make() => new ListElement(ListStyle.OrderedDecimal)
            .Add(new ListItem("before", H(10)))
            .Add(new ListItem([new TextRun("abc", H(10))]) { Style = H(50) })
            .Add(new ListItem("after", H(10)));

        var list = Make();
        var renderer = new ListRenderer(list);
        var first = Page(200, firstHeight, 0);
        first.Add(renderer);
        Outcome(first);

        var reused = Page(200, secondHeight, 1);
        reused.Add(renderer);
        var fresh = Page(200, secondHeight, 1);
        fresh.Add(new ListRenderer(list));

        Assert.Equal(Outcome(fresh), Outcome(reused));
    }

    /// <summary>
    /// An item that no page holds, added to a list that held none, makes the list place as a new
    /// renderer does, with the diagnostics of the entry reported where the second save drops it, and
    /// strict mode refuses the save as it refuses a new renderer's.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEntryNoPageHoldsAddedSinceTheLastSave_isReportedAsANewRendererReportsIt(bool strict)
    {
        var list = Kind("wrap");
        var renderer = new ListRenderer(list);
        var first = Page(200, 100, 0, strict);
        first.Add(renderer);
        Save(first);
        Change(list, "nopage");

        var reused = Page(200, 100, 1, strict);
        reused.Add(renderer);
        var fresh = Page(200, 100, 1, strict);
        fresh.Add(new ListRenderer(list));

        var expected = Outcome(fresh);
        Assert.Equal(expected, Outcome(reused));
        Assert.Equal(strict, expected.StartsWith("EX ", StringComparison.Ordinal));
    }

    /// <summary>
    /// The counts a list reports belong to the save that built the entries: a list whose gutter the
    /// clamp narrowed in each of two saves reports the number of entries narrowed in that save, and
    /// not the sum of both.
    /// </summary>
    [Fact]
    public void TheCountsOfAClampedList_belongToEachSave()
    {
        var list = Kind("clamp");
        var renderer = new ListRenderer(list);
        var first = Page(200, 100, 0);
        first.Add(renderer);
        Save(first);
        list.Add(new ListItem("third", H(10)));

        var second = Page(200, 100, 0);
        second.Add(renderer);
        Save(second);
        var third = Page(200, 100, 0);
        third.Add(renderer);
        Save(third);

        Assert.Equal(2, first.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClamped).Count);
        Assert.Equal(3, second.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClamped).Count);
        Assert.Equal(3, third.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClamped).Count);
    }

    /// <summary>
    /// A list the document lays out as it was because it holds a renderer of the caller's reports,
    /// in each save, the entries the clamp would have narrowed in that save.
    /// </summary>
    [Fact]
    public void TheClampSkippedCount_belongsToEachSave()
    {
        var list = Kind("clamp");
        var renderer = new ListRenderer(list);

        Document Build()
        {
            var doc = Page(200, 100, 0);
            doc.Add(renderer);
            doc.Add(new TakesNoRoom());
            return doc;
        }

        var first = Build();
        Save(first);
        list.Add(new ListItem("third", H(10)));
        var second = Build();
        Save(second);
        var third = Build();
        Save(third);

        Assert.Equal(2, first.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).Count);
        Assert.Equal(3, second.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).Count);
        Assert.Equal(3, third.Diagnostics.Single(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped).Count);
    }

    // ── What a save measures and keeps ───────────────────────────────────────

    /// <summary>
    /// The font subset of the second save is the one a new renderer gives. Building the entries
    /// measures every item, so an item added since puts its glyphs in the subset whether or not a
    /// page draws it, as it does for a new renderer.
    /// </summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AListChangedAfterTheFirstSave_putsTheGlyphsAFreshRendererPutsInTheSubset(bool child, bool laidOut)
    {
        byte[] Second(bool reuse)
        {
            var first = new Document { PageSize = new PdfRectangle(0, 0, 300, 200), Margins = new EdgeInsets(10) };
            var firstStyle = new TextStyle { FontRef = first.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 10 };
            var list = new ListElement(ListStyle.OrderedDecimal)
                .Add(new ListItem("abc", firstStyle))
                .Add(new ListItem("abc", firstStyle));
            var renderer = new ListRenderer(list);
            first.Add(renderer);
            if (laidOut) renderer.Layout(new LayoutContext(new LayoutBox(10, 10, 280, 180)));
            else Save(first);

            if (child) list.Items[0].AddChild(new ListItem("QJKXYZ", firstStyle));
            else list.Add(new ListItem("QJKXYZ", firstStyle));

            var second = new Document { PageSize = new PdfRectangle(0, 0, 300, 200), Margins = new EdgeInsets(10) };
            var secondStyle = new TextStyle { FontRef = second.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 10 };
            second.Add(reuse ? renderer : new ListRenderer(list));
            second.Add(new Paragraph("after", secondStyle));
            return Save(second);
        }

        Assert.Equal(Sha(Second(reuse: false)), Sha(Second(reuse: true)));
    }

    /// <summary>
    /// A renderer that served a save does not keep the document of that save alive: the entries it
    /// holds name no run and no document.
    /// </summary>
    [Fact]
    public void AReusedRenderer_doesNotKeepTheDocumentOfAnEarlierSaveAlive()
    {
        var renderer = new ListRenderer(Kind("wrap"));
        var weak = SaveInAnotherFrame(renderer);

        for (var i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.False(weak.TryGetTarget(out _));
        GC.KeepAlive(renderer);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<Document> SaveInAnotherFrame(ListRenderer renderer)
    {
        var doc = Page(200, 100, 0);
        doc.Add(renderer);
        Save(doc);
        doc.Dispose();
        return new WeakReference<Document>(doc);
    }
}

internal static class ListReuseTestExtensions
{
    internal static Document With(this Document doc, IRenderer renderer)
    {
        doc.Add(renderer);
        return doc;
    }
}
