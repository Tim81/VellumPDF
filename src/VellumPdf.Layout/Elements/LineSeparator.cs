// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>A horizontal rule drawn as a full-width line.</summary>
/// <remarks>
/// <see cref="LineWidth"/> and <see cref="Margins"/> are refused when non-finite, from save.
/// Zero width is drawn as the thinnest device line.
/// </remarks>
public sealed class LineSeparator
{
    /// <summary>Creates a rule with a 1pt black stroke and default margins.</summary>
    /// <remarks>See <see cref="LineWidth"/> and <see cref="Margins"/> for refusals at save.</remarks>
    public LineSeparator() { }

    /// <summary>Stroke width of the rule in points.</summary>
    /// <remarks>
    /// A non-finite width is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> and names the width. The number would otherwise
    /// reach the content stream as a token that no reader can parse.
    /// <para><b>Attention</b>: zero is drawn, <b>not</b> skipped. ISO 32000-2, 8.4.3.2: a line
    /// width of zero shall denote the thinnest line that can be rendered at device resolution,
    /// one device pixel wide. The same clause says such lines are nearly invisible on
    /// high-resolution devices, that the result is device-dependent, and that zero-width lines
    /// should not be used. That clause is about device resolution.
    /// Zoom is a separate matter: the line stays one device pixel while everything around it
    /// shrinks, so it reads as proportionally heavier the further the page is scaled down. If you
    /// want no rule, leave the element out.</para>
    /// <para>A negative width is not refused today, though ISO 32000-2, 8.4.3.2 requires a line
    /// width to be a non-negative number: the token this writes is one the specification forbids,
    /// so refusing a negative width is the format's business, not only the renderer's. Whether to
    /// refuse it is undecided (#482).</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property,
    /// when the width is not finite. The message names the separator. It is also raised, as the
    /// too-tall exception, when the width is large enough that the separator does not fit on a
    /// page.
    /// </exception>
    public double LineWidth { get; init; } = 1;

    /// <summary>Stroke color of the rule.</summary>
    /// <remarks>Stored as given. Channels are not clamped; see <see cref="ColorRgb"/>.</remarks>
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    /// <summary>Margins around the rule.</summary>
    /// <remarks>
    /// A non-finite inset is refused. <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> reading <c>A line separator has a non-finite
    /// inset. Every inset must be a finite number.</c> The <c>Top</c> and <c>Bottom</c> insets set the
    /// rule's own y coordinate, so either one alone would put a token no reader can parse into
    /// the <c>m</c> and <c>l</c> operators.
    /// <para>All four edges are checked, and the message names none of them, so it tells you the
    /// separator is at fault rather than which edge you set. <c>Left</c> and <c>Right</c> are
    /// checked too, though the rule spans the content width and neither of them moves it.</para>
    /// <para>A negative <c>Top</c> or <c>Bottom</c> is not refused: it moves the rule, or the
    /// elements after it, up the page.</para>
    /// <para>Do not pass one. A later major version will refuse it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property,
    /// when any of the four insets is not finite. It is also raised, as the too-tall exception,
    /// when <c>Top</c> and <c>Bottom</c> are large enough that the separator does not fit on a
    /// page.
    /// </exception>
    public EdgeInsets Margins { get; init; } = new EdgeInsets(6, 0, 6, 0);
}
