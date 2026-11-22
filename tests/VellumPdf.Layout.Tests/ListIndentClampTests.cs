// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A list indent that leaves an entry's text no width gives the text the width of its widest word
/// instead of discarding it (#476). Every row is a known answer: exact show strings, exact
/// <c>Tm</c> operands, exact report fields. The rows that must not move, a width that is positive
/// but short of the widest word, a long word, an entry with no word, and the nested refusal, are
/// pinned with the output the code produced before the change.
/// </summary>
public sealed class ListIndentClampTests
{
    private static TextStyle Helv(double size) => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static string Content(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    private static string[] Shows(string content) =>
        Regex.Matches(content, @"\(((?:\\.|[^\\)])*)\)\s*Tj").Select(m => m.Groups[1].Value).ToArray();

    private static string[] Positions(string content) =>
        Regex.Matches(content, @"1 0 0 1 (\S+) (\S+) Tm").Select(m => m.Groups[1].Value + "," + m.Groups[2].Value).ToArray();

    // 200 x 400 with 72pt margins leaves an area 56pt wide.
    private static Document Narrow(ListElement list)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 400) };
        doc.Add(list);
        return doc;
    }

    // 400 x 400 with 72pt margins leaves an area 256pt wide.
    private static Document Wide(ListElement list)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 400) };
        doc.Add(list);
        return doc;
    }

    private static ListElement TenWs(double indent) =>
        new ListElement(ListStyle.OrderedDecimal) { Indent = indent }
            .Add(new ListItem(string.Join(" ", Enumerable.Repeat("W", 10)), Helv(36)));

    private static ListElement Nested(double indent)
    {
        var item = new ListItem("parent", Helv(10));
        item.AddChild("child text");
        return new ListElement(ListStyle.OrderedDecimal) { Indent = indent }.Add(item);
    }

    // ── The change ───────────────────────────────────────────────────────────

    /// <summary>
    /// An indent at or beyond the 56pt area keeps the text: ten <c>W</c> shows, the first at
    /// 72 + 56 - 33.984 (a <c>W</c> is 944/1000 em, 33.984pt at 36pt), and the marker still first.
    /// </summary>
    [Theory]
    [InlineData(56.0)]
    [InlineData(200.0)]
    [InlineData(10000.0)]
    [InlineData(double.PositiveInfinity)]
    public void FlatIndent_atOrBeyondTheArea_drawsTheTextAtTheWidthOfItsWidestWord(double indent)
    {
        using var doc = Narrow(TenWs(indent));

        var content = Content(doc);

        var shows = Shows(content);
        Assert.Equal("1.", shows[0]);
        Assert.Equal(10, shows.Count(s => s == "W"));
        Assert.Equal(11, shows.Length);
        var positions = Positions(content);
        Assert.Equal("72,292", positions[0]);
        Assert.Equal("94.016,292", positions[1]);
        Assert.Equal("94.016,248.8", positions[2]);
    }

    [Fact]
    public void FlatIndent_atTheArea_isReportedOnceWithTheCountOfClampedEntries()
    {
        using var doc = Narrow(TenWs(56));

        Content(doc);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.ListIndentClamped, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(1, item.Count);
        Assert.Equal(
            "Error ListIndentClamped List[0] page 1: A list indent left an entry's text no width beside its gutter, "
            + "so the text was given a width, that of its widest word or the room right of its marker, "
            + "and may overprint the marker.",
            item.ToString());
    }

    /// <summary>
    /// Every clamped entry is counted: at an indent of 200 in a 256pt area a parent's gutter of 200
    /// leaves 56, so only the three children, whose gutter is 400, are clamped.
    /// </summary>
    [Fact]
    public void ReportCount_isTheNumberOfEntriesWhoseGutterWasNarrowed()
    {
        var one = new ListItem("one", Helv(10));
        one.AddChild("a");
        one.AddChild("b");
        var two = new ListItem("two", Helv(10));
        two.AddChild("c");
        using var doc = Wide(new ListElement(ListStyle.OrderedDecimal) { Indent = 200 }.Add(one).Add(two));

        Content(doc);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.ListIndentClamped, item.Code);
        Assert.Equal(3, item.Count);
    }

    /// <summary>
    /// A flat list at a 256pt area, an indent of 256, thirty items on a page that holds twelve:
    /// the list is split, and the report comes from the piece that starts the list, once, with the
    /// count of every entry, on page 1.
    /// </summary>
    [Fact]
    public void ASplitList_reportsOnceFromItsFirstPage_withEveryEntryCounted()
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 256 };
        for (var i = 0; i < 30; i++) list.Add(new ListItem("item" + i, Helv(20)));
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 400) };
        doc.Add(list);

        var content = Content(doc);

        Assert.Equal(30, Shows(content).Count(s => s.StartsWith("item", StringComparison.Ordinal)));
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(30, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    [Fact]
    public void StrictDiagnostics_refusesTheClampedList_andNamesTheCode()
    {
        using var doc = Narrow(TenWs(56));
        doc.StrictDiagnostics = true;

        var ex = Assert.Throws<InvalidOperationException>(() => Content(doc));

        Assert.StartsWith("StrictDiagnostics refused the save: ListIndentClamped in List 0, page 1: ", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A nested list from half the area: the child text is drawn at the width of its widest word,
    /// its marker where it was.
    /// </summary>
    [Theory]
    [InlineData(128.0, "200,306")]
    [InlineData(200.0, "272,306")]
    public void NestedIndent_fromHalfTheArea_drawsTheChildText(double indent, string childMarker)
    {
        using var doc = Wide(Nested(indent));

        var content = Content(doc);

        Assert.Equal(["1.", "parent", "1.", "child", "text"], Shows(content));
        var positions = Positions(content);
        Assert.Equal("72,318", positions[0]);
        Assert.Equal(childMarker, positions[2]);
        Assert.Equal("307.44,306", positions[3]);
        Assert.Equal("307.44,294", positions[4]);
        Assert.Equal(1, Assert.Single(doc.Diagnostics).Count);
    }

    /// <summary>
    /// Just short of the area width the child's marker has 0.001pt left, so the clamp gives the
    /// child text that width and it breaks to a glyph a line, as the parent text already did.
    /// </summary>
    [Fact]
    public void NestedIndent_justBelowTheArea_drawsTheChildTextAtTheWidthLeftOfItsMarker()
    {
        using var doc = Wide(Nested(255.999));

        var content = Content(doc);

        Assert.Equal(
            ["1.", "p", "a", "r", "e", "n", "t", "1", ".", "c", "h", "i", "l", "d", "t", "e", "x", "t"],
            Shows(content));
        var positions = Positions(content);
        Assert.Equal("327.999,246", positions[7]);
        Assert.Equal("327.999,234", positions[8]);
        Assert.Equal("327.999,246", positions[9]);
        Assert.Equal(1, Assert.Single(doc.Diagnostics).Count);
    }

    // ── What does not move ───────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0, "72,292 72,292 72,248.8")]
    [InlineData(20.0, "72,292 92,292 92,248.8")]
    [InlineData(55.0, "72,292 127,292 127,248.8")]
    [InlineData(55.999999, "72,292 128,292 128,248.8")]
    public void FlatIndent_belowTheArea_keepsItsOutput(double indent, string firstThree)
    {
        using var doc = Narrow(TenWs(indent));

        var content = Content(doc);

        Assert.Equal(firstThree, string.Join(" ", Positions(content).Take(3)));
        Assert.Equal(10, Shows(content).Count(s => s == "W"));
        Assert.Empty(doc.Diagnostics);
    }

    [Theory]
    [InlineData(20.0, "1.|parent|1.|child text")]
    [InlineData(100.0, "1.|parent|1.|child text")]
    [InlineData(127.999, "1.|parent|1.|c|h|i|l|d|t|e|x|t")]
    public void NestedIndent_belowHalfTheArea_keepsItsOutput(double indent, string shows)
    {
        using var doc = Wide(Nested(indent));

        Assert.Equal(shows, string.Join("|", Shows(Content(doc))));
        Assert.Empty(doc.Diagnostics);
    }

    [Theory]
    [InlineData(200.0, "1.|alpha beta|gamma")]
    [InlineData(220.0, "1.|alpha|beta|gamma")]
    [InlineData(230.0, "1.|alpha|beta|gam|ma")]
    [InlineData(240.0, "1.|alp|ha|bet|a|ga|m|ma")]
    [InlineData(250.0, "1.|a|l|p|h|a|b|e|t|a|g|a|m|m|a")]
    [InlineData(255.0, "1.|a|l|p|h|a|b|e|t|a|g|a|m|m|a")]
    public void ShredZone_whereTheWidthIsPositive_isNotClamped(double indent, string shows)
    {
        using var doc = Wide(new ListElement(ListStyle.OrderedDecimal) { Indent = indent }
            .Add(new ListItem("alpha beta gamma", Helv(10))));

        Assert.Equal(shows, string.Join("|", Shows(Content(doc))));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// A word wider than the whole area, at the default indent: the content stays at the gutter,
    /// not on the marker.
    /// </summary>
    [Fact]
    public void ALongWord_widerThanTheArea_keepsItsGutter()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 595.28, 841.89) };
        doc.Add(new ListElement(ListStyle.OrderedDecimal).Add(new ListItem(new string('M', 70) + " tail", Helv(10))));

        var content = Content(doc);

        Assert.Equal(4, Shows(content).Length);
        Assert.Equal("72,759.89 92,759.89 92,747.89", string.Join(" ", Positions(content).Take(3)));
        Assert.Empty(doc.Diagnostics);
    }

    [Theory]
    [InlineData("   ", 10.0)]
    [InlineData("abc", 0.0)]
    public void AnEntryWithNoWordWidth_isNotClamped(string text, double size)
    {
        using var doc = Narrow(new ListElement(ListStyle.OrderedDecimal) { Indent = 56 }.Add(new ListItem(text, Helv(size))));

        Assert.Equal(["1."], Shows(Content(doc)));
        Assert.Empty(doc.Diagnostics);
    }

    /// <summary>
    /// The child's own marker inset reaches the area: refused as before, with the same type and
    /// message.
    /// </summary>
    [Theory]
    [InlineData(256.0)]
    [InlineData(300.0)]
    public void NestedIndent_atTheArea_isStillRefused(double indent)
    {
        using var doc = Wide(Nested(indent));

        var ex = Assert.Throws<InvalidOperationException>(() => Content(doc));

        Assert.StartsWith("An element is too tall to fit on a single page and cannot be rendered.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANaNIndent_isStillRefusedByTheCanvas()
    {
        using var doc = Narrow(TenWs(double.NaN));

        Assert.Throws<ArgumentException>(() => Content(doc));
    }

    // ── What was refused stays refused ───────────────────────────────────────

    // 200 x 150 with margins of 10 above and below and 72 either side: an area 56pt wide and 130pt
    // tall, so a marker set at 200pt (a 240pt line) fits no page, and one at 100pt (120pt) fits a
    // page top but not what is left under a line of ten-point text.
    private static Document TallPage(ListElement list)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 150), Margins = new EdgeInsets(10, 72, 10, 72) };
        doc.Add(list);
        return doc;
    }

    private static ListItem TextAt10(string text, TextStyle markerStyle) =>
        new([new TextRun(text, Helv(10))]) { Style = markerStyle };

    /// <summary>
    /// A marker taller than the page has no room, and an entry whose text had no width was refused
    /// for it. Giving the text a width must not turn that refusal into a save that draws the text
    /// and no marker.
    /// </summary>
    [Theory]
    [InlineData(56.0)]
    [InlineData(1000.0)]
    public void AFlatEntry_whoseMarkerHasNoRoom_isStillRefused(double indent)
    {
        using var doc = TallPage(new ListElement(ListStyle.OrderedDecimal) { Indent = indent }.Add(TextAt10("abc", Helv(200))));

        var ex = Assert.Throws<InvalidOperationException>(() => Content(doc));

        Assert.StartsWith("An element is too tall to fit on a single page and cannot be rendered.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedEntry_whoseMarkerHasNoRoom_isStillRefused()
    {
        var parent = new ListItem("p", Helv(10));
        parent.AddChild(TextAt10("abc", Helv(200)));
        using var doc = TallPage(new ListElement(ListStyle.OrderedDecimal) { Indent = 30 }.Add(parent));

        var ex = Assert.Throws<InvalidOperationException>(() => Content(doc));

        Assert.StartsWith("An element is too tall to fit on a single page and cannot be rendered.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same fixtures with a marker that fits: the clamp applies, both are drawn.
    /// </summary>
    [Fact]
    public void TheSameEntries_withAMarkerThatFits_drawMarkerAndText()
    {
        var parent = new ListItem("p", Helv(10));
        parent.AddChild(TextAt10("abc", Helv(10)));
        using var flat = TallPage(new ListElement(ListStyle.OrderedDecimal) { Indent = 56 }.Add(TextAt10("abc", Helv(10))));
        using var nested = TallPage(new ListElement(ListStyle.OrderedDecimal) { Indent = 30 }.Add(parent));

        Assert.Equal(["1.", "abc"], Shows(Content(flat)));
        Assert.Equal(["1.", "p", "1.", "abc"], Shows(Content(nested)));
        Assert.Equal(1, Assert.Single(flat.Diagnostics).Count);
        Assert.Equal(1, Assert.Single(nested.Diagnostics).Count);
    }

    /// <summary>
    /// Under a line of ten-point text 118pt are left, and the second entry's marker needs 120. The
    /// marker has no room there, so the entry moves whole to the top of the next page, as it did
    /// when its text was discarded; it is not drawn on the first page without its marker.
    /// </summary>
    [Fact]
    public void AClampedEntry_whoseMarkerHasNoRoomOnThisPage_movesWholeToTheNext()
    {
        // An area 120pt wide, so that the 83pt marker is not broken.
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 120 }
            .Add(TextAt10("one", Helv(10)))
            .Add(TextAt10("abc", Helv(100)));
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 150), Margins = new EdgeInsets(10, 40, 10, 40) };
        doc.Add(list);

        var content = Content(doc);

        Assert.Equal(["1.", "one", "2.", "abc"], Shows(content));
        var positions = Positions(content);
        Assert.Equal("40,130", positions[0]);
        Assert.Equal("40,40", positions[2]);
    }

    // ── The arithmetic ───────────────────────────────────────────────────────

    /// <summary>
    /// For seeded areas, widest words and marker insets: the paragraph's own deflation
    /// (<c>area - gutter</c>) never comes out below the width the word needs, and the gutter is the
    /// widest that holds, so no word is broken and none gets more than a rounding error above the
    /// width it asks for. The seed, the pair count and the number of pairs a plain
    /// <c>area - word</c> would have broken are stated so the correction is shown to be reachable.
    /// </summary>
    [Fact]
    public void ClampedGutter_leavesTheWordItsWidth_forSeededPairs()
    {
        const int Seed = 476;
        const int Pairs = 200_000;
        var rng = new Random(Seed);
        var plainWouldBreak = 0;
        for (var i = 0; i < Pairs; i++)
        {
            var area = 1 + (rng.NextDouble() * 1999);
            var word = rng.NextDouble() * area;
            var inset = i % 3 == 0 ? 0 : rng.NextDouble() * area * 0.9;
            if (i % 50 == 0) word = area;

            // The widest word the room allows short of the room itself, where the gutter comes
            // closest to the marker's inset.
            if (i % 7 == 0 && area - inset > 0) word = Math.BitDecrement(area - inset);

            Assert.True(ListRenderer.TryClampGutter(area, area + rng.NextDouble(), inset, word, out var gutter));

            var room = area - inset;
            var need = Math.Min(word, room);
            Assert.True(area - gutter >= need, $"pair {i}: {area} {word} {inset} -> {gutter}");
            Assert.True(gutter >= inset, $"pair {i}: gutter {gutter} left of inset {inset}");
            Assert.True(area - gutter - need <= area * 1e-9, $"pair {i}: gutter {gutter} gives the word more than it needs");
            if (word < room && area - (area - word) < word) plainWouldBreak++;
        }

        Assert.True(plainWouldBreak > 0, "no seeded pair needs the one-step correction");
    }

    [Theory]
    [InlineData(100.0, 100.0, 0.0, 30.0)]
    [InlineData(100.0, 120.0, 0.0, 30.0)]
    [InlineData(100.0, double.PositiveInfinity, 0.0, 30.0)]
    public void TryClampGutter_whenTheTextWouldBeDiscarded_givesItsWord(double area, double gutter, double inset, double word)
    {
        Assert.True(ListRenderer.TryClampGutter(area, gutter, inset, word, out var clamped));
        Assert.Equal(70, clamped);
    }

    [Theory]
    [InlineData(100.0, 99.9, 0.0, 30.0)]
    [InlineData(100.0, double.NaN, 0.0, 30.0)]
    [InlineData(100.0, 100.0, 0.0, 0.0)]
    [InlineData(100.0, 100.0, 100.0, 30.0)]
    [InlineData(100.0, 100.0, 120.0, 30.0)]
    [InlineData(100.0, 100.0, 0.0, double.NaN)]
    public void TryClampGutter_otherwise_returnsTheGutterAsGiven(double area, double gutter, double inset, double word)
    {
        Assert.False(ListRenderer.TryClampGutter(area, gutter, inset, word, out var clamped));
        Assert.Equal(gutter, clamped);
    }

    [Fact]
    public void TryClampGutter_aWordWiderThanTheRoom_startsAtTheMarkersInset()
    {
        Assert.True(ListRenderer.TryClampGutter(100, 100, 0, 150, out var top));
        Assert.Equal(0, top);
        Assert.True(ListRenderer.TryClampGutter(100, 250, 40, 90, out var nested));
        Assert.Equal(40, nested);
    }
}
