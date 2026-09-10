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
/// wedges. An operator histogram over 400 documents <see cref="LayoutGen"/> now builds confirms
/// <c>cm</c>, <c>m</c> and <c>c</c> in all 400; <c>l</c> is still matched by none, since the
/// generator's own chart always carries exactly one drawable slice (the branch that emits it) and
/// never adds a separator (the other source), so it stays here for the pull request that does.
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
    /// its side bearings, and a stroked rectangle or curve spreads half the line width outside its
    /// own path, which for the table border's default 0.5pt is 0.25pt per edge. A chart's own
    /// <c>StrokeColor</c> defaults to null, so a generated chart strokes nothing; every stroke
    /// operator in these documents today is one of the three table cell borders. Neither is
    /// bounded here, and 0.25pt is well above any tolerance a caller compares against, so on a
    /// page whose margin is near zero a quarter-point of border ink outside the page passes.
    /// Bounding ink rather than geometry would mean carrying the stroke width and the font's
    /// bearings through this reader.
    ///
    /// "Ordered" describes concatenation order, not per-page order: every flate stream in the file
    /// is decompressed and appended with no separator, so a document with more than one content
    /// stream — more than one page, or a page with a non-content stream ahead of it — sees its
    /// path operators in concatenation order across all of them, not in the order one page alone
    /// would draw them. The current-point walk below still gets each subpath right, because a
    /// stream boundary can only ever open a fresh subpath with its own <c>m</c>, never continue one
    /// left open by a different stream.
    ///
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

        WalkPath(decompressed, Note);

        return seen ? new Extent(minX, maxX, minY, maxY) : null;
    }

    /// <summary>
    /// Walks <c>m</c>, <c>l</c>, <c>re</c>, <c>h</c> and <c>c</c> in stream order, tracking the
    /// current point the way path construction defines it (ISO 32000-2 §8.5.2), and notes the
    /// exact extent of what each one draws — for <c>c</c> the curve's true extrema, not its
    /// control hull. A control-hull bound was the earlier approach here (separate <c>m</c>/<c>l</c>
    /// and <c>c</c> passes with no current-point tracking between them), and it is exact only at
    /// the default start angle -- where the hull span equals the true extent -- overshooting it at
    /// every other one, up to 1.13216 times the true extent on a chart's own wedge circle at a
    /// start angle of 1.2. Restoring that hull reader changes nothing a document built at the
    /// default start angle asserts, but it is exactly what a generated start angle off the default
    /// would need: the true-extent walk here is what makes testing one possible at all.
    /// </summary>
    private static void WalkPath(string decompressed, Action<double, double> note)
    {
        double curX = 0, curY = 0, startX = 0, startY = 0;
        var hasCurrent = false;

        foreach (Match m in PathEvent().Matches(decompressed))
        {
            if (m.Groups["mx"].Success)
            {
                curX = startX = Num(m.Groups["mx"].Value);
                curY = startY = Num(m.Groups["my"].Value);
                hasCurrent = true;
                note(curX, curY);
            }
            else if (m.Groups["lx"].Success)
            {
                curX = Num(m.Groups["lx"].Value);
                curY = Num(m.Groups["ly"].Value);
                hasCurrent = true;
                note(curX, curY);
            }
            else if (m.Groups["rx"].Success)
            {
                // re is defined as m l l l h (ISO 32000-2, 8.5.2.1), so the current point after it
                // is the rectangle's own (x, y) — the corner the construction starts and closes
                // on — not one of the other three corners a plain "note both extremes" read might
                // suggest. A c immediately after an re therefore starts from that corner.
                var x = Num(m.Groups["rx"].Value);
                var y = Num(m.Groups["ry"].Value);
                var w = Num(m.Groups["rw"].Value);
                var h = Num(m.Groups["rh"].Value);
                note(x, y);
                note(x + w, y + h);
                curX = startX = x;
                curY = startY = y;
                hasCurrent = true;
            }
            else if (m.Groups["h"].Success)
            {
                // Returns the current point to the start of the subpath; it does not by itself
                // establish one, so hasCurrent is left exactly as it was.
                curX = startX;
                curY = startY;
            }
            else
            {
                // A cubic Bézier. AppendArc documents that the caller must position the current
                // point first and emits no m of its own. This tree has seven AppendArc call
                // sites -- two in PieChartRenderer, four in the Kernel graphics-primitive tests,
                // and one in OffPagePlacementTests' own probe renderer -- and every one positions
                // with MoveTo (or MoveTo then LineTo) before calling it, so this never fires in
                // practice. Thrown rather than defaulting the start to (0, 0), because that
                // default would silently drag the extent toward the origin instead of the walk
                // failing loudly on a stream this reader cannot actually interpret. That throw
                // only covers hasCurrent being false outright: this walk never clears the current
                // point after a painting operator (S, f, B), so a c that followed a paint with no
                // fresh m would anchor to the stale point rather than throw, and the walk has no v
                // or y arms at all, so either would silently under-read instead of failing.
                // Nothing this library emits produces either shape, which is why the scope stops
                // here rather than adding an untested branch.
                if (!hasCurrent)
                    throw new InvalidOperationException(
                        "A curve operator (c) appeared with no current point to start from.");

                var x1 = Num(m.Groups["c1x"].Value);
                var y1 = Num(m.Groups["c1y"].Value);
                var x2 = Num(m.Groups["c2x"].Value);
                var y2 = Num(m.Groups["c2y"].Value);
                var x3 = Num(m.Groups["c3x"].Value);
                var y3 = Num(m.Groups["c3y"].Value);

                note(curX, curY);
                note(x3, y3);
                foreach (var t in CubicExtremaT(curX, x1, x2, x3))
                    note(CubicAt(curX, x1, x2, x3, t), CubicAt(curY, y1, y2, y3, t));
                foreach (var t in CubicExtremaT(curY, y1, y2, y3))
                    note(CubicAt(curX, x1, x2, x3, t), CubicAt(curY, y1, y2, y3, t));

                curX = x3;
                curY = y3;
            }
        }
    }

    /// <summary>Evaluates a one-dimensional cubic Bézier at parameter <paramref name="t"/>.</summary>
    private static double CubicAt(double p0, double p1, double p2, double p3, double t)
    {
        var mt = 1 - t;
        return (mt * mt * mt * p0) + (3 * mt * mt * t * p1) + (3 * mt * t * t * p2) + (t * t * t * p3);
    }

    /// <summary>
    /// The parameter values in (0, 1) where a one-dimensional cubic Bézier's derivative is zero —
    /// its interior extrema, found by solving the derivative's quadratic <c>A·t² + B·t + C = 0</c>.
    /// Endpoints are the caller's job to note separately; a root at exactly 0 or 1 duplicates one.
    /// </summary>
    private static IEnumerable<double> CubicExtremaT(double p0, double p1, double p2, double p3)
    {
        var a = 3 * (-p0 + (3 * p1) - (3 * p2) + p3);
        var b = 6 * (p0 - (2 * p1) + p2);
        var c = 3 * (p1 - p0);

        if (a == 0)
        {
            if (b != 0)
            {
                var t = -c / b;
                if (t is > 0 and < 1) yield return t;
            }
            yield break;
        }

        var discriminant = (b * b) - (4 * a * c);
        if (discriminant < 0) yield break;

        var sq = Math.Sqrt(discriminant);
        var t1 = (-b + sq) / (2 * a);
        var t2 = (-b - sq) / (2 * a);
        if (t1 is > 0 and < 1) yield return t1;
        if (t2 is > 0 and < 1) yield return t2;
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

    [GeneratedRegex(@"(?m)^(" + N + ") (" + N + ") (" + N + ") (" + N + ") (" + N + ") (" + N + @") cm$")]
    private static partial Regex Matrix();

    // One alternation, in the same shape as TextEvent below, so m, l, re, h and c stay in stream
    // order for the current-point walk in WalkPath: reading them via four separate passes (as
    // PathPoint, Rect and Curve once did) loses which m or re a given c actually started from.
    [GeneratedRegex(@"(?m)^(?:(?<mx>" + N + ") (?<my>" + N + @") m"
        + "|(?<lx>" + N + ") (?<ly>" + N + @") l"
        + "|(?<rx>" + N + ") (?<ry>" + N + ") (?<rw>" + N + ") (?<rh>" + N + @") re"
        + "|(?<h>h)"
        + "|(?<c1x>" + N + ") (?<c1y>" + N + ") (?<c2x>" + N + ") (?<c2y>" + N + ") (?<c3x>" + N
        + ") (?<c3y>" + N + @") c)$")]
    private static partial Regex PathEvent();

    // One alternation so the three event kinds stay in stream order: a font switch, a text-matrix
    // set, and a show. Reading them separately would lose which size was in force for which show.
    [GeneratedRegex(@"(?m)^(?:/\w+ (?<size>" + N + @") Tf|1 0 0 1 (?<tmx>" + N + ") (?<tmy>" + N
        + @") Tm|\((?<lit>(?:[^()\\]|\\.)*)\) Tj)$")]
    private static partial Regex TextEvent();

    [GeneratedRegex(@"\\(.)")]
    private static partial Regex Escape();
}
