// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Reader;

/// <summary>
/// Folds a page's own <see cref="PositionedGlyph"/> stream, as <see cref="TextExtractionVisitor"/>
/// produces it in content order, into <see cref="TextRun"/>s: one run per baseline, a new run (and
/// a newline once the runs are joined) whenever <see cref="PositionedGlyph.LineY"/> changes from
/// the previous glyph (#98). Never retains the glyph stream itself — <see cref="Add"/> is called
/// once per glyph and only <see cref="Runs"/>, already reduced to whole lines, survives past that
/// call — so this type's own memory use is bounded by the run count, not the glyph count.
/// </summary>
internal sealed class TextAssembler
{
    // Purely floating-point tolerance on the LineY comparison: the same Trm.Concat chain evaluated
    // for two glyphs on the one nominal baseline can differ in its last bit or two from a
    // fused-multiply-add path taken by one call and not the other, not a layout heuristic. The
    // later word-gap/paragraph-heuristics PR gets its own constants for that; this one exists only
    // so round-off cannot masquerade as a baseline change.
    private const double LineYTolerance = 1e-6;

    private readonly int _pageIndex;
    private readonly TextCallBudget _budget;
    private readonly List<TextRun> _runs = [];

    private StringBuilder? _currentText;
    private double _currentStartX;
    private double _currentEndX;
    private double _currentLineY;
    private double _currentFontSize;

    /// <summary>Every run closed so far, in content order. <see cref="Finish"/> must be called once
    /// the page's glyph stream is exhausted so the last, still-open run is included here too.</summary>
    internal IReadOnlyList<TextRun> Runs => _runs;

    internal TextAssembler(int pageIndex, TextCallBudget budget)
    {
        _pageIndex = pageIndex;
        _budget = budget;
    }

    /// <summary>
    /// Folds one glyph into the run currently being built, opening a new run first when its <see
    /// cref="PositionedGlyph.LineY"/> differs from the run in progress (or none is open yet: the
    /// page's first glyph always opens one). A non-finite <paramref name="glyph"/>.<see
    /// cref="PositionedGlyph.LineY"/> is treated as the SAME line as whatever came before, so a
    /// malformed <c>Tm</c>/<c>cm</c> cannot turn every remaining glyph on the page into its own
    /// one-glyph run. Returns <see langword="false"/> once <see cref="TextCallBudget.TryConsumeRun"/>
    /// refuses a new run, meaning the caller should stop feeding this page any further glyphs.
    /// </summary>
    internal bool Add(PositionedGlyph glyph)
    {
        if (_currentText is null || !SameLine(glyph.LineY, _currentLineY))
        {
            if (!_budget.TryConsumeRun(_pageIndex))
                return false;

            FlushCurrentRun();
            _currentText = new StringBuilder();
            _currentStartX = glyph.Trm.E;
            _currentLineY = glyph.LineY;
            _currentFontSize = glyph.FontSize;
        }
        else if (!double.IsFinite(_currentLineY) && double.IsFinite(glyph.LineY))
        {
            // SameLine treats a non-finite _currentLineY as matching ANY glyph, finite or not
            // (see its own remarks), which is what stops a malformed Tm/cm from fragmenting the
            // page into one run per glyph while the overflow lasts. Left at that, the stuck
            // non-finite key would go on matching every later glyph forever, even once real,
            // finite positions return — folding every genuinely distinct line after the
            // excursion into this one run instead of just the excursion itself. Adopting the
            // first finite LineY seen after the excursion re-anchors the key, so the NEXT actual
            // line break is judged against a real baseline again.
            _currentLineY = glyph.LineY;
        }

        _currentEndX = glyph.Trm.E;
        if (glyph.Characters.Length > 0)
            _currentText!.Append(glyph.Characters);
        return true;
    }

    /// <summary>Closes whatever run is still open, so it appears in <see cref="Runs"/>. Idempotent:
    /// calling it with no open run does nothing.</summary>
    internal void Finish() => FlushCurrentRun();

    private void FlushCurrentRun()
    {
        if (_currentText is null)
            return;

        _runs.Add(
            new TextRun(_currentText.ToString(), _currentStartX, _currentEndX, _currentLineY,
                _currentFontSize, _pageIndex));
        _currentText = null;
    }

    private static bool SameLine(double lineY, double currentLineY) =>
        !double.IsFinite(lineY) || !double.IsFinite(currentLineY) || Math.Abs(lineY - currentLineY) <= LineYTolerance;
}
