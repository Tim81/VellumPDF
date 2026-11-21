// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The preformatted element (#591): lines, tab expansion, columns, the wrap and its marker,
/// pagination, tagging and the bounds. Every expected value is written out, not computed by the
/// code under test; glyph ids are read from the font through a document of its own.
/// </summary>
public sealed class PreformattedTests
{
    private const string Row3 = "\tingesprongen\tmet tabs\n    vier  spaties  dubbel";
    private static readonly string Row3Line1 = Spaces(8) + "ingesprongen" + Spaces(4) + "met tabs";
    private const string Row3Line2 = "    vier  spaties  dubbel";

    // ── Tabs and spaces, embedded and Standard-14 ────────────────────────────

    [Fact]
    public void LiberationMono_expandsTheTabs_andDrawsEveryGlyph600Wide()
    {
        using var doc = new Document();
        doc.Add(new Preformatted(Row3, MonoStyle(doc)));

        var pdf = Save(doc);
        var text = TextObject(Assert.Single(PageContents(pdf)));

        Assert.Equal(
            "BT\n/TT1 10 Tf\n0 0 0 rg\n" +
            $"1 0 0 1 72 759.89 Tm\n{HexOf(MonoPath, Row3Line1)} Tj\n" +
            $"1 0 0 1 72 747.89 Tm\n{HexOf(MonoPath, Row3Line2)} Tj\nET\n",
            text);

        // Every glyph drawn has an advance of 600 in the CIDFont /W array, and the space is mapped
        // to U+0020 in the ToUnicode map.
        var everything = Encoding.Latin1.GetString(pdf) + "\n" + PdfTestUtil.DecompressAllFlatStreams(pdf);
        var widths = new Dictionary<int, int>();
        foreach (Match run in Regex.Matches(Regex.Match(everything, @"/W \[(.*?)\]\s*>>", RegexOptions.Singleline).Groups[1].Value + "]", @"(\d+) \[([^\]]*)\]"))
        {
            var first = int.Parse(run.Groups[1].Value, CultureInfo.InvariantCulture);
            var each = run.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < each.Length; i++)
                widths[first + i] = int.Parse(each[i], CultureInfo.InvariantCulture);
        }

        var drawn = CodePoints(Row3Line1 + Row3Line2).Select(cp => (int)Gid(MonoPath, cp)).Distinct().ToArray();
        Assert.NotEmpty(drawn);
        Assert.All(drawn, gid => Assert.Equal(600, widths[gid]));
        Assert.Equal(3, Gid(MonoPath, ' '));
        Assert.Contains("<0003> <0020>", everything, StringComparison.Ordinal);
    }

    [Fact]
    public void Courier_isTheDefault_andWritesNoTabByte()
    {
        using var doc = new Document();
        doc.Add(new Preformatted(Row3));

        var pdf = Save(doc);
        var content = Assert.Single(PageContents(pdf));

        Assert.Equal(
            "BT\n/F1 10 Tf\n0 0 0 rg\n" +
            $"1 0 0 1 72 759.89 Tm\n({Row3Line1}) Tj\n" +
            $"1 0 0 1 72 747.89 Tm\n({Row3Line2}) Tj\nET\n",
            TextObject(content));
        Assert.DoesNotContain('\t', content);
        Assert.Equal(Row3Line1, Literals(content)[0]);
    }

    /// <summary>The spaces a tab adds depend on the columns before it, whatever the tab width.</summary>
    [Theory]
    [InlineData(4, 0, 4)]
    [InlineData(4, 3, 1)]
    [InlineData(4, 7, 1)]
    [InlineData(4, 8, 4)]
    [InlineData(4, 12, 4)]
    [InlineData(8, 0, 8)]
    [InlineData(8, 3, 5)]
    [InlineData(8, 7, 1)]
    [InlineData(8, 8, 8)]
    [InlineData(8, 12, 4)]
    [InlineData(1, 5, 1)]
    [InlineData(32, 31, 1)]
    [InlineData(32, 32, 32)]
    public void ATabAddsTheSpacesToTheNextStop(int tabWidth, int prefix, int spaces)
    {
        using var doc = new Document();
        doc.Add(new Preformatted(new string('x', prefix) + "\tY", Courier()) { TabWidth = tabWidth });

        var literal = Assert.Single(Literals(Assert.Single(PageContents(Save(doc)))));

        Assert.Equal(new string('x', prefix) + Spaces(spaces) + "Y", literal);
    }

    [Fact]
    public void AClusterIsOneColumn_whateverItsLength()
    {
        // e + combining acute is one cluster; the emoji is one scalar, two UTF-16 units.
        foreach (var cluster in new[] { "e\u0301", char.ConvertFromUtf32(0x1F600) })
        {
            using var doc = new Document();
            doc.Add(new Preformatted(cluster + "\tX", MonoStyle(doc)));

            var hex = Assert.Single(HexStrings(Assert.Single(PageContents(Save(doc)))));

            Assert.Equal(HexOf(MonoPath, cluster + Spaces(7) + "X"), hex);
        }
    }

    /// <summary>A tab counts a cluster as one column, so counting UTF-16 units would give 6 spaces here.</summary>
    [Fact]
    public void AClusterOfTwoUnits_doesNotShortenTheTab()
    {
        var lines = PreformattedText.Layout(new Preformatted("e\u0301\tX", Courier()), 1000, out _);

        Assert.Equal([new PreformattedLine("e\u0301" + Spaces(7) + "X", false)], lines);
    }

    // ── Lines ─────────────────────────────────────────────────────────────────

    public static TheoryData<string, string[]> LineCases() => new()
    {
        { "  a  b  ", ["  a  b  "] },
        { "   ", ["   "] },
        { "", [""] },
        { "a\n", ["a"] },
        { "a\n\n", ["a", ""] },
        { "\n\nx", ["", "", "x"] },
        { "a\r\nb", ["a", "b"] },
        { "a\rb", ["a", "b"] },
        { "a\r\n\r\nb\r", ["a", "", "b"] },
        { "a\n\rb", ["a", "", "b"] },
        { "a  \nb", ["a  ", "b"] },
    };

    [Theory]
    [MemberData(nameof(LineCases))]
    public void LinesEndWhereTheTextEndsThem_andNoSpaceIsLost(string text, string[] expected)
    {
        var lines = PreformattedText.Layout(new Preformatted(text, Courier()), 1000, out var wrapped);

        Assert.Equal(expected, lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.False(l.Continued));
        Assert.Equal(0, wrapped);
    }

    [Fact]
    public void ThreeLineEndForms_giveTheSameStream()
    {
        string Stream(string text)
        {
            using var doc = new Document();
            doc.Add(new Preformatted(text, Courier()));
            return Assert.Single(PageContents(Save(doc)));
        }

        var lf = Stream("one\n two\n  three");

        Assert.Equal(lf, Stream("one\r\n two\r\n  three"));
        Assert.Equal(lf, Stream("one\r two\r  three"));
        Assert.Equal(["one", " two", "  three"], Literals(lf));
    }

    [Fact]
    public void AnEmptyLine_takesItsLeading_andWritesNothing()
    {
        using var doc = new Document();
        doc.Add(new Preformatted("a\n\nb", Courier()));

        var content = Assert.Single(PageContents(Save(doc)));

        Assert.Equal(["759.89", "735.89"], TmYs(content));
        Assert.Equal(["a", "b"], Literals(content));
    }

    // ── Widths ────────────────────────────────────────────────────────────────

    [Fact]
    public void ALinesWidth_equalsMeasureString_bitForBit()
    {
        using var doc = new Document();
        var styles = new[]
        {
            Courier(),
            MonoStyle(doc, 9.5),
            new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 11.3 },
            new TextStyle { Font = Standard14.Helvetica, FontSize = 10 },
            new TextStyle { Font = Standard14.TimesBold, FontSize = 7.7 },
        };
        string[] fixtures = ["", "x", "  leading and trailing  ", "\tab\tcd\t", "e\u0301 e\u0301", "n\u00A0b\u202Fc", "The quick brown fox, 0123456789"];

        foreach (var style in styles)
        {
            foreach (var text in fixtures)
            {
                var element = new Preformatted(text, style);
                var line = Assert.Single(PreformattedText.Layout(element, 1e9, out _)).Text;
                var units = PreformattedText.LineUnits(style.FontRef, line);

                Assert.Equal(style.FontRef.MeasureString(line, style.FontSize), PreformattedText.Points(units, style.FontSize));
            }
        }
    }

    // ── Wrap ──────────────────────────────────────────────────────────────────

    private static string Digits(int n) => string.Concat(Enumerable.Range(0, n).Select(i => (char)('0' + i % 10)));

    [Fact]
    public void WideLines_wrapAtTheMarkerReserve_andEveryCharacterSurvives()
    {
        var sources = new[] { Digits(75), Digits(76), Digits(149), Digits(200) };
        using var doc = new Document();
        doc.Add(new Preformatted(string.Join('\n', sources), Courier()));

        var content = Assert.Single(PageContents(Save(doc)));

        // W is 451.28, so 75 columns (450) fit whole, and a segment holds 74 columns (444 + the 6
        // reserved for the marker).
        var expected = new[]
        {
            Digits(75),
            Digits(76)[..74], Digits(76)[74..],
            Digits(149)[..74], Digits(149)[74..],
            Digits(200)[..74], Digits(200)[74..148], Digits(200)[148..],
        };
        Assert.Equal(expected, Literals(content));
        Assert.Equal(sources[3], string.Concat(expected[5..]));
        Assert.Equal(4, Regex.Matches(content, @"^0\.6 w$", RegexOptions.Multiline).Count);
    }
}
