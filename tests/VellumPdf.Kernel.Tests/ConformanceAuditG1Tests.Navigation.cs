// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Kernel.Tests.ConformanceAuditG1Support;
using TextRun = VellumPdf.Layout.Elements.TextRun;

namespace VellumPdf.Kernel.Tests;

// Destinations, links, outlines and page labels as the Layout package writes them (ISO 32000-1,
// 12.3.2, 12.3.3, 12.4.2, 12.5.2 to 12.5.6.5, 12.6.4.7).
public sealed partial class ConformanceAuditG1Tests
{
    private static readonly ConcurrentDictionary<PdfConformance, Lazy<byte[]>> NavigationCache = new();

    // One saved fixture per conformance, shared by the tests that only read it back.
    private static byte[] Navigation(PdfConformance conformance) =>
        NavigationCache.GetOrAdd(conformance, c => new Lazy<byte[]>(() => Save(NavigationDocument(c).Doc))).Value;

    private static readonly string[] Anchors =
        ["appendix", "ch1", "closed", "image", "longlink", "note", "para", "pre", "table", "toc", "tocitem", "toclist"];

    public static TheoryData<PdfConformance> Claims => new()
    {
        PdfConformance.None, PdfConformance.PdfA2b, PdfConformance.PdfA2u, PdfConformance.PdfA2a,
    };

    private static int PageIndexOf(Pdf pdf, PdfObject destination)
    {
        var page = Assert.IsType<PdfIndirectReference>(pdf.Array(destination)[0]);
        for (var i = 0; i < pdf.Reader.PageCount; i++)
        {
            if (pdf.PageObjectNumber(i) == page.ObjectNumber)
                return i;
        }

        throw new InvalidOperationException("a destination names an object that is not a page of the document");
    }

    private static string DestKey(PdfObject dest) => Encoding.ASCII.GetString(Assert.IsType<PdfLiteralString>(dest).Bytes.ToArray());

    private static string? UriOf(Pdf pdf, PdfDictionary annot) =>
        annot.Get(N("A")) is { } a && pdf.Dict(a).Get(N("URI")) is PdfLiteralString s ? Encoding.Latin1.GetString(s.Bytes.Span) : null;

    // ── 12.3.2.3 Named destinations: an anchor on every element kind that takes one ──────────

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_3_2_3_EveryAnchorIsANamedDestinationOnItsPage(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var tree = DestsTree(pdf);

        // The anchors are on a heading, a list, a list item, a paragraph, a table, a note block, a
        // preformatted block and an image: the eight kinds that take one. The tree holds those names
        // and no others, so nothing is written for a name nothing holds.
        Assert.Equal(Anchors, tree.Entries.Select(e => Encoding.ASCII.GetString(e.NameKey!)));
        Assert.All(tree.Entries, e => Assert.True(e.NameKey!.Length is >= 1 and <= 127));

        var pages = tree.Entries.ToDictionary(e => Encoding.ASCII.GetString(e.NameKey!), e => PageIndexOf(pdf, e.Value));
        var last = pdf.Reader.PageCount - 1;
        Assert.True(last >= 3, "the fixture is meant to span several pages");
        Assert.Equal(0, pages["toc"]);
        Assert.Equal(0, pages["toclist"]);
        Assert.Equal(0, pages["tocitem"]);
        Assert.Equal(1, pages["ch1"]);
        Assert.Equal(last, pages["appendix"]);

        // Each value is [page /XYZ 0 top null], with top inside the page's content area (40 to 460
        // for a 400 x 500 page with margins of 40).
        foreach (var entry in tree.Entries)
        {
            var array = pdf.Array(entry.Value);
            Assert.Equal(5, array.Count);
            Assert.Equal("XYZ", Assert.IsType<PdfName>(array[1]).Value);
            Assert.Equal(0, Number(array[2]));
            Assert.InRange(Number(array[3]), 40, 460.01);
            Assert.IsType<PdfNull>(array[4]);
        }
    }

    [Theory]
    [InlineData(PdfConformance.None)]
    [InlineData(PdfConformance.PdfA2u)]
    public void Iso32000_1_12_3_2_3_PopplerResolvesEveryDestinationToTheSamePage(PdfConformance claim)
    {
        var bytes = Navigation(claim);
        using var pdf = new Pdf(bytes);
        var own = DestsTree(pdf).Entries.ToDictionary(e => Encoding.ASCII.GetString(e.NameKey!), e => PageIndexOf(pdf, e.Value) + 1);

        var poppler = PopplerDestinations(bytes);

        Assert.Equal(own.Count, poppler.Count);
        foreach (var (name, page) in own)
            Assert.Equal(page, poppler[name]);
    }

    // ── 12.5.2 and 12.5.6.5: the link annotations ───────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_5_6_5_EveryLinkNamesOneTargetAndADestinationNameResolves(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var links = AnnotationsOf(pdf);
        var tree = DestsTree(pdf);
        var names = tree.Entries.Select(e => Encoding.ASCII.GetString(e.NameKey!)).ToHashSet();

        Assert.NotEmpty(links);
        Assert.All(links, l => Assert.Equal("Link", Subtype(l.Dict)));

        // "Dest ... not permitted if an A entry is present": every link here has exactly one of them.
        Assert.All(links, l => Assert.True((l.Dict.Get(N("A")) is null) != (l.Dict.Get(N("Dest")) is null)));

        // A destination is a byte string looked up in the name tree (12.3.2.3), and must be there.
        var dests = links.Where(l => l.Dict.Get(N("Dest")) is not null).Select(l => DestKey(l.Dict.Get(N("Dest"))!)).ToList();
        Assert.All(dests, d => Assert.Contains(d, names));

        // Every action is a URI action (6.5.1: GoTo and URI are the actions a PDF/A-2 file may use here).
        var uris = links.Select(l => UriOf(pdf, l.Dict)).Where(u => u is not null).ToList();
        Assert.All(links.Where(l => l.Dict.Get(N("A")) is not null),
            l => Assert.Equal("URI", Assert.IsType<PdfName>(pdf.Dict(l.Dict.Get(N("A"))).Get(N("S"))).Value));

        // The links the fixture was built to produce: by anchor from a paragraph, a list item, a
        // heading, a cell and the header band; by URI from a paragraph, a cell, the footer band and
        // the long link.
        foreach (var anchor in new[] { "toc", "ch1", "longlink", "appendix", "table", "pre", "image" })
            Assert.Contains(anchor, dests);
        Assert.Contains("https://example.com/a?x=1&y=2", uris);
        Assert.Contains("https://example.com/cell", uris);
        Assert.Contains("https://example.com/footer", uris);
        Assert.Contains("https://example.com/long", uris);

        // A band repeats on every page, so the header link and the footer link are on all of them.
        for (var page = 0; page < pdf.Reader.PageCount; page++)
        {
            Assert.Contains(links, l => l.PageIndex == page && l.Dict.Get(N("Dest")) is { } d && DestKey(d) == "toc");
            Assert.Contains(links, l => l.PageIndex == page && UriOf(pdf, l.Dict) == "https://example.com/footer");
        }
    }

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_5_2_AnAnnotationDictionaryIsInTheAnnotsOfOnePage(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var links = AnnotationsOf(pdf);

        Assert.True(links.Count > pdf.Reader.PageCount);
        Assert.Equal(links.Count, links.Select(l => l.ObjectNumber).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_5_2_ARectIsFourNumbersInsideThePageAndHasArea(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        foreach (var link in AnnotationsOf(pdf))
        {
            var rect = pdf.Array(link.Dict.Get(N("Rect")));
            Assert.Equal(4, rect.Count);
            var (x0, y0, x1, y1) = (Number(rect[0]), Number(rect[1]), Number(rect[2]), Number(rect[3]));
            Assert.True(x0 < x1 && y0 < y1, $"link {link.ObjectNumber} has an empty or inverted rectangle");
            Assert.InRange(x0, 0, 400);
            Assert.InRange(x1, 0, 400);
            Assert.InRange(y0, 0, 500);
            Assert.InRange(y1, 0, 500);
        }
    }

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_5_3_ALinkIsPrintedAndHasNoOtherFlag(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var links = AnnotationsOf(pdf);

        Assert.NotEmpty(links);
        // F is bit 3 (Print) alone: 4. Every other bit "shall be set to 0" (12.5.3).
        Assert.All(links, l => Assert.Equal(4, Assert.IsType<PdfInteger>(l.Dict.Get(N("F"))).Value));
    }

    [Fact]
    public void Iso32000_1_12_6_4_7_AUriIsAnAsciiStringWhateverTheCallerGave()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfA2b);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        // Non-ASCII (two and four UTF-8 bytes), parentheses and a backslash, which a literal string
        // must escape, and a lone surrogate.
        const string uri = "https://example.com/café/\U0001F600?q=(1)\\2&lone=\uD800";
        doc.Add(new Paragraph([new TextRun("link", new TextStyle { FontRef = font, FontSize = 11, LinkUri = uri })]));
        using var pdf = new Pdf(Save(doc));

        var link = Assert.Single(AnnotationsOf(pdf));
        var action = pdf.Dict(link.Dict.Get(N("A")));
        Assert.Equal("URI", Assert.IsType<PdfName>(action.Get(N("S"))).Value);
        var bytes = Assert.IsType<PdfLiteralString>(action.Get(N("URI"))).Bytes.ToArray();

        // 7-bit ASCII in ISO 32000-1 (Table 206), UTF-8 in ISO 32000-2 (Table 210): ASCII is both.
        Assert.All(bytes, b => Assert.True(b is >= 0x20 and < 0x7F, "a URI byte is printable 7-bit ASCII"));
        Assert.Equal("https://example.com/caf%C3%A9/%F0%9F%98%80?q=(1)\\2&lone=%EF%BF%BD", Encoding.ASCII.GetString(bytes));
        // IsMap is absent, so the 14289-1 rule on it (7.18.5) has nothing to forbid.
        Assert.Null(action.Get(N("IsMap")));
    }

    // ── #659, #475: where the rectangle of a link lies ──────────────────────────────────────

    [Fact]
    public void Iso32000_1_12_5_2_ALinkThatWrapsHasOneRectanglePerLineAndStaysInTheColumn()
    {
        using var pdf = new Pdf(Navigation(PdfConformance.PdfA2u));
        var rects = AnnotationsOf(pdf)
            .Where(l => UriOf(pdf, l.Dict) == "https://example.com/long")
            .Select(l => (l.PageIndex, R: pdf.Array(l.Dict.Get(N("Rect"))).Items().Select(Number).ToArray()))
            .ToList();

        // The paragraph is long enough for lines on two pages.
        Assert.True(rects.Select(r => r.PageIndex).Distinct().Count() >= 2, "the link runs on to a second page");
        Assert.True(rects.Count >= 6);
        Assert.DoesNotContain(AnnotationsOf(pdf), l => l.Dict.Get(N("QuadPoints")) is not null);

        foreach (var group in rects.GroupBy(r => r.PageIndex))
        {
            var lines = group.OrderByDescending(r => r.R[3]).ToList();
            for (var i = 0; i < lines.Count; i++)
            {
                // The text column is 40 to 360 on a 400-point page with margins of 40.
                Assert.InRange(lines[i].R[0], 39.99, 360.01);
                Assert.InRange(lines[i].R[2], 39.99, 360.01);
                if (i > 0) // one rectangle per line: the next line starts where the one above ends
                    Assert.True(lines[i].R[3] <= lines[i - 1].R[1] + 0.01, "line rectangles do not overlap");
                // A justified line that is not the last of its page's text spans the column (#659).
                if (i < lines.Count - 1)
                {
                    Assert.InRange(lines[i].R[0], 39.99, 40.5);
                    Assert.InRange(lines[i].R[2], 359.5, 360.01);
                }
            }
        }
    }

    [Fact]
    public void Iso32000_1_12_5_2_ACellAndABandLinkLieInsideTheirBoxes()
    {
        using var pdf = new Pdf(Navigation(PdfConformance.PdfA2u));
        var links = AnnotationsOf(pdf);

        double[] Rect(Link link) => [.. pdf.Array(link.Dict.Get(N("Rect"))).Items().Select(Number)];

        // The first column of the table is 150 points wide, starting at the left margin.
        var cell = Assert.Single(links, l => UriOf(pdf, l.Dict) == "https://example.com/cell");
        Assert.InRange(Rect(cell)[0], 39.99, 190.01);
        Assert.InRange(Rect(cell)[2], 39.99, 190.01);

        // A footer band prints in the bottom margin region; a header band in the top one.
        foreach (var footer in links.Where(l => UriOf(pdf, l.Dict) == "https://example.com/footer"))
        {
            var r = Rect(footer);
            Assert.InRange(r[1], 0, 60);
            Assert.InRange(r[3], 0, 60);
        }
        foreach (var header in links.Where(l => l.Dict.Get(N("Dest")) is { } d && DestKey(d) == "toc" && Rect(l)[1] > 440))
        {
            var r = Rect(header);
            Assert.InRange(r[3], 440, 500);
            Assert.InRange(r[0], 39.99, 360.01);
        }
    }

    // ── 12.3.3 The outline ──────────────────────────────────────────────────────────────────

    private static int Descendants(OutlineItem item) =>
        item.Children.Sum(c => 1 + (c.Count > 0 ? Descendants(c) : 0));

    private static int AllBelow(OutlineItem item) => item.Children.Sum(c => 1 + AllBelow(c));

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_3_3_TheOutlineIsALinkedListWhoseCountsFollowTable153(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        Assert.NotNull(pdf.Catalog.Get(N("Outlines")));
        var (root, top) = ReadOutline(pdf); // asserts Parent, Prev, Next, First and Last as it walks

        Assert.Equal("Outlines", Assert.IsType<PdfName>(root.Get(PdfName.Type)).Value);
        Assert.Equal(["Contents", "Chapter one", "Appendix"], top.Select(i => i.Title));
        var chapter = top[1];
        Assert.Equal(
            ["A section whose closed bookmark holds two children", "Linking heading: see the table", "The long link"],
            chapter.Children.Select(i => i.Title));
        var closed = chapter.Children[0];
        Assert.Equal(["Child of the closed section", "Another child"], closed.Children.Select(i => i.Title));

        // A closed item has a negative Count whose size is the number of descendants that would show
        // if it were opened; an open one has the number that show; a childless one has none.
        Assert.True(closed.HasCount);
        Assert.Equal(-2, closed.Count);
        Assert.Equal(AllBelow(closed), -closed.Count);
        Assert.Equal(3, chapter.Count); // its three children, and none of the two the closed item hides
        Assert.Equal(Descendants(chapter), chapter.Count);
        Assert.All(Flatten(top).Where(i => i.Children.Count == 0), i => Assert.False(i.HasCount));

        // The outline dictionary's Count: "total number of visible outline items at all levels",
        // never negative.
        var visible = top.Count + top.Where(i => i.Count > 0).Sum(Descendants);
        Assert.Equal(6, visible);
        Assert.Equal(visible, Assert.IsType<PdfInteger>(root.Get(N("Count"))).Value);
    }

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_3_3_EachItemHasATextStringTitleAndAnExplicitDestinationNotAnAction(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var (_, top) = ReadOutline(pdf);
        var items = Flatten(top).ToList();

        Assert.Equal(8, items.Count);
        var previousPage = 0;
        foreach (var item in items)
        {
            Assert.NotEmpty(item.Title);
            Assert.Null(item.Dict.Get(N("A"))); // Dest and A are exclusive; only Dest is written
            var dest = pdf.Array(item.Dict.Get(N("Dest")));
            Assert.Equal(5, dest.Count);
            Assert.Equal("XYZ", Assert.IsType<PdfName>(dest[1]).Value);
            Assert.InRange(Number(dest[3]), 0, 500);
            // Document order is page order here: the list is in the order the headings occur.
            var page = PageIndexOf(pdf, dest);
            Assert.True(page >= previousPage);
            previousPage = page;
        }
    }

    [Fact]
    public void Iso32000_1_12_3_3_ASplitHeadingHasOneBookmarkAtItsFirstLine()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfA2u);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var style = new TextStyle { FontRef = font, FontSize = 11 };
        // A paragraph of 28 lines first, so the heading starts near the foot of page 1 and goes on
        // to page 2; it holds a closed bookmark and a child, which must keep the count of one.
        doc.Add(new Paragraph(string.Join(' ', Enumerable.Repeat("filler", 190)), style));
        var text = string.Join(' ', Enumerable.Repeat("a heading that is much too long for the space that is left on its page", 12));
        doc.Add(new Heading([new TextRun(text, style)]) { Level = 0, BookmarkTitle = "Split heading", BookmarkExpanded = false });
        doc.Add(new Heading([new TextRun("Child", style)]) { Level = 1 });
        var bytes = Save(doc);
        using var pdf = new Pdf(bytes);

        var (_, top) = ReadOutline(pdf);

        var split = Assert.Single(top, i => i.Title == "Split heading");
        Assert.Single(Flatten(top), i => i.Title == "Split heading");
        // The heading really did split: the pages it reaches hold the heading's text twice or more.
        var text0 = PopplerText(bytes, 0);
        var text1 = PopplerText(bytes, 1);
        Assert.Contains("a heading that is much", text0, StringComparison.Ordinal);
        Assert.Contains("a heading that is much", text1, StringComparison.Ordinal);
        // The bookmark goes to the first line, not to the page the heading ends on.
        var firstLinePage = Enumerable.Range(0, pdf.Reader.PageCount)
            .First(p => PopplerText(bytes, p).Contains("a heading that is much", StringComparison.Ordinal));
        Assert.Equal(firstLinePage, PageIndexOf(pdf, split.Dict.Get(N("Dest"))!));
        // A closed item with one child: Count is -1.
        Assert.Equal(-1, split.Count);
    }

    // ── 12.4.2 Page labels, from Layout's ranges ────────────────────────────────────────────

    [Theory]
    [MemberData(nameof(Claims))]
    public void Iso32000_1_12_4_2_LayoutRangesLabelThePagesTheyStartOn(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var count = pdf.Reader.PageCount;
        var tree = LabelsTree(pdf);

        Assert.Equal([0L, 1, count - 1], tree.Entries.Select(e => e.NumberKey));
        var labels = EvaluateLabels(pdf, count);
        Assert.Equal("i", labels[0]);
        Assert.Equal(Enumerable.Range(1, count - 2).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)), labels[1..^1]);
        Assert.Equal("A-8", labels[^1]);

        var appendix = pdf.Dict(tree.Entries[2].Value);
        Assert.Equal("D", Assert.IsType<PdfName>(appendix.Get(N("S"))).Value);
        Assert.Equal(8, Assert.IsType<PdfInteger>(appendix.Get(N("St"))).Value);
        Assert.Equal("A-", TextString(appendix.Get(N("P"))!));
    }

    [Fact]
    public void Iso32000_1_12_4_2_APageReferencePrintsTheLabelThePageHasInTheFile()
    {
        var bytes = Navigation(PdfConformance.PdfA2u);
        using var pdf = new Pdf(bytes);
        var labels = EvaluateLabels(pdf, pdf.Reader.PageCount);
        var tree = DestsTree(pdf);
        string LabelOf(string anchor) =>
            labels[PageIndexOf(pdf, tree.Entries.Single(e => Encoding.ASCII.GetString(e.NameKey!) == anchor).Value)];

        // The contents list is on the first page; each entry prints the label of the page its anchor
        // is on, whichever range that page is in: 1 in the decimal body, A-8 in the appendix.
        var squashed = PopplerText(bytes, 0);
        Assert.Equal("1", LabelOf("ch1"));
        Assert.Equal("A-8", LabelOf("appendix"));
        Assert.Contains("Chapter one, page " + LabelOf("ch1"), squashed, StringComparison.Ordinal);
        Assert.Contains("The long link, page " + LabelOf("longlink"), squashed, StringComparison.Ordinal);
        Assert.Contains("Appendix, page A-8", squashed, StringComparison.Ordinal);

        // A reference in a table cell and in a paragraph of the appendix.
        var body = PopplerText(bytes, 1);
        Assert.Contains("cell with a page reference, page " + LabelOf("image"), body, StringComparison.Ordinal);
        var appendixText = PopplerText(bytes, pdf.Reader.PageCount - 1);
        Assert.Contains("which is on page 1", appendixText, StringComparison.Ordinal);

    }

    [Fact]
    public void Iso32000_1_12_4_2_ALabelFollowsABlankRectoPageAndTheTreeAgreesWithThePrintedReferences()
    {
        // No range at page 0 and a Recto break that inserts a blank page: the file's number tree and
        // the text the references print must agree, whichever page the library gives the range.
        var doc = NewLayoutDocument(PdfConformance.None);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var style = new TextStyle { FontRef = font, FontSize = 11 };
        doc.Add(new Paragraph("first", style) { Anchor = "a" });
        doc.Add(new PageBreak { Recto = true, PageLabel = new PageLabelRange(PdfPageLabelStyle.UpperRoman, "X-", 4) });
        doc.Add(new Paragraph([new TextRun("ref ", style), TextRun.PageReference("a", style), new TextRun(" and ", style), TextRun.PageReference("b", style)]) { Anchor = "b" });
        var bytes = Save(doc);
        using var pdf = new Pdf(bytes);

        Assert.Equal(3, pdf.Reader.PageCount);
        var labels = EvaluateLabels(pdf, 3);
        Assert.Equal(["1", "2", "X-IV"], labels);
        Assert.Contains("ref 1 and X-IV", PopplerText(bytes, 2), StringComparison.Ordinal);
    }

    [Fact]
    public void Iso32000_1_12_3_2_3_AnAnchorOnAParagraphThatSplitsIsAtItsFirstLine()
    {
        var doc = NewLayoutDocument(PdfConformance.None);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var style = new TextStyle { FontRef = font, FontSize = 11 };
        doc.Add(new Paragraph(string.Join(' ', Enumerable.Repeat("filler", 190)), style));
        doc.Add(new Paragraph("SPLITSTART " + string.Join(' ', Enumerable.Repeat("words that make the paragraph run on to the next page", 20)), style) { Anchor = "split" });
        var bytes = Save(doc);
        using var pdf = new Pdf(bytes);

        var entry = Assert.Single(DestsTree(pdf).Entries);
        var firstPage = Enumerable.Range(0, pdf.Reader.PageCount)
            .First(p => PopplerText(bytes, p).Contains("SPLITSTART", StringComparison.Ordinal));
        Assert.True(pdf.Reader.PageCount >= 2);
        Assert.Equal(firstPage, PageIndexOf(pdf, entry.Value));
        // And below the filler on that page (the filler is about 19 lines), not at the top of it.
        Assert.InRange(Number(pdf.Array(entry.Value)[3]), 200, 240);
    }

    [Fact]
    public void Iso14289_1_7_17_AnOutlineWhoseLevelsSkipIsNestedUnderTheEarlierItemOfTheLevelAbove()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfA2u);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var style = new TextStyle { FontRef = font, FontSize = 11 };
        foreach (var (title, level) in new[] { ("A", 0), ("B", 1), ("C", 0), ("D", 2), ("E", 1) })
            doc.Add(new Heading([new TextRun(title, style)]) { Level = level });
        using var pdf = new Pdf(Save(doc));

        var (_, top) = ReadOutline(pdf);

        // What the file holds today: D is a child of B, which is under A, although C comes between B
        // and D in the document. Valid under 12.3.3 (the order of a linked list is the list's own);
        // it does not match the reading order that 7.17 asks of an outline. See the skipped test.
        Assert.Equal(["A", "C"], top.Select(i => i.Title));
        Assert.Equal(["A", "B", "D", "C", "E"], Flatten(top).Select(i => i.Title));
    }

    [Fact(Skip = "#736: an outline whose level skips lists a heading before an earlier one; ISO 14289-1, 7.17 (should)")]
    public void Iso14289_1_7_17_TheOutlineListsHeadingsInTheOrderTheyOccur()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfA2u);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var style = new TextStyle { FontRef = font, FontSize = 11 };
        foreach (var (title, level) in new[] { ("A", 0), ("B", 1), ("C", 0), ("D", 2), ("E", 1) })
            doc.Add(new Heading([new TextRun(title, style)]) { Level = level });
        using var pdf = new Pdf(Save(doc));

        Assert.Equal(["A", "B", "C", "D", "E"], Flatten(ReadOutline(pdf).Top).Select(i => i.Title));
    }
}
