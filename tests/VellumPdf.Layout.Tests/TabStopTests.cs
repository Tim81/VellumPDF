// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Tab stops and their leaders (#298). Positions are asserted against the content stream, with
/// the expected numbers computed from font metrics and from the constants the leader is built
/// from, never from the layout code's own arithmetic.
/// </summary>
public sealed partial class TabStopTests
{
    private static readonly string Regular = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    private static TextStyle Helvetica(double size = 12) =>
        new() { Font = Standard14.Helvetica, FontSize = size };

    private static double Width(string text, double size = 12) => Helvetica(size).MeasureString(text);

    /// <summary>A page with no margins, so content x is page x.</summary>
    private static Document Page(double width, double height = 600) => new()
    {
        PageSize = new PdfRectangle(0, 0, width, height),
        Margins = EdgeInsets.Zero,
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Content(Document doc) => PdfTestUtil.DecompressAllFlatStreams(Save(doc));

    private static string Content(Paragraph paragraph, double width = 400)
    {
        using var doc = Page(width);
        doc.Add(paragraph);
        return Content(doc);
    }

    private static string Num(double v) =>
        Math.Round(v, 5, MidpointRounding.AwayFromZero).ToString("0.#####", CultureInfo.InvariantCulture);

    private readonly record struct Show(double X, double Y, string Text);

    private static List<Show> Shows(string content)
    {
        var shows = new List<Show>();
        double x = 0, y = 0;
        foreach (Match m in ShowEvent().Matches(content))
        {
            if (m.Groups["x"].Success)
            {
                x = double.Parse(m.Groups["x"].Value, CultureInfo.InvariantCulture);
                y = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                shows.Add(new Show(x, y, m.Groups["tok"].Value));
            }
        }
        return shows;
    }

    /// <summary>The operators of every <c>q ... Q</c> block, one array per block.</summary>
    private static List<string[]> QBlocks(string content)
    {
        var blocks = new List<string[]>();
        List<string>? open = null;
        foreach (var line in content.Split('\n').Select(l => l.TrimEnd('\r')))
        {
            if (line == "q") open = [line];
            else if (open is not null)
            {
                open.Add(line);
                if (line == "Q")
                {
                    blocks.Add([.. open]);
                    open = null;
                }
            }
        }
        return blocks;
    }

    private static void AssertNear(double expected, double actual, string what, double tolerance = 2e-5) =>
        Assert.True(Math.Abs(expected - actual) <= tolerance, $"{what}: expected {expected}, got {actual}");

    private static IEnumerable<double> Numbers(string line) =>
        line.Split(' ').Where(t => t.Length > 0 && (char.IsDigit(t[0]) || t[0] == '-'))
            .Select(t => double.Parse(t, CultureInfo.InvariantCulture));

    [GeneratedRegex(@"(?m)^(?:1 0 0 1 (?<x>-?\d+(?:\.\d+)?) (?<y>-?\d+(?:\.\d+)?) Tm|(?<tok>\((?:[^()\\]|\\.)*\)|<[0-9A-F]*>) Tj)\r?$")]
    private static partial Regex ShowEvent();

    // ── Public API ───────────────────────────────────────────────────────────

    [Fact]
    public void TabStop_defaults_andStoresWhatItWasGiven()
    {
        var stop = new TabStop(72);

        Assert.Equal(72, stop.Position);
        Assert.Equal(TabAlignment.Left, stop.Alignment);
        Assert.Equal(TabLeader.None, stop.Leader);
        Assert.Equal('.', stop.DecimalCharacter);

        var full = new TabStop(0, TabAlignment.Decimal, TabLeader.Dashes) { DecimalCharacter = ',' };
        Assert.Equal((0d, TabAlignment.Decimal, TabLeader.Dashes, ','), (full.Position, full.Alignment, full.Leader, full.DecimalCharacter));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(-0.0001)]
    public void TabStop_refusesAPositionThatIsNotAFiniteNonNegativeNumber(double position)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new TabStop(position));
        Assert.Equal("position", ex.ParamName);
    }

    [Fact]
    public void TabStop_refusesAnUndefinedAlignmentAndLeader()
    {
        Assert.Equal("alignment", Assert.Throws<ArgumentOutOfRangeException>(() => new TabStop(1, (TabAlignment)9)).ParamName);
        Assert.Equal("leader", Assert.Throws<ArgumentOutOfRangeException>(() => new TabStop(1, TabAlignment.Left, (TabLeader)9)).ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => new TabStop(1, (TabAlignment)(-1)));
    }

    [Theory]
    [InlineData(' ')]
    [InlineData('\t')]
    [InlineData('\n')]
    [InlineData(' ')]
    [InlineData(' ')]
    [InlineData('\u0000')]
    [InlineData('\uD800')]
    public void TabStop_refusesADecimalCharacterThatIsWhiteSpaceControlOrASurrogate(char c)
    {
        Assert.Throws<ArgumentException>(() => new TabStop(1) { DecimalCharacter = c });
    }

    [Fact]
    public void TabStops_refusesANullStopAndPositionsThatDoNotIncrease()
    {
        Assert.Throws<ArgumentNullException>(() => new Paragraph("x") { TabStops = [new TabStop(10), null!] });
        Assert.Throws<ArgumentException>(() => new Paragraph("x") { TabStops = [new TabStop(100), new TabStop(50)] });
        var equal = Assert.Throws<ArgumentException>(() => new Paragraph("x") { TabStops = [new TabStop(50), new TabStop(50)] });
        Assert.Contains("index 1", equal.Message, StringComparison.Ordinal);
        Assert.Null(new Paragraph("x").TabStops);
        Assert.Null(new Paragraph("x") { TabStops = null }.TabStops);
        Assert.Empty(new Paragraph("x") { TabStops = [] }.TabStops!);
    }

    [Fact]
    public void TabStops_copiesTheList()
    {
        var list = new List<TabStop> { new(10), new(20) };
        var paragraph = new Paragraph("a\tb") { TabStops = list };

        list.Clear();
        list.Add(new TabStop(5));

        Assert.Equal([10d, 20d], paragraph.TabStops!.Select(s => s.Position));
    }

    // ── Paragraphs without stops keep their bytes ────────────────────────────

    [Fact]
    public void WithoutStops_aTabIsASpace()
    {
        var plain = Content(new Paragraph("Title 12", Helvetica()));

        Assert.Contains("(Title 12) Tj", Content(new Paragraph("Title\t12", Helvetica())));
        Assert.Equal(plain, Content(new Paragraph("Title\t12", Helvetica())));
        Assert.Equal(plain, Content(new Paragraph("Title\t12", Helvetica()) { TabStops = [] }));
        Assert.Equal(plain, Content(new Paragraph("Title\t12", Helvetica()) { TabStops = null }));
    }

    // ── The rule ─────────────────────────────────────────────────────────────

    [Fact]
    public void RightStopWithDots_putsTheTextAtTheRightEdge_andDrawsTheLeaderAsAStrokedPath()
    {
        var content = Content(new Paragraph("Inleiding\t12", Helvetica())
        {
            TabStops = [new TabStop(400, TabAlignment.Right, TabLeader.Dots)],
        });

        var shows = Shows(content);
        Assert.Equal(["(Inleiding )", "(12)"], shows.Select(s => s.Text));
        AssertNear(0, shows[0].X, "x of the first segment");
        var start = 400 - Width("12");
        AssertNear(start, shows[1].X, "x of the right-aligned segment");
        AssertNear(400, shows[1].X + Width("12"), "right edge");

        // The text object closes before the leader is drawn.
        var et = content.IndexOf("\nET\n", StringComparison.Ordinal);
        var q = content.IndexOf("\nq\n", StringComparison.Ordinal);
        Assert.True(et >= 0 && q > et, "the leader follows ET");

        var block = Assert.Single(QBlocks(content));
        var pen = Width("Inleiding ");
        var from = pen + (0.25 * 12);
        var to = start - (0.25 * 12);
        var x1 = Math.Ceiling(from / 6) * 6;
        var y = shows[0].Y + (0.96 / 2);

        Assert.Equal(["q", "0 0 0 RG", "1 J", "[0 6] 0 d", "0.96 w"], block[..5]);
        Assert.Equal(["S", "Q"], block[^2..]);
        Assert.Equal(9, block.Length);
        var m = Numbers(block[5]).ToArray();
        var l = Numbers(block[6]).ToArray();
        Assert.EndsWith(" m", block[5], StringComparison.Ordinal);
        Assert.EndsWith(" l", block[6], StringComparison.Ordinal);
        AssertNear(x1, m[0], "leader starts on the grid");
        AssertNear(y, m[1], "leader height");
        AssertNear(to, l[0], "leader ends before the text");
        AssertNear(y, l[1], "leader is level");
    }

    [Fact]
    public void DashesAndRule_haveTheirOwnCapDashAndHeight()
    {
        foreach (var (leader, cap, dash) in new[]
        {
            (TabLeader.Dashes, "0 J", "[2.88 1.92] 0 d"),
            (TabLeader.Rule, "0 J", "[] 0 d"),
        })
        {
            var content = Content(new Paragraph("Name\t7", Helvetica())
            {
                TabStops = [new TabStop(300, TabAlignment.Right, leader)],
            });

            var shows = Shows(content);
            var block = Assert.Single(QBlocks(content));
            Assert.Equal(["q", "0 0 0 RG", cap, dash, "0.96 w"], block[..5]);
            var m = Numbers(block[5]).ToArray();
            AssertNear(shows[0].Y + 3, m[1], $"{leader} height is a quarter of the size above the baseline");
            AssertNear(Width("Name ") + 3, m[0], $"{leader} starts a gap after the pen");
        }
    }

    [Fact]
    public void LeaderDots_sitOnAGridAnchoredAtTheContentEdge_soLinesAlign()
    {
        // Margins of the document and the paragraph move the content edge; the dots follow it.
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 600),
            Margins = new EdgeInsets(20),
        };
        doc.Add(new Paragraph("A\t1\nBBBBBBBBBB\t2\nCC\t3", Helvetica())
        {
            Margins = new EdgeInsets(0, 0, 0, 5),
            TabStops = [new TabStop(300, TabAlignment.Right, TabLeader.Dots)],
        });

        var content = Content(doc);
        var blocks = QBlocks(content);
        Assert.Equal(3, blocks.Count);
        var starts = blocks.Select(b => Numbers(b[5]).First()).ToArray();
        foreach (var x in starts)
        {
            var steps = (x - 25) / 6;
            AssertNear(Math.Round(steps), steps, $"x {x} is a whole number of periods from the content edge");
        }
    }

    [Fact]
    public void LeftCenterAndDecimalStops_placeTheirTextAtTheStop()
    {
        var left = Shows(Content(new Paragraph("a\tbcd", Helvetica()) { TabStops = [new TabStop(100)] }));
        AssertNear(100, left[1].X, "left");

        var center = Shows(Content(new Paragraph("a\tbcd", Helvetica()) { TabStops = [new TabStop(100, TabAlignment.Center)] }));
        AssertNear(100 - (Width("bcd") / 2), center[1].X, "centre");

        var right = Shows(Content(new Paragraph("a\tbcd", Helvetica()) { TabStops = [new TabStop(100, TabAlignment.Right)] }));
        AssertNear(100 - Width("bcd"), right[1].X, "right");
    }

    [Fact]
    public void DecimalStop_linesUpOnTheFirstDecimalCharacter_orEndsAtTheStopWithoutOne()
    {
        var stops = new[] { new TabStop(200, TabAlignment.Decimal) };
        foreach (var (number, before) in new[] { ("-12.5", "-12"), ("3.25", "3") })
        {
            var shows = Shows(Content(new Paragraph("n\t" + number, Helvetica()) { TabStops = stops }));
            AssertNear(200, shows[1].X + Width(before), $"the point of {number}", 0.01);
        }

        var whole = Shows(Content(new Paragraph("n\t100", Helvetica()) { TabStops = stops }));
        AssertNear(200, whole[1].X + Width("100"), "a number with no point ends at the stop", 0.01);

        var comma = new[] { new TabStop(200, TabAlignment.Decimal) { DecimalCharacter = ',' } };
        var dutch = Shows(Content(new Paragraph("n\t-12,5", Helvetica()) { TabStops = comma }));
        AssertNear(200, dutch[1].X + Width("-12"), "the comma of -12,5", 0.01);

        // The default character does not line up a comma.
        var wrong = Shows(Content(new Paragraph("n\t-12,5", Helvetica()) { TabStops = stops }));
        AssertNear(200, wrong[1].X + Width("-12,5"), "a comma is not the point", 0.01);
    }

    [Fact]
    public void DecimalPoint_inAMultiWordSegmentCountsTheSpacesBeforeIt()
    {
        var shows = Shows(Content(new Paragraph("n\tEUR 12.50", Helvetica())
        {
            TabStops = [new TabStop(200, TabAlignment.Decimal)],
        }));

        Assert.Equal("(EUR 12.50)", shows[1].Text);
        AssertNear(200, shows[1].X + Width("EUR 12"), "the point", 0.01);
    }

    [Fact]
    public void TheTextBeforeATabEndsInASpace_andTheSpaceIsPartOfThePen()
    {
        var shows = Shows(Content(new Paragraph("Inleiding\t12", Helvetica())
        {
            TabStops = [new TabStop(300, TabAlignment.Left)],
        }));

        Assert.Equal("(Inleiding )", shows[0].Text);

        // The next stop is to the right of the end of the text with its space.
        var pen = Width("Inleiding ");
        var next = Shows(Content(new Paragraph("Inleiding\t12", Helvetica())
        {
            TabStops = [new TabStop(pen - 0.001), new TabStop(pen), new TabStop(pen + 20)],
        }));
        AssertNear(pen + 20, next[1].X, "a stop the pen has reached is passed over");
    }

    [Fact]
    public void ATabWithNoStopToItsRight_isDrawnAsOneSpace()
    {
        var shows = Shows(Content(new Paragraph("Wider than ten\tB", Helvetica())
        {
            TabStops = [new TabStop(10)],
        }));

        var show = Assert.Single(shows);
        Assert.Equal("(Wider than ten B)", show.Text);
        Assert.Equal(Shows(Content(new Paragraph("Wider than ten B", Helvetica()))).Single().Text, show.Text);
    }

    [Fact]
    public void ATextThatWouldStartLeftOfThePen_startsAtThePen()
    {
        var pen = Width("A long first segment ");
        var shows = Shows(Content(new Paragraph("A long first segment\tX", Helvetica())
        {
            TabStops = [new TabStop(pen + 5, TabAlignment.Right)],
        }));

        Assert.Equal(["(A long first segment )", "(X)"], shows.Select(s => s.Text));
        AssertNear(pen, shows[1].X, "no overlap");
        Assert.Empty(QBlocks(Content(new Paragraph("A long first segment\tX", Helvetica())
        {
            TabStops = [new TabStop(pen + 5, TabAlignment.Right, TabLeader.Dots)],
        })));
    }

    [Fact]
    public void ALeaderShorterThanOnePeriod_isNotDrawn()
    {
        var pen = Width("Inleiding ");
        var start = (pen + 6) + 0.4;
        Assert.Empty(QBlocks(Content(new Paragraph("Inleiding\t1", Helvetica())
        {
            TabStops = [new TabStop(start + Width("1"), TabAlignment.Right, TabLeader.Dots)],
        })));

        // The same paragraph with the stop moved right by one period draws it.
        Assert.Single(QBlocks(Content(new Paragraph("Inleiding\t1", Helvetica())
        {
            TabStops = [new TabStop(start + Width("1") + 6, TabAlignment.Right, TabLeader.Dots)],
        })));
    }

    [Fact]
    public void ConsecutiveTabs_andALeadingTab_useSuccessiveStops()
    {
        var shows = Shows(Content(new Paragraph("a\t\tb", Helvetica())
        {
            TabStops = [new TabStop(50), new TabStop(100)],
        }));
        Assert.Equal(["(a )", "(b)"], shows.Select(s => s.Text));
        AssertNear(100, shows[1].X, "second stop");

        var leading = Shows(Content(new Paragraph("\tb", Helvetica()) { TabStops = [new TabStop(40)] }));
        AssertNear(40, Assert.Single(leading).X, "a leading tab");
    }

    [Fact]
    public void ALeaderBetweenTwoTabs_isDrawnEvenWhenNoTextSitsBetween()
    {
        var content = Content(new Paragraph("a\t\tb", Helvetica())
        {
            TabStops = [new TabStop(100, TabAlignment.Left, TabLeader.Rule), new TabStop(200)],
        });

        var block = Assert.Single(QBlocks(content));
        AssertNear(Width("a ") + 3, Numbers(block[5]).First(), "leader start");
        AssertNear(100 - 3, Numbers(block[6]).First(), "leader end");
        AssertNear(200, Shows(content)[1].X, "text after the second tab");
    }

    [Fact]
    public void LeftStopText_wrapsWordByWordFromTheStop()
    {
        var shows = Shows(Content(
            new Paragraph("Term\tone two three four five six seven", Helvetica())
            {
                TabStops = [new TabStop(60)],
            },
            width: 150));

        AssertNear(60, shows[1].X, "first word at the stop");
        Assert.True(shows.Count >= 3);
        AssertNear(0, shows[2].X, "the wrapped line starts at the left edge");
        Assert.StartsWith("(one", shows[1].Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ALineWithATabAtAStop_startsAtTheLeftEdge_andIsNotJustified()
    {
        foreach (var alignment in new[] { HorizontalAlignment.Justify, HorizontalAlignment.Right, HorizontalAlignment.Center })
        {
            // A line that ends at a line break is stretched when justified, so the absence of
            // word spacing is the tab's doing.
            var content = Content(
                new Paragraph("Item\t12\nnext line of the paragraph", Helvetica())
                {
                    Alignment = alignment,
                    TabStops = [new TabStop(100, TabAlignment.Right)],
                },
                width: 200);

            var shows = Shows(content);
            AssertNear(0, shows[0].X, $"{alignment}: the tab line starts at the left edge");
            Assert.DoesNotContain(" Tw", content, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RunsWithOtherStyles_leadToTheColourOfTheTextAfterTheTab()
    {
        var red = new TextStyle { Font = Standard14.Helvetica, FontSize = 10, Color = new ColorRgb(1, 0, 0) };
        var content = Content(new Paragraph([new TextRun("Name\t", Helvetica()), new TextRun("12", red)])
        {
            TabStops = [new TabStop(300, TabAlignment.Right, TabLeader.Dots)],
        });

        var block = Assert.Single(QBlocks(content));
        Assert.Equal("1 0 0 RG", block[1]);
        Assert.Equal("[0 5] 0 d", block[3]);
        Assert.Equal("0.8 w", block[4]);
    }

    // ── Tagged documents ─────────────────────────────────────────────────────

    [Fact]
    public void InATaggedDocument_theLeaderIsAnArtifactOutsideTheParagraph()
    {
        using var doc = Page(400);
        doc.Tagged = true;
        doc.Add(new Paragraph("Inleiding\t12", Helvetica())
        {
            TabStops = [new TabStop(400, TabAlignment.Right, TabLeader.Dots)],
        });

        var lines = Content(doc).Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var bdc = lines.FindIndex(l => l.EndsWith("BDC", StringComparison.Ordinal));
        var et = lines.IndexOf("ET");
        var emc = lines.IndexOf("EMC");
        var artifact = lines.IndexOf("/Artifact BMC");
        var q = lines.IndexOf("q");
        var bigQ = lines.IndexOf("Q");
        var secondEmc = lines.LastIndexOf("EMC");

        Assert.True(bdc >= 0 && bdc < lines.IndexOf("BT"), "the paragraph's marked content opens before BT");
        Assert.True(et < emc, "ET before the paragraph's EMC");
        Assert.True(emc < artifact, "the artifact follows the paragraph's marked content");
        Assert.Equal(artifact + 1, q);
        Assert.True(q < bigQ && bigQ + 1 == secondEmc, "q ... Q sits inside the artifact");
        Assert.Equal(2, lines.Count(l => l == "EMC"));
    }

    [Fact]
    public void WithoutTagging_theLeaderHasNoArtifactBrackets()
    {
        var content = Content(new Paragraph("Inleiding\t12", Helvetica())
        {
            TabStops = [new TabStop(400, TabAlignment.Right, TabLeader.Dots)],
        });

        Assert.DoesNotContain("BMC", content, StringComparison.Ordinal);
        Assert.DoesNotContain("EMC", content, StringComparison.Ordinal);
    }

    [Fact]
    public void ALeaderDoesNotLeakItsStrokeStateIntoTheNextParagraph()
    {
        using var doc = Page(400);
        doc.Add(new Paragraph("Inleiding\t12", Helvetica())
        {
            TabStops = [new TabStop(400, TabAlignment.Right, TabLeader.Dots)],
        });
        doc.Add(new Paragraph("Next", Helvetica()));

        var content = Content(doc);
        var q = content.IndexOf("\nq\n", StringComparison.Ordinal);
        var bigQ = content.IndexOf("\nQ\n", StringComparison.Ordinal);
        Assert.True(q > 0 && bigQ > q);
        Assert.Single(Regex.Matches(content, @"(?m)^1 J\r?$"));

        // Every stroke operator the leader sets lies between its q and Q.
        foreach (var op in new[] { " RG\n", " J\n", " d\n", " w\n" })
        {
            foreach (Match m in Regex.Matches(content, Regex.Escape(op)))
                Assert.True(m.Index > q && m.Index < bigQ, $"'{op.Trim()}' sits inside q ... Q");
        }
    }

    // ── Refusals at the save ─────────────────────────────────────────────────

    [Fact]
    public void AStopBeyondTheContentWidth_isRefusedBeforeLayout_andTheDocumentStaysSaveable()
    {
        using var doc = Page(300);
        doc.Add(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(350)] });

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("350", ex.Message, StringComparison.Ordinal);
        Assert.Contains("300", ex.Message, StringComparison.Ordinal);

        // Nothing was laid out, so widening the page lets the same document save, once.
        doc.PageSize = new PdfRectangle(0, 0, 400, 600);
        var bytes = Save(doc);
        Assert.Single(Regex.Matches(PdfTestUtil.DecompressAllFlatStreams(bytes), @"(?m)^BT\r?$"));
        AssertNear(350, Shows(PdfTestUtil.DecompressAllFlatStreams(bytes))[1].X, "stop used after the retry");
    }

    [Fact]
    public void AStopAtExactlyTheContentWidth_isAccepted_andOneBeyondItByAnyAmountIsNot()
    {
        using (var doc = Page(300))
        {
            doc.Add(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(300, TabAlignment.Right)] });
            Save(doc);
        }

        using (var doc = Page(300))
        {
            doc.Add(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(300.01, TabAlignment.Right)] });
            Assert.Throws<InvalidOperationException>(() => Save(doc));
        }
    }

    [Fact]
    public void TheContentWidthTakesTheParagraphMarginsOff()
    {
        using var doc = Page(300);
        doc.Add(new Paragraph("a\tb", Helvetica())
        {
            Margins = new EdgeInsets(0, 50, 0, 0),
            TabStops = [new TabStop(260)],
        });

        Assert.Contains("250", Assert.Throws<InvalidOperationException>(() => Save(doc)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusalNamesAParagraphOnlyWhenItHoldsStops()
    {
        using var doc = Page(300);
        doc.Add(new Paragraph("a\tb", Helvetica()));
        doc.Add(new Paragraph("a\tb", Helvetica()) { TabStops = [] });
        Save(doc);
    }

    [Theory]
    [InlineData(0d, TabLeader.Dots)]
    [InlineData(-4d, TabLeader.Dots)]
    [InlineData(0d, TabLeader.Dashes)]
    [InlineData(0d, TabLeader.Rule)]
    [InlineData(6e-5, TabLeader.Rule)]
    [InlineData(double.NaN, TabLeader.Dots)]
    public void ALeaderWhoseMeasuresWouldBeWrittenAsZero_isRefusedAtTheSave(double size, TabLeader leader)
    {
        using var doc = Page(300);
        doc.Add(new Paragraph("a\tb", Helvetica(size))
        {
            TabStops = [new TabStop(200, TabAlignment.Right, leader)],
        });

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("leader", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSmallestSizeAWrittenLeaderAllows_isAccepted_andTheSameSizeWithoutALeaderIsNeverRefused()
    {
        using (var doc = Page(300))
        {
            doc.Add(new Paragraph("a\tb", Helvetica(7e-5))
            {
                TabStops = [new TabStop(200, TabAlignment.Right, TabLeader.Dots)],
            });
            Save(doc);
        }

        using (var doc = Page(300))
        {
            doc.Add(new Paragraph("a\tb", Helvetica(0)) { TabStops = [new TabStop(200, TabAlignment.Right)] });
            Save(doc);
        }
    }

    [Fact]
    public void ALeaderChecksEveryRunOfTheParagraph()
    {
        using var doc = Page(300);
        doc.Add(new Paragraph([new TextRun("a\t", Helvetica()), new TextRun("b", Helvetica(0))])
        {
            TabStops = [new TabStop(200, TabAlignment.Right, TabLeader.Dots)],
        });

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("Run 1", ex.Message, StringComparison.Ordinal);
    }

    // ── A renderer laid out narrower than its stops ──────────────────────────

    private sealed class Narrow(ParagraphRenderer inner, double width) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            return inner.Layout(context.WithArea(new LayoutBox(context.Area.X, context.Area.Y, width, context.Area.Height)));
        }

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    [Fact]
    public void AStopBeyondTheWidthARendererIsLaidOutAt_isTreatedAsAbsent_andReported()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = EdgeInsets.Zero };
        var paragraph = new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(60), new TabStop(250), new TabStop(300)] };
        doc.Add(new Narrow(new ParagraphRenderer(paragraph), 100));

        var bytes = Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.TabStopBeyondWidth, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(2, item.Count);

        // The stop within the width is used; the others are absent, so a tab past the first
        // advances as a space.
        var shows = Shows(PdfTestUtil.DecompressAllFlatStreams(bytes));
        AssertNear(60, shows[^1].X, "the stop within the width is used");
    }

    [Fact]
    public void AStopBeyondTheWidth_leavesATabAsASpace_whenNoStopRemains()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = EdgeInsets.Zero };
        doc.Add(new Narrow(new ParagraphRenderer(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(250)] }), 100));

        var bytes = Save(doc);

        Assert.Equal(["(a b)"], Shows(PdfTestUtil.DecompressAllFlatStreams(bytes)).Select(s => s.Text));
        Assert.Equal(LayoutDiagnosticCode.TabStopBeyondWidth, Assert.Single(doc.Diagnostics).Code);
    }

    [Fact]
    public void StrictDiagnostics_refusesAStopBeyondTheWidth_andASavedParagraphWithoutATabReportsNothing()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 600),
            Margins = EdgeInsets.Zero,
            StrictDiagnostics = true,
        };
        doc.Add(new Narrow(new ParagraphRenderer(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(250)] }), 100));
        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("TabStopBeyondWidth", ex.Message, StringComparison.Ordinal);

        using var quiet = new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = EdgeInsets.Zero };
        quiet.Add(new Narrow(new ParagraphRenderer(new Paragraph("a b", Helvetica()) { TabStops = [new TabStop(250)] }), 100));
        Save(quiet);
        Assert.Empty(quiet.Diagnostics);
    }

    // ── Review round: placement again at the same stop, one rule for stopless tabs ──────────────

    [Fact]
    public void ATextThatFitsOnlyFromTheLeftEdge_isPlacedThereAtItsStop_withNoLeaderBeforeIt()
    {
        var content = Content(
            new Paragraph("Hello\tabcdefghijklmnopqrstuvwxyz", Helvetica())
            {
                TabStops = [new TabStop(100, TabAlignment.Right, TabLeader.Dots)],
            },
            width: 170);

        var shows = Shows(content);
        Assert.Equal(["(Hello)", "(abcdefghijklmnopqrstuvwxyz)"], shows.Select(s => s.Text));
        AssertNear(0, shows[1].X, "starts at the left edge: the stop is left of where it would end");
        Assert.True(shows[0].Y > shows[1].Y, "second line is lower");
        Assert.Empty(QBlocks(content));
    }

    [Fact]
    public void ATextThatFitsNowhere_isSetAsOrdinaryTextFromTheLeftEdge()
    {
        var shows = Shows(Content(
            new Paragraph("Hello\tabcdefghijklmnopqrstuvwxyz", Helvetica())
            {
                TabStops = [new TabStop(100, TabAlignment.Right, TabLeader.Dots)],
            },
            width: 120));

        Assert.Equal("(Hello)", shows[0].Text);
        Assert.All(shows.Skip(1), s => AssertNear(0, s.X, "ordinary text from the left edge"));
        Assert.True(shows.Count >= 3, "the over-wide word is cut");
    }

    [Fact]
    public void ATocNumberThatDoesNotFitAfterTheTitle_goesToTheNextLineAtTheStop_withALeaderFromTheLeftEdge()
    {
        // 49 letters x are 294 pt; the pen after the space is 297.336, and "12" needs 13.344, so the
        // number would end at 310.68 on a 300 pt line.
        var title = new string('x', 49);
        var content = Content(
            new Paragraph(title + "\t12", Helvetica())
            {
                TabStops = [new TabStop(300, TabAlignment.Right, TabLeader.Dots)],
            },
            width: 300);

        var shows = Shows(content);
        Assert.Equal(["(" + title + ")", "(12)"], shows.Select(s => s.Text));
        AssertNear(0, shows[0].X, "title");
        AssertNear(300 - Width("12"), shows[1].X, "the number ends at the stop");
        Assert.True(shows[0].Y > shows[1].Y, "the number is on the next line");

        var block = Assert.Single(QBlocks(content));
        var m = Numbers(block[5]).ToArray();
        var l = Numbers(block[6]).ToArray();
        AssertNear(0, m[0], "the leader starts at the left edge");
        AssertNear(300 - Width("12") - 3, l[0], "and ends a gap before the number");
        AssertNear(shows[1].Y + 0.48, m[1], "on the number's baseline");
    }

    [Fact]
    public void ATocNumberThatFitsAfterTheTitle_staysOnTheLine_withALeaderFromTheText()
    {
        var title = new string('x', 40);
        var content = Content(
            new Paragraph(title + "\t12", Helvetica())
            {
                TabStops = [new TabStop(300, TabAlignment.Right, TabLeader.Dots)],
            },
            width: 300);

        var shows = Shows(content);
        Assert.Equal(["(" + title + " )", "(12)"], shows.Select(s => s.Text));
        AssertNear(shows[0].Y, shows[1].Y, "one line");
        var block = Assert.Single(QBlocks(content));
        AssertNear(Math.Ceiling((Width(title + " ") + 3) / 6) * 6, Numbers(block[5]).First(), "leader starts after the title");
    }

    [Fact]
    public void ALineThatEndsInATabWhoseTextMovedOn_dropsTheSeparatorSpace()
    {
        var words = "aaaa bbbb cccc dddd eeee ffff";
        var paragraph = new Paragraph(words + "\t1234567", Helvetica())
        {
            Alignment = HorizontalAlignment.Justify,
            TabStops = [new TabStop(200, TabAlignment.Right)],
        };
        var content = Content(paragraph, width: 200);

        var shows = Shows(content);
        Assert.Equal("(" + words + ")", shows[0].Text);
        var tw = double.Parse(
            Regex.Match(content, @"(?m)^(-?\d+(?:\.\d+)?) Tw").Groups[1].Value, CultureInfo.InvariantCulture);
        AssertNear(200, Width(words) + (5 * tw), "the justified line reaches the right edge", 0.01);

        var right = Shows(Content(
            new Paragraph(words + "\t1234567", Helvetica())
            {
                Alignment = HorizontalAlignment.Right,
                TabStops = [new TabStop(200, TabAlignment.Right)],
            },
            width: 200));
        AssertNear(200 - Width(words), right[0].X, "a right-aligned line ends at the edge without the space");
    }

    [Fact]
    public void ATabOnAStopWithNoLeader_followedByAStoplessTab_keepsTheFirstTab()
    {
        var shows = Shows(Content(new Paragraph("abc\t\tdef ghi", Helvetica()) { TabStops = [new TabStop(50)] }));

        Assert.Equal(["(abc )", "( def ghi)"], shows.Select(s => s.Text));
        AssertNear(50, shows[1].X, "the first tab reached the stop, the second advances one space");
        var dots = Shows(Content(new Paragraph("abc\t\tdef ghi", Helvetica())
        {
            TabStops = [new TabStop(50, TabAlignment.Left, TabLeader.Dots)],
        }));
        Assert.Equal(shows.Select(s => (s.X, s.Text)), dots.Select(s => (s.X, s.Text)));
    }

    [Fact]
    public void ATabLandingOnAStop_thenAStoplessTab_leavesRoomForTheTextThatFollows()
    {
        var text = "abc\t\t" + string.Concat(Enumerable.Repeat("word ", 20));
        var shows = Shows(Content(new Paragraph(text, Helvetica()) { TabStops = [new TabStop(250)] }, width: 300));

        Assert.Equal("(abc )", shows[0].Text);
        AssertNear(250, shows[1].X, "after the first tab");
        Assert.Equal("( word)", shows[1].Text);
        AssertNear(shows[0].Y, shows[1].Y, "on the first line");
        AssertNear(0, shows[2].X, "the next word wraps");
    }

    [Fact]
    public void EveryTabWithNoStopToItsRight_isOneSpace()
    {
        Assert.Equal(
            ["(abc  def)"],
            Shows(Content(new Paragraph("abc\t\tdef", Helvetica()) { TabStops = [new TabStop(10)] })).Select(s => s.Text));
        var second = Shows(Content(new Paragraph("\t\tb", Helvetica()) { TabStops = [new TabStop(10)] }));
        Assert.Equal("( b)", Assert.Single(second).Text);
        AssertNear(10, second[0].X, "the first tab reaches the stop, the second is one space");
    }

    [Fact]
    public void AStopAtZero_isNeverUsed_soItBehavesAsNoStop()
    {
        Assert.Equal(
            ["(a b)"],
            Shows(Content(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(0)] })).Select(s => s.Text));
        Assert.Equal(
            ["(  b)"],
            Shows(Content(new Paragraph("\t\tb", Helvetica()) { TabStops = [new TabStop(0)] })).Select(s => s.Text));
    }

    [Fact]
    public void ATabThatUsesNoStop_leavesTheLineToTheAlignment()
    {
        var plain = Shows(Content(
            new Paragraph("a\tb", Helvetica()) { Alignment = HorizontalAlignment.Right, TabStops = [new TabStop(0)] }));
        var leading = Shows(Content(
            new Paragraph("\tb", Helvetica()) { Alignment = HorizontalAlignment.Right, TabStops = [new TabStop(0)] }));

        AssertNear(400 - Width("a b"), plain[0].X, "a right-aligned line");
        AssertNear(400 - Width(" b"), leading[0].X, "a leading tab with no stop is still right-aligned");
    }

    [Fact]
    public void ATabInARunWithNoStyle_isASpace_andReportsNothing()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = EdgeInsets.Zero };
        var paragraph = new Paragraph([new TextRun("a b", Helvetica()), new TextRun("\t", null!)])
        {
            TabStops = [new TabStop(250)],
        };
        doc.Add(new Narrow(new ParagraphRenderer(paragraph), 100));

        Save(doc);

        Assert.DoesNotContain(doc.Diagnostics, d => d.Code == LayoutDiagnosticCode.TabStopBeyondWidth);
    }

    // ── A stop at the width, computed two ways ───────────────────────────────

    [Fact]
    public void AStopAtTheContentWidth_isAcceptedWhicheverWayTheCallerComputesTheWidth()
    {
        const double margin = 42.52;
        var libraryWidth = PageSize.A4.Width - (margin + margin);
        var callerWidth = PageSize.A4.Width - margin - margin;
        Assert.NotEqual(libraryWidth, callerWidth);

        foreach (var position in new[] { libraryWidth, callerWidth })
        {
            using var doc = new Document { PageSize = PageSize.A4, Margins = new EdgeInsets(margin) };
            doc.Add(new Paragraph("Inleiding\t12", Helvetica())
            {
                TabStops = [new TabStop(position, TabAlignment.Right, TabLeader.Dots)],
            });

            var content = Content(doc);
            var shows = Shows(content);
            AssertNear(margin + libraryWidth, shows[1].X + Width("12"), "the number ends at the right edge", 1e-4);
            Assert.Single(QBlocks(content));
        }
    }

    [Fact]
    public void AStopPastTheWidthByTheWrittenScale_isStillRefused_andTheMessagePrintsBothNumbers()
    {
        using var doc = Page(300);
        doc.Add(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(300.00001, TabAlignment.Right)] });

        Assert.Throws<InvalidOperationException>(() => Save(doc));

        using var close = Page(300.1234567);
        close.Add(new Paragraph("a\tb", Helvetica()) { TabStops = [new TabStop(300.1234567 + 4e-6)] });
        Save(close);
    }

    [Fact]
    public void ARightStopAtTheWidthOfARendererOfYours_isUsedAndNeverMovesTheTextOnAfterARoundingStep()
    {
        foreach (var width in new[] { 100.1, 123.456, 77.7, 250.123, 301.37, 199.99, 87.3, 140.01 })
        {
            foreach (var position in new[] { width, width + 1e-9 })
            {
                using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = EdgeInsets.Zero };
                doc.Add(new Narrow(
                    new ParagraphRenderer(new Paragraph("a\t12", Helvetica()) { TabStops = [new TabStop(position, TabAlignment.Right)] }),
                    width));

                var shows = Shows(PdfTestUtil.DecompressAllFlatStreams(Save(doc)));

                Assert.Equal(["(a )", "(12)"], shows.Select(s => s.Text));
                AssertNear(shows[0].Y, shows[1].Y, $"one line at {width}, stop {position}");
                AssertNear(width - Width("12"), shows[1].X, $"the number ends at the width {width}", 1e-4);
                Assert.Empty(doc.Diagnostics);
            }
        }
    }

    // ── A leader's size, checked where the renderer builds its wrap ──────────

    [Theory]
    [InlineData(-12d)]
    [InlineData(1e-6)]
    [InlineData(0d)]
    public void ARendererOfYours_refusesALeaderSizeItCannotDraw_fromLayout(double size)
    {
        var paragraph = new Paragraph("a\tb", Helvetica(size))
        {
            TabStops = [new TabStop(50, TabAlignment.Right, TabLeader.Dots)],
        };
        var renderer = new ParagraphRenderer(paragraph);

        var ex = Assert.Throws<InvalidOperationException>(
            () => renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 100, 100))));
        Assert.Contains("Run 0", ex.Message, StringComparison.Ordinal);

        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 600), Margins = EdgeInsets.Zero };
        doc.Add(new Narrow(new ParagraphRenderer(paragraph), 100));
        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    // ── Cost ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FindStop_searchesByHalving()
    {
        var stops = Enumerable.Range(1, 50).Select(i => new TabStop(i * 10)).ToArray();

        TabStop? Probe(double pen, out long probes)
        {
            ParagraphRenderer.TabStopProbes = 0;
            var stop = ParagraphRenderer.FindStop(stops, pen);
            probes = ParagraphRenderer.TabStopProbes;
            return stop;
        }

        Assert.Equal(10, Probe(0, out _)!.Position);
        Assert.Equal(30, Probe(25, out _)!.Position);
        Assert.Equal(30, Probe(20, out var atStop)!.Position);
        Assert.True(atStop is > 0 and <= 6, $"{atStop} comparisons for 50 stops");
        Assert.Equal(500, Probe(499.5, out _)!.Position);
        Assert.Null(Probe(500, out var last));
        Assert.True(last is > 0 and <= 6);
    }

    [Fact]
    public void ThousandsOfTabs_costTheSamePerTab_andLookEachStopUpByHalving()
    {
        var stops = Enumerable.Range(1, 50).Select(i => new TabStop(i * 10, TabAlignment.Left)).ToArray();
        var style = Helvetica();

        (long Bytes, long Probes) Measure(int tabs)
        {
            var runs = new[] { new TextRun(string.Concat(Enumerable.Repeat("x\t", tabs)) + "y", style) };
            ParagraphRenderer.TabStopProbes = 0;
            var before = GC.GetAllocatedBytesForCurrentThread();
            var (lines, _) = ParagraphRenderer.WordWrap(runs, 500, out _, stops);
            var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.True(lines.Count > 1);
            return (bytes, ParagraphRenderer.TabStopProbes);
        }

        Measure(200);
        var small = Measure(1_000);
        var large = Measure(10_000);

        Assert.True(large.Bytes / 10_000d <= 1.5 * (small.Bytes / 1_000d), $"{small.Bytes} bytes for 1,000 tabs, {large.Bytes} for 10,000");
        Assert.True(large.Probes > 0 && large.Probes <= 10_000 * 7, $"{large.Probes} stop comparisons for 10,000 tabs and 50 stops");
    }

    [Fact]
    public void AParagraphWithStops_isWrappedOnce_withAFooter()
    {
        var renderer = new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, 400, 400), new EdgeInsets(20))
        {
            Footer = new RunningBand("Page {page}"),
        };
        var paragraphs = new List<ParagraphRenderer>();
        for (var i = 0; i < 100; i++)
        {
            var p = new ParagraphRenderer(new Paragraph($"Item {i}\t{i}", Helvetica())
            {
                TabStops = [new TabStop(360, TabAlignment.Right, TabLeader.Dots)],
            });
            paragraphs.Add(p);
            renderer.Add(p);
        }

        renderer.Render(new MemoryStream());

        Assert.Equal(1, renderer.MeasureRuns);
        Assert.All(paragraphs, p => Assert.Equal(1, p.WrapCalls));
    }

    [Fact]
    public void ASplitParagraph_drawsTheLeaderOfEveryLineOnEveryPage()
    {
        var text = string.Join('\n', Enumerable.Range(1, 120).Select(i => $"Item {i}\t{i}"));
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 200),
            Margins = new EdgeInsets(10),
        };
        doc.Add(new Paragraph(text, Helvetica())
        {
            TabStops = [new TabStop(280, TabAlignment.Right, TabLeader.Dots)],
        });

        var bytes = Save(doc);
        var content = PdfTestUtil.DecompressAllFlatStreams(bytes);

        Assert.True(Regex.Matches(content, @"(?m)^BT\r?$").Count > 1, "the paragraph spans pages");
        Assert.Equal(120, QBlocks(content).Count);
        var shows = Shows(content).Where(s => !s.Text.StartsWith("(Item", StringComparison.Ordinal)).ToList();
        Assert.Equal(120, shows.Count);
        foreach (var (s, i) in shows.Select((s, i) => (s, i)))
            AssertNear(280 + 10 - Width((i + 1).ToString(CultureInfo.InvariantCulture)), s.X, $"number {i + 1}");
    }

    // ── Embedded fonts ───────────────────────────────────────────────────────

    [Fact]
    public void AnEmbeddedFont_drawsTheTrailingSpaceInTheGlyphString()
    {
        using var doc = Page(400);
        var handle = doc.LoadTrueTypeFont(Regular);
        var style = new TextStyle { FontRef = new FontReference(handle), FontSize = 12 };
        doc.Add(new Paragraph("Inleiding\t12", style) { TabStops = [new TabStop(400, TabAlignment.Right)] });

        var shows = Shows(Content(doc));

        Assert.Equal([Hex(handle, "Inleiding "), Hex(handle, "12")], shows.Select(s => s.Text));
        AssertNear(400, shows[1].X + style.MeasureString("12"), "right edge");
    }

    private static string Hex(EmbeddedFontHandle handle, string text)
    {
        var gids = new ushort[text.Length];
        var n = handle.GetGlyphIds(text, gids);
        return "<" + string.Concat(gids.Take(n).Select(g => g.ToString("X4", CultureInfo.InvariantCulture))) + ">";
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    private sealed class TempDir : IDisposable
    {
        // Not CreateTempSubdirectory (mode 0700): the Docker veraPDF on CI runs as another user.
        private readonly string _root = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vellum-tabs-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    private static byte[] ContentsPage(bool tagged, TabLeader leader)
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 400, 300), Margins = new EdgeInsets(20), Tagged = tagged };
        var stops = new[] { new TabStop(360, TabAlignment.Right, leader) };
        doc.Add(new Paragraph("Inleiding\t12", Helvetica()) { TabStops = stops });
        doc.Add(new Paragraph("Achtergrond en doelstelling\t14", Helvetica()) { TabStops = stops });
        return Save(doc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PdftotextPrintsTheTextAndNoLeader(bool tagged)
    {
        using var dir = new TempDir();
        var path = dir.Path("toc.pdf");
        File.WriteAllBytes(path, ContentsPage(tagged, TabLeader.Dots));

        ExternalTool.TryRun("pdftotext", ["-layout", path, "-"], out _, out var layout, out var err, out _);
        Assert.Matches(@"Inleiding\s+12", layout);
        Assert.Matches(@"Achtergrond en doelstelling\s+14", layout);
        Assert.False(layout.Contains("..", StringComparison.Ordinal), $"a leader was extracted as text:\n{layout}\n{err}");
        Assert.False(layout.Contains("--", StringComparison.Ordinal));

        // Without -layout, the words of a tagged page are still apart.
        ExternalTool.TryRun("pdftotext", ["-raw", path, "-"], out _, out var raw, out _, out _);
        Assert.Matches(@"Inleiding\s+12", raw);
    }

    [Theory]
    [InlineData(TabLeader.Dots, false)]
    [InlineData(TabLeader.Dashes, true)]
    [InlineData(TabLeader.Rule, true)]
    public void QpdfChecksTheFileClean(TabLeader leader, bool tagged)
    {
        using var dir = new TempDir();
        var path = dir.Path("toc.pdf");
        File.WriteAllBytes(path, ContentsPage(tagged, leader));

        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);

        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
    }

    [Fact]
    public void VeraPdfAcceptsALeaderPageAsPdfA2u()
    {
        using var dir = new TempDir();
        var path = dir.Path("toc-2u.pdf");
        using (var doc = new Document { Conformance = PdfConformance.PdfA2u, PageSize = new PdfRectangle(0, 0, 400, 300) })
        {
            doc.Info.Title = "Contents";
            var handle = doc.LoadTrueTypeFont(Regular);
            var style = new TextStyle { FontRef = new FontReference(handle), FontSize = 12 };
            var stops = new[] { new TabStop(250, TabAlignment.Right, TabLeader.Dots) };
            doc.Add(new Paragraph("Inleiding\t12", style) { TabStops = stops });
            doc.Add(new Paragraph("Achtergrond\t14", style) { TabStops = stops });
            var bytes = Save(doc);

            // The leaders are in the file before the oracle is asked about it.
            Assert.Equal(2, QBlocks(PdfTestUtil.DecompressAllFlatStreams(bytes)).Count);
            File.WriteAllBytes(path, bytes);
        }

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var exit, out var report, out var err, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut);
        Assert.True(exit is 0 or 1, $"veraPDF exit {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{err}");
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
    }
}
