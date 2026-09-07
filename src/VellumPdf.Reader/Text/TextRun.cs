// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// One line's worth of text <see cref="TextAssembler"/> accumulated before the baseline changed
/// (#98). Stays internal: a caller-facing bounding box would need <c>/Ascent</c>, <c>/Descent</c>,
/// or <c>/FontBBox</c> from a font descriptor that a standard-14 PDF 1.x font may omit entirely, so
/// this run's own height would be a guess this reader does not make; a test reaches this type
/// through <c>InternalsVisibleTo</c> instead of a public accessor.
/// </summary>
/// <param name="Text">The run's own text, in content order, with no newline of its own: the newline
/// between two runs is <see cref="TextAssembler"/>'s job, not this type's.</param>
/// <param name="StartX">The first glyph's own <c>Trm</c> origin X.</param>
/// <param name="EndX">The last glyph's own <c>Trm</c> origin X — the glyph's OWN advance is not
/// added, so a single-glyph run reports <see cref="StartX"/> and <see cref="EndX"/> equal.</param>
/// <param name="Baseline">The run's own line-grouping key (<see cref="PositionedGlyph.LineY"/>,
/// rise forced to zero), shared by construction across every glyph the run holds.</param>
/// <param name="FontSize">The font size in effect for the run's first glyph. A run does not split
/// on a font-size change mid-line, so a later glyph in the same run may have shown at a different
/// size than this.</param>
/// <param name="PageIndex">The zero-based page this run came from.</param>
internal readonly record struct TextRun(
    string Text, double StartX, double EndX, double Baseline, double FontSize, int PageIndex);
