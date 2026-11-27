// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

// The paragraph's twin: the wrap as 2.3.3 made it, which keeps the verdict of the continuation
// limit (#687, #594). It differs from the current wrap in three ways: a word that continues over
// a run boundary is two words, the no-break spaces U+202F and U+2007 are spaces, and a word wider
// than the line is cut at the glyph, never after a solidus or hyphen. Everything in this file is
// removed in 3.0 (#688), which wraps one way under every context.
public sealed partial class ParagraphRenderer : IContinuationTwin
{
    // The wrap the last Layout did not use, and whether the one in _lines is the twin's. A wrap is
    // made once per instance and at the width of the first call, so a renderer that is laid out in
    // both modes holds both.
    private List<LineFragment[]>? _otherLines;
    private List<LineEnd>? _otherEnds;
    private bool _activeIsTwin;
    private double _firstWidth = double.NaN;

    // 0 until read, then 1 when the 2.3.3 wrap applies to this paragraph and 2 when it does not:
    // 2.3.3 had no tab stops and no page references, and refused a null style with another
    // exception, so a paragraph that holds one of them has no twin wrap and wraps as it does now.
    private byte _twinApplies;

    // 0 until read, then 1 when the two wraps can differ in line count and 2 when they cannot.
    private byte _mayDiffer;

    private ParagraphRenderer? _twin;

    /// <summary>Whether this instance made the wrap it does not read, for tests.</summary>
    internal bool MadeTheOtherWrap => _otherLines is not null;

    // The renderer laid out the way 2.3.3 laid it out, from the same start. Explicit: the class is
    // public and an implicit member would be public API.
    IRenderer IContinuationTwin.Twin() =>
        _twin ??= new ParagraphRenderer(_para, _startLine)
        {
            StructType = StructType,
            ParentStructElem = ParentStructElem,
            ElementLanguage = ElementLanguage,
            RunOwner = RunOwner,
        };

    private bool TwinWrapApplies()
    {
        if (_twinApplies != 0) return _twinApplies == 1;

        var applies = _para.TabStops is not { Count: > 0 };
        foreach (var run in _para.Runs)
        {
            if (run.IsReference || run.Style is null)
            {
                applies = false;
                break;
            }
        }

        _twinApplies = applies ? (byte)1 : (byte)2;
        return applies;
    }

    // Whether the layout is the twin's. An instance that has wrapped keeps its wrap. One that has
    // not takes the twin's under a context the document did not address to it and under the twin
    // the document chose, which lay out as 2.3.3 did. A renderer built with a start line is an
    // index into the lines of one wrap, which is 2.3.3's when a caller made it.
    private bool WantsTwinWrap(LayoutContext context) =>
        _lines is not null
            ? _activeIsTwin
            : TwinWrapApplies() && (context.ModeFor(this) == ContinuationMode.Twin || _startLine != 0);

    // The wrap an instance reads is the one its first layout made, as the wrap has always been made
    // once, at the width of the first call. A piece of a split carries the wrap of the layout that
    // made it, so a placement reads one wrap from its first line to its last; the document that
    // chooses the twin lays out the twin's own instance from the start.
    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lines), nameof(_lineEnds))]
    private void Activate(double width, bool twin)
    {
        if (_lines is null) _activeIsTwin = twin;
        EnsureWrapped(width);
    }

    // Wraps in the mode that is not the active one, at the width of the first wrap.
    private void MakeOther(double width, bool twin)
    {
        var at = double.IsNaN(_firstWidth) ? width : _firstWidth;
        if (twin)
        {
            (_otherLines, _otherEnds) = WordWrapAsBefore(_para.Runs, at);
            return;
        }

        var stops = StopsWithin(_para, at, out _);
        var runs = PageReferences.Effective(_para.Runs, ref _reads);
        var cutsBefore = LongWordBreaks.PlaceCuts;
        (_otherLines, _otherEnds) = WordWrap(runs, at, out _, stops);
        _cutAtAPlace = LongWordBreaks.PlaceCuts != cutsBefore;
        _mayDiffer = 0;
    }

    // The wrap this renderer made in the active mode is the twin's when _activeIsTwin.
    internal static (List<LineFragment[]> Lines, List<LineEnd> Ends) WordWrapAsBefore(
        IReadOnlyList<TextRun> runs, double maxWidth)
    {
        var lines = WordWrapBefore(runs, maxWidth);
        var ends = new List<LineEnd>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            ends.Add(i == lines.Count - 1 ? LineEnd.Last : LineEnd.Soft);
        return (lines, ends);
    }

    // Whether this paragraph can need a different number of lines from its twin, from what the
    // wrap used: a cut at a place, a word that continues over a run boundary, or one of the two
    // no-break spaces 2.3.3 read as a space. Read once; false when there is no twin wrap.
    private bool MayDiffer()
    {
        if (_mayDiffer != 0) return _mayDiffer == 1;

        var may = false;
        if (TwinWrapApplies())
        {
            may = _cutAtAPlace;
            var runs = _para.Runs;
            for (var i = 0; i < runs.Count && !may; i++)
            {
                var text = runs[i].Text;
                if (text.AsSpan().IndexOfAny(' ', ' ') >= 0) may = true;
                else if (i + 1 < runs.Count && text.Length > 0 && runs[i + 1].Text.Length > 0
                    && !char.IsWhiteSpace(text[^1]) && !char.IsWhiteSpace(runs[i + 1].Text[0]))
                    may = true;
            }
        }

        _mayDiffer = may ? (byte)1 : (byte)2;
        return may;
    }

    // True when the wrap now in _lines, or the one a cut at a place made, used a place.
    private bool _cutAtAPlace;

    /// <summary>
    /// The number of continuations this paragraph can take in either wrap, when the two wraps give
    /// it a different number of lines, and null when they do not or when there is no twin wrap.
    /// Wraps both ways when the text can differ, so a paragraph that cannot costs nothing more.
    /// </summary>
    internal long? TwinDifferenceBound(double width)
    {
        if (!TwinWrapApplies()) return null;

        // The current wrap first, because it is what decides whether the text can differ.
        EnsureNewWrap(width);
        if (!MayDiffer()) return null;

        var current = (_activeIsTwin ? _otherLines : _lines)!.Count;
        if (TwinLineCount(width) is not { } before) return 2L + TextLength();
        return current == before ? null : 2L + Math.Max(current, before);
    }

    // The number of lines of the twin's wrap, made without recording the glyphs it measures: the
    // current wrap has recorded every character already, and a wrap that is only compared must not
    // change what a font embeds. Null when 2.3.3 refused the text, which a text the current wrap
    // accepts can still be (a surrogate pair split between two runs): the caller then bounds by
    // the length of the text, which covers any wrap, and the document's probes find the refusal.
    private int? TwinLineCount(double width)
    {
        if (_activeIsTwin) return _lines!.Count;
        if (_otherLines is not null) return _otherLines.Count;

        var previous = FontReference.MeasuringWithoutRegistering;
        FontReference.MeasuringWithoutRegistering = true;
        try
        {
            MakeOther(width, twin: true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            _otherLines = null;
            _otherEnds = null;
            return null;
        }
        finally
        {
            FontReference.MeasuringWithoutRegistering = previous;
        }

        return _otherLines!.Count;
    }

    private long TextLength()
    {
        long length = 0;
        foreach (var run in _para.Runs) length += run.Text.Length;
        return length;
    }

    // The current wrap exists, whichever slot holds it.
    private void EnsureNewWrap(double width)
    {
        if (!_activeIsTwin)
        {
            EnsureWrapped(width);
            return;
        }

        if (_otherLines is null) MakeOther(width, twin: false);
    }

    // ── 2.3.3's wrap ──────────────────────────────────────────────────────────

    private static string NormaliseWhitespaceBefore(string s)
    {
        const char nbsp = ' ';
        var needsNorm = false;
        foreach (var c in s)
        {
            if (c != '\n' && c != nbsp && char.IsWhiteSpace(c))
            {
                needsNorm = true;
                break;
            }
        }

        if (!needsNorm) return s;

        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c != '\n' && c != nbsp && char.IsWhiteSpace(c)) sb.Append(' ');
            else sb.Append(c);
        }

        return sb.ToString();
    }

    // Greedy wrap of the runs as 2.3.3 made it: each run is split into words of its own, so a word
    // never continues over a run boundary, and a word wider than the line is cut at the glyph.
    private static List<LineFragment[]> WordWrapBefore(IReadOnlyList<TextRun> runs, double maxWidth)
    {
        var lines = new List<LineFragment[]>();
        var tokens = new List<(string Word, TextStyle Style, bool HardBreakBefore)>();
        foreach (var run in runs)
        {
            var normalised = run.Text.Replace("\r\n", "\n").Replace('\r', '\n');
            var hardLines = normalised.Split('\n');
            for (var hl = 0; hl < hardLines.Length; hl++)
            {
                var words = NormaliseWhitespaceBefore(hardLines[hl]).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var isFirstHardLine = hl == 0;
                var isFirstWord = true;
                foreach (var w in words)
                {
                    tokens.Add((w, run.Style, !isFirstHardLine && isFirstWord));
                    isFirstWord = false;
                }

                if (words.Length == 0 && !isFirstHardLine)
                    tokens.Add((string.Empty, run.Style, true));
            }
        }

        if (tokens.Count == 0)
        {
            lines.Add([]);
            return lines;
        }

        var lineFrags = new List<(string Word, TextStyle Style, double Width)>();
        var lineWidth = 0.0;
        for (var ti = 0; ti < tokens.Count; ti++)
        {
            var (word, style, hardBreakBefore) = tokens[ti];
            if (hardBreakBefore)
            {
                lines.Add(BuildFragments(lineFrags));
                lineFrags.Clear();
                lineWidth = 0.0;
            }

            if (word.Length == 0) continue;

            _ = style.FontRef.MeasureString(" ", style.FontSize);
            var wordW = style.FontRef.MeasureString(word, style.FontSize);
            if (lineFrags.Count == 0)
            {
                if (wordW > maxWidth)
                {
                    HardBreakWordBefore(word, style, maxWidth, lines);
                    lineWidth = 0.0;
                    continue;
                }

                lineFrags.Add((word, style, wordW));
                lineWidth = wordW;
                continue;
            }

            var prevStyle = lineFrags[^1].Style;
            var prevSpaceW = prevStyle.FontRef.MeasureString(" ", prevStyle.FontSize);
            if (lineWidth + prevSpaceW + wordW <= maxWidth)
            {
                var last = lineFrags[^1];
                if (last.Style == style)
                {
                    lineFrags[^1] = (last.Word + " " + word, style, last.Width + prevSpaceW + wordW);
                }
                else
                {
                    lineFrags[^1] = (last.Word + " ", last.Style, last.Width + prevSpaceW);
                    lineFrags.Add((word, style, wordW));
                }

                lineWidth += prevSpaceW + wordW;
                continue;
            }

            lines.Add(BuildFragments(lineFrags));
            lineFrags.Clear();
            if (wordW > maxWidth)
            {
                HardBreakWordBefore(word, style, maxWidth, lines);
                lineWidth = 0.0;
            }
            else
            {
                lineFrags.Add((word, style, wordW));
                lineWidth = wordW;
            }
        }

        if (lineFrags.Count > 0) lines.Add(BuildFragments(lineFrags));
        return lines;
    }

    private static void HardBreakWordBefore(string word, TextStyle style, double maxWidth, List<LineFragment[]> lines)
    {
        var fragment = new System.Text.StringBuilder();
        var fragmentW = 0.0;
        foreach (var rune in word.EnumerateRunes())
        {
            var ch = rune.ToString();
            var charW = style.FontRef.MeasureString(ch, style.FontSize);
            if (fragment.Length > 0 && fragmentW + charW > maxWidth)
            {
                lines.Add([new LineFragment(fragment.ToString(), style, fragmentW)]);
                fragment.Clear();
                fragmentW = 0.0;
            }

            fragment.Append(ch);
            fragmentW += charW;
        }

        if (fragment.Length > 0)
            lines.Add([new LineFragment(fragment.ToString(), style, fragmentW)]);
    }
}
