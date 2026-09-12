// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>A horizontal rule drawn as a full-width line.</summary>
public sealed class LineSeparator
{
    /// <summary>Stroke width of the rule in points.</summary>
    /// <remarks>
    /// A non-finite width is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the width. The number would otherwise
    /// reach the content stream as a token that no reader can parse.
    /// <para>Attention: zero does <b>not</b> hide the rule. ISO 32000-2, 8.4.3.2 defines a line
    /// width of zero as the thinnest line the device can render, one pixel wide. That is one pixel
    /// at any zoom, so the rule grows heavier as the page is scaled down. If you want no rule,
    /// leave the element out.</para>
    /// <para>A negative width is not refused. What it draws is the renderer's business rather
    /// than the format's, so do not rely on it. A later major version will reject it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property, when the width is not finite. The message names the separator.
    /// </exception>
    public double LineWidth { get; init; } = 1;

    /// <summary>Stroke color of the rule.</summary>
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    /// <summary>Margins around the rule.</summary>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6, 0, 6, 0);
}
