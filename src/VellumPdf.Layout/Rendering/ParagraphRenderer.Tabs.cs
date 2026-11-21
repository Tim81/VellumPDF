// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

// Tab stops and their leaders. The wrap places the text after a tab and records where; Draw only
// reads the result, so the placement is cached with the wrap like every other line property.
public sealed partial class ParagraphRenderer
{
    // A leader is stroked, not typeset, and every measure of it is a fraction of the font size of
    // the text it leads to, so it scales with that text.

    /// <summary>
    /// Line width of a leader: thin enough to read as a rule beside the text's strokes.
    /// </summary>
    internal const double LeaderWidthFactor = 0.08;

    /// <summary>
    /// The leader period: the distance between dots, and the shortest span worth drawing. It is
    /// half the font size, so a dot every half em at any size.
    /// </summary>
    internal const double LeaderPeriodFactor = 0.5;

    /// <summary>Clear space between a leader and the text on either side of it.</summary>
    internal const double LeaderGapFactor = 0.25;

    /// <summary>
    /// How far above the baseline a dash or rule leader runs: about the middle of a lower-case
    /// letter's height.
    /// </summary>
    internal const double LeaderRaiseFactor = 0.25;

    /// <summary>
    /// Dash length, in line widths. Zero-length dashes make dots; see <see cref="DrawLeaders"/>.
    /// </summary>
    internal const double DashLengthInWidths = 3;

    /// <summary>Gap between dashes, in line widths.</summary>
    internal const double DashGapInWidths = 2;

    /// <summary>
    /// How far past the width a stop may lie and still count as at the width, in points: the scale
    /// below which <see cref="PdfCanvas"/> writes the same number. Two ways of computing one width
    /// differ by a rounding step. A stop within it is placed at the width, not past it.
    /// </summary>
    internal const double WidthTolerance = 5e-6;

    /// <summary>
    /// How many stop positions the lookup compared on this thread, for a test to pin that it
    /// searches by halving and does not scan.
    /// </summary>
    [ThreadStatic]
    internal static long TabStopProbes;

    /// <summary>The paragraph this renderer lays out.</summary>
    internal Paragraph Paragraph => _para;

    /// <summary>
    /// The stops that lie within <paramref name="width"/>, or null when there are none. The stops
    /// are in increasing order, so those beyond the width are a suffix. <paramref name="beyond"/>
    /// is how many were left out, counted only when the text holds a tab, because a stop nothing
    /// uses misplaces nothing. A width that is not a number keeps every stop: the existing margin
    /// refusals deal with it.
    /// </summary>
    private static IReadOnlyList<TabStop>? StopsWithin(Paragraph para, double width, out int beyond)
    {
        beyond = 0;
        var stops = para.TabStops;
        if (stops is not { Count: > 0 })
            return null;

        if (double.IsNaN(width))
            return stops;

        // A stop past the width by less than the scale the file is written at is the right edge:
        // two ways of computing one width differ by a rounding step.
        var within = 0;
        while (within < stops.Count && stops[within].Position - width < WidthTolerance)
            within++;

        if (within == stops.Count)
            return stops;

        // A tab in a run with no style is a space, so it uses no stop.
        foreach (var run in para.Runs)
        {
            if (run.Style is not null && run.Text.Contains('\t'))
            {
                beyond = stops.Count - within;
                break;
            }
        }

        // With none within the width the list is empty, not null: the text still holds tabs and
        // the one-space rule for a tab with no stop applies to them.
        return [.. stops.Take(within)];
    }

    /// <summary>Whether a tab stop placed any fragment of the line.</summary>
    private static bool HoldsPlacedFragment(LineFragment[] fragments)
    {
        foreach (var frag in fragments)
            if (frag.StartX.HasValue)
                return true;
        return false;
    }

    /// <summary>
    /// The first stop whose position is greater than <paramref name="pen"/>, found by halving, or
    /// null when there is none. A stop at the pen is not to its right, so the tab moves on to the
    /// next one.
    /// </summary>
    internal static TabStop? FindStop(IReadOnlyList<TabStop> stops, double pen)
    {
        var low = 0;
        var high = stops.Count;
        while (low < high)
        {
            TabStopProbes++;
            var mid = (int)(((uint)low + (uint)high) >> 1);
            if (stops[mid].Position > pen)
                high = mid;
            else
                low = mid + 1;
        }
        return low < stops.Count ? stops[low] : null;
    }

    private sealed class Frag(string text, TextStyle style, double width)
    {
        public string Text = text;
        public TextStyle Style = style;
        public double Width = width;
        public double? StartX;
        public LineLeader? Leader;

        // The fragment ends in a space written for a tab, and PlainWidth is the width without it.
        // A line that ends there drops the space, so it does not shorten the visible line.
        public bool TabSpace;
        public double PlainWidth;
    }

    private static LineFragment[] BuildFragments(List<Frag> frags)
    {
        var result = new LineFragment[frags.Count];
        for (var i = 0; i < frags.Count; i++)
        {
            var f = frags[i];
            result[i] = new LineFragment(f.Text, f.Style, f.Width) { StartX = f.StartX, Leader = f.Leader };
        }
        return result;
    }

    private static double SpaceWidth(TextStyle style) => style.FontRef.MeasureString(" ", style.FontSize);

    /// <summary>
    /// The wrap for a paragraph that has tab stops. It is the greedy wrap of <see cref="WordWrap"/>
    /// with a position kept for the pen, and a tab placing the text after it: see
    /// <see cref="Paragraph.TabStops"/> for the rule. <paramref name="stops"/> are all within
    /// <paramref name="maxWidth"/>.
    /// </summary>
    private static (List<LineFragment[]> Lines, List<LineEnd> Ends) WrapWithTabs(
        List<(string Text, TextStyle? Style, TokenKind Kind)> tokens,
        double maxWidth,
        IReadOnlyList<TabStop> stops,
        List<LineFragment[]> lines,
        List<LineEnd> ends)
    {
        // Each piece is measured once, in its own style. Measuring a piece in an embedded font
        // records its glyphs, and so does the space measured beside it, as the plain wrap does.
        var tokenWidth = new double[tokens.Count];
        for (var k = 0; k < tokens.Count; k++)
        {
            if (tokens[k].Kind is not (TokenKind.WordStart or TokenKind.WordContinue))
                continue;
            var style = tokens[k].Style!;
            _ = style.FontRef.MeasureString(" ", style.FontSize);
            tokenWidth[k] = style.FontRef.MeasureString(tokens[k].Text, style.FontSize);
        }

        var cur = new List<Frag>();
        var pen = 0.0;
        var unresolved = -1;

        void Commit(LineEnd end)
        {
            if (cur.Count > 0 && cur[^1].TabSpace)
            {
                var last = cur[^1];
                last.Text = last.Text[..^1];
                last.Width = last.PlainWidth;
                last.TabSpace = false;
            }

            lines.Add(BuildFragments(cur));
            ends.Add(end);
            cur.Clear();
            pen = 0.0;
        }

        void ResolveCutWord(LineEnd end)
        {
            if (unresolved >= 0)
                ends[unresolved] = end;
            unresolved = -1;
        }

        double WordWidth(int first, int end)
        {
            var w = 0.0;
            for (var k = first; k < end; k++)
                w = k == first ? tokenWidth[k] : w + tokenWidth[k];
            return w;
        }

        // The last fragment shows text that does not end in a space, so a word added after it
        // needs one between.
        bool NeedsSeparator() => cur.Count > 0 && cur[^1].Text.Length > 0 && cur[^1].Text[^1] != ' ';

        // Adds the pieces of the word first..end. The first piece joins the fragment before it when
        // both use one style instance and nothing positions it; every further piece is a style
        // change and a fragment of its own.
        void PushWord(int first, int end, bool addSpace, double? startX, LineLeader? leader)
        {
            var piece = tokens[first];
            if (cur.Count > 0 && startX is null && leader is null && cur[^1].Text.Length > 0 &&
                cur[^1].Style == piece.Style)
            {
                var last = cur[^1];
                if (addSpace)
                {
                    last.Text += " ";
                    last.Width += SpaceWidth(last.Style);
                }
                last.Text += piece.Text;
                last.Width += tokenWidth[first];
                last.TabSpace = false;
            }
            else
            {
                if (addSpace)
                {
                    var last = cur[^1];
                    last.Text += " ";
                    last.Width += SpaceWidth(last.Style);
                }
                cur.Add(new Frag(piece.Text, piece.Style!, tokenWidth[first]) { StartX = startX, Leader = leader });
            }

            for (var k = first + 1; k < end; k++)
                cur.Add(new Frag(tokens[k].Text, tokens[k].Style!, tokenWidth[k]));
        }

        // A word at the start of a line. A word wider than the line is cut at a character, as the
        // plain wrap does.
        void StartLine(int first, int end, double wordW)
        {
            if (wordW > maxWidth)
            {
                unresolved = HardBreakWord(tokens, first, end, maxWidth, lines, ends);
                pen = 0.0;
                return;
            }

            PushWord(first, end, false, null, null);
            pen = wordW;
        }

        // An ordinary word: after the text before it, a space away, or on the next line when it
        // does not fit.
        void FlowWord(int first, int end)
        {
            ResolveCutWord(LineEnd.Soft);
            var wordW = WordWidth(first, end);
            if (cur.Count == 0)
            {
                StartLine(first, end, wordW);
                return;
            }

            var addSpace = NeedsSeparator();
            var spaceW = addSpace ? SpaceWidth(cur[^1].Style) : 0.0;
            if (pen + spaceW + wordW <= maxWidth)
            {
                PushWord(first, end, addSpace, null, null);
                pen += spaceW + wordW;
            }
            else
            {
                Commit(LineEnd.Soft);
                StartLine(first, end, wordW);
            }
        }

        // One space of advance for a tab: appended to the text before it when there is some, a
        // fragment of its own otherwise.
        void AppendTabSpace(TextStyle style)
        {
            if (cur.Count > 0 && cur[^1].Text.Length > 0)
            {
                var last = cur[^1];
                var spaceW = SpaceWidth(last.Style);
                last.PlainWidth = last.Width;
                last.Text += " ";
                last.Width += spaceW;
                last.TabSpace = true;
                pen += spaceW;
            }
            else
            {
                var spaceW = SpaceWidth(style);
                cur.Add(new Frag(" ", style, spaceW) { TabSpace = true });
                pen += spaceW;
            }
        }

        // The leader between the text before a tab and the text after it. Text before it clears a
        // quarter of the size; the left edge of a line needs no clearance.
        LineLeader? MakeLeader(TabStop stop, double from, double to, TextStyle style, bool atLineStart)
        {
            if (stop.Leader == TabLeader.None)
                return null;

            var gap = LeaderGapFactor * style.FontSize;
            var begin = atLineStart ? from : from + gap;
            var span = to - gap - begin;

            // A span shorter than one period would draw less than a dot or a dash, or nothing.
            return span >= LeaderPeriodFactor * style.FontSize
                ? new LineLeader(stop.Leader, begin, to - gap, style)
                : null;
        }

        void PlaceAfterTab(int tab, int first, int end)
        {
            var tabStyle = tokens[tab].Style!;
            ResolveCutWord(LineEnd.Soft);

            // The text before the tab ends in one space, so a reader that extracts the text sees
            // the words apart (ISO 32000-2, 14.8.2.6.2). The space is part of the pen.
            var penBeforeSeparator = pen;
            var separatorWritten = false;
            if (NeedsSeparator())
            {
                AppendTabSpace(tabStyle);
                separatorWritten = true;
            }

            // The separator can carry the pen past a stop the text itself stayed within; that stop
            // is still the one this tab is for.
            var stop = FindStop(stops, pen);
            if (stop is null && separatorWritten)
                stop = FindStop(stops, penBeforeSeparator);
            if (stop is null)
            {
                // No stop to the right: the tab advances the pen by one space. The space written
                // above is that advance when text came straight before the tab; after a tab, or at
                // the start of a line, there is no such text and the advance is written here.
                if (!separatorWritten)
                    AppendTabSpace(tabStyle);

                for (var a = first; a < end;)
                {
                    var b = a + 1;
                    while (b < end && tokens[b].Kind == TokenKind.WordContinue)
                        b++;
                    FlowWord(a, b);
                    a = b;
                }
                return;
            }

            // A stop within the written-token scale of the width is the right edge, and is placed
            // there; a stop the pen has not passed is never placed left of the pen.
            var stopClamp = Math.Min(stop.Position, maxWidth);
            var stopX = Math.Max(pen, stopClamp);
            var penBefore = pen;
            if (first == end)
            {
                // Nothing follows the tab before the next one or the end of the line: the pen
                // moves to the stop, and a leader still fills the gap. The fragment is written
                // without text even when there is no leader, so the pen and the line agree.
                var leader = MakeLeader(stop, penBefore, stopX, tabStyle, cur.Count == 0);
                cur.Add(new Frag(string.Empty, tabStyle, 0.0) { StartX = stopX, Leader = leader });
                pen = stopX;
                return;
            }

            // Measure the text after the tab: its width, the first word's, and how far the first
            // decimal character lies from its start.
            var segmentW = 0.0;
            var firstWordW = 0.0;
            double? decimalOffset = null;
            var wordCount = 0;
            for (var a = first; a < end;)
            {
                var b = a + 1;
                while (b < end && tokens[b].Kind == TokenKind.WordContinue)
                    b++;
                if (wordCount > 0)
                    segmentW += SpaceWidth(tokens[a - 1].Style!);
                for (var k = a; k < b; k++)
                {
                    if (stop.Alignment == TabAlignment.Decimal && decimalOffset is null)
                    {
                        var at = tokens[k].Text.IndexOf(stop.DecimalCharacter);
                        if (at >= 0)
                        {
                            var style = tokens[k].Style!;
                            decimalOffset = segmentW + (at == 0
                                ? 0.0
                                : style.FontRef.MeasureString(tokens[k].Text[..at], style.FontSize));
                        }
                    }
                    segmentW += tokenWidth[k];
                }
                if (wordCount == 0)
                    firstWordW = WordWidth(a, b);
                wordCount++;
                a = b;
            }

            var offset = stop.Alignment switch
            {
                TabAlignment.Right => segmentW,
                TabAlignment.Center => segmentW / 2,
                TabAlignment.Decimal => decimalOffset ?? segmentW,
                _ => 0.0,
            };

            // A segment that would start left of the pen starts at the pen. A left stop needs room
            // for the first word only, since the rest wraps; the others need the whole segment. A
            // segment overflows when it ends at least one written unit past the width: two ways of
            // adding the same widths differ by a rounding step.
            var needs = stop.Alignment == TabAlignment.Left ? firstWordW : segmentW;
            bool Overflows(double from) => from + needs - maxWidth >= WidthTolerance;
            var start = Math.Max(pen, stopX - offset);
            var atLineStart = cur.Count == 0;
            if (Overflows(start))
            {
                var fresh = Math.Max(0.0, stopClamp - offset);
                if (cur.Count > 0 && !Overflows(fresh))
                {
                    // It does not fit after the text before it but fits at its stop on a fresh
                    // line: that line ends and the segment is placed there, its leader running from
                    // the left edge.
                    Commit(LineEnd.Soft);
                    penBefore = 0.0;
                    start = fresh;
                    atLineStart = true;
                }
                else
                {
                    // It fits at its stop nowhere: ordinary text from the pen, with no tab pending
                    // and the line not ended.
                    for (var a = first; a < end;)
                    {
                        var b = a + 1;
                        while (b < end && tokens[b].Kind == TokenKind.WordContinue)
                            b++;
                        FlowWord(a, b);
                        a = b;
                    }
                    return;
                }
            }

            var placed = MakeLeader(stop, penBefore, start, tokens[first].Style!, atLineStart);
            var firstEnd = first + 1;
            while (firstEnd < end && tokens[firstEnd].Kind == TokenKind.WordContinue)
                firstEnd++;
            PushWord(first, firstEnd, false, start, placed);
            pen = start + firstWordW;

            for (var a = firstEnd; a < end;)
            {
                var b = a + 1;
                while (b < end && tokens[b].Kind == TokenKind.WordContinue)
                    b++;
                if (stop.Alignment == TabAlignment.Left)
                {
                    FlowWord(a, b);
                }
                else
                {
                    // The whole text was measured to fit, so no word moves to another line.
                    var addSpace = NeedsSeparator();
                    PushWord(a, b, addSpace, null, null);
                    pen += (addSpace ? SpaceWidth(tokens[a - 1].Style!) : 0.0) + WordWidth(a, b);
                }
                a = b;
            }
        }

        for (var ti = 0; ti < tokens.Count;)
        {
            switch (tokens[ti].Kind)
            {
                case TokenKind.HardBreak:
                    ResolveCutWord(LineEnd.Hard);
                    Commit(LineEnd.Hard);
                    ti++;
                    break;

                case TokenKind.Tab:
                    {
                        var first = ti + 1;
                        var end = first;
                        while (end < tokens.Count && tokens[end].Kind is TokenKind.WordStart or TokenKind.WordContinue)
                            end++;
                        PlaceAfterTab(ti, first, end);
                        ti = end;
                        break;
                    }

                default:
                    {
                        var tj = ti + 1;
                        while (tj < tokens.Count && tokens[tj].Kind == TokenKind.WordContinue)
                            tj++;
                        FlowWord(ti, tj);
                        ti = tj;
                        break;
                    }
            }
        }

        if (cur.Count > 0)
            Commit(LineEnd.Last);
        else
            ResolveCutWord(LineEnd.Last);

        return (lines, ends);
    }

    /// <summary>
    /// Strokes each leader after the paragraph's text object and its marked content have closed.
    /// <para>
    /// Dots are zero-length dashes (<c>[0 p] 0 d</c>) with round caps: ISO 32000-2, 8.5.3.2 says a
    /// cap is painted for a zero-length dash, so each dash shows as a dot as wide as the line.
    /// Their positions are multiples of the period from the content's left edge, so the dots of
    /// consecutive lines line up. A dot is centred half a line width above the baseline, which
    /// sits it on the baseline like a full stop. Dashes and rules run a quarter of the size above
    /// it. The path is bracketed in <c>q</c> and <c>Q</c> so the stroke state does not reach the
    /// text after it, and in a tagged document in an artifact, because a leader is a mechanical
    /// layout element and not content (ISO 32000-2, 14.8.2.2).
    /// </para>
    /// </summary>
    private static void DrawLeaders(
        PdfCanvas canvas, bool tagged, double contentX, List<(LineLeader Leader, double BaselineY)> leaders)
    {
        foreach (var (leader, baseline) in leaders)
        {
            var size = leader.Style.FontSize;
            var width = LeaderWidthFactor * size;
            var period = LeaderPeriodFactor * size;
            var dots = leader.Kind == TabLeader.Dots;

            var x1 = contentX + (dots ? Math.Ceiling(leader.From / period) * period : leader.From);
            var x2 = contentX + leader.To;
            var y = baseline + (dots ? width / 2 : LeaderRaiseFactor * size);

            if (tagged)
                canvas.BeginArtifactMarkedContent();
            canvas.SaveState();
            canvas.SetStrokeColorRgb(leader.Style.Color.R, leader.Style.Color.G, leader.Style.Color.B);
            canvas.SetLineCap(dots ? 1 : 0);
            switch (leader.Kind)
            {
                case TabLeader.Dots:
                    canvas.SetLineDash([0, period], 0);
                    break;
                case TabLeader.Dashes:
                    canvas.SetLineDash([DashLengthInWidths * width, DashGapInWidths * width], 0);
                    break;
                default:
                    canvas.SetLineDash([], 0);
                    break;
            }
            canvas.SetLineWidth(width);
            canvas.MoveTo(x1, y);
            canvas.LineTo(x2, y);
            canvas.Stroke();
            canvas.RestoreState();
            if (tagged)
                canvas.EndMarkedContent();
        }
    }
}
