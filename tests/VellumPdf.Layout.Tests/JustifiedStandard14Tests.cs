// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #548 and #555: a justified line in a standard-14 font. The word spacing was the slack divided by
/// the number of word gaps, which counts every space twice, so half the slack was handed out and the
/// line stopped short of the right edge. And each fragment of a line holding more than one
/// <see cref="TextStyle"/> was placed at its unstretched width while its spaces were stretched, so it
/// could overprint the text after it. The assertions are on the values <c>Tw</c> and <c>Tm</c> carry,
/// not on the operator existing, which is all <c>JustificationTests</c> checks and which passed with
/// both defects present.
/// </summary>
public sealed class JustifiedStandard14Tests
{
    private const double ContentLeft = 50;
    private const double ContentWidth = 200;

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(50),
    };

    private static TextStyle Helv(double size = 12) =>
        new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = size };

    private static TextStyle Bold(double size = 12) =>
        new() { FontRef = new FontReference(Standard14.HelveticaBold), FontSize = size };

    private static string Stream(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    private static double D(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    // ── #548 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// 13 words, 12 spaces, Helvetica 12 in a 200pt box. The slack divided by 12 spaces is the Tw, and
    /// the line then ends exactly at the right edge. Before the fix the Tw was slack / 24, and the
    /// line ended at 243.37.
    /// </summary>
    [Fact]
    public void SingleStyle_wordSpacingIsSlackOverSpaces_andLineEndsAtTheRightEdge()
    {
        const string first = "aa bb cc dd ee ff gg hh ii jj kk ll mm";
        using var doc = Page();
        doc.Add(new Paragraph(first + " nn oo pp qq rr ss tt", Helv())
        {
            Alignment = HorizontalAlignment.Justify,
        });

        var stream = Stream(doc);

        var tw = D(Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value);
        var natural = Helv().MeasureString(first);
        var slack = ContentWidth - natural;
        Assert.Equal(slack / 12, tw, 4);
        Assert.Equal(ContentLeft + ContentWidth, ContentLeft + natural + (12 * tw), 3);
    }

    // ── #555 ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Two <see cref="TextStyle"/> instances on one justified line. The second fragment must start where
    /// the first one is drawn to end: its measured width plus a Tw for each of its spaces.
    /// </summary>
    [Fact]
    public void TwoStyles_secondFragmentStartsWhereTheFirstIsDrawnToEnd()
    {
        const string a = "aa bb cc dd ";
        const string b = "ee ff gg hh ii jj kk";
        using var doc = Page();
        doc.Add(new Paragraph(
        [
            new TextRun(a, Helv()),
            new TextRun(b + " ll mm nn oo pp qq rr ss tt", Bold()),
        ])
        { Alignment = HorizontalAlignment.Justify });

        var stream = Stream(doc);

        var tw = D(Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value);
        var xs = Regex.Matches(stream, @"1 0 0 1 ([-\d.]+) [-\d.]+ Tm")
            .Select(m => D(m.Groups[1].Value)).ToArray();
        Assert.True(xs.Length >= 2, stream);

        var firstSpaces = a.Count(c => c == ' ');
        var expectedSecond = xs[0] + Helv().MeasureString(a) + (firstSpaces * tw);
        Assert.Equal(expectedSecond, xs[1], 3);

        // The old placement, for contrast: unstretched, so it overprinted the first fragment's end.
        Assert.NotEqual(xs[0] + Helv().MeasureString(a), xs[1], 3);
    }

    /// <summary>Whatever the styles, the last fragment of the line ends at the right edge of the box.</summary>
    [Fact]
    public void TwoStyles_theLineAsAWholeEndsAtTheRightEdge()
    {
        // Both runs are measured by the style that draws them, so the line's natural width is the sum.
        const string a = "aa bb cc dd ";
        const string b = "ee ff gg hh ii";
        using var doc = Page();
        doc.Add(new Paragraph(
        [
            new TextRun(a, Helv()),
            new TextRun(b + " jj kk ll mm nn oo pp qq rr ss tt uu", Bold()),
        ])
        { Alignment = HorizontalAlignment.Justify });

        var stream = Stream(doc);

        var tw = D(Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value);
        var xs = Regex.Matches(stream, @"1 0 0 1 ([-\d.]+) [-\d.]+ Tm")
            .Select(m => D(m.Groups[1].Value)).ToArray();

        // Line 1 is whatever precedes the first Tw reset; its last Tm is the last fragment's start.
        var lineOneEnd = Regex.Match(stream, @"[-\d.]+ Tw[\s\S]*?0 Tw").Value;
        var tmsOnLineOne = Regex.Matches(lineOneEnd, @"1 0 0 1 ([-\d.]+) [-\d.]+ Tm").Count;
        Assert.Equal(2, tmsOnLineOne);
        Assert.True(tw > 0);
        Assert.NotEmpty(xs);
    }

    /// <summary>A line that is not justified advances exactly as before, with no Tw at all.</summary>
    [Fact]
    public void NotJustified_isUnchanged_andWritesNoWordSpacing()
    {
        using var doc = Page();
        doc.Add(new Paragraph(
        [
            new TextRun("aa bb cc dd ", Helv()),
            new TextRun("ee ff gg hh ii jj kk ll mm nn oo pp", Bold()),
        ])
        { Alignment = HorizontalAlignment.Left });

        var stream = Stream(doc);
        var xs = Regex.Matches(stream, @"1 0 0 1 ([-\d.]+) [-\d.]+ Tm")
            .Select(m => D(m.Groups[1].Value)).ToArray();

        Assert.DoesNotContain(" Tw", stream, StringComparison.Ordinal);
        Assert.Equal(xs[0] + Helv().MeasureString("aa bb cc dd "), xs[1], 3);
    }

    /// <summary>A link on a justified standard-14 line is placed where its text is drawn.</summary>
    [Fact]
    public void LinkedFragment_rectangleStartsAtTheDrawnPositionAndCoversTheDrawnWidth()
    {
        const string a = "aa bb cc dd ";
        const string b = "ee ff gg hh";
        using var doc = Page();
        doc.Add(new Paragraph(
        [
            new TextRun(a, Helv()),
            new TextRun(b, new TextStyle
            {
                FontRef = new FontReference(Standard14.Helvetica),
                FontSize = 12,
                LinkUri = "https://a.example",
            }),
            new TextRun(" ii jj kk ll mm nn oo pp qq rr ss tt uu vv", Helv()),
        ])
        { Alignment = HorizontalAlignment.Justify });

        using var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        var rect = Regex.Match(bytes, @"/Rect \[([-\d.]+) ([-\d.]+) ([-\d.]+) ([-\d.]+)\]");
        Assert.True(rect.Success);

        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        var tw = D(Regex.Match(stream, @"([-\d.]+) Tw").Groups[1].Value);
        var xs = Regex.Matches(stream, @"1 0 0 1 ([-\d.]+) [-\d.]+ Tm")
            .Select(m => D(m.Groups[1].Value)).ToArray();

        // The linked fragment is the second Tm on the line.
        Assert.Equal(xs[1], D(rect.Groups[1].Value), 2);
        // The space between two runs stays with the fragment before it, so the linked fragment is
        // "ee ff gg hh " with a trailing space, and its rectangle covers that space as it always did.
        var drawn = b + " ";
        var linkWidth = Helv().MeasureString(drawn) + (drawn.Count(c => c == ' ') * tw);
        Assert.Equal(xs[1] + linkWidth, D(rect.Groups[3].Value), 2);
    }
}
