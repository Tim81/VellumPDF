// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

// The paragraph's twin: the wrap as 2.3.3 made it, which keeps the verdict of the continuation
// limit (#687, #594). It differs from the current wrap in the ways IRenderer.Layout lists, which
// are where this library wraps as 2.3.3 did. The twin is used where the document's limit decision
// chooses it, and where a context the document did not address, or a start line a caller made,
// calls for it. Everything in this file is removed in 3.0 (#688), which wraps one way under every
// context.
public sealed partial class ParagraphRenderer : IContinuationTwin
{
    // Whether the wrap in _lines is the twin's. An instance wraps once, in the mode of its first
    // layout, and the pieces of its splits keep that wrap, so one placement reads one wrap from its
    // first line to its last.
    private bool _activeIsTwin;

    // 0 until read, then 1 when the 2.3.3 wrap applies to this paragraph and 2 when it does not:
    // 2.3.3 had no tab stops and no page references, so a paragraph that holds one of them has no
    // twin wrap and wraps as it does now. A run with no style that holds a word is refused by the
    // current wrap before any decision, and one that holds none wraps alike in both.
    private byte _twinApplies;

    // Whether the tokeniser joined a word across a run boundary when it made the wrap in _lines.
    private bool _joinedAcrossRuns;

    // 0 until read, then 1 when the current normalisation of white space reads the text as 2.3.3
    // did not (U+202F and U+2007) and 2 when it reads it alike.
    private byte _readsApart;

    private ParagraphRenderer? _twin;

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
            if (run.IsReference)
            {
                applies = false;
                break;
            }
        }

        _twinApplies = applies ? (byte)1 : (byte)2;
        return applies;
    }

    // Whether the layout is the twin's. An instance that has wrapped keeps its wrap. One that has
    // not takes the twin's only where the layout is chosen because of the continuation limit: the
    // twin the document chose or is probing, which it has addressed in the mode Twin, and a child
    // of a list laid out for the limit, whose context says so. Under any other context the current
    // wrap applies, as on main.
    private bool WantsTwinWrap(LayoutContext context) =>
        _lines is not null
            ? _activeIsTwin
            : TwinWrapApplies()
                && (context.WrapAsBefore || context.IsLimitTwin(this));

    [System.Diagnostics.CodeAnalysis.MemberNotNull(nameof(_lines), nameof(_lineEnds))]
    private void Activate(double width, bool twin)
    {
        if (_lines is null) _activeIsTwin = twin;
        EnsureWrapped(width);
    }

    internal static (List<LineFragment[]> Lines, List<LineEnd> Ends) WordWrapAsBefore(
        IReadOnlyList<TextRun> runs, double maxWidth)
    {
        var lines = WordWrapBefore(runs, maxWidth);
        var ends = new List<LineEnd>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
            ends.Add(i == lines.Count - 1 ? LineEnd.Last : LineEnd.Soft);
        return (lines, ends);
    }

    /// <summary>
    /// The continuations this paragraph can take in either wrap, to report to the limit decision,
    /// and null when it has no twin wrap. Every line of either wrap holds a character, so the
    /// bound is the length of the text plus two; it holds from any start. The decision probes the
    /// element only where this bound passes what the limit leaves, so a paragraph far from the
    /// limit costs no layout pass.
    /// </summary>
    internal long? TwinBound() => TwinWrapApplies() ? 2L + TextLength() : null;

    // Whether the two wraps can give this paragraph more lines than its own: its tokeniser joined a
    // word across a run boundary (the wrap's own decision, made whether the runs share a style
    // instance or not), or its normalisation of white space read a character as part of a word
    // where 2.3.3's read it as a space (U+202F, U+2007). A cut at a place is not read: with the same
    // words, a cut at the glyph never takes more lines than a cut after a place, because each of its
    // lines starts no earlier and reaches at least as far, so the current lines already bound the
    // twin's. It keeps no list of characters of its own. Call after the wrap.
    private bool WrapsCanDiffer()
    {
        if (_joinedAcrossRuns) return true;
        if (_readsApart != 0) return _readsApart == 1;

        var apart = false;
        foreach (var run in _para.Runs)
        {
            if (NormaliseWhitespace(run.Text) != NormaliseWhitespaceBefore(run.Text))
            {
                apart = true;
                break;
            }
        }

        _readsApart = apart ? (byte)1 : (byte)2;
        return apart;
    }

    private long TextLength()
    {
        long length = 0;
        foreach (var run in _para.Runs) length += run.Text.Length;
        return length;
    }

    // ── 2.3.3's wrap ──────────────────────────────────────────────────────────

    private static string NormaliseWhitespaceBefore(string s)
    {
        const char nbsp = '\u00A0';
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
