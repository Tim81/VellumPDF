// Copyright 2026 Timothy van der Ham (@Tim81)
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
}
