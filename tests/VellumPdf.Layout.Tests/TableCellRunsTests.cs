// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
/// A table cell built from runs (#692). The fixtures with a short page are 300x400pt with 10pt
/// margins and two 100pt columns in Helvetica 12pt: a line is 14.4pt, the default padding is 4pt
/// above and below and 6pt left and right, so a cell's text starts at x = 16 and its first
/// baseline is at y = 400 - 10 - 4 - 12 = 374.
/// </summary>
public sealed class TableCellRunsTests
{
    private static readonly FontReference Helv = new(Standard14.Helvetica);
    private static readonly FontReference HelvBold = new(Standard14.HelveticaBold);

    private static TextStyle Plain(double size = 12) => new() { FontRef = Helv, FontSize = size };

    private static TextStyle Bold(double size = 12) => new() { FontRef = HelvBold, FontSize = size };

    private static Document ShortPage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(10),
    };

    private static TableElement Table12(params double[] widths) =>
        new TableElement { DefaultCellStyle = Plain() }.SetColumnWidths(widths.Length == 0 ? [100, 100] : widths);

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Content(byte[] pdf) => PdfTestUtil.DecompressAllFlatStreams(pdf);

    private static List<ContentStreamReadback.TextPlacement> Placements(string content) =>
        [.. ContentStreamReadback.TextPlacements(content).Where(p => p.Text.Length > 0)];

    private static string[] Texts(string content) => [.. Placements(content).Select(p => p.Text)];

    private static double Width(FontReference font, string text, double size = 12) => font.MeasureString(text, size);

    private static TextRun Run(string text, TextStyle style) => new(text, style);

    private static string DrawnCell(Cell cell, params double[] widths)
    {
        using var doc = ShortPage();
        var t = Table12(widths);
        t.AddRow().AddCell(cell).AddCell("x");
        doc.Add(t);
        return Content(Save(doc));
    }

    private static readonly Regex RectOf = new(
        @"/Rect \[(?<x0>-?[\d.]+) (?<y0>-?[\d.]+) (?<x1>-?[\d.]+) (?<y1>-?[\d.]+)\]", RegexOptions.CultureInvariant);

    private static List<(string Uri, double X0, double Y0, double X1, double Y1)> Links(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var found = new List<(string, double, double, double, double)>();
        foreach (var obj in Regex.Split(text, @"\d+ 0 obj"))
        {
            if (!obj.Contains("/Subtype /Link", StringComparison.Ordinal)) continue;
            var rect = RectOf.Match(obj);
            var uri = Regex.Match(obj, @"/URI \((?<u>[^)]*)\)");
            if (!rect.Success || !uri.Success) continue;
            double D(string g) => double.Parse(rect.Groups[g].Value, CultureInfo.InvariantCulture);
            found.Add((uri.Groups["u"].Value, D("x0"), D("y0"), D("x1"), D("y1")));
        }
        return found;
    }

    // ── The constructor ──────────────────────────────────────────────────────

    [Fact]
    public void Constructor_refusesANullSequence_withTheParameterName()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new Cell((IEnumerable<TextRun>)null!));
        Assert.Equal("runs", ex.ParamName);
    }

    [Fact]
    public void Constructor_refusesANullRun_namingItsIndex()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new Cell(new TextRun?[] { Run("a", Plain()), null }!));
        Assert.Equal("runs", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("")]
    public void Constructor_refusesARunWithANullStyle_whateverItsText(string text)
    {
        var ex = Assert.Throws<ArgumentException>(() => new Cell([Run("a", Plain()), new TextRun(text, null!)]));
        Assert.IsNotType<ArgumentNullException>(ex);
        Assert.Equal("runs", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_anEmptySequenceIsOneEmptyRunInTheDefaultStyle()
    {
        var cell = new Cell([]);

        Assert.Equal("", cell.Content);
        var run = Assert.Single(cell.Runs!);
        Assert.Equal("", run.Text);
        Assert.Same(TextStyle.Default, run.Style);
    }

    [Fact]
    public void Constructor_copiesTheSequence_andJoinsTheText()
    {
        var runs = new List<TextRun> { Run("ab", Plain()), Run("cd", Bold()) };
        var cell = new Cell(runs);
        runs.Add(Run("ef", Plain()));

        Assert.Equal("abcd", cell.Content);
        Assert.Equal(2, cell.Runs!.Count);
    }

    [Fact]
    public void ANullLiteral_stillBindsToTheStringConstructor()
    {
        var cell = new Cell(null!);

        Assert.Null(cell.Content);
        Assert.Null(cell.Runs);
    }

    [Fact]
    public void AStringCell_hasNoRuns() => Assert.Null(new Cell("a").Runs);

    // ── Against the string cell ──────────────────────────────────────────────

    /// <summary>
    /// A cell of one run draws the bytes of the string cell of the same text in the same style:
    /// the wrap takes the same steps, and the first font, colour, text matrix and show operator are
    /// the ones the string cell writes.
    /// </summary>
    [Theory]
    [InlineData("plain text")]
    [InlineData("a longer line of words that has to wrap in a hundred point column")]
    [InlineData("line one\nline two\r\nline three\rfour\ttab")]
    [InlineData("  leading and  double   spaces ")]
    [InlineData("a\n")]
    [InlineData("")]
    [InlineData("Wwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwwww")]
    public void OneRun_drawsTheBytesOfTheStringCell(string text)
    {
        var style = Plain();

        Assert.Equal(DrawnCell(new Cell(text) { Style = style }), DrawnCell(new Cell([Run(text, style)])));
    }

    /// <summary>
    /// Runs that share one style instance are one piece of text, so splitting the text anywhere,
    /// inside a word included, writes the bytes of the string cell. Two instances with equal
    /// values are two pieces: the text read back is the same and the show operators are not.
    /// </summary>
    [Fact]
    public void RunsSharingAStyleInstance_drawTheBytesOfTheStringCell_andTwoInstancesDoNot()
    {
        var style = Plain();
        var text = "astrologie is a word and so is cosmology here";
        var shared = DrawnCell(new Cell([Run("astro", style), Run("logie is a word and so is", style), Run(" cosmology here", style)]));

        Assert.Equal(DrawnCell(new Cell(text) { Style = style }), shared);

        var twin = DrawnCell(new Cell([Run("astro", style), Run("logie is a word and so is", Plain()), Run(" cosmology here", style)]));
        Assert.NotEqual(shared, twin);
        Assert.Equal(string.Concat(Texts(shared)), string.Concat(Texts(twin)));
        Assert.True(Texts(twin).Length > Texts(shared).Length);
    }

    /// <summary>
    /// For every width, a run cell of one style wraps into the lines of the string cell. The two
    /// walks are separate code, so this compares them over a sweep of widths and texts.
    /// </summary>
    [Fact]
    public void OneRun_wrapsAsTheStringCellDoes_overAGridOfWidthsAndTexts()
    {
        var style = Plain();
        string[] texts =
        [
            "the quick brown fox jumps over the lazy dog",
            "  two  spaces   and   three ",
            "supercalifragilisticexpialidocious word",
            "a\n\nb\r\nc",
            "WWWW iiii WWWW",
            "ends with a space ",
            " ",
            "",
        ];
        foreach (var text in texts)
        {
            foreach (var honour in new[] { true, false })
            {
                var run = new Cell([Run(text, style)]);
                for (var width = 1.0; width < 260; width += 3.7)
                {
                    var expected = TableRenderer.WordWrapLines(text, style, width, honour);
                    Assert.Equal(expected, run.RunText!.Lines(width, honour));
                    Assert.Equal(expected.Count, run.RunText.CountLines(width, honour));

                    // The widths the count holds over are the string wrap's own.
                    var runCount = run.RunText.CountLines(width, honour, out var runFrom, out var runBelow);
                    var stringCount = TableRenderer.CountLines(text, style, width, honour, out var stringFrom, out var stringBelow);
                    Assert.Equal((stringCount, stringFrom, stringBelow), (runCount, runFrom, runBelow));
                }

                var (longest, widest) = run.RunText!.Measure(honour);
                var (expectedLongest, expectedWidest) = TableGridResolver.MeasureCell(text, style, honour);
                Assert.Equal(expectedLongest, longest);
                Assert.Equal(expectedWidest, widest);
            }
        }
    }

    // ── Mixed styles ─────────────────────────────────────────────────────────

    [Fact]
    public void MixedStyles_areShownInOrder_eachAtTheEndOfTheOneBefore()
    {
        var red = new TextStyle { FontRef = Helv, FontSize = 12, Color = new ColorRgb(1, 0, 0) };
        var content = DrawnCell(new Cell([Run("Hello ", Plain()), Run("bold", Bold()), Run(" red", red)]));

        var shown = Placements(content);
        Assert.Equal(["Hello ", "bold", " red", "x"], shown.Select(p => p.Text));
        Assert.Equal(16.0, shown[0].X, 4);
        Assert.Equal(16.0 + Width(Helv, "Hello "), shown[1].X, 4);
        Assert.Equal(16.0 + Width(Helv, "Hello ") + Width(HelvBold, "bold"), shown[2].X, 4);
        Assert.All(shown.Take(3), p => Assert.Equal(374.0, p.Y, 4));

        // The bold face is selected for its run, and the colour for the last one.
        Assert.Equal(2, Regex.Matches(content, @"(?m)^/\S+ 12 Tf$").Select(m => m.Value).Distinct().Count());
        Assert.Contains("1 0 0 rg", content, StringComparison.Ordinal);
        Assert.True(content.IndexOf("1 0 0 rg", StringComparison.Ordinal) > content.IndexOf("(bold) Tj", StringComparison.Ordinal));
    }

    [Fact]
    public void MixedStyles_aWordIsMeasuredInPieces_soARunBoundaryInsideItDrawsNoSpace()
    {
        // "astro" + "logie" is one word of 10 + 5 = ... measured in each run's own face.
        var runs = new[] { Run("see astro", Plain()), Run("logie now", Bold()) };
        var whole = Width(Helv, "see astro") + Width(HelvBold, "logie");

        // A column that holds "see astrologie" but not "see astrologie now".
        var fit = whole + 12 + 1;
        var content = DrawnCell(new Cell(runs), fit, 100);
        var shown = Placements(content);

        Assert.Equal(["see astro", "logie", "now", "x"], shown.Select(p => p.Text));
        Assert.Equal(shown[0].X + Width(Helv, "see astro"), shown[1].X, 4);
        Assert.Equal(shown[0].Y, shown[1].Y, 4);
        Assert.Equal(shown[0].Y - 14.4, shown[2].Y, 4);

        // Narrower, so that the word cannot sit after "see ": it moves whole to the next line, and
        // the two runs of it stay together.
        var narrow = DrawnCell(new Cell(runs), Width(Helv, "astro") + Width(HelvBold, "logie") + 12 + 1, 100);
        var moved = Placements(narrow);
        Assert.Equal(["see", "astro", "logie", "now", "x"], moved.Select(p => p.Text));
    }

    /// <summary>
    /// A space belongs to the run that holds it, and is measured in that run's style: the same
    /// words wrap at a different width when the spaces are in the wide face.
    /// </summary>
    [Fact]
    public void ASpaceIsMeasuredInTheStyleOfTheRunThatHoldsIt()
    {
        var big = Plain(24);
        var small = Plain(12);
        var cell = new Cell([Run("ab", small), Run(" ", big), Run("cd", small)]);
        var one = Width(Helv, "ab") + Width(Helv, " ", 24) + Width(Helv, "cd");

        var fits = Placements(DrawnCell(cell, one + 12 + 0.5, 100));
        var wraps = Placements(DrawnCell(cell, one + 12 - 0.5, 100));

        Assert.Equal(["ab", " ", "cd", "x"], fits.Select(p => p.Text));
        Assert.Equal(fits[0].Y, fits[2].Y, 4);
        Assert.Equal(["ab", "cd", "x"], wraps.Select(p => p.Text));
        Assert.True(wraps[0].Y > wraps[1].Y);
    }

    /// <summary>
    /// Every line is as tall as the tallest leading among the runs that hold text, and the first
    /// baseline lies one largest size below the top.
    /// </summary>
    [Fact]
    public void ALineIsAsTallAsTheTallestRun_andTheBaselineFollowsTheLargestSize()
    {
        var content = DrawnCell(new Cell([Run("small ", Plain(10)), Run("large\nnext", Plain(20))]));
        var shown = Placements(content);

        Assert.Equal(["small ", "large", "next", "x"], shown.Select(p => p.Text));
        Assert.Equal(400 - 10 - 4 - 20.0, shown[0].Y, 4);
        Assert.Equal(shown[0].Y - 24, shown[2].Y, 4);

        // The row is two 24pt lines and the 8pt of padding tall.
        var borders = Regex.Matches(content, @"(?<r>\S+ \S+ \S+ \S+) re\s+S\b").Select(m => m.Groups["r"].Value.Split(' ')[3]).First();
        Assert.Equal(56.0, double.Parse(borders, CultureInfo.InvariantCulture), 4);
    }

    /// <summary>
    /// A line that holds only the small run is as tall as the others and its baseline is one line
    /// height below the one before: 4pt of padding, a 30pt first baseline, then 36pt steps.
    /// </summary>
    [Fact]
    public void ALaterLineOfSmallText_sitsOneLineHeightBelowTheLineBefore()
    {
        var shown = Placements(DrawnCell(new Cell([Run("big\n", Plain(30)), Run("small", Plain(10))])));

        Assert.Equal([356.0, 320.0], shown.Take(2).Select(p => Math.Round(p.Y, 4)));
    }

    [Fact]
    public void ARunWithNoText_doesNotSetTheHeightOrTheBaseline()
    {
        var shown = Placements(DrawnCell(new Cell([Run("ab", Plain(12)), Run("", Plain(40))])));

        Assert.Equal(374.0, shown[0].Y, 4);
    }

    // ── Line ends ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ab\r\ncd", "", "ab", "cd")]
    [InlineData("ab\r", "\ncd", "ab", "cd")]
    [InlineData("ab\n", "\ncd", "ab", "", "cd")]
    [InlineData("ab", "\rcd", "ab", "cd")]
    [InlineData("ab\r\n", "\r\ncd", "ab", "", "cd")]
    public void LineEnds_areReadOverTheJoinedText_aPairAcrossARunBoundaryIsOne(string first, string second, params string[] lines)
    {
        var content = DrawnCell(new Cell([Run(first, Plain()), Run(second, Bold())]));
        var shown = Placements(content);

        Assert.Equal([.. lines.Where(l => l.Length > 0), "x"], shown.Select(p => p.Text));

        // A blank line is one leading tall, so the baselines differ by lines.Length - 1.
        var baselines = shown.Take(shown.Count - 1).Select(p => Math.Round(p.Y, 4)).Distinct().ToList();
        Assert.Equal(lines.Where(l => l.Length > 0).Count(), baselines.Count);
        Assert.Equal(374.0 - (lines.Length - 1) * 14.4, baselines.Min(), 4);
    }

    [Fact]
    public void OtherWhiteSpaceControlCharacters_areOneSpace_acrossRuns()
    {
        var content = DrawnCell(new Cell([Run("a\t", Plain()), Run("\fb\v", Bold()), Run("\u0085c\u2028d", Plain())]));

        Assert.Equal(["a ", " b ", " c d", "x"], Texts(content));
        Assert.DoesNotContain('\t', content);
        Assert.DoesNotContain('\f', content);
    }

    /// <summary>
    /// The line is placed by the width of all its fragments: centre and right alignment start the
    /// first fragment at the offset of the whole line, not of the first fragment.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void AlignmentOfAMixedStyleLine_usesTheWidthOfTheWholeLine(HorizontalAlignment alignment)
    {
        var shown = Placements(DrawnCell(new Cell([Run("ab ", Plain()), Run("cd", Bold(20))]) { Alignment = alignment }));

        var total = Width(Helv, "ab ") + Width(HelvBold, "cd", 20);
        var offset = alignment == HorizontalAlignment.Center ? (88 - total) / 2 : 88 - total;
        Assert.Equal(16.0 + offset, shown[0].X, 4);
        Assert.Equal(16.0 + offset + Width(Helv, "ab "), shown[1].X, 4);
    }

    // ── Links ────────────────────────────────────────────────────────────────

    [Fact]
    public void ALinkedRun_getsAnAnnotationOverItsOwnText()
    {
        using var doc = ShortPage();
        var t = Table12(200, 60);
        var a = new TextStyle { FontRef = Helv, FontSize = 12, LinkUri = "https://a.example/" };
        var b = new TextStyle { FontRef = HelvBold, FontSize = 12, LinkUri = "https://b.example/" };
        t.AddRow().AddCell(new Cell([Run("one ", Plain()), Run("two", a), Run(" and ", Plain()), Run("three", b)])).AddCell("x");
        doc.Add(t);

        var links = Links(Save(doc));

        Assert.Equal(2, links.Count);
        var two = links.Single(l => l.Uri == "https://a.example/");
        var three = links.Single(l => l.Uri == "https://b.example/");
        var x2 = 16.0 + Width(Helv, "one ");
        var x3 = x2 + Width(Helv, "two") + Width(Helv, " and ");

        // The rectangle is the run's text wide and one leading tall, from the top of the line.
        Assert.Equal(x2, two.X0, 3);
        Assert.Equal(x2 + Width(Helv, "two"), two.X1, 3);
        Assert.Equal(400 - 14 - 14.4, two.Y0, 3);
        Assert.Equal(400 - 14.0, two.Y1, 3);
        Assert.Equal(x3, three.X0, 3);
        Assert.Equal(x3 + Width(HelvBold, "three"), three.X1, 3);
    }

    /// <summary>
    /// A run that wraps over two lines is linked on each, over the text the run has on that line,
    /// and a link over the whole cell is not drawn.
    /// </summary>
    [Fact]
    public void ALinkedRunThatWraps_getsOneAnnotationPerLine_notOneOverTheCell()
    {
        using var doc = ShortPage();
        var t = Table12(60, 100);
        var link = new TextStyle { FontRef = Helv, FontSize = 12, LinkUri = "https://c.example/" };
        t.AddRow().AddCell(new Cell([Run("zz ", Plain()), Run("linked words", link), Run(" tail", Plain())])).AddCell("x");
        doc.Add(t);

        var pdf = Save(doc);
        var shown = Placements(Content(pdf));
        var links = Links(pdf);

        var linked = shown.Where(p => p.Text is "linked" or "words").ToList();
        Assert.Equal(2, linked.Count);
        Assert.Equal(2, links.Count);
        foreach (var (placement, link2) in linked.OrderByDescending(p => p.Y).Zip(links.OrderByDescending(l => l.Y1)))
        {
            Assert.Equal(placement.X, link2.X0, 3);
            Assert.Equal(placement.X + Width(Helv, placement.Text), link2.X1, 3);
        }
        Assert.True(links.All(l => l.Y1 - l.Y0 < 15));
    }

    /// <summary>
    /// The rectangle is cut to the cell: a glyph wider than the column, which no wrap can narrow,
    /// is linked over the column and not past it.
    /// </summary>
    [Fact]
    public void ALinkOverAGlyphWiderThanTheColumn_isCutToTheCell()
    {
        var link = new TextStyle { FontRef = Helv, FontSize = 12, LinkUri = "https://d.example/" };

        // A column narrower than the glyph: 8pt against the 11.3pt "W".
        using var doc2 = ShortPage();
        var narrow = Table12(8, 100);
        narrow.AddRow().AddCell(new Cell([Run("W", link)]) { Padding = new EdgeInsets(0) }).AddCell("x");
        doc2.Add(narrow);

        var l = Assert.Single(Links(Save(doc2)));
        Assert.Equal(10.0, l.X0, 3);
        Assert.Equal(18.0, l.X1, 3);
    }

    /// <summary>
    /// A linked run that draws no glyph gets no annotation, and neither does a cell style's link.
    /// </summary>
    [Fact]
    public void NoAnnotation_forAnEmptyLinkedRun_andTheStringCellBesideItIsLinkedByItsOwnStyle()
    {
        using var doc = ShortPage();
        var t = Table12();
        var link = new TextStyle { FontRef = Helv, FontSize = 12, LinkUri = "https://e.example/" };
        t.AddRow().AddCell(new Cell([Run("a", Plain()), Run("", link)])).AddCell(new Cell("b") { Style = link });
        doc.Add(t);

        // The run cell has no linked text, so its one link is the string cell's own (#475).
        var links = Links(Save(doc));
        Assert.Single(links);
    }

    /// <summary>A run that measures zero wide has no area to link and gets no annotation.</summary>
    [Fact]
    public void ALinkedRunThatMeasuresZeroWide_getsNoAnnotation_andSaves()
    {
        using var doc = ShortPage();
        var t = Table12();
        var symbol = new TextStyle { FontRef = new FontReference(Standard14.Symbol), FontSize = 12, LinkUri = "https://g.example/" };
        t.AddRow().AddCell(new Cell([Run("abc", symbol)])).AddCell("b");
        doc.Add(t);

        Assert.Empty(Links(Save(doc)));
    }

    /// <summary>
    /// A style with a link is the one case where a cell of runs sharing a style instance is not the
    /// string cell of its text: the run cell links the text and the string cell drops the link.
    /// </summary>
    [Fact]
    public void ASharedStyleWithALink_linksTheRunCell_andTheStringCell()
    {
        var link = new TextStyle { FontRef = Helv, FontSize = 12, LinkUri = "https://h.example/" };
        byte[] Build(Cell cell)
        {
            using var doc = ShortPage();
            var t = Table12();
            t.AddRow().AddCell(cell);
            doc.Add(t);
            return Save(doc);
        }

        var run = Build(new Cell([Run("ab ", link), Run("cd", link)]));
        var text = Build(new Cell("ab cd") { Style = link });

        Assert.Single(Links(run));
        Assert.Single(Links(text));
        Assert.Equal(Content(text), Content(run));
    }

    [Fact]
    public void AStringCell_isLinkedThroughItsStyle()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell(new Cell("b") { Style = new TextStyle { FontRef = Helv, FontSize = 12, LinkUri = "https://f.example/" } });
        doc.Add(t);

        Assert.Single(Links(Save(doc)));
    }

    // ── Rows, headers, spans, pages ──────────────────────────────────────────

    private static Document SixtyPage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 100),
        Margins = new EdgeInsets(10),
    };

    private static string Lorem(int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => "w" + i));

    /// <summary>
    /// The pages a table takes and where each text lands do not depend on whether its cells are
    /// strings or one run of the same style: a header row repeats, a tall row is cut between lines.
    /// </summary>
    [Fact]
    public void HeaderRowsRepeat_andARowIsCutBetweenLines_asWithStringCells()
    {
        byte[] Build(bool runs)
        {
            using var doc = SixtyPage();
            var t = Table12();
            Cell Make(string s) => runs ? new Cell([Run(s, Plain())]) : new Cell(s);
            t.AddHeaderRow().AddCell(Make("head")).AddCell(Make("h2"));
            t.AddRow().AddCell(Make(Lorem(60))).AddCell(Make("b"));
            t.AddRow().AddCell(Make("last")).AddCell(Make("z"));
            doc.Add(t);
            return Save(doc);
        }

        var strings = Build(false);
        var runs = Build(true);

        Assert.Equal(Content(strings), Content(runs));
        var texts = Texts(Content(runs));
        Assert.True(texts.Count(s => s == "head") > 1, "the header repeats");
        Assert.True(Regex.Matches(Encoding.Latin1.GetString(runs), @"/Type\s*/Page\b").Count > 2);
    }

    [Fact]
    public void ARowOfMixedRunCells_isCutBetweenLines_andEveryLineIsDrawnOnce()
    {
        using var doc = SixtyPage();
        var t = Table12();
        var runs = Enumerable.Range(0, 40).Select(i => Run($"w{i} ", i % 2 == 0 ? Plain() : Bold())).ToArray();
        t.AddRow().AddCell(new Cell(runs)).AddCell("b");
        doc.Add(t);

        var drawn = string.Concat(Texts(Content(Save(doc))));
        var expected = string.Concat(runs.Select(r => r.Text));

        Assert.Equal(expected.Replace(" ", ""), drawn.Replace(" ", "").Replace("b", ""));
    }

    [Fact]
    public void ASpan_givesARunCellTheWidthOfItsColumns()
    {
        using var doc = ShortPage();
        var t = Table12();
        var text = "aaa bbb ccc ddd eee fff ggg hhh iii jjj kkk";
        t.AddRow().AddCell(new Cell([Run(text, Plain())]) { ColSpan = 2 });
        t.AddRow().AddCell(new Cell([Run(text, Plain())])).AddCell("y");
        doc.Add(t);

        var content = Content(Save(doc));
        var fromStrings = ShortPage();
        var ts = Table12();
        ts.AddRow().AddCell(new Cell(text) { ColSpan = 2 });
        ts.AddRow().AddCell(text).AddCell("y");
        fromStrings.Add(ts);

        Assert.Equal(Content(Save(fromStrings)), content);
        var borders = Regex.Matches(content, @"(?<r>\S+ \S+ \S+ \S+) re\s+S\b").Select(m => m.Groups["r"].Value.Split(' ')[2]).First();
        Assert.Equal(200.0, double.Parse(borders, CultureInfo.InvariantCulture), 4);
    }

    // ── Refusals at the save ─────────────────────────────────────────────────

    [Fact]
    public void ANonFiniteFontSize_inARunThatHoldsText_isRefusedNamingTheCell()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell("a").AddCell(new Cell([Run("ok", Plain()), Run("bad", Plain(double.NaN))]));
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("Table row 0, cell 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonFiniteFontSize_inARunThatHoldsNoText_isNotRefused()
    {
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell(new Cell([Run("ok", Plain()), Run("", Plain(double.NaN))])).AddCell("b");
        doc.Add(t);

        Assert.Equal(["ok", "b"], Texts(Content(Save(doc))));
    }

    [Theory]
    [InlineData(double.NaN, 0, 0, "red")]
    [InlineData(0, double.PositiveInfinity, 0, "green")]
    [InlineData(0, 0, -0.5, "blue")]
    [InlineData(1.0000001, 0, 0, "red")]
    public void ARunThatHoldsText_withAColourOutsideZeroToOne_isRefusedByTheConstructor(
        double r, double g, double b, string channel)
    {
        var bad = new TextStyle { FontRef = Helv, FontSize = 12, Color = new ColorRgb(r, g, b) };

        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new Cell([Run("ok", Plain()), Run("bad", bad)]));

        Assert.Equal("runs", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
        Assert.Contains(channel, ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The colour is read from every run that holds text, drawn or not: a run of a line end alone
    /// is refused wherever it sits, and a run with no text is not read.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ARunOfALineEnd_isRefusedForItsColour_whereverItSits(int position)
    {
        var bad = new TextStyle { FontRef = Helv, FontSize = 12, Color = new ColorRgb(double.NaN, 0, 0) };
        TextRun[] runs = position == 0
            ? [Run("\n", bad), Run("abc", Plain())]
            : [Run("abc", Plain()), Run("\n", bad)];

        Assert.Throws<ArgumentOutOfRangeException>(() => new Cell(runs));
        _ = new Cell([Run("abc", Plain()), Run("", bad)]);
    }

    [Fact]
    public void ACellWithNoText_readsNoRunStyle_andSaves()
    {
        var bad = new TextStyle { FontRef = Helv, FontSize = double.NaN, Color = new ColorRgb(double.NaN, 0, 0) };
        using var doc = ShortPage();
        var t = Table12();
        t.AddRow().AddCell(new Cell([Run("", bad)])).AddCell("b");
        doc.Add(t);

        Assert.Equal(["b"], Texts(Content(Save(doc))));
    }

    /// <summary>
    /// A cell with no text is the string cell of the empty string: it takes the cell's style, then
    /// the table's default, for its font, colour and height.
    /// </summary>
    [Fact]
    public void ACellWithNoText_takesTheCellStyleThenTheTableDefault()
    {
        double Height(Cell cell)
        {
            using var doc = ShortPage();
            var t = Table12();
            t.AddRow().AddCell(cell);
            doc.Add(t);
            return double.Parse(Regex.Match(Content(Save(doc)), @"\S+ \S+ \S+ (?<h>\S+) re\s+S\b").Groups["h"].Value,
                CultureInfo.InvariantCulture);
        }

        Assert.Equal(Height(new Cell("")), Height(new Cell([])), 4);
        Assert.Equal(Height(new Cell("") { Style = Plain(20) }), Height(new Cell([]) { Style = Plain(20) }), 4);
        Assert.Equal(22.4, Height(new Cell([Run("", Plain(40))])), 4);
        Assert.Equal(32.0, Height(new Cell([Run("", Plain(40))]) { Style = Plain(20) }), 4);
    }

    [Fact]
    public void Runs_isReadOnly_andACopy()
    {
        var source = new List<TextRun> { Run("ab", Plain()) };
        var cell = new Cell(source);

        Assert.Throws<NotSupportedException>(() => ((IList<TextRun>)cell.Runs!).Add(Run("cd", Plain())));
        source.Add(Run("cd", Plain()));
        Assert.Single(cell.Runs!);
    }

    [Fact]
    public void ACellStyleAndTheDefaultStyle_doNotStyleARun()
    {
        using var doc = ShortPage();
        var t = new TableElement { DefaultCellStyle = Plain(30) }.SetColumnWidths(100, 100);
        t.AddRow().AddCell(new Cell([Run("a", Plain(10))]) { Style = Plain(50) });
        doc.Add(t);

        var shown = Placements(Content(Save(doc)));

        Assert.Equal(10.0, shown[0].FontSize);
    }

    // ── The continuation limit ───────────────────────────────────────────────

    private static TextStyle Helvetica10() => new() { FontRef = Helv, FontSize = 10 };

    private static Setup Sixty { get; } = new() { PageWidth = 400, PageHeight = 60 };

    private static Setup Wide { get; } = new() { PageWidth = 2000, PageHeight = 40 };

    private static Setup TwoHundred { get; } = new() { PageWidth = 400, PageHeight = 200 };

    /// <summary>
    /// A cell of the text, as a string or as runs of one style, and in the second case with the
    /// text split at the middle so that a run boundary lies inside it.
    /// </summary>
    private static Cell MakeCell(string text, TextStyle style, bool runs)
    {
        if (!runs) return new Cell(text);
        var at = text.Length / 2;
        return new Cell([Run(text[..at], style), Run(text[at..], style)]);
    }

    private static TableElement TwoLineRows(int rows, bool runs)
    {
        var style = Helvetica10();
        var t = new TableElement { DefaultCellStyle = style };
        t.AddRow().AddCell(MakeCell(string.Join("\n", Enumerable.Repeat("mmmmmmmm", 6)), style, runs)).AddCell(MakeCell("s", style, runs));
        for (var i = 0; i < rows; i++) t.AddRow().AddCell(MakeCell("a", style, runs)).AddCell(MakeCell("lorem lorem", style, runs));
        return t;
    }

    private static TableElement OneCellOfLines(int lines, bool runs)
    {
        var style = Helvetica10();
        var t = new TableElement { DefaultCellStyle = style }.SetColumnWidths(1900);
        t.AddRow().AddCell(MakeCell(string.Join("\n", Enumerable.Repeat("ab", lines)), style, runs));
        return t;
    }

    private static TableElement OneRowAPage(int rows, bool runs)
    {
        var style = Helvetica10();
        var t = new TableElement { DefaultCellStyle = style };
        t.AddRow().AddCell(MakeCell(string.Join("\n", Enumerable.Repeat("mmmmmmmm", 10)), style, runs)).AddCell(MakeCell("s", style, runs));
        var paragraph = string.Join(" ", Enumerable.Repeat("lorem", 8));
        for (var i = 0; i < rows; i++) t.AddRow().AddCell(MakeCell("a", style, runs)).AddCell(MakeCell(paragraph, style, runs));
        return t;
    }

    private static Outcome Place(Setup setup, TableElement table, bool oneLine = false)
    {
        TableMeasureProbe.Current = oneLine ? new TableMeasureProbe { ReadEveryCellUnbroken = true } : null;
        try
        {
            return ContinuationSupport.Run(setup, new Log(), new TableRenderer(table));
        }
        finally
        {
            TableMeasureProbe.Current = null;
        }
    }

    private static void AssertSameVerdict(Outcome expected, Outcome actual)
    {
        Assert.Equal(expected.Error?.GetType(), actual.Error?.GetType());
        Assert.Equal(expected.Error?.Message, actual.Error?.Message);
        Assert.Equal(expected.Pages, actual.Pages);
        if (expected.Bytes is not null) Assert.Equal(expected.Bytes, actual.Bytes);
    }

    /// <summary>
    /// A table at the limit gets the verdict of the table of strings, in the same bytes: refused
    /// with the same message one row over, saved with its breaks at the limit.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void ATableAtTheLimit_getsTheVerdictOfTheStringTable(int over)
    {
        var strings = Place(Sixty, TwoLineRows(Limit + over, runs: false));
        var runs = Place(Sixty, TwoLineRows(Limit + over, runs: true));

        Assert.Equal(over == 1, IsLimitRefusal(strings.Error));
        AssertSameVerdict(strings, runs);
    }

    [Fact]
    public void ARunCellThatWouldTakeMoreThanTheLimitIsLaidOutUnbroken_andReported()
    {
        var oneLine = Place(Wide, OneCellOfLines(Limit + 2, runs: true), oneLine: true);
        var decided = Place(Wide, OneCellOfLines(Limit + 2, runs: true));
        var strings = Place(Wide, OneCellOfLines(Limit + 2, runs: false));

        Assert.Null(decided.Error);
        Assert.Equal(oneLine.Bytes, decided.Bytes);
        Assert.Equal(strings.Bytes, decided.Bytes);
        Assert.Equal(1, decided.Renderer.Diagnostics.Count(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped));

        // The unbroken fallback reads the runs as it reads the string: one long line, no show of
        // the line ends.
        var drawn = string.Concat(Texts(Content(decided.Bytes!)));
        Assert.Equal(string.Concat(Enumerable.Repeat("ab", Limit + 2)), drawn);
    }

    [Fact]
    public void ATableWhoseBreaksBoundIsWithinTheLimit_butWhoseUnbrokenLayoutReachesIt_isRefusedAsTheStringTableIs()
    {
        var strings = Place(TwoHundred, OneRowAPage(Limit + 2, runs: false));
        var runs = Place(TwoHundred, OneRowAPage(Limit + 2, runs: true));

        Assert.True(IsLimitRefusal(strings.Error), "the string table must be refused for the test to mean anything");
        AssertSameVerdict(strings, runs);
    }

    /// <summary>
    /// The bound the document reads counts the characters of every run, and a table of runs has
    /// the bound of the table of the same text.
    /// </summary>
    [Fact]
    public void TheBound_countsTheCharactersOfEveryRun()
    {
        var document = new DocumentRenderer(PlacementLoopSupport.NewPdf(), new PdfRectangle(0, 0, 400, 200), new EdgeInsets(10));
        var style = Helvetica10();
        var t = new TableElement { DefaultCellStyle = style };
        t.AddRow().AddCell(new Cell([Run("abc", style), Run("defg", Bold(10)), Run("hi", style)])).AddCell("jk");
        var s = new TableElement { DefaultCellStyle = style };
        s.AddRow().AddCell("abcdefghi").AddCell("jk");

        var bound = document.ContinuationBoundOf(new TableRenderer(t));

        // 2, one for the row, and the eleven characters.
        Assert.Equal(2 + 1 + 11, bound);
        Assert.Equal(document.ContinuationBoundOf(new TableRenderer(s)), bound);
    }

    // ── Measured cost ────────────────────────────────────────────────────────

    /// <summary>
    /// A decision that measures the table again answers a run cell from its cache where the width
    /// is the one it counted at, and a probe that counts the cell again on every hit finds the
    /// same number: the count of a run cell is a function of the cell and the width.
    /// </summary>
    [Fact]
    public void ARunCell_isNotWrappedAgainAtTheWidthItWasCountedAt()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 2000, 600), Margins = new EdgeInsets(10) };
        var style = Helvetica10();
        var t = new TableElement { DefaultCellStyle = style };
        for (var i = 0; i < 5; i++)
        {
            var words = string.Join("\n", Enumerable.Repeat("ab", 48 + i));
            var paragraph = string.Join(" ", Enumerable.Repeat("lorem ipsum dolor", 3 + i));
            t.AddRow().AddCell(MakeCell("x", style, runs: true)).AddCell(new Cell([Run(words[..20], style), Run(words[20..], Bold(10))]) { RowSpan = 2 })
                .AddCell(MakeCell(paragraph, style, runs: true));
            t.AddRow().AddCell(MakeCell("y", style, runs: true));
        }
        doc.Add(t);

        var probe = new TableMeasureProbe { Verify = true };
        TableMeasureProbe.Current = probe;
        try { Save(doc); }
        finally { TableMeasureProbe.Current = null; }

        Assert.True(probe.WrapMisses > 0, "the cells are wrapped");
        Assert.True(probe.WrapHits > 0, "a later pass of the decision reads the cache");
    }

    /// <summary>
    /// With an embedded font, a cell of runs sharing one style instance saves the bytes of the
    /// string cell, the font subset included: the string wrap measures a space before every line
    /// it wraps, so the run wrap does as well, even when no second word joins the line.
    /// </summary>
    [Theory]
    [InlineData("word")]
    [InlineData("two words")]
    [InlineData("supercalifragilistic-no-spaces-at-all-supercalifragilistic")]
    [InlineData("a\nb")]
    [InlineData("")]
    public void SharedStyleRuns_saveTheBytesOfTheStringCell_inAnEmbeddedFont(string text)
    {
        byte[] Build(Func<TextStyle, Cell> make)
        {
            using var doc = FontReportSupport.NewDocument(PdfConformance.None);
            var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
            var style = new TextStyle { FontRef = font, FontSize = 11 };
            var t = new TableElement { DefaultCellStyle = style }.SetColumnWidths(60, 60);

            // The neighbour is in another font, so that its own wrap does not put a space in this
            // font's subset: only the cell under test can.
            t.AddRow().AddCell(make(style)).AddCell(new Cell("x") { Style = Plain(11) });
            doc.Add(t);
            return FontReportSupport.Save(doc);
        }

        var expected = Build(style => new Cell(text));
        var half = text.Length / 2;

        Assert.Equal(expected, Build(style => new Cell([Run(text, style)])));
        Assert.Equal(expected, Build(style => new Cell([Run(text[..half], style), Run(text[half..], style)])));
    }

    // ── PDF/A-2u ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A cell with no text opens in the table's style, so under a PDF/A claim with an embedded
    /// default font it names no Helvetica and veraPDF 2u passes it, as it passes the empty string.
    /// </summary>
    [Fact]
    public void PdfA2u_aCellWithNoText_takesTheEmbeddedDefaultStyle_andPassesVeraPdf()
    {
        using var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        var body = new TextStyle { FontRef = font, FontSize = 11 };
        var t = new TableElement { DefaultCellStyle = body }.SetColumnWidths(200, 200);
        t.AddRow().AddCell(new Cell([])).AddCell(new Cell([Run("", body)]));
        t.AddRow().AddCell(new Cell("")).AddCell("text");
        doc.Add(t);

        var pdf = FontReportSupport.Save(doc);

        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.UnembeddedFont);
        Assert.DoesNotContain("Helvetica", Encoding.Latin1.GetString(pdf), StringComparison.Ordinal);
        var report = FontReportSupport.VeraPdf("2u", pdf);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        Assert.Empty(FontReportSupport.FailedRules(report));
    }

    /// <summary>
    /// A document of run cells in an embedded font, with links, passes veraPDF's PDF/A-2u profile
    /// and shows the cells' own text, so the verdict is about the feature.
    /// </summary>
    [Fact]
    public void PdfA2u_aRunCellInAnEmbeddedFont_withLinks_passesVeraPdf()
    {
        using var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        var body = new TextStyle { FontRef = font, FontSize = 11 };
        var big = new TextStyle { FontRef = font, FontSize = 16, Color = new ColorRgb(0.6, 0, 0) };
        var link = new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.com/a" };
        var t = new TableElement { DefaultCellStyle = body }.SetColumnWidths(220, 220);
        t.AddHeaderRow().AddCell(new Cell([Run("Name", big)])).AddCell(new Cell([Run("Detail ", body), Run("link", link)]));
        t.AddRow()
            .AddCell(new Cell([Run("first line\nsecond ", body), Run("astro", big), Run("logie", body)]))
            .AddCell(new Cell([Run("see ", body), Run("the page", link), Run(" and more words so that the line wraps in the narrow column", body)]));
        doc.Add(t);

        var pdf = FontReportSupport.Save(doc);

        Assert.Equal(2, Links(pdf).Count);
        Assert.Contains(FontReportSupport.TfLines(pdf), l => l.EndsWith(" 16 Tf", StringComparison.Ordinal));
        Assert.All(FontReportSupport.ShowLines(pdf), l => Assert.StartsWith("<", l, StringComparison.Ordinal));
        var report = FontReportSupport.VeraPdf("2u", pdf);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        Assert.Empty(FontReportSupport.FailedRules(report));
    }
}
