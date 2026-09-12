// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>An image element that can be placed in document flow.</summary>
public sealed class LayoutImage
{
    /// <summary>The image to draw.</summary>
    public PdfImageXObject Image { get; }

    /// <summary>
    /// Display width in points; when null the image fits the available width. Must be finite and
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// A value wider than the available width is clamped to it, so this is an upper bound rather
    /// than a guaranteed display size. Positive infinity is therefore accepted and simply fills the
    /// content box.
    /// <para><b>Zero and non-finite values other than positive infinity are refused.</b>
    /// <see cref="Document.Save(System.IO.Stream)"/> throws <see cref="InvalidOperationException"/>
    /// naming the width. A zero width writes a transformation matrix that cannot be inverted, which
    /// ISO 32000-2 leaves undefined for a painted image, and it also collapses the height, which is
    /// derived from the width. A width of NaN writes a token that is not a PDF number.</para>
    /// <para><b>Do not pass a negative width.</b> It is not refused today and mirrors the image
    /// horizontally, which is a side effect of the transformation matrix rather than a supported
    /// way to flip an image. A later major version will reject it.</para>
    /// </remarks>
    /// <remarks>
    /// <c>null</c> fits the image to the width available, which is the usual case.
    ///
    /// <b>Zero is refused, and so is anything under 5e-6 in magnitude.</b>
    /// <see cref="Document.Save(System.IO.Stream)"/> throws
    /// <see cref="InvalidOperationException"/> naming the width. The check is on the magnitude
    /// rather than on equality with zero because the canvas writes coordinates to five decimals,
    /// so a width of 4e-6 is written as the token <c>0</c> and the image's transformation matrix
    /// has no inverse either way. Measured at the boundary: 5e-6 writes <c>0.00001</c>, 4.9e-6
    /// writes <c>0</c>.
    /// <para><b>A non-finite width is refused</b> by the same exception, because the number
    /// would reach the content stream as a token no reader can parse.</para>
    /// <para><b>Do not pass a negative width.</b> It is not refused and it is not an error in
    /// the format: it writes a valid matrix that mirrors the image horizontally, which #472 left
    /// in place deliberately. Use it only if that is what is wanted; a later major version will
    /// require it to be stated some other way.</para>
    /// <para>A width wider than the content box is clamped to the box, so the image drawn is
    /// narrower than the number set here and nothing reports the difference.</para>
    /// </remarks>
    public double? Width { get; init; }  // null = fit to available width

    /// <summary>
    /// Display height in points; when null the aspect ratio is maintained. Must be finite and
    /// non-zero.
    /// </summary>
    /// <remarks>
    /// Setting this is a request for a non-proportional box: unlike a null height, it is not
    /// rescaled when the width is clamped to the content box. An image taller than the remaining
    /// space moves to the next page.
    /// <para><b>Zero and non-finite values are refused</b>, for the same reason as
    /// <see cref="Width"/>: the emitted transformation matrix is either singular or not made of PDF
    /// numbers. <b>Do not pass a negative height</b>; it is not refused today and flips the image
    /// vertically as a side effect.</para>
    /// </remarks>
    /// <remarks>
    /// <c>null</c> derives the height from <see cref="Width"/> and the image's own aspect ratio,
    /// which is the usual case.
    ///
    /// <b>Zero, anything under 5e-6 in magnitude, and any non-finite value are refused</b>, for
    /// the reasons given on <see cref="Width"/>: a zero written into the transformation matrix
    /// leaves it with no inverse, and a non-finite value is not a PDF number.
    /// <para><b>Do not pass a negative height</b> unless a vertical mirror is what is wanted.
    /// It is accepted and writes a valid matrix that flips the image.</para>
    /// <para>Setting both this and <see cref="Width"/> overrides the aspect ratio rather than
    /// fitting inside the pair, so the image is distorted if they disagree with it.</para>
    /// </remarks>
    public double? Height { get; init; }  // null = maintain aspect ratio

    /// <summary>Horizontal alignment of the image within the available width.</summary>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>Margins around the image.</summary>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>
    /// Optional alternate text for the PDF /Figure structure element (tagged PDF).
    /// Used as the <c>/Alt</c> entry on the Figure struct elem when tagging is enabled.
    /// If null, a generic fallback "Figure" is used.
    /// </summary>
    public string? AltText { get; init; }

    /// <summary>Creates a flow image element for the given image.</summary>
    public LayoutImage(PdfImageXObject image) => Image = image;
}
