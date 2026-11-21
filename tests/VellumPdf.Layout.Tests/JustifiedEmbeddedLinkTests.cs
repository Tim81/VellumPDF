// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The rectangle of a <see cref="TextStyle.LinkUri"/> link on a line in an embedded font. The drawn
/// positions are read back from the content stream (the <c>Tm</c> in front of every shown glyph
/// run), so the expected rectangle never comes from the code that computes it: it starts at the x
/// of the linked run's first glyph and ends at the x of the text that follows it on the line, or,
/// when the linked text closes the line, where its last glyph ends.
/// </summary>
public sealed class JustifiedEmbeddedLinkTests
{
    private const double Left = 50;
    private const double Right = 250;
    private const string Uri = "https://a.example";

    private static readonly string Liberation = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");

    /// <summary>
    /// One shown run: its text matrix origin and either its glyph ids in hex or its literal.
    /// </summary>
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

    private static List<Shown> Runs(byte[] pdf) =>
        [.. Regex.Matches(
            PdfTestUtil.DecompressAllFlatStreams(pdf),
            @"(?m)^1 0 0 1 (?<x>-?[\d.]+) (?<y>-?[\d.]+) Tm\n(?:<(?<hex>[0-9A-F]+)>|\((?<lit>[^)]*)\)) Tj$")
            .Select(m => new Shown(Num(m.Groups["x"].Value), Num(m.Groups["y"].Value),
                m.Groups["hex"].Success ? m.Groups["hex"].Value : m.Groups["lit"].Value))];

    private static List<double[]> Rects(byte[] pdf) =>
        [.. Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Rect \[([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+)\]")
            .Select(m => new[] { Num(m.Groups[1].Value), Num(m.Groups[2].Value), Num(m.Groups[3].Value), Num(m.Groups[4].Value) })];

    /// <summary>The glyph run the embedded path shows for <paramref name="text"/>.</summary>
    private static string Hex(FontReference font, string text)
    {
        var ids = new ushort[text.Length];
        var n = font.Embedded.GetGlyphIds(text, ids);
        return string.Concat(ids.Take(n).Select(g => g.ToString("X4", CultureInfo.InvariantCulture)));
    }

    private static Shown First(List<Shown> runs, string key) => runs.First(r => r.Key == key);

    private static TextStyle Embedded(FontReference font, string? uri = null) =>
        new() { FontRef = font, FontSize = 12, LinkUri = uri };

    private static TextStyle Helvetica(string? uri = null) =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 12, LinkUri = uri };

    // Three runs, the middle one a link of two words: every word on line one sits at a stretched x.
    private const string Lead = "aa bb cc ";
    private const string Linked = "dd ee";
    private const string Tail = " ff gg hh ii jj kk ll mm nn oo pp";

    private static Paragraph MidLine(TextStyle plain, TextStyle link, HorizontalAlignment alignment) =>
        new([new TextRun(Lead, plain), new TextRun(Linked, link), new TextRun(Tail, plain)]) { Alignment = alignment };

    // ── Justified, embedded ───────────────────────────────────────────────────

    /// <summary>
    /// The rectangle of a link in the middle of a stretched line starts at the linked word's
    /// drawn x and ends where the next word is drawn, the stretched gap after the closing space
    /// included.
    /// </summary>
    [Fact]
    public void MidLine_rectangleRunsFromTheLinkedGlyphToTheNextRunsGlyph()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        doc.Add(MidLine(Embedded(font), Embedded(font, Uri), HorizontalAlignment.Justify));
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var dd = First(runs, Hex(font, "dd"));
        var next = First(runs, Hex(font, "ff"));
        var rect = Assert.Single(Rects(pdf));

        // The change is in the fixture: the word is drawn well right of where an unstretched line
        // puts it.
        var unstretched = Left + Embedded(font).MeasureString(Lead);
        Assert.True(dd.X > unstretched + 0.5, $"dd at {dd.X}, unstretched {unstretched}");

        Assert.Equal(dd.X, rect[0], 0.001);
        Assert.Equal(next.X, rect[2], 0.001);
        Assert.Equal(dd.Y - 2.4, rect[1], 0.001);
        Assert.Equal(dd.Y + 12, rect[3], 0.001);
    }

    /// <summary>
    /// A link that closes a stretched line ends at the right edge of the box, where the line ends.
    /// </summary>
    [Fact]
    public void LineEnd_rectangleEndsAtTheRightEdge()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Paragraph(
        [
            new TextRun("aa bb cc dd ee ff gg hh ", plain),
            new TextRun("ii jj", Embedded(font, Uri)),
            new TextRun("\nnext line", plain),
        ])
        { Alignment = HorizontalAlignment.Justify });
        var pdf = Save(doc);

        var ii = First(Runs(pdf), Hex(font, "ii"));
        var rect = Assert.Single(Rects(pdf));

        Assert.Equal(ii.X, rect[0], 0.001);
        Assert.Equal(Right, rect[2], 0.01);
    }

    /// <summary>
    /// A justified line that is not the last of its paragraph and holds one word has no gap to
    /// stretch, so its link ends where its own last glyph ends, not at the right edge of the box.
    /// </summary>
    [Fact]
    public void NonLastLineWithoutAGap_rectangleEndsAtTheLastGlyph()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        const string Word = "mmmmmmmmmmmm";
        doc.Add(new Paragraph(
        [
            new TextRun(Word, Embedded(font, Uri)),
            new TextRun(" nnnnnnnnnnnn", plain),
        ])
        { Alignment = HorizontalAlignment.Justify });
        var pdf = Save(doc);

        var word = First(Runs(pdf), Hex(font, Word));
        var rect = Assert.Single(Rects(pdf));

        // The fixture holds a second line, and the first line is narrower than the box.
        var end = word.X + plain.MeasureString(Word);
        Assert.True(end < Right - 10, $"the word ends at {end}");
        Assert.Equal(word.X, rect[0], 0.001);
        Assert.Equal(end, rect[2], 0.001);
    }

    /// <summary>A link that opens a stretched line starts at the left edge.</summary>
    [Fact]
    public void LineStart_rectangleStartsAtTheLeftEdge()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Paragraph(
        [
            new TextRun("aa bb", Embedded(font, Uri)),
            new TextRun(" cc dd ee ff gg hh ii jj kk ll mm nn oo", plain),
        ])
        { Alignment = HorizontalAlignment.Justify });
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var rect = Assert.Single(Rects(pdf));

        Assert.Equal(Left, rect[0], 0.001);
        Assert.Equal(First(runs, Hex(font, "cc")).X, rect[2], 0.001);
    }

    /// <summary>
    /// The offset grows with the gaps before a link, so two links on one line each follow their own
    /// text.
    /// </summary>
    [Fact]
    public void TwoLinksOnOneLine_eachFollowsItsOwnText()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Paragraph(
        [
            new TextRun("aa ", Embedded(font, Uri)),
            new TextRun("bb cc ", plain),
            new TextRun("dd", Embedded(font, "https://b.example")),
            new TextRun(" ee ff gg hh ii jj kk ll mm nn oo pp", plain),
        ])
        { Alignment = HorizontalAlignment.Justify });
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var rects = Rects(pdf);
        Assert.Equal(2, rects.Count);

        Assert.Equal(Left, rects[0][0], 0.001);
        Assert.Equal(First(runs, Hex(font, "bb")).X, rects[0][2], 0.001);
        Assert.Equal(First(runs, Hex(font, "dd")).X, rects[1][0], 0.001);
        Assert.Equal(First(runs, Hex(font, "ee")).X, rects[1][2], 0.001);
        Assert.True(rects[1][0] > rects[0][2]);
    }

    /// <summary>
    /// A standard-14 link on a justified line that also holds an embedded run goes through the same
    /// path: its rectangle starts at the literal's drawn x and ends at the next run's glyph.
    /// </summary>
    [Fact]
    public void MixedLine_standard14LinkBesideEmbeddedRuns()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        doc.Add(MidLine(Embedded(font), Helvetica(Uri), HorizontalAlignment.Justify));
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var dd = First(runs, "dd");
        var rect = Assert.Single(Rects(pdf));

        Assert.Equal(dd.X, rect[0], 0.001);
        Assert.Equal(First(runs, Hex(font, "ff")).X, rect[2], 0.001);
        Assert.True(dd.X > Left + Embedded(font).MeasureString(Lead) + 0.5);
    }

    /// <summary>
    /// The embedded face is metric-compatible with Helvetica, so a stretched line in either
    /// gives the same rectangle; the two paths place and size it by one rule.
    /// </summary>
    [Fact]
    public void LiberationSansAndHelvetica_giveTheSameRectangle()
    {
        using var embeddedDoc = Page();
        var font = embeddedDoc.LoadTrueTypeFont(Liberation);
        embeddedDoc.Add(MidLine(Embedded(font), Embedded(font, Uri), HorizontalAlignment.Justify));
        var embedded = Assert.Single(Rects(Save(embeddedDoc)));

        using var standardDoc = Page();
        standardDoc.Add(MidLine(Helvetica(), Helvetica(Uri), HorizontalAlignment.Justify));
        var standard = Assert.Single(Rects(Save(standardDoc)));

        for (var i = 0; i < 4; i++)
            Assert.Equal(standard[i], embedded[i], 0.05);
    }

    /// <summary>
    /// A standard-14 link that closes a stretched line also ends at the right edge.
    /// </summary>
    [Fact]
    public void Standard14LineEnd_rectangleEndsAtTheRightEdge()
    {
        using var doc = Page();
        doc.Add(new Paragraph(
        [
            new TextRun("aa bb cc dd ee ff gg hh ", Helvetica()),
            new TextRun("ii jj", Helvetica(Uri)),
            new TextRun("\nnext line", Helvetica()),
        ])
        { Alignment = HorizontalAlignment.Justify });
        var pdf = Save(doc);

        var rect = Assert.Single(Rects(pdf));
        Assert.Equal(First(Runs(pdf), "ii jj").X, rect[0], 0.001);
        Assert.Equal(Right, rect[2], 0.01);
    }

    // ── Heading ───────────────────────────────────────────────────────────────

    /// <summary>A justified heading built from runs is a paragraph for its links.</summary>
    [Fact]
    public void JustifiedHeading_rectangleFollowsTheDrawnText()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Heading([new TextRun(Lead, plain), new TextRun(Linked, Embedded(font, Uri)), new TextRun(Tail, plain)])
        {
            Alignment = HorizontalAlignment.Justify,
        });
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var rect = Assert.Single(Rects(pdf));
        var dd = First(runs, Hex(font, "dd"));

        Assert.True(dd.X > Left + plain.MeasureString(Lead) + 0.5);
        Assert.Equal(dd.X, rect[0], 0.001);
        Assert.Equal(First(runs, Hex(font, "ff")).X, rect[2], 0.001);
    }

    // ── Lines that are not stretched ─────────────────────────────────────────

    /// <summary>
    /// Left, centre and right lines draw each run in one piece, so the rectangle is the linked
    /// run's drawn x to the next run's drawn x.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Left)]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void NotJustified_rectangleIsTheRunsOwnDrawnExtent(HorizontalAlignment alignment)
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Paragraph([new TextRun("aa bb cc ", plain), new TextRun("dd ee", Embedded(font, Uri)), new TextRun(" ff gg", plain)])
        {
            Alignment = alignment,
        });
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var rect = Assert.Single(Rects(pdf));

        Assert.Equal(3, runs.Count);
        Assert.Equal(runs[1].X, rect[0], 0.001);
        Assert.Equal(runs[2].X, rect[2], 0.001);
        Assert.Equal(runs[0].X + plain.MeasureString("aa bb cc "), runs[1].X, 0.001);

        var width = plain.MeasureString("aa bb cc dd ee ff gg");
        var expectedLeft = alignment switch
        {
            HorizontalAlignment.Center => Left + ((200 - width) / 2),
            HorizontalAlignment.Right => Right - width,
            _ => Left,
        };
        Assert.Equal(expectedLeft, runs[0].X, 0.001);
    }

    /// <summary>
    /// The last line of a justified paragraph is not stretched, and its link is drawn as it is.
    /// </summary>
    [Fact]
    public void LastLineOfAJustifiedParagraph_isNotStretched()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Paragraph([new TextRun("aa bb ", plain), new TextRun("cc dd", Embedded(font, Uri)), new TextRun(" ee", plain)])
        {
            Alignment = HorizontalAlignment.Justify,
        });
        var pdf = Save(doc);

        var runs = Runs(pdf);
        var rect = Assert.Single(Rects(pdf));

        Assert.Equal(Left + plain.MeasureString("aa bb "), rect[0], 0.001);
        Assert.Equal(rect[0], runs[1].X, 0.001);
        Assert.Equal(runs[2].X, rect[2], 0.001);
    }

    /// <summary>A link in an item of a list sits where the item draws it.</summary>
    [Fact]
    public void ListItem_rectangleOfTheLinkedRunFollowsItsDrawnText()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = plain }
            .Add(new ListItem([new TextRun("see ", plain), new TextRun("the page", Embedded(font, Uri)), new TextRun(" now", plain)])));
        var pdf = Save(doc);

        var runs = Runs(pdf);
        // The run keeps the space that opens the next run, so its glyph run starts with the words.
        var link = runs.First(r => r.Key.StartsWith(Hex(font, "the page"), StringComparison.Ordinal));
        var after = runs[runs.IndexOf(link) + 1];
        var own = Rects(pdf).Single(r => Math.Abs(r[0] - link.X) < 0.001);

        Assert.Equal(after.X, own[2], 0.001);
        Assert.Equal(link.Y - 2.4, own[1], 0.001);
    }

    // ── A link run that draws nothing ─────────────────────────────────────────

    /// <summary>
    /// A tab with no stop to its right (a stop at 0 is not right of the pen) at the start of a line
    /// leaves a fragment of one space. In the second case a hard break closes the line there, so
    /// the space trims to nothing; in the first it stays, and the embedded path still draws no
    /// glyph run for it. A link on it has no drawn text to cover, so it gets no annotation, and the
    /// link on a run that is drawn keeps its rectangle.
    /// </summary>
    [Theory]
    [InlineData("aa bb cc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr")]
    [InlineData("\naa bb cc dd ee ff gg hh ii jj kk ll mm nn oo pp qq rr")]
    public void TabWithNoStopAtLineStart_linkOnItGetsNoRectangle(string after)
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        var plain = Embedded(font);
        doc.Add(new Paragraph(
        [
            new TextRun("\t", Embedded(font, Uri)),
            new TextRun(after, plain),
            new TextRun(" ss", Embedded(font, "https://b.example")),
        ])
        { Alignment = HorizontalAlignment.Justify, TabStops = [new TabStop(0)] });
        var pdf = Save(doc);

        // The fixture holds the change: the text is drawn, and the line is stretched, so a later
        // word sits right of where an unstretched line puts it.
        var runs = Runs(pdf);
        Assert.Contains(runs, r => r.Key == Hex(font, "aa"));
        var bb = First(runs, Hex(font, "bb"));
        var unstretched = Left + Embedded(font).MeasureString("aa ");
        Assert.True(bb.X > unstretched + 0.5, $"bb at {bb.X}, unstretched {unstretched}");

        var text = Encoding.Latin1.GetString(pdf);
        Assert.DoesNotContain("https://a.example", text, StringComparison.Ordinal);
        Assert.All(Rects(pdf), r => Assert.True(r[2] > r[0], string.Join(' ', r)));
    }

    // ── Oracles ───────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>pdftotext -bbox</c> puts the words of the link inside its rectangle and the words
    /// around it outside, on the stretched line, and qpdf finds the file well formed.
    /// </summary>
    [Fact]
    public void Oracles_pdftotextBoxesLieInsideTheRectangle_qpdfIsClean()
    {
        using var doc = Page();
        var font = doc.LoadTrueTypeFont(Liberation);
        doc.Add(MidLine(Embedded(font), Embedded(font, Uri), HorizontalAlignment.Justify));
        var pdf = Save(doc);
        var rect = Assert.Single(Rects(pdf));

        // The fixture holds the change: the rectangle is not where an unstretched line puts it.
        Assert.True(rect[0] > Left + Embedded(font).MeasureString(Lead) + 0.5);

        var dir = Path.Combine(Path.GetTempPath(), "vellum-justlink-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "link.pdf");
            File.WriteAllBytes(path, pdf);

            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, stdout + stderr);
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

            ExternalTool.TryRun("pdftotext", ["-bbox", path, "-"], out exit, out stdout, out stderr, out timedOut);
            Assert.False(timedOut);
            Assert.Equal(0, exit);

            // pdftotext measures y downwards from the top of the 400 pt page; the rectangle
            // upwards.
            var words = Regex.Matches(stdout, "<word xMin=\"([\\d.]+)\" yMin=\"([\\d.]+)\" xMax=\"([\\d.]+)\" yMax=\"([\\d.]+)\">(\\w+)</word>")
                .ToDictionary(m => m.Groups[5].Value, m => new[] { Num(m.Groups[1].Value), Num(m.Groups[2].Value), Num(m.Groups[3].Value), Num(m.Groups[4].Value) });

            foreach (var inside in new[] { "dd", "ee" })
            {
                var w = words[inside];
                Assert.True(w[0] >= rect[0] - 0.01 && w[2] <= rect[2] + 0.01, $"{inside} {string.Join(',', w)} not inside {string.Join(',', rect)}");
                Assert.True(400 - w[3] >= rect[1] - 0.5 && 400 - w[1] <= rect[3] + 0.5, $"{inside} outside the rectangle's height");
            }

            foreach (var outside in new[] { "cc", "ff" })
            {
                var w = words[outside];
                Assert.True(w[2] <= rect[0] + 0.01 || w[0] >= rect[2] - 0.01, $"{outside} {string.Join(',', w)} lies inside {string.Join(',', rect)}");
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
