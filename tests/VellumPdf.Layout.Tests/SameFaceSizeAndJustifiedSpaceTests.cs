// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A run in the font of the run before it at another size selects that size, and a justified line
/// in an embedded font draws and advances every space its width counts. Everything is read back
/// from the content stream: the <c>Tf</c> operands, and the <c>Tm</c> in front of each shown run.
/// </summary>
public sealed class SameFaceSizeAndJustifiedSpaceTests
{
    private const double Left = 50;
    private const double Right = 250;
    private const string Words = "aa bb cc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr";
    private const string TwoTabs = "aa bb\t\tcc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr";

    private static readonly string Liberation = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    private sealed record Shown(double X, double Y, string Key);

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(50),
    };

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Stream(byte[] pdf) => PdfTestUtil.DecompressAllFlatStreams(pdf);

    private static List<string> TfSizes(byte[] pdf) =>
        [.. Regex.Matches(Stream(pdf), @"(?m)^/\S+ (?<size>[-\d.]+) Tf$").Select(m => m.Groups["size"].Value)];

    private static List<Shown> Runs(byte[] pdf) =>
        [.. Regex.Matches(
            Stream(pdf),
            @"(?m)^1 0 0 1 (?<x>-?[\d.]+) (?<y>-?[\d.]+) Tm\n(?:<(?<hex>[0-9A-F]+)>|\((?<lit>[^)]*)\)) Tj$")
            .Select(m => new Shown(Num(m.Groups["x"].Value), Num(m.Groups["y"].Value),
                m.Groups["hex"].Success ? m.Groups["hex"].Value : m.Groups["lit"].Value))];

    private static string Hex(FontReference font, string text)
    {
        var ids = new ushort[text.Length];
        var n = font.Embedded.GetGlyphIds(text, ids);
        return string.Concat(ids.Take(n).Select(g => g.ToString("X4", CultureInfo.InvariantCulture)));
    }

    private static TextStyle Std(double size) =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static TextStyle Emb(FontReference font, double size = 12) => new() { FontRef = font, FontSize = size };

    // ── Item 8: same face, another size ───────────────────────────────────────

    private static byte[] Sizes(bool embedded, params double[] sizes)
    {
        using var doc = Page();
        FontReference font = embedded ? doc.LoadTrueTypeFont(Liberation) : new FontReference(Standard14.Helvetica);
        var runs = sizes.Select((s, i) =>
            new TextRun(i == sizes.Length - 1 ? "x" : "x ", embedded ? Emb(font, s) : Std(s)));
        doc.Add(new Paragraph([.. runs]));
        return Save(doc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NineThenTwentyFour_writesTwoFontOperators(bool embedded) =>
        Assert.Equal(["9", "24"], TfSizes(Sizes(embedded, 9, 24)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARunBackAtTheFirstSize_writesAThirdFontOperator(bool embedded) =>
        Assert.Equal(["9", "24", "9"], TfSizes(Sizes(embedded, 9, 24, 9)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheSameSizeTwice_writesOneFontOperator(bool embedded) =>
        Assert.Equal(["12"], TfSizes(Sizes(embedded, 12, 12)));

    // 12 and 12.000001 both write 12, so the operator is compared as written.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASizeDifferenceBelowTheWrittenPrecision_writesOneFontOperator(bool embedded) =>
        Assert.Equal(["12"], TfSizes(Sizes(embedded, 12, 12.000001)));

    // 12 and 12.00001 differ in the fifth decimal, which is written.
    [Fact]
    public void ASizeDifferenceAtTheWrittenPrecision_writesTwoFontOperators() =>
        Assert.Equal(["12", "12.00001"], TfSizes(Sizes(false, 12, 12.00001)));

    [Fact]
    public void ARunInAnotherFaceAtTheSameSize_stillWritesAFontOperator()
    {
        using var doc = Page();
        doc.Add(new Paragraph([
            new TextRun("a ", Std(12)),
            new TextRun("b", new TextStyle { Font = Standard14.TimesRoman, FontSize = 12 })]));
        Assert.Equal(["12", "12"], TfSizes(Save(doc)));
    }

    [Fact]
    public void AHeadingBuiltFromRuns_selectsEachRunsSize()
    {
        using var doc = Page();
        doc.Add(new Heading([new TextRun("a ", Std(10)), new TextRun("b", Std(20))]));
        Assert.Equal(["10", "20"], TfSizes(Save(doc)));
    }

    // The run is drawn where the wrap measured it, at the size it was measured at.
    [Fact]
    public void TheSecondRunStartsAfterTheFirstRunsMeasuredWidth()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        doc.Add(new Paragraph([new TextRun("a ", Emb(font, 10)), new TextRun("b", Emb(font, 20))]));
        var pdf = Save(doc);
        Assert.Equal(["10", "20"], TfSizes(pdf));
        var b = Runs(pdf).First(r => r.Key == Hex(font, "b"));
        Assert.Equal(Left + Emb(font, 10).MeasureString("a "), b.X, 0.001);
    }

    // ── Items 36 and 37: every space drawn and advanced ───────────────────────

    private static Paragraph Justified(TextStyle style, params string[] texts) =>
        new([.. texts.Select(t => new TextRun(t, style))])
        {
            Alignment = HorizontalAlignment.Justify,
            TabStops = [new TabStop(0)],
        };

    private static (List<Shown> Line, FontReference Font) FirstLine(params string[] texts)
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        doc.Add(Justified(Emb(font), texts));
        var runs = Runs(Save(doc));
        var y = runs[0].Y;
        return ([.. runs.Where(r => Math.Abs(r.Y - y) < 0.001)], font);
    }

    private static double WidthOf(FontReference font, string key) =>
        Emb(font).MeasureString(key == Hex(font, " ") ? " " : Words.Split(' ').First(w => Hex(font, w) == key));

    private static void AssertEndsAtTheRightEdge(params string[] texts)
    {
        var (line, font) = FirstLine(texts);
        var last = line[^1];
        Assert.Equal(Right, last.X + WidthOf(font, last.Key), 0.001);
    }

    [Fact]
    public void ALeadingTabAtAStop_lineEndsAtTheRightEdge() => AssertEndsAtTheRightEdge("\t", Words);

    [Fact]
    public void ATabInTheRunOfTheWords_lineEndsAtTheRightEdge() => AssertEndsAtTheRightEdge("\t" + Words);

    [Fact]
    public void TwoLeadingTabs_lineEndsAtTheRightEdge() => AssertEndsAtTheRightEdge("\t\t" + Words);

    [Fact]
    public void TwoTabsBetweenWords_lineEndsAtTheRightEdge() => AssertEndsAtTheRightEdge(TwoTabs);

    [Fact]
    public void TwoTabsInTheirOwnRuns_lineEndsAtTheRightEdge() =>
        AssertEndsAtTheRightEdge("aa bb", "\t", "\tcc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr");

    [Fact]
    public void TheNoTabControl_lineEndsAtTheRightEdge() => AssertEndsAtTheRightEdge(Words);

    [Fact]
    public void ALeadingTab_isDrawnAtTheEdgeAndAdvanced()
    {
        var (line, font) = FirstLine("\t", Words);
        Assert.Equal(Hex(font, " "), line[0].Key);
        Assert.Equal(Left, line[0].X, 0.001);
        var aa = line.First(r => r.Key == Hex(font, "aa"));
        Assert.True(aa.X > Left + Emb(font).MeasureString(" ") - 0.001, $"aa at {aa.X}");
        for (var i = 1; i < line.Count; i++)
            Assert.True(line[i].X > line[i - 1].X);
    }

    // Two spaces between "bb" and "cc" are two shown spaces, the second after the first's advance.
    [Fact]
    public void TwoTabsBetweenWords_drawTwoSpaces()
    {
        var (line, font) = FirstLine(TwoTabs);
        var space = Hex(font, " ");
        var bb = line.FindIndex(r => r.Key == Hex(font, "bb"));
        var cc = line.FindIndex(r => r.Key == Hex(font, "cc"));
        Assert.Equal(3, cc - bb);
        Assert.Equal(space, line[bb + 1].Key);
        Assert.Equal(space, line[bb + 2].Key);
        Assert.True(line[bb + 2].X > line[bb + 1].X + Emb(font).MeasureString(" ") - 0.001);
    }

    // The stretch matches Tw: every space the line holds takes the same share of the slack, so the
    // word after a space sits one glyph width plus one share from the space's own x.
    [Fact]
    public void EverySpaceTakesTheSameShareOfTheSlack()
    {
        var (line, font) = FirstLine(TwoTabs);
        var spaces = line.Count(r => r.Key == Hex(font, " "));
        var share = (Right - Left - line.Sum(r => WidthOf(font, r.Key))) / spaces;
        var space = Emb(font).MeasureString(" ");

        // A space has half its share on each side: after the word before it, and after itself.
        // "aa", space, "bb":
        Assert.Equal(line[0].X + WidthOf(font, line[0].Key) + share / 2, line[1].X, 0.001);
        Assert.Equal(line[1].X + space + share / 2, line[2].X, 0.001);

        // Two spaces in a row: the first is followed by the second one whole share on, and the
        // second by "cc" half a share on, so the pair takes two shares, as Tw gives two spaces.
        var bb = line.FindIndex(r => r.Key == Hex(font, "bb"));
        Assert.Equal(line[bb].X + WidthOf(font, line[bb].Key) + share / 2, line[bb + 1].X, 0.001);
        Assert.Equal(line[bb + 1].X + space + share, line[bb + 2].X, 0.001);
        Assert.Equal(line[bb + 2].X + space + share / 2, line[bb + 3].X, 0.001);
    }

    [Fact]
    public void TheLeadingSpaceTakesTheSameShareAsAnyOther()
    {
        var (line, font) = FirstLine("\t", Words);
        var spaces = line.Count(r => r.Key == Hex(font, " "));
        var share = (Right - Left - line.Sum(r => WidthOf(font, r.Key))) / spaces;
        Assert.Equal(line[0].X + Emb(font).MeasureString(" ") + share, line[1].X, 0.001);
    }

    [Fact]
    public void TheStandard14Control_keepsTheLeadingSpaceInTheString()
    {
        using var doc = Page();
        doc.Add(Justified(Std(12), "\t", Words));
        Assert.Contains("( aa bb cc", Stream(Save(doc)));
    }

    // ── Oracles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// A PDF/A-2u document with a justified line that starts with a tab at a stop and holds two
    /// sizes of one font: veraPDF passes it, and <c>pdftotext -bbox</c> puts the last word of the
    /// first line at the right edge, where the content stream says it ends.
    /// </summary>
    [Fact]
    public void Oracles_veraPdfPasses_andPdftotextPutsTheLastWordAtTheRightEdge()
    {
        using var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        doc.PageSize = new PdfRectangle(0, 0, 300, 400);
        doc.Margins = new EdgeInsets(50);
        var font = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes);
        var small = new TextStyle { FontRef = font, FontSize = 10 };
        var big = new TextStyle { FontRef = font, FontSize = 12 };
        doc.Add(new Paragraph([
            new TextRun("\t", small),
            new TextRun("\t" + Words, big)])
        {
            Alignment = HorizontalAlignment.Justify,
            TabStops = [new TabStop(0)],
        });
        var pdf = Save(doc);

        // The fixture holds the change: both sizes are selected, and a leading space is drawn.
        Assert.Equal(["10", "12"], TfSizes(pdf).Distinct().ToList());
        Assert.Equal(Left, Runs(pdf)[0].X, 0.001);

        var report = FontReportSupport.VeraPdf("2u", pdf);
        Assert.Contains("isCompliant=" + (char)34 + "true" + (char)34, report, StringComparison.Ordinal);
        Assert.Empty(FontReportSupport.FailedRules(report));

        using var dir = new FontReportSupport.TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("pdftotext", ["-bbox", path, "-"], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        var words = Regex.Matches(stdout, "<word xMin=" + (char)34 + "([" + (char)92 + "d.]+)" + (char)34 + " yMin=" + (char)34 + "([" + (char)92 + "d.]+)" + (char)34 + " xMax=" + (char)34 + "([" + (char)92 + "d.]+)" + (char)34)
            .Select(m => (XMax: Num(m.Groups[3].Value), YMin: Num(m.Groups[2].Value))).ToList();
        var firstLineY = words.Min(w => w.YMin);
        var lastOnFirstLine = words.Where(w => Math.Abs(w.YMin - firstLineY) < 1).Max(w => w.XMax);
        Assert.Equal(Right, lastOnFirstLine, 0.6);
    }

    // A link on a line with a tab at a stop covers exactly the glyphs drawn for it.
    [Fact]
    public void ALinkAfterATabAtAStop_coversItsDrawnGlyphs()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var link = new TextStyle { FontRef = font, FontSize = 12, LinkUri = "https://a.example" };
        doc.Add(new Paragraph([
            new TextRun("\t", Emb(font)),
            new TextRun("aa bb", link),
            new TextRun(" cc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr", Emb(font))])
        {
            Alignment = HorizontalAlignment.Justify,
            TabStops = [new TabStop(0)],
        });
        var pdf = Save(doc);
        var runs = Runs(pdf);
        var rects = Regex.Matches(System.Text.Encoding.Latin1.GetString(pdf), @"/Rect \[([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+)\]")
            .Select(m => new[] { Num(m.Groups[1].Value), Num(m.Groups[3].Value) }).ToList();
        var rect = Assert.Single(rects);
        Assert.Equal(runs.First(r => r.Key == Hex(font, "aa")).X, rect[0], 0.001);
        // The space that closes the linked run is part of its rectangle, as on the Tw path.
        Assert.Equal(runs.First(r => r.Key == Hex(font, "cc")).X, rect[1], 0.001);
    }
}
