// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
        string.Concat(Enumerable.Repeat("cccc jjjjjj jjj vvvvvv sss ", 560));

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

    // A paragraph whose text cannot need another number of lines makes no second wrap.
    [Fact]
    public void AParagraphThatCannotDiffer_makesNoSecondWrap()
    {
        var plain = new ParagraphRenderer(new Paragraph("plain words with no place to cut at all", Courier()));
        var cut = new ParagraphRenderer(new Paragraph(new string('x', 20) + "/" + new string('y', 40), Courier()));

        _ = Run(new Setup(), new Log(), plain);
        _ = Run(new Setup(), new Log(), cut);

        Assert.False(plain.MadeTheOtherWrap);
        Assert.True(cut.MadeTheOtherWrap);
    }

    // A paragraph laid out under a context the document did not address, as a caller's wrapper
    // lays it out, wraps as 2.3.3 did: the word is cut at the glyph, in two lines and not three.
    [Fact]
    public void ARendererACallerHolds_wrapsAs2_3_3Did()
    {
        var renderer = new ParagraphRenderer(new Paragraph("aaaa/bbbbbbbbbbbb", Courier()));

        var result = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 60, 1000)));

        Assert.Equal(2 * 12, result.OccupiedArea!.Value.Height);
    }

    // A start line a caller made indexes the lines of 2.3.3's wrap, which has two here, so a start
    // of 2 lays out as Nothing and the save refuses it, as it did.
    [Theory]
    [InlineData(1, LayoutResult.Outcome.Full)]
    [InlineData(2, LayoutResult.Outcome.Nothing)]
    public void AStartLineACallerMade_readsTheLinesOf2_3_3(int start, LayoutResult.Outcome expected)
    {
        var renderer = new ParagraphRenderer(new Paragraph("aaaa/bbbbbbbbbbbb", Courier()), start);

        Assert.Equal(expected, renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 60, 1000))).Status);
    }
}
