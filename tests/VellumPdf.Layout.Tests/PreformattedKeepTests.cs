// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Reader;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <c>Preformatted.KeepTogether</c> and <c>KeepWithNext</c>. A page is 300 x 200 pt with 20 pt
/// margins, so the content box is 160 pt tall. A filler paragraph line is 14.4 pt and a block
/// line 12 pt, so ten filler lines leave 16 pt, room for one block line and 4 pt over. Every
/// expectation is worked out from that geometry and read back as the words on each page.
/// </summary>
public sealed class PreformattedKeepTests
{
    private static Document NewDoc() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 200),
        Margins = new EdgeInsets(20),
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    private static string Seq(string tag, int count) =>
        string.Join('\n', Enumerable.Range(1, count).Select(i => tag + i.ToString(CultureInfo.InvariantCulture)));

    private static string[] Words(string tag, int from, int to) =>
        [.. Enumerable.Range(from, to - from + 1).Select(i => tag + i.ToString(CultureInfo.InvariantCulture))];

    private static Paragraph Filler(int lines) => new(Seq("f", lines), Body);

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[][] Pages(Document doc)
    {
        using var reader = PdfReader.Open(Save(doc));
        return [.. Enumerable.Range(0, reader.PageCount).Select(i =>
            reader.GetPage(i).ExtractText().Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))];
    }

    private static (LayoutDiagnosticCode Code, LayoutElementKind Kind, int Index, int Page)[] Keep(Document doc) =>
        [.. doc.Diagnostics
            .Where(d => d.Code is LayoutDiagnosticCode.KeepTogetherRelaxed or LayoutDiagnosticCode.KeepWithNextRelaxed)
            .Select(d => (d.Code, d.ElementKind, d.ElementIndex ?? -1, d.PageNumber ?? -1))];

    [Fact]
    public void BothFlags_areFalseByDefault()
    {
        var block = new Preformatted("x");

        Assert.False(block.KeepTogether);
        Assert.False(block.KeepWithNext);
    }

    [Fact]
    public void Block_withoutTheFlag_splitsAtThePageFoot()
    {
        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted(Seq("p", 10)));
        var pages = Pages(doc);

        Assert.Equal([.. Words("f", 1, 10), "p1"], pages[0]);
        Assert.Equal(Words("p", 2, 10), pages[1]);
        Assert.Empty(Keep(doc));
    }

    [Fact]
    public void KeepTogether_movesAllTenLinesToPage2()
    {
        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted(Seq("p", 10)) { KeepTogether = true });
        var pages = Pages(doc);

        Assert.Equal(Words("f", 1, 10), pages[0]);
        Assert.Equal(Words("p", 1, 10), pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepTogether_thatFitsWhereItStands_isNotMoved()
    {
        using var doc = NewDoc();
        doc.Add(Filler(2));
        doc.Add(new Preformatted(Seq("p", 10)) { KeepTogether = true });
        var pages = Pages(doc);

        Assert.Single(pages);
        Assert.Equal([.. Words("f", 1, 2), .. Words("p", 1, 10)], pages[0]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepTogether_ofABlockThatFillsAPage_atTheTopOfTheDocument_isNotReported()
    {
        using var doc = NewDoc();
        doc.Add(new Preformatted(Seq("p", 13)) { KeepTogether = true });

        Assert.Single(Pages(doc));
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepTogether_ofABlockTallerThanAPage_splitsAsWithoutTheFlag_andIsReported()
    {
        // Thirteen 12 pt lines fill a page; twenty cannot be held by any page.
        using var plain = NewDoc();
        plain.Add(Filler(4));
        plain.Add(new Preformatted(Seq("p", 20)));
        var expected = Pages(plain);

        using var doc = NewDoc();
        doc.Add(Filler(4));
        doc.Add(new Preformatted(Seq("p", 20)) { KeepTogether = true });
        var pages = Pages(doc);

        Assert.Equal(expected, pages);
        Assert.Equal([.. Words("f", 1, 4), .. Words("p", 1, 8)], pages[0]);
        Assert.Equal(Words("p", 9, 20), pages[1]);
        Assert.Equal(
            [(LayoutDiagnosticCode.KeepTogetherRelaxed, LayoutElementKind.Preformatted, 1, 1)],
            Keep(doc));
        var diagnostic = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal(1, diagnostic.Count);
    }

    [Fact]
    public void KeepTogether_ofABlockTallerThanAPage_atThePageTop_isReportedOnce()
    {
        using var doc = NewDoc();
        doc.Add(new Preformatted(Seq("p", 30)) { KeepTogether = true });
        var pages = Pages(doc);

        Assert.Equal(3, pages.Length);
        Assert.Equal(
            [(LayoutDiagnosticCode.KeepTogetherRelaxed, LayoutElementKind.Preformatted, 0, 1)],
            Keep(doc));
        Assert.Equal(1, doc.Diagnostics[0].Count);
    }

    [Fact]
    public void KeepTogether_countsAWrappedLineAsTheLinesItWrapsInto()
    {
        // Three source lines, so the block would fit the 16 pt that are left only if a source line
        // were a drawn line. The first wraps into several at this width, so the block moves.
        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted(new string('w', 200) + "\nb\nc") { KeepTogether = true });
        var pages = Pages(doc);

        Assert.Equal(Words("f", 1, 10), pages[0]);
        Assert.Equal(["b", "c"], pages[1][^2..]);
        Assert.DoesNotContain("b", pages[0]);
        Assert.Contains(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.PreformattedLineWrapped);
        Assert.Empty(Keep(doc));
    }

    [Fact]
    public void KeepTogether_isReadFromTheTopLevelBlockOnly_theOverflowIsNotMovedOrReportedAgain()
    {
        // Eleven filler lines leave 1.6 pt, so nothing of the block fits: it goes to page 2, is too
        // tall for it, and splits there. The overflow on page 3 is not moved again and not
        // reported a second time.
        using var doc = NewDoc();
        doc.Add(Filler(11));
        doc.Add(new Preformatted(Seq("p", 20)) { KeepTogether = true });
        var pages = Pages(doc);

        Assert.Equal(3, pages.Length);
        Assert.Equal(Words("f", 1, 11), pages[0]);
        Assert.Equal(Words("p", 1, 13), pages[1]);
        Assert.Equal(Words("p", 14, 20), pages[2]);
        Assert.Equal(
            [(LayoutDiagnosticCode.KeepTogetherRelaxed, LayoutElementKind.Preformatted, 1, 2)],
            Keep(doc));
    }

    [Fact]
    public void KeepWithNext_ofASingleLineBlock_movesItToTheNextPageWithTheParagraphAfterIt()
    {
        // 16 pt are left: the line fits, the paragraph after it does not.
        using var control = NewDoc();
        control.Add(Filler(10));
        control.Add(new Preformatted("p1"));
        control.Add(new Paragraph("next", Body));
        var controlPages = Pages(control);
        Assert.Equal([.. Words("f", 1, 10), "p1"], controlPages[0]);
        Assert.Equal(["next"], controlPages[1]);

        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted("p1") { KeepWithNext = true });
        doc.Add(new Paragraph("next", Body));
        var pages = Pages(doc);

        Assert.Equal(Words("f", 1, 10), pages[0]);
        Assert.Equal(["p1", "next"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void KeepWithNext_behavesAsAParagraphOfTheSameLinesDoes(int lines)
    {
        // The same text in the same font and leading, once as a paragraph and once as a block.
        var style = new TextStyle { Font = Standard14.Courier, FontSize = 10 };
        using var asParagraph = NewDoc();
        asParagraph.Add(Filler(10));
        asParagraph.Add(new Paragraph(Seq("p", lines), style) { KeepWithNext = true });
        asParagraph.Add(new Paragraph("next", Body));

        using var asBlock = NewDoc();
        asBlock.Add(Filler(10));
        asBlock.Add(new Preformatted(Seq("p", lines)) { KeepWithNext = true });
        asBlock.Add(new Paragraph("next", Body));

        Assert.Equal(Pages(asParagraph), Pages(asBlock));
        Assert.Equal(
            asParagraph.Diagnostics.Select(d => (d.Code, d.PageNumber)),
            asBlock.Diagnostics.Select(d => (d.Code, d.PageNumber)));
    }

    [Fact]
    public void KeepWithNext_ofABlockThatSplits_holdsItsLastPieceWithTheNextElement()
    {
        // Three block lines, 16 pt left: one line stays, two go on, and the paragraph follows the
        // piece on page 2, so the run holds as it stands. A paragraph does the same.
        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted(Seq("p", 3)) { KeepWithNext = true });
        doc.Add(new Paragraph("next", Body));
        var pages = Pages(doc);

        Assert.Equal([.. Words("f", 1, 10), "p1"], pages[0]);
        Assert.Equal(["p2", "p3", "next"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepWithNext_thatCannotHold_isDroppedAndReported()
    {
        // A full page of block lines and a paragraph do not fit one page from the top either, so
        // the request is dropped, as for a paragraph.
        using var doc = NewDoc();
        doc.Add(new Preformatted(Seq("p", 13)) { KeepWithNext = true });
        doc.Add(new Paragraph("next", Body));
        var pages = Pages(doc);

        Assert.Equal(Words("p", 1, 13), pages[0]);
        Assert.Equal(["next"], pages[1]);
        Assert.Equal(
            [(LayoutDiagnosticCode.KeepWithNextRelaxed, LayoutElementKind.Preformatted, 0, 1)],
            Keep(doc));
    }

    [Fact]
    public void KeepWithNext_endsAtABreakBeforeOnTheNextElement()
    {
        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted("p1") { KeepWithNext = true });
        doc.Add(new Paragraph("next", Body) { BreakBefore = PageBreakMode.Page });
        var pages = Pages(doc);

        Assert.Equal([.. Words("f", 1, 10), "p1"], pages[0]);
        Assert.Equal(["next"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void KeepTogetherAndKeepWithNext_together_moveTheBlockWholeWithTheNextElement()
    {
        // 8 x 14.4 = 115.2, so 44.8 pt are left: the three lines (36) fit, the paragraph (14.4) does not.
        using var doc = NewDoc();
        doc.Add(Filler(8));
        doc.Add(new Preformatted(Seq("p", 3)) { KeepTogether = true, KeepWithNext = true });
        doc.Add(new Paragraph("next", Body));
        var pages = Pages(doc);

        Assert.Equal(Words("f", 1, 8), pages[0]);
        Assert.Equal([.. Words("p", 1, 3), "next"], pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void BreakBefore_withKeepTogether_startsThePageAndIsNotReported()
    {
        using var doc = NewDoc();
        doc.Add(Filler(3));
        doc.Add(new Preformatted(Seq("p", 10)) { KeepTogether = true, BreakBefore = PageBreakMode.Page });
        var pages = Pages(doc);

        Assert.Equal(Words("f", 1, 3), pages[0]);
        Assert.Equal(Words("p", 1, 10), pages[1]);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void Anchor_withKeepTogether_isOnThePageTheBlockMovedTo()
    {
        using var doc = NewDoc();
        doc.Add(Filler(10));
        doc.Add(new Preformatted(Seq("p", 10)) { KeepTogether = true, Anchor = "moved" });
        var pdf = Save(doc);

        var text = Regex.Replace(System.Text.Encoding.Latin1.GetString(pdf), @"\s+", " ");
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pageObjects = Regex.Matches(kids, @"(\d+) 0 R").Select(m => m.Groups[1].Value).ToArray();
        var names = Regex.Match(text, @"/Dests << /Names \[(.*?)\]").Groups[1].Value;
        var pair = Regex.Match(names, @"\(moved\) (\d+) 0 R");
        Assert.True(pair.Success);
        var dest = Regex.Match(text, @"(?<![0-9])" + pair.Groups[1].Value + @" 0 obj \[(\d+) 0 R /XYZ (\S+) (\S+) null\] endobj");
        Assert.True(dest.Success);

        Assert.Equal(pageObjects[1], dest.Groups[1].Value);
        Assert.Equal(180, double.Parse(dest.Groups[3].Value, CultureInfo.InvariantCulture), 4);
    }

    // The probe of the report that asked for the flags: 52 one-line Courier paragraphs, then a
    // block of ten lines, on the default page.
    private static Document Probe(bool keep)
    {
        var doc = new Document
        {
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        var courier = new TextStyle { Font = Standard14.Courier, FontSize = 10 };
        for (var i = 1; i <= 52; i++)
            doc.Add(new Paragraph("filler" + i.ToString(CultureInfo.InvariantCulture), courier));
        doc.Add(new Preformatted(Seq("p", 10)) { KeepTogether = keep });
        return doc;
    }

    [Fact]
    public void TheReportedProbe_splitsWithoutTheFlag_andStaysWholeWithIt()
    {
        using var plain = Probe(false);
        var plainPages = Pages(plain);
        Assert.Equal(Words("p", 1, 6), plainPages[0][^6..]);
        Assert.Equal(Words("p", 7, 10), plainPages[1]);

        using var kept = Probe(true);
        var pages = Pages(kept);
        Assert.Equal(2, pages.Length);
        Assert.Equal("filler52", pages[0][^1]);
        Assert.Equal(Words("p", 1, 10), pages[1]);
        Assert.Empty(kept.Diagnostics);
    }

    [Fact]
    public void ABlockWithoutTheFlags_keepsItsBytes()
    {
        // Recorded on the commit before the flags existed, with this body, which uses neither.
        using var doc = Probe(false);
        doc.Add(new Preformatted("a" + (char)10 + (char)9 + "b") { Margins = new EdgeInsets(3), Anchor = "end", BreakBefore = PageBreakMode.Page });

        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();
        var actual = $"{NormalisedSha256(bytes)} pages={PageCount(bytes)}";
        Assert.True("F7493F02E260244728D679CE70870C6F495F403B0E4321DCF4149FEB36263846 pages=3" == actual, $"computed {actual}");
    }

    [Fact]
    public void TheContinuationLimit_refusesAKeptBlockAsItRefusesAPlainOne()
    {
        // One line a page: a block of more lines than the limit allows continuations.
        static Document Build(bool keep)
        {
            var doc = new Document { PageSize = new PdfRectangle(0, 0, 100, 14), Margins = new EdgeInsets(1) };
            doc.Add(new Preformatted(string.Join('\n', Enumerable.Repeat("x", 50_010))) { KeepTogether = keep });
            return doc;
        }

        using var plain = Build(false);
        var expected = Assert.Throws<InvalidOperationException>(() => Save(plain));
        using var kept = Build(true);
        var actual = Assert.Throws<InvalidOperationException>(() => Save(kept));

        Assert.Equal(expected.Message, actual.Message);
    }
}
