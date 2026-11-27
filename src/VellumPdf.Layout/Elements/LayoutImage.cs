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

    /// <summary>
    /// Loads an image from its bytes, reading the format from the first bytes, and creates the
    /// element for it.
    /// </summary>
    /// <param name="data">
    /// The whole file: PNG, JPEG, TIFF, BMP, GIF, JBIG2 with its 8-byte file header, or JPEG 2000
    /// as a JP2 file or as a bare codestream.
    /// </param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// <b>Data that is empty, or that begins with no signature listed here, is refused.</b>
    /// The call throws <see cref="ArgumentException"/> for an empty array and
    /// <see cref="InvalidDataException"/> for an unknown signature, because a format that cannot be
    /// read from the data cannot be loaded. The message of the second names the first eight bytes
    /// found, in hex. The signatures are the PNG signature, the JPEG start-of-image marker,
    /// <c>II</c> or <c>MM</c> followed by 42 for TIFF, <c>BM</c>, <c>GIF87a</c> and <c>GIF89a</c>,
    /// the JBIG2 file ID string, and for JPEG 2000 the 12-byte JP2 signature box or the
    /// start-of-codestream marker. The box is matched whole, length field included, so a JP2 file
    /// whose signature box declares another length is refused here although
    /// <see cref="JpxImageLoader"/> reads it. Data that ends before a signature does, with every
    /// byte agreeing so far, is refused with a message that says so.
    /// <para>The data then goes to the loader a caller would have called by hand, so a file loads
    /// to the same image as with <see cref="PngImageLoader"/>, <see cref="JpegImageLoader"/>,
    /// <see cref="TiffImageLoader"/>, <see cref="BmpImageLoader"/>, <see cref="GifImageLoader"/>,
    /// <see cref="Jbig2ImageLoader"/> or <see cref="JpxImageLoader"/>. What that loader refuses,
    /// and every limit it applies, is the loader's own: it reaches the caller with the same
    /// exception type and message, and the loader's documentation is where to read it. A JPEG is
    /// passed through as <c>/DCTDecode</c>, never decoded or re-encoded. The format is read from
    /// the data, not from a file name.</para>
    /// <para>Two kinds of data have no signature to read and are not loaded here. A JBIG2 stream in
    /// the embedded form used inside a PDF starts straight at its first segment, and CCITT Group 3
    /// and Group 4 data carries no header and needs its width, height and coding parameters. Load
    /// them with <see cref="Jbig2ImageLoader"/> and <see cref="CcittImageLoader"/> and pass the
    /// result to the constructor. Data that begins with the JBIG2 file ID string is handed to
    /// <see cref="Jbig2ImageLoader"/> as it is, whatever follows the string.</para>
    /// <para>For a JPEG the image keeps a reference to <paramref name="data"/>, not a copy.
    /// <b>Do not change the array</b> before the document is saved; the output holds whatever the
    /// array holds then.</para>
    /// <para>The array itself has no bound here. <see cref="TiffImageLoader"/> can allocate much
    /// more than the file holds before it refuses the file (#534), and this method reaches the
    /// same code.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="data"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="data"/> is empty.</exception>
    /// <exception cref="InvalidDataException">
    /// The data begins with no known signature or ends inside one, or the loader refuses it with the
    /// exception it documents.
    /// </exception>
    /// <exception cref="NotSupportedException">The loader does not support a variant of its format.</exception>
    public static LayoutImage FromBytes(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new LayoutImage(ImageSource.Load(RefuseEmpty(data, nameof(data)), null, null));
    }

    /// <summary>
    /// Loads an image from its bytes as <see cref="FromBytes(byte[])"/> does, with the choices of
    /// <see cref="ImageLoadOptions"/>.
    /// </summary>
    /// <param name="data">The whole file; see <see cref="FromBytes(byte[])"/>.</param>
    /// <param name="options">The options passed to the loader that reads the data.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// <b>The options go to the data's loader exactly as an explicit call passes them.</b> What
    /// the loader does with each member, including a member it ignores for some inputs, is in the
    /// remarks of <see cref="PngImageLoader.Load(byte[], ImageLoadOptions)"/>,
    /// <see cref="TiffImageLoader.Load(byte[], ImageLoadOptions)"/>,
    /// <see cref="Jbig2ImageLoader.Load(byte[], ImageLoadOptions)"/> and
    /// <see cref="JpxImageLoader.Load(byte[], ImageLoadOptions)"/>, and is not repeated here.
    /// <para><b>Options for a loader that takes none are refused.</b> JPEG, BMP and GIF are read by
    /// loaders with no <see cref="ImageLoadOptions"/>, so for them the call throws
    /// <see cref="ArgumentException"/> with <c>ParamName</c> <c>options</c> unless the options equal
    /// <see cref="ImageLoadOptions.Default"/>. A member that is not a defined
    /// <see cref="ImageBitDepth"/> or <see cref="ImageDecodeMode"/> value is refused with
    /// <see cref="ArgumentOutOfRangeException"/> for every format, after the signature is read.
    /// <b>Do not pass</b> options meant for one format when the data may be another: pass the
    /// default, or call <see cref="FromBytes(byte[])"/>. A GIF takes the options of
    /// <see cref="FromBytes(byte[], GifLoadOptions)"/>, not these.</para>
    /// <para>Everything else is as for <see cref="FromBytes(byte[])"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/> or <paramref name="options"/> is <see langword="null"/>. A call with
    /// both null reports <paramref name="data"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is empty, or the data is JPEG, BMP or GIF and
    /// <paramref name="options"/> differs from <see cref="ImageLoadOptions.Default"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A member of <paramref name="options"/> is not a defined enumeration value.
    /// </exception>
    /// <exception cref="InvalidDataException">As for <see cref="FromBytes(byte[])"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// As for <see cref="FromBytes(byte[])"/>, and whatever the loader throws for the options it is
    /// given.
    /// </exception>
    public static LayoutImage FromBytes(byte[] data, ImageLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        return new LayoutImage(ImageSource.Load(RefuseEmpty(data, nameof(data)), options, null));
    }

    /// <summary>
    /// Loads an image from its bytes as <see cref="FromBytes(byte[])"/> does, with the choice a GIF
    /// offers in <see cref="GifLoadOptions"/>.
    /// </summary>
    /// <param name="data">The whole file; see <see cref="FromBytes(byte[])"/>.</param>
    /// <param name="options">The options passed to the GIF loader when the data is a GIF.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// <b>Options for a format that is not GIF are refused unless they equal
    /// <see cref="GifLoadOptions.Default"/>.</b> Only <see cref="GifImageLoader"/> takes
    /// <see cref="GifLoadOptions"/>, so for any other format the call throws
    /// <see cref="ArgumentException"/> with <c>ParamName</c> <c>options</c>. For a GIF the options
    /// go to the loader as given; see <see cref="GifImageLoader.Load(byte[], GifLoadOptions)"/>.
    /// <b>Do not pass</b> options that differ from the default unless the data is a GIF. Everything
    /// else is as for <see cref="FromBytes(byte[])"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="data"/> or <paramref name="options"/> is <see langword="null"/>. A call with
    /// both null reports <paramref name="data"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="data"/> is empty, or the data is not a GIF and <paramref name="options"/>
    /// differs from <see cref="GifLoadOptions.Default"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">As for <see cref="FromBytes(byte[])"/>.</exception>
    /// <exception cref="NotSupportedException">As for <see cref="FromBytes(byte[])"/>.</exception>
    public static LayoutImage FromBytes(byte[] data, GifLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(options);
        return new LayoutImage(ImageSource.Load(RefuseEmpty(data, nameof(data)), null, options));
    }

    /// <summary>
    /// Reads a file once and loads the image in it as <see cref="FromBytes(byte[])"/> does.
    /// </summary>
    /// <param name="path">The path of an image file.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// <b>A file of more than 268,435,456 bytes (256 MiB) is refused</b> with
    /// <see cref="InvalidDataException"/> before it is read. Read it yourself and pass the bytes to
    /// <see cref="FromBytes(byte[])"/>, which has no bound of its own. An empty file is refused with
    /// <see cref="ArgumentException"/>. The file is opened for reading, allowing other readers, and
    /// closed before the call returns. An exception that <see cref="FileStream"/> throws for the
    /// path propagates unchanged, so whether another process's hold on the file blocks the read,
    /// and the <see cref="IOException"/> that reports it, are those of the platform. The format is
    /// read from the bytes, not from the extension. Everything else is as for
    /// <see cref="FromBytes(byte[])"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty or contains a null character, or the file holds no bytes.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory of the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller may not read the file, or the path names a directory.
    /// </exception>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    /// <exception cref="InvalidDataException">
    /// The file is larger than the bound, begins with no known signature, or the loader refuses it.
    /// </exception>
    /// <exception cref="NotSupportedException">As for <see cref="FromBytes(byte[])"/>.</exception>
    public static LayoutImage FromFile(string path) =>
        new(ImageSource.Load(ImageSource.ReadFile(path, ImageSource.MaxBytes), null, null));

    /// <summary>
    /// Reads a file once and loads the image in it as
    /// <see cref="FromBytes(byte[], ImageLoadOptions)"/> does.
    /// </summary>
    /// <param name="path">The path of an image file.</param>
    /// <param name="options">The options passed to the loader that reads the file.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// The refusals for <paramref name="options"/> are those of
    /// <see cref="FromBytes(byte[], ImageLoadOptions)"/>, and the file's are those of
    /// <see cref="FromFile(string)"/>. The file is read before the options are checked against its
    /// format. A call with a null <paramref name="path"/> and null <paramref name="options"/>
    /// reports the path.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// As for <see cref="FromFile(string)"/>, or the file is JPEG, BMP or GIF and
    /// <paramref name="options"/> differs from <see cref="ImageLoadOptions.Default"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A member of <paramref name="options"/> is not a defined enumeration value.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory of the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller may not read the file, or the path names a directory.
    /// </exception>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="FromFile(string)"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// As for <see cref="FromBytes(byte[], ImageLoadOptions)"/>.
    /// </exception>
    public static LayoutImage FromFile(string path, ImageLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        return new(ImageSource.Load(ImageSource.ReadFile(path, ImageSource.MaxBytes), options, null));
    }

    /// <summary>
    /// Reads a file once and loads the image in it as
    /// <see cref="FromBytes(byte[], GifLoadOptions)"/> does.
    /// </summary>
    /// <param name="path">The path of an image file.</param>
    /// <param name="options">The options passed to the GIF loader when the file is a GIF.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// The refusals for <paramref name="options"/> are those of
    /// <see cref="FromBytes(byte[], GifLoadOptions)"/>, and the file's are those of
    /// <see cref="FromFile(string)"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// As for <see cref="FromFile(string)"/>, or the file is not a GIF and
    /// <paramref name="options"/> differs from <see cref="GifLoadOptions.Default"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="DirectoryNotFoundException">A directory of the path does not exist.</exception>
    /// <exception cref="UnauthorizedAccessException">
    /// The caller may not read the file, or the path names a directory.
    /// </exception>
    /// <exception cref="IOException">The file cannot be opened or read.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="FromFile(string)"/>.</exception>
    /// <exception cref="NotSupportedException">As for <see cref="FromBytes(byte[])"/>.</exception>
    public static LayoutImage FromFile(string path, GifLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(options);
        return new(ImageSource.Load(ImageSource.ReadFile(path, ImageSource.MaxBytes), null, options));
    }

    /// <summary>
    /// Reads a stream from its current position to its end and loads the image in it as
    /// <see cref="FromBytes(byte[])"/> does.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the first byte of the image file.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// <b>A stream with more than 268,435,456 bytes (256 MiB) left is refused</b> with
    /// <see cref="InvalidDataException"/>. A seekable stream whose <c>Length</c> says so is refused
    /// before a byte is read. Every stream is also read in chunks and refused once one byte past
    /// the bound has arrived, so a stream whose length is wrong, or one that never ends, costs the
    /// call the memory of the chunks read, and one more copy of what it keeps. Read the bytes
    /// yourself and pass them to <see cref="FromBytes(byte[])"/> for a larger image. A stream with
    /// nothing left to read is refused with <see cref="ArgumentException"/>, and so is one whose
    /// <c>CanRead</c> is false, such as a disposed <see cref="MemoryStream"/>.
    /// <para>The stream need not be seekable. It is read from the position it is at, not from the
    /// start, so the bytes before the position take no part in the signature. After a successful
    /// call the position is the end of the stream. After a refusal it is: unchanged, when the
    /// stream had nothing left or its <c>Length</c> showed it over the bound; one byte past the
    /// bound from where the call began, when the bound was found by reading; at the end of the
    /// stream, for any refusal that follows the read, such as an unknown signature, the options or
    /// the loader. An exception the stream itself throws propagates unchanged and leaves the
    /// position wherever the stream left it.</para>
    /// <para><b>The stream is not disposed or closed.</b> It belongs to the caller before and after
    /// the call, and the image holds no reference to it. Everything else is as for
    /// <see cref="FromBytes(byte[])"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="stream"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// The stream cannot be read, which includes a stream that has been disposed, or has no bytes
    /// left.
    /// </exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    /// <exception cref="ObjectDisposedException">
    /// The stream throws it, for a stream that is disposed but still reports <c>CanRead</c>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The stream is larger than the bound, begins with no known signature, or the loader refuses it.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// As for <see cref="FromBytes(byte[])"/>, or the stream throws it, for instance from a
    /// <c>Length</c> it does not support.
    /// </exception>
    public static LayoutImage FromStream(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return new(ImageSource.Load(
            ImageSource.ReadAll(stream, ImageSource.MaxBytes, nameof(stream), "stream"), null, null));
    }

    /// <summary>
    /// Reads a stream as <see cref="FromStream(Stream)"/> does and loads the image with the options
    /// of <see cref="FromBytes(byte[], ImageLoadOptions)"/>.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the first byte of the image file.</param>
    /// <param name="options">The options passed to the loader that reads the stream.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// The refusals for <paramref name="options"/> are those of
    /// <see cref="FromBytes(byte[], ImageLoadOptions)"/>, and the stream's are those of
    /// <see cref="FromStream(Stream)"/>. The stream is read in full before the options are checked
    /// against its format, so it is consumed even when the options are refused.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// As for <see cref="FromStream(Stream)"/>, or the stream holds JPEG, BMP or GIF and
    /// <paramref name="options"/> differs from <see cref="ImageLoadOptions.Default"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A member of <paramref name="options"/> is not a defined enumeration value.
    /// </exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    /// <exception cref="ObjectDisposedException">As for <see cref="FromStream(Stream)"/>.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="FromStream(Stream)"/>.</exception>
    /// <exception cref="NotSupportedException">
    /// As for <see cref="FromBytes(byte[], ImageLoadOptions)"/>.
    /// </exception>
    public static LayoutImage FromStream(Stream stream, ImageLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        return new(ImageSource.Load(
            ImageSource.ReadAll(stream, ImageSource.MaxBytes, nameof(stream), "stream"), options, null));
    }

    /// <summary>
    /// Reads a stream as <see cref="FromStream(Stream)"/> does and loads the image with the options
    /// of <see cref="FromBytes(byte[], GifLoadOptions)"/>.
    /// </summary>
    /// <param name="stream">A readable stream positioned at the first byte of the image file.</param>
    /// <param name="options">The options passed to the GIF loader when the stream holds a GIF.</param>
    /// <returns>An element with the default size, margins and alignment.</returns>
    /// <remarks>
    /// The refusals for <paramref name="options"/> are those of
    /// <see cref="FromBytes(byte[], GifLoadOptions)"/>, and the stream's are those of
    /// <see cref="FromStream(Stream)"/>. The stream is read in full before the options are checked
    /// against its format.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="stream"/> or <paramref name="options"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// As for <see cref="FromStream(Stream)"/>, or the stream does not hold a GIF and
    /// <paramref name="options"/> differs from <see cref="GifLoadOptions.Default"/>.
    /// </exception>
    /// <exception cref="IOException">Reading the stream fails.</exception>
    /// <exception cref="ObjectDisposedException">As for <see cref="FromStream(Stream)"/>.</exception>
    /// <exception cref="InvalidDataException">As for <see cref="FromStream(Stream)"/>.</exception>
    /// <exception cref="NotSupportedException">As for <see cref="FromBytes(byte[])"/>.</exception>
    public static LayoutImage FromStream(Stream stream, GifLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(options);
        return new(ImageSource.Load(
            ImageSource.ReadAll(stream, ImageSource.MaxBytes, nameof(stream), "stream"), null, options));
    }

    private static byte[] RefuseEmpty(byte[] data, string paramName) =>
        data.Length == 0 ? throw new ArgumentException("The image data is empty.", paramName) : data;

    private double _spaceAfter;

    /// <summary>
    /// The gap, in points, between this image and the element the document places after it. The
    /// default, 0, leaves none.
    /// </summary>
    /// <remarks>
    /// <b>A value that is not finite or is below zero is refused.</b> This <c>init</c> accessor
    /// throws <see cref="ArgumentOutOfRangeException"/>, so the exception is raised by the
    /// statement that set the value. The rule, the keep requests, where the gap is read and its
    /// removal in 3.0 are those of <see cref="Paragraph.SpaceAfter"/>. The gap follows the
    /// slot, which already holds the vertical <see cref="Margins"/>, so the two add.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is <c>NaN</c>, infinite or below zero.
    /// </exception>
    public double SpaceAfter
    {
        get => _spaceAfter;
        init => _spaceAfter = LayoutValidation.SpaceAfter(value);
    }
}
