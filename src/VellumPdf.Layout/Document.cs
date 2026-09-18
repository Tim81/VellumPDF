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
    /// A save before an element is added throws <see cref="InvalidOperationException"/>, once the
    /// checks that come before it have passed; see <see cref="Save(System.IO.Stream)"/>.
    /// </remarks>
    public Document() { }

    private readonly PdfDocument _pdf = new();
    private readonly List<IRenderer> _content = [];
    private TextStyle _defaultStyle = TextStyle.Default;
    private readonly List<TextEncodingWarning> _textEncodingWarnings = [];

    private readonly List<BandTruncationWarning> _bandTruncations = [];

    /// <summary>
    /// Characters written through a Standard-14 text element that WinAnsiEncoding could not
    /// represent (each was substituted with '?' in the saved PDF). Populated by <see cref="Save(Stream)"/>
    /// and the signing-prep path; empty when every character rendered is in WinAnsi.
    /// </summary>
    /// <remarks>
    /// Empty until a save (or signing prep) has run, and replaced by each one that succeeds.
    /// </remarks>
    public IReadOnlyList<TextEncodingWarning> TextEncodingWarnings => _textEncodingWarnings;

    /// <summary>
    /// Running bands whose text was wider than the content box and was cut to fit, from the last
    /// save. At most one report per band, each naming the page that lost the most. Empty when both
    /// bands fitted, or when no band was set.
    /// </summary>
    /// <remarks>
    /// Empty until a save has run, and replaced by each one that succeeds. A cut band is not an
    /// error, and this list is the only report of it. See <see cref="RunningBand.Template"/>.
    /// </remarks>
    public IReadOnlyList<BandTruncationWarning> BandTruncations => _bandTruncations;

    /// <summary>Document metadata (title, author, subject, keywords, etc.).</summary>
    /// <remarks>
    /// Stored as given. No field on this dictionary is validated by Layout.
    /// </remarks>
    public PdfDocumentInfo Info => _pdf.Info;

    /// <summary>The default page size used for newly created pages.</summary>
    /// <remarks>
    /// Validated at save, not here. A null value is accepted here, and the save throws
    /// <see cref="NullReferenceException"/>. A width or height that is not a positive finite number
    /// is refused with <see cref="ArgumentOutOfRangeException"/> naming the axis and the value, and
    /// a page narrower or shorter than <see cref="Margins"/> is refused with
    /// <see cref="ArgumentException"/>.
    /// <para>Do not pass null. A later major version will refuse it.</para>
    /// <para>Setting this after adding elements applies to what you have already
    /// added. The layout runs at save, so the size in force then is the size the whole document
    /// is laid out at, not only the part added after you set it.</para>
    /// <para>On a document no save has been attempted on, that holds byte for byte. Resizing before
    /// <see cref="Save(System.IO.Stream)"/> produces a file matching one built at the new size from
    /// the start, except the random <c>/ID</c> and the XMP <c>CreateDate</c>/<c>ModifyDate</c>
    /// timestamps, which carry the time each build actually ran. Measured by resizing a document
    /// from 600 by 800 to 200 by 120 at <b>10pt</b> margins and normalising those three fields: the
    /// bytes match a build at 200 by 120 throughout, and every <c>/MediaBox</c> carries the new
    /// size. The margin is part of the measurement, not an aside: the default 72pt insets on
    /// <see cref="Margins"/> do not fit a 120pt page, so that combination is refused before either
    /// file is built. A save that already threw breaks that equivalence, along with the rest of the
    /// document's state; see <see cref="Save(System.IO.Stream)"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from a save rather than from this property, when the width or height is zero,
    /// negative or not finite.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from a save rather than from this property, when the value is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when the margins on either axis meet or exceed this page size.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when the content area this size and <see cref="Margins"/> compute to
    /// is positive but still too small for a single element to fit on one page. A document that
    /// merely runs to several pages does not reach it. One other route reaches the same type: the
    /// page-continuation cap, which fires when a single element needs more than 50,000 page
    /// continuations. How many an element needs is itself a function of this size.
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
    /// <para>Do not pass one. A later major version will refuse it.</para>
    /// <para>Combined with <see cref="Encrypt"/>, the save throws
    /// <see cref="InvalidOperationException"/> for every PDF/A level, because ISO 19005-2 §6.1.3
    /// forbids the <c>Encrypt</c> key in a PDF/A file. For PDF/UA-1 it throws only when the
    /// encryption settings leave out <see cref="PdfPermissions.Extract"/>, because ISO 14289-1,
    /// 7.16, requires the permission that allows extraction for accessibility.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when
    /// <see cref="Encrypt"/> has been called and this is a PDF/A level, or PDF/UA-1 without
    /// <see cref="PdfPermissions.Extract"/>.
    /// </exception>
    public PdfConformance Conformance
    {
        get => _pdf.Conformance;
        set => _pdf.Conformance = value;
    }

    /// <summary>
    /// When true, paragraphs and headings are wrapped in marked-content sequences
    /// and a /StructTreeRoot is written. Default is false.
    /// Forwarded to the underlying <see cref="PdfDocument"/>.
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
    /// written as given. PDF/A-2a and PDF/UA-1 both require a well-formed tag, and this property
    /// does not check one.
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
    /// <para><b>Attention</b>: a negative inset is not refused. The document saves and the content is
    /// placed outside the page's boundaries, where a reader clips it. On a one-paragraph document
    /// with every inset at -72, the file is written with no invalid token in it, so nothing
    /// downstream reports the loss either. A later major version will reject it.</para>
    /// <para>The three non-finite forms fail three different ways, and only one names its own
    /// cause. Positive infinity reaches the margin check and throws
    /// <see cref="ArgumentException"/> naming the margin. <c>NaN</c> and negative infinity both
    /// slip past that check, because a comparison against either is false, and surface as
    /// <see cref="InvalidOperationException"/> instead. One type for one cause and another for
    /// two, so you cannot catch all three together.</para>
    /// <para><c>NaN</c> surfaces as an element being too tall to fit, and negative infinity as
    /// the page-continuation cap. Neither message is what actually happened. The same wrong-cause
    /// messages were corrected elsewhere (#481); here they are still open (#502).</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, when the margins on either axis meet
    /// or exceed the page, or when they leave the content area no positive size once the header
    /// and footer are taken off. Positive infinity reaches this check.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save when an inset is <c>NaN</c> or negative infinity, and also when a large
    /// finite inset leaves the content area positive but too small for a single element. The two
    /// non-finite forms do not reach the check above, so each surfaces as one of the unrelated
    /// messages described in the remarks. Catching <see cref="ArgumentException"/> alone will not
    /// catch them.
    /// </exception>
    public EdgeInsets Margins { get; set; } = new EdgeInsets(72); // 1 inch

    /// <summary>
    /// Optional header band drawn at the top of every page.
    /// Set via <see cref="SetHeader"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    /// <remarks>
    /// A band whose <see cref="RunningBand.Template"/> is null makes the save throw (#531). The
    /// band's height and style carry refusals of their own; see <see cref="RunningBand.Height"/>
    /// and <see cref="TextStyle.FontSize"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// property, when the band's <see cref="RunningBand.Template"/> is <see langword="null"/>
    /// (#531).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size; see
    /// <see cref="RunningBand.Height"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height or its style's size is refused; see <see cref="RunningBand.Height"/> and
    /// <see cref="TextStyle.FontSize"/>.
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
    /// A band whose <see cref="RunningBand.Template"/> is null makes the save throw (#531). The
    /// band's height and style carry refusals of their own; see <see cref="RunningBand.Height"/>
    /// and <see cref="TextStyle.FontSize"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// property, when the band's <see cref="RunningBand.Template"/> is <see langword="null"/>
    /// (#531).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size; see
    /// <see cref="RunningBand.Height"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height or its style's size is refused; see <see cref="RunningBand.Height"/> and
    /// <see cref="TextStyle.FontSize"/>.
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
    /// </remarks>
    public Document SetDefaultFont(TextStyle style) { _defaultStyle = style; return this; }

    /// <summary>Sets a header band with optional style and alignment. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null <paramref name="template"/> is accepted here and throws during the save. The parameter
    /// is non-nullable, but that is a compiler diagnostic rather than a check: how loudly your
    /// build complains depends on your own nullable settings, and <c>null!</c> silences it
    /// entirely. This method builds a <see cref="RunningBand"/>, whose
    /// constructor does not check the template, so the band resolves it during layout and the
    /// failure surfaces as a <see cref="NullReferenceException"/> from a call you did not make.
    /// Pass an empty string for a band that draws no text (#531). Assigning to <see cref="Header"/>
    /// directly reaches the same throw.
    /// <para>The band's height and <paramref name="style"/> carry refusals of their own; see
    /// <see cref="RunningBand.Height"/> and <see cref="TextStyle.FontSize"/>.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// method, when the band's <see cref="RunningBand.Template"/> is <see langword="null"/> (#531).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size; see
    /// <see cref="RunningBand.Height"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height or its style's size is refused; see <see cref="RunningBand.Height"/> and
    /// <see cref="TextStyle.FontSize"/>.
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
    /// A null <paramref name="template"/> is accepted here and throws during the save. The parameter
    /// is non-nullable, but that is a compiler diagnostic rather than a check: how loudly your
    /// build complains depends on your own nullable settings, and <c>null!</c> silences it
    /// entirely. This method builds a <see cref="RunningBand"/>, whose
    /// constructor does not check the template, so the band resolves it during layout and the
    /// failure surfaces as a <see cref="NullReferenceException"/> from a call you did not make.
    /// Pass an empty string for a band that draws no text (#531). Assigning to <see cref="Footer"/>
    /// directly reaches the same throw.
    /// <para>The band's height and <paramref name="style"/> carry refusals of their own; see
    /// <see cref="RunningBand.Height"/> and <see cref="TextStyle.FontSize"/>.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// method, when the band's <see cref="RunningBand.Template"/> is <see langword="null"/> (#531).
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height leaves the content area no positive size; see
    /// <see cref="RunningBand.Height"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, when the
    /// band's height or its style's size is refused; see <see cref="RunningBand.Height"/> and
    /// <see cref="TextStyle.FontSize"/>.
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
    /// and from <see cref="Save(System.IO.Stream)"/> and the other save overloads for the outline tables.
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
    /// <paramref name="path"/> is empty, holds only white space, or holds a null character. Other
    /// characters the file system refuses raise <see cref="IOException"/>.
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
    /// Same refusals as <see cref="LoadTrueTypeFont"/>. This method does not throw them itself:
    /// each one faults the returned task, and surfaces when you await it. A cancelled
    /// <paramref name="cancellationToken"/> cancels the task instead, and awaiting it throws
    /// <see cref="OperationCanceledException"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="path"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds only white space, or holds a null character. Other
    /// characters the file system refuses raise <see cref="IOException"/>.
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
    /// <paramref name="cancellationToken"/> was cancelled before the file was read.
    /// </exception>
    public async Task<EmbeddedFontHandle> LoadTrueTypeFontAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return UseTrueTypeFont(bytes);
    }

    // ── Content methods ──────────────────────────────────────────────────────

    /// <summary>Adds a paragraph to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null paragraph throws <see cref="NullReferenceException"/> from this call.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// <paramref name="paragraph"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(Paragraph paragraph)
    {
        _content.Add(new ParagraphRenderer(paragraph) { ElementLanguage = paragraph.Language });
        return this;
    }

    /// <summary>Adds a horizontal line separator to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null separator is stored, and the save throws when it lays the element out.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="separator"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(LineSeparator separator)
    {
        _content.Add(new LineSeparatorRenderer(separator));
        return this;
    }

    /// <summary>Adds a table to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null table is stored, and the save throws when it lays the element out. A table's own
    /// refusals are on <see cref="TableElement"/>, among them a table that needs more than 50,000
    /// page continuations.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="table"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(TableElement table)
    {
        _content.Add(new TableRenderer(table));
        return this;
    }

    /// <summary>Adds an image to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null image is stored, and the save throws when it lays the element out.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="image"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(LayoutImage image)
    {
        _content.Add(new LayoutImageRenderer(image));
        return this;
    }

    /// <summary>Adds a pie chart to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null chart is stored, and the save throws when it lays the element out.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="chart"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(PieChart chart)
    {
        _content.Add(new PieChartRenderer(chart));
        return this;
    }

    /// <summary>Adds a bulleted or numbered list to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null list is stored, and the save throws when it lays the element out.
    /// <para>Do not pass null. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="list"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(ListElement list)
    {
        _content.Add(new ListRenderer(list));
        return this;
    }

    /// <summary>Adds a heading to the document content. Returns this document for chaining.</summary>
    /// <remarks>
    /// A null heading throws <see cref="NullReferenceException"/> from this call.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// <paramref name="heading"/> is <see langword="null"/>.
    /// </exception>
    public Document Add(Heading heading)
    {
        _content.Add(new HeadingRenderer(heading));
        return this;
    }

    /// <summary>Adds a paragraph built from the given text, using the supplied style or the default style. Returns this document for chaining.</summary>
    /// <remarks>
    /// With <paramref name="style"/> null, the paragraph takes the style from the most recent
    /// <see cref="SetDefaultFont"/> call made before this one.
    /// A null <paramref name="text"/> is stored, and the save throws when it lays the paragraph
    /// out.
    /// <para>Do not pass null text. A later major version will throw
    /// <see cref="ArgumentNullException"/> from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from <see cref="Save(System.IO.Stream)"/> and the other save overloads, not from this
    /// call, when <paramref name="text"/> is <see langword="null"/>.
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
    /// <see cref="PdfConformance.None"/>.
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
    /// <paramref name="destination"/>, so an exception from any of those reaches you from here. The
    /// tags below name what this library raises itself; a custom renderer or the destination can
    /// raise others.</para>
    /// <para>A second call after a save that succeeded throws. It reports that
    /// the document has already been written and tells you to create a new one. It writes nothing
    /// to the stream, appended or otherwise.</para>
    /// <para>A save that threw can leave the document holding pages from the failed attempt, and
    /// its elements holding state from it, so a retry can succeed on a file that differs from a
    /// fresh build: a one-page document retried after a null <paramref name="destination"/> saved
    /// two pages (#530). Build a fresh <see cref="Document"/> rather than retrying a save that
    /// threw.</para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>. This is checked after the layout
    /// has run, so the document is left holding a layout's pages; see the remarks.
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
    /// <see cref="UseTrueTypeFont"/> lacks a table the save needs. For the element inputs, the
    /// boundary documentation on the property you set says which values reach this.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Many unrelated conditions share this type, so <b>do not</b> switch on the parameter name to
    /// tell them apart. A non-writable <paramref name="destination"/> reports the internal name
    /// <c>stream</c> rather than <c>destination</c>. Document geometry reports <c>margins</c>. An
    /// element that refuses its own input reports a name of its own choosing, which is not always
    /// the property you set. A pie chart names the property; an image names a private field of its
    /// renderer, the chart having been fixed and the image left (#481). Read the boundary
    /// documentation on the property instead, where each element has it. A registered font with a
    /// metric this library cannot write, such as a <c>unitsPerEm</c> of 0, reports <c>value</c>;
    /// see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called. The two
    /// cannot be combined.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member stored without a check: a band's <see cref="RunningBand.Template"/>
    /// (#531), an element or text passed to an <c>Add</c> overload that stores it, a null
    /// <see cref="PageSize"/>, or a null inside an element.
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
        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
        };
        foreach (var r in _content) renderer.Add(r);
        renderer.Render(destination);
        CollectDiagnostics(renderer);
    }

    /// <summary>Runs the layout pass and writes the resulting PDF to a file at the given path.</summary>
    /// <remarks>
    /// Everything on <see cref="Save(System.IO.Stream)"/> applies, and one thing more.
    /// <para><b>Attention</b>: the file is opened before the layout runs, so a failure destroys whatever
    /// the path held. Measured: a path already holding a 1,535-byte file, given to a new
    /// document whose layout then fails, is left existing and zero bytes long. The same happens
    /// on a second call to the same path, since opening it already truncates whatever was there
    /// before the "document already written" check runs, so
    /// <see cref="Save(System.IO.Stream)"/>'s "writes nothing to the stream" guarantee does not
    /// carry over here. If the target matters, write to a temporary path and move it into place
    /// yourself, or save to a stream you control (#508).</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also
    /// catches it.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds only white space, or holds a null character,
    /// reported while the file is opened. The layout and writing causes listed on
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
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, or
    /// <paramref name="path"/> is otherwise invalid for the file system, being over-long or holding
    /// a character the file system refuses, such as <c>&lt;</c> or <c>|</c>. It is also raised when
    /// writing the file fails.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>, or a registered
    /// font lacks a table the save needs. The file is open and truncated by the time any of these
    /// fires.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member stored without a check: a band's <see cref="RunningBand.Template"/>
    /// (#531), an element or text passed to an <c>Add</c> overload that stores it, a null
    /// <see cref="PageSize"/>, or a null inside an element.
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
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(fs);
    }

    /// <summary>
    /// Asynchronously runs the layout pass and writes the resulting PDF to the given stream.
    ///
    /// <para>
    /// The layout pass is CPU-bound, so it runs on a thread-pool thread via
    /// <see cref="Task.Run(Action)"/>; the resulting document is then written to
    /// <paramref name="destination"/> via <see cref="PdfDocument.SaveAsync"/>.
    /// <paramref name="cancellationToken"/> is honoured before layout starts and during the
    /// final write, but does not abort layout or serialisation already in progress.
    /// </para>
    /// </summary>
    /// <remarks>
    /// A document is single-use. The layout runs here, not when you add an element, so most of what
    /// can go wrong goes wrong at this call rather than at the one that set the bad value.
    /// <para>The save runs every element's renderer, a custom <see cref="IRenderer"/> included,
    /// reads every font registered with <see cref="UseTrueTypeFont"/>, and writes to
    /// <paramref name="destination"/>, so an exception from any of those reaches you from here. The
    /// tags below name what this library raises itself; a custom renderer or the destination can
    /// raise others.</para>
    /// <para>A second call after a save that succeeded throws. It reports that
    /// the document has already been written and tells you to create a new one. It writes nothing
    /// to the stream, appended or otherwise.</para>
    /// <para>A save that threw can leave the document holding pages and element state from the
    /// failed attempt, so a retry can succeed on a wrong file (#530). Build a fresh
    /// <see cref="Document"/> rather than retrying a save that threw.</para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="destination"/> is <see langword="null"/>. This is checked after the layout
    /// has run, so the document is left holding a layout's pages; see the remarks.
    /// </exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also catches
    /// it; <see cref="Document"/> implements <see cref="IDisposable"/>, so this is ordinary misuse
    /// rather than a case this API adds. It is also raised when <paramref name="destination"/> was
    /// already closed.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>; see
    /// <see cref="Conformance"/>. It is also raised when a font registered with
    /// <see cref="UseTrueTypeFont"/> lacks a table the save needs. For the element inputs, the
    /// boundary documentation on the property you set says which values reach this.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The margins, header and footer leave the content area no positive size, or an element
    /// refuses its own input while being laid out. The boundary documentation on the individual
    /// properties says which inputs those are. A registered font with a metric this library cannot
    /// write, such as a <c>unitsPerEm</c> of 0, raises it too; see <see cref="UseTrueTypeFont"/>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Two unrelated conditions share this type: <paramref name="destination"/> does not support
    /// writing, or <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called,
    /// which cannot be combined.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member stored without a check: a band's <see cref="RunningBand.Template"/>
    /// (#531), an element or text passed to an <c>Add</c> overload that stores it, a null
    /// <see cref="PageSize"/>, or a null inside an element.
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
    /// <paramref name="cancellationToken"/> was cancelled before the write to the destination
    /// started. A token already cancelled when the call starts stops it before the layout. A
    /// cancellation during the layout is seen once the layout has finished, with its pages kept,
    /// and one during serialisation once the document is in memory, which then counts as written.
    /// All three end the task with a <see cref="TaskCanceledException"/> before anything is written
    /// to the destination. During that write the token is passed to the destination stream, which
    /// may ignore it, raise <see cref="OperationCanceledException"/>, or raise something else.
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
        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
        };
        foreach (var r in _content) renderer.Add(r);
        await Task.Run(renderer.RunLayout, cancellationToken).ConfigureAwait(false);
        await _pdf.SaveAsync(destination, cancellationToken).ConfigureAwait(false);
        CollectDiagnostics(renderer);
    }

    /// <summary>Asynchronously runs the layout pass and writes the resulting PDF to a file at the given path.</summary>
    /// <remarks>
    /// Everything on <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>
    /// applies, and one thing more.
    /// <para><b>Attention</b>: the file is opened before the layout runs, so a failure destroys whatever
    /// the path held. Measured: a path already holding a 1,535-byte file, given to a new
    /// document whose layout then fails, is left existing and zero bytes long. The same happens
    /// on a second call to the same path, since opening it already truncates whatever was there
    /// before the "document already written" check runs, so
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/>'s "writes
    /// nothing to the stream" guarantee does not carry over here. If the target matters, write to
    /// a temporary path and move it into place yourself, or save to a stream you control
    /// (#508).</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="path"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also
    /// catches it.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="path"/> is empty, holds only white space, or holds a null character,
    /// reported while the file is opened. The layout causes listed on
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
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, or
    /// <paramref name="path"/> is otherwise invalid for the file system, being over-long or holding
    /// a character the file system refuses, such as <c>&lt;</c> or <c>|</c>. It is also raised when
    /// writing the file fails.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be laid
    /// out, or <see cref="Encrypt"/> is combined with a PDF/A <see cref="Conformance"/>, or with
    /// PDF/UA-1 when the settings leave out <see cref="PdfPermissions.Extract"/>, or a registered
    /// font lacks a table the save needs. The file is open and truncated by the time any of these
    /// fires.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A null that a member stored without a check: a band's <see cref="RunningBand.Template"/>
    /// (#531), an element or text passed to an <c>Add</c> overload that stores it, a null
    /// <see cref="PageSize"/>, or a null inside an element.
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
    /// <paramref name="cancellationToken"/> was cancelled before the write to the destination
    /// started. A token already cancelled when the call starts stops it before the layout. A
    /// cancellation during the layout is seen once the layout has finished, with its pages kept,
    /// and one during serialisation once the document is in memory, which then counts as written.
    /// All three end the task with a <see cref="TaskCanceledException"/> before anything is written
    /// to the destination. During that write the token is passed to the destination stream, which
    /// may ignore it, raise <see cref="OperationCanceledException"/>, or raise something else.
    /// </exception>
#pragma warning disable RS0026
    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        await using var fs = new FileStream(
            path, FileMode.Create, FileAccess.Write, FileShare.None,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await SaveAsync(fs, cancellationToken).ConfigureAwait(false);
    }

    // ── Signing seam (VellumPdf.Signing only) ────────────────────────────────

    /// <summary>
    /// Runs the layout pass and delegates to
    /// <see cref="PdfDocument.PrepareForSigning"/> to produce the unsigned placeholder bytes.
    /// Called exclusively by <c>VellumPdf.Signing.SigningExtensions</c>.
    /// </summary>
    internal byte[] PrepareForSigning(SignaturePlaceholderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
        };
        foreach (var r in _content) renderer.Add(r);
        renderer.RunLayout();
        var prepared = _pdf.PrepareForSigning(options);
        CollectDiagnostics(renderer);
        return prepared;
    }

    /// <summary>Releases the underlying <see cref="PdfDocument"/> and its resources.</summary>
    /// <remarks>
    /// Calling it again does nothing. Afterwards <see cref="Encrypt"/> throws
    /// <see cref="ObjectDisposedException"/>, and so does a save once its geometry check and
    /// layout have passed; a geometry refusal is reported first. <see cref="Add(string, TextStyle)"/>
    /// and the other <c>Add</c> overloads, <see cref="UseTrueTypeFont"/> and
    /// <see cref="UseCmykOutputIntent"/> are still accepted, and what they add is lost.
    /// </remarks>
    /// <exception cref="ObjectDisposedException">
    /// Raised from a later save or <see cref="Encrypt"/>, not from this call.
    /// </exception>
    public void Dispose() => _pdf.Dispose();
}
