// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Reads back where a decompressed content stream actually put things, so a test can assert
/// against emitted coordinates rather than against the layout code's own arithmetic.
///
/// <c>PdfCanvas</c> writes one operator per line with no leading whitespace, so every pattern here
/// is line-anchored, and the anchoring is load-bearing: <c>c</c> is a prefix of <c>cm</c>, so an
/// unanchored six-number curve pattern matches every image matrix as well as every real Bezier.
/// Measured on a stream holding two <c>cm</c> lines and one <c>c</c> line, the unanchored pattern
/// returns all three and the anchored one returns the curve alone. The four-number <c>re</c>
/// pattern is safe against that particular collision, because the literal <c>re</c> token cannot
/// match the tail of a <c>cm</c> line, but not safe in general: unanchored it also matches a text
/// literal that happens to contain four numbers and the token, which anchoring rules out.
///
/// The operators covered are the ones the layout package emits, established by grepping it for
/// every public <c>PdfCanvas</c> method name: <c>Tm</c> and <c>Tj</c> for text, <c>re</c> for table
/// cells and backgrounds, <c>cm</c> for image placement (the one <c>PdfCanvas.Concat</c> call site,
/// in <c>LayoutImageRenderer</c>), and <c>m</c>, <c>l</c> and <c>c</c> for separators and chart
/// wedges. The last four are matched by no document <see cref="LayoutGen"/> currently builds; they
/// are here for the pull requests that add an image, a separator and a chart to the generator.
///
/// It does not live in <c>PdfTestUtil</c> beside <c>DecompressAllFlatStreams</c> because that class
/// is a grab-bag of fixture builders shared with the Barcodes suite, and this is one cohesive
/// reader with eight regexes and a documented operator scope.
/// </summary>
internal static partial class ContentStreamReadback
{
    /// <summary>An axis-aligned bounding box in PDF user space.</summary>
    internal readonly record struct Extent(double MinX, double MaxX, double MinY, double MaxY);

    /// <summary>
    /// One text-showing operation: where its baseline starts, at what size, and what it drew.
    /// The width is deliberately not computed here — that needs the font, and keeping the metrics
    /// on the caller's side stops this helper from quietly assuming Helvetica for everyone.
    /// </summary>
    internal readonly record struct TextPlacement(double X, double Y, double FontSize, string Text);

    /// <summary>
    /// The bounding box of every geometric placement: rectangle paths, image matrices, path
    /// points, and text baseline origins. Text is included only at its origin, because its extent
    /// depends on metrics this helper does not have — use <see cref="TextPlacements"/> for that.
    ///
    /// Two things it measures the path of rather than the ink: a glyph reaches past its origin by
    /// its side bearings, and a stroked rectangle spreads half the line width outside its own path,
    /// which for the table border's default 0.5pt is 0.25pt per edge. Neither is bounded here, and
    /// 0.25pt is well above any tolerance a caller compares against, so on a page whose margin is
    /// near zero a quarter-point of border ink outside the page passes. Bounding ink rather than
    /// geometry would mean carrying the stroke width and the font's bearings through this reader.
    /// Returns null when the stream places nothing.
    /// </summary>
    internal static Extent? GeometryExtent(string decompressed)
    {
        double minX = double.MaxValue, maxX = double.MinValue;
        double minY = double.MaxValue, maxY = double.MinValue;
        var seen = false;

        void Note(double x, double y)
        {
            seen = true;
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }

        foreach (Match m in TextMatrix().Matches(decompressed))
            Note(Num(m.Groups[1].Value), Num(m.Groups[2].Value));

        foreach (Match m in Rect().Matches(decompressed))
        {
            var x = Num(m.Groups[1].Value);
            var y = Num(m.Groups[2].Value);
            Note(x, y);
            Note(x + Num(m.Groups[3].Value), y + Num(m.Groups[4].Value));
        }

        foreach (Match m in Matrix().Matches(decompressed))
        {
            var a = Num(m.Groups[1].Value);
            var b = Num(m.Groups[2].Value);
            var c = Num(m.Groups[3].Value);
            var d = Num(m.Groups[4].Value);
            var e = Num(m.Groups[5].Value);
            var f = Num(m.Groups[6].Value);

            Note(e, f);

            // The layout package's only Concat call is an axis-aligned scale plus translate, so
            // the off-diagonal terms are zero and the placed box is exactly (e,f) to (e+a, f+d).
            // A rotated or skewed matrix would need its four corners transformed; nothing here
            // emits one, so rather than write untested code for that case this notes the
            // translation alone and leaves the extent understated.
            if (b == 0 && c == 0) Note(e + a, f + d);
        }

        foreach (Match m in PathPoint().Matches(decompressed))
            Note(Num(m.Groups[1].Value), Num(m.Groups[2].Value));

        foreach (Match m in Curve().Matches(decompressed))
        {
            Note(Num(m.Groups[1].Value), Num(m.Groups[2].Value));
            Note(Num(m.Groups[3].Value), Num(m.Groups[4].Value));
            Note(Num(m.Groups[5].Value), Num(m.Groups[6].Value));
        }

        return seen ? new Extent(minX, maxX, minY, maxY) : null;
    }

    /// <summary>
    /// Every literal string shown through <c>Tj</c>, with the baseline origin and font size in
    /// force when it was shown. Only the simple-font path is covered: an embedded font emits a hex
    /// glyph run, which carries no recoverable text.
    /// </summary>
    internal static List<TextPlacement> TextPlacements(string decompressed)
    {
        var result = new List<TextPlacement>();
        double size = 0, x = 0, y = 0;

        foreach (Match m in TextEvent().Matches(decompressed))
        {
            if (m.Groups["size"].Success)
            {
                size = Num(m.Groups["size"].Value);
            }
            else if (m.Groups["tmx"].Success)
            {
                x = Num(m.Groups["tmx"].Value);
                y = Num(m.Groups["tmy"].Value);
            }
            else
            {
                result.Add(new TextPlacement(x, y, size, Unescape(m.Groups["lit"].Value)));
            }
        }

        return result;
    }

    // PdfCanvas.WritePdfString escapes five bytes, not the three that would otherwise end or nest
    // the literal: it also writes 0x0A as an escaped n and 0x0D as an escaped r, even though
    // ISO 32000-2 7.3.4.2 permits a raw end-of-line inside a literal string. Replacing every
    // backslash pair with its second character therefore decoded an escaped newline to the letter
    // n, which Helvetica measures at 6.672pt at 12pt where the newline itself measures nothing. No
    // document this generator builds contains a newline, so the error was latent, but it would have
    // inflated every computed right edge for the first one that did.
    private static string Unescape(string literal) =>
        Escape().Replace(literal, m => m.Groups[1].Value switch
        {
            "n" => "\n",
            "r" => "\r",
            var other => other,
        });

    private static double Num(string s) => double.Parse(s, CultureInfo.InvariantCulture);

    private const string N = @"-?\d+(?:\.\d+)?";

    [GeneratedRegex(@"(?m)^1 0 0 1 (" + N + ") (" + N + @") Tm$")]
    private static partial Regex TextMatrix();

    [GeneratedRegex(@"(?m)^(" + N + ") (" + N + ") (" + N + ") (" + N + @") re$")]
    private static partial Regex Rect();

    [GeneratedRegex(@"(?m)^(" + N + ") (" + N + ") (" + N + ") (" + N + ") (" + N + ") (" + N + @") cm$")]
    private static partial Regex Matrix();

    [GeneratedRegex(@"(?m)^(" + N + ") (" + N + @") [ml]$")]
    private static partial Regex PathPoint();

    [GeneratedRegex(@"(?m)^(" + N + ") (" + N + ") (" + N + ") (" + N + ") (" + N + ") (" + N + @") c$")]
    private static partial Regex Curve();

    // One alternation so the three event kinds stay in stream order: a font switch, a text-matrix
    // set, and a show. Reading them separately would lose which size was in force for which show.
    [GeneratedRegex(@"(?m)^(?:/\w+ (?<size>" + N + @") Tf|1 0 0 1 (?<tmx>" + N + ") (?<tmy>" + N
        + @") Tm|\((?<lit>(?:[^()\\]|\\.)*)\) Tj)$")]
    private static partial Regex TextEvent();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Escape();
}
