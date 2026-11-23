// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Reader;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <c>Preformatted.Anchor</c>: the named destination of a code block. A page is 300 x 300 pt with
/// 20 pt margins, so the content box is 260 pt high and holds 21 lines of the default 12 pt
/// leading. Expected positions are worked out from that geometry, not read back from the file.
/// </summary>
public sealed class PreformattedAnchorTests
{
    private const double Leading = 12;

    private static readonly double TopOfPage = Page.Height - Margins.Top;

    private static Document NewDocument() => new() { PageSize = Page, Margins = Margins };

    private static string Lines(int count) => string.Join('\n', Enumerable.Range(1, count).Select(i => $"line {i}"));

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private sealed record Dest(int Page, double Left, double Top);

    // The name tree and the destinations it names, read from the file text. Page numbers are
    // 0-based and follow the page tree's /Kids order.
    private static Dictionary<string, Dest> Dests(byte[] pdf)
    {
        var text = Regex.Replace(System.Text.Encoding.Latin1.GetString(pdf), @"\s+", " ");
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

    [Fact]
    public void Anchor_isNullByDefault_andHoldsWhatWasSet()
    {
        Assert.Null(new Preformatted("x").Anchor);
        Assert.Equal("code-1", new Preformatted("x") { Anchor = "code-1" }.Anchor);
        var all = "AZaz09._-:" + new string('k', 117);
        Assert.Equal(all, new Preformatted("x") { Anchor = all }.Anchor);
    }

    [Fact]
    public void BlockAtTheTopOfTheDocument_isANamedDestinationAtTheTopOfTheContentBox()
    {
        using var doc = NewDocument();
        doc.Add(new Preformatted("a\n\tb") { Anchor = "block" });

        var dests = Dests(Save(doc));

        Assert.Equal(["block"], dests.Keys);
        Assert.Equal(new Dest(0, 0, TopOfPage), dests["block"]);
    }

    [Fact]
    public void BlockAfterAFillerLine_isBelowThatLine()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("filler", Body));
        doc.Add(new Preformatted("target") { Anchor = "target" });

        var dest = Dests(Save(doc))["target"];

        Assert.Equal(0, dest.Page);
        Assert.Equal(TopOfPage - Body.EffectiveLeading, dest.Top, 4);
    }

    [Fact]
    public void BlockWithATopMargin_isAnchoredBelowTheMargin()
    {
        using var doc = NewDocument();
        doc.Add(new Preformatted("target") { Margins = new EdgeInsets(7, 0, 0, 0), Anchor = "target" });

        var dest = Dests(Save(doc))["target"];

        Assert.Equal(TopOfPage - 7, dest.Top, 4);
    }

    [Fact]
    public void BlockThatSplits_isAnchoredOnceOnThePageOfItsFirstLine()
    {
        // Three lines of filler leave 18 lines on page 0; 50 lines run on to a third page.
        const int Filler = 3;
        static Document Build()
        {
            var doc = NewDocument();
            doc.Add(new Preformatted(Lines(Filler)));
            doc.Add(new Preformatted(Lines(50)) { Anchor = "long" });
            return doc;
        }

        using (var measured = Build())
        {
            var renderer = measured.CreateRenderer();
            renderer.RunLayout();
            var reported = renderer.DrawPlan!.Placements.Where(p => p.Anchor == "long").ToList();
            Assert.Equal(0, Assert.Single(reported).PageIndex);
            Assert.Equal(Margins.Top + (Filler * Leading), reported[0].Y, 4);
        }

        using var doc = Build();
        var pdf = Save(doc);
        using var reader = PdfReader.Open(pdf);
        Assert.Equal(3, reader.PageCount);
        var dests = Dests(pdf);
        Assert.Equal(["long"], dests.Keys);
        Assert.Equal(new Dest(0, 0, TopOfPage - (Filler * Leading)), dests["long"]);
    }

    [Fact]
    public void BlockAnchor_isReportedUnderTheElement_soAPlanTargetFindsIt()
    {
        // A page reference resolves a target by the element it was given, so a report keyed by
        // anything else, the renderer for one, would leave the element without a page.
        var block = new Preformatted(Lines(50)) { Anchor = "long" };
        using var doc = NewDocument();
        doc.Add(new Preformatted(Lines(3)));
        doc.Add(block);

        var renderer = doc.CreateRenderer();
        renderer.RunLayout();

        var reported = Assert.Single(renderer.DrawPlan!.Placements, p => p.Anchor == "long");
        Assert.Same(block, reported.Key);
        Assert.True(renderer.DrawPlan.TryGetTarget(block, out var page, out var y));
        Assert.Equal(0, page);
        Assert.Equal(Margins.Top + (3 * Leading), y, 4);
    }

    [Fact]
    public void LabelledPageBreak_followedOnlyByABlock_saves()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("front", Body));
        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman) });
        doc.Add(new Preformatted("code") { Anchor = "code" });

        var pdf = Save(doc);

        Assert.Equal(new Dest(1, 0, TopOfPage), Dests(pdf)["code"]);
    }

    [Fact]
    public void BlockThatStartsOnALaterPage_isAnchoredOnThatPage()
    {
        using var doc = NewDocument();
        doc.Add(new Preformatted(Lines(21)));
        doc.Add(new Preformatted("second") { Anchor = "second" });

        var dest = Dests(Save(doc))["second"];

        Assert.Equal(new Dest(1, 0, TopOfPage), dest);
    }

    [Fact]
    public void BlockWithBreakBefore_isAnchoredAtTheTopOfTheNewPage()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("front", Body));
        doc.Add(new Preformatted("code") { BreakBefore = PageBreakMode.Page, Anchor = "code" });

        Assert.Equal(new Dest(1, 0, TopOfPage), Dests(Save(doc))["code"]);
    }

    [Fact]
    public void Anchor_refusesTheNamesAParagraphRefuses_withTheSameTypeAndMessage()
    {
        foreach (var bad in new[] { "", "a b", "é", "a/b", "a#b", "a(b", new string('x', 128), "\uD800" })
        {
            var expected = Assert.Throws<ArgumentException>(() => new Paragraph("x", Body) { Anchor = bad });
            var actual = Assert.Throws<ArgumentException>(() => new Preformatted("x") { Anchor = bad });
            Assert.Equal(expected.GetType(), actual.GetType());
            Assert.Equal(expected.ParamName, actual.ParamName);
            Assert.Equal(expected.Message, actual.Message);
        }
    }

    [Fact]
    public void RepeatedAnchor_acrossAParagraphAndABlock_isRefusedBeforeLayout_namingBoth()
    {
        using var doc = NewDocument();
        doc.Add(new Paragraph("a", Body) { Anchor = "same" });
        doc.Add(new Preformatted("b") { Anchor = "same" });

        var first = Assert.Throws<InvalidOperationException>(() => Save(doc));
        var second = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("\"same\"", first.Message, StringComparison.Ordinal);
        Assert.Contains("Paragraph 0 and Preformatted 1", first.Message, StringComparison.Ordinal);
        Assert.Equal(first.Message, second.Message);
        Assert.DoesNotContain("cannot be saved again", second.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedAnchor_acrossABlockAndAList_isRefusedBeforeLayout()
    {
        using var doc = NewDocument();
        doc.Add(new Preformatted("b") { Anchor = "same" });
        var list = new ListElement();
        list.Add(new ListItem("x", Body) { Anchor = "same" });
        doc.Add(list);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("Preformatted 0 and List 1 (item 0)", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SameBlockAddedTwice_holdsItsAnchorTwice()
    {
        using var doc = NewDocument();
        var block = new Preformatted("a") { Anchor = "once" };
        doc.Add(block);
        doc.Add(block);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("Preformatted 0 and Preformatted 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlockAnchor_pdfinfoResolvesTheNameToThePageAndTopWeComputed()
    {
        using var doc = NewDocument();
        doc.Add(new Preformatted(Lines(21)));
        doc.Add(new Preformatted(Lines(2)) { Anchor = "listing" });
        var path = Path.Combine(Path.GetTempPath(), "vellum_preanchor_" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            File.WriteAllBytes(path, Save(doc));
            var only = Assert.Single(PdfinfoDests(path));
            Assert.Equal(2, only.Page);
            Assert.Equal(TopOfPage, only.Top, 4);
            Assert.Equal("listing", only.Name);
        }
        finally
        {
            try { File.Delete(path); }
            catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
