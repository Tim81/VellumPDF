// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Internal: one styled text segment within a wrapped line.
/// Produced by word-wrapping and consumed by ParagraphRenderer.Draw.
/// </summary>
internal sealed class LineFragment(string Text, TextStyle Style, double Width)
{
    public string Text { get; } = Text;
    public TextStyle Style { get; } = Style;
    public double Width { get; } = Width;

    /// <summary>
    /// Where the fragment starts, in points from the left edge of the paragraph's content, when a
    /// tab stop placed it; null when it follows the fragment before it.
    /// </summary>
    public double? StartX { get; init; }

    /// <summary>The leader to draw before this fragment, or null.</summary>
    public LineLeader? Leader { get; init; }
}

/// <summary>
/// Internal: the span a tab stop's leader fills, in points from the left edge of the paragraph's
/// content, and the style whose size and colour it takes.
/// </summary>
internal sealed record LineLeader(TabLeader Kind, double From, double To, TextStyle Style);
