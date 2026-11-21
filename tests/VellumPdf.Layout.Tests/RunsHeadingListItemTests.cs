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
/// <see cref="Heading"/> and <see cref="ListItem"/> built from <see cref="TextRun"/>s (#367): one
/// structure element and one marked-content sequence per heading, the outline title, a link over
/// the linked run alone, the refusals, and what the external tools read back.
/// </summary>
public sealed class RunsHeadingListItemTests
{
    private static readonly string Regular = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Regular.ttf");
    private static readonly string Bold = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Bold.ttf");

    private static readonly TextStyle Plain14 = new() { Font = Standard14.Helvetica, FontSize = 14 };
    private static readonly TextStyle Bold14 = new() { Font = Standard14.HelveticaBold, FontSize = 14 };
    private static readonly TextStyle Plain12 = new() { Font = Standard14.Helvetica, FontSize = 12 };

    private static Document NewDocument(double width = 300) => new()
    {
        Tagged = true,
        PageSize = new PdfRectangle(0, 0, width, 300),
        Margins = EdgeInsets.Zero,
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Content(byte[] pdf) => PdfTestUtil.DecompressAllFlatStreams(pdf);

    private static int Count(string text, string needle) =>
        Regex.Count(text, Regex.Escape(needle), RegexOptions.CultureInvariant);

    // Outline titles are PDF text strings: UTF-16BE with a byte order mark, read here as Latin-1.
    private static string[] Titles(byte[] pdf) =>
        [.. Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Title \((.*?)\)\n", RegexOptions.Singleline)
            .Select(m => Encoding.BigEndianUnicode.GetString(Encoding.Latin1.GetBytes(m.Groups[1].Value)).TrimStart('﻿'))];

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private static Heading StyledHeading() =>
        new([new TextRun("De ", Plain14), new TextRun("Save", Bold14), new TextRun("-methode", Plain14)]);

    // ── Heading: structure, content stream, outline ──────────────────────────

    [Fact]
    public void Heading_runs_areOneMarkedContentSequenceWithTheFontSwitchBetweenThem()
    {
        var doc = NewDocument();
        doc.Add(StyledHeading());
        var content = Content(Save(doc));

        Assert.Equal(1, Count(content, "BDC"));
        Assert.Equal(1, Count(content, "EMC"));
        Assert.Contains("/H1 <</MCID 0>> BDC", content, StringComparison.Ordinal);

        // One BT..ET, with the regular font, then the bold one, then the regular one again.
        Assert.Matches(
            @"/F1 14 Tf\n0 0 0 rg\n1 0 0 1 0 286 Tm\n\(De \) Tj\n/F2 14 Tf\n1 0 0 1 [\d.]+ 286 Tm\n\(Save\) Tj\n/F1 14 Tf\n1 0 0 1 [\d.]+ 286 Tm\n\(-methode\) Tj\nET\nEMC",
            content);
        Assert.Equal(1, Count(content, "BT\n"));
    }

    [Fact]
    public void Heading_runs_makeOneStructureElementAndOneOutlineEntry()
    {
        var doc = NewDocument();
        doc.Add(StyledHeading());
        var pdf = Save(doc);

        Assert.Equal(1, Count(Encoding.Latin1.GetString(pdf), "/S /H1"));
        Assert.Equal(["De Save-methode"], Titles(pdf));
    }

    [Fact]
    public void Heading_runs_runsPositionsFollowTheMeasuredWidths()
    {
        var doc = NewDocument();
        doc.Add(StyledHeading());
        var content = Content(Save(doc));
        var xs = Regex.Matches(content, @"1 0 0 1 ([\d.]+) 286 Tm").Select(m => Num(m.Groups[1].Value)).ToArray();

        Assert.Equal([0, Plain14.MeasureString("De "), Plain14.MeasureString("De ") + Bold14.MeasureString("Save")],
            xs.Select(x => Math.Round(x, 3)).ToArray(),
            new ToleranceComparer(0.002));
    }

    private sealed class ToleranceComparer(double tolerance) : IEqualityComparer<double>
    {
        public bool Equals(double x, double y) => Math.Abs(x - y) <= tolerance;
        public int GetHashCode(double obj) => 0;
    }

    [Fact]
    public void Heading_bookmarkTitle_overridesTheConcatenatedText()
    {
        var doc = NewDocument();
        doc.Add(new Heading([new TextRun("De ", Plain14), new TextRun("Save", Bold14)]) { BookmarkTitle = "Methoden" });
        Assert.Equal(["Methoden"], Titles(Save(doc)));
    }

    [Fact]
    public void Heading_textStyleAndRuns_describeTheRuns()
    {
        var heading = StyledHeading();

        Assert.Equal("De Save-methode", heading.Text);
        Assert.Same(Plain14, heading.Style);
        Assert.Equal(["De ", "Save", "-methode"], heading.Runs.Select(r => r.Text));
    }

    [Fact]
    public void Heading_runs_areCopiedFromTheSequence()
    {
        var source = new List<TextRun> { new("a", Plain14) };
        var heading = new Heading(source);
        source.Add(new TextRun("b", Plain14));

        Assert.Equal("a", heading.Text);
        Assert.Single(heading.Runs);
    }

    [Fact]
    public void Heading_wordSplitBetweenStyles_drawsNoSpace()
    {
        var doc = NewDocument();
        doc.Add(new Heading([new TextRun("Bold", Bold14), new TextRun("er", Plain14)]));
        var content = Content(Save(doc));

        var m = Regex.Match(content, @"\(Bold\) Tj\n/F2 14 Tf\n1 0 0 1 ([\d.]+) 286 Tm\n\(er\) Tj");
        Assert.True(m.Success, content);
        var x = Num(m.Groups[1].Value);
        Assert.Equal(Bold14.MeasureString("Bold"), x, 3);
    }

    [Fact]
    public void Heading_runBuilt_splitAcrossPages_hasOneBookmark()
    {
        var doc = new Document { Tagged = true, PageSize = new PdfRectangle(0, 0, 100, 60), Margins = EdgeInsets.Zero };
        doc.Add(new Heading(Enumerable.Range(0, 30).Select(i => new TextRun($"word{i} ", i % 2 == 0 ? Plain14 : Bold14))));
        var pdf = Save(doc);

        Assert.True(Count(Encoding.Latin1.GetString(pdf), "/Type /Page\n") > 1, "the heading should span several pages");
        Assert.Single(Titles(pdf));
    }

    // ── ListItem ──────────────────────────────────────────────────────────────

    private static ListItem LinkItem(out TextStyle link)
    {
        link = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, LinkUri = "https://example.com/instellingen" };
        return new ListItem([new TextRun("Zie ", Plain12), new TextRun("Instellingen", link), new TextRun(".", Plain12)]);
    }

    [Fact]
    public void ListItem_linkRun_getsOneLinkWhoseRectIsTheRunsDrawnExtent()
    {
        var item = LinkItem(out var link);
        var doc = NewDocument();
        doc.Add(new ListElement().Add(item));
        var raw = Encoding.Latin1.GetString(Save(doc));

        Assert.Equal(1, Count(raw, "/Subtype /Link"));
        var m = Regex.Match(raw, @"/Rect \[([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)\]");
        Assert.True(m.Success, raw);
        var x0 = 20 + Plain12.MeasureString("Zie ");
        Assert.Equal(x0, Num(m.Groups[1].Value), 3);
        Assert.Equal(x0 + link.MeasureString("Instellingen"), Num(m.Groups[3].Value), 3);
    }

    [Fact]
    public void ListItem_runs_areOneContentSequenceAndTheMarkerKeepsTheItemStyle()
    {
        var doc = NewDocument();
        doc.Add(new ListElement().Add(new ListItem([new TextRun("Zie ", Plain12), new TextRun("vet", Bold14)]) { Style = Plain12 }));
        var content = Content(Save(doc));

        // Marker paragraph, then one paragraph holding both runs.
        Assert.Equal(2, Count(content, "BDC"));
        Assert.Matches(@"\(Zie \) Tj\n/F2 14 Tf\n1 0 0 1 [\d.]+ [\d.]+ Tm\n\(vet\) Tj", content);
    }

    [Fact]
    public void ListItem_itemStyleWithALink_linksTheMarkerAndNoRun()
    {
        var link = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, LinkUri = "https://example.com/m" };
        var doc = NewDocument();
        doc.Add(new ListElement().Add(new ListItem([new TextRun("Zie ", Plain12)]) { Style = link }));
        var raw = Encoding.Latin1.GetString(Save(doc));

        Assert.Equal(1, Count(raw, "/Subtype /Link"));
        var m = Regex.Match(raw, @"/Rect \[([\d.]+) ([\d.]+) ([\d.]+) ([\d.]+)\]");
        Assert.True(Num(m.Groups[3].Value) < 20, "the annotation covers the marker, left of the 20pt gutter");
    }

    [Fact]
    public void ListItem_wholeItemLink_keepsItsTwoAnnotations()
    {
        var link = new TextStyle { Font = Standard14.Helvetica, FontSize = 12, LinkUri = "https://example.com/w" };
        var doc = NewDocument();
        doc.Add(new ListElement().Add("Zie hier", link));
        var raw = Encoding.Latin1.GetString(Save(doc));

        Assert.Equal(2, Count(raw, "/Subtype /Link"));
    }

    [Fact]
    public void ListItem_textAndRuns_describeTheRuns()
    {
        var item = new ListItem([new TextRun("a", Plain12), new TextRun("b", Bold14)]);

        Assert.Equal("ab", item.Text);
        Assert.Null(item.Style);
        Assert.Equal(["a", "b"], item.Runs.Select(r => r.Text));
        Assert.Same(Bold14, item.Runs[1].Style);

        var text = new ListItem("x", Plain12);
        var run = Assert.Single(text.Runs);
        Assert.Equal("x", run.Text);
        Assert.Same(Plain12, run.Style);
        Assert.Same(TextStyle.Default, Assert.Single(new ListItem("x").Runs).Style);
    }

    [Fact]
    public void ListItem_childFromRuns_isDrawnWithItsRuns()
    {
        var doc = NewDocument();
        doc.Add(new ListElement().Add(new ListItem("Parent", Plain12).AddChild(
            new ListItem([new TextRun("kind ", Plain12), new TextRun("vet", Bold14)]))));
        var content = Content(Save(doc));

        Assert.Matches(@"\(kind \) Tj\n/F2 14 Tf\n1 0 0 1 [\d.]+ [\d.]+ Tm\n\(vet\) Tj", content);
    }

    // The gutter is widened for a long marker only while the widest word still fits next to it. A
    // word cut between two styles is one word, so its width is the sum of its pieces: here the
    // sum does not fit beside the widened gutter, so the indent stays, and either piece alone
    // would fit.
    [Fact]
    public void ListItem_gutterWidening_readsAWordThatContinuesOverARunBoundaryAsOne()
    {
        var wide = new TextStyle { Font = Standard14.Helvetica, FontSize = 10 };
        var wideBold = new TextStyle { Font = Standard14.HelveticaBold, FontSize = 10 };
        var marker = new ListElement(ListStyle.OrderedRoman).FormatMarker(27);
        var gutter = Math.Max(20, wide.MeasureString(marker));
        Assert.True(gutter > 20, "item 27 needs a widened gutter at this size");

        var sum = wide.MeasureString("Wide") + wideBold.MeasureString("word");
        var pieceMax = Math.Max(wide.MeasureString("Wide"), wideBold.MeasureString("word"));
        var width = 20 + sum + 0.5;
        Assert.True(width - gutter < sum && width - gutter >= pieceMax);

        var list = new ListElement(ListStyle.OrderedRoman) { DefaultStyle = wide };
        for (var i = 1; i < 27; i++)
            list.Add("a");
        list.Add(new ListItem([new TextRun("Wide", wide), new TextRun("word", wideBold)]));
        var doc = NewDocument(width);
        doc.Add(list);
        var content = Content(Save(doc));

        var x = Num(Regex.Match(content, @"1 0 0 1 ([\d.]+) [\d.]+ Tm\n\(Wide\) Tj").Groups[1].Value);
        Assert.Equal(20, x, 3);
    }

    // ── Refusals and the empty sequence ───────────────────────────────────────

    [Fact]
    public void Heading_refusesANullSequence()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new Heading((IEnumerable<TextRun>)null!));
        Assert.Equal("runs", ex.ParamName);
    }

    [Fact]
    public void Heading_refusesANullRun_namingTheIndex()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new Heading([new TextRun("a", Plain14), null!]));
        Assert.Equal("runs", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Heading_refusesARunWithANullStyle_namingTheIndex()
    {
        var ex = Assert.Throws<ArgumentException>(() => new Heading([new TextRun("a", Plain14), new TextRun("b", null!)]));
        Assert.IsNotType<ArgumentNullException>(ex);
        Assert.Equal("runs", ex.ParamName);
        Assert.Contains("index 1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Heading_refusesANullStyleEvenOnAnEmptyRun()
    {
        Assert.Throws<ArgumentException>(() => new Heading([new TextRun(string.Empty, null!)]));
    }

    [Fact]
    public void ListItem_refusesANullSequence_aNullRun_andANullStyle()
    {
        Assert.Equal("runs", Assert.Throws<ArgumentNullException>(() => new ListItem((IEnumerable<TextRun>)null!)).ParamName);

        var nullRun = Assert.Throws<ArgumentNullException>(() => new ListItem([null!]));
        Assert.Equal("runs", nullRun.ParamName);
        Assert.Contains("index 0", nullRun.Message, StringComparison.Ordinal);

        var nullStyle = Assert.Throws<ArgumentException>(() => new ListItem([new TextRun("a", Plain12), new TextRun("b", null!)]));
        Assert.IsNotType<ArgumentNullException>(nullStyle);
        Assert.Equal("runs", nullStyle.ParamName);
        Assert.Contains("index 1", nullStyle.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySequences_areAccepted_andSave()
    {
        var heading = new Heading(Array.Empty<TextRun>());
        var run = Assert.Single(heading.Runs);
        Assert.Equal(string.Empty, run.Text);
        Assert.Equal(14, heading.Style.FontSize);
        Assert.Equal(string.Empty, heading.Text);

        var item = new ListItem(Array.Empty<TextRun>());
        Assert.Equal(string.Empty, Assert.Single(item.Runs).Text);

        var doc = NewDocument();
        doc.Add(heading);
        doc.Add(new ListElement().Add(item));
        var raw = Encoding.Latin1.GetString(Save(doc));
        Assert.Equal(1, Count(raw, "/S /H1"));
        Assert.Equal(1, Count(raw, "/S /LI"));
    }

    [Fact]
    public void RunBuiltElements_stillRefuseARefusedStyleAtTheSave()
    {
        var doc = NewDocument();
        doc.Add(new Heading([new TextRun("a", Plain14), new TextRun("b", new TextStyle { FontSize = double.NaN })]));
        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    // ── Oracles ───────────────────────────────────────────────────────────────

    private static Document EmbeddedDocument()
    {
        var doc = new Document
        {
            Tagged = true,
            Conformance = PdfConformance.PdfA2u,
            Language = "nl-NL",
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        doc.Info.Title = "Runs";
        var regular = new TextStyle { FontRef = doc.LoadTrueTypeFont(Regular), FontSize = 14 };
        var bold = new TextStyle { FontRef = doc.LoadTrueTypeFont(Bold), FontSize = 14 };
        var link = new TextStyle { FontRef = regular.FontRef, FontSize = 14, LinkUri = "https://example.com/instellingen" };
        doc.Add(new Heading([new TextRun("De ", regular), new TextRun("Save", bold), new TextRun("-methode", regular)]));
        doc.Add(new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = regular }
            .Add(new ListItem([new TextRun("Zie ", regular), new TextRun("Instellingen", link), new TextRun(".", regular)])));
        return doc;
    }

    private static string WriteTemp(byte[] pdf, out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "vellum-runs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "runs.pdf");
        File.WriteAllBytes(path, pdf);
        return path;
    }

    [Fact]
    public void EmbeddedRuns_oracles_qpdfPdftotextVeraPdf()
    {
        var pdf = Save(EmbeddedDocument());

        // The fixture holds the change: the bold font switch and the link are in it.
        var content = Content(pdf);
        Assert.Matches(@"/(TT\d+) 14 Tf[\s\S]*?\n/(?!\1 )TT\d+ 14 Tf[\s\S]*?\n/\1 14 Tf", content);
        Assert.Equal(1, Count(Encoding.Latin1.GetString(pdf), "/Subtype /Link"));

        var path = WriteTemp(pdf, out var dir);
        try
        {
            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, stdout + stderr);
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

            ExternalTool.TryRun("pdftotext", ["-layout", path, "-"], out exit, out stdout, out stderr, out timedOut);
            Assert.False(timedOut);
            Assert.Equal(0, exit);
            var lines = stdout.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
            Assert.Equal("De Save-methode", lines[0]);
            Assert.Equal("1. Zie Instellingen.", lines[1]);

            ExternalTool.TryRun("verapdf", ["--flavour", "2u", path], out exit, out stdout, out stderr, out timedOut, timeoutMs: 120_000);
            Assert.False(timedOut);
            Assert.True(exit is 0 or 1, $"veraPDF exited {exit}\n{stdout}\n{stderr}");
            Assert.True(stdout.Contains("isCompliant=\"true\"", StringComparison.Ordinal), stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
