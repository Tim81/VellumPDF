// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>The continuation marker, what the wrap reports, and how a block moves across pages (#591).</summary>
public sealed class PreformattedWrapTests
{
    private static string Digits(int n) => string.Concat(Enumerable.Range(0, n).Select(i => (char)('0' + i % 10)));

    private static readonly string[] MarkerOperators =
    [
        "q", "0 0 0 RG", "1 J", "1 j", "[] 0 d", "0.6 w",
        "520.8 765.89 m", "520.8 761.89 l", "517.5 761.89 l", "S",
        "518.7 763.39 m", "517.5 761.89 l", "518.7 760.39 l", "S",
        "Q",
    ];

    [Fact]
    public void AWrappedLine_isFollowedByAVectorMarker_atTheSegmentsRightEnd()
    {
        using var doc = new Document();
        doc.Add(new Preformatted(Digits(76), Courier()));

        var content = Assert.Single(PageContents(Save(doc)));

        // 74 columns of 6 pt from the 72 pt margin end at 516; the first line's baseline is 759.89.
        var afterText = content[(content.IndexOf("ET\n", StringComparison.Ordinal) + 3)..];
        Assert.Equal(string.Join('\n', MarkerOperators) + "\n", afterText);
    }

    [Fact]
    public void TheMarker_isAnArtifactOutsideTheTaggedParagraph_inATaggedDocument()
    {
        using var doc = new Document { Tagged = true, Language = "en-US" };
        doc.Info.Title = "t";
        doc.Add(new Preformatted(Digits(76), Courier()));

        var content = Assert.Single(PageContents(Save(doc)));

        var expected =
            "/P <</MCID 0>> BDC\nBT\n/F1 10 Tf\n0 0 0 rg\n1 0 0 1 72 759.89 Tm\n(" + Digits(76)[..74] + ") Tj\n" +
            "1 0 0 1 72 747.89 Tm\n(" + Digits(76)[74..] + ") Tj\nET\nEMC\n/Artifact BMC\n" +
            string.Join('\n', MarkerOperators) + "\nEMC\n";
        Assert.Equal(expected, content);
    }

    [Fact]
    public void TheWrapIsReported_onceForTheElement_asAWarning_andStrictModeSaves()
    {
        using var doc = new Document { StrictDiagnostics = true };
        doc.Add(new Preformatted(string.Join('\n', Digits(75), Digits(76), Digits(149), Digits(200)), Courier()));

        Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.PreformattedLineWrapped, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Warning, item.Severity);
        Assert.Equal(LayoutElementKind.Preformatted, item.ElementKind);
        Assert.Equal(0, item.ElementIndex);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(3, item.Count);
        Assert.Null(item.Clause);
        Assert.Equal(
            "3 line(s) wider than the content width of 451.28 pt were wrapped and marked as continued.",
            item.Message);
    }

    [Fact]
    public void NoWrap_reportsNothing()
    {
        using var doc = new Document();
        doc.Add(new Preformatted(Digits(75), Courier()));

        Save(doc);

        Assert.Empty(doc.Diagnostics);
    }

    // ── Where the wrap breaks ────────────────────────────────────────────────

    /// <summary>Courier measures the U+0301 as the question mark it draws, so the cluster is 12 pt wide and does not fit the 74th column; a break by scalar would split it from its letter.</summary>
    [Fact]
    public void TheBreak_fallsBeforeACluster_notInsideIt()
    {
        var combining = new string('a', 73) + "e\u0301" + new string('b', 10);
        var pair = new string('a', 73) + char.ConvertFromUtf32(0x1F600) + new string('b', 10);

        var first = PreformattedText.Layout(new Preformatted(combining, Courier()), 451.28, out _);
        var second = PreformattedText.Layout(new Preformatted(pair, Courier()), 451.28, out _);

        Assert.Equal(
            [new PreformattedLine(new string('a', 73), true), new PreformattedLine("e\u0301" + new string('b', 10), false)],
            first);
        Assert.Equal(
            [new PreformattedLine(new string('a', 73), true), new PreformattedLine(char.ConvertFromUtf32(0x1F600) + new string('b', 10), false)],
            second);
    }

    /// <summary>A box of 5 pt cannot hold a cluster and the marker, so each segment is one cluster and the marker lies past the box.</summary>
    [Fact]
    public void ABoxNarrowerThanAClusterAndTheMarker_takesOneClusterPerSegment()
    {
        var lines = PreformattedText.Layout(new Preformatted("abc", Courier()), 5, out var wrapped);

        Assert.Equal(
            [new PreformattedLine("a", true), new PreformattedLine("b", true), new PreformattedLine("c", false)],
            lines);
        Assert.Equal(1, wrapped);

        using var doc = new Document();
        doc.Add(new Preformatted("abc", Courier()) { Margins = new EdgeInsets(0, 223.14, 0, 223.14) });
        var content = Assert.Single(PageContents(Save(doc)));

        // The box is 5 pt wide from x 295.14; the first marker starts at 301.14, past its right edge.
        Assert.Contains("\n305.94 765.89 m\n", content, StringComparison.Ordinal);
        Assert.Equal(["a", "b", "c"], Literals(content));
    }

    // ── Pages ─────────────────────────────────────────────────────────────────

    private static (List<string> Pages, PreformattedRenderer Block, DocumentRenderer Renderer) TwoHundredLines(RunningBand? footer)
    {
        var page = new PdfRectangle(0, 0, 595.28, 841.89);
        var renderer = new DocumentRenderer(NewPdf(), page, new EdgeInsets(72)) { Footer = footer };
        var block = new PreformattedRenderer(new Preformatted(string.Join('\n', Enumerable.Range(1, 200).Select(i => $"line {i}")), Courier()));
        renderer.Add(block);
        return (PageContents(Render(renderer)), block, renderer);
    }

    private static int[] LinesPerPage(List<string> pages) =>
        [.. pages.Select(c => Literals(c).Count(l => l.StartsWith("line ", StringComparison.Ordinal)))];

    [Fact]
    public void TwoHundredLines_fillPagesOf58_andTheWrapRunsOnce()
    {
        var (pages, block, renderer) = TwoHundredLines(null);

        Assert.Equal([58, 58, 58, 26], LinesPerPage(pages));
        Assert.Equal(1, block.WrapCalls);
        Assert.Equal(0, renderer.MeasureRuns);
        Assert.Equal("759.89", TmYs(pages[0])[0]);
        Assert.Equal("75.89", TmYs(pages[0])[57]);
    }

    /// <summary>The footer takes room from every page, so the split moves; the measuring pass still runs once and the wrap is still cached.</summary>
    [Fact]
    public void WithAFooter_thePagesHoldFewerLines_theMeasuringPassRunsOnce_andTheWrapRunsOnce()
    {
        var (pages, block, renderer) = TwoHundredLines(new RunningBand("{page}/{pages}"));

        Assert.Equal([56, 56, 56, 32], LinesPerPage(pages));
        Assert.Equal(1, block.WrapCalls);
        Assert.Equal(1, renderer.MeasureRuns);
        Assert.Contains("(1/4) Tj", pages[0], StringComparison.Ordinal);
        Assert.Contains("(4/4) Tj", pages[3], StringComparison.Ordinal);
    }

    [Fact]
    public void AWrappedLine_thatStraddlesAPageEnd_hasItsMarkerOnTheFirstPage_andIsReportedOnce()
    {
        using var doc = new Document();
        var lines = Enumerable.Range(1, 57).Select(i => $"line {i}").Append(Digits(149));
        doc.Add(new Preformatted(string.Join('\n', lines), Courier()));

        var contents = PageContents(Save(doc));

        Assert.Equal(2, contents.Count);
        Assert.Equal(Digits(149)[..74], Literals(contents[0])[^1]);
        Assert.Equal(Digits(149)[74..], Literals(contents[1])[0]);
        Assert.Single(Regex.Matches(contents[0], @"^0\.6 w$", RegexOptions.Multiline));
        Assert.DoesNotContain("0.6 w", contents[1], StringComparison.Ordinal);
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(1, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    [Fact]
    public void AZeroWidthBox_laysOutAsNothing()
    {
        var renderer = new PreformattedRenderer(new Preformatted("x", Courier()));

        var result = renderer.Layout(new LayoutContext(new LayoutBox(0, 0, 0, 100)));

        Assert.Equal(LayoutResult.Outcome.Nothing, result.Status);
    }

    [Fact]
    public void BreakBefore_startsANewPage_andIsHonouredOnlyForTheElement()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("before"));
        doc.Add(new Preformatted("block", Courier()) { BreakBefore = PageBreakMode.Page });

        var contents = PageContents(Save(doc));

        Assert.Equal(2, contents.Count);
        Assert.Equal(["block"], Literals(contents[1]));
    }

    // ── Show strings ──────────────────────────────────────────────────────────

    /// <summary>A line of zero-advance glyphs is one cluster, one visual line, shown in runs of 4,096 glyphs with no positioning between them.</summary>
    [Fact]
    public void ALongShowString_isCutIntoRunsOf4096Scalars_withNoTmBetween()
    {
        using var doc = new Document();
        doc.Add(new Preformatted("a" + new string('\u0301', 10_000), MonoStyle(doc)));

        var content = Assert.Single(PageContents(Save(doc)));

        var runs = HexStrings(content);
        Assert.Equal([4096, 4096, 1809], runs.Select(r => (r.Length - 2) / 4));
        Assert.Single(Regex.Matches(content, @"^1 0 0 1 \S+ \S+ Tm$", RegexOptions.Multiline));
        Assert.Equal(HexOf(MonoPath, "a" + new string('\u0301', 10_000)), "<" + string.Concat(runs.Select(r => r[1..^1])) + ">");
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void AnExpandedTabAfterAZeroWidthCluster_keepsEveryScalar()
    {
        var marks = new string('\u0301', 9000);
        using var doc = new Document();
        doc.Add(new Preformatted("x" + marks + "	y", MonoStyle(doc)));

        var content = Assert.Single(PageContents(Save(doc)));

        Assert.Equal(
            HexOf(MonoPath, "x" + marks + Spaces(7) + "y")[1..^1],
            string.Concat(HexStrings(content).Select(r => r[1..^1])));
    }
}
