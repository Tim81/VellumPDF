// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.Reader;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #306: keep-with-next, keep-together and a minimum number of lines at a break. A page is
/// 300 x 200 pt with 20 pt margins, so the content area is 160 pt tall and holds 11 lines of the
/// 12 pt body style, whose leading is 14.4 pt. Every expectation is worked out from that geometry
/// and read back as the words on each page, not from the layout code's own arithmetic.
/// </summary>
public sealed class KeepRulesTests
{
    private const int LinesPerPage = 11;

    private static Document NewDoc() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 200),
        Margins = new EdgeInsets(20),
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    // A paragraph of `count` lines, each one word: tag1, tag2, ...
    private static Paragraph Lines(
        string tag, int count, bool keepWithNext = false, bool keepTogether = false, int before = 1, int after = 1) =>
        new(string.Join('\n', Enumerable.Range(1, count).Select(i => tag + i.ToString(CultureInfo.InvariantCulture))), Body)
        {
            KeepWithNext = keepWithNext,
            KeepTogether = keepTogether,
            MinLinesBeforeBreak = before,
            MinLinesAfterBreak = after,
        };

    private static string[] Seq(string tag, int from, int to) =>
        [.. Enumerable.Range(from, to - from + 1).Select(i => tag + i.ToString(CultureInfo.InvariantCulture))];

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // The words on each page, in the order the page draws them.
    private static string[][] Pages(byte[] pdf)
    {
        using var reader = PdfReader.Open(pdf);
        return [.. Enumerable.Range(0, reader.PageCount).Select(i =>
            reader.GetPage(i).ExtractText().Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))];
    }

    private static string[][] PagesOf(Document doc) => Pages(Save(doc));

    private static int PageOf(string[][] pages, string word) =>
        Array.FindIndex(pages, page => Array.IndexOf(page, word) >= 0);

    private static void AssertDiagnostics(Document doc, params (LayoutDiagnosticCode Code, LayoutElementKind Kind, int Index, int Page)[] expected) =>
        Assert.Equal(
            expected,
            doc.Diagnostics.Select(d => (d.Code, d.ElementKind, d.ElementIndex ?? -1, d.PageNumber ?? -1)).ToArray());

    // ── The acceptance table ─────────────────────────────────────────────────

    private static Document Strand(bool keep)
    {
        var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(new Heading("HeadA", Body) { KeepWithNext = keep });
        doc.Add(new Paragraph("BodyA", Body));
        return doc;
    }

    [Fact]
    public void StrandedHeading_withoutTheFlag_isTheLastLineOfPage1()
    {
        using var doc = Strand(keep: false);
        var pages = PagesOf(doc);

        Assert.Equal([.. Seq("f", 1, 10), "HeadA"], pages[0]);
        Assert.Equal(["BodyA"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void StrandedHeading_withKeepWithNext_movesToTheTopOfPage2WithItsBody()
    {
        using var doc = Strand(keep: true);
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 10), pages[0]);
        Assert.Equal(["HeadA", "BodyA"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    private static Document Orphan(int before, int after = 1)
    {
        var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(Lines("o", 3, before: before, after: after));
        return doc;
    }

    [Fact]
    public void Orphan_default_leavesOneLineOnPage1()
    {
        var pages = PagesOf(Orphan(1));

        Assert.Equal([.. Seq("f", 1, 10), "o1"], pages[0]);
        Assert.Equal(["o2", "o3"], pages[1]);
    }

    [Fact]
    public void Orphan_withMinLinesBeforeBreak2_movesAllThreeLinesToPage2()
    {
        using var doc = Orphan(2);
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 10), pages[0]);
        Assert.Equal(["o1", "o2", "o3"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void FiveLineParagraph_whereFourFit_withMinLinesAfterBreak2_splitsThreeAndTwo()
    {
        var control = NewDoc();
        control.Add(Lines("f", 7));
        control.Add(Lines("w", 5));
        var controlPages = PagesOf(control);
        Assert.Equal([.. Seq("f", 1, 7), .. Seq("w", 1, 4)], controlPages[0]);
        Assert.Equal(["w5"], controlPages[1]);

        using var doc = NewDoc();
        doc.Add(Lines("f", 7));
        doc.Add(Lines("w", 5, after: 2));
        var pages = PagesOf(doc);

        Assert.Equal([.. Seq("f", 1, 7), .. Seq("w", 1, 3)], pages[0]);
        Assert.Equal(["w4", "w5"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void MinLinesBeforeAndAfterTogether_moveTheBreakUpAndKeepBothSides()
    {
        // Eleven lines of a 14-line paragraph fit; leaving three over satisfies after = 3, and
        // 11 stays above before = 4.
        using var doc = NewDoc();
        doc.Add(Lines("w", 14, before: 4, after: 4));
        var pages = PagesOf(doc);

        Assert.Equal(Seq("w", 1, 10), pages[0]);
        Assert.Equal(Seq("w", 11, 14), pages[1]);
    }

    private static Document Figure(bool keep)
    {
        var doc = NewDoc();
        doc.Add(Lines("f", 8));
        doc.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Width = 40, Height = 40, KeepWithNext = keep });
        doc.Add(new Paragraph("Caption", Body));
        return doc;
    }

    private static int[] ImagesPerPage(byte[] pdf)
    {
        using var reader = PdfReader.Open(pdf);
        return [.. Enumerable.Range(0, reader.PageCount).Select(i => reader.GetPage(i).ExtractImages().Images.Count)];
    }

    [Fact]
    public void ImageAndCaption_withoutTheFlag_areSeparated()
    {
        var pdf = Save(Figure(keep: false));

        Assert.Equal([1, 0], ImagesPerPage(pdf));
        Assert.Equal(1, PageOf(Pages(pdf), "Caption"));
    }

    [Fact]
    public void ImageAndCaption_withKeepWithNextOnTheImage_stayTogether()
    {
        using var doc = Figure(keep: true);
        var pdf = Save(doc);

        Assert.Equal([0, 1], ImagesPerPage(pdf));
        Assert.Equal(1, PageOf(Pages(pdf), "Caption"));
        Assert.Empty(doc.Diagnostics);
    }

    private static Document Chain(bool first, bool second)
    {
        var doc = NewDoc();
        doc.Add(Lines("f", 9));
        doc.Add(new Heading("HeadOne", Body) { KeepWithNext = first });
        doc.Add(new Heading("HeadTwo", Body) { KeepWithNext = second });
        doc.Add(new Paragraph("BodyChain", Body));
        return doc;
    }

    [Fact]
    public void HeadingChain_atAPageFoot_splitsWithoutTheFlags()
    {
        var pages = PagesOf(Chain(false, false));

        Assert.Equal([.. Seq("f", 1, 9), "HeadOne", "HeadTwo"], pages[0]);
        Assert.Equal(["BodyChain"], pages[1]);
    }

    [Fact]
    public void HeadingChain_withBothFlags_movesTogetherWithItsBody()
    {
        using var doc = Chain(true, true);
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 9), pages[0]);
        Assert.Equal(["HeadOne", "HeadTwo", "BodyChain"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void HeadingChain_whereOnlyTheSecondHasTheFlag_movesTheSecondAlone()
    {
        var pages = PagesOf(Chain(false, true));

        Assert.Equal([.. Seq("f", 1, 9), "HeadOne"], pages[0]);
        Assert.Equal(["HeadTwo", "BodyChain"], pages[1]);
    }

    // ── Relaxation: reported, never an empty page, never an exception ────────

    private static Document LongChain(int filler, bool keep)
    {
        var doc = NewDoc();
        if (filler > 0) doc.Add(Lines("f", filler));
        for (var i = 1; i <= 13; i++)
            doc.Add(new Heading("H" + i.ToString(CultureInfo.InvariantCulture), Body) { KeepWithNext = keep });
        doc.Add(new Paragraph("BodyEnd", Body));
        return doc;
    }

    [Fact]
    public void ChainTallerThanAPage_isPlacedAsWithoutTheFlag_andReportedOnce()
    {
        var control = PagesOf(LongChain(3, keep: false));

        using var doc = LongChain(3, keep: true);
        var pages = PagesOf(doc);

        Assert.Equal(control, pages);
        AssertDiagnostics(doc, (LayoutDiagnosticCode.KeepWithNextRelaxed, LayoutElementKind.Heading, 1, 1));
        Assert.Equal(LayoutDiagnosticSeverity.Warning, doc.Diagnostics[0].Severity);
        Assert.Equal(1, doc.Diagnostics[0].Count);
    }

    [Fact]
    public void ChainTallerThanAPage_atTheTopOfThePage_leavesNoEmptyPage()
    {
        using var doc = LongChain(0, keep: true);
        var pages = PagesOf(doc);

        Assert.Equal(PagesOf(LongChain(0, keep: false)), pages);
        Assert.All(pages, page => Assert.NotEmpty(page));
        AssertDiagnostics(doc, (LayoutDiagnosticCode.KeepWithNextRelaxed, LayoutElementKind.Heading, 0, 1));
    }

    private static Document BigTable(bool keep, int filler)
    {
        var doc = NewDoc();
        if (filler > 0) doc.Add(Lines("f", filler));
        var table = new TableElement { KeepTogether = keep };
        for (var r = 1; r <= 30; r++)
            table.AddRow().AddCell("R" + r.ToString(CultureInfo.InvariantCulture));
        doc.Add(table);
        return doc;
    }

    [Fact]
    public void KeepTogetherTableTallerThanAPage_isPlacedAsWithoutTheFlag_andReportedOnce()
    {
        var control = PagesOf(BigTable(keep: false, filler: 2));

        using var doc = BigTable(keep: true, filler: 2);
        var pages = PagesOf(doc);

        Assert.Equal(control, pages);
        Assert.True(pages.Length >= 3);
        AssertDiagnostics(doc, (LayoutDiagnosticCode.KeepTogetherRelaxed, LayoutElementKind.Table, 1, 1));
    }

    [Fact]
    public void KeepTogetherTableTallerThanAPage_atTheTopOfThePage_isReportedOnPage1()
    {
        using var doc = BigTable(keep: true, filler: 0);
        var pages = PagesOf(doc);

        Assert.Equal(PagesOf(BigTable(keep: false, filler: 0)), pages);
        AssertDiagnostics(doc, (LayoutDiagnosticCode.KeepTogetherRelaxed, LayoutElementKind.Table, 0, 1));
    }

    [Fact]
    public void KeepTogetherParagraph_thatFitsAnEmptyPage_movesWhole()
    {
        var control = NewDoc();
        control.Add(Lines("f", 8));
        control.Add(Lines("k", 5));
        var controlPages = PagesOf(control);
        Assert.Equal([.. Seq("f", 1, 8), .. Seq("k", 1, 3)], controlPages[0]);

        using var doc = NewDoc();
        doc.Add(Lines("f", 8));
        doc.Add(Lines("k", 5, keepTogether: true));
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 8), pages[0]);
        Assert.Equal(Seq("k", 1, 5), pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepTogetherParagraph_thatDoesNotFitAnEmptyPage_splitsAndReports()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 4));
        doc.Add(Lines("k", 15, keepTogether: true));
        var pages = PagesOf(doc);

        Assert.Equal([.. Seq("f", 1, 4), .. Seq("k", 1, 7)], pages[0]);
        Assert.Equal(Seq("k", 8, 15), pages[1]);
        AssertDiagnostics(doc, (LayoutDiagnosticCode.KeepTogetherRelaxed, LayoutElementKind.Paragraph, 1, 1));
    }

    [Fact]
    public void KeepTogetherList_andKeepTogetherHeading_moveWhole()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 9));
        var list = new ListElement(ListStyle.Unordered) { KeepTogether = true };
        for (var i = 1; i <= 4; i++) list.Add("item" + i.ToString(CultureInfo.InvariantCulture), Body);
        doc.Add(list);
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 9), pages[0]);
        Assert.Equal(["item1", "item2", "item3", "item4"], pages[1].Select(w => w.TrimStart('•')));

        // A heading of three lines that would split moves whole.
        using var heading = NewDoc();
        heading.Add(Lines("f", 9));
        heading.Add(new Heading("one\ntwo\nthree", Body) { KeepTogether = true });
        var headingPages = PagesOf(heading);
        Assert.Equal(Seq("f", 1, 9), headingPages[0]);
        Assert.Equal(["one", "two", "three"], headingPages[1]);
    }

    [Fact]
    public void KeepTogetherTable_thatFitsAnEmptyPage_movesWhole()
    {
        Document Build(bool keep)
        {
            var doc = NewDoc();
            doc.Add(Lines("f", 6));
            var table = new TableElement { KeepTogether = keep };
            for (var r = 1; r <= 6; r++)
                table.AddRow().AddCell("T" + r.ToString(CultureInfo.InvariantCulture));
            doc.Add(table);
            return doc;
        }

        var control = PagesOf(Build(false));
        Assert.Contains(control[0], w => w.StartsWith('T'));
        Assert.Contains(control[1], w => w.StartsWith('T'));

        using var kept = Build(true);
        var pages = PagesOf(kept);
        Assert.DoesNotContain(pages[0], w => w.StartsWith('T'));
        Assert.Equal(Seq("T", 1, 6), pages[1]);
        Assert.Empty(kept.Diagnostics);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void MinLines_belowOne_isRefusedAtTheInit(int value)
    {
        var before = Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { MinLinesBeforeBreak = value });
        Assert.Equal("MinLinesBeforeBreak", before.ParamName);
        var after = Assert.Throws<ArgumentOutOfRangeException>(() => new Paragraph("x") { MinLinesAfterBreak = value });
        Assert.Equal("MinLinesAfterBreak", after.ParamName);
    }

    [Fact]
    public void MinLines_aboveTheLineCount_movesTheWholeParagraph_andAtThePageTopSplitsAndReports()
    {
        using var below = NewDoc();
        below.Add(Lines("f", 10));
        below.Add(Lines("o", 3, before: 50));
        var pages = PagesOf(below);
        Assert.Equal(Seq("f", 1, 10), pages[0]);
        Assert.Equal(["o1", "o2", "o3"], pages[1]);
        Assert.Empty(below.Diagnostics);

        using var top = NewDoc();
        top.Add(Lines("o", 15, after: 50));
        var topPages = PagesOf(top);
        Assert.Equal(Seq("o", 1, 11), topPages[0]);
        Assert.Equal(Seq("o", 12, 15), topPages[1]);
        AssertDiagnostics(top, (LayoutDiagnosticCode.MinLinesRelaxed, LayoutElementKind.Paragraph, 0, 1));
    }

    [Fact]
    public void MinLines_relaxedOnEveryPageOfALongParagraph_countsEachSplit()
    {
        using var doc = NewDoc();
        doc.Add(Lines("o", 30, before: 40));
        var pages = PagesOf(doc);

        Assert.Equal([11, 11, 8], pages.Select(p => p.Length));
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.MinLinesRelaxed, item.Code);
        Assert.Equal(2, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    // ── Conflicts: keep-with-next goes first, then keep-together, then line counts ──

    [Fact]
    public void HeadingThatCannotShareAPageWithAKeepTogetherParagraph_staysAndTheParagraphMoves()
    {
        // The heading and the 11-line paragraph need 12 lines, which no page holds, so the
        // paragraph, which fits a page alone, stays whole and the heading is left behind.
        using var doc = NewDoc();
        doc.Add(Lines("f", 3));
        doc.Add(new Heading("HeadX", Body) { KeepWithNext = true });
        doc.Add(Lines("k", 11, keepTogether: true));
        var pages = PagesOf(doc);

        Assert.Equal([.. Seq("f", 1, 3), "HeadX"], pages[0]);
        Assert.Equal(Seq("k", 1, 11), pages[1]);
        AssertDiagnostics(doc, (LayoutDiagnosticCode.KeepWithNextRelaxed, LayoutElementKind.Heading, 1, 1));
    }

    [Fact]
    public void KeepWithNext_countsTheNextElementsLineCount_whenDecidingWhatFits()
    {
        // The paragraph after the heading wants two lines before a break, and one line is left.
        using var doc = NewDoc();
        doc.Add(Lines("f", 9));
        doc.Add(new Heading("HeadY", Body) { KeepWithNext = true });
        doc.Add(Lines("o", 3, before: 2));
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 9), pages[0]);
        Assert.Equal(["HeadY", "o1", "o2", "o3"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    // ── What ends a group ────────────────────────────────────────────────────

    [Fact]
    public void APageBreakAfterTheRun_endsTheGroupWithNoConstraint_andNoReport()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(new Heading("HeadZ", Body) { KeepWithNext = true });
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("AfterBreak", Body));
        var pages = PagesOf(doc);

        Assert.Equal([.. Seq("f", 1, 10), "HeadZ"], pages[0]);
        Assert.Equal(["AfterBreak"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void AnElementThatAsksForABreak_endsTheGroupToo()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(new Heading("HeadZ", Body) { KeepWithNext = true });
        doc.Add(new Paragraph("Fresh", Body) { BreakBefore = PageBreakMode.Page });
        var pages = PagesOf(doc);

        Assert.Equal([.. Seq("f", 1, 10), "HeadZ"], pages[0]);
        Assert.Equal(["Fresh"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepWithNextOnTheLastElement_hasNothingToKeepWith()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(new Heading("Last", Body) { KeepWithNext = true });
        var pages = PagesOf(doc);

        Assert.Single(pages);
        Assert.Empty(doc.Diagnostics);
    }

    private sealed class Block(double height) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) =>
            context.Area.Height >= height
                ? LayoutResult.Full(context.Area.WithHeight(height))
                : LayoutResult.Nothing();

        public void Draw(DrawContext context)
        {
        }
    }

    [Fact]
    public void ACustomRendererAfterTheRun_isPlacedByTheSameRule()
    {
        // 50 pt do not fit in what is left of the page after the heading, but they fit a page.
        using var doc = NewDoc();
        doc.Add(Lines("f", 8));
        doc.Add(new Heading("HeadC", Body) { KeepWithNext = true });
        doc.Add(new Block(60));
        doc.Add(new Paragraph("Tail", Body));
        var pages = PagesOf(doc);

        Assert.Equal(Seq("f", 1, 8), pages[0]);
        Assert.Equal(["HeadC", "Tail"], pages[1]);
    }

    // ── Pagination counts, determinism, and where things are recorded ────────

    [Fact]
    public void WithABand_thePageCountOfTheMeasuringPass_isThePageCountOfTheDrawingPass()
    {
        // {pages} is printed from the measuring pass, so a keep that only the drawing pass honours
        // prints a total that is not the number of pages. A trailing paragraph fills the page the
        // heading moves to, so that moving it adds a page; swept over the filler so that the
        // heading meets the foot of a page at several offsets.
        var changedThePageCount = 0;
        for (var filler = 1; filler <= 12; filler++)
        {
            Document Build(bool keep)
            {
                var doc = NewDoc();
                doc.Footer = new RunningBand("[{page}/{pages}]");
                doc.Add(Lines("f", filler));
                doc.Add(new Heading("HeadB", Body) { KeepWithNext = keep });
                doc.Add(new Paragraph("BodyB", Body));
                doc.Add(Lines("t", 8));
                return doc;
            }

            var pages = Pages(Save(Build(true)));
            var total = pages.Length;
            for (var i = 0; i < total; i++)
                Assert.Contains($"[{i + 1}/{total}]", pages[i]);

            Assert.Equal(PageOf(pages, "HeadB"), PageOf(pages, "BodyB"));
            if (Pages(Save(Build(false))).Length != total) changedThePageCount++;
        }

        // The sweep has to reach a document the keep gives another page count, or it proves nothing.
        Assert.True(changedThePageCount > 0, "no filler made the keep change the page count");
    }

    [Fact]
    public void SavingTwice_givesTheSameBytes()
    {
        byte[] Build()
        {
            using var doc = NewDoc();
            doc.Footer = new RunningBand("{page}/{pages}");
            doc.Add(Lines("f", 9));
            doc.Add(new Heading("HeadD", Body) { KeepWithNext = true });
            doc.Add(Lines("o", 4, before: 2, after: 2));
            doc.Add(Lines("k", 8, keepTogether: true));
            return Save(doc);
        }

        Assert.Equal(NormalisedSha256(Build()), NormalisedSha256(Build()));
    }

    private static string Flat(byte[] pdf) => Regex.Replace(System.Text.Encoding.Latin1.GetString(pdf), @"\s+", " ");

    private sealed record Dest(int Page, double Top);

    // The 0-based page of each page object, in /Kids order.
    private static Dictionary<int, int> PageIndexOf(string text)
    {
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pageOf = new Dictionary<int, int>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
            pageOf[int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture)] = pageOf.Count;
        return pageOf;
    }

    // The name tree's destinations, read from the file text: the 0-based page and the PDF y of
    // the top. No oracle is involved.
    private static Dictionary<string, Dest> Dests(byte[] pdf)
    {
        var text = Flat(pdf);
        var pageOf = PageIndexOf(text);
        var result = new Dictionary<string, Dest>(StringComparer.Ordinal);
        var names = Regex.Match(text, @"/Dests << /Names \[(.*?)\]").Groups[1].Value;
        foreach (Match pair in Regex.Matches(names, @"\(([^)]*)\) (\d+) 0 R"))
        {
            var obj = Regex.Match(
                text, @"(?<![0-9])" + pair.Groups[2].Value + @" 0 obj \[(\d+) 0 R /XYZ (\S+) (\S+) null\] endobj");
            Assert.True(obj.Success, $"destination object for '{pair.Groups[1].Value}'");
            result[pair.Groups[1].Value] = new Dest(
                pageOf[int.Parse(obj.Groups[1].Value, CultureInfo.InvariantCulture)],
                double.Parse(obj.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return result;
    }

    // The page index each outline entry points at.
    private static int[] OutlinePages(byte[] pdf)
    {
        var text = Flat(pdf);
        var pageOf = PageIndexOf(text);
        return [.. Regex.Matches(text, @"/Dest \[(\d+) 0 R /XYZ")
            .Select(m => pageOf[int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)])];
    }

    [Fact]
    public void AMovedHeading_hasItsOutlineEntryAndItsAnchorOnTheNewPage()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 10));
        doc.Add(new Heading("HeadO", Body) { KeepWithNext = true, Anchor = "moved" });
        doc.Add(new Paragraph("BodyO", Body));
        var pdf = Save(doc);

        Assert.Equal(1, PageOf(Pages(pdf), "HeadO"));
        Assert.Equal([1], OutlinePages(pdf));

        // The destination is the top of page index 1, not where the heading would have been.
        Assert.Equal(new Dest(1, 200 - 20), Dests(pdf)["moved"]);
    }

    [Fact]
    public void ARunOfAnchoredElements_registersEachDestinationAtItsFinalPage()
    {
        using var doc = NewDoc();
        doc.Add(Lines("f", 9));
        doc.Add(new Heading("HeadP", Body) { KeepWithNext = true, Anchor = "first" });
        doc.Add(new Paragraph("MidP", Body) { KeepWithNext = true, Anchor = "second" });
        doc.Add(new Paragraph("EndP", Body) { Anchor = "third" });
        var pdf = Save(doc);

        var dests = Dests(pdf);
        Assert.Equal(["first", "second", "third"], dests.Keys.Order());
        Assert.Equal(new Dest(1, 180), dests["first"]);
        Assert.Equal(new Dest(1, 180 - Body.EffectiveLeading), dests["second"]);
        Assert.Equal(new Dest(1, 180 - (2 * Body.EffectiveLeading)), dests["third"]);
    }

    [Fact]
    public void LabelledBreakFollowedByARun_keepsItsRangeOnThePageTheElementsLandOn()
    {
        string Nums(bool keep)
        {
            using var doc = NewDoc();
            doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
            doc.Add(new Paragraph("front", Body));
            doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });
            doc.Add(Lines("f", 10));
            doc.Add(new Heading("HeadL", Body) { KeepWithNext = keep });
            doc.Add(new Paragraph("BodyL", Body));
            var pdf = Save(doc);
            var pages = Pages(pdf);
            Assert.Equal(keep ? 2 : 1, PageOf(pages, keep ? "BodyL" : "HeadL"));
            return Regex.Match(Flat(pdf), @"/Nums \[.*?\]").Value;
        }

        // The decimal range starts at page index 1 whether or not the heading moves on.
        Assert.Equal(Nums(false), Nums(true));
        Assert.Equal("/Nums [0 << /S /r >> 1 << /S /D >>]", Nums(true));
    }

    [Fact]
    public void ARunAtTheTopOfALabelledPage_isNotMovedOffIt()
    {
        using var doc = NewDoc();
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        doc.Add(new Paragraph("front", Body));
        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });
        doc.Add(new Heading("HeadT", Body) { KeepWithNext = true });
        doc.Add(new Paragraph("BodyT", Body));
        var pages = PagesOf(doc);

        Assert.Equal(2, pages.Length);
        Assert.Equal(["HeadT", "BodyT"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void WithMirroredMargins_aMovedRunTakesTheXOfItsNewPage()
    {
        double X(bool keep, string word)
        {
            using var doc = NewDoc();
            doc.MirrorMargins = true;
            doc.Margins = new EdgeInsets(20, 50, 20, 10); // top, right (outside), bottom, left (inside)
            doc.Add(Lines("f", 10));
            doc.Add(new Heading("HeadM", Body) { KeepWithNext = keep });
            doc.Add(new Paragraph("BodyM", Body));
            var pdf = Save(doc);
            return ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(pdf))
                .Single(t => t.Text == word).X;
        }

        // Page 1 is right-hand with the inside margin (10) on the left; page 2 puts the outside
        // margin (50) there. Without the flag the heading is on page 1 and the body on page 2.
        Assert.Equal(10, X(false, "HeadM"), 3);
        Assert.Equal(50, X(false, "BodyM"), 3);
        Assert.Equal(50, X(true, "HeadM"), 3);
        Assert.Equal(50, X(true, "BodyM"), 3);
    }

    [Fact]
    public void AKeepTogetherParagraphAsTallAsTheContentArea_movesWholeToTheNextPageTop()
    {
        // Ten lines of exactly 16 pt fill the 160 pt area exactly. It must be offered the whole
        // height on the page it moves to and not be refused or looped.
        var style = new TextStyle { Font = VellumPdf.Fonts.Standard14.Helvetica, FontSize = 12, Leading = 16 };
        using var doc = NewDoc();
        doc.Add(new Paragraph("a\nb\nc", style));
        doc.Add(new Paragraph(string.Join('\n', Enumerable.Range(1, 10).Select(i => "k" + i.ToString(CultureInfo.InvariantCulture))), style) { KeepTogether = true });
        var pages = PagesOf(doc);

        Assert.Equal(["a", "b", "c"], pages[0]);
        Assert.Equal(Seq("k", 1, 10), pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void StrictDiagnostics_savesADocumentThatOnlyRelaxed()
    {
        using var doc = LongChain(3, keep: true);
        doc.StrictDiagnostics = true;

        var pdf = Save(doc);

        Assert.NotEmpty(pdf);
        Assert.Equal(LayoutDiagnosticCode.KeepWithNextRelaxed, Assert.Single(doc.Diagnostics).Code);
    }

    // ── The pagination loop ──────────────────────────────────────────────────

    // A block whose height depends on the page count of the run before it, as a page reference's
    // text does: one line against the placeholder, then as many lines as the plan has pages.
    private sealed class GrowsWithPages : IRenderer, IPlanDependent
    {
        private ParagraphRenderer _inner = new(new Paragraph("g1", Body));
        private int _lines = 1;

        public string TargetName => "pages";

        public string ResolvedText => _lines.ToString(CultureInfo.InvariantCulture);

        public bool Resolve(PaginationPlan plan)
        {
            var lines = plan.IsPlaceholder ? 1 : plan.PageCount * 3;
            if (lines == _lines) return false;
            _lines = lines;
            _inner = new ParagraphRenderer(new Paragraph(
                string.Join('\n', Enumerable.Range(1, lines).Select(i => "g" + i.ToString(CultureInfo.InvariantCulture))), Body));
            return true;
        }

        public LayoutResult Layout(LayoutContext context) => _inner.Layout(context);

        public void Draw(DrawContext context) => _inner.Draw(context);
    }

    [Fact]
    public void KeepsInsideThePaginationLoop_followTheDependentThatMovesThem()
    {
        var pdfDocument = NewPdf();
        var renderer = new DocumentRenderer(pdfDocument, new PdfRectangle(0, 0, 300, 200), new EdgeInsets(20))
        {
            RecordPlacements = true,
        };
        renderer.Add(new ParagraphRenderer(Lines("f", 6)));
        renderer.Add(new GrowsWithPages());
        var heading = new Heading("HeadG", Body) { KeepWithNext = true };
        renderer.Add(new HeadingRenderer(heading));
        renderer.Add(new ParagraphRenderer(new Paragraph("BodyG", Body)));

        var pdf = Render(renderer);
        var pages = Pages(pdf);

        Assert.True(renderer.MeasureRuns >= 2);
        Assert.Equal(PageOf(pages, "HeadG"), PageOf(pages, "BodyG"));

        // The plan the document ends on agrees with the pages the words are on.
        var placed = renderer.DrawPlan!.Placements.Single(p => ReferenceEquals(p.Key, heading));
        Assert.Equal(PageOf(pages, "HeadG"), placed.PageIndex);
    }

    // ── Documents that saved without the flags still save with them ──────────

    private sealed record Spec(
        PdfRectangle Page, EdgeInsets Margins, bool Mirror, bool Footer, List<object> Elements);

    private static Spec MakeSpec(int seed, bool flags)
    {
        var r = new System.Random(seed);
        var sizes = new[] { (300.0, 200.0), (300.0, 300.0), (400.0, 500.0), (250.0, 160.0) };
        var (width, height) = sizes[r.Next(sizes.Length)];
        var margin = 10 + r.Next(25);
        var mirror = r.Next(4) == 0;
        var footer = r.Next(3) == 0;
        var style = new TextStyle { Font = VellumPdf.Fonts.Standard14.Helvetica, FontSize = 8 + r.Next(6) };
        var words = new[] { "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta" };
        string Words(int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => words[r.Next(words.Length)]));

        // Drawn whether or not the flags are on, so the two documents are the same text.
        bool Flag()
        {
            var drawn = r.Next(2) == 0;
            return flags && drawn;
        }

        int Count()
        {
            var drawn = 1 + r.Next(3);
            return flags ? drawn : 1;
        }

        var elements = new List<object>();
        var count = 3 + r.Next(30);
        for (var i = 0; i < count; i++)
        {
            var anchor = "e" + i.ToString(CultureInfo.InvariantCulture);
            switch (r.Next(7))
            {
                case 0 or 1:
                    elements.Add(new Paragraph(Words(1 + r.Next(60)), style)
                    {
                        Anchor = anchor,
                        KeepWithNext = Flag(),
                        KeepTogether = Flag(),
                        MinLinesBeforeBreak = Count(),
                        MinLinesAfterBreak = Count(),
                    });
                    break;
                case 2:
                    elements.Add(new Heading(Words(1 + r.Next(2)), style) { Anchor = anchor, KeepWithNext = Flag(), KeepTogether = Flag() });
                    break;
                case 3:
                    {
                        var list = new ListElement(ListStyle.Unordered) { Anchor = anchor, KeepWithNext = Flag(), KeepTogether = Flag() };
                        for (var k = 0; k < 2 + r.Next(8); k++) list.Add(Words(1 + r.Next(15)), style);
                        elements.Add(list);
                        break;
                    }

                case 4:
                    {
                        var table = new TableElement { Anchor = anchor, KeepWithNext = Flag(), KeepTogether = Flag() };
                        for (var k = 0; k < 1 + r.Next(25); k++)
                            table.AddRow().AddCell(Words(1 + r.Next(6)));
                        elements.Add(table);
                        break;
                    }

                case 5:
                    elements.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng()))
                    {
                        Anchor = anchor,
                        Width = 20 + r.Next(50),
                        Height = 10 + r.Next(40),
                        KeepWithNext = Flag(),
                    });
                    break;
                default:
                    elements.Add(new PageBreak { Recto = r.Next(3) == 0 });
                    break;
            }
        }

        return new Spec(new PdfRectangle(0, 0, width, height), new EdgeInsets(margin), mirror, footer, elements);
    }

    private static Document ToDocument(Spec spec)
    {
        var doc = new Document
        {
            PageSize = spec.Page,
            Margins = spec.Margins,
            MirrorMargins = spec.Mirror,
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        if (spec.Footer) doc.Footer = new RunningBand("{page} of {pages}");
        foreach (var element in spec.Elements)
        {
            switch (element)
            {
                case Paragraph p: doc.Add(p); break;
                case Heading h: doc.Add(h); break;
                case ListElement l: doc.Add(l); break;
                case TableElement t: doc.Add(t); break;
                case LayoutImage i: doc.Add(i); break;
                case PageBreak b: doc.Add(b); break;
            }
        }

        return doc;
    }

    private static DocumentRenderer ToRenderer(Spec spec)
    {
        var renderer = new DocumentRenderer(NewPdf(), spec.Page, spec.Margins)
        {
            MirrorMargins = spec.Mirror,
            RecordPlacements = true,
        };
        if (spec.Footer) renderer.Footer = new RunningBand("{page} of {pages}");
        foreach (var element in spec.Elements)
        {
            renderer.Add(element switch
            {
                Paragraph p => new ParagraphRenderer(p),
                Heading h => new HeadingRenderer(h),
                ListElement l => new ListRenderer(l),
                TableElement t => new TableRenderer(t),
                LayoutImage i => new LayoutImageRenderer(i),
                PageBreak b => new PageBreakRenderer(b),
                _ => throw new InvalidOperationException(),
            });
        }

        return renderer;
    }

    // Pages that draw neither a word nor an image. A page that holds only an image has no text.
    private static int BlankPages(byte[] pdf)
    {
        var pages = Pages(pdf);
        var images = ImagesPerPage(pdf);
        return pages.Where((p, i) => p.Length == 0 && images[i] == 0).Count();
    }

    [Fact]
    public void RandomKeepFlags_onDocumentsThatSaveWithout_stillSave_withNoNewBlankPage()
    {
        var saved = 0;
        var relaxed = 0;
        var changed = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            string[][] plainPages;
            int plainBlank;
            using (var plain = ToDocument(MakeSpec(seed, flags: false)))
            {
                try
                {
                    var plainBytes = Save(plain);
                    plainPages = Pages(plainBytes);
                    plainBlank = BlankPages(plainBytes);
                }
                catch (InvalidOperationException) { continue; }
            }

            using var flagged = ToDocument(MakeSpec(seed, flags: true));
            var bytes = Save(flagged);
            var pages = Pages(bytes);
            saved++;

            // A blank page is the one a recto break adds, and moving content can change which side
            // of a spread a later break starts on, so a flagged document can need fewer of them.
            // The pages that hold something never become fewer, and no blank page is new.
            var blank = BlankPages(bytes);
            var content = pages.Length - blank;
            var plainContent = plainPages.Length - plainBlank;
            Assert.True(content >= plainContent, $"seed {seed}: {content} pages with content against {plainContent}");

            // A recto break adds a blank page when the page it would start on is a left-hand one,
            // so moving content can add one. Without a recto break no blank page can appear.
            var recto = MakeSpec(seed, flags: true).Elements.OfType<PageBreak>().Any(b => b.Recto);
            if (!recto) Assert.True(blank <= plainBlank, $"seed {seed}: a blank page appeared");
            Assert.All(flagged.Diagnostics, d =>
            {
                Assert.True(
                    d.Code is LayoutDiagnosticCode.KeepWithNextRelaxed or LayoutDiagnosticCode.KeepTogetherRelaxed
                        or LayoutDiagnosticCode.MinLinesRelaxed,
                    $"seed {seed}: unexpected {d}");
                Assert.Equal(LayoutDiagnosticSeverity.Warning, d.Severity);
                Assert.InRange(d.ElementIndex ?? -1, 0, 32);
            });
            relaxed += flagged.Diagnostics.Count;
            if (pages.Length != plainPages.Length || !pages.Zip(plainPages).All(p => p.First.SequenceEqual(p.Second)))
                changed++;

            using var again = ToDocument(MakeSpec(seed, flags: true));
            Assert.Equal(NormalisedSha256(bytes), NormalisedSha256(Save(again)));
        }

        // The sweep reaches both outcomes: documents the flags changed, and relaxations.
        Assert.True(saved > 300, $"{saved} documents saved without the flags");
        Assert.True(changed > 50, $"{changed} documents the flags changed");
        Assert.True(relaxed > 0, "no relaxation was reached");
    }

    [Fact]
    public void ASingleLineElementKeptWithItsNext_isOnTheNextElementsPage_unlessItsRunWasReported()
    {
        var pairs = 0;
        for (var seed = 0; seed < 400; seed++)
        {
            var spec = MakeSpec(seed, flags: true);
            var renderer = ToRenderer(spec);
            using var sink = new MemoryStream();
            try { renderer.Render(sink); }
            catch (InvalidOperationException) { continue; }

            // A relaxed run is reported against its first element, and a run can start before the
            // element checked here, so a document that reported one is left out.
            if (renderer.Diagnostics.Any(d => d.Code == LayoutDiagnosticCode.KeepWithNextRelaxed)) continue;

            var placements = renderer.DrawPlan!.Placements;
            for (var i = 0; i + 1 < spec.Elements.Count; i++)
            {
                // A heading of one or two words is one line at these widths, and an image does not
                // split, so each starts and ends on one page.
                var keep = spec.Elements[i] is Heading { KeepWithNext: true } or LayoutImage { KeepWithNext: true };
                if (!keep || spec.Elements[i + 1] is PageBreak) continue;

                var here = placements.First(p => ReferenceEquals(p.Key, spec.Elements[i])).PageIndex;
                var next = placements.First(p => ReferenceEquals(p.Key, spec.Elements[i + 1])).PageIndex;
                Assert.True(here == next, $"seed {seed}: element {i} on page {here}, element {i + 1} on page {next}");
                pairs++;
            }
        }

        Assert.True(pairs > 50, $"{pairs} kept pairs were checked");
    }

    // ── Cost ─────────────────────────────────────────────────────────────────

    private static (int Calls, long Allocated) CostOf(bool keep, int every = 1)
    {
        var renderer = new DocumentRenderer(NewPdf(), new PdfRectangle(0, 0, 595, 842), new EdgeInsets(72));
        for (var i = 0; i < 1000; i++)
        {
            renderer.Add(new ParagraphRenderer(new Paragraph(
                string.Join(' ', Enumerable.Repeat("word", 32)) + i.ToString(CultureInfo.InvariantCulture), Body)
            {
                KeepWithNext = keep && i % every == 0,
                KeepTogether = keep && i % every == 0,
            }));
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return (renderer.LayoutCalls, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void FlaggingEveryParagraphOfAThousand_costsAtMostTwoLayoutsEach_andAFifthMoreAllocation()
    {
        _ = CostOf(false); // warm the font metrics and the JIT
        var (baseCalls, baseBytes) = CostOf(false);
        var (flagCalls, flagBytes) = CostOf(true);

        Assert.True(flagCalls <= baseCalls + (2 * 1000), $"{flagCalls} layout calls against {baseCalls} unflagged");
        Assert.True(flagBytes <= baseBytes * 1.2, $"{flagBytes} bytes against {baseBytes} unflagged");
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(10)]
    public void FlaggingEveryNthParagraphOfAThousand_costsTwoLayoutsPerFlag_andThreePerPage(int every)
    {
        // Each flagged paragraph and the one after it are laid out once more before they are
        // placed. A group that straddles a page foot is laid out again from the next page, which
        // adds a few calls for each page and not for each element. The unflagged document takes 63
        // pages on this geometry. For every 2, 3 and 10 the extra over two per flag was 124, 138
        // and 24 calls, at most 2.2 a page, so 3 a page is the bound.
        _ = CostOf(false);
        var (baseCalls, baseBytes) = CostOf(false);
        var (flagCalls, flagBytes) = CostOf(true, every);
        var flagged = (1000 + every - 1) / every;

        Assert.True(flagCalls <= baseCalls + (2 * flagged) + (3 * 64), $"{flagCalls} layout calls against {baseCalls} unflagged");
        Assert.True(flagBytes <= baseBytes * 1.2, $"{flagBytes} bytes against {baseBytes} unflagged");
    }
}
