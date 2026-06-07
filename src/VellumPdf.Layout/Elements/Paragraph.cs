// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A block of text with uniform style. Wraps text across lines and paginates.
/// </summary>
public sealed class Paragraph
{
    public string Text { get; }
    public TextStyle Style { get; }
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    public Paragraph(string text, TextStyle? style = null)
    {
        Text = text;
        Style = style ?? TextStyle.Default;
    }
}
