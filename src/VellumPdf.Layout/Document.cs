// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout;

/// <summary>
/// High-level document builder. The primary entry point for the layout engine.
///
/// <code>
/// using var doc = new Document();
/// doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
/// doc.Add("Hello, world!");
/// doc.Save("output.pdf");
/// </code>
/// </summary>
/// <remarks>
/// Most refusals fire from the save, not from the member you set, and each member says which call
/// throws. A save before any element is added throws; see the constructor.
/// </remarks>
public sealed class Document : IDisposable
{
    /// <summary>Creates an empty document with A4 pages and 72pt margins.</summary>
    /// <remarks>
    /// A save before an element is added throws <see cref="InvalidOperationException"/>, unless a
    /// check the save runs earlier fails first; see <see cref="Save(System.IO.Stream)"/>.
    /// </remarks>
    public Document() { }

    private readonly PdfDocument _pdf = new();
    private readonly List<IRenderer> _content = [];
    private TextStyle _defaultStyle = TextStyle.Default;
    private readonly List<TextEncodingWarning> _textEncodingWarnings = [];

    private readonly List<BandTruncationWarning> _bandTruncations = [];

    /// <summary>
    /// Characters passed to a page canvas's <see cref="PdfCanvas.ShowText"/> that WinAnsiEncoding
    /// could not represent (each UTF-16 code unit was substituted with '?' in the saved PDF, so a
    /// character outside the Basic Multilingual Plane gives two question marks).
    /// </summary>
    /// <remarks>
    /// Empty until a save (or signing prep) has run, and replaced by each one that succeeds.
    /// <para>The built-in elements and running bands pass <see cref="PdfCanvas.ShowText"/> only
    /// their Standard-14 text. Characters a custom <see cref="IRenderer"/> passes to
    /// <see cref="PdfCanvas.ShowText"/> are recorded whatever font is selected.</para>
    /// </remarks>
    public IReadOnlyList<TextEncodingWarning> TextEncodingWarnings => _textEncodingWarnings;

    /// <summary>
    /// Running bands whose text was cut to fit the content box, from the last save that succeeded.
    /// At most one report per band, each naming the page that lost the most. Empty when no band was
    /// cut, or when no band was set.
    /// </summary>
    /// <remarks>
    /// Empty until a save (or signing prep) has run, and replaced by each one that succeeds. A cut
    /// band is not an error, and for a save or signing prep this list is the only report of it.
    /// <see cref="VellumPdf.Layout.Rendering.DocumentRenderer.BandTruncations"/> describes when a
    /// band is cut.
    /// </remarks>
    public IReadOnlyList<BandTruncationWarning> BandTruncations => _bandTruncations;

    /// <summary>Document metadata (title, author, subject, keywords, etc.).</summary>
    /// <remarks>
    /// Layout validates no field of this dictionary. Each entry is written to <c>/Info</c> as
    /// given. The XMP metadata can differ from <c>/Info</c>. The XMP copy drops the C0 control
    /// characters other than tab, line feed and carriage return, replaces an unpaired surrogate
    /// with U+FFFD, can leave out an empty entry, and carries <c>VellumPdf</c> as the producer when
    /// <c>Producer</c> is null.
    /// </remarks>
    public PdfDocumentInfo Info => _pdf.Info;

    /// <summary>The default page size used for newly created pages.</summary>
    /// <remarks>
    /// Validated at save, not here. A null value is accepted here, and the save throws
    /// <see cref="NullReferenceException"/>. A width or height that is not a positive finite number
    /// is refused with <see cref="ArgumentOutOfRangeException"/> naming the axis and the value, and
    /// a page whose <see cref="Margins"/> meet or exceed its width or height is refused with
    /// <see cref="ArgumentException"/>.
    /// <para>The rectangle's lower-left corner is written to each page's <c>/MediaBox</c> but not
    /// used for layout, which places content from (0, 0) and reads only the width and height.
    /// Relative to the page, the content therefore moves by minus that corner, and off the page
    /// once the corner's offset exceeds the margins.</para>
    /// <para>Do not pass null, a rectangle not at the origin, or a size so large that positions
    /// overflow; a page 1e308 wide makes an auto-width table write <c>NaN</c>. A later major
    /// version will refuse all three.</para>
    /// <para>Setting this after adding elements applies to what you have already
    /// added. The layout runs at save, so the size in force then is the size the whole document
    /// is laid out at, not only the part added after you set it.</para>
    /// <para>On a document no save has been attempted on, that holds byte for byte. Resizing before
    /// <see cref="Save(System.IO.Stream)"/> produces a file matching one built at the new size from
    /// the start, except the random <c>/ID</c> and the XMP <c>CreateDate</c>/<c>ModifyDate</c>
    /// timestamps, which carry the time each build ran. Measured by resizing a document from 600 by
    /// 800 to 200 by 120 at <b>10pt</b> margins and normalising those three fields: the bytes match
    /// a build at 200 by 120 throughout, and every <c>/MediaBox</c> carries the new size. The
    /// default 72pt insets on <see cref="Margins"/> do not fit a 120pt page, so that combination is
    /// refused before either file is built. A save that already threw breaks that equivalence,
    /// along with the rest of the document's state; see
    /// <see cref="Save(System.IO.Stream)"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from a save rather than from this property, when the width or height is zero,
    /// negative or not finite.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from a save rather than from this property, when the value is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when the margins on either axis meet or exceed this page size, or when a
    /// header or footer leaves the content area no positive size; see <see cref="Margins"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when the content area this size and <see cref="Margins"/> compute to is
    /// positive but still too small for a single element to fit on one page. A document that merely
    /// runs to several pages does not reach it. Two other routes reach the same type. The
    /// page-continuation cap fires when a single element needs more than 50,000 page continuations,
    /// and how many an element needs is itself a function of this size. The content area can also
    /// leave an image a width or height at which the image cannot be drawn; see
    /// <see cref="LayoutImage.Width"/>.
    /// </exception>
    public PdfRectangle PageSize
    {
        get => _pdf.DefaultPageSize;
        set => _pdf.DefaultPageSize = value;
    }

    /// <summary>
    /// Requested PDF/A conformance level. Forwarded to the underlying <see cref="PdfDocument"/>.
    /// PDF/A-2a and PDF/UA-1 imply <see cref="Tagged"/> = true.
    /// </summary>
    /// <remarks>
    /// Setting a level does not run the rules. A document can declare PDF/A and still fail a
    /// preflight; the stamp is written either way (#474).
    /// <para>A value the enumeration does not name is accepted and stamped as PDF/A-2b.</para>
    /// <para>Do not pass a value the enumeration does not name. A later major version will refuse
    /// it.</para>
    /// <para>Combined with <see cref="Encrypt"/>, the save throws
    /// <see cref="InvalidOperationException"/> for every PDF/A level, because ISO 19005-2 §6.1.3
    /// forbids the <c>Encrypt</c> key in a PDF/A file. For PDF/UA-1 it throws only when the
    /// encryption settings leave out <see cref="PdfPermissions.Extract"/>, because ISO 14289-1,
    /// 7.16, requires the permission that allows extraction for accessibility.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when
    /// <see cref="Encrypt"/> has been called and this is a PDF/A level or a value the enumeration
    /// does not name, or PDF/UA-1 without <see cref="PdfPermissions.Extract"/>.
    /// </exception>
    public PdfConformance Conformance
    {
        get => _pdf.Conformance;
        set => _pdf.Conformance = value;
    }

    /// <summary>
    /// When true, the built-in elements wrap their content in marked-content sequences (a custom
    /// renderer should wrap its own content; see <see cref="DrawContext.Tagged"/>) and a
    /// /StructTreeRoot is written. Default is false. Forwarded to the underlying
    /// <see cref="PdfDocument"/>.
    /// </summary>
    /// <remarks>
    /// Reads true while <see cref="Conformance"/> is PDF/A-2a or PDF/UA-1, whatever you set. The
    /// value you set is kept and used again once the conformance level changes to another one.
    /// </remarks>
    public bool Tagged
    {
        get => _pdf.Tagged;
        set => _pdf.Tagged = value;
    }

    /// <summary>
    /// Optional document language tag (BCP 47 / RFC 5646, e.g. <c>"en-US"</c>, <c>"fr"</c>).
    /// Forwarded to the underlying <see cref="PdfDocument"/>.
    /// When set, writes <c>/Lang</c> in the catalog and <c>dc:language</c> in XMP.
    /// Required by PDF/A-2a and PDF/UA-1 (set it explicitly — no default is applied).
    /// Leading and trailing whitespace is trimmed when the value is written.
    /// </summary>
    /// <remarks>
    /// <b>Attention</b>: the string is not validated as BCP 47. After trim, an empty value is
    /// omitted from <c>/Lang</c> and XMP. A non-empty ill-formed tag such as <c>not a tag</c> is
    /// written as given. PDF/A-2a and PDF/UA-1 both require a well-formed tag.
    /// <para>Do not pass a tag that is not well-formed BCP 47. A later major version will refuse
    /// one.</para>
    /// </remarks>
    public string? Language
    {
        get => _pdf.Language;
        set => _pdf.Language = value;
    }

    /// <summary>
    /// When true, <see cref="Save(Stream)"/> uses PDF 1.5+ object streams and a
    /// cross-reference stream for smaller output. Forwarded to the underlying
    /// <see cref="PdfDocument"/>. Cannot be combined with <see cref="Encrypt"/>.
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="Encrypt"/>, the save throws <see cref="NotSupportedException"/>.
    /// Neither this property nor <see cref="Encrypt"/> refuses the pairing when it is set. The
    /// pairing is refused after the layout has run, so a retry after this refusal adds the pages
    /// again; see <see cref="Save(System.IO.Stream)"/>.
    /// <para>A document signed through VellumPdf.Signing is written without object streams,
    /// whatever this property holds.</para>
    /// </remarks>
    /// <exception cref="NotSupportedException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when this is
    /// true and <see cref="Encrypt"/> has been called.
    /// </exception>
    public bool UseObjectStreams
    {
        get => _pdf.UseObjectStreams;
        set => _pdf.UseObjectStreams = value;
    }

    /// <summary>Page margins applied to the content area. Defaults to 72 points (1 inch) on all sides.</summary>
    /// <remarks>
    /// Margins that meet or exceed the page are refused. Saving throws
    /// <see cref="ArgumentException"/> and names the axis and both figures. The content box would
    /// otherwise have no positive size, and no element could be placed in it.
    /// <para>The header and footer count toward this. Their heights come off the same box, so
    /// margins that fit on their own can still leave nothing once you set a running band.</para>
    /// <para><b>Attention</b>: a finite negative inset is not refused. That edge of the content
    /// area then lies at the inset plus the height of any header or footer there, measured inward
    /// from the page's edge, so a negative total places it beyond the page, where a reader clips
    /// the content. A header or footer on that edge starts at the inset, so part or all of it lies
    /// beyond the page. On a one-paragraph document with every inset at -72, the file is written
    /// with no invalid token in it, so nothing downstream reports the loss either. Negative insets
    /// large enough in magnitude to overflow the position arithmetic, such as -1e308 on the left or
    /// right edge alone, are finite and so pass the check below. They were not measured again after
    /// #502, and what they do depends on the elements laid out.</para>
    /// <para>An inset that is not a finite number is refused, whichever edge holds it and whichever
    /// of <c>NaN</c>, positive infinity and negative infinity it is. The save throws
    /// <see cref="ArgumentException"/> with the parameter name <c>margins</c> before anything is laid
    /// out, naming the edge and the value, such as <c>The top margin is NaN, which cannot be laid
    /// out. A margin must be a finite number.</c> Before #502 only a positive-infinity sum was
    /// caught, and <c>NaN</c> and negative infinity surfaced later as a message about an element,
    /// or saved with <c>NaN</c> or <c>Infinity</c> in the content stream.</para>
    /// <para><b>Attention</b>: do not pass a finite negative inset. A later major version will
    /// reject it.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, when an inset is <c>NaN</c> or infinite,
    /// when the margins on either axis meet or exceed the page, or when they leave the content area
    /// no positive size once the header and footer are taken off. The message for a non-finite
    /// inset names the edge. The exception is also raised, with a message saying PDF does not
    /// support NaN or Infinity as a real number, when finite insets whose arithmetic overflows put
    /// a position the save writes outside the content stream, such as a link rectangle, at a
    /// non-finite value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save when an inset that passes the <see cref="ArgumentException"/> check
    /// leaves an element no area it can be laid out in, or leaves an image an extent it cannot
    /// draw, and also when a large finite inset leaves the content area positive but too small for
    /// a single element.
    /// </exception>
    public EdgeInsets Margins { get; set; } = new EdgeInsets(72); // 1 inch

    /// <summary>
    /// Optional header band drawn at the top of every page.
    /// Set via <see cref="SetHeader"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    /// <remarks>
    /// A band's <see cref="RunningBand.Template"/> is never null, because the
    /// <see cref="RunningBand"/> constructor refuses one (#531). The band's height and style carry
    /// refusals of their own; see <see cref="RunningBand.Height"/>,
    /// <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.FontRef"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size, or when <c>Height</c> is null and
    /// its style's size or leading does; see <see cref="RunningBand.Height"/> and
    /// <see cref="RunningBand.Style"/>. It is also raised when the band measures text that holds an
    /// unpaired surrogate in an embedded font, which can include part of the template it then does
    /// not draw; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height, or its style's size or leading, is refused; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not
    /// name and the band draws text; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public RunningBand? Header { get; set; }

    /// <summary>
    /// Optional footer band drawn at the bottom of every page.
    /// Set via <see cref="SetFooter"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    /// <remarks>
    /// A band's <see cref="RunningBand.Template"/> is never null, because the
    /// <see cref="RunningBand"/> constructor refuses one (#531). The band's height and style carry
    /// refusals of their own; see <see cref="RunningBand.Height"/>,
    /// <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.FontRef"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size, or when <c>Height</c> is null and
    /// its style's size or leading does; see <see cref="RunningBand.Height"/> and
    /// <see cref="RunningBand.Style"/>. It is also raised when the band measures text that holds an
    /// unpaired surrogate in an embedded font, which can include part of the template it then does
    /// not draw; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height, or its style's size or leading, is refused; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not
    /// name and the band draws text; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public RunningBand? Footer { get; set; }

    /// <summary>
    /// Sets the text style that later <see cref="Add(string, TextStyle)"/> calls use when they are
    /// given no style. Returns this document for chaining.
    /// </summary>
    /// <remarks>
    /// <b>Attention</b>: nothing else reads it. <see cref="Add(string, TextStyle)"/> takes the
    /// style in force when it is called, so text added before this call keeps the earlier
    /// style, and a <see cref="Paragraph"/>, <see cref="Heading"/>, list, table or running band
    /// never uses it. Give those their own style.
    /// <para>A null style is stored. A later <see cref="Add(string, TextStyle)"/> then uses
    /// <see cref="TextStyle.Default"/>.</para>
    /// <para>The style is not checked here. Its refusals are raised from the save, for the text
    /// that uses it; see <see cref="TextStyle.FontSize"/>, <see cref="TextStyle.Leading"/> and
    /// <see cref="TextStyle.FontRef"/>.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the style a later <see cref="Add(string, TextStyle)"/> uses has a size or leading
    /// that is refused; see <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the style a later <see cref="Add(string, TextStyle)"/> uses holds a
    /// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name and the font is
    /// selected on a page; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when text a later <see cref="Add(string, TextStyle)"/> adds in this style holds an
    /// unpaired surrogate and is measured in an embedded font; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document SetDefaultFont(TextStyle style) { _defaultStyle = style; return this; }

    /// <summary>Sets a header band with optional style and alignment. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="template"/> is refused here (#531). The parameter is non-nullable, but
    /// that is a compiler diagnostic rather than a check, and <c>null!</c> silences it. This method
    /// builds a <see cref="RunningBand"/>, whose constructor throws, so the failure now lands on
    /// this call. It used to surface as a <see cref="NullReferenceException"/> from the save, a
    /// call you did not make. Pass an empty string for a band that draws no text. A null band
    /// assigned to <see cref="Header"/> directly is a different case and means no band.
    /// <para>The band's height and <paramref name="style"/> carry refusals of their own; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/>,
    /// <see cref="TextStyle.Leading"/> and <see cref="TextStyle.FontRef"/>.
    /// <see cref="HorizontalAlignment.Justify"/> and values the enumeration does not name are drawn
    /// left-aligned; see <see cref="RunningBand.Alignment"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size, or when <c>Height</c> is null and
    /// its style's size or leading does; see <see cref="RunningBand.Height"/> and
    /// <see cref="RunningBand.Style"/>. It is also raised when the band measures text that holds an
    /// unpaired surrogate in an embedded font, which can include part of the template it then does
    /// not draw; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height, or its style's size or leading, is refused; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not
    /// name and the band draws text; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document SetHeader(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Header = new RunningBand(template, style, alignment);
        return this;
    }

    /// <summary>Sets a footer band with optional style and alignment. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="template"/> is refused here (#531). The parameter is non-nullable, but
    /// that is a compiler diagnostic rather than a check, and <c>null!</c> silences it. This method
    /// builds a <see cref="RunningBand"/>, whose constructor throws, so the failure now lands on
    /// this call. It used to surface as a <see cref="NullReferenceException"/> from the save, a
    /// call you did not make. Pass an empty string for a band that draws no text. A null band
    /// assigned to <see cref="Footer"/> directly is a different case and means no band.
    /// <para>The band's height and <paramref name="style"/> carry refusals of their own; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/>,
    /// <see cref="TextStyle.Leading"/> and <see cref="TextStyle.FontRef"/>.
    /// <see cref="HorizontalAlignment.Justify"/> and values the enumeration does not name are drawn
    /// left-aligned; see <see cref="RunningBand.Alignment"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size, or when <c>Height</c> is null and
    /// its style's size or leading does; see <see cref="RunningBand.Height"/> and
    /// <see cref="RunningBand.Style"/>. It is also raised when the band measures text that holds an
    /// unpaired surrogate in an embedded font, which can include part of the template it then does
    /// not draw; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height, or its style's size or leading, is refused; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's style holds a <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not
    /// name and the band draws text; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document SetFooter(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Footer = new RunningBand(template, style, alignment);
        return this;
    }

    // ── Embedded font registration ───────────────────────────────────────────

    /// <summary>
    /// Registers a TrueType font for embedding and returns a handle that can be
    /// used in <see cref="TextStyle.FontRef"/>.
    /// </summary>
    /// <remarks>
    /// This call reads the table directory and the <c>cmap</c>, <c>head</c>, <c>hhea</c>,
    /// <c>maxp</c>, <c>name</c>, <c>OS/2</c>, <c>post</c> and <c>hmtx</c> tables. It throws
    /// <see cref="InvalidDataException"/> for bytes it cannot read as a font,
    /// <see cref="InvalidOperationException"/> when a table from that list is missing, and
    /// <see cref="NotSupportedException"/> when the <c>cmap</c> has no subtable in format 0, 4 or
    /// 6.
    /// <para>It does not check every value in those tables, and it does not read the outlines. Both
    /// are used at save, for every font registered here whether or not text uses it, so a font this
    /// call accepts can still make the save throw: <see cref="InvalidOperationException"/> for a
    /// missing outline table, <see cref="InvalidDataException"/> for malformed outline data, and
    /// <see cref="ArgumentException"/> for a value that makes a font metric non-finite, such as a
    /// <c>unitsPerEm</c> of 0. An OpenType font with CFF outlines is accepted.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fontData"/> is <see langword="null"/>. <c>ParamName</c> is <c>source</c>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="fontData"/> is malformed or truncated. Malformed outline data is reported
    /// from the save instead.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The font's <c>cmap</c> table has no subtable in format 0, 4 or 6.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A table the parser needs is missing: from this call for the tables listed in the remarks,
    /// and from <see cref="Save(System.IO.Stream)"/> and the other save overloads for the outline
    /// tables.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a value in the font makes a font metric non-finite, such as a
    /// <c>unitsPerEm</c> of 0. <c>ParamName</c> is <c>value</c>.
    /// </exception>
    public EmbeddedFontHandle UseTrueTypeFont(byte[] fontData) =>
        _pdf.UseTrueTypeFont(fontData);

    /// <summary>
    /// Loads a TrueType font file from disk, registers it for embedding, and returns a handle.
    /// </summary>
    /// <remarks>
    /// The file is read here, not at save, and then parsed as <see cref="UseTrueTypeFont"/>
    /// parses it, with the same exceptions.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds a null character, or, on Windows, holds only spaces.
    /// Other characters the file system refuses raise <see cref="IOException"/>.
    /// <see cref="Save(System.IO.Stream)"/> and the other save overloads also raise
    /// <see cref="ArgumentException"/>, not this call, for a value in the font that makes a font
    /// metric non-finite; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="path"/> names a file that does not exist.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory named in <paramref name="path"/> does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="path"/> names a directory, or a file you may not read.
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be read for another reason the file system reports.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The file's bytes are malformed or truncated; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A table the parser needs is missing; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The font's <c>cmap</c> table has no subtable in format 0, 4 or 6; see
    /// <see cref="UseTrueTypeFont"/>.
    /// </exception>
    public EmbeddedFontHandle LoadTrueTypeFont(string path) =>
        UseTrueTypeFont(File.ReadAllBytes(path));

    /// <summary>
    /// Asynchronously loads a TrueType font file from disk, registers it for embedding, and
    /// returns a handle.
    /// </summary>
    /// <remarks>
    /// Same refusals as <see cref="LoadTrueTypeFont"/>. This method throws none of them itself.
    /// Each one, a null or empty path included, faults the returned task and surfaces when you
    /// await it. The ones <see cref="UseTrueTypeFont"/> leaves to the save are raised by
    /// the save, not the task. A <paramref name="cancellationToken"/> already cancelled when you
    /// call this cancels the task instead of faulting it, and awaiting it throws
    /// <see cref="OperationCanceledException"/>. It does so whatever <paramref name="path"/>
    /// holds, so it pre-empts the refusals above. The token is passed only to the file read. A
    /// cancellation after the read has no effect, and a font the parse accepts stays registered.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds a null character, or, on Windows, holds only spaces.
    /// Other characters the file system refuses raise <see cref="IOException"/>.
    /// <see cref="Save(System.IO.Stream)"/> and the other save overloads also raise
    /// <see cref="ArgumentException"/>, not the returned task, for a value in the font that makes a
    /// font metric non-finite; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="FileNotFoundException">
    /// <paramref name="path"/> names a file that does not exist.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory named in <paramref name="path"/> does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="path"/> names a directory, or a file you may not read.
    /// </exception>
    /// <exception cref="IOException">
    /// The file cannot be read for another reason the file system reports.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// The file's bytes are malformed or truncated; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// A table the parser needs is missing; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The font's <c>cmap</c> table has no subtable in format 0, 4 or 6; see
    /// <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was already cancelled when this method was called.
    /// </exception>
    public async Task<EmbeddedFontHandle> LoadTrueTypeFontAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return UseTrueTypeFont(bytes);
    }

    // ── Content methods ──────────────────────────────────────────────────────

    /// <summary>Adds a paragraph to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null paragraph throws <see cref="ArgumentNullException"/> from this call. What the
    /// paragraph holds is not checked here; the save refuses it, and the members that hold each
    /// value say what it does.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="paragraph"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the paragraph's text holds an unpaired surrogate and is measured in an embedded
    /// font, or when linked text lands at a non-finite position; see
    /// <see cref="TextStyle.FontRef"/> and <see cref="TextStyle.LinkUri"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the paragraph uses has a size or leading that is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the paragraph uses holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document Add(Paragraph paragraph)
    {
        ArgumentNullException.ThrowIfNull(paragraph);
        _content.Add(new ParagraphRenderer(paragraph) { ElementLanguage = paragraph.Language });
        return this;
    }

    /// <summary>Adds a horizontal line separator to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null separator is refused here (#547); it used to be stored, and the save threw
    /// <see cref="NullReferenceException"/> when it laid the element out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="separator"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the separator's width or an inset is refused, or when it is too tall for a page;
    /// see <see cref="LineSeparator.LineWidth"/> and <see cref="LineSeparator.Margins"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the separator leaves a later element at a non-finite position that the save
    /// writes outside the content stream; see <see cref="LineSeparator.Margins"/>.
    /// </exception>
    public Document Add(LineSeparator separator)
    {
        ArgumentNullException.ThrowIfNull(separator);
        _content.Add(new LineSeparatorRenderer(separator));
        return this;
    }

    /// <summary>Adds a table to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null table is refused here (#547); it used to be stored, and the save threw
    /// <see cref="NullReferenceException"/> when it laid the element out. A table's own
    /// refusals are on <see cref="TableElement"/>, among them a table that needs more than 50,000
    /// page continuations.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="table"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a cell's text holds an unpaired surrogate and is measured in an embedded font;
    /// see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the table uses has a size or leading that is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the table uses holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
    /// enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document Add(TableElement table)
    {
        ArgumentNullException.ThrowIfNull(table);
        _content.Add(new TableRenderer(table));
        return this;
    }

    /// <summary>Adds an image to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null image is refused here (#547); it used to be stored, and the save threw
    /// <see cref="NullReferenceException"/> when it laid the element out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="image"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a drawn width or height is refused, or when the image is too tall for a page; see
    /// <see cref="LayoutImage.Width"/> and <see cref="LayoutImage.Height"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the image's pixel dimensions are refused, or when it leaves a later element at a
    /// non-finite position that the save writes outside the content stream; see
    /// <see cref="LayoutImage.Margins"/>.
    /// </exception>
    public Document Add(LayoutImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        _content.Add(new LayoutImageRenderer(image));
        return this;
    }

    /// <summary>Adds a pie chart to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null chart is refused here (#547); it used to be stored, and the save threw
    /// <see cref="NullReferenceException"/> when it laid the element out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="chart"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the chart's slices or its stroke width are refused; see
    /// <see cref="PieChart.Slices"/> and <see cref="PieChart.StrokeWidth"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the chart is too tall for a page; see <see cref="PieChart.Diameter"/>.
    /// </exception>
    public Document Add(PieChart chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        _content.Add(new PieChartRenderer(chart));
        return this;
    }

    /// <summary>Adds a bulleted or numbered list to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null list is refused here (#547); it used to be stored, and the save threw
    /// <see cref="NullReferenceException"/> when it laid the element out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="list"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when an item's text holds an unpaired surrogate and is measured in an embedded font,
    /// or when linked item text lands at a non-finite position; see
    /// <see cref="TextStyle.FontRef"/> and <see cref="ListElement.Indent"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the list uses has a size or leading that is refused, or when a nested
    /// list's indent reaches its own area width; see <see cref="TextStyle.FontSize"/> and
    /// <see cref="ListElement.Indent"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the list uses holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
    /// enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document Add(ListElement list)
    {
        ArgumentNullException.ThrowIfNull(list);
        _content.Add(new ListRenderer(list));
        return this;
    }

    /// <summary>Adds a heading to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null heading throws <see cref="ArgumentNullException"/> from this call. What the heading
    /// holds is not checked here; the save refuses it, and the members that hold each value say
    /// what it does.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="heading"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the heading's text holds an unpaired surrogate and is measured in an embedded
    /// font, or when the heading's bookmark or linked text lands at a non-finite position; see
    /// <see cref="TextStyle.FontRef"/> and <see cref="Heading.Margins"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the heading uses has a size or leading that is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when a style the heading uses holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document Add(Heading heading)
    {
        ArgumentNullException.ThrowIfNull(heading);
        _content.Add(new HeadingRenderer(heading));
        return this;
    }

    /// <summary>Adds a paragraph built from the given text, using the supplied style or the default style. Returns this document for chaining.</summary>
    /// <remarks>
    /// With <paramref name="style"/> null, the paragraph takes the style from the most recent
    /// <see cref="SetDefaultFont"/> call made before this one.
    /// A null <paramref name="text"/> is refused here (#547); it used to be stored, and the save
    /// threw <see cref="NullReferenceException"/> when it laid the paragraph out.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the text holds an unpaired surrogate and is measured in an embedded font; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the style used has a size or leading that is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when the style used holds a <see cref="VellumPdf.Fonts.Standard14"/> value the
    /// enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public Document Add(string text, TextStyle? style = null)
        => Add(new Paragraph(text, style ?? _defaultStyle));

    /// <summary>
    /// Adds a custom renderer to the document content. Returns this document for chaining.
    /// Accepts any <see cref="IRenderer"/> implementation, including ones defined outside this
    /// library — the seam satellite packages (e.g. VellumPdf.Barcodes) use to plug their own
    /// elements into the flow layout.
    /// </summary>
    /// <remarks>
    /// One element may take at most <b>50,000</b> page continuations, and a renderer whose overflow
    /// never gets smaller reaches that limit; <see cref="IRenderer.Layout"/> has the rule. The save
    /// then throws <see cref="InvalidOperationException"/> naming the limit. Split a long element
    /// yourself.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="renderer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when this renderer, or an overflow it returns, needs more than 50,000 page
    /// continuations, or when it returns <see cref="LayoutResult.Outcome.Nothing"/> twice in a row,
    /// the second time on a new page.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from the save, not from this call, when <paramref name="renderer"/> returns a null
    /// result from <see cref="IRenderer.Layout"/>, or a <see cref="LayoutResult.Partial"/> with a
    /// null renderer.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="renderer"/> returns a result whose non-finite bottom leaves a
    /// later element at a position the save writes outside the content stream; see
    /// <see cref="LayoutResult.Full"/>.
    /// </exception>
    public Document Add(IRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        _content.Add(renderer);
        return this;
    }

    // ── OutputIntent configuration ───────────────────────────────────────────

    /// <summary>
    /// Configures a custom ICC profile as the PDF/A OutputIntent for this document.
    /// Forwarded to the underlying <see cref="PdfDocument.SetPdfAOutputIntent"/>.
    ///
    /// <para>
    /// Note: the layout engine uses DeviceRGB for text and table fills. A CMYK
    /// output intent is appropriate when the document contains custom CMYK or ICCBased
    /// content (e.g. via kernel-level drawing). Mixing DeviceRGB layout content with a
    /// pure CMYK output intent may be flagged by strict PDF/A validators.
    /// </para>
    /// </summary>
    /// <param name="iccProfile">The raw ICC profile bytes.</param>
    /// <param name="componentCount">
    /// Number of colour components: 1 (Gray), 3 (RGB), or 4 (CMYK).
    /// </param>
    /// <param name="outputConditionIdentifier">The OutputConditionIdentifier string.</param>
    /// <param name="info">
    /// Optional /Info string. Defaults to <paramref name="outputConditionIdentifier"/> when null.
    /// </param>
    /// <remarks>
    /// Refused from this call, not from save. An empty or null profile raises
    /// <see cref="ArgumentException"/>. A component count other than 1, 3 or 4 raises
    /// <see cref="ArgumentOutOfRangeException"/>. A null identifier raises
    /// <see cref="ArgumentNullException"/>.
    /// <para><b>Attention</b>: the output intent is written only when <see cref="Conformance"/>
    /// is not <see cref="PdfConformance.None"/>. Otherwise the call is accepted and the intent is
    /// left out of the file.</para>
    /// <para>The profile is not parsed, and <paramref name="componentCount"/> is not checked
    /// against it: any non-empty bytes are embedded as the profile.</para>
    /// <para>The identifier and <paramref name="info"/> are written as Latin-1: a character outside
    /// it becomes a best-fit Latin-1 character where there is one, such as <c>-</c> for an en dash,
    /// and otherwise <c>?</c>, one for each UTF-16 code unit.</para>
    /// <para>Do not pass bytes that are not an ICC profile, or a count that does not match it. A
    /// later major version will refuse both.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="iccProfile"/> is null or empty.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="componentCount"/> is not 1, 3 or 4.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="outputConditionIdentifier"/> is <see langword="null"/>.
    /// </exception>
    public void SetPdfAOutputIntent(byte[] iccProfile, int componentCount, string outputConditionIdentifier, string? info = null) =>
        _pdf.SetPdfAOutputIntent(iccProfile, componentCount, outputConditionIdentifier, info);

    /// <summary>
    /// Convenience method: sets the PDF/A OutputIntent to the built-in generic CMYK
    /// ICC profile (4 components). Forwarded to the underlying
    /// <see cref="PdfDocument.UseCmykOutputIntent"/>.
    ///
    /// <para>
    /// Note: the layout engine uses DeviceRGB for text and table fills. This method is
    /// intended for documents that combine layout content with custom CMYK kernel drawing.
    /// </para>
    /// </summary>
    /// <param name="outputConditionIdentifier">The OutputConditionIdentifier string written to the
    /// OutputIntent dictionary.</param>
    /// <remarks>
    /// Same identifier refusal as <see cref="SetPdfAOutputIntent"/>, and the same condition: the
    /// intent is written only when <see cref="Conformance"/> is not
    /// <see cref="PdfConformance.None"/>. The identifier is written as Latin-1, as on
    /// <see cref="SetPdfAOutputIntent"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="outputConditionIdentifier"/> is <see langword="null"/>.
    /// </exception>
    public void UseCmykOutputIntent(string outputConditionIdentifier = "Generic CMYK") =>
        _pdf.UseCmykOutputIntent(outputConditionIdentifier);

    // ── Encryption ──────────────────────────────────────────────────────────

    /// <summary>
    /// Configures AES-256 encryption for this document.
    /// Delegates to <see cref="PdfDocument.Encrypt"/>.
    /// Must be called before <see cref="Save(Stream)"/>.
    /// </summary>
    /// <remarks>
    /// Combined with <see cref="UseObjectStreams"/>, the save throws
    /// <see cref="NotSupportedException"/>. This method does not check that flag.
    /// Combined with a PDF/A <see cref="Conformance"/>, or with PDF/UA-1 when the settings leave
    /// out <see cref="PdfPermissions.Extract"/>, the save throws
    /// <see cref="InvalidOperationException"/>; see <see cref="Conformance"/>. All three are
    /// refused after the layout has run; see <see cref="Save(System.IO.Stream)"/>.
    /// <para>An encrypted document cannot be signed: VellumPdf.Signing throws
    /// <see cref="NotSupportedException"/> for one.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settings"/> has an empty <see cref="PdfEncryptionSettings.OwnerPassword"/>
    /// beside a non-empty <see cref="PdfEncryptionSettings.UserPassword"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Raised from a save, when <see cref="UseObjectStreams"/> is true.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when
    /// <see cref="Conformance"/> is a PDF/A level, or PDF/UA-1 and the settings leave out
    /// <see cref="PdfPermissions.Extract"/>.
    /// </exception>
    public Document Encrypt(PdfEncryptionSettings settings)
    {
        _pdf.Encrypt(settings);
        return this;
    }

    // ── Output ───────────────────────────────────────────────────────────────

    // Set when a save threw after its layout pass had already appended pages to the underlying
    // document, whether the layout itself threw or a step after it did: serialising, a cancellation,
    // preparing a signature. Those pages cannot be taken back, so the next save would lay everything
    // out a second time on top of them and write a file with several times the pages the caller
    // asked for, silently (#530). A save that failed before it appended anything, such as one
    // refused for its page geometry, leaves the document exactly as it was and stays usable.
    private bool _layoutFailed;

    private void ThrowIfLayoutFailed()
    {
        if (_layoutFailed)
        {
            throw new InvalidOperationException(
                "This document cannot be saved again: an earlier save stopped after laying it out, once " +
                "pages had already been added to it, and those pages cannot be taken back. " +
                "Create a new Document and add the content again.");
        }
    }

    /// <summary>
    /// Everything a save can refuse that does not depend on the layout, run before the layout starts.
    /// Layout appends pages, so a refusal that came after it left them behind and the next save
    /// appended a second layout (#530). Running these first keeps each refusal recoverable: correct
    /// the cause and save again.
    /// </summary>
    private void BeginSave(Stream destination)
    {
        _pdf.EnsureCanSave(destination);
        ThrowIfLayoutFailed();
        _pdf.ValidateSaveOptions();
    }

    // ── Saving to a path ─────────────────────────────────────────────────────

    // A save to a path opens the target before it lays anything out and writes it last (#508). It
    // used to open the target with FileMode.Create first, which truncates it, and then lay the
    // document out, so a layout failure left a zero-byte file in place of whatever the path held: a
    // caller regenerating a report over its previous output lost the previous report to one bad
    // value, with an exception about layout that gave no hint the file was gone.
    //
    // Opening early surfaces a lock or a permission error while the Document is still usable, since
    // nothing has been laid out. The open does not truncate (FileMode.OpenOrCreate), the document is
    // written into memory, and only then is the file truncated and filled, so the previous file
    // survives every failure up to the final copy and keeps its identity: permissions, owner, hard
    // links, a symbolic link at the path, a device file. A temporary file moved over the target kept
    // none of those. The price is that the final copy is not atomic against a crash.

    /// <summary>
    /// Makes the path absolute and nothing more. The file is opened exactly as given, so the operating
    /// system follows any symbolic link, a chain or a link into another directory included, and
    /// resolves a relative link target against the link's own directory, as the open always did
    /// (#508). Whether the path is a directory, read-only or otherwise unwritable is also for the
    /// operating system to say when the file is opened: .NET reads a Unix file's read-only attribute
    /// from its mode bits alone, which ignores root, supplementary groups and access-control lists,
    /// so a check made here refused files the open would have written. Following links here by
    /// string rules instead got both wrong, for a link to a device such as <c>/dev/stdout</c>, which
    /// ends at <c>pipe:[N]</c>, and for a relative target containing <c>..</c> below a symbolic
    /// link to a directory.
    /// </summary>
    private static string ResolveSaveTarget(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        // Throws ArgumentException for an empty path or one holding a null character, and
        // PathTooLongException (an IOException) for an over-long one, as opening the file did.
        return Path.GetFullPath(path);
    }

    /// <summary>
    /// Whether something is already at the path, which decides whether a failed save may delete what
    /// the open created. A file there counts. Failing that, a symbolic link counts, a dangling one
    /// included, so a failed save never deletes a file it reached through a link and never touches
    /// the link; the link is looked for by reading its target. If that read says nothing is there
    /// (<see cref="FileNotFoundException"/> or <see cref="DirectoryNotFoundException"/>, which
    /// Windows can raise for a path that does not exist) the answer is no. Any other
    /// <see cref="IOException"/> or <see cref="UnauthorizedAccessException"/> from it means the link
    /// cannot be read, and that counts as something there: not deleting is the safe answer.
    /// </summary>
    private static bool SomethingExistsAt(string path)
    {
        if (File.Exists(path)) return true;

        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // Empties the file the PDF is about to be copied into. Skipped when there is nothing to remove,
    // which is also what keeps a device file working: /dev/null reports that it can seek and a
    // length of 0, and then refuses ftruncate with EINVAL.
    private static void EmptyTarget(FileStream fs)
    {
        if (!fs.CanSeek) return;
        if (fs.Length > 0) fs.SetLength(0);
        fs.Position = 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Nothing more can be done for a file this save created and could not remove, and the
            // exception that got us here is the one the caller needs to see.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private DocumentRenderer CreateRenderer()
    {
        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
        };
        foreach (var r in _content) renderer.Add(r);
        return renderer;
    }

    private void LayOut(DocumentRenderer renderer)
    {
        var pagesBefore = _pdf.Pages.Count;
        try
        {
            renderer.RunLayout();
        }
        catch
        {
            MarkFailedIfPagesAdded(pagesBefore);
            throw;
        }
    }

    /// <summary>
    /// Runs the layout and then the step that follows it, and treats a failure of either the same
    /// way: any exception, a cancellation included, once the layout had added pages leaves the
    /// document unable to be saved again (#530).
    /// </summary>
    private void LayOutThen(DocumentRenderer renderer, Action afterLayout)
    {
        var pagesBefore = _pdf.Pages.Count;
        try
        {
            renderer.RunLayout();
            afterLayout();
        }
        catch
        {
            MarkFailedIfPagesAdded(pagesBefore);
            throw;
        }
    }

    private void MarkFailedIfPagesAdded(int pagesBefore)
    {
        if (_pdf.Pages.Count != pagesBefore) _layoutFailed = true;
    }


    /// <summary>
    /// Copies a finished renderer's notify-and-continue reports onto this document, replacing the
    /// previous save's rather than accumulating across saves.
    ///
    /// Called from every save path — the stream and asynchronous overloads and the signing
    /// placeholder — because each builds its own DocumentRenderer over the same PdfDocument. Three
    /// copies of the same two lines is the drift the shared too-tall message exists to prevent
    /// (#460), so there is one copy and the call sites are named here instead.
    ///
    /// Every caller collects only after its write has succeeded. Two of them did not: the
    /// asynchronous and signing paths collected between the layout and the write, so a second call
    /// on an already-written document ran a whole second layout — appending fresh pages to the same
    /// PdfDocument — collected from it, and only then threw. That left reports naming a page
    /// present in no written output, and it made this method's Clear reachable through a failure.
    /// Collecting last means a throw leaves the previous save's reports untouched.
    /// </summary>
    private void CollectDiagnostics(DocumentRenderer renderer)
    {
        _textEncodingWarnings.Clear();
        _textEncodingWarnings.AddRange(renderer.TextEncodingWarnings);

        _bandTruncations.Clear();
        _bandTruncations.AddRange(renderer.BandTruncations);
    }

    /// <summary>Runs the layout pass and writes the resulting PDF to the given stream.</summary>
    /// <remarks>
    /// A document is single-use. The layout runs here, not when you add an element, so most of what
    /// can go wrong goes wrong at this call rather than at the one that set the bad value.
    /// <para>The save runs every element's renderer, a custom <see cref="IRenderer"/> included,
    /// reads every font registered with <see cref="UseTrueTypeFont"/>, and writes to
    /// <paramref name="destination"/>, so an exception from any of those reaches you from
    /// here.</para>
    /// <para>A second call after a save that succeeded throws. Unless a check the save runs earlier
    /// fails first, it reports that the document has already been written and tells you to create a
    /// new one. It writes nothing to the stream, appended or otherwise.</para>
    /// <para>A failure once the save has started writing leaves what was written so far in
    /// <paramref name="destination"/>, which can be the PDF header alone.</para>
    /// <para>What a save that threw leaves behind depends on where it threw (#530). A refusal that
    /// does not depend on the layout comes first, before anything is laid out: a null or unwritable
    /// <paramref name="destination"/>, a document already written, <see cref="UseObjectStreams"/>
    /// together with <see cref="Encrypt"/>, a PDF/A <see cref="Conformance"/> together with
    /// <see cref="Encrypt"/>, and PDF/UA-1 with settings that omit
    /// <see cref="PdfPermissions.Extract"/>. The document is untouched, so correct the cause and
    /// save again. A refused page geometry, such as margins that meet the page, is raised before any
    /// page is added and is just as recoverable. A save that threw <b>after</b> its layout had
    /// added pages cannot be undone, whether the layout itself threw, such as for an element too
    /// tall for the page, or a later step did, such as building the PDF or a cancellation seen once
    /// the layout has run. (With a <see cref="Header"/> or <see cref="Footer"/> set, a counting pass
    /// runs first, and it refuses a too-tall element before any page is added, so that case can be
    /// corrected and saved again.) Every later save after one that did add pages then
    /// throws <see cref="InvalidOperationException"/> telling you to create a new
    /// <see cref="Document"/>. Before this, the retry laid everything out again on top of the first
    /// attempt's pages and wrote a file with several times the pages you asked for, with no
    /// exception.</para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>. This is checked before the layout
    /// runs, so the document is untouched and can be saved again; see the remarks.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also catches
    /// it; <see cref="Document"/> implements <see cref="IDisposable"/>, so this is ordinary misuse
    /// rather than a case this API adds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>; see
    /// <see cref="Conformance"/>. It is also raised when a font registered with
    /// <see cref="UseTrueTypeFont"/> lacks a table the save needs, and by every save after one whose
    /// save threw once its layout had added pages (#530). For the element inputs, the boundary
    /// documentation on the property you set says which values reach this.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Many unrelated conditions share this type, so <b>do not</b> switch on the parameter name to
    /// tell them apart. A non-writable <paramref name="destination"/> is refused before the layout
    /// runs and reports <c>destination</c>. The margin check reports <c>margins</c>. A
    /// non-finite position the save writes outside the content stream, such as a link rectangle
    /// moved by a non-finite margin, reports <c>value</c>; see <see cref="Margins"/>. An element
    /// that refuses its own input reports a name of its own choosing, which is not always the
    /// property you set. A pie chart names the property; an image names a private field of its
    /// renderer (#481). Read the boundary documentation on the property instead, where each element
    /// has it. A registered font with a value that makes a font metric non-finite, such as a
    /// <c>unitsPerEm</c> of 0, reports <c>value</c>; see <see cref="UseTrueTypeFont"/>. Text that
    /// holds an unpaired surrogate and is measured in an embedded font raises it too, and reports
    /// <c>s</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called. The two
    /// cannot be combined.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member still stores without a check: a null <see cref="PageSize"/>. A custom
    /// renderer that returns a null result, or a null renderer inside one, raises it too; see
    /// <see cref="IRenderer.Layout"/>. A band template and an element, text or item passed to an
    /// <c>Add</c> overload are refused where they are passed instead (#523, #531, #547).
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// A <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name is selected
    /// on a page; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A font registered with <see cref="UseTrueTypeFont"/> has malformed outline data.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Cell.ColSpan"/> values sum past <see cref="int.MaxValue"/> while a
    /// table's column count is resolved.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A table's column count, resolved by <see cref="Cell.ColSpan"/> to something near
    /// <see cref="int.MaxValue"/>, asks for a per-column width array too large to allocate.
    /// </exception>
    /// <exception cref="IOException">
    /// Writing to <paramref name="destination"/> failed, such as on a full disk. The stream's own
    /// exception reaches you unchanged.
    /// </exception>
    public void Save(Stream destination)
    {
        BeginSave(destination);
        var renderer = CreateRenderer();
        LayOutThen(renderer, () => _pdf.Save(destination));
        CollectDiagnostics(renderer);
    }

    /// <summary>Runs the layout pass and writes the resulting PDF to a file at the given path.</summary>
    /// <remarks>
    /// Everything on <see cref="Save(System.IO.Stream)"/> applies, and one thing more.
    /// <para>The file is opened for writing first, before anything is laid out, without emptying it,
    /// and the PDF is built in memory. Only when the whole PDF is ready is the file emptied and the
    /// bytes copied in (#508). A lock or a permission error therefore surfaces while the document is
    /// still usable, and a save that fails at layout, at building the PDF or by cancellation leaves
    /// whatever the path held exactly as it was, byte for byte, and creates nothing at a path that
    /// did not exist. It used to open the path with truncation first, so a layout failure left a
    /// zero-byte file in place of the previous report.</para>
    /// <para>The existing file is rewritten in place, so it keeps its permissions, owner, hard links
    /// and extended attributes. The path is opened as given, so the operating system follows a
    /// symbolic link, a chain of them included, and the file at the end is rewritten while the link
    /// stays. A link whose target does not exist yet is followed too, so the save creates the
    /// target and keeps the link. A device or pipe such as <c>/dev/null</c> or <c>/dev/stdout</c>,
    /// which cannot be truncated, is written to as it is.</para>
    /// <para>A failed save deletes a file only if nothing was at the path when it started, and a
    /// symbolic link, a dangling one included, counts as something. So it never deletes a file it
    /// reached through a link, and never the link. <b>Attention</b>: the accepted gap is a failed
    /// save through a dangling link, which can leave an empty file at the link's destination,
    /// because the open created it and the save then could not tell that this call had.</para>
    /// <para>Whether the path can be written is decided when the file is opened, which is before the
    /// layout. A path that names a directory, a read-only file, or any file the operating system
    /// will not open for writing throws <see cref="UnauthorizedAccessException"/> then, with the
    /// document untouched. Nothing is checked beforehand, because .NET reads a Unix file's read-only
    /// attribute from its mode bits alone, which ignores root, supplementary groups and
    /// access-control lists.</para>
    /// <para><b>Attention</b>: the final copy is not atomic. A failure during it, such as a full
    /// disk, or a crash, can leave the file at the path truncated or incomplete. A file this call
    /// created is deleted then; a file that was already there is not restored. A file that another
    /// process creates at the path between this call's check that it exists and its open is taken
    /// for one this call created, and is deleted if the save fails.</para>
    /// <para><b>Attention</b>: the file is held open with <see cref="FileShare.None"/> from before
    /// the layout until the save returns. On Windows that excludes every other opener. On Linux and
    /// macOS it is an advisory lock, which other .NET code honours but a process that does not ask
    /// for the lock, <c>cp</c> or a text editor, can ignore.</para>
    /// <para><b>Attention</b>: the whole PDF is built in memory before any of it is written, in a
    /// <see cref="MemoryStream"/>, whose capacity is limited to about 2 GB. A larger PDF fails with
    /// <see cref="IOException"/> or <see cref="OutOfMemoryException"/>. To save one, open a
    /// <see cref="FileStream"/> yourself and pass it to <see cref="Save(System.IO.Stream)"/>, which
    /// writes to it as it goes. (A linearized save, which this class does not offer, buffers the
    /// whole file as well.) <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>
    /// builds the PDF in memory too, so it does not avoid the limit.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also
    /// catches it.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds a null character, or, on Windows, holds only spaces.
    /// It is raised while the file is opened. The layout and writing causes listed on
    /// <see cref="Save(System.IO.Stream)"/> reach here too, once it is open. That tag says why not
    /// to tell any of them apart by parameter name.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory named in <paramref name="path"/> does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="path"/> names a directory rather than a file, an existing file at
    /// <paramref name="path"/> is read-only, or you may not write to that location.
    /// </exception>
    /// <exception cref="IOException">
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, which is
    /// found before the layout runs, or <paramref name="path"/> is otherwise invalid for the file
    /// system, being over-long or holding a character the file system refuses, such as
    /// <c>&lt;</c> or <c>|</c>. It is also raised when writing the file fails, or when the PDF is
    /// too large for the memory buffer it is built in.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>, or a registered
    /// font lacks a table the save needs. None of these touches the file at the path; see the
    /// remarks.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member still stores without a check: a null <see cref="PageSize"/>. A custom
    /// renderer that returns a null result, or a null renderer inside one, raises it too; see
    /// <see cref="IRenderer.Layout"/>. A band template and an element, text or item passed to an
    /// <c>Add</c> overload are refused where they are passed instead (#523, #531, #547).
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// A <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name is selected
    /// on a page; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A font registered with <see cref="UseTrueTypeFont"/> has malformed outline data.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Cell.ColSpan"/> values sum past <see cref="int.MaxValue"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A column count resolved from <see cref="Cell.ColSpan"/> asks for a per-column width array
    /// too large to allocate.
    /// </exception>
    public void Save(string path)
    {
        var target = ResolveSaveTarget(path);
        var existed = SomethingExistsAt(target);

        // Opened before the layout, so a lock or a permission error surfaces while the document is
        // still usable, and every check BeginSave runs sees the stream the PDF will go to. Opening
        // does not truncate; the file is emptied only once the whole PDF is in memory.
        var fs = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var succeeded = false;
        DocumentRenderer renderer;
        try
        {
            try
            {
                BeginSave(fs);
                renderer = CreateRenderer();
                using var buffer = new MemoryStream();
                LayOutThen(renderer, () => _pdf.Save(buffer));

                EmptyTarget(fs);

                fs.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                fs.Flush();
            }
            finally
            {
                // Inside its own try, so a Dispose that throws (the flush of a full disk) still
                // reaches the delete below.
                fs.Dispose();
            }

            succeeded = true;
        }
        finally
        {
            if (!succeeded && !existed) TryDelete(target);
        }

        CollectDiagnostics(renderer);
    }

    /// <summary>
    /// Asynchronously runs the layout pass and writes the resulting PDF to the given stream.
    ///
    /// <para>The layout pass is CPU-bound, so it runs on a thread-pool thread via
    /// <see cref="Task.Run(Action)"/>; the resulting document is then written to
    /// <paramref name="destination"/> via <see cref="PdfDocument.SaveAsync"/>.
    /// <paramref name="cancellationToken"/> does not abort layout or serialisation already in
    /// progress.</para>
    /// </summary>
    /// <remarks>
    /// A document is single-use. The layout runs here, not when you add an element, so most of what
    /// can go wrong goes wrong at this call rather than at the one that set the bad value. Each
    /// exception this method lists reaches you when the returned task is awaited: it faults the
    /// task, except a cancellation, which ends the task in the <c>Canceled</c> state.
    /// <para>The save runs every element's renderer, a custom <see cref="IRenderer"/> included,
    /// reads every font registered with <see cref="UseTrueTypeFont"/>, and writes to
    /// <paramref name="destination"/>, so an exception from any of those reaches you from
    /// here.</para>
    /// <para>A second call after a save that succeeded throws. Unless a check the save runs earlier
    /// fails first, it reports that the document has already been written and tells you to create a
    /// new one. It writes nothing to the stream, appended or otherwise.</para>
    /// <para>The PDF is built in memory before any of it is written to
    /// <paramref name="destination"/>, so a failure while it is built writes nothing to
    /// <paramref name="destination"/>.</para>
    /// <para>What a save that threw leaves behind depends on where it threw (#530). A refusal that
    /// does not depend on the layout comes first, before anything is laid out: a null or unwritable
    /// <paramref name="destination"/>, a document already written, <see cref="UseObjectStreams"/>
    /// together with <see cref="Encrypt"/>, a PDF/A <see cref="Conformance"/> together with
    /// <see cref="Encrypt"/>, and PDF/UA-1 with settings that omit
    /// <see cref="PdfPermissions.Extract"/>. The document is untouched, so correct the cause and
    /// save again. A refused page geometry, such as margins that meet the page, is raised before any
    /// page is added and is just as recoverable. A save that threw <b>after</b> its layout had
    /// added pages cannot be undone, whether the layout itself threw, such as for an element too
    /// tall for the page, or a later step did, such as building the PDF or a cancellation seen once
    /// the layout has run. (With a <see cref="Header"/> or <see cref="Footer"/> set, a counting pass
    /// runs first, and it refuses a too-tall element before any page is added, so that case can be
    /// corrected and saved again.) Every later save after one that did add pages then
    /// throws <see cref="InvalidOperationException"/> telling you to create a new
    /// <see cref="Document"/>. Before this, the retry laid everything out again on top of the first
    /// attempt's pages and wrote a file with several times the pages you asked for, with no
    /// exception.</para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>. This is checked before the layout
    /// runs, so the document is untouched and can be saved again; see the remarks.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also catches
    /// it; <see cref="Document"/> implements <see cref="IDisposable"/>, so this is ordinary misuse
    /// rather than a case this API adds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>; see
    /// <see cref="Conformance"/>. It is also raised when a font registered with
    /// <see cref="UseTrueTypeFont"/> lacks a table the save needs, and by every save after one whose
    /// save threw once its layout had added pages (#530). For the element inputs, the boundary
    /// documentation on the property you set says which values reach this.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="destination"/> cannot be written to, which includes a stream that was already
    /// closed. That is refused before the layout runs and reports <c>destination</c>, so the
    /// document is untouched; it used to be <see cref="NotSupportedException"/> or
    /// <see cref="ObjectDisposedException"/> from the write. The margins, header and footer leave
    /// the content area no positive size, or an element refuses its own input while being laid
    /// out. The boundary documentation on the individual
    /// properties says which inputs those are. A registered font with a value that makes a font
    /// metric non-finite, such as a <c>unitsPerEm</c> of 0, raises it too; see
    /// <see cref="UseTrueTypeFont"/>. So does text that holds an unpaired surrogate and is measured
    /// in an embedded font, and a non-finite position the save writes outside the content stream;
    /// see <see cref="Margins"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called, which cannot be
    /// combined.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member still stores without a check: a null <see cref="PageSize"/>. A custom
    /// renderer that returns a null result, or a null renderer inside one, raises it too; see
    /// <see cref="IRenderer.Layout"/>. A band template and an element, text or item passed to an
    /// <c>Add</c> overload are refused where they are passed instead (#523, #531, #547).
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// A <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name is selected
    /// on a page; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A font registered with <see cref="UseTrueTypeFont"/> has malformed outline data.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Cell.ColSpan"/> values sum past <see cref="int.MaxValue"/> while a
    /// table's column count is resolved.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A table's column count, resolved by <see cref="Cell.ColSpan"/> to something near
    /// <see cref="int.MaxValue"/>, asks for a per-column width array too large to allocate.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token is looked at three times: before the layout starts, once it has finished, and once
    /// serialisation has finished. A cancellation seen at one of those points ends the task with a
    /// <see cref="TaskCanceledException"/> before anything is written. A failure that comes first
    /// is reported instead, such as a refused page geometry, an exception from an element, a null
    /// destination, or a closed or non-writable one, which is refused before the layout. During the
    /// write, <paramref name="destination"/> receives the token and decides what to raise.
    /// </exception>
    /// <exception cref="IOException">
    /// Writing to <paramref name="destination"/> failed, such as on a full disk. The stream's own
    /// exception reaches you unchanged.
    /// </exception>
    // RS0026 flags multiple overloads with optional parameters as a future-ambiguity risk;
    // Stream and string share no implicit conversion, so overload resolution can never be
    // ambiguous between these two.
#pragma warning disable RS0026
    public async Task SaveAsync(Stream destination, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        BeginSave(destination);
        var renderer = CreateRenderer();
        var pagesBefore = _pdf.Pages.Count;
        await Task.Run(() => LayOut(renderer), cancellationToken).ConfigureAwait(false);
        try
        {
            await _pdf.SaveAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // A token cancelled once the layout has run ends the task before anything is serialised,
            // which leaves the pages behind just as a throw from the layout does (#530).
            MarkFailedIfPagesAdded(pagesBefore);
            throw;
        }

        CollectDiagnostics(renderer);
    }

    /// <summary>Asynchronously runs the layout pass and writes the resulting PDF to a file at the given path.</summary>
    /// <remarks>
    /// Everything on <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>
    /// applies, and one thing more, except the points where the token is looked at, which differ
    /// because the PDF goes into a file rather than a caller's stream: the
    /// <see cref="OperationCanceledException"/> tag below lists them.
    /// <para>The file is opened first and emptied only once the PDF is built in memory, as on
    /// <see cref="Save(string)"/> (#508). A save that fails, at layout, at building the PDF or by
    /// cancellation, therefore leaves whatever the path held exactly as it was, byte for byte, and
    /// creates nothing at a path that did not exist. Everything <see cref="Save(string)"/> says
    /// about symbolic links, permissions, the advisory lock, the memory limit of about 2 GB and the
    /// final copy not being atomic applies here too.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is
    /// <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also
    /// catches it.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds a null character, or, on Windows, holds only spaces.
    /// It is raised while the file is opened. The layout and writing causes listed on
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/> reach here
    /// too, once it is open. These are unrelated conditions that happen to share a type, so do
    /// <b>not</b> tell them apart by parameter name.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory named in <paramref name="path"/> does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="path"/> names a directory rather than a file, an existing file at
    /// <paramref name="path"/> is read-only, or you may not write to that location.
    /// </exception>
    /// <exception cref="IOException">
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, which is
    /// found before the layout runs, or <paramref name="path"/> is otherwise invalid for the file
    /// system, being over-long or holding a character the file system refuses, such as
    /// <c>&lt;</c> or <c>|</c>. It is also raised when writing the file fails, or when the PDF is
    /// too large for the memory buffer it is built in.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>, or a registered
    /// font lacks a table the save needs. None of these touches the file at the path; see the
    /// remarks.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member still stores without a check: a null <see cref="PageSize"/>. A custom
    /// renderer that returns a null result, or a null renderer inside one, raises it too; see
    /// <see cref="IRenderer.Layout"/>. A band template and an element, text or item passed to an
    /// <c>Add</c> overload are refused where they are passed instead (#523, #531, #547).
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// A <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name is selected
    /// on a page; see <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// A font registered with <see cref="UseTrueTypeFont"/> has malformed outline data.
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Cell.ColSpan"/> values sum past <see cref="int.MaxValue"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A column count resolved from <see cref="Cell.ColSpan"/> asks for a per-column width array
    /// too large to allocate.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The token is looked at twice: before the layout starts, and once it has finished, when the
    /// serialisation is queued. A cancellation seen at either point ends the task with a
    /// <see cref="TaskCanceledException"/> before the file is emptied, so the file that was there is
    /// untouched; see the remarks. A failure that comes first, such as a refused page geometry or
    /// an exception from an element, is reported instead. Once the serialisation is under way the
    /// token is no longer looked at, and neither is the copy into the file, because stopping
    /// halfway would leave a truncated file. A cancellation after the layout has run also means this
    /// <see cref="Document"/> cannot be saved again; see
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>.
    /// </exception>
#pragma warning disable RS0026
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        var target = ResolveSaveTarget(path);
        var existed = SomethingExistsAt(target);

        // See Save(string): opened before the layout, emptied only once the PDF is in memory.
        var fs = new FileStream(
            target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var succeeded = false;
        DocumentRenderer renderer;
        try
        {
            try
            {
                BeginSave(fs);
                renderer = CreateRenderer();
                using var buffer = new MemoryStream();
                var pagesBefore = _pdf.Pages.Count;
                await Task.Run(() => LayOut(renderer), cancellationToken).ConfigureAwait(false);
                try
                {
                    // The last point where the token is looked at, when the serialisation is queued.
                    // Past it the PDF is built and copied to the end, since stopping halfway would
                    // leave a truncated file over a good one. A cancelled queue throws
                    // TaskCanceledException, as the point before the layout does.
                    await Task.Run(() => _pdf.Save(buffer), cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    MarkFailedIfPagesAdded(pagesBefore);
                    throw;
                }

                EmptyTarget(fs);

                await fs.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), CancellationToken.None).ConfigureAwait(false);
                await fs.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                await fs.DisposeAsync().ConfigureAwait(false);
            }

            succeeded = true;
        }
        finally
        {
            if (!succeeded && !existed) TryDelete(target);
        }

        CollectDiagnostics(renderer);
    }

    // ── Signing seam (VellumPdf.Signing only) ────────────────────────────────

    /// <summary>
    /// Runs the layout pass and delegates to
    /// <see cref="PdfDocument.PrepareForSigning"/> to produce the unsigned placeholder bytes.
    /// Called exclusively by <c>VellumPdf.Signing.SigningExtensions</c>.
    /// </summary>
    internal byte[] PrepareForSigning(SignaturePlaceholderOptions options)
    {

        // Everything PdfDocument.PrepareForSigning refuses without looking at the pages, and the
        // encryption refusal among it, comes before the layout: after it, the pages stay behind and
        // the next save lays out a second time on top of them (#530).
        //
        // A document with nothing in it lays out no pages, so its pages are already known: none. It
        // gets the document's own order, which reports "no pages" before looking at the options, as
        // it did before the pre-check existed, and nothing is laid out or marked as failed.
        _pdf.EnsureCanPrepareForSigning(options, pagesKnown: _content.Count == 0);
        ThrowIfLayoutFailed();

        var renderer = CreateRenderer();
        byte[] prepared = [];
        LayOutThen(renderer, () => prepared = _pdf.PrepareForSigning(options));
        CollectDiagnostics(renderer);
        return prepared;
    }

    /// <summary>Marks the underlying <see cref="PdfDocument"/> as disposed.</summary>
    /// <remarks>
    /// Calling it again does nothing. Afterwards only <see cref="Encrypt"/>, the saves and signing
    /// through <c>VellumPdf.Signing</c> check whether the document is disposed.
    /// <see cref="Encrypt"/> throws <see cref="ObjectDisposedException"/>, and so does a save,
    /// unless a check the save runs earlier fails first. A save to a path leaves the file at the path
    /// untouched when it throws that. Every other member works as before.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// Raised from a later save or <see cref="Encrypt"/>, not from this call.
    /// </exception>
    public void Dispose() => _pdf.Dispose();
}
