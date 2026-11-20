// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.Reader;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A run boundary draws a space only where the text has white space there. Two runs with no white
/// space between them hold one word: it wraps as a unit, is drawn with nothing between the pieces,
/// and a space is added only between words.
/// </summary>
public sealed partial class RunBoundaryTests
{
    private static readonly string Regular = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");
    private static readonly string Bold = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Bold.ttf");

    private static TextStyle Helvetica(double size = 12) =>
        new() { Font = Standard14.Helvetica, FontSize = size };

    private static TextStyle HelveticaBold(double size = 12) =>
        new() { Font = Standard14.HelveticaBold, FontSize = size };

    private static TextStyle Courier() =>
        new() { Font = Standard14.Courier, FontSize = 10 };

    private static (TextStyle R, TextStyle B) Embedded(Document doc)
    {
        var r = doc.LoadTrueTypeFont(Regular);
        var b = doc.LoadTrueTypeFont(Bold);
        return (new TextStyle { FontRef = new FontReference(r), FontSize = 12 },
            new TextStyle { FontRef = new FontReference(b), FontSize = 12 });
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Content(Document doc) => PdfTestUtil.DecompressAllFlatStreams(Save(doc));

    /// <summary>A page whose content width is exactly <paramref name="width"/> (no margins).</summary>
    private static Document Page(double width) => new()
    {
        PageSize = new PdfRectangle(0, 0, width, 400),
        Margins = EdgeInsets.Zero,
    };

    // ── Reading the content stream back ──────────────────────────────────────

    private readonly record struct Show(double X, double Y, string Token);

    private static List<Show> Shows(string content)
    {
        var result = new List<Show>();
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
                result.Add(new Show(x, y, m.Groups["tok"].Value));
            }
        }

        return result;
    }

    /// <summary>The show strings of each baseline, top to bottom.</summary>
    private static List<string[]> Lines(string content)
    {
        var lines = new List<string[]>();
        var current = new List<string>();
        double? lastY = null;
        foreach (var s in Shows(content))
        {
            if (lastY is not null && s.Y != lastY)
            {
                lines.Add([.. current]);
                current.Clear();
            }

            current.Add(s.Token);
            lastY = s.Y;
        }

        if (current.Count > 0)
            lines.Add([.. current]);
        return lines;
    }

    /// <summary>The show strings by baseline row, an empty array for a row with none.</summary>
    private static string[][] Grid(string content, double leading)
    {
        var shows = Shows(content);
        var rows = new SortedDictionary<int, List<string>>();
        foreach (var s in shows)
        {
            var i = (int)Math.Round((shows[0].Y - s.Y) / leading);
            if (!rows.TryGetValue(i, out var row))
                rows[i] = row = [];
            row.Add(s.Token);
        }

        return [.. Enumerable.Range(0, rows.Keys.Max() + 1).Select(i => rows.TryGetValue(i, out var r) ? r.ToArray() : [])];
    }

    private static string Hex(EmbeddedFontHandle handle, string text)
    {
        var gids = new ushort[text.Length];
        var n = handle.GetGlyphIds(text, gids);
        return "<" + string.Concat(gids.Take(n).Select(g => g.ToString("X4", CultureInfo.InvariantCulture))) + ">";
    }

    private static string Lit(string text) =>
        "(" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("(", "\\(", StringComparison.Ordinal)
            .Replace(")", "\\)", StringComparison.Ordinal) + ")";

    private static void AssertNear(double expected, double actual, string what) =>
        Assert.True(Math.Abs(expected - actual) < 2e-5, $"{what}: expected {expected}, got {actual}");

    [GeneratedRegex(@"(?m)^(?:1 0 0 1 (?<x>-?\d+(?:\.\d+)?) (?<y>-?\d+(?:\.\d+)?) Tm|(?<tok>\((?:[^()\\]|\\.)*\)|<[0-9A-F]*>) Tj)\r?$")]
    private static partial Regex ShowEvent();

    [GeneratedRegex(@"(?m)^(-?\d+(?:\.\d+)?) Tw\r?$")]
    private static partial Regex WordSpacing();

    [GeneratedRegex(@"/Rect \[(-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?) (-?\d+(?:\.\d+)?)\]")]
    private static partial Regex LinkRect();

    // ── The issue's paragraph ────────────────────────────────────────────────

    private static readonly string[] IssueTexts =
        ["Open ", "Instellingen", ". Daarna astro", "logie", " en (", "vet", ")."];

    private static Paragraph IssueParagraph(TextStyle r, TextStyle b) => new(
        IssueTexts.Select((t, i) => new TextRun(t, i % 2 == 0 ? r : b)));

    // The text a reader sees: the space before "en" belongs to the fragment that ends "logie".
    private static readonly string[] IssueShown =
        ["Open ", "Instellingen", ". Daarna astro", "logie ", "en (", "vet", ")."];

    private const string IssueExtracted = "Open Instellingen. Daarna astrologie en (vet).";

    [Fact]
    public void Issue_standard14_drawsEachShowStringWithTheSpaceAtTheEndOfTheFragmentBeforeIt()
    {
        var (r, b) = (Helvetica(), HelveticaBold());
        using var doc = new Document();
        doc.Add(IssueParagraph(r, b));

        var shows = Shows(Content(doc));

        Assert.Equal(IssueShown.Select(Lit), shows.Select(s => s.Token));
        var styles = new[] { r, b, r, b, r, b, r };
        for (var i = 0; i + 1 < shows.Count; i++)
            AssertNear(shows[i].X + styles[i].MeasureString(IssueShown[i]), shows[i + 1].X, $"x of fragment {i + 1}");
    }

    [Fact]
    public void Issue_embedded_drawsTheSameFragmentsAsGlyphStrings()
    {
        using var doc = new Document();
        var (r, b) = Embedded(doc);
        doc.Add(IssueParagraph(r, b));

        var shows = Shows(Content(doc));

        var handles = new[] { r.FontRef.Embedded, b.FontRef.Embedded };
        Assert.Equal(
            IssueShown.Select((t, i) => Hex(handles[i % 2], t)),
            shows.Select(s => s.Token));
        var styles = new[] { r, b, r, b, r, b, r };
        for (var i = 0; i + 1 < shows.Count; i++)
            AssertNear(shows[i].X + styles[i].MeasureString(IssueShown[i]), shows[i + 1].X, $"x of fragment {i + 1}");
    }

    [Fact]
    public void Issue_extractsWithoutTheInsertedSpaces_pdftotext()
    {
        using var dir = new TempDir();
        using (var doc = new Document())
        {
            doc.Add(IssueParagraph(Helvetica(), HelveticaBold()));
            File.WriteAllBytes(dir.Path("h14.pdf"), Save(doc));
        }

        using (var doc = new Document())
        {
            var (r, b) = Embedded(doc);
            doc.Add(IssueParagraph(r, b));
            File.WriteAllBytes(dir.Path("emb.pdf"), Save(doc));
        }

        foreach (var name in new[] { "h14.pdf", "emb.pdf" })
        {
            ExternalTool.TryRun("pdftotext", ["-raw", dir.Path(name), "-"], out _, out var text, out var stderr, out _);
            Assert.True(IssueExtracted == text.Trim(), $"{name}: got '{text.Trim()}'\nstderr: {stderr}");
        }
    }

    [Fact]
    public void Issue_extractsWithoutTheInsertedSpaces_reader()
    {
        using var doc = new Document();
        doc.Add(IssueParagraph(Helvetica(), HelveticaBold()));

        using var reader = PdfReader.Open(Save(doc));

        Assert.Equal(IssueExtracted, reader.GetPage(0).ExtractText().Text.Trim());
    }

    // ── Style instances ──────────────────────────────────────────────────────

    [Fact]
    public void SameInstance_drawsAndMeasuresAsTheSingleRun()
    {
        var r = Helvetica();
        string Joined(Paragraph p)
        {
            using var doc = new Document();
            doc.Add(p);
            return Content(doc);
        }

        var single = Joined(new Paragraph("astrologie", r));
        var twoRuns = Joined(new Paragraph([new TextRun("astro", r), new TextRun("logie", r)]));
        var added = Joined(new Paragraph("astro", r).Add("logie"));

        Assert.Contains("(astrologie) Tj", single);
        Assert.Equal(single, twoRuns);
        Assert.Equal(single, added);
    }

    [Fact]
    public void EqualValueInstances_sharePositionsButDrawTwoFragments()
    {
        var r = Helvetica();
        var r2 = Helvetica();

        using var one = new Document();
        one.Add(new Paragraph([new TextRun("astro", r), new TextRun("logie", r)]));
        using var two = new Document();
        two.Add(new Paragraph([new TextRun("astro", r), new TextRun("logie", r2)]));
        var oneContent = Content(one);
        var twoContent = Content(two);

        var twoShows = Shows(twoContent);
        Assert.Equal(["(astro)", "(logie)"], twoShows.Select(s => s.Token));
        AssertNear(twoShows[0].X + r.MeasureString("astro"), twoShows[1].X, "x of the second fragment");
        Assert.Equal(["(astrologie)"], Shows(oneContent).Select(s => s.Token));

        // Equal values are not merged: a different instance is a fragment of its own. This pins the
        // rule so that nobody turns the merge test into value equality without a decision.
        Assert.NotEqual(oneContent, twoContent);
        Assert.Equal(Shows(oneContent)[0].Y, twoShows[0].Y);
        Assert.Equal(Shows(oneContent)[0].X, twoShows[0].X);
    }

    // ── Wrapping ─────────────────────────────────────────────────────────────

    [Fact]
    public void AGluedWord_wrapsAsOneUnit()
    {
        var r = Helvetica();
        var b = HelveticaBold();
        const string Lead = "aaaaaaaaaaaaaaaaaaaaaa";

        // "astro" fits beside the lead word, "astrologie" does not.
        var width = r.MeasureString(Lead + " astro") + 0.5;
        Assert.True(r.MeasureString(Lead + " astro") + b.MeasureString("logie") > width);
        using var doc = Page(width);
        doc.Add(new Paragraph([new TextRun(Lead + " astro", r), new TextRun("logie", b), new TextRun(" end", r)]));

        var lines = Lines(Content(doc));

        Assert.Equal(2, lines.Count);
        Assert.Equal([Lit(Lead)], lines[0]);
        Assert.Equal([Lit("astro"), Lit("logie "), Lit("end")], lines[1]);
    }

    // ── What keeps its bytes: the boundaries that hold white space ───────────

    public static TheoryData<string[], string[][]> WhiteSpaceBoundaries() => new()
    {
        // The expected lines were recorded from the build before this change, which draws them
        // as it did on 2.3.3. Runs alternate between two style instances, R then B then R.
        { ["a ", "b"], [["(a )", "(b)"]] },
        { ["a", " b"], [["(a )", "(b)"]] },
        { ["a", " ", "b"], [["(a b)"]] },
        { ["a", "\tb"], [["(a )", "(b)"]] },
        { ["a", " b"], [["(a )", "(b)"]] },
        { ["a", "  ", "b"], [["(a b)"]] },
        { ["a\n", "b"], [["(a)"], ["(b)"]] },
        { ["a", "\nb"], [["(a)"], ["(b)"]] },
        { ["a", "\n", "b"], [["(a)"], ["(b)"]] },
        { ["a\r", "\nb"], [["(a)"], [], ["(b)"]] },
        { ["a\r\n", "b"], [["(a)"], ["(b)"]] },
        { ["a", "\r", "b"], [["(a)"], ["(b)"]] },
        { ["a\n\n", "b"], [["(a)"], [], ["(b)"]] },
        { ["a", "", "\n", "b"], [["(a)"], ["(b)"]] },
    };

    [Theory]
    [MemberData(nameof(WhiteSpaceBoundaries))]
    public void ABoundaryWithWhiteSpaceOrALineBreak_isDrawnAsBefore(string[] runs, string[][] expected) =>
        AssertDrawnAsBefore(runs, expected, HelveticaBold());

    /// <summary>The same rows with a second instance whose values equal the first's: it is still a fragment of its own.</summary>
    [Theory]
    [MemberData(nameof(WhiteSpaceBoundaries))]
    public void ABoundaryWithWhiteSpaceOrALineBreak_isDrawnAsBefore_whenTheStylesAreEqualCopies(string[] runs, string[][] expected) =>
        AssertDrawnAsBefore(runs, expected, Helvetica());

    private static void AssertDrawnAsBefore(string[] runs, string[][] expected, TextStyle second)
    {
        var first = Helvetica();
        using var doc = new Document();
        doc.Add(new Paragraph(runs.Select((t, i) => new TextRun(t, i % 2 == 0 ? first : second))));

        // An empty line draws nothing, so the grid places each show string on its baseline row and
        // an empty row stands for a gap.
        Assert.Equal(expected, Grid(Content(doc), first.EffectiveLeading));
    }

    [Fact]
    public void ANullStyledWhiteSpaceRunBetweenWords_stillSeparatesThem()
    {
        var (r, b) = (Helvetica(), HelveticaBold());
        using var doc = new Document();
        doc.Add(new Paragraph([new TextRun("a", r), new TextRun(" ", null!), new TextRun("b", b)]));

        Assert.Equal([["(a )", "(b)"]], Lines(Content(doc)).Select(l => l.ToArray()).ToArray());
    }

    // ── What changes ─────────────────────────────────────────────────────────

    [Fact]
    public void AnEmptyRun_adds_nothingToTheWordAroundIt()
    {
        var (r, b) = (Helvetica(), HelveticaBold());
        using var doc = new Document();
        doc.Add(new Paragraph([new TextRun("a", r), new TextRun(string.Empty, b), new TextRun("b", r)]));

        Assert.Equal(["(ab)"], Shows(Content(doc)).Select(s => s.Token));
    }

    [Theory]
    [InlineData("a ", "b")]
    [InlineData("a", " b")]
    [InlineData("a ", "b")]
    public void ANoBreakSpaceAtTheBoundary_isACharacterOfTheWord(string first, string second)
    {
        var r = Helvetica();
        using var doc = new Document();
        doc.Add(new Paragraph([new TextRun(first, r), new TextRun(second, r)]));

        var shows = Shows(Content(doc));

        // One word, one show string, and no U+0020 in it: the space the old tokenising added is gone.
        Assert.Single(shows);
        Assert.DoesNotContain(' ', shows[0].Token);
    }

    [Fact]
    public void AGluedWordWiderThanTheLine_isCutAcrossTheStyleChange()
    {
        // Courier is 600 units wide, so a 60 pt line holds exactly ten glyphs at 10 pt.
        var c = Courier();
        var c2 = Courier();
        using var doc = Page(60);
        doc.Add(new Paragraph([new TextRun("aaaaaa", c), new TextRun("bbbbbbbbbbbb", c2)]));

        var shows = Shows(Content(doc));

        Assert.Equal(["(aaaaaa)", "(bbbb)", "(bbbbbbbb)"], shows.Select(s => s.Token));
        Assert.Equal(shows[0].X + 36, shows[1].X);
        Assert.Equal(shows[0].Y, shows[1].Y);
        Assert.True(shows[2].Y < shows[1].Y);
        Assert.Equal(shows[0].X, shows[2].X);
    }

    [Fact]
    public void AOneStyleWordWiderThanTheLine_isCutAsBefore()
    {
        var c = Courier();
        using var doc = Page(60);
        doc.Add(new Paragraph("aaaaaaaaaaaaaaa b c", c));
        doc.Add(new Paragraph("aaaaaaaaaaaaaaa\nb", c));

        var content = Content(doc);
        var lines = Lines(content);

        // The second paragraph shows the empty line a break right after a cut word adds.
        Assert.Equal(
            [["(aaaaaaaaaa)"], ["(aaaaa)"], ["(b c)"], ["(aaaaaaaaaa)"], ["(aaaaa)"], ["(b)"]],
            lines.Select(l => l.ToArray()).ToArray());
        var ys = Shows(content).Select(s => s.Y).Distinct().ToList();
        var leading = ys[0] - ys[1];
        Assert.Equal(ys[4] - ys[5], 2 * leading, 4);
    }

    // ── Justification ────────────────────────────────────────────────────────

    private static Paragraph JustifiedParagraph(TextStyle r, TextStyle b) => new(
    [
        new TextRun("Lorem ipsum dolor sit amet, astro", r),
        new TextRun("logie", b),
        new TextRun(" consectetur adipiscing elit, sed do eiusmod tempor", r),
    ])
    { Alignment = HorizontalAlignment.Justify };

    [Fact]
    public void Justified_standard14_widensTheSpacesAndNotTheGluedBoundary()
    {
        var (r, b) = (Helvetica(), HelveticaBold());
        var lineWidth = r.MeasureString("Lorem ipsum dolor sit amet, astro") + b.MeasureString("logie");
        const double Slack = 25;
        using var doc = Page(lineWidth + Slack);
        doc.Add(JustifiedParagraph(r, b));

        var content = Content(doc);
        var lines = Lines(content);
        var shows = Shows(content);

        // The first line ends at "astrologie": the five spaces share the slack, the boundary gets none.
        Assert.Equal([Lit("Lorem ipsum dolor sit amet, astro"), Lit("logie")], lines[0]);
        var tw = double.Parse(WordSpacing().Match(content).Groups[1].Value, CultureInfo.InvariantCulture);
        AssertNear(Slack / 5, tw, "Tw");
        AssertNear(r.MeasureString("Lorem ipsum dolor sit amet, astro") + (5 * Slack / 5), shows[1].X, "x of logie");
        AssertNear(lineWidth + Slack, shows[1].X + b.MeasureString("logie"), "right edge of the line");
    }

    [Fact]
    public void Justified_embedded_startsTheGluedTokenWhereThePreviousOneEnds()
    {
        using var doc0 = new Document();
        var (r0, b0) = Embedded(doc0);
        var lineWidth = r0.MeasureString("Lorem ipsum dolor sit amet, astro") + b0.MeasureString("logie");
        const double Slack = 25;

        using var doc = Page(lineWidth + Slack);
        var (r, b) = Embedded(doc);
        doc.Add(JustifiedParagraph(r, b));
        var shows = Shows(Content(doc));

        var astro = Hex(r.FontRef.Embedded, "astro");
        var logie = Hex(b.FontRef.Embedded, "logie");
        var iAstro = shows.FindIndex(s => s.Token == astro);
        var iLogie = shows.FindIndex(s => s.Token == logie);
        Assert.True(iAstro >= 0 && iLogie == iAstro + 1, "the two pieces are drawn one after the other");
        AssertNear(shows[iAstro].X + r.MeasureString("astro"), shows[iLogie].X, "x of logie");
        AssertNear(lineWidth + Slack, shows[iLogie].X + b.MeasureString("logie"), "right edge of the line");
    }

    // ── Links ────────────────────────────────────────────────────────────────

    [Fact]
    public void ALinkRunInsideAWord_coversItsOwnTextOnly()
    {
        var r = Helvetica();
        var l = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, LinkUri = "https://example.com/" };
        using var doc = new Document();
        doc.Add(new Paragraph([new TextRun("Zie ", r), new TextRun("example", l), new TextRun(".com", r)]));

        var pdf = System.Text.Encoding.Latin1.GetString(Save(doc));
        var rect = LinkRect().Match(pdf);

        Assert.True(rect.Success);
        var x1 = double.Parse(rect.Groups[1].Value, CultureInfo.InvariantCulture);
        var x2 = double.Parse(rect.Groups[3].Value, CultureInfo.InvariantCulture);
        AssertNear(72 + r.MeasureString("Zie "), x1, "left edge");
        AssertNear(l.MeasureString("example"), x2 - x1, "width");
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Fact]
    public void ANullStyledRunThatContributesACharacter_isStillRefusedWhenGlued()
    {
        var r = Helvetica();
        using var doc = new Document();
        doc.Add(new Paragraph([new TextRun("a", r), new TextRun("x", null!), new TextRun("b", r)]));

        var ex = Assert.Throws<ArgumentNullException>(() => Save(doc));

        Assert.Equal("Style", ex.ParamName);
    }

    // ── How each line ended ──────────────────────────────────────────────────

    private static (string[] Texts, LineEnd[] Ends) Wrap(double width, params TextRun[] runs)
    {
        var (lines, ends) = ParagraphRenderer.WordWrap(runs, width);
        return ([.. lines.Select(l => string.Concat(l.Select(f => f.Text)))], [.. ends]);
    }

    [Fact]
    public void LineEnds_recordSoftHardForcedAndLast()
    {
        var c = Courier();

        var (texts, ends) = Wrap(60, new TextRun("aaaa bbbb cc\nzzzzzzzzzzzzzzzzzzzzzzzzz end", c));

        Assert.Equal(["aaaa bbbb", "cc", "zzzzzzzzzz", "zzzzzzzzzz", "zzzzz", "end"], texts);
        Assert.Equal([LineEnd.Soft, LineEnd.Hard, LineEnd.Forced, LineEnd.Forced, LineEnd.Soft, LineEnd.Last], ends);
    }

    [Fact]
    public void LineEnds_theLastPieceOfACutWordEndsWhereTheWordDoes()
    {
        var c = Courier();

        var (texts, ends) = Wrap(60, new TextRun("zzzzzzzzzzzzzzz\nb", c));

        // The break after a cut word adds an empty line, as before.
        Assert.Equal(["zzzzzzzzzz", "zzzzz", string.Empty, "b"], texts);
        Assert.Equal([LineEnd.Forced, LineEnd.Hard, LineEnd.Hard, LineEnd.Last], ends);

        (texts, ends) = Wrap(60, new TextRun("zzzzzzzzzzzzzzz", c));
        Assert.Equal(["zzzzzzzzzz", "zzzzz"], texts);
        Assert.Equal([LineEnd.Forced, LineEnd.Last], ends);
    }

    [Fact]
    public void LineEnds_aTrailingBreakEndsTheLastLineAsHard_andNoTextIsOneLast()
    {
        var c = Courier();

        var (texts, ends) = Wrap(60, new TextRun("a\n", c));
        Assert.Equal(["a"], texts);
        Assert.Equal([LineEnd.Hard], ends);

        (texts, ends) = Wrap(60, new TextRun(string.Empty, c));
        Assert.Equal([string.Empty], texts);
        Assert.Equal([LineEnd.Last], ends);
    }

    [Fact]
    public void LineEnds_reachBothHalvesOfASplitParagraph()
    {
        var c = Courier();
        var renderer = new ParagraphRenderer(new Paragraph("aaaa bbbb cc dddd eeee ffff", c));

        // Ten glyphs a line, 12 pt leading: room for two of the three lines.
        var result = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 60, 24)));

        Assert.Equal(LayoutResult.Outcome.Partial, result.Status);
        var split = (ParagraphRenderer)result.SplitRenderer!;
        var overflow = (ParagraphRenderer)result.OverflowRenderer!;
        Assert.Equal([LineEnd.Soft, LineEnd.Soft, LineEnd.Last], renderer.LineEnds);
        Assert.Equal(renderer.LineEnds, split.LineEnds);
        Assert.Equal(renderer.LineEnds, overflow.LineEnds);
    }

    // ── Oracles ──────────────────────────────────────────────────────────────

    [Fact]
    public void EmbeddedIssue_qpdfChecksClean_andVeraPdfAcceptsPdfA2u()
    {
        using var dir = new TempDir();
        var path = dir.Path("issue-emb.pdf");
        using (var doc = new Document { Conformance = PdfConformance.PdfA2u })
        {
            doc.Info.Title = "Run boundary";
            var (r, b) = Embedded(doc);
            doc.Add(IssueParagraph(r, b));
            var bytes = Save(doc);

            // The change is in the file before any oracle is asked about it.
            var shows = Shows(PdfTestUtil.DecompressAllFlatStreams(bytes));
            Assert.Equal(Hex(r.FontRef.Embedded, "Instellingen"), shows[1].Token);
            Assert.Equal(Hex(r.FontRef.Embedded, ". Daarna astro"), shows[2].Token);
            File.WriteAllBytes(path, bytes);
        }

        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var veraExit, out var report, out var veraErr, out var veraTimedOut, timeoutMs: 120_000);
        Assert.False(veraTimedOut);
        Assert.True(veraExit is 0 or 1, $"veraPDF exit {veraExit}: {VeraPdfExitCode.Describe(veraExit)}\n{report}\n{veraErr}");
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
    }

    private sealed class TempDir : IDisposable
    {
        private readonly string _root = Directory.CreateTempSubdirectory("vellum-runs-").FullName;

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }
}
