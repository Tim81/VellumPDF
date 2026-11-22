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
/// <para><b>A non-finite number is mostly not checked before the layout.</b> An inset, indent, colour
/// channel or band height that is <c>NaN</c> or infinite is accepted by the member that holds it. The
/// save then throws only where the layout or the canvas writes the value: <see cref="PdfCanvas"/>, and
/// the <c>PdfReal</c> constructor for a bookmark or a link rectangle, throw
/// <see cref="ArgumentException"/> saying PDF does not support NaN or Infinity as a real number.
/// Otherwise the result depends on the content: the file can save with the content drawn, wrapped
/// after every word, placed off the page or not drawn at all, or the layout can refuse it as too tall
/// or after 50,000 page continuations.</para>
/// <para><b>Where the layout checks run.</b> A few inputs are refused in the layout and name the
/// element, because each yields an invalid file or an exception. Each runs inside the element's own
/// <c>Layout</c> call, in document order, so an earlier element that is too tall for the page is
/// reported first and a later element's own refusal is not reached: the font size and colour of a
/// paragraph run, heading or list item, the font size of a table cell, a line separator's width,
/// margins and colour, a table cell's padding, a table's border width when it is not finite, an
/// image extent that is zero or not finite, and a <c>ColSpan</c> below one. Three run where the value is
/// written, because a table or band that is laid out and not drawn writes no invalid token: a table
/// cell's text colour, a negative table border width, and a running band's font size. These are
/// <see cref="InvalidOperationException"/>, so a <c>catch</c> of <see cref="ArgumentException"/>
/// around a save does not see them.</para>
/// <para>A call with two or more defects is refused, but which defect's exception is reported can
/// differ between releases. A save precondition (null destination, disposed document, an unwritable
/// stream, an option conflict) is reported before a layout defect, and an already cancelled token
/// before both. In a document with nothing added, or only page breaks, the page geometry and bands
/// are checked before the preconditions, but not before the token. The path checks of
/// <see cref="SaveAsync(string, System.Threading.CancellationToken)"/> are the exception: they run
/// when the file is opened, before the token is looked at.</para>
/// <para><b>Do not pass</b> a non-finite number to any of these members. The saved cases are not
/// reported: <see cref="Diagnostics"/> has no code for them yet, and 3.0 refuses every non-finite
/// value at the member that holds it.</para>
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
    /// <para>This list holds one entry per substituted character and has no limit, and it stays as
    /// it is until 3.0. <see cref="Diagnostics"/> holds the same substitutions grouped by
    /// character; see <see cref="LayoutDiagnosticCode.UnencodableCharacter"/> for what it leaves
    /// out.</para>
    /// <para>A character an embedded font has no glyph for is written as glyph 0, not as '?', and
    /// is not listed here. <see cref="LayoutDiagnosticCode.NotdefGlyph"/> reports it in
    /// <see cref="Diagnostics"/>.</para>
    /// </remarks>
    public IReadOnlyList<TextEncodingWarning> TextEncodingWarnings => _textEncodingWarnings;

    private IReadOnlyList<LayoutDiagnostic> _diagnostics = Array.Empty<LayoutDiagnostic>().AsReadOnly();

    /// <summary>
    /// What the last save noticed and kept going through, one item per condition and subject.
    /// </summary>
    /// <remarks>
    /// Empty until a save (or signing prep) has run, and replaced by each one that succeeds. A save
    /// that threw leaves it as it was, with one exception: a refusal by
    /// <see cref="StrictDiagnostics"/> fills it with the items the refused layout reported, because
    /// that layout is the document's only one.
    /// <para>The order is drawing order, except that
    /// <see cref="LayoutDiagnosticCode.BandTruncated"/> items follow all the others, and
    /// <see cref="LayoutDiagnosticCode.DiagnosticsDropped"/> is last.</para>
    /// <para>The codes, each described on <see cref="LayoutDiagnosticCode"/>:
    /// <see cref="LayoutDiagnosticCode.NullTextRunStyle"/> (a run with a null style that yields no
    /// word), <see cref="LayoutDiagnosticCode.UnencodableCharacter"/> (a character written as
    /// <c>?</c>, mirroring <see cref="TextEncodingWarnings"/>),
    /// <see cref="LayoutDiagnosticCode.TabStopBeyondWidth"/> (a tab stop treated as absent because
    /// it lies beyond the width a custom renderer laid the paragraph out at),
    /// <see cref="LayoutDiagnosticCode.NullGrandchildText"/> and
    /// <see cref="LayoutDiagnosticCode.NullGrandchild"/> (a null below the second level of a list),
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> (a character an embedded font has no glyph
    /// for, written as glyph 0), <see cref="LayoutDiagnosticCode.UnembeddedFont"/> (an element that
    /// selects a Standard 14 font under a conformance claim),
    /// <see cref="LayoutDiagnosticCode.BandTruncated"/> (mirroring <see cref="BandTruncations"/>),
    /// <see cref="LayoutDiagnosticCode.PreformattedLineWrapped"/> (a preformatted line split with
    /// a marker; a warning, which <see cref="StrictDiagnostics"/> saves),
    /// <see cref="LayoutDiagnosticCode.CellWhiteSpaceDropped"/> (a table cell's tab or line break left
    /// out of what is drawn; also a warning)
    /// and <see cref="LayoutDiagnosticCode.DiagnosticsDropped"/>. Every other one saves by
    /// default; only <see cref="StrictDiagnostics"/> refuses it, and a later major version refuses
    /// them all.</para>
    /// <para>The channel stores at most 1,000 distinct items, and adds
    /// <see cref="LayoutDiagnosticCode.DiagnosticsDropped"/> after them when it dropped any, so the
    /// list can hold 1,001. That item's <see cref="LayoutDiagnostic.Count"/> is the number of
    /// occurrences dropped. A repeat of a stored item adds to its count. No message quotes caller
    /// text beyond the one code point it names.</para>
    /// <para><b>What is not reported.</b> A <see cref="DrawContext"/> you construct carries no
    /// channel, so a built-in renderer drawn with it reports nothing of its own. A character
    /// written as <c>?</c> on the document's canvas is still listed, because it is read from the
    /// canvas after each element is drawn, whichever renderer wrote it. The one exception is text
    /// the library wrote itself: the marker of a built-in list is not listed when the document
    /// draws the list, and through a context you built it can be. Glyph 0 that a custom renderer
    /// writes with <see cref="PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/> on the
    /// document's canvas is listed as <see cref="LayoutDiagnosticCode.NotdefGlyph"/>, with the
    /// character unknown, whichever renderer wrote it. A context you construct reports no
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>, a Standard 14 font that a renderer
    /// fetches with <see cref="DrawContext.GetFont(VellumPdf.Fonts.Standard14)"/> and never selects
    /// is not reported, and neither is one that a renderer selects with its own
    /// <see cref="PdfCanvas.SetFont(VellumPdf.Fonts.PdfFontResource, double)"/> call. Callers of
    /// the Kernel alone have no channel except
    /// <see cref="VellumPdf.Document.PdfDocument.NotdefGlyphs"/> for glyph 0, and
    /// <see cref="VellumPdf.Layout.Rendering.DocumentRenderer.Render(System.IO.Stream)"/> exposes
    /// none.
    /// Input that is refused is thrown, not listed. <b>Do not</b> read an empty list as proof that
    /// the document is free of every defect the library keeps: only the codes above are reported
    /// yet.</para>
    /// </remarks>
    public IReadOnlyList<LayoutDiagnostic> Diagnostics => _diagnostics;

    /// <summary>
    /// Whether a save refuses a document whose layout reports an error item. Off by default.
    /// </summary>
    /// <remarks>
    /// <b>A save that would report an error item is refused.</b> Every save overload
    /// (<see cref="Save(System.IO.Stream)"/>, <see cref="Save(string)"/>,
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>,
    /// <see cref="SaveAsync(string, System.Threading.CancellationToken)"/>) and signing preparation
    /// through <c>VellumPdf.Signing</c> throw <see cref="InvalidOperationException"/> from the
    /// call, after the layout and before anything is written. The message names the first error
    /// item and how many further stored error items there were; errors dropped past the channel's
    /// limit are not in that number. A stream is left as it was. An existing file at the path keeps
    /// its bytes, and a file the save created is removed. The document cannot be saved again, as
    /// after any failure once the layout has started.
    /// <para>A save precondition (a null or unwritable destination, a disposed document, an option
    /// conflict) and a refusal raised while the document is laid out, including a non-finite value
    /// the canvas refuses, are reported first, exactly as with the switch off. A refusal that only
    /// writing the PDF would raise, such as a signature page past the last page or a null inside a
    /// <see cref="PdfDictionary"/>, is not reached: the strict refusal comes first. So is a
    /// cancellation seen after the layout started: a token cancelled while an element is drawn
    /// gives the strict refusal, not <see cref="OperationCanceledException"/>. A token already
    /// cancelled when the call starts still wins, as it does with the switch off. The switch is
    /// read when the save runs, so setting it after an element is added applies to that element.
    /// A <see cref="LayoutDiagnosticSeverity.Warning"/> item never makes the save throw.</para>
    /// <para>A later major version refuses every error item whatever this property says. Do not
    /// rely on leaving it off to keep such a document saving.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this setter, when an error item is reported.
    /// </exception>
    public bool StrictDiagnostics { get; set; }

    /// <summary>
    /// Running bands whose text was cut to fit the content box, from the last save that succeeded.
    /// At most one report per band, each naming the page that lost the most. Empty when no band was
    /// cut, or when no band was set.
    /// </summary>
    /// <remarks>
    /// Empty until a save (or signing prep) has run, and replaced by each one that succeeds. A cut
    /// band loses text the caller wrote, so it is also listed in <see cref="Diagnostics"/> as
    /// <see cref="LayoutDiagnosticCode.BandTruncated"/> with error severity, which
    /// <see cref="StrictDiagnostics"/> refuses. This list stays as it is until 3.0.
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
    /// <para>The size in force when the save runs applies to the whole document, including elements
    /// added before it was set.</para>
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
    /// <para>With a level set, the save reports an element that selects a Standard 14 font, such as
    /// the Helvetica a list, table, heading, paragraph or running band without its own style falls
    /// back to, as <see cref="LayoutDiagnosticCode.UnembeddedFont"/>, and a character an embedded
    /// font has no glyph for as <see cref="LayoutDiagnosticCode.NotdefGlyph"/>. Both save unless
    /// <see cref="StrictDiagnostics"/> is set; see <see cref="Diagnostics"/>.
    /// <see cref="SetDefaultFont"/> does not reach those elements. <b>Do not pass</b> a style with
    /// a Standard 14 font, or text the font cannot draw, to a PDF/A or PDF/UA document. A later
    /// major version refuses text the font cannot draw, and a Standard 14 font, under either
    /// claim.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when
    /// <see cref="Encrypt"/> has been called and this is a PDF/A level or a value the enumeration
    /// does not name, or PDF/UA-1 without <see cref="PdfPermissions.Extract"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the same calls, not from this setter, when <see cref="StrictDiagnostics"/> is
    /// set and the document has a level and writes a Standard 14 font or a glyph 0; the save
    /// reports them as <see cref="LayoutDiagnosticCode.UnembeddedFont"/> and
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/>.
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

    /// <summary>
    /// The instant written as the XMP creation and modification date and mixed into the derived
    /// file identifier. Null, the default, means the time of the save. Forwarded to the underlying
    /// <see cref="PdfDocument"/>.
    /// </summary>
    /// <remarks>
    /// <b>Pinning this makes the output reproducible for one library build on one runtime and one
    /// operating system.</b> Two documents built by the same calls then save to the same bytes,
    /// because the save reads the clock only when this is null and for a signature's time, and
    /// reads random values only to encrypt. Those are the only clock and random reads in the
    /// Kernel, Layout and Signing projects; code outside those projects and the .NET framework
    /// are not covered by that statement.
    /// <para>It does not hold when:</para>
    /// <list type="bullet">
    /// <item>An encrypted document is never reproducible: <see cref="Encrypt"/> draws random
    /// salts and initialisation vectors on every save.</item>
    /// <item>A document signed through VellumPdf.Signing takes the signature's time from
    /// <c>PdfSignatureSettings.SigningTime</c> in that package, not from this property, and the
    /// signature value itself is outside this guarantee.</item>
    /// <item>The instant is before 1970 (a negative Unix time) and <see cref="DocumentId"/> is
    /// null: the derived identifier is hashed from the instant's decimal text, and the minus sign
    /// of that text follows the current culture. Cultures whose negative sign differs from the invariant
    /// one (sv-SE, nb-NO, fa-IR and ar-SA among them) give another identifier. Pin
    /// <see cref="DocumentId"/> as well to remove the dependence.</item>
    /// </list>
    /// <para>The XMP dates keep whole seconds and the offset and drop the fraction of a second.
    /// The derived file identifier uses the instant in milliseconds and ignores the offset, so
    /// the same instant with two offsets gives one identifier and two XMP dates. The identifier
    /// is derived from the title, the producer (<c>VellumPdf</c> when <c>Info.Producer</c> is
    /// null) and the page count together with this instant, so <see cref="DocumentId"/> is not
    /// needed for reproducibility, except for the pre-1970 case above.</para>
    /// <para>The value is read once, when a save writes. A value set after the document has
    /// saved is stored and never written, because a document saves once. A refused save that
    /// failed before layout started leaves the document usable, and a value set before the retry
    /// is the one written.</para>
    /// </remarks>
    public DateTimeOffset? Timestamp
    {
        get => _pdf.Timestamp;
        set => _pdf.Timestamp = value;
    }

    /// <summary>
    /// The permanent file identifier, written as both strings of the trailer <c>/ID</c> array.
    /// Null, the default, means an identifier derived from the title, the producer, the page
    /// count and <see cref="Timestamp"/>. Forwarded to the underlying <see cref="PdfDocument"/>.
    /// </summary>
    /// <remarks>
    /// <b>An array that is not 16 bytes is refused.</b> The setter throws
    /// <see cref="ArgumentException"/> naming the length, because the writer emits no
    /// <c>/ID</c> for any other length and ISO 32000-2, Table 15 requires one once the file is
    /// encrypted. Every 16-byte value is accepted, an all-zero one included.
    /// <para>The array is copied on get and on set, so changing the caller's array afterwards does
    /// not change the output. A value set after the document has saved is stored and never
    /// written.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Thrown by the setter when the value is neither null nor exactly 16 bytes long.
    /// </exception>
    public byte[]? DocumentId
    {
        get => _pdf.DocumentId;
        set => _pdf.DocumentId = value;
    }

    /// <summary>Page margins applied to the content area. Defaults to 72 points (1 inch) on all sides.</summary>
    /// <remarks>
    /// Margins that meet or exceed the page are refused. Saving throws
    /// <see cref="ArgumentException"/> and names the axis and both figures, because the content box
    /// would otherwise have no positive size.
    /// <para>The header and footer count toward this. Their heights come off the same box, so
    /// margins that fit on their own can still leave nothing once you set a running band.</para>
    /// <para>With <see cref="MirrorMargins"/> set, <see cref="EdgeInsets.Left"/> is the inside margin
    /// and <see cref="EdgeInsets.Right"/> the outside one, and every refusal here applies to the
    /// pair as it does without it.</para>
    /// <para><b>Attention</b>: a finite negative inset is not refused. That edge of the content
    /// area then lies at the inset plus the height of any header or footer there, measured inward
    /// from the page's edge, so a negative total places it beyond the page, where a reader clips
    /// the content, and the file contains no invalid token. A finite inset large enough in magnitude to
    /// overflow the position arithmetic, such as -1e308 on the left or right edge alone, also passes the
    /// check above; what it does depends on the elements laid out.</para>
    /// <para>An inset that is not a finite number is not checked before the layout; see
    /// <see cref="Document"/>. A positive-infinity inset meets the page and is refused with
    /// <see cref="ArgumentException"/> (parameter name <c>margins</c>). <c>NaN</c> and negative infinity
    /// pass that check and then give whatever the layout gives: a table can save, drawn or not, a
    /// paragraph is refused after 50,000 page continuations, and a position the canvas writes is
    /// refused. <b>Do not pass</b> a non-finite or a finite negative inset: 3.0 refuses both.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, when the margins on either axis meet or
    /// exceed the page, positive infinity included, or when they leave the content area no positive
    /// size once the header and footer are taken off. It is also raised, with a message saying PDF
    /// does not support NaN or Infinity as a real number, when a <c>NaN</c> or infinite inset puts
    /// a position the layout writes, or when finite insets whose arithmetic overflows put
    /// a position the save writes outside the content stream, such as a link rectangle, at a
    /// non-finite value.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save when an inset that passes the <see cref="ArgumentException"/> check
    /// leaves an element no area it can be laid out in, or leaves an image an extent it cannot
    /// draw, and also when a large finite inset leaves the content area positive but too small for
    /// a single element, or when a single element needs more than 50,000 page continuations, which a
    /// negative-infinity top inset gives a paragraph.
    /// </exception>
    public EdgeInsets Margins { get; set; } = new EdgeInsets(72); // 1 inch

    /// <summary>
    /// Mirrors the left and right margins for double-sided print. Defaults to <see langword="false"/>,
    /// where <see cref="Margins"/> applies unchanged to every page.
    /// </summary>
    /// <remarks>
    /// <b>Nothing new is refused.</b> <see cref="EdgeInsets.Left"/> becomes the inside margin and
    /// <see cref="EdgeInsets.Right"/> the outside one, and the content box keeps the width
    /// <c>page width - Left - Right</c> on every page, so a save refuses exactly the margins it
    /// refuses with this property off, with the same exception and message; see
    /// <see cref="Margins"/>. The refusal raised for the size and the refusal raised for a
    /// non-finite inset are therefore unchanged by this property.
    /// <para>The inside margin is on the left of a right-hand page (an odd physical page number,
    /// page 1 first) and on the right of a left-hand page. The top and bottom margins and the
    /// header and footer heights are the same on every page. Every renderer is laid out in the same
    /// width on every page, so a paragraph wraps the same way wherever it falls, and only its left
    /// edge moves. Body alignment never swaps.</para>
    /// <para>A running band's <see cref="HorizontalAlignment.Left"/> and
    /// <see cref="HorizontalAlignment.Right"/> alignments swap on a left-hand page, so a page
    /// number aligned right stays on the outside edge; <see cref="HorizontalAlignment.Center"/>
    /// centres in that page's content box. A band is drawn as left unless it asks for center or
    /// right (see <see cref="RunningBand.Alignment"/>), so <see cref="HorizontalAlignment.Justify"/>
    /// and a value the enumeration does not name are drawn on the right of a left-hand page too.</para>
    /// <para>The side follows the page's position in the file, not its page label, and a blank page
    /// that a <see cref="PageBreak.Recto"/> inserts counts as a page. The first page is always a
    /// right-hand page: nothing configures the other side.</para>
    /// <para>A custom <see cref="IRenderer"/> is offered the content area of the page it is laid
    /// out on, so when <see cref="EdgeInsets.Left"/> and <see cref="EdgeInsets.Right"/> differ its
    /// <see cref="LayoutContext.Area"/> has a different X from one page to the next. A renderer
    /// that keeps an X from an earlier <see cref="IRenderer.Layout"/> call must not reuse it on
    /// the next page.</para>
    /// <para>The value is read when a save lays the document out. A value set after the document
    /// has saved is stored and never used, because a document saves once. A refused save that
    /// failed before layout started leaves the document usable, and a value set before the retry
    /// is the one used.</para>
    /// </remarks>
    public bool MirrorMargins { get; set; }

    /// <summary>
    /// Optional header band drawn at the top of every page.
    /// Set via <see cref="SetHeader"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    /// <remarks>
    /// A band whose <see cref="RunningBand.Template"/> is null is refused when the band is drawn,
    /// not when it is assigned. The band's height and style carry
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
    /// <inheritdoc cref="Header" path="/remarks"/>
    /// <inheritdoc cref="Header" path="/exception"/>
    public RunningBand? Footer { get; set; }

    /// <summary>
    /// The page-label range that starts at the first page, or <see langword="null"/> (the default)
    /// for none. A viewer shows the labels in its page box, and a running band's <c>{page}</c>
    /// prints them.
    /// </summary>
    /// <remarks>
    /// <b>Nothing is refused here.</b> <see cref="PageLabelRange"/> refuses its own bad input, and
    /// a break's range is on <see cref="PageBreak.PageLabel"/>. Setting this is the same as a
    /// labelled <see cref="PageBreak"/> before the first element, except that it needs no element
    /// to follow. A range that a <see cref="PageBreak.PageLabel"/> starts on the first page
    /// replaces it.
    /// <para>Once any range exists, the save writes a <c>/PageLabels</c> number tree, and when no
    /// range starts at the first page it adds one there that numbers in decimal from 1, which is
    /// what a viewer shows without labels. A document with no range writes no <c>/PageLabels</c>
    /// tree. <c>{pages}</c> stays the number of pages, whatever the labels are.</para>
    /// <para>A page that a custom <see cref="IRenderer"/> fills is labelled like any
    /// other, but such a renderer cannot start a range of its own.</para>
    /// </remarks>
    public PageLabelRange? PageLabel { get; set; }

    /// <summary>
    /// Sets the text style that later <see cref="Add(string, TextStyle)"/> calls use when they are
    /// given no style. Returns this document for chaining.
    /// </summary>
    /// <remarks>
    /// <b>Attention</b>: nothing else reads it. <see cref="Add(string, TextStyle)"/> takes the
    /// style in force when it is called, so text added before this call keeps the earlier
    /// style, and a <see cref="Paragraph"/>, <see cref="Heading"/>, list, table or running band
    /// never uses it. Give those their own style.
    /// <para>A list, table, paragraph, heading or running band without a style of its own does not
    /// take this one. The font is then Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
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
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
    /// </exception>
    public Document SetDefaultFont(TextStyle style) { _defaultStyle = style; return this; }

    /// <summary>Sets a header band with optional style and alignment. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="template"/> is refused here. The parameter is non-nullable, but
    /// that is a compiler diagnostic rather than a check, and <c>null!</c> silences it. This method
    /// builds a <see cref="RunningBand"/>, whose constructor throws, so the failure lands on this call. Pass an empty string for a band that draws no text. A null band
    /// assigned to <see cref="Header"/> directly is a different case and means no band.
    /// <para>The band's height and <paramref name="style"/> carry refusals of their own; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/>,
    /// <see cref="TextStyle.Leading"/> and <see cref="TextStyle.FontRef"/>.
    /// <see cref="HorizontalAlignment.Justify"/> and values the enumeration does not name are drawn
    /// left-aligned; see <see cref="RunningBand.Alignment"/>.</para>
    /// <para>A null <paramref name="style"/> is <see cref="TextStyle.Default"/>, not the style set
    /// with <see cref="SetDefaultFont"/>. The font is then Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> is <see langword="null"/>.
    /// </exception>
    /// <inheritdoc cref="Header" path="/exception[@cref='T:System.ArgumentException' or @cref='T:System.InvalidOperationException' or @cref='T:System.IndexOutOfRangeException']"/>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
    /// </exception>
    public Document SetHeader(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Header = new RunningBand(template, style, alignment);
        return this;
    }

    /// <summary>Sets a footer band with optional style and alignment. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="template"/> is refused here. The parameter is non-nullable, but
    /// that is a compiler diagnostic rather than a check, and <c>null!</c> silences it. This method
    /// builds a <see cref="RunningBand"/>, whose constructor throws, so the failure lands on this call. Pass an empty string for a band that draws no text. A null band
    /// assigned to <see cref="Footer"/> directly is a different case and means no band.
    /// <para>The band's height and <paramref name="style"/> carry refusals of their own; see
    /// <see cref="RunningBand.Height"/>, <see cref="TextStyle.FontSize"/>,
    /// <see cref="TextStyle.Leading"/> and <see cref="TextStyle.FontRef"/>.
    /// <see cref="HorizontalAlignment.Justify"/> and values the enumeration does not name are drawn
    /// left-aligned; see <see cref="RunningBand.Alignment"/>.</para>
    /// <para>A null <paramref name="style"/> is <see cref="TextStyle.Default"/>, not the style set
    /// with <see cref="SetDefaultFont"/>. The font is then Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> is <see langword="null"/>.
    /// </exception>
    /// <inheritdoc cref="Header" path="/exception[@cref='T:System.ArgumentException' or @cref='T:System.InvalidOperationException' or @cref='T:System.IndexOutOfRangeException']"/>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
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
    /// <see cref="NotSupportedException"/> when the <c>cmap</c> has no Unicode subtable this
    /// library reads: format 0, 4 or 6, or format 12 on platform 3 / encoding 10 or on platform 0
    /// / encoding 4, or on platform 0 / encoding 10 beside a 16-bit subtable.
    /// <para><b>A font whose only readable subtable is format 12 is held to more.</b> This call
    /// also refuses it, with <see cref="NotSupportedException"/> naming the reason, when that
    /// subtable is malformed (a group that is inverted, unsorted or past U+10FFFF, a glyph ID at or
    /// beyond <c>numGlyphs</c>, or a group count the declared length cannot hold); when two
    /// encoding records share a platform, encoding and language, or the records are not sorted by
    /// them; when a subtable outside the Macintosh platform has a non-zero language, or a format 8,
    /// 10, 12 or 13 subtable a non-zero reserved field; when the only format 12 record is platform
    /// 0 / encoding 10; when any table in the list above is missing or truncated; when
    /// <c>head</c>'s <c>unitsPerEm</c> is 0; when a glyph's <c>loca</c> range lies outside
    /// <c>glyf</c>; when a composite glyph names a component at or past <c>numGlyphs</c> or the
    /// composite glyphs form a cycle; or when the <c>CFF </c> table does not parse. It does not
    /// check glyph or charstring contents, checksums, or any other value in <c>head</c>,
    /// <c>name</c>, <c>OS/2</c> and <c>post</c>.</para>
    /// <para>For every other font, this call does not check every value in those tables, and it
    /// does not read the outlines. Both are used at save, for every font registered here whether or
    /// not text uses it, so a font this call accepts can still make the save throw:
    /// <see cref="InvalidOperationException"/> for a
    /// missing outline table, <see cref="InvalidDataException"/> for malformed outline data, and
    /// <see cref="ArgumentException"/> for a value that makes a font metric non-finite, such as a
    /// <c>unitsPerEm</c> of 0. An OpenType font with CFF outlines is accepted.</para>
    /// <para>A variable font (one with an <c>fvar</c> table) is accepted. When its outlines are
    /// glyf, or CFF that this library can subset, it is drawn as its default instance: the embedded
    /// program leaves out its variation tables, so no other instance can be drawn. A CFF font that
    /// cannot be subset (CID-keyed, or with a <c>CFF </c> table that does not parse) is embedded
    /// whole and keeps its variation tables. A variable font whose outlines are in a <c>CFF2</c>
    /// table has no <c>CFF </c> table: this call accepts it, and
    /// <see cref="Save(System.IO.Stream)"/> and the other save overloads throw
    /// <see cref="InvalidOperationException"/> with the message "Font is missing required table
    /// 'CFF '.". <b>Do not pass</b> a CFF2 font and expect it to embed.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="fontData"/> is <see langword="null"/>. <c>ParamName</c> is <c>source</c>.
    /// </exception>
    /// <exception cref="InvalidDataException">
    /// <paramref name="fontData"/> is malformed or truncated. Malformed outline data is reported
    /// from the save instead.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// The font's <c>cmap</c> table has no Unicode subtable this library reads (format 0, 4 or 6,
    /// or format 12 on platform 3 / encoding 10 or platform 0 / encoding 4, or on platform 0 /
    /// encoding 10 beside a 16-bit subtable), or the font's only such subtable is format 12 and the
    /// font breaks a rule the remarks list.
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
    /// The font has no readable Unicode <c>cmap</c> subtable, or its only one is a malformed
    /// format 12; see <see cref="UseTrueTypeFont"/>.
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
    /// <inheritdoc cref="LoadTrueTypeFont" path="/exception[@cref='T:System.ArgumentNullException' or @cref='T:System.IO.FileNotFoundException' or @cref='T:System.IO.DirectoryNotFoundException' or @cref='T:System.UnauthorizedAccessException' or @cref='T:System.IO.IOException' or @cref='T:System.IO.InvalidDataException' or @cref='T:System.InvalidOperationException' or @cref='T:System.NotSupportedException']"/>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds a null character, or, on Windows, holds only spaces.
    /// Other characters the file system refuses raise <see cref="IOException"/>.
    /// <see cref="Save(System.IO.Stream)"/> and the other save overloads also raise
    /// <see cref="ArgumentException"/>, not the returned task, for a value in the font that makes a
    /// font metric non-finite; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was already cancelled when this method was called, or is
    /// cancelled while the file is being read.
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
    /// A null separator is refused here.
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
    /// A null table is refused here. A table's own
    /// refusals are on <see cref="TableElement"/>, among them a table that needs more than 50,000
    /// page continuations. A row taller than a page is cut between its lines and continues on the
    /// next page. Repeated header rows that do not fit a page, a row held in a
    /// <see cref="Cell.RowSpan"/> group, and a row with a cell that cannot hold one line are
    /// refused instead, and <see cref="TableElement"/> says which.
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
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>. Also raised from the
    /// save, with a message that ends in the rows or the cell it names, when a row cannot be placed
    /// on a page and cannot be cut; see <see cref="TableElement"/>.
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
    /// A null image is refused here.
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
    /// A null chart is refused here.
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
    /// A null list is refused here.
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

    /// <summary>
    /// Adds a block of text whose spaces, tabs and line ends are kept. Returns this document for
    /// chaining.
    /// </summary>
    /// <remarks>
    /// <b>A null block is refused.</b> It throws <see cref="ArgumentNullException"/> from this
    /// call. What the block holds was checked when it was built, by <see cref="Preformatted"/>; the
    /// save refuses what depends on the document: the content box, and the font and the characters
    /// under a <see cref="Conformance"/> claim. A refusal of that kind is raised before any layout,
    /// so the document stays saveable once you have changed what the message names. The block is
    /// listed in <see cref="Diagnostics"/> as <see cref="LayoutElementKind.Preformatted"/> when a
    /// line is wrapped.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="preformatted"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, before any layout, when the content box the margins leave is not wider than zero or is
    /// shorter than one line, and, under a <see cref="Conformance"/> other than
    /// <see cref="PdfConformance.None"/>, when the style's font is not embedded or the embedded
    /// font has no glyph for a character of the text. Under PDF/A-2a, PDF/A-2u and PDF/UA-1 a
    /// U+FEFF or U+FFFE is refused as well. It is also raised when the block needs more than
    /// 50,000 page continuations; see <see cref="Add(IRenderer)"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from the save, not from this call, when the style's embedded font came from another
    /// document that the saving document does not define a font under the handle's resource name
    /// for; see <see cref="FontReference(EmbeddedFontHandle)"/>.
    /// </exception>
    public Document Add(Preformatted preformatted)
    {
        ArgumentNullException.ThrowIfNull(preformatted);
        _content.Add(new PreformattedRenderer(preformatted));
        return this;
    }

    /// <summary>
    /// Adds a page break: the next element starts on a new page, or on the next right-hand page
    /// when <see cref="PageBreak.Recto"/> is set. Returns this document for chaining.
    /// </summary>
    /// <remarks>
    /// <b>A null break is refused.</b> It throws <see cref="ArgumentNullException"/> from this
    /// call. A break holding a <see cref="PageBreak.PageLabel"/> that no element follows is refused
    /// by the save, before any layout; no other break is refused later. A break adds no page when it
    /// is first, follows another break, or ends a page with nothing laid out on it, and a break
    /// with nothing after it is dropped. A document of breaks alone has no content, so it takes the same path as a
    /// document with nothing added: a save throws <see cref="InvalidOperationException"/> saying it
    /// has no pages, before the destination is checked, and leaves the document usable. See
    /// <see cref="PageBreak"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="pageBreak"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(PageBreak pageBreak)
    {
        ArgumentNullException.ThrowIfNull(pageBreak);
        _content.Add(new PageBreakRenderer(pageBreak));
        return this;
    }

    /// <summary>Adds a paragraph built from the given text, using the supplied style or the default style. Returns this document for chaining.</summary>
    /// <remarks>
    /// With <paramref name="style"/> null, the paragraph takes the style from the most recent
    /// <see cref="SetDefaultFont"/> call made before this one.
    /// A null <paramref name="text"/> is refused here.
    /// <para>When no style was set with <see cref="SetDefaultFont"/> and <paramref name="style"/>
    /// is null, the paragraph takes <see cref="TextStyle.Default"/>. The font is then Helvetica,
    /// which is not embedded. While <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses text the font cannot draw, and a Standard 14 font, under
    /// either claim.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <inheritdoc cref="Paragraph.Add(string, TextStyle)" path="/exception[@cref='T:System.ArgumentException']"/>
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
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the paragraph
    /// is written as glyph 0, or the document has a conformance claim and the paragraph writes
    /// Helvetica or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
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
    /// <para>A renderer you wrote yourself cannot ask for a page break. The library's own
    /// <see cref="ParagraphRenderer"/>, <see cref="TableRenderer"/>, <see cref="ListRenderer"/> and
    /// <see cref="LayoutImageRenderer"/> honour their element's <c>BreakBefore</c> when added here.
    /// Add a <see cref="PageBreak"/> before a custom renderer instead.</para>
    /// <para>Those four renderers also report their element's <c>Anchor</c>, so the save writes its
    /// named destination as if the element had been added directly. A custom renderer has no
    /// anchor, and cannot start a page-label range: put a <see cref="PageBreak"/> with a
    /// <see cref="PageBreak.PageLabel"/> before it.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="renderer"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when this renderer, or an overflow it returns, needs more than 50,000 page
    /// continuations, or when it returns <see cref="LayoutResult.Outcome.Nothing"/> twice in a row,
    /// the second time on a new page. It is also raised when the anchor of one of the four renderers
    /// above is held by another element too, before any layout, and when a
    /// <see cref="ParagraphRenderer"/> built to continue its paragraph from a later line holds an
    /// anchor, because that piece never draws the first line; see <see cref="Paragraph.Anchor"/>.
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
    /// <para>The identifier and <paramref name="info"/> are written as PDF text strings (ISO 32000-2,
    /// Table 401). A value made only of U+0020 to U+007E, U+00A1 to U+00FF except U+00AD, and
    /// U+0000 to U+0017 is written one byte per character, unless it begins with U+00FE U+00FF or
    /// U+00EF U+00BB U+00BF; any other value is written as UTF-16BE with a byte order mark and keeps
    /// its characters.
    /// Table 401 describes the identifier as a text string identifying the output device or
    /// production condition, and gives the name of a condition in an industry-standard registry as
    /// a typical value, so a character outside ASCII is unusual there; it is not refused.</para>
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
    /// <see cref="PdfConformance.None"/>. The identifier is written as a text string,
    /// as on <see cref="SetPdfAOutputIntent"/>.
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

    // Set when a save threw after its layout had started. The pages the layout appended and the
    // line breaks each renderer cached at the first width cannot be taken back, so a second layout
    // would stack pages on the first or reuse stale wraps (#530). A save that failed before its
    // layout started leaves the document as it was and stays usable.
    private bool _layoutFailed;

    private void ThrowIfLayoutFailed()
    {
        if (_layoutFailed)
        {
            throw new InvalidOperationException(
                "This document cannot be saved again: an earlier save stopped after its layout had " +
                "started, and the pages or line breaks it produced cannot be taken back. " +
                "Create a new Document and add the content again.");
        }
    }

    /// <summary>
    /// Everything a save can refuse that does not depend on the layout, run before the layout starts.
    /// Layout appends pages, so a refusal that came after it left them behind and the next save
    /// appended a second layout. Running these first keeps each refusal recoverable: correct
    /// the cause and save again.
    /// </summary>
    private void BeginSave(Stream destination, bool asynchronous = false)
    {
        // An empty document lays out no pages, so its page count is already known. The page
        // geometry and the bands are still checked, then "no pages", before the destination and
        // the options.
        var empty = HasNoContent;
        if (empty) CreateRenderer().RunLayout();
        _pdf.EnsureCanSave(destination, pagesKnown: empty, asynchronous: asynchronous);
        ThrowIfLayoutFailed();
        _pdf.ValidateSaveOptions();
    }

    // ── Saving to a path ─────────────────────────────────────────────────────

    // A save to a path opens the target before it lays anything out and writes it last (#508), so a
    // layout failure does not leave a truncated file in place of whatever the path held.
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
    /// resolves a relative link target against the link's own directory, as the open always did.
    /// Whether the path is a directory, read-only or otherwise unwritable is also for the
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

    /// <summary>
    /// Windows refuses an existing Hidden or System file, as opening it with <c>FileMode.Create</c>
    /// does; <c>OpenOrCreate</c> would open it, so the refusal is made here and
    /// <see cref="UnauthorizedAccessException"/> is thrown. Through a symbolic link the open judges the
    /// final target, not the link, so the attributes read here are the target's. A chain that
    /// <see cref="FileSystemInfo.ResolveLinkTarget"/> cannot resolve is judged by the link's own
    /// attributes. Windows only: on a Unix file system <see cref="FileAttributes.Hidden"/> follows from a
    /// leading dot in the name, and a dot file is an ordinary target there.
    /// </summary>
    private static void RefuseHiddenOrSystemFile(string target)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(target)) return;

        // File.GetAttributes reports a link's own attributes, which the open never looks at.
        FileSystemInfo judged = new FileInfo(target);
        if (judged.LinkTarget is not null)
        {
            try
            {
                judged = judged.ResolveLinkTarget(returnFinalTarget: true) ?? judged;
            }
            catch (IOException)
            {
                // ResolveLinkTarget resolves a relative link's ".." textually, so it throws for a ".."
                // below a symlinked directory, which the operating system resolves differently and
                // the open follows (SaveThroughARelativeLinkWithDotDotBelowASymlinkedDirectory...).
                // For that shape the link's own attributes are all this check has, as before.
            }
        }

        if (judged.Exists && (judged.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
            throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
    }

    /// <summary>
    /// The chunk size of the buffer a path save builds the PDF in; a test lowers it to copy in several
    /// pieces.
    /// </summary>
    internal int SaveBufferChunkSize { get; set; } = ChunkedBufferStream.DefaultChunkSize;

    /// <summary>
    /// Called with the finished buffer once the target is emptied and before the copy starts, so a test
    /// can look at the buffer or cancel a token at the one point where the copy is the next thing to
    /// look at it. Null outside the tests; it belongs to this instance, not to the process.
    /// </summary>
    internal Action<ChunkedBufferStream>? BeforeSaveCopy { get; set; }

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

    // A page break is not content: a document of breaks alone lays out no page, so it takes the
    // empty-document path, which reports "no pages" before the layout and leaves the document
    // usable.
    private bool HasNoContent
    {
        get
        {
            foreach (var renderer in _content)
                if (renderer is not PageBreakRenderer)
                    return false;
            return true;
        }
    }

    internal DocumentRenderer CreateRenderer()
    {
        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
            MirrorMargins = MirrorMargins,
            DocumentPageLabel = PageLabel,
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
            ThrowIfStrictRefused(renderer);
        }
        catch
        {
            MarkFailedIfLaidOut(pagesBefore, renderer);
            throw;
        }
    }

    /// <summary>
    /// Runs the layout and then the step that follows it, and treats a failure of either the same
    /// way: any exception, a cancellation included, once the layout had added pages leaves the
    /// document unable to be saved again.
    /// </summary>
    private void LayOutThen(DocumentRenderer renderer, Action afterLayout)
    {
        var pagesBefore = _pdf.Pages.Count;
        try
        {
            renderer.RunLayout();
            ThrowIfStrictRefused(renderer);
            afterLayout();
        }
        catch
        {
            MarkFailedIfLaidOut(pagesBefore, renderer);
            throw;
        }
    }

    // Runs inside the try of both layout helpers, right after the layout returns, so every save
    // path shares it and the failure is marked like any other layout failure. Everything that
    // writes follows it, and a non-finite value the canvas refused was already rethrown by
    // RunLayout. A refusal that only the write raises is pre-empted by this one when the switch is
    // on.
    private void ThrowIfStrictRefused(DocumentRenderer renderer)
    {
        if (!StrictDiagnostics || !renderer.Sink.HasErrors) return;

        var items = renderer.Diagnostics;
        _diagnostics = items;

        LayoutDiagnostic? first = null;
        var errors = 0;
        foreach (var item in items)
        {
            if (item.Severity != LayoutDiagnosticSeverity.Error) continue;
            first ??= item;
            errors++;
        }

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        if (first is null)
            throw new InvalidOperationException(string.Create(
                inv,
                $"StrictDiagnostics refused the save: {renderer.Sink.ErrorReports} error report(s) were dropped past the channel's limit of {LayoutDiagnosticSink.MaxItems} items."));

        var text = new System.Text.StringBuilder("StrictDiagnostics refused the save: ");
        text.Append(first.Code);
        if (first.Clause is not null) text.Append(" (").Append(first.Clause).Append(')');
        text.Append(" in ").Append(first.ElementKind);
        if (first.ElementIndex is { } index) text.Append(' ').Append(index.ToString(inv));
        if (first.PageNumber is { } page) text.Append(", page ").Append(page.ToString(inv));
        text.Append(": ").Append(first.Message);
        if (errors > 1)
            text.Append(' ').Append((errors - 1).ToString(inv)).Append(" further error item(s).");
        throw new InvalidOperationException(text.ToString());
    }

    // A renderer caches its wrap at the first width it is laid out at (ParagraphRenderer._lines,
    // ListRenderer._items), so a document whose layout started cannot be laid out again even when
    // no page was added yet, as when the counting pass throws: a retry after a PageSize or margin
    // change would reuse the wrap measured for the old width. HasStartedLayout covers that case.
    private void MarkFailedIfLaidOut(int pagesBefore, DocumentRenderer renderer)
    {
        if (_pdf.Pages.Count != pagesBefore || renderer.HasStartedLayout) _layoutFailed = true;
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

        _diagnostics = renderer.Diagnostics;
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
    /// <para>What a save that threw leaves behind depends on where it threw. A refusal that
    /// does not depend on the layout comes first, before anything is laid out: a null or unwritable
    /// <paramref name="destination"/>, a document already written, <see cref="UseObjectStreams"/>
    /// together with <see cref="Encrypt"/>, a PDF/A <see cref="Conformance"/> together with
    /// <see cref="Encrypt"/>, and PDF/UA-1 with settings that omit
    /// <see cref="PdfPermissions.Extract"/>. In a document with nothing added, or only page breaks,
    /// the page geometry and bands are checked before these. The document is untouched, so correct
    /// the cause and save again. A refused page geometry, such as margins that meet the page, is
    /// raised before any
    /// page is added and is just as recoverable. A save that threw <b>after</b> its layout had
    /// added pages cannot be undone, whether the layout itself threw, such as for an element too
    /// tall for the page, or a later step did, such as building the PDF or a cancellation seen once
    /// the layout has run. So does a save whose layout started and threw before it added a page,
    /// which a counting pass does when a <see cref="Header"/> or <see cref="Footer"/> is set,
    /// because the elements keep the line breaks they measured at the old page width and a retry
    /// after changing <see cref="PageSize"/> would reuse them. Every later save after one of these
    /// then throws <see cref="InvalidOperationException"/> telling you to create a new
    /// <see cref="Document"/>. </para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>. This is checked before the layout
    /// runs, except in a document with nothing added, or only page breaks, where the page geometry
    /// and bands are checked first. The document is untouched, so it can be saved again; see the
    /// remarks.
    /// It is also raised, from the layout and not from the call that stored the null, for a null an
    /// element holds that is refused where it is used: a <see cref="TextRun"/> style that yields a
    /// word (<c>ParamName</c> <c>Style</c>), a <see cref="Cell"/> text in a table with an
    /// automatically sized column (<c>content</c>), a <see cref="ListItem"/> null child held by a
    /// top-level item (<c>child</c>), a <see cref="ListItem"/> null text on a top-level item or on a
    /// child of one (<c>text</c>), and a <see cref="RunningBand"/>
    /// template that is drawn (<c>template</c>). A null value in a <see cref="PdfDictionary"/>, or a
    /// null child of a <see cref="PdfStructElem"/> in a tagged document, is refused when the PDF is
    /// written, with <c>ParamName</c> <c>value</c> or <c>child</c>; see
    /// <see cref="PdfDocument.Save(System.IO.Stream)"/>. The layout had started, so the document
    /// cannot be saved again.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also catches it.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>; see
    /// <see cref="Conformance"/>. It is also raised when two elements hold the same
    /// <c>Anchor</c> or a <see cref="PageBreak"/> holds a <see cref="PageBreak.PageLabel"/> with no
    /// element after it, both before any layout, so the document stays saveable; and when an anchor's
    /// element was never drawn, after the layout, so the document cannot be saved again. It is also
    /// raised when a font registered with
    /// <see cref="UseTrueTypeFont"/> lacks a table the save needs, and by every save after one whose
    /// save threw once its layout had started. For the element inputs, the boundary
    /// documentation on the property you set says which values reach this. It is also raised when
    /// <see cref="StrictDiagnostics"/> is <see langword="true"/> and the layout reported an item
    /// whose <see cref="LayoutDiagnostic.Severity"/> is
    /// <see cref="LayoutDiagnosticSeverity.Error"/>, or dropped an error report past the channel's
    /// limit; nothing is written and the document cannot be saved again.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Many unrelated conditions share this type, so <b>do not</b> switch on the parameter name to
    /// tell them apart. A non-writable <paramref name="destination"/> is refused before the layout
    /// runs and reports <c>destination</c>; a document with nothing added, or only page breaks,
    /// throws "The document has no pages." first. The margin check reports <c>margins</c>. A
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
    /// <see cref="IRenderer.Layout"/>. An element, text or item passed to an
    /// <c>Add</c> overload is refused where it is passed instead.
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
    /// bytes copied in. A lock or a permission error therefore surfaces while the document is
    /// still usable, and a save that fails at layout or at building the PDF leaves
    /// whatever the path held exactly as it was, byte for byte, and creates nothing at a path that
    /// did not exist.</para>
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
    /// <para><b>Attention</b>: the whole PDF is built in memory before any of it is written, so the
    /// finished PDF is held in memory once more, in chunks of up to 1 MiB, until it is copied to the file.
    /// There is no
    /// size limit beyond the memory available: the buffer is a list of chunks, not one array. To avoid
    /// the extra copy, open a <see cref="FileStream"/> yourself and pass it to
    /// <see cref="Save(System.IO.Stream)"/>, which writes to it as it goes. The failed-save guarantee
    /// above does not hold for that stream, which was truncated when you opened it.
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/> builds the whole
    /// PDF in a <see cref="MemoryStream"/> instead, whose capacity is about 2 GB.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is <see langword="null"/>. A null that an element or a written Kernel
    /// object holds is refused from the layout or the write, as it is by
    /// <see cref="Save(System.IO.Stream)"/>; see there for the shapes and the parameter names.
    /// </exception>
    /// <inheritdoc cref="Save(System.IO.Stream)" path="/exception[@cref='T:System.ObjectDisposedException' or @cref='T:System.ArgumentOutOfRangeException' or @cref='T:System.NotSupportedException' or @cref='T:System.NullReferenceException' or @cref='T:System.IndexOutOfRangeException' or @cref='T:System.IO.InvalidDataException' or @cref='T:System.OverflowException' or @cref='T:System.OutOfMemoryException']"/>
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
    /// <paramref name="path"/> is read-only, or, on Windows, hidden or a system file, or you may not
    /// write to that location. Through a symbolic link, the file the link resolves to decides
    /// whether it is hidden or a system file, unless the chain cannot be resolved, when the link's
    /// own attributes do.
    /// </exception>
    /// <exception cref="IOException">
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, which is
    /// found before the layout runs, or <paramref name="path"/> is otherwise invalid for the file
    /// system, being over-long or holding a character the file system refuses, such as
    /// <c>&lt;</c> or <c>|</c>. It is also raised when writing the file fails.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>, or a registered
    /// font lacks a table the save needs, or <see cref="StrictDiagnostics"/> is
    /// <see langword="true"/> and the layout reported an error item. None of these touches the
    /// file at the path; see the remarks.
    /// </exception>
    public void Save(string path)
    {
        var target = ResolveSaveTarget(path);
        var existed = SomethingExistsAt(target);

        // Opened before the layout, so a lock or a permission error surfaces while the document is
        // still usable, and every check BeginSave runs sees the stream the PDF will go to. Opening
        // does not truncate; the file is emptied only once the whole PDF is in memory.
        RefuseHiddenOrSystemFile(target);
        var fs = new FileStream(target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        var succeeded = false;
        DocumentRenderer renderer;
        try
        {
            try
            {
                BeginSave(fs);
                renderer = CreateRenderer();
                var buffer = new ChunkedBufferStream(SaveBufferChunkSize);
                LayOutThen(renderer, () => _pdf.Save(buffer));

                EmptyTarget(fs);

                BeforeSaveCopy?.Invoke(buffer);
                buffer.CopyContentsTo(fs);
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
    /// <para>What a save that threw leaves behind depends on where it threw. A refusal that
    /// does not depend on the layout comes first, before anything is laid out: a null or unwritable
    /// <paramref name="destination"/>, a document already written, <see cref="UseObjectStreams"/>
    /// together with <see cref="Encrypt"/>, a PDF/A <see cref="Conformance"/> together with
    /// <see cref="Encrypt"/>, and PDF/UA-1 with settings that omit
    /// <see cref="PdfPermissions.Extract"/>. In a document with nothing added, or only page breaks,
    /// the page geometry and bands are checked before these. The document is untouched, so correct
    /// the cause and save again. A refused page geometry, such as margins that meet the page, is
    /// raised before any
    /// page is added and is just as recoverable. A save that threw <b>after</b> its layout had
    /// added pages cannot be undone, whether the layout itself threw, such as for an element too
    /// tall for the page, or a later step did, such as building the PDF or a cancellation seen once
    /// the layout has run. So does a save whose layout started and threw before it added a page,
    /// which a counting pass does when a <see cref="Header"/> or <see cref="Footer"/> is set,
    /// because the elements keep the line breaks they measured at the old page width and a retry
    /// after changing <see cref="PageSize"/> would reuse them. Every later save after one of these
    /// then throws <see cref="InvalidOperationException"/> telling you to create a new
    /// <see cref="Document"/>. </para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <inheritdoc cref="Save(System.IO.Stream)" path="/exception[@cref='T:System.ArgumentNullException' or @cref='T:System.InvalidOperationException' or @cref='T:System.IO.IOException' or @cref='T:System.ArgumentOutOfRangeException' or @cref='T:System.NullReferenceException' or @cref='T:System.IndexOutOfRangeException' or @cref='T:System.IO.InvalidDataException' or @cref='T:System.OverflowException' or @cref='T:System.OutOfMemoryException']"/>
    /// <exception cref="NotSupportedException">
    /// <paramref name="destination"/> cannot be written to. That is refused before the layout runs,
    /// so the document is untouched; a document with nothing added, or only page breaks, throws
    /// "The document has no pages." first. It is also raised when <see cref="UseObjectStreams"/> is
    /// true and <see cref="Encrypt"/> has been called.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// <paramref name="destination"/> was already closed, which is refused before the layout runs
    /// (a document with nothing added, or only page breaks, throws "The document has no pages."
    /// first), or this
    /// <see cref="Document"/> was already disposed.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The margins, header and footer leave
    /// the content area no positive size, or an element refuses its own input while being laid
    /// out. The boundary documentation on the individual
    /// properties says which inputs those are. A registered font with a value that makes a font
    /// metric non-finite, such as a <c>unitsPerEm</c> of 0, raises it too; see
    /// <see cref="UseTrueTypeFont"/>. So does text that holds an unpaired surrogate and is measured
    /// in an embedded font, and a non-finite position the save writes outside the content stream;
    /// see <see cref="Margins"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// A token that is already cancelled when the call starts ends the task with a
    /// <see cref="TaskCanceledException"/> whatever else is wrong, a null, closed or non-writable
    /// <paramref name="destination"/> included. The token is then looked at when the layout is queued,
    /// once it has finished, and once serialisation has finished; a cancellation seen at one of those
    /// points ends the task the same way before anything is written. Only a failure inside the layout,
    /// such as an element that refuses its input, or, with <see cref="StrictDiagnostics"/> set, a
    /// strict refusal, is reported ahead of a cancellation seen after the layout starts. During the
    /// write, <paramref name="destination"/> receives the token and
    /// decides what to raise.
    /// </exception>
    // RS0026 flags multiple overloads with optional parameters as a future-ambiguity risk;
    // Stream and string share no implicit conversion, so overload resolution can never be
    // ambiguous between these two.
#pragma warning disable RS0026
    public async Task SaveAsync(Stream destination, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        // Before every other check, so an already cancelled token wins over a null or closed destination.
        ChunkedBufferStream.ThrowIfCanceled(cancellationToken);
        BeginSave(destination, asynchronous: true);
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
            MarkFailedIfLaidOut(pagesBefore, renderer);
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
    /// <see cref="Save(string)"/>. A save that fails at layout or at building the PDF, or that
    /// is cancelled before the copy starts, therefore leaves whatever the path held exactly as it
    /// was, byte for byte, and creates nothing at a path that did not exist. Only a cancellation
    /// during the copy can leave the file truncated. Everything <see cref="Save(string)"/> says
    /// about symbolic links, permissions, the advisory lock, the extra in-memory copy of the PDF, which
    /// has no 2 GB limit, and the final copy not being atomic applies here too.</para>
    /// </remarks>
    /// <inheritdoc cref="Save(string)" path="/exception[@cref='T:System.ArgumentNullException' or @cref='T:System.IO.DirectoryNotFoundException' or @cref='T:System.UnauthorizedAccessException' or @cref='T:System.IO.IOException' or @cref='T:System.InvalidOperationException']"/>
    /// <inheritdoc cref="Save(System.IO.Stream)" path="/exception[@cref='T:System.ObjectDisposedException' or @cref='T:System.ArgumentOutOfRangeException' or @cref='T:System.NotSupportedException' or @cref='T:System.NullReferenceException' or @cref='T:System.IndexOutOfRangeException' or @cref='T:System.IO.InvalidDataException' or @cref='T:System.OverflowException' or @cref='T:System.OutOfMemoryException']"/>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds a null character, or, on Windows, holds only spaces.
    /// It is raised while the file is opened. The layout and writing causes listed on
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/> reach here
    /// too, once it is open. These are unrelated conditions that happen to share a type, so do
    /// <b>not</b> tell them apart by parameter name.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// A cancellation seen at any point where the token is looked at throws
    /// <see cref="TaskCanceledException"/>: before the layout starts, once the file is open; once the
    /// layout has finished, when the serialisation is queued; once the PDF is built, before the file is
    /// emptied; and in the copy into the file, before each chunk. The copy passes the token to each
    /// write into the file as well, which decides what that write raises. A cancellation seen at one of the
    /// first three leaves the file that was there untouched; see the remarks. A failure that comes
    /// first, such as a refused page geometry, an exception from an element or a strict refusal, is
    /// reported instead. While the PDF is being built the token is not looked at. The copy is the
    /// one point after the file was emptied, so a cancellation there leaves it truncated or
    /// incomplete; one seen after the last chunk was written is not reported. A cancellation after
    /// the layout has run also means this <see cref="Document"/> cannot be saved again; see
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>.
    /// </exception>
#pragma warning disable RS0026
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        var target = ResolveSaveTarget(path);
        var existed = SomethingExistsAt(target);

        // See Save(string): opened before the layout, emptied only once the PDF is in memory.
        RefuseHiddenOrSystemFile(target);
        var fs = new FileStream(
            target, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var succeeded = false;
        DocumentRenderer renderer;
        try
        {
            try
            {
                // After the open, which reports a bad path or a lock, and before the other
                // checks, so a token that is already cancelled wins over those.
                ChunkedBufferStream.ThrowIfCanceled(cancellationToken);
                BeginSave(fs);
                renderer = CreateRenderer();
                var buffer = new ChunkedBufferStream(SaveBufferChunkSize);
                var pagesBefore = _pdf.Pages.Count;
                await Task.Run(() => LayOut(renderer), cancellationToken).ConfigureAwait(false);
                try
                {
                    // The last point where the token is looked at, when the serialisation is queued.
                    // Past it the PDF is built and copied to the end, since stopping halfway would
                    // leave a truncated file over a good one. A cancelled queue throws
                    // TaskCanceledException, as the point before the layout does.
                    await Task.Run(() => _pdf.Save(buffer), cancellationToken).ConfigureAwait(false);

                    // The PDF is built and the file is not yet emptied: the last point where stopping
                    // costs nothing.
                    ChunkedBufferStream.ThrowIfCanceled(cancellationToken);
                }
                catch
                {
                    MarkFailedIfLaidOut(pagesBefore, renderer);
                    throw;
                }

                EmptyTarget(fs);

                BeforeSaveCopy?.Invoke(buffer);
                await buffer.CopyContentsToAsync(fs, cancellationToken).ConfigureAwait(false);
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
        _pdf.EnsureCanPrepareForSigning(options, pagesKnown: HasNoContent);
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
    /// untouched when it throws that. Every other member keeps working.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// Raised from a later save or <see cref="Encrypt"/>, not from this call.
    /// </exception>
    public void Dispose() => _pdf.Dispose();
}
