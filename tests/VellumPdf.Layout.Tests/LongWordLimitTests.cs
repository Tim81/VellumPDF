// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using static VellumPdf.Layout.Tests.ContinuationSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The limit of page continuations keeps the verdict of 2.3.3 for the cut of a word wider than
/// its line (#594) and for the word split of #562 and #590 (#687). The harness limit is 300
/// continuations and the text is Courier at 10 pt, a line 12 pt tall: a page of the default
/// setup holds 6 lines of 30 characters, and one of the narrow setup 6 lines of 10.
/// </summary>
public sealed class LongWordLimitTests
{
    private static TextStyle Courier() => new() { Font = Standard14.Courier, FontSize = 10 };

    private static readonly Setup Narrow = new() { PageWidth = 80 };

    // Each repeat is one word of 41 characters with a solidus at its end. A cut after the solidus
    // where one fits takes about 1.4 times the lines that a cut at the glyph does.
    private static readonly string Address = string.Concat(Enumerable.Repeat(new string('a', 40) + "/", 1000));

    // Three lines of 10 characters in 2.3.3 and four in the current wrap: U+202F and U+2007 are
    // spaces to 2.3.3, and join the words around them now.
    private static readonly string NoBreakSpaces =
        string.Concat(Enumerable.Repeat("cccc jjjjjj\u202Fjjj\u2007vvvvvv\u2007sss ", 560));

    // Runs that continue one word: 2.3.3 read each run as words of its own and wrapped each pair
    // into three lines of 10 characters, where the current wrap needs two.
    private static List<TextRun> Glued()
    {
        var runs = new List<TextRun>();
        var style = Courier();
        for (var i = 0; i < 700; i++)
        {
            runs.Add(new TextRun("iiiiiiiiiii", style));
            runs.Add(new TextRun("aaa ", style));
        }

        return runs;
    }

    public enum Kind
    {
        Paragraph,
        Heading,
        ListItem,
        CellString,
        CellRuns,
    }

    private static IRenderer Element(Kind kind, string text, IReadOnlyList<TextRun>? runs = null, double indent = 20)
    {
        var style = Courier();
        switch (kind)
        {
            case Kind.Paragraph:
                return new ParagraphRenderer(runs is null ? new Paragraph(text, style) : new Paragraph(runs));
            case Kind.Heading:
                return new HeadingRenderer(runs is null ? new Heading(text, style) : new Heading(runs));
            case Kind.ListItem:
                var list = new ListElement { DefaultStyle = style, Indent = indent };
                return new ListRenderer(runs is null ? list.Add(text) : list.Add(new ListItem(runs)));
            case Kind.CellString:
                var table = new TableElement { DefaultCellStyle = style }.SetColumnWidths(192);
                table.AddRow().AddCell(text);
                return new TableRenderer(table);
            default:
                var runTable = new TableElement { DefaultCellStyle = style }.SetColumnWidths(192);
                runTable.AddRow().AddCell(new Cell(runs ?? [new TextRun(text, style)]));
                return new TableRenderer(runTable);
        }
    }

    private static string Verdict(Outcome o) => o.Error is null
        ? $"saves {o.Pages} pages {PlacementLoopSupport.NormalisedSha256(o.Bytes!)[..16]}"
        : IsLimitRefusal(o.Error) ? "refused at the limit" : o.Error.GetType().Name;

    // What main saved for each element, with its page count and the start of the hash of its
    // normalised bytes. Each is below the limit of 300 pages, so the verdict that has to hold is a
    // save, and the bytes are the ones the cut at the glyph gives.
    [Theory]
    [InlineData(Kind.Paragraph, "saves 228 pages 2B433EECCA172633")]
    [InlineData(Kind.Heading, "saves 228 pages 6FBED24972CB6288")]
    [InlineData(Kind.ListItem, "saves 263 pages BBCC372D25FE6382")]
    [InlineData(Kind.CellString, "saves 245 pages 553CB8B1D36EA6E5")]
    [InlineData(Kind.CellRuns, "saves 245 pages 553CB8B1D36EA6E5")]
    public void AnAddressThatMainSaves_stillSavesWithMainsBytes(Kind kind, string expected)
    {
        var o = Run(new Setup(), new Log(), Element(kind, Address));

        Assert.Equal(expected, Verdict(o));
        Assert.True(o.Probes > 0);
    }

    // #687, the first direction: 2.3.3 saved these and the word split of #562 refuses them.
    [Theory]
    [InlineData(Kind.Paragraph, "saves 280 pages 22BED5063621A216")]
    [InlineData(Kind.Heading, "saves 280 pages 6003662B1C0BCD27")]
    [InlineData(Kind.ListItem, "saves 280 pages CBD5A8DF26560EBC")]
    public void ATextThat2_3_3Saved_savesAgain(Kind kind, string expected) =>
        Assert.Equal(expected, Verdict(Run(Narrow, new Log(), Element(kind, NoBreakSpaces, indent: 0))));

    // #687, the second direction: 2.3.3 refused these multi-run texts and #590 saves them.
    [Theory]
    [InlineData(Kind.Paragraph)]
    [InlineData(Kind.Heading)]
    [InlineData(Kind.ListItem)]
    public void AMultiRunTextThat2_3_3Refused_isRefusedAgain(Kind kind) =>
        Assert.Equal("refused at the limit", Verdict(Run(Narrow, new Log(), Element(kind, "", Glued(), indent: 0))));

    // A document far from the limit keeps the cut after the solidus, and its limit decision costs
    // no layout pass: the bound of the element is within what the limit leaves.
    [Theory]
    [InlineData(Kind.Paragraph)]
    [InlineData(Kind.Heading)]
    [InlineData(Kind.ListItem)]
    public void ADocumentFarFromTheLimit_keepsTheNewCut_andProbesNothing(Kind kind)
    {
        var text = new string('x', 20) + "/" + new string('y', 40);

        var o = Run(new Setup(), new Log(), Element(kind, text));

        Assert.Null(o.Error);
        Assert.Equal(0, o.Probes);
        var shown = ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(o.Bytes!))
            .Select(p => p.Text).Where(s => s.Length > 0).ToArray();
        Assert.Contains(new string('x', 20) + "/", shown);
    }

    // Near the limit the decision lays the element out twice without drawing, once as the twin and
    // once as the new layout. A document far from it lays out no more than it did.
    [Fact]
    public void TheProbesRunOnlyWhereTheBoundPassesTheLimit()
    {
        var far = Run(new Setup(), new Log(), Element(Kind.Paragraph, new string('x', 20) + "/" + new string('y', 40)));
        var near = Run(new Setup(), new Log(), Element(Kind.Paragraph, Address));

        Assert.Equal(0, far.Probes);
        Assert.Equal(2, near.Probes);
    }

    // The rows of the review: the current wrap and 2.3.3's give a different number of lines at a
    // run boundary that holds U+00A0, at an empty run and at a run of U+00A0 alone. The pages are
    // those of a v2.3.3 build and of main (31f1261 and 56df51b agree on them; main has no cut after
    // a place, so its 255 pages for the address row become the 340 of the current wrap), made with
    // the same runs repeated and one 12 pt line to a page of 80 x 37 pt with 10 pt margins. The verdict at
    // the harness limit of 300 has to be 2.3.3's: saved when its pages fit, refused when they do
    // not, whether the runs share one style instance or each has its own.
    private sealed record Boundary(string Name, int Repeats, int Pages233, int PagesCurrent, string[] Runs);

    private static readonly Boundary[] Boundaries =
    [
        new("nbsp-end, 2.3.3 saves", 68, 272, 340, ["fff gggg", "hhhhhhh ii\u00A0", "fff gggg\n"]),
        new("nbsp-end, 2.3.3 refuses", 85, 340, 255, ["dd\u00A0", "ccccccc", "hhhhhhh ii", "e\n"]),
        new("nbsp-start, 2.3.3 saves", 114, 228, 342, ["e", "\u00A0dd", "aaaa", "e", "fff gggg\n"]),
        new("nbsp-start, 2.3.3 refuses", 57, 342, 285, ["ccccc dd", "mmmmm nnnnn", "jj kk ll", "ccccccc", "\u00A0ccccccc "]),
        new("nbsp-only, 2.3.3 saves", 49, 245, 343, ["jj kk ll", "fff gggg", "aaaa", "\u00A0", "jj kk ll", "hhhhhhh ii\n"]),
        new("nbsp-only, 2.3.3 refuses", 68, 340, 272, ["ccccc dd", "ccccccc", "aaaa", "\u00A0", "ccccc dd", "dd", "dd\n"]),
        new("empty run, 2.3.3 saves", 85, 170, 340, ["e", "aa bbbb", "bbbbbb", "", "e", "e\n"]),
        new("empty run, 2.3.3 refuses", 57, 342, 285, ["e", "mmmmm nnnnn", "", "jj kk ll", "mmmmm nnnnn", "jj kk ll\n"]),
        new("no boundary special, 2.3.3 refuses", 114, 342, 228, ["iiiiiiiiiii", "aaa "]),
        new("address with a place, 2.3.3 saves", 85, 255, 340, ["aaaaaaaaaaaa/bbbbbbbbbbbb "]),
        new("no-break spaces in a word, 2.3.3 saves", 90, 270, 360, ["cccc jjjjjj\u202Fjjj\u2007vvvvvv\u2007sss "]),
        new("no-break spaces in a word, 2.3.3 refuses", 85, 340, 255, ["oo\u202Fmmmmm\u2007u eeeeee\u2007ddddddd\u202Frrrr\u2007 "])
    ];

    private static readonly Setup OneLinePerPage = new() { PageWidth = 80, PageHeight = 37 };

    public static TheoryData<string, Kind, bool> BoundaryRows()
    {
        var rows = new TheoryData<string, Kind, bool>();
        foreach (var boundary in Boundaries)
        {
            foreach (var kind in new[] { Kind.Paragraph, Kind.Heading, Kind.ListItem })
            {
                rows.Add(boundary.Name, kind, true);
                rows.Add(boundary.Name, kind, false);
            }
        }

        return rows;
    }

    [Theory]
    [MemberData(nameof(BoundaryRows))]
    public void ARunBoundaryThe2_3_3WrapReadsAlone_getsThe2_3_3Verdict(string name, Kind kind, bool oneInstance)
    {
        var boundary = Boundaries.Single(b => b.Name == name);
        var shared = Courier();
        List<TextRun> Repeated() =>
            [.. Enumerable.Range(0, boundary.Repeats).SelectMany(_ => boundary.Runs.Select(s => new TextRun(s, oneInstance ? shared : Courier())))];

        // The port of 2.3.3's wrap makes the pages of a v2.3.3 build, and the current wrap those of main.
        Assert.Equal(boundary.Pages233, ParagraphRenderer.WordWrapAsBefore(Repeated(), 60).Lines.Count);
        Assert.Equal(boundary.PagesCurrent, ParagraphRenderer.WordWrap(Repeated(), 60, out _).Lines.Count);

        var saves = boundary.Pages233 < 300;
        Assert.Equal(name.EndsWith("saves", StringComparison.Ordinal), saves);

        var o = Run(OneLinePerPage, new Log(), Element(kind, "", Repeated(), indent: 0));

        Assert.Equal(saves, o.Error is null);
        if (!saves) Assert.True(IsLimitRefusal(o.Error));
    }

    // A path the limit decision cannot see wraps exactly as main does: main's tokenisation and a cut
    // at the glyph, so main's bytes and main's verdict. A table that a renderer of the caller's
    // forwards to cuts at the glyph and draws the lines it laid out.
    [Fact]
    public void AForwardedTable_cutsAtTheGlyph_andDrawsWhatItLaidOut()
    {
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        table.AddRow().AddCell("aaaa/bbbbbbbbbbbb");

        var o = Run(new Setup { PageWidth = 92 }, new Log(), new Forward(new TableRenderer(table)));

        Assert.Null(o.Error);
        Assert.Equal(["aaaa/bbbbb", "bbbbbbb"], Shown(o));
    }

    // The same for a paragraph a renderer of the caller's forwards to, with or without tab stops,
    // and U+202F, which main joins to the word before it.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AForwardedParagraph_wrapsAsMainDoes(bool tabStops)
    {
        var paragraph = new Paragraph("aaaa/bbbbbbbbbbbb", Courier());
        if (tabStops) paragraph = new Paragraph("aaaa/bbbbbbbbbbbb", Courier()) { TabStops = [new TabStop(30)] };

        var o = Run(Narrow, new Log(), new Forward(new ParagraphRenderer(paragraph)));

        Assert.Null(o.Error);
        Assert.Equal(["aaaa/bbbbb", "bbbbbbb"], Shown(o));

        var joined = Run(Narrow, new Log(), new Forward(new ParagraphRenderer(new Paragraph("cccc\u202Fdd eeee", Courier()))));
        Assert.Equal(["cccc\u00A0dd", "eeee"], Shown(joined));
    }

    private static string[] Shown(Outcome o) =>
        [.. ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(o.Bytes!))
            .Select(p => p.Text).Where(s => s.Length > 0)];

    private sealed class Forward(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    // A paragraph laid out under a context the document did not address, as a caller's wrapper
    // lays it out, wraps as main does: the word is cut at the glyph, in two lines.
    [Fact]
    public void ARendererACallerHolds_wrapsAsMainDoes()
    {
        var renderer = new ParagraphRenderer(new Paragraph("aaaa/bbbbbbbbbbbb", Courier()));

        var result = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 60, 1000)));

        Assert.Equal(2 * 12, result.OccupiedArea!.Value.Height);
    }

    // A renderer a caller built with a start line wraps as main does, forwarded or added to the
    // document, and the limit decision is not asked: the start indexes the lines of main's wrap,
    // two here, so a start of 2 is past the last line.
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void AStartLineACallerMade_readsTheLinesOfMainsWrap(int start, bool saves)
    {
        var renderer = new ParagraphRenderer(new Paragraph("aaaaaaa/bbbbbbbbbbbb", Courier()), start);

        var o = Run(Narrow, new Log(), renderer);

        Assert.Equal(saves, o.Error is null);
        if (!saves) Assert.IsType<InvalidOperationException>(o.Error);
        Assert.Equal(0, o.Probes);
    }

    // The paths the limit decision cannot see keep main's verdict at the limit. Each is the address
    // of the first test, which main saves in 228 pages for a paragraph, 263 for a list item and 245
    // for a cell; cut after the solidus each would need about 1.4 times as many and pass 300.
    public enum UnseenPath
    {
        ForwardedParagraph,
        ForwardedStartLineParagraph,
        StartLineAddedDirectly,
        ForwardedList,
        ListAfterARendererOfTheCallers,
        ListWithAnEntryNoPageHolds,
        ForwardedTable,
    }

    [Theory]
    [InlineData(UnseenPath.ForwardedParagraph, 228)]
    [InlineData(UnseenPath.ForwardedStartLineParagraph, 228)]
    [InlineData(UnseenPath.StartLineAddedDirectly, 228)]
    [InlineData(UnseenPath.ForwardedList, 263)]
    [InlineData(UnseenPath.ListAfterARendererOfTheCallers, 263)]
    [InlineData(UnseenPath.ListWithAnEntryNoPageHolds, 247)]
    [InlineData(UnseenPath.ForwardedTable, 245)]
    public void APathTheLimitDecisionCannotSee_keepsMainsVerdict(UnseenPath path, int pagesOfMain)
    {
        var style = Courier();
        IRenderer[] elements = path switch
        {
            UnseenPath.ForwardedParagraph => [new Forward(Element(Kind.Paragraph, Address))],
            UnseenPath.ForwardedStartLineParagraph => [new Forward(new ParagraphRenderer(new Paragraph(Address, style), 1))],
            UnseenPath.StartLineAddedDirectly => [new ParagraphRenderer(new Paragraph(Address, style), 1)],
            UnseenPath.ForwardedList => [new Forward(Element(Kind.ListItem, Address))],
            UnseenPath.ListAfterARendererOfTheCallers => [new Block(5), Element(Kind.ListItem, Address)],
            UnseenPath.ListWithAnEntryNoPageHolds =>
            [
                new ListRenderer(new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = style, Indent = 20 }
                    .Add(new ListItem("x", new TextStyle { Font = Standard14.Courier, FontSize = 70 }))
                    .Add(string.Concat(Enumerable.Repeat(new string('a', 40) + "/", 600)))),
            ],
            _ => [new Forward(Element(Kind.CellString, Address))],
        };

        // The entry of the last row is a marker of 70 pt, which fits no page of 140 pt.
        var setup = path == UnseenPath.ListWithAnEntryNoPageHolds ? new Setup { PageWidth = 100, PageHeight = 140 } : new Setup();

        var o = Run(setup, new Log(), elements);

        Assert.Null(o.Error);
        Assert.Equal(pagesOfMain, o.Pages);
    }

    // A list laid out as its twin for a reason other than the limit lays its entries out as main
    // does: one that holds an entry no page holds is placed as it was before the fixes of #682 (and
    // the drawability ruling), a document that holds a renderer of the caller's lays every list out
    // as its twin. U+202F joins the word before it and a word is cut at the glyph, on the pages
    // after the first too, where the document places the pieces of the twin; the same list in a
    // document without those reasons cuts after the place.
    [Fact]
    public void AListLaidOutAsItsTwinForAnotherReason_wrapsAsMainDoes()
    {
        string[] Lines(Action<Document, ListElement> arrange)
        {
            using var doc = new Document { PageSize = new PdfRectangle(0, 0, 100, 70), Margins = new EdgeInsets(10) };
            var list = new ListElement { DefaultStyle = Courier(), Indent = 20 };
            list.Add("aaaa/bbbbbbbbbbbb");
            list.Add("cccc dd eeee");
            list.Add(new ListItem([new TextRun("a b ", Courier()), new TextRun("c d", new TextStyle { Font = Standard14.Courier, FontSize = 10 })]));
            list.Add("ee ff");
            arrange(doc, list);
            return [.. ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(LinkSupport.Save(doc)))
                .Select(p => p.Text).Where(s => s.Length > 0)];
        }

        var plain = Lines((doc, list) => doc.Add(list));
        var withCallerRenderer = Lines((doc, list) => { doc.Add(new Forward(new LayoutImageSpacer())); doc.Add(list); });
        string[] entryNoPageHolds;
        {
            using var doc = new Document { PageSize = new PdfRectangle(0, 0, 100, 140), Margins = new EdgeInsets(10) };
            var list = new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = Courier(), Indent = 20 };
            list.Add(new ListItem("x", new TextStyle { Font = Standard14.Courier, FontSize = 70 }));
            list.Add("aaaa/bbbbbbbbbbbb");
            list.Add("cccc dd eeee");
            list.Add("ee ff");
            doc.Add(list);
            entryNoPageHolds = [.. ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(LinkSupport.Save(doc)))
                .Select(p => p.Text).Where(s => s.Length > 0)];
        }

        Assert.Contains("aaaa/", plain);
        Assert.Contains("cccc dd", plain);
        foreach (var other in new[] { withCallerRenderer, entryNoPageHolds })
        {
            Assert.Contains("aaaa/bbbbb", other);
            Assert.Contains("cccc dd", other);
            Assert.Contains("ee ff", other);
        }
    }

    private sealed class LayoutImageSpacer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(5));

        public void Draw(DrawContext context) { }
    }

    // The third case of the limit: the entries of every list of a document in which an element after
    // a list comes near the limit wrap as 2.3.3 did, on the first page (the list reads what the
    // document handed it) and on the pages after it (the pieces read what they carry). The same list
    // before a short paragraph wraps as the document does, joining U+202F to the word before it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheEntriesOfAListBeforeAnElementNearTheLimit_wrapAs2_3_3Did(bool near)
    {
        var list = new ListElement { DefaultStyle = Courier(), Indent = 20 };
        list.Add("cccc dd eeee");
        for (var i = 0; i < 8; i++) list.Add("xx yy");
        list.Add("ffff gg hh");
        var after = near ? new string('w', 9) + " " : "w ";

        var o = Run(new Setup { PageWidth = 100, PageHeight = 100 }, new Log(),
            new ListRenderer(list), new ParagraphRenderer(new Paragraph(string.Concat(Enumerable.Repeat(after, near ? 60 : 3)), Courier())));

        Assert.Null(o.Error);
        var shown = Shown(o);
        Assert.Equal(near, shown.Contains("cccc dd"));
        Assert.Equal(near, shown.Contains("ffff gg hh"));
        Assert.Equal(!near, shown.Contains("cccc dd"));
        Assert.Equal(!near, shown.Contains("ffff gg hh"));
    }

    // A note block is new API: its paragraphs cut after a place like any text the document lays out,
    // it has no twin and reports nothing to the limit decision. Near the limit it is refused where
    // its own lines pass the limit, with no probe, where a cut at the glyph would have saved it.
    [Fact]
    public void ANoteBlockNearTheLimit_isRefusedWhereItsLinesPassTheLimit_andIsNotProbed()
    {
        var o = Run(new Setup(), new Log(), new NoteBlockRenderer(new NoteBlock().Add(new Paragraph(Address, Courier()))));

        Assert.True(IsLimitRefusal(o.Error));
        Assert.Equal(0, o.Probes);

        var shown = Run(new Setup(), new Log(), new NoteBlockRenderer(new NoteBlock().Add(new Paragraph("aaaa/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", Courier()))));
        Assert.Contains("aaaa/", Shown(shown));
    }

    // A run with no style that holds no word does not change a paragraph's verdict: 2.3.3 read it as
    // nothing, so the paragraph of the glued runs is refused at the limit as it was. A run with no
    // style that holds a word is refused by the current wrap before any decision, for its style.
    [Fact]
    public void ARunWithNoStyleAndNoWord_leavesTheVerdictOf2_3_3()
    {
        var boundary = Boundaries.Single(b => b.Name == "no boundary special, 2.3.3 refuses");
        List<TextRun> runs = [new TextRun("", null!)];
        for (var i = 0; i < boundary.Repeats; i++) runs.AddRange(boundary.Runs.Select(s => new TextRun(s, Courier())));

        var refused = Run(OneLinePerPage, new Log(), new ParagraphRenderer(new Paragraph(runs)));
        Assert.True(IsLimitRefusal(refused.Error));

        var withAWord = Run(Narrow, new Log(), new ParagraphRenderer(new Paragraph([new TextRun(new string('x', 2000), Courier()), new TextRun("y", null!)])));
        var ex = Assert.IsType<ArgumentNullException>(withAWord.Error);
        Assert.Equal("Style", ex.ParamName);
    }

    // A paragraph longer than the continuations left is probed, whatever its page count. The bound
    // is the length of the text, which every line of either wrap meets, so a text just over what
    // the limit leaves is probed, and a bound smaller than that, an eighth of it say, would let the
    // current wrap decide where 2.3.3's refuses it.
    [Fact]
    public void ATextJustOverWhatTheLimitLeaves_isProbed()
    {
        var runs = new List<TextRun>();
        for (var i = 0; i < 120; i++)
        {
            runs.Add(new TextRun("iiiiiiiiiii", Courier()));
            runs.Add(new TextRun("aaa ", Courier()));
        }

        var o = Run(OneLinePerPage, new Log(), new ParagraphRenderer(new Paragraph(runs)));

        Assert.True(IsLimitRefusal(o.Error));
        Assert.True(o.Probes >= 1);
    }
}
