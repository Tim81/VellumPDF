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
    /// <para><b>Attention</b>: zero is drawn, <b>not</b> skipped, though not always visible either.
    /// ISO 32000-2, 8.4.3.2 defines it as the
    /// thinnest line the device can render, one device pixel wide, and calls that
    /// device-dependent. Resolution and zoom then pull in opposite directions. On a
    /// high-resolution device, that one pixel is small enough that the same clause says the
    /// result can be nearly invisible. Independently of resolution, displaying the page at a
    /// smaller zoom does not shrink the rule with it: the line stays one device pixel wide while
    /// everything around it shrinks, so it reads as proportionally heavier the further the page is
    /// scaled down. If you want no rule, leave the element out.</para>
    /// <para>A negative width is not refused today, though ISO 32000-2, 8.4.3.2 requires a line
    /// width to be a non-negative number: the token this writes is one the specification forbids,
    /// so it is the format's business rather than the renderer's. Whether to refuse it is being
    /// decided in #482.</para>
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
