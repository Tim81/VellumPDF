// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering.Table;

/// <summary>A stretch of one drawn line of a cell that is shown in one style.</summary>
internal readonly record struct CellFragment(string Text, TextStyle Style);

/// <summary>
/// The lines a run cell wraps into. Each entry of the list is the text of the line, which is what
/// the string cell's wrap returns, so the code that counts, cuts and slices lines reads it as it
/// reads that list; <see cref="Fragments"/> holds, for the same index, the stretches of the line
/// and their styles.
/// </summary>
internal sealed class CellRunLines : List<string>
{
    /// <summary>The stretches of each line, in drawing order, parallel to the list itself.</summary>
    internal List<CellFragment[]> Fragments { get; } = [];

    internal void AddLine(CellFragment[] fragments)
    {
        Add(fragments.Length switch
        {
            0 => string.Empty,
            1 => fragments[0].Text,
            _ => string.Concat(fragments.Select(f => f.Text)),
        });
        Fragments.Add(fragments);
    }
}

/// <summary>
/// The text of a cell built from runs, wrapped the way <c>TableRenderer.WordWrapLines</c> wraps a
/// string: the same hard lines, the same break points, the same hard break of a word wider than
/// the line, so that a run cell whose runs share one style wraps exactly as the string cell of
/// the same text.
/// </summary>
/// <remarks>
/// The runs are joined into one string. A word is whatever lies between two spaces of that string,
/// so a run boundary inside a word draws no space and the word is measured in pieces, each in the
/// style of the run holding it. A space is measured in the style of the run that holds it. Adjacent
/// runs that share one style instance are one piece, which makes a cell of such runs the same cell
/// as one run of the joined text, kerning across the boundary included.
/// Every line of the cell is as tall as the tallest leading of the cell's runs and sits on a
/// baseline that follows the largest font size, so a row is cut between lines as a string cell is.
/// </remarks>
internal sealed class CellRunText
{
    private readonly TextStyle[] _styles;
    private readonly int[] _starts;
    private readonly double[] _spaceWidths;
    private string? _spaced;

    internal CellRunText(IReadOnlyList<TextRun> runs)
    {
        var text = new StringBuilder();
        var styles = new List<TextStyle>();
        var starts = new List<int>();
        foreach (var run in runs)
        {
            if (run.Text.Length == 0) continue;
            if (styles.Count == 0 || !ReferenceEquals(styles[^1], run.Style))
            {
                starts.Add(text.Length);
                styles.Add(run.Style);
            }
            text.Append(run.Text);
        }

        Text = text.ToString();
        _styles = [.. styles];
        _starts = [.. starts];
        _spaceWidths = new double[_styles.Length];
        Array.Fill(_spaceWidths, double.NaN);

        // The styles that draw something, or every style when no run holds text, so that an empty
        // cell is as tall as the string cell of the same style.
        Relevant = _styles.Length > 0 ? _styles : [.. runs.Select(r => r.Style).Distinct()];
        FirstStyle = Relevant[0];
        Leading = Relevant.Max(s => s.EffectiveLeading);
        FontSize = Relevant.Max(s => s.FontSize);
    }

    /// <summary>The text of every run, joined.</summary>
    internal string Text { get; }

    /// <summary>The styles of the runs that hold text, or of every run when none does.</summary>
    internal IReadOnlyList<TextStyle> Relevant { get; }

    /// <summary>The style the first stretch of text is drawn in, for the first font and colour.</summary>
    internal TextStyle FirstStyle { get; }

    /// <summary>The height of every line of the cell.</summary>
    internal double Leading { get; }

    /// <summary>The font size that places the first baseline below the top of the cell.</summary>
    internal double FontSize { get; }

    /// <summary>
    /// The number of lines <see cref="Lines"/> returns, without building them, and the widths over
    /// which that number holds: the wrap takes the same path at every width in
    /// [<paramref name="from"/>, <paramref name="below"/>), because each comparison it makes is of
    /// a width that does not depend on the line width against the line width, and the two bounds
    /// are the largest such width that fitted and the smallest that did not. This is the interval
    /// <c>TableRenderer.CountLines</c> finds for a string.
    /// </summary>
    internal int CountLines(double maxWidth, bool honourBreaks, out double from, out double below)
    {
        from = double.NegativeInfinity;
        below = double.PositiveInfinity;
        var d = Derived(honourBreaks);
        return Wrap(d, HardLines(honourBreaks), maxWidth, ref from, ref below).Count;
    }

    /// <summary>The number of lines <see cref="Lines"/> returns, without building them.</summary>
    internal int CountLines(double maxWidth, bool honourBreaks) =>
        CountLines(maxWidth, honourBreaks, out _, out _);

    /// <summary>The lines the text is drawn in, each with its stretches.</summary>
    internal CellRunLines Lines(double maxWidth, bool honourBreaks)
    {
        var d = Derived(honourBreaks);
        var from = double.NegativeInfinity;
        var below = double.PositiveInfinity;
        var ranges = Wrap(d, HardLines(honourBreaks), maxWidth, ref from, ref below);
        var lines = new CellRunLines();
        foreach (var (start, end) in ranges)
            lines.AddLine(Fragments(d, start, end, removeControlSpaces: !honourBreaks));
        return lines;
    }

    /// <summary>The widest word and the widest line, each line measured on its own.</summary>
    internal (double Longest, double Widest) Measure(bool honourBreaks)
    {
        var d = Derived(honourBreaks);
        var longest = 0.0;
        var widest = 0.0;
        var first = true;
        foreach (var (hs, he) in HardLines(honourBreaks))
        {
            var lineLongest = 0.0;
            var firstWord = true;
            var ws = hs;
            while (true)
            {
                var we = d.IndexOf(' ', ws, he - ws);
                var last = we < 0;
                if (last) we = he;
                var w = MeasureRange(d, ws, we);
                lineLongest = firstWord ? w : Math.Max(lineLongest, w);
                firstWord = false;
                if (last) break;
                ws = we + 1;
            }

            var lineWidth = MeasureRange(d, hs, he);
            longest = first ? lineLongest : Math.Max(longest, lineLongest);
            widest = first ? lineWidth : Math.Max(widest, lineWidth);
            first = false;
        }
        return (longest, widest);
    }

    /// <summary>
    /// The text that is wrapped: with every control white-space character a space when the breaks
    /// are honoured, which keeps every index of <see cref="Text"/>, and as written otherwise.
    /// </summary>
    private string Derived(bool honourBreaks) =>
        honourBreaks ? _spaced ??= LayoutText.ControlSpacesToSpaces(Text) : Text;

    /// <summary>
    /// The hard lines as ranges of the text. With breaks honoured a carriage return and line feed
    /// pair, a lone carriage return and a lone line feed each end one, as in
    /// <c>LayoutText.SplitHardLines</c>; otherwise the whole text is one line.
    /// </summary>
    private List<(int Start, int End)> HardLines(bool honourBreaks)
    {
        var text = Text;
        if (!honourBreaks || !LayoutText.HasLineBreak(text)) return [(0, text.Length)];

        var lines = new List<(int, int)>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\r' && c != '\n') continue;
            lines.Add((start, i));
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            start = i + 1;
        }
        lines.Add((start, text.Length));
        return lines;
    }

    /// <summary>
    /// Wraps each hard line as <c>TableRenderer.WrapLine</c> wraps a string, step for step, and
    /// returns each line as a range of <paramref name="d"/>. A line is always a stretch of the
    /// text, because the words it joins are the ones next to each other with their single space.
    /// </summary>
    private List<(int Start, int End)> Wrap(
        string d, List<(int Start, int End)> hardLines, double maxWidth, ref double from, ref double below)
    {
        var result = new List<(int, int)>();
        foreach (var (hs, he) in hardLines)
        {
            var before = result.Count;
            WrapLine(d, hs, he, maxWidth, result, ref from, ref below);
            if (result.Count == before) result.Add((hs, hs));
        }
        return result;
    }

    private void WrapLine(
        string d, int hs, int he, double maxWidth, List<(int, int)> result, ref double from, ref double below)
    {
        if (hs == he) { result.Add((hs, hs)); return; }

        var has = false;
        var ls = 0;
        var le = 0;
        var lineW = 0.0;
        var ws = hs;
        var last = false;
        while (!last)
        {
            var we = d.IndexOf(' ', ws, he - ws);
            last = we < 0;
            if (last) we = he;

            var ww = MeasureRange(d, ws, we);
            if (!has)
            {
                StartLine(d, ws, we, ww, maxWidth, result, ref has, ref ls, ref le, ref lineW, ref from, ref below);
            }
            else
            {
                var spaceW = SpaceWidth(ws - 1);
                var joined = lineW + spaceW + ww;
                if (joined <= maxWidth)
                {
                    from = Math.Max(from, joined);
                    le = we;
                    lineW += spaceW + ww;
                }
                else
                {
                    below = Math.Min(below, joined);
                    result.Add((ls, le));
                    has = false;
                    lineW = 0.0;
                    StartLine(d, ws, we, ww, maxWidth, result, ref has, ref ls, ref le, ref lineW, ref from, ref below);
                }
            }

            ws = we + 1;
        }
        if (has) result.Add((ls, le));
    }

    /// <summary>
    /// A word at the start of a line: broken at its characters when it is wider than the line, and
    /// otherwise the line under way, which an empty word leaves empty.
    /// </summary>
    private void StartLine(
        string d, int ws, int we, double ww, double maxWidth, List<(int, int)> result,
        ref bool has, ref int ls, ref int le, ref double lineW, ref double from, ref double below)
    {
        if (ww > maxWidth)
        {
            below = Math.Min(below, ww);
            HardBreakWord(d, ws, we, maxWidth, result, ref from, ref below);
            return;
        }

        from = Math.Max(from, ww);
        if (we > ws) { has = true; ls = ws; le = we; }
        lineW = ww;
    }

    /// <summary>Breaks a word wider than <paramref name="maxWidth"/> at character granularity.</summary>
    private void HardBreakWord(
        string d, int ws, int we, double maxWidth, List<(int, int)> lines, ref double from, ref double below)
    {
        var fragmentStart = ws;
        var fragmentW = 0.0;
        var at = ws;
        foreach (var rune in d.AsSpan(ws, we - ws).EnumerateRunes())
        {
            var n = rune.Utf16SequenceLength;
            var charW = MeasureRange(d, at, at + n);
            if (at > fragmentStart)
            {
                var grown = fragmentW + charW;
                if (grown > maxWidth)
                {
                    below = Math.Min(below, grown);
                    lines.Add((fragmentStart, at));
                    fragmentStart = at;
                    fragmentW = 0.0;
                }
                else
                {
                    from = Math.Max(from, grown);
                }
            }
            fragmentW += charW;
            at += n;
        }
        if (at > fragmentStart) lines.Add((fragmentStart, at));
    }

    /// <summary>
    /// The stretches of <c>d[start..end]</c> by run, with the control white space taken out of each
    /// when the cell is read unbroken, and a stretch left with nothing dropped.
    /// </summary>
    private CellFragment[] Fragments(string d, int start, int end, bool removeControlSpaces)
    {
        if (start >= end) return [];

        var found = new List<CellFragment>();
        var seg = SegmentAt(start);
        while (start < end)
        {
            var segEnd = seg + 1 < _starts.Length ? _starts[seg + 1] : Text.Length;
            var stop = Math.Min(end, segEnd);
            var piece = d.Substring(start, stop - start);
            if (removeControlSpaces) piece = LayoutText.RemoveControlSpaces(piece);
            if (piece.Length > 0) found.Add(new CellFragment(piece, _styles[seg]));
            start = stop;
            seg++;
        }
        return [.. found];
    }

    /// <summary>The width of <c>d[start..end]</c>, each stretch in the style of its run.</summary>
    private double MeasureRange(string d, int start, int end)
    {
        if (start >= end || _styles.Length == 0) return 0.0;

        var total = 0.0;
        var seg = SegmentAt(start);
        while (start < end)
        {
            var segEnd = seg + 1 < _starts.Length ? _starts[seg + 1] : Text.Length;
            var stop = Math.Min(end, segEnd);
            var style = _styles[seg];
            total += style.FontRef.MeasureString(d.Substring(start, stop - start), style.FontSize);
            start = stop;
            seg++;
        }
        return total;
    }

    private double SpaceWidth(int index)
    {
        var seg = SegmentAt(index);
        var cached = _spaceWidths[seg];
        if (!double.IsNaN(cached)) return cached;

        var style = _styles[seg];
        var measured = style.FontRef.MeasureString(" ", style.FontSize);
        // A NaN width is never cached, so a NaN font size measures again, as the string cell does.
        _spaceWidths[seg] = measured;
        return measured;
    }

    private int SegmentAt(int index)
    {
        var at = Array.BinarySearch(_starts, index);
        return at >= 0 ? at : (~at) - 1;
    }
}
