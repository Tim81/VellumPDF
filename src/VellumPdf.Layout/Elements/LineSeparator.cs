// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>A horizontal rule drawn as a full-width line.</summary>
public sealed class LineSeparator
{
    /// <summary>Stroke width of the rule in points.</summary>
    /// <remarks>
    /// <b>A non-finite width is refused.</b> <see cref="Document.Save(System.IO.Stream)"/>
    /// throws <see cref="InvalidOperationException"/> naming the width, because the number would
    /// reach the content stream as a token no reader can parse.
    /// <para><b>Do not use zero to hide the rule.</b> Zero is accepted and is not a no-op: it
    /// asks the device for the thinnest line it can draw, which is one pixel at any zoom and so
    /// grows heavier as the page is scaled down. To omit the rule, omit the element.</para>
    /// <para><b>Do not pass a negative width.</b> It is not refused today and its effect is the
    /// renderer's, not the format's. A later major version will reject it.</para>
    /// </remarks>
    public double LineWidth { get; init; } = 1;

    /// <summary>Stroke color of the rule.</summary>
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    /// <summary>Margins around the rule.</summary>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6, 0, 6, 0);
}
