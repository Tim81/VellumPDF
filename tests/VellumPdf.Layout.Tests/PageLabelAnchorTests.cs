// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Reader;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #262, Layout half: page-label ranges (<see cref="Document.PageLabel"/>,
/// <see cref="PageBreak.PageLabel"/>), <c>{page}</c> printing the label, and an element's
/// <c>Anchor</c> becoming a named destination. A page is 300 x 300 pt with 20 pt margins, so a
/// paragraph with a 200 pt top margin is 214.4 pt of the 260 pt content height and two of them
/// never share a page.
/// Expected positions are worked out from that geometry and the style's leading, not read back
/// from the files.
/// </summary>
public sealed class PageLabelAnchorTests
{
    // ── Fixtures ─────────────────────────────────────────────────────────────

    private static Paragraph Tall(string label) =>
        new(label, Body) { Margins = new EdgeInsets(200, 0, 0, 0) };

    private static Document NewDocument() => new() { PageSize = Page, Margins = Margins };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // The first line of a page at the top of the content area, in PDF coordinates.
    private static readonly double TopOfPage = Page.Height - Margins.Top;

    private static PageLabelRange Range(PdfPageLabelStyle style, string? prefix = null, int first = 1) =>
        new(style, prefix, first);

    // Front matter in lower-case roman, a recto break that starts the decimal body, and a body of
    // `bodyPages` pages: pages 0-2 are i-iii, page 3 is the recto blank, pages 4.. are 1..
    private static Document RomanThenDecimal(int bodyPages = 4)
    {
        var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.LowerRoman);
        doc.Footer = new RunningBand("{page}");
        doc.Add(Tall("front a"));
        doc.Add(Tall("front b"));
        doc.Add(Tall("front c"));
        doc.Add(new PageBreak { Recto = true, PageLabel = Range(PdfPageLabelStyle.Decimal) });
        for (var i = 1; i <= bodyPages; i++)
            doc.Add(Tall($"body {i}"));
        return doc;
    }

    private static string Flat(byte[] pdf) => Regex.Replace(Encoding.Latin1.GetString(pdf), @"\s+", " ");

    // A label prefix is written as a UTF-16BE text string with a byte order mark (ISO 32000-2,
    // 7.9.2.2): FE FF, then two bytes a character. In the Latin-1 view of the file the byte order
    // mark is two characters, and the high byte of an ASCII character is a NUL character.
    private static string Text(string ascii) =>
        "(\u00FE\u00FF" + string.Concat(ascii.Select(c => "\0" + c)) + ")";

    private static string Nums(byte[] pdf)
    {
        var m = Regex.Match(Flat(pdf), @"/PageLabels << (/Nums \[.*?\]) >>");
        Assert.True(m.Success, "no /PageLabels tree in the catalog");
        return m.Groups[1].Value;
    }

    // The last word on each page: the footer, which is the last text a page is given.
    private static string[] FooterTokens(byte[] pdf)
    {
        using var reader = PdfReader.Open(pdf);
        var result = new string[reader.PageCount];
        for (var i = 0; i < result.Length; i++)
        {
            var words = reader.GetPage(i).ExtractText().Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            result[i] = words.Length == 0 ? "" : words[^1];
        }

        return result;
    }

    private sealed record Dest(int Page, double Left, double Top);

    // The name tree and the destinations it names, read from the file text: no oracle is involved,
    // so these run everywhere. Page numbers are 0-based and follow the page tree's /Kids order.
    private static Dictionary<string, Dest> Dests(byte[] pdf)
    {
        var text = Flat(pdf);
        var kids = Regex.Match(text, @"/Kids \[([^\]]*)\]").Groups[1].Value;
        var pageOf = new Dictionary<int, int>();
        foreach (Match kid in Regex.Matches(kids, @"(\d+) 0 R"))
            pageOf[int.Parse(kid.Groups[1].Value, CultureInfo.InvariantCulture)] = pageOf.Count;

        var result = new Dictionary<string, Dest>(StringComparer.Ordinal);
        var names = Regex.Match(text, @"/Dests << /Names \[(.*?)\]").Groups[1].Value;
        foreach (Match pair in Regex.Matches(names, @"\(([^)]*)\) (\d+) 0 R"))
        {
            var obj = Regex.Match(
                text,
                @"(?<![0-9])" + pair.Groups[2].Value + @" 0 obj \[(\d+) 0 R /XYZ (\S+) (\S+) null\] endobj");
            Assert.True(obj.Success, $"destination object {pair.Groups[2].Value} for '{pair.Groups[1].Value}'");
            result[pair.Groups[1].Value] = new Dest(
                pageOf[int.Parse(obj.Groups[1].Value, CultureInfo.InvariantCulture)],
                double.Parse(obj.Groups[2].Value, CultureInfo.InvariantCulture),
                double.Parse(obj.Groups[3].Value, CultureInfo.InvariantCulture));
        }

        return result;
    }

    private sealed class TempPdf : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "vellum_labels_" + Guid.NewGuid().ToString("N") + ".pdf");

        internal TempPdf(byte[] pdf) => File.WriteAllBytes(Path, pdf);

        public void Dispose()
        {
            try { File.Delete(Path); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    private static string Qpdf(params string[] args)
    {
        ExternalTool.TryRun("qpdf", args, out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf {string.Join(' ', args)} exit {exit}\n{stdout}\n{stderr}");
        return stdout;
    }

    private static void AssertQpdfClean(string path) =>
        Assert.Contains("No syntax or stream encoding errors found", Qpdf("--check", path));

    // The pages of `qpdf --json`, each with its label dictionary.
    private static List<(int Page, string Style, int Start)> QpdfPageLabels(string path)
    {
        using var json = JsonDocument.Parse(Qpdf("--json", "--json-key=pages", path));
        var result = new List<(int, string, int)>();
        foreach (var page in json.RootElement.GetProperty("pages").EnumerateArray())
        {
            var label = page.GetProperty("label");
            result.Add((
                page.GetProperty("pageposfrom1").GetInt32(),
                label.GetProperty("/S").GetString()!,
                label.GetProperty("/St").GetInt32()));
        }

        return result;
    }

    private static List<(int Page, double Top, string Name)> PdfinfoDests(string path)
    {
        if (!ExternalTool.TryRun("pdfinfo", ["-dests", path], out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdfinfo");
        Assert.False(timedOut);
        Assert.True(exit == 0, $"pdfinfo -dests exit {exit}\n{stdout}\n{stderr}");
        var result = new List<(int, double, string)>();
        foreach (var line in stdout.Split('\n', StringSplitOptions.TrimEntries))
        {
            var m = Regex.Match(line, @"^(\d+)\s+\[\s*XYZ\s+(\S+)\s+(\S+)\s+null\s*\]\s+""([^""]*)""$");
            if (m.Success)
            {
                result.Add((
                    int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture),
                    m.Groups[4].Value));
            }
        }

        return result;
    }

    // ── Page labels: the number tree ─────────────────────────────────────────

    [Fact]
    public void RomanFrontMatterThenDecimalBody_writesTheExactNumberTree()
    {
        // Page index 3 is the recto blank and keeps the roman range; the body range starts at 4.
        Assert.Equal("/Nums [0 << /S /r >> 4 << /S /D >>]", Nums(Save(RomanThenDecimal())));
    }

    [Fact]
    public void BodyRangeAfterACover_getsAnImplicitDecimalRangeAtZero()
    {
        using var doc = NewDocument();
        doc.Add(Tall("cover"));
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.Decimal) });
        doc.Add(Tall("body"));

        // 12.4.2 requires a value for page index 0; the cover then reads 1, as without labels.
        Assert.Equal("/Nums [0 << /S /D >> 1 << /S /D >>]", Nums(Save(doc)));
    }

    [Fact]
    public void FirstNumberAndPrefix_areWrittenAsStAndP()
    {
        using var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.UpperLetters, null, 8);
        doc.Add(Tall("a"));
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.Decimal, "A-", 8) });
        doc.Add(Tall("b"));

        Assert.Equal("/Nums [0 << /S /A /St 8 >> 1 << /S /D /P " + Text("A-") + " /St 8 >>]", Nums(Save(doc)));
    }

    [Fact]
    public void NoRange_writesNoPageLabelsTree()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));

        Assert.DoesNotContain("/PageLabels", Encoding.Latin1.GetString(Save(doc)), StringComparison.Ordinal);
    }

    [Fact]
    public void TwoBreaksOnOnePage_theLaterRangeWins()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.UpperRoman) });
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.LowerLetters) });
        doc.Add(Tall("b"));

        Assert.Equal("/Nums [0 << /S /D >> 1 << /S /a >>]", Nums(Save(doc)));
    }

    [Fact]
    public void BreakOnTheFirstPage_replacesTheDocumentsRange()
    {
        using var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.UpperRoman);
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.LowerLetters) });
        doc.Add(Tall("a"));

        // The break adds no page before the first element, so its range starts at page index 0.
        Assert.Equal("/Nums [0 << /S /a >>]", Nums(Save(doc)));
    }

    [Fact]
    public void DocumentRange_withNoBreak_startsAtTheFirstPage()
    {
        using var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.LowerRoman);
        doc.Add(Tall("a"));

        Assert.Equal("/Nums [0 << /S /r >>]", Nums(Save(doc)));
    }

    [Fact]
    public void RectoBreakWithARange_startsItAfterTheBlankPage()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        // The next page would be index 1, a left-hand page, so index 1 is left blank and the
        // element after the break is on index 2, which is where the range starts.
        doc.Add(new PageBreak { Recto = true, PageLabel = Range(PdfPageLabelStyle.Decimal, "S-") });
        doc.Add(Tall("b"));

        Assert.Equal("/Nums [0 << /S /D >> 2 << /S /D /P " + Text("S-") + " >>]", Nums(Save(doc)));
    }

    [Fact]
    public void CustomRenderer_afterALabelledBreak_isLabelled()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.UpperLetters) });
        doc.Add(new FixedHeight(50));

        Assert.Equal("/Nums [0 << /S /D >> 1 << /S /A >>]", Nums(Save(doc)));
    }

    private sealed class FixedHeight(double height) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(height));

        public void Draw(DrawContext ctx)
        {
        }
    }

    // ── {page} ───────────────────────────────────────────────────────────────

    [Fact]
    public void Footer_printsLabels_andNothingOnTheBlankPage()
    {
        Assert.Equal(["i", "ii", "iii", "", "1", "2", "3", "4"], FooterTokens(Save(RomanThenDecimal())));
    }

    [Fact]
    public void Footer_pagesStaysThePhysicalCount_blankPageIncluded()
    {
        using var doc = RomanThenDecimal();
        doc.Footer = new RunningBand("{pages}");

        Assert.Equal(["8", "8", "8", "", "8", "8", "8", "8"], FooterTokens(Save(doc)));
    }

    [Fact]
    public void Footer_withoutAnyRange_printsTheDecimalNumberAsBefore()
    {
        using var doc = NewDocument();
        doc.Footer = new RunningBand("{page}");
        doc.Add(Tall("a"));
        doc.Add(Tall("b"));
        doc.Add(Tall("c"));

        Assert.Equal(["1", "2", "3"], FooterTokens(Save(doc)));
    }

    [Fact]
    public void Header_printsLabelsToo_andRangesStartingLaterLeaveEarlierPagesDecimal()
    {
        using var doc = NewDocument();
        doc.Header = new RunningBand("{page}");
        doc.Add(Tall("a"));
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.UpperRoman, null, 4) });
        doc.Add(Tall("b"));
        doc.Add(Tall("c"));

        // The header is drawn after the content, so it is the last word as the footer is.
        Assert.Equal(["1", "IV", "V"], FooterTokens(Save(doc)));
    }

    [Fact]
    public void Footer_printsAPrefixThatLooksLikeAToken_asGiven()
    {
        using var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.Decimal, "{pages}-");
        doc.Footer = new RunningBand("{page}");
        doc.Add(Tall("a"));

        // The label is copied, never scanned again, so the prefix is not replaced by the count.
        Assert.Equal(["{pages}-1"], FooterTokens(Save(doc)));
    }

    [Fact]
    public void PlanLabels_matchTheFootersTheDrawingPassPrinted()
    {
        using var doc = RomanThenDecimal();
        var renderer = doc.CreateRenderer();
        renderer.RunLayout();
        var plan = renderer.DrawPlan!;

        string[] expected = ["i", "ii", "iii", "iv", "1", "2", "3", "4"];
        Assert.Equal(expected, Enumerable.Range(0, plan.PageCount).Select(plan.LabelOf).ToArray());
        Assert.Equal(8, plan.PageCount);
    }

    [Fact]
    public void PlanLabels_withoutRanges_areTheDecimalNumber()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        doc.Add(Tall("b"));
        var renderer = doc.CreateRenderer();
        renderer.RecordPlacements = true;
        renderer.RunLayout();

        Assert.Equal(["1", "2"], Enumerable.Range(0, 2).Select(renderer.DrawPlan!.LabelOf).ToArray());
        Assert.Equal("1", PaginationPlan.Placeholder.LabelOf(0));
    }

    [Fact]
    public void PageLabelMap_laterRangeAtTheSameStartReplaces_andPagesBeforeTheFirstRangeAreDecimal()
    {
        var map = new PageLabelMap();
        Assert.Equal("5", map.LabelOf(4));

        map.Add(2, Range(PdfPageLabelStyle.UpperRoman));
        map.Add(2, Range(PdfPageLabelStyle.LowerLetters, "x-", 3));
        map.Add(5, Range(PdfPageLabelStyle.Decimal, null, 10));

        Assert.Equal(["1", "2", "x-c", "x-d", "x-e", "10", "11"], Enumerable.Range(0, 7).Select(map.LabelOf).ToArray());
    }

    [Fact]
    public void PageLabelMap_aLabelOverTheLimit_isRefusedNamingThePage()
    {
        var map = new PageLabelMap();
        // A letter label is (number - 1) / 26 + 1 characters long. The first page's number gives
        // exactly the limit, which the constructor allows, and ten pages on the label is one longer.
        var first = (int)(32_767 * 26L - 5);
        map.Add(0, Range(PdfPageLabelStyle.UpperLetters, null, first));

        Assert.Equal(32_767, map.LabelOf(0).Length);
        var ex = Assert.Throws<InvalidOperationException>(() => map.LabelOf(10));
        Assert.Contains("page 11", ex.Message, StringComparison.Ordinal);
    }

    // ── PageLabelRange ───────────────────────────────────────────────────────

    [Fact]
    public void PageLabelRange_keepsWhatItWasGiven()
    {
        var range = new PageLabelRange(PdfPageLabelStyle.UpperLetters, "App-", 8);

        Assert.Equal(PdfPageLabelStyle.UpperLetters, range.Style);
        Assert.Equal("App-", range.Prefix);
        Assert.Equal(8, range.FirstNumber);
        Assert.Null(new PageLabelRange(PdfPageLabelStyle.Decimal).Prefix);
        Assert.Equal(1, new PageLabelRange(PdfPageLabelStyle.Decimal).FirstNumber);
    }

    [Fact]
    public void PageLabelRange_refusesAnUndefinedStyle_aFirstNumberBelowOne_andAnOverlongLabel()
    {
        var style = Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange((PdfPageLabelStyle)99));
        Assert.Equal("style", style.ParamName);

        foreach (var first in new[] { 0, -1, int.MinValue })
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.Decimal, null, first));
            Assert.Equal("firstNumber", ex.ParamName);
        }

        // 82,595,525 letters: far past the 32,767 characters one label may have.
        var long1 = Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.UpperLetters, null, int.MaxValue));
        Assert.Equal("firstNumber", long1.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.LowerRoman, null, int.MaxValue));
    }

    [Fact]
    public void PageLabelRange_overlongLabelBoundaries_areThoseTheTypeDocumentationStates()
    {
        // Letters: (number - 1) / 26 + 1 characters, so 851,942 is exactly 32,767.
        _ = new PageLabelRange(PdfPageLabelStyle.LowerLetters, null, 851_942);
        Assert.Equal("firstNumber", Assert.Throws<ArgumentOutOfRangeException>(
            () => new PageLabelRange(PdfPageLabelStyle.LowerLetters, null, 851_943)).ParamName);

        // Roman: one M per thousand, then the numeral for the last three digits (DCCCLXXXVIII is 12).
        _ = new PageLabelRange(PdfPageLabelStyle.UpperRoman, null, 32_767_000);
        _ = new PageLabelRange(PdfPageLabelStyle.UpperRoman, null, 32_755_999);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.UpperRoman, null, 32_766_888));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.UpperRoman, null, 32_767_001));

        // A prefix takes one character from the label each: 26 numbers of letters, 1,000 of Roman.
        var prefix = new string('x', 100);
        _ = new PageLabelRange(PdfPageLabelStyle.LowerLetters, prefix, 849_342);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.LowerLetters, prefix, 849_343));
        _ = new PageLabelRange(PdfPageLabelStyle.LowerRoman, prefix, 32_667_000);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PageLabelRange(PdfPageLabelStyle.LowerRoman, prefix, 32_667_001));
    }

    // The constructor checks the first page's label. A later page's label is worked out only for a
    // band that prints {page}, so the same document saves with a footer that does not.
    private static Document TwoPagesFromTheLastLetterLabel(string footerTemplate)
    {
        var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.LowerLetters, null, 851_942);
        doc.Footer = new RunningBand(footerTemplate);
        doc.Add(Tall("one"));
        doc.Add(Tall("two"));
        return doc;
    }

    [Fact]
    public void AFooterWithoutThePageToken_doesNotComputeALabelThatTooLongPageWouldNeed()
    {
        Assert.NotEmpty(Save(TwoPagesFromTheLastLetterLabel("page")));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(TwoPagesFromTheLastLetterLabel("{page}")));
        Assert.Contains("page 2", ex.Message, StringComparison.Ordinal);
        Assert.Contains("32767", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PageLabelRange_prefixLimit_is16382Characters_andNoUnpairedSurrogate()
    {
        _ = new PageLabelRange(PdfPageLabelStyle.Decimal, new string('x', 16_382));
        var tooLong = Assert.Throws<ArgumentException>(() => new PageLabelRange(PdfPageLabelStyle.Decimal, new string('x', 16_383)));
        Assert.Equal("prefix", tooLong.ParamName);

        _ = new PageLabelRange(PdfPageLabelStyle.Decimal, "a\U0001F600b");
        foreach (var bad in new[] { "\uD800", "a\uDC00", "\uD800x", "\uDC00\uD800" })
        {
            var ex = Assert.Throws<ArgumentException>(() => new PageLabelRange(PdfPageLabelStyle.Decimal, bad));
            Assert.Equal("prefix", ex.ParamName);
        }
    }

    // ── Anchors: the destinations ────────────────────────────────────────────

    private static Document FourPagesThen(Action<Document> add)
    {
        var doc = NewDocument();
        for (var i = 1; i <= 4; i++)
            doc.Add(Tall($"filler {i}"));
        add(doc);
        return doc;
    }

    [Fact]
    public void HeadingAnchor_onPage5_isANamedDestinationAtTheTopOfThePage()
    {
        using var doc = FourPagesThen(d => d.Add(new Heading("Installatie") { BreakBefore = PageBreakMode.Page, Anchor = "installatie" }));
        var pdf = Save(doc);

        var dests = Dests(pdf);
        Assert.Equal(["installatie"], dests.Keys);
        Assert.Equal(new Dest(4, 0, TopOfPage), dests["installatie"]);
    }

    [Fact]
    public void ParagraphAnchor_afterAFillerLine_isBelowThatLine()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("filler", Body));
        doc.Add(new Paragraph("target", Body) { Anchor = "target" });

        var dest = Dests(Save(doc))["target"];

        Assert.Equal(0, dest.Page);
        Assert.Equal(TopOfPage - Body.EffectiveLeading, dest.Top, 4);
    }

    [Fact]
    public void ParagraphThatSplits_isAnchoredOnThePageItsFirstLineIsOn()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("filler", Body));
        // 400 words wrap to far more lines than the 260 pt content area holds.
        doc.Add(new Paragraph(string.Join(' ', Enumerable.Range(0, 400).Select(i => $"word{i}")), Body) { Anchor = "long" });
        var pdf = Save(doc);

        var dest = Dests(pdf)["long"];

        using var reader = PdfReader.Open(pdf);
        Assert.True(reader.PageCount >= 2);
        Assert.Equal(0, dest.Page);
        Assert.Equal(TopOfPage - Body.EffectiveLeading, dest.Top, 4);
    }

    [Fact]
    public void HeadingThatSplits_isAnchoredOnThePageItsFirstLineIsOn()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        // What is left of the page after the paragraph holds a few lines of the heading and the
        // rest goes on, so the heading splits.
        var words = string.Join(' ', Enumerable.Range(0, 400).Select(i => $"word{i}"));
        doc.Add(new Heading(words, Body) { Anchor = "split" });
        var pdf = Save(doc);

        var dest = Dests(pdf)["split"];

        using var reader = PdfReader.Open(pdf);
        Assert.True(reader.PageCount >= 2);
        Assert.Equal(0, dest.Page);
        Assert.Equal(TopOfPage - (200 + Body.EffectiveLeading), dest.Top, 4);
    }

    [Fact]
    public void ListAnchors_listItemsAndChildren_landOnTheirOwnFirstLine()
    {
        using var doc = NewDocument();
        var list = new ListElement { Anchor = "list" };
        var second = new ListItem("second", Body) { Anchor = "second" };
        second.AddChild(new ListItem("child", Body) { Anchor = "child" });
        list.Add(new ListItem("first", Body) { Anchor = "first" });
        list.Add(second);
        list.Add(new ListItem("third", Body) { Anchor = "third" });
        doc.Add(list);

        var dests = Dests(Save(doc));

        var line = Body.EffectiveLeading;
        Assert.Equal(["child", "first", "list", "second", "third"], dests.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(TopOfPage, dests["list"].Top, 4);
        Assert.Equal(TopOfPage, dests["first"].Top, 4);
        Assert.Equal(TopOfPage - line, dests["second"].Top, 4);
        Assert.Equal(TopOfPage - 2 * line, dests["child"].Top, 4);
        Assert.Equal(TopOfPage - 3 * line, dests["third"].Top, 4);
        Assert.All(dests.Values, d => Assert.Equal(0, d.Page));
    }

    [Fact]
    public void ListItemOnALaterPage_isAnchoredOnThatPage()
    {
        using var doc = NewDocument();
        // 25.6 pt remain on the page after this paragraph: one line of the list, which is 14.4 pt.
        doc.Add(new Paragraph("a", Body) { Margins = new EdgeInsets(220, 0, 0, 0) });
        var list = new ListElement();
        list.Add(new ListItem("one", Body) { Anchor = "one" });
        list.Add(new ListItem("two", Body) { Anchor = "two" });
        list.Add(new ListItem("three", Body) { Anchor = "three" });
        doc.Add(list);

        var dests = Dests(Save(doc));

        Assert.Equal(0, dests["one"].Page);
        Assert.Equal(1, dests["two"].Page);
        Assert.Equal(TopOfPage, dests["two"].Top, 4);
        Assert.Equal(1, dests["three"].Page);
        Assert.Equal(TopOfPage - Body.EffectiveLeading, dests["three"].Top, 4);
    }

    [Fact]
    public void TableAnchor_isTheTopOfItsFirstRow()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("filler", Body));
        var table = new TableElement { Anchor = "table" };
        table.AddRow().AddCell("a");
        doc.Add(table);

        var dest = Dests(Save(doc))["table"];

        Assert.Equal(0, dest.Page);
        Assert.Equal(TopOfPage - Body.EffectiveLeading, dest.Top, 4);
    }

    [Fact]
    public void TableThatSplits_isAnchoredOnThePageOfItsFirstRow()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        var table = new TableElement { Anchor = "table" };
        for (var i = 0; i < 12; i++)
            table.AddRow().AddCell("row " + i);
        doc.Add(table);
        var pdf = Save(doc);

        var dests = Dests(pdf);

        using var reader = PdfReader.Open(pdf);
        Assert.True(reader.PageCount >= 2);
        Assert.Equal(0, dests["table"].Page);
        Assert.Equal(TopOfPage - (200 + Body.EffectiveLeading), dests["table"].Top, 4);
    }

    [Fact]
    public void TableWhoseFirstRowIsTallerThanAPage_hasOneDestination_atTheTableTop()
    {
        // A 300 x 600 pt page with 10 pt margins has a 580 pt content box, top edge at y = 590.
        // The first row has 100 lines of 12 pt in a 40 pt column, so it is cut between its lines
        // over three pages, and its continuations are overflow renderers that start at row 0.
        static Document Build()
        {
            var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 600), Margins = new EdgeInsets(10) };
            var style = new TextStyle { Font = VellumPdf.Fonts.Standard14.Helvetica, FontSize = 10 };
            var table = new TableElement { DefaultCellStyle = style, Anchor = "tall" }.SetColumnWidths(40, 100);
            var row = new Row();
            row.AddCell(string.Join(' ', Enumerable.Repeat("aaaa", 100))).AddCell("short");
            table.Add(row);
            doc.Add(table);
            return doc;
        }

        // The save registers a name once whatever is reported, so the reports are counted too: a
        // continuation that reported the anchor again would put it in the plan on a later page.
        using (var measured = Build())
        {
            var renderer = measured.CreateRenderer();
            renderer.RunLayout();
            var reported = renderer.DrawPlan!.Placements.Where(p => p.Anchor == "tall").ToList();
            Assert.Equal(0, Assert.Single(reported).PageIndex);
            Assert.Equal(10, reported[0].Y);
        }

        using var doc = Build();
        var pdf = Save(doc);
        using var reader = PdfReader.Open(pdf);
        Assert.True(reader.PageCount >= 3);
        var dests = Dests(pdf);
        Assert.Equal(["tall"], dests.Keys);
        Assert.Equal(new Dest(0, 0, 590), dests["tall"]);
    }

    [Fact]
    public void ImageAnchor_isTheTopOfTheImageInsideItsMargins()
    {
        using var doc = NewDocument();
        var image = PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());
        doc.Add(new LayoutImage(image) { Width = 40, Margins = new EdgeInsets(5), Anchor = "figure" });

        var dest = Dests(Save(doc))["figure"];

        Assert.Equal(0, dest.Page);
        Assert.Equal(TopOfPage - 5, dest.Top, 4);
    }

    [Fact]
    public void Anchors_areWrittenInOrdinalOrder_whateverOrderTheyWereAddedIn()
    {
        string[] names = ["b", "B", "a", "a.1", "a-1", "A:1"];
        using var doc = NewDocument();
        foreach (var name in names)
            doc.Add(new Paragraph(name, Body) { Anchor = name });

        var text = Flat(Save(doc));

        var keys = Regex.Matches(Regex.Match(text, @"/Dests << /Names \[(.*?)\]").Groups[1].Value, @"\(([^)]*)\) \d+ 0 R")
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(names.Order(StringComparer.Ordinal), keys);
    }

    [Fact]
    public void SameDocumentBuiltTwice_writesTheSameDestinationsAndTheSameBytes()
    {
        static byte[] Build(bool insertBefore)
        {
            var doc = new Document
            {
                PageSize = Page,
                Margins = Margins,
                Timestamp = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero),
                DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            };
            using (doc)
            {
                doc.PageLabel = Range(PdfPageLabelStyle.LowerRoman);
                doc.Add(Tall("a"));
                if (insertBefore) doc.Add(Tall("inserted"));
                doc.Add(new Heading("Intro") { Anchor = "intro" });
                return Save(doc);
            }
        }

        var first = Build(false);
        var second = Build(false);
        var third = Build(true);

        Assert.Equal(Dests(first), Dests(second));
        Assert.Equal(SHA256.HashData(first), SHA256.HashData(second));
        Assert.Equal(["intro"], Dests(third).Keys);
        // The heading follows a paragraph that is 200 pt of margin and a line tall, on the first
        // page; the inserted paragraph fills the page it is on, so the heading moves to the next.
        var below = TopOfPage - (200 + Body.EffectiveLeading);
        Assert.Equal(new Dest(0, 0, below), Dests(first)["intro"]);
        Assert.Equal(new Dest(1, 0, below), Dests(third)["intro"]);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void Anchor_refusesANameOutsideTheRule_atTheInit()
    {
        foreach (var bad in new[] { "", "a b", "é", "a/b", "a#b", "a(b", new string('x', 128), "\uD800" })
        {
            var ex = Assert.Throws<ArgumentException>(() => new Paragraph("x", Body) { Anchor = bad });
            Assert.Equal("value", ex.ParamName);
            Assert.Throws<ArgumentException>(() => new Heading("x") { Anchor = bad });
            Assert.Throws<ArgumentException>(() => new TableElement { Anchor = bad });
            Assert.Throws<ArgumentException>(() => new ListElement { Anchor = bad });
            Assert.Throws<ArgumentException>(() => new ListItem("x") { Anchor = bad });
            Assert.Throws<ArgumentException>(
                () => new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Anchor = bad });
        }
    }

    [Fact]
    public void Anchor_acceptsTheWholeRule_andNull()
    {
        var all = "AZaz09._-:" + new string('k', 117);
        Assert.Equal(127, all.Length);
        Assert.Equal(all, new Paragraph("x", Body) { Anchor = all }.Anchor);
        Assert.Null(new Paragraph("x", Body) { Anchor = null }.Anchor);
        Assert.Null(new Paragraph("x", Body).Anchor);
    }

    [Fact]
    public void RepeatedAnchor_isRefusedBeforeLayout_namingBothElements_andTheDocumentIsNotBurned()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("a", Body) { Anchor = "same" });
        var list = new ListElement();
        list.Add(new ListItem("x", Body));
        list.Add(new ListItem("y", Body) { Anchor = "same" });
        doc.Add(list);

        var first = Assert.Throws<InvalidOperationException>(() => Save(doc));
        var second = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("\"same\"", first.Message, StringComparison.Ordinal);
        Assert.Contains("Paragraph 0", first.Message, StringComparison.Ordinal);
        Assert.Contains("List 1 (item 1)", first.Message, StringComparison.Ordinal);
        // Refused before any layout ran: a second save meets the same refusal, not "cannot be saved again".
        Assert.Equal(first.Message, second.Message);
        Assert.DoesNotContain("cannot be saved again", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameElementAddedTwice_holdsItsAnchorTwice()
    {
        using var doc = NewDocument();
        var p = new Paragraph("a", Body) { Anchor = "once" };
        doc.Add(p);
        doc.Add(p);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("Paragraph 0 and Paragraph 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LabelledBreakWithNoElementAfterIt_isRefusedBeforeLayout_andSavesOnceAnElementIsAdded()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        doc.Add(new PageBreak { PageLabel = Range(PdfPageLabelStyle.Decimal) });
        doc.Add(new PageBreak());

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("PageBreak 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("no element follows", ex.Message, StringComparison.Ordinal);

        doc.Add(Tall("b"));

        Assert.Equal("/Nums [0 << /S /D >> 1 << /S /D >>]", Nums(Save(doc)));
    }

    [Fact]
    public void UnlabelledTrailingBreak_isStillDroppedWithoutARefusal()
    {
        using var doc = NewDocument();
        doc.Add(Tall("a"));
        doc.Add(new PageBreak());

        using var reader = PdfReader.Open(Save(doc));
        Assert.Equal(1, reader.PageCount);
    }

    [Fact]
    public void AnchorOnAnItemBelowTheSecondLevel_isRefusedAfterLayout_andTheDocumentCannotBeSavedAgain()
    {
        using var doc = NewDocument();
        var top = new ListItem("top", Body);
        var child = new ListItem("child", Body);
        child.AddChild(new ListItem("grandchild", Body) { Anchor = "deep" });
        top.AddChild(child);
        var list = new ListElement();
        list.Add(top);
        doc.Add(list);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("\"deep\"", ex.Message, StringComparison.Ordinal);
        Assert.Contains("List 0 (item 0, child 0, below the second level)", ex.Message, StringComparison.Ordinal);
        Assert.Contains("cannot be saved again", ex.Message, StringComparison.Ordinal);
        var again = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.StartsWith("This document cannot be saved again", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnchorWhoseTopIsBeyondTheRealRange_isRefusedNamingTop()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("x", Body) { Margins = new EdgeInsets(-1e39, 0, 0, 0), Anchor = "far" });

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => Save(doc));

        Assert.Equal("top", ex.ParamName);
    }

    [Fact]
    public void AnchorOnAParagraphRendererContinuedFromALaterLine_isRefused()
    {
        var pdf = new PdfDocument();
        var words = string.Join(' ', Enumerable.Range(0, 100).Select(i => $"word{i}"));
        var renderer = new DocumentRenderer(pdf, Page, Margins)
            .Add(new ParagraphRenderer(new Paragraph(words, Body) { Anchor = "later" }, startLine: 1));

        var ex = Assert.Throws<InvalidOperationException>(() => renderer.Render(new MemoryStream()));
        Assert.Contains("\"later\"", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListItemTreeThatLoopsBack_isWalkedOnce_andItsDeepAnchorIsRefused()
    {
        using var doc = NewDocument();
        var a = new ListItem("a", Body);
        var b = new ListItem("b", Body);
        var c = new ListItem("c", Body) { Anchor = "cycle" };
        a.AddChild(b);
        b.AddChild(c);
        c.AddChild(b);
        var list = new ListElement();
        list.Add(a);
        doc.Add(list);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("never placed", ex.Message, StringComparison.Ordinal);
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    [Fact]
    public void RomanThenDecimal_qpdfAgreesOnEveryPagesLabel_andChecksClean()
    {
        using var file = new TempPdf(Save(RomanThenDecimal()));

        AssertQpdfClean(file.Path);
        var labels = QpdfPageLabels(file.Path);

        Assert.Equal(
            [
                (1, "/r", 1), (2, "/r", 2), (3, "/r", 3), (4, "/r", 4),
                (5, "/D", 1), (6, "/D", 2), (7, "/D", 3), (8, "/D", 4),
            ],
            labels);
        using var json = JsonDocument.Parse(Qpdf("--json", "--json-key=pagelabels", file.Path));
        Assert.Equal(2, json.RootElement.GetProperty("pagelabels").GetArrayLength());
    }

    [Fact]
    public void RomanThenDecimal_pdftotextReadsTheLabelsOffEachPage()
    {
        using var file = new TempPdf(Save(RomanThenDecimal()));
        var pages = new List<string>();
        for (var page = 1; page <= 8; page++)
        {
            ExternalTool.TryRun("pdftotext", ["-layout", "-f", page.ToString(CultureInfo.InvariantCulture), "-l", page.ToString(CultureInfo.InvariantCulture), file.Path, "-"],
                out var exit, out var text, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"pdftotext page {page} exit {exit}\n{stderr}");
            var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            pages.Add(words.Length == 0 ? "" : words[^1]);
        }

        Assert.Equal(["i", "ii", "iii", "", "1", "2", "3", "4"], pages);
    }

    [Fact]
    public void HeadingAnchor_pdfinfoListsItOnPage5_atTheTopWeComputed()
    {
        using var doc = FourPagesThen(d => d.Add(new Heading("Installatie") { BreakBefore = PageBreakMode.Page, Anchor = "installatie" }));
        using var file = new TempPdf(Save(doc));

        AssertQpdfClean(file.Path);
        var dests = PdfinfoDests(file.Path);

        var only = Assert.Single(dests);
        Assert.Equal(5, only.Page);
        Assert.Equal(TopOfPage, only.Top, 4);
        Assert.Equal("installatie", only.Name);
    }

    [Fact]
    public void ReorderedPages_theDestinationFollowsItsPage_andLabelsFollowToo()
    {
        using var doc = NewDocument();
        doc.PageLabel = Range(PdfPageLabelStyle.LowerRoman);
        for (var i = 1; i <= 4; i++)
            doc.Add(Tall($"filler {i}"));
        doc.Add(new Heading("Installatie") { BreakBefore = PageBreakMode.Page, Anchor = "installatie" });
        doc.Add(Tall("six"));
        doc.Add(Tall("seven"));
        doc.Add(Tall("eight"));
        using var input = new TempPdf(Save(doc));
        var output = input.Path + ".reordered.pdf";
        try
        {
            Assert.Equal(5, Assert.Single(PdfinfoDests(input.Path)).Page);

            // Page 6 moves to the front, so the heading's page 5 becomes page 6. The heading shares
            // page 5 with "six"; "seven" is page 6 and "eight" page 7.
            Qpdf(input.Path, "--pages", ".", "6,1-5,7", "--", output);

            Assert.Equal(6, Assert.Single(PdfinfoDests(output)).Page);
            AssertQpdfClean(output);
        }
        finally
        {
            File.Delete(output);
        }
    }

    [Fact]
    public void TenThousandAnchoredParagraphs_pdfinfoListsTenThousandDestinations()
    {
        using var doc = new Document();
        for (var i = 0; i < 10_000; i++)
            doc.Add(new Paragraph($"p{i}", Body) { Anchor = $"a{i:D5}" });
        using var file = new TempPdf(Save(doc));

        Assert.Equal(10_000, PdfinfoDests(file.Path).Count);
    }

    [Fact]
    public void AllocationPerAnchoredParagraph_growsByLessThanOneAndAHalf_from1000To10000()
    {
        static double PerAnchor(int count)
        {
            using var doc = new Document();
            for (var i = 0; i < count; i++)
                doc.Add(new Paragraph($"p{i}", Body) { Anchor = $"a{i:D5}" });
            using var sink = new MemoryStream();
            var before = GC.GetAllocatedBytesForCurrentThread();
            doc.Save(sink);
            return (double)(GC.GetAllocatedBytesForCurrentThread() - before) / count;
        }

        // Warm the JIT so its allocations are not measured.
        _ = PerAnchor(200);
        var small = PerAnchor(1_000);
        var large = PerAnchor(10_000);

        Assert.True(large <= small * 1.5, $"{small:F0} bytes per anchor at 1,000, {large:F0} at 10,000");
    }

    [Fact]
    public void DocumentsWithLabelsAndAnchors_inPdfA2u_passVeraPdf()
    {
        var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        using (doc)
        {
            var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 11 };
            doc.PageLabel = Range(PdfPageLabelStyle.LowerRoman);
            doc.Footer = new RunningBand("{page}", style);
            doc.Add(new Paragraph("Front matter", style) { Anchor = "front" });
            doc.Add(new PageBreak { Recto = true, PageLabel = Range(PdfPageLabelStyle.Decimal, "A-", 8) });
            doc.Add(new Heading([new TextRun("Chapter", style)]) { Anchor = "installatie" });
            doc.Add(new Paragraph("Body text", style));
            var pdf = FontReportSupport.Save(doc);

            Assert.Equal("/Nums [0 << /S /r >> 2 << /S /D /P " + Text("A-") + " /St 8 >>]", Nums(pdf));
            Assert.Equal(["front", "installatie"], Dests(pdf).Keys.Order(StringComparer.Ordinal));
            Assert.Empty(FontReportSupport.FailedRules(FontReportSupport.VeraPdf("2u", pdf)));
        }
    }

    // ── Reader round trip ────────────────────────────────────────────────────

    [Fact]
    public void ReaderReadsBackThePageLabelsTreeAndTheNameTree()
    {
        using var doc = RomanThenDecimal();
        doc.Add(new Paragraph("end", Body) { Anchor = "end" });
        doc.Add(new Paragraph("start", Body) { Anchor = "start" });
        using var reader = PdfReader.Open(Save(doc));

        var labels = Assert.IsType<PdfDictionary>(reader.Catalog.Get(new PdfName("PageLabels")));
        var nums = Assert.IsType<PdfArray>(labels.Get(new PdfName("Nums")));
        Assert.Equal(4, nums.Count);
        Assert.Equal(0, ((PdfInteger)nums[0]).Value);
        Assert.Equal("r", ((PdfName)Assert.IsType<PdfDictionary>(nums[1]).Get(new PdfName("S"))!).Value);
        Assert.Equal(4, ((PdfInteger)nums[2]).Value);
        Assert.Equal("D", ((PdfName)Assert.IsType<PdfDictionary>(nums[3]).Get(new PdfName("S"))!).Value);

        var names = Assert.IsType<PdfDictionary>(reader.Catalog.Get(new PdfName("Names")));
        var dests = Assert.IsType<PdfDictionary>(names.Get(new PdfName("Dests")));
        var pairs = Assert.IsType<PdfArray>(dests.Get(new PdfName("Names")));
        Assert.Equal(4, pairs.Count);
        Assert.Equal("end", Encoding.Latin1.GetString(Assert.IsType<PdfLiteralString>(pairs[0]).Bytes.Span));
        Assert.Equal("start", Encoding.Latin1.GetString(Assert.IsType<PdfLiteralString>(pairs[2]).Bytes.Span));
        Assert.IsType<PdfIndirectReference>(pairs[1]);
    }
}
