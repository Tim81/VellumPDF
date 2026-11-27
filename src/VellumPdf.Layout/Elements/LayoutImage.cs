// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>An image element that can be placed in document flow.</summary>
/// <remarks>
/// <see cref="Width"/> and <see cref="Height"/> carry the size refusals. A null
/// <see cref="Image"/> makes the save throw. <see cref="HorizontalAlignment.Justify"/> is drawn
/// as <see cref="HorizontalAlignment.Left"/>. An image that is too tall for a page makes the save
/// throw unless <see cref="FitToPage"/> scales it down.
/// </remarks>
public sealed class LayoutImage
{
    /// <summary>The image to draw.</summary>
    /// <remarks>
    /// Never null; the constructor refuses a null. An image whose own pixel width or height
    /// is not a positive number makes the save throw. A JPEG that declares a zero dimension loads
    /// as such an image.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the image's own pixel width or height is not a positive number; a
    /// JPEG that declares a zero dimension loads as such an image. <c>ParamName</c> is <c>_img</c>.
    /// It is raised before <see cref="Width"/> and <see cref="Height"/> are checked.
    /// </exception>
    public PdfImageXObject Image { get; }

    /// <summary>
    /// Display width in points; when null the image fits the available width. Must be finite or
    /// positive infinity, and at least 5e-6 points in magnitude. The height derived from the width
    /// must clear the same floor, so a width above it is still refused when the source is wide
    /// enough that its proportional height falls below it.
    /// </summary>
    /// <remarks>
    /// A value wider than the available width is clamped to it, so this is an upper bound rather
    /// than a guaranteed display size. Positive infinity is therefore accepted, and fills the
    /// content box.
    /// <para>Zero and non-finite values other than positive infinity are <b>refused</b>.
    /// <see cref="Document.Save(System.IO.Stream)"/> throws <see cref="InvalidOperationException"/>
    /// naming the width. A zero width writes a transformation matrix that cannot be inverted, which
    /// ISO 32000-2 leaves undefined for a painted image, and it also collapses the height, which is
    /// derived from the width. A width of NaN writes a token that is not a PDF number.</para>
    /// <para>Do <b>not</b> pass a negative width. It is not refused today. With
    /// <see cref="Height"/> left null the derived height takes the sign too, so the image is
    /// rotated by 180 degrees rather than mirrored; with an explicit height it is mirrored
    /// horizontally. Either way it is a side effect of the transformation matrix rather than a
    /// supported way to flip an image, and a later major version will reject it.</para>
    /// <para>The check is on the token written, not on the value held. <c>PdfCanvas</c>
    /// formats coordinates to five decimals, so any magnitude below 5e-6 is written as <c>0</c>
    /// and the matrix is singular whatever you passed. At the boundary, 5e-6 writes
    /// <c>0.00001</c> and 4.9e-6 writes <c>0</c>.</para>
    /// <para>The width is validated before the height, so a width under the boundary is always
    /// what the message names, whatever the source image's aspect ratio. Above the boundary it is
    /// the other way round: a width that is legal but small collapses the derived height of a
    /// non-square image, and then the height is named. A width of 5e-6 on a 100 by 1 source
    /// reports a height of 0.0000001.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this property,
    /// for zero, for a magnitude under 5e-6, and for NaN or negative infinity. It is also raised
    /// when the height derived from a width that clears the floor falls under it, and the message
    /// then names the height rather than the width. Positive infinity is not refused as a
    /// non-finite width: it is clamped to the content box's width. The height derived from that
    /// clamped width can still exceed the box's height, and that overflow throws this same type
    /// through the generic too-tall exception. For an image the document lays out, the message
    /// then names the derived height and the content height. Whether it does is decided by
    /// proportion rather than by absolute size, so a source proportionally taller than the box is
    /// refused however narrow it is, unless <see cref="FitToPage"/> scales it down.
    /// </exception>
    public double? Width { get; init; }  // null = fit to available width

    /// <summary>
    /// Display height in points; when null the aspect ratio is maintained. Must be finite and at
    /// least 5e-6 points in magnitude.
    /// </summary>
    /// <remarks>
    /// Setting this is a request for a non-proportional box: unlike a null height, it is not
    /// rescaled when the width is clamped to the content box. An image taller than the remaining
    /// space moves to the next page. One taller than a page makes the save throw
    /// <see cref="InvalidOperationException"/>, unless <see cref="FitToPage"/> scales it down. For
    /// an image the document lays out, the message names this height and the content height.
    /// <para>Zero and non-finite values are <b>refused</b>, for the same reason as
    /// <see cref="Width"/>: the emitted transformation matrix is either singular or not made of PDF
    /// numbers. Do <b>not</b> pass a negative height; it is not refused today and flips the image
    /// vertically as a side effect.</para>
    /// <para>Any magnitude below 5e-6 is refused too. The canvas writes it as the token
    /// <c>0</c>, as described on <see cref="Width"/>.</para>
    /// <para><b>Attention</b>: setting both this and <see cref="Width"/> overrides the aspect ratio. The
    /// image is not fitted inside the pair, so it is distorted whenever the two disagree with its
    /// own proportions. If you want it fitted, set one and leave the other null.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> rather than from this
    /// property, for zero, for a magnitude under 5e-6, and for any non-finite value.
    /// </exception>
    public double? Height { get; init; }  // null = maintain aspect ratio

    /// <summary>
    /// Whether an image taller than a page's content height, less its <see cref="Margins"/>, is
    /// scaled down to that height, keeping its width-to-height ratio. The default,
    /// <see langword="false"/>, leaves the size as
    /// <see cref="Width"/> and <see cref="Height"/> give it.
    /// </summary>
    /// <remarks>
    /// <b>A fitted extent too small to paint is refused.</b>
    /// <see cref="Document.Save(System.IO.Stream)"/> throws <see cref="InvalidOperationException"/>
    /// when the scale takes the width or the height under 5e-6 points, because the canvas writes
    /// it as <c>0</c> and the transformation matrix has no inverse. A 1 by 2147483647 pixel image
    /// fitted to a 400 point height is about 1.9e-7 points wide. The exception comes from the
    /// save, not from this <c>init</c> accessor, which has nothing to refuse.
    /// <para>The image is sized first as it is without this member, from <see cref="Width"/>,
    /// <see cref="Height"/>, the source's proportions and the clamp to the content width. When
    /// that height is more than a page's content height, less <see cref="Margins"/>, both extents
    /// are multiplied by the one factor that makes the height equal it. This applies to whatever
    /// the other members produced, so an explicit <see cref="Height"/> is scaled too, along with
    /// the width it was stretched to. The image is never enlarged: one that already fits keeps
    /// its size, and the output is the same as with the default.</para>
    /// <para>The content height is that of a fresh page, not what is left on the current one. An
    /// image that fits a page but not the space below the previous element moves to the next page
    /// at the size it has there, as it does without this member. Only the height of that page's
    /// content area is read, so mirrored margins do not change the size. A running header or
    /// footer shortens it, as it shortens any element's area. When a renderer is laid out
    /// through a <see cref="LayoutContext"/> built by calling its public constructor, no fresh
    /// page is known, and the image is fitted to the area that context offers.</para>
    /// <para>The fresh page travels with the context the document passes, and the context's
    /// <c>WithArea</c> method keeps it. A renderer of yours that wraps a
    /// <see cref="Rendering.LayoutImageRenderer"/> and lays it out in a smaller area through
    /// that method therefore still gets the image fitted to the page, not to the smaller area.
    /// An image that fits the page and not that area is then refused as too tall, and that
    /// message has no closing reason. Pass the context you were given to the image's renderer
    /// unchanged to get the document's behaviour.</para>
    /// <para>When no positive height is left once the margins are taken off, the image is not
    /// scaled and the save throws the too-tall exception, as it does without this member.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the fitted width or height is under 5e-6 points, or when no
    /// positive height is left for the scale, as described above. For a fitted extent the
    /// message names that extent; with no positive height left, it is the too-tall message,
    /// which, for an image the document lays out, names the image's height and the content
    /// height. A size refused before the scale, such as a <see cref="Width"/> of zero, is
    /// refused with the message it has without this member.
    /// </exception>
    public bool FitToPage { get; init; }

    /// <summary>Horizontal alignment of the image within the available width.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> is neither refused nor honoured. It falls through
    /// to left alignment. An image is one box; there is nothing to justify against.
    /// </remarks>
    public HorizontalAlignment Alignment { get; init; } = HorizontalAlignment.Left;

    /// <summary>Margins around the image.</summary>
    /// <remarks>
    /// With <see cref="FitToPage"/> set, the top and bottom edges are taken off the content
    /// height the image is fitted to, so an image that is scaled down and its margins fill that
    /// height. A non-finite edge is not checked before the layout; see <see cref="Document"/>. The
    /// save throws <see cref="ArgumentException"/> from <see cref="VellumPdf.Canvas.PdfCanvas"/>
    /// only where it would write the value.
    /// <b>Do not
    /// pass</b> a
    /// non-finite edge: 3.0 refuses it.
    /// <para><b>Attention</b>: no other edge is checked. The top and bottom edges add to the space the
    /// image takes on the page. The left and right edges work differently by <see cref="Width"/>: with
    /// it set, the image keeps that width, up to the content width, and its position is clamped back
    /// inside the content box; with it null, the image is sized to the width the edges leave. A
    /// negative edge, or edges wider than the area, can make the save throw an exception about
    /// something else, or move, mirror or rotate the image. A negative top or bottom edge can also
    /// move the elements placed after this one.</para>
    /// <para>Do not pass a negative edge. 3.0 will refuse it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the box the edges leave is too small for the element, in
    /// which case the message says the element is too tall to fit on a page. For an image the
    /// document lays out, its last sentence names the image's height and the content height left
    /// inside these margins, not the margins themselves. With <see cref="Width"/> null, the width
    /// the edges leave is refused when it is under 5e-6 in magnitude, or not finite, and so is a
    /// height derived from it that is under 5e-6 or not finite. The message then names that width
    /// or height instead.
    /// </exception>
    /// <inheritdoc cref="Paragraph.Margins" path="/exception[@cref='T:System.ArgumentException']"/>
    public EdgeInsets Margins { get; init; } = EdgeInsets.Zero;

    /// <summary>Whether the image starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The
    /// default, <see cref="PageBreakMode.None"/>, starts the element where the previous one ended.
    /// <para>It is honoured when the image is added with <see cref="Document.Add(LayoutImage)"/>,
    /// or when its <see cref="Rendering.LayoutImageRenderer"/> is added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. An image does not split. See
    /// <see cref="PageBreakMode"/> for what else is ignored and how parity is counted, and
    /// <see cref="PageBreak"/> for how breaks combine.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is not one of
    /// <see cref="PageBreakMode.None"/>, <see cref="PageBreakMode.Page"/> and
    /// <see cref="PageBreakMode.Recto"/>.
    /// </exception>
    public PageBreakMode BreakBefore
    {
        get => _breakBefore;
        init => _breakBefore = PageBreakModes.Validate(value);
    }

    private PageBreakMode _breakBefore;

    /// <summary>
    /// Whether the image stays on the page the next element starts on. The default,
    /// <see langword="false"/>, places the image without looking at what follows.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused, and a request the pages cannot hold is dropped and reported, not
    /// thrown.</b> The group, the order in which requests are dropped, the diagnostic and the extra
    /// layout are those of <see cref="Paragraph.KeepWithNext"/>.
    /// <para>The flag is read from an image added with <see cref="Document.Add(LayoutImage)"/>,
    /// and from a <see cref="Rendering.LayoutImageRenderer"/> added with
    /// <see cref="Document.Add(IRenderer)"/> or
    /// <see cref="Rendering.DocumentRenderer.Add(IRenderer)"/>. An image does not split, so it has
    /// no keep-together flag. Set on an image, this keeps the image and the paragraph after it,
    /// such as its caption, on one page.</para>
    /// </remarks>
    public bool KeepWithNext { get; init; }

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the top edge of this image, inside its margins. <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// The rule, the refusals and the position are those of <see cref="Paragraph.Anchor"/>.
    /// <para>A <see cref="TextStyle.LinkAnchor"/> or a
    /// <see cref="TextRun.PageReference(string, TextStyle)"/> names an anchor; the refusals and
    /// their limits are on <see cref="TextStyle.LinkAnchor"/>. A reference prints the page this
    /// element starts on.</para>
    /// </remarks>
    /// <inheritdoc cref="Paragraph.Anchor" path="/exception"/>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    private string? _anchor;

    /// <summary>
    /// Optional alternate text for the PDF /Figure structure element (tagged PDF).
    /// Used as the <c>/Alt</c> entry on the Figure struct elem when tagging is enabled.
    /// If null, a generic fallback "Figure" is used.
    /// </summary>
    /// <remarks>Null becomes <c>Figure</c> when tagged. Empty is an empty <c>/Alt</c>.</remarks>
    public string? AltText { get; init; }

    /// <summary>Creates a flow image element for the given image.</summary>
    /// <remarks>
    /// A null <paramref name="image"/> is refused here.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="image"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the image's own pixel width or height is not a positive number;
    /// a JPEG that declares a zero dimension loads as such an image. <c>ParamName</c> is
    /// <c>_img</c>. It is raised before <see cref="Width"/> and <see cref="Height"/> are checked.
    /// </exception>
    public LayoutImage(PdfImageXObject image)
    {
        ArgumentNullException.ThrowIfNull(image);
        Image = image;
    }
}
