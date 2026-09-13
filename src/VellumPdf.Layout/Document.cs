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
/// doc.Add(new Paragraph("Hello, world!"));
/// doc.Save("output.pdf");
/// </code>
/// </summary>
public sealed class Document : IDisposable
{
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
    public IReadOnlyList<TextEncodingWarning> TextEncodingWarnings => _textEncodingWarnings;

    /// <summary>
    /// Running bands whose text was wider than the content box and was cut to fit, from the last
    /// save. At most one report per band, each naming the page that lost the most. Empty when both
    /// bands fitted, or when no band was set.
    /// </summary>
    public IReadOnlyList<BandTruncationWarning> BandTruncations => _bandTruncations;

    /// <summary>Document metadata (title, author, subject, keywords, etc.).</summary>
    public PdfDocumentInfo Info => _pdf.Info;

    /// <summary>The default page size used for newly created pages.</summary>
    /// <remarks>
    /// Validated at save, not here. A width or height that is not a positive finite number is
    /// refused with <see cref="ArgumentOutOfRangeException"/> naming the axis and the value, and
    /// a page narrower or shorter than <see cref="Margins"/> is refused with
    /// <see cref="ArgumentException"/>.
    /// <para>Setting this after <see cref="Add(Paragraph)"/> applies to what you have already
    /// added. The layout runs at save, so the size in force then is the size the whole document
    /// is laid out at, not only the part added after you set it.</para>
    /// <para>On a document no save has been attempted on, that holds byte for byte. Resizing
    /// before <see cref="Save(System.IO.Stream)"/> produces a file matching one built at the new
    /// size from the start, except the random <c>/ID</c> and the XMP
    /// <c>CreateDate</c>/<c>ModifyDate</c> timestamps, which carry the time each build actually
    /// ran. Measured by resizing a document from 600 by 800 to 200 by 120 before saving and
    /// normalising those three fields: the bytes match a build at 200 by 120 throughout, and
    /// every <c>/MediaBox</c> carries the new size. A save that already threw during layout
    /// breaks this, along with the rest of the document's state; see
    /// <see cref="Save(System.IO.Stream)"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from a save rather than from this property, when the width or height is zero,
    /// negative or not finite.
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
    /// PDF/A-2a implies <see cref="Tagged"/> = true.
    /// </summary>
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
    /// the page-continuation cap. Neither message is what actually happened. Both were fixed for
    /// the ordinary case elsewhere and are still open here (#481, #502).</para>
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
    public RunningBand? Header { get; set; }

    /// <summary>
    /// Optional footer band drawn at the bottom of every page.
    /// Set via <see cref="SetFooter"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    public RunningBand? Footer { get; set; }

    /// <summary>Sets the default text style applied to content added without an explicit style. Returns this document for chaining.</summary>
    public Document SetDefaultFont(TextStyle style) { _defaultStyle = style; return this; }

    /// <summary>Sets a header band with optional style and alignment. Returns this document for chaining.</summary>
    public Document SetHeader(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Header = new RunningBand(template, style, alignment);
        return this;
    }

    /// <summary>Sets a footer band with optional style and alignment. Returns this document for chaining.</summary>
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
    public EmbeddedFontHandle UseTrueTypeFont(byte[] fontData) =>
        _pdf.UseTrueTypeFont(fontData);

    /// <summary>
    /// Loads a TrueType font file from disk, registers it for embedding, and returns a handle.
    /// </summary>
    public EmbeddedFontHandle LoadTrueTypeFont(string path) =>
        UseTrueTypeFont(File.ReadAllBytes(path));

    /// <summary>
    /// Asynchronously loads a TrueType font file from disk, registers it for embedding, and
    /// returns a handle.
    /// </summary>
    public async Task<EmbeddedFontHandle> LoadTrueTypeFontAsync(string path, CancellationToken cancellationToken = default)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return UseTrueTypeFont(bytes);
    }

    // ── Content methods ──────────────────────────────────────────────────────

    /// <summary>Adds a paragraph to the document content. Returns this document for chaining.</summary>
    public Document Add(Paragraph paragraph)
    {
        _content.Add(new ParagraphRenderer(paragraph) { ElementLanguage = paragraph.Language });
        return this;
    }

    /// <summary>Adds a horizontal line separator to the document content. Returns this document for chaining.</summary>
    public Document Add(LineSeparator separator)
    {
        _content.Add(new LineSeparatorRenderer(separator));
        return this;
    }

    /// <summary>Adds a table to the document content. Returns this document for chaining.</summary>
    public Document Add(TableElement table)
    {
        _content.Add(new TableRenderer(table));
        return this;
    }

    /// <summary>Adds an image to the document content. Returns this document for chaining.</summary>
    public Document Add(LayoutImage image)
    {
        _content.Add(new LayoutImageRenderer(image));
        return this;
    }

    /// <summary>Adds a pie chart to the document content. Returns this document for chaining.</summary>
    public Document Add(PieChart chart)
    {
        _content.Add(new PieChartRenderer(chart));
        return this;
    }

    /// <summary>Adds a bulleted or numbered list to the document content. Returns this document for chaining.</summary>
    public Document Add(ListElement list)
    {
        _content.Add(new ListRenderer(list));
        return this;
    }

    /// <summary>Adds a heading to the document content. Returns this document for chaining.</summary>
    public Document Add(Heading heading)
    {
        _content.Add(new HeadingRenderer(heading));
        return this;
    }

    /// <summary>Adds a paragraph built from the given text, using the supplied style or the default style. Returns this document for chaining.</summary>
    public Document Add(string text, TextStyle? style = null)
        => Add(new Paragraph(text, style ?? _defaultStyle));

    /// <summary>
    /// Adds a custom renderer to the document content. Returns this document for chaining.
    /// Accepts any <see cref="IRenderer"/> implementation, including ones defined outside this
    /// library — the seam satellite packages (e.g. VellumPdf.Barcodes) use to plug their own
    /// elements into the flow layout.
    /// </summary>
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
    /// <param name="componentCount">Number of colour components: 1 (Gray), 3 (RGB), or 4 (CMYK).</param>
    /// <param name="outputConditionIdentifier">The OutputConditionIdentifier string.</param>
    /// <param name="info">Optional /Info string. Defaults to <paramref name="outputConditionIdentifier"/> when null.</param>
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
    /// <param name="outputConditionIdentifier">The OutputConditionIdentifier string written to the OutputIntent dictionary.</param>
    public void UseCmykOutputIntent(string outputConditionIdentifier = "Generic CMYK") =>
        _pdf.UseCmykOutputIntent(outputConditionIdentifier);

    // ── Encryption ──────────────────────────────────────────────────────────

    /// <summary>
    /// Configures AES-256 encryption for this document.
    /// Delegates to <see cref="PdfDocument.Encrypt"/>.
    /// Must be called before <see cref="Save(Stream)"/>.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="settings"/> has an empty <see cref="PdfEncryptionSettings.OwnerPassword"/>
    /// beside a non-empty <see cref="PdfEncryptionSettings.UserPassword"/>.
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
    /// A document is single-use. The layout runs here, not when you add an element, so most of
    /// what can go wrong goes wrong at this call rather than at the one that set the bad value.
    /// <para>Calling this twice after a save that succeeded throws. The second call reports that
    /// the document has already been written and tells you to create a new one. It writes nothing
    /// to the stream, appended or otherwise.</para>
    /// <para><b>Attention</b>: a save that threw does not reliably leave the document usable
    /// again either, and the three routes out of a failed save leave it in three different
    /// states. Geometry refused before the layout starts, such as margins exceeding the page,
    /// leaves it clean: after correcting the geometry, a retry produced a file identical in
    /// length and page count to a fresh document's. Reaching the writer leaves it dead, and a
    /// retry on a good stream throws about the document having already been written.</para>
    /// <para>A throw from the layout itself leaves it alive and wrong, and this is the route to
    /// watch, because the pages laid out before the throw stay and the retry appends a second
    /// layout to them. On the fixture in #530, retrying after enlarging the page gave
    /// <b>4 pages</b> where a fresh document with the same content gave 1. No exception was
    /// raised on any retry measured, so treat the file as wrong rather than expecting the save
    /// to tell you. Build a fresh <see cref="Document"/> rather than retrying a save that threw
    /// (#530).</para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also catches
    /// it; <see cref="Document"/> implements <see cref="IDisposable"/>, so this is ordinary misuse
    /// rather than a case this API adds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be
    /// laid out, or <see cref="Conformance"/> asks for PDF/A while <see cref="Encrypt"/> has been
    /// called, which ISO 19005-2 §6.1.3 prohibits. For the element inputs, the boundary
    /// documentation on the property you set says which values reach this. That last pairing has
    /// no such documentation on either member, so it is stated here.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Many unrelated conditions share this type, so <b>do not</b> switch on the parameter name to
    /// tell them apart. A non-writable <paramref name="destination"/> reports the internal name
    /// <c>stream</c> rather than <c>destination</c>. Document geometry reports <c>margins</c>.
    /// An element that refuses its own input reports a name of its own choosing, which is not
    /// always the property you set. A pie chart names the property; an image names a private
    /// field of its renderer, the chart having been fixed and the image left (#481). Read the
    /// boundary documentation on the property instead, where each element has it.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called. The two
    /// cannot be combined.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A running band set through <see cref="SetHeader"/> or <see cref="SetFooter"/> has a null
    /// <see cref="RunningBand.Template"/>. The constructor does not check it and the band resolves
    /// its template during the save (#531).
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Cell.ColSpan"/> values sum past <see cref="int.MaxValue"/> while a
    /// table's column count is resolved.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A table's column count, resolved by <see cref="Cell.ColSpan"/> to something near
    /// <see cref="int.MaxValue"/>, asks for a per-column width array too large to allocate.
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
    /// <paramref name="path"/> is empty or otherwise not a path the file system accepts, reported
    /// while the file is opened. The layout and writing causes listed on
    /// <see cref="Save(System.IO.Stream)"/> reach here too, once it is open. That tag says why not
    /// to tell any of them apart by parameter name.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory named in <paramref name="path"/> does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="path"/> names a directory rather than a file, or an existing file at
    /// <paramref name="path"/> is read-only.
    /// </exception>
    /// <exception cref="IOException">
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, or
    /// <paramref name="path"/> is otherwise invalid for the file system, being over-long or a syntax
    /// the file system refuses.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be
    /// laid out, or PDF/A <see cref="Conformance"/> is combined with <see cref="Encrypt"/>. The
    /// file is open and truncated by the time any of these fires.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A running band set through <see cref="SetHeader"/> or <see cref="SetFooter"/> has a null
    /// <see cref="RunningBand.Template"/>. The constructor does not check it and the band resolves
    /// its template during the save (#531).
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
    /// A document is single-use. The layout runs here, not when you add an element, so most of
    /// what can go wrong goes wrong at this call rather than at the one that set the bad value.
    /// <para>Calling this twice after a save that succeeded throws. The second call reports that
    /// the document has already been written and tells you to create a new one. It writes nothing
    /// to the stream, appended or otherwise.</para>
    /// <para>A save that threw does not reliably leave the document usable again either. The
    /// three routes out of a failed save leave it clean, dead, or alive and wrong, and nothing
    /// reports the third. Build a fresh <see cref="Document"/> rather than retrying a save that
    /// threw; <see cref="Save(System.IO.Stream)"/> has the measurements (#530).</para>
    /// <para>A document with no pages throws as well. Add at least one element before you
    /// save.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">
    /// This <see cref="Document"/> was already disposed. It derives from
    /// <see cref="InvalidOperationException"/>, so a catch written for the base type also catches
    /// it; <see cref="Document"/> implements <see cref="IDisposable"/>, so this is ordinary misuse
    /// rather than a case this API adds.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be
    /// laid out, or <see cref="Conformance"/> asks for PDF/A while <see cref="Encrypt"/> has been
    /// called, which ISO 19005-2 §6.1.3 prohibits. For the element inputs, the boundary
    /// documentation on the property you set says which values reach this. That last pairing has
    /// no such documentation on either member, so it is stated here.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The margins, header and footer leave the content area no positive size, or an element
    /// refuses its own input while being laid out. The boundary documentation on the individual
    /// properties says which inputs those are.
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
    /// A running band set through <see cref="SetHeader"/> or <see cref="SetFooter"/> has a null
    /// <see cref="RunningBand.Template"/>. The constructor does not check it and the band resolves
    /// its template during the save (#531).
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
    /// <paramref name="cancellationToken"/> was already cancelled, or was cancelled before the
    /// layout pass started. Catch the base type: the layout pass raises the derived
    /// <see cref="TaskCanceledException"/>, and a cancellation during the write raises whatever
    /// the destination stream raises, which need not be the same type.
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
    /// <paramref name="path"/> is empty or otherwise not a path the file system accepts, reported
    /// while the file is opened. The layout causes listed on
    /// <see cref="SaveAsync(System.IO.Stream, System.Threading.CancellationToken)"/> reach here
    /// too, once it is open. These are unrelated conditions that happen to share a type, so do
    /// <b>not</b> tell them apart by parameter name.
    /// </exception>
    /// <exception cref="DirectoryNotFoundException">
    /// The directory named in <paramref name="path"/> does not exist.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// <paramref name="path"/> names a directory rather than a file, or an existing file at
    /// <paramref name="path"/> is read-only.
    /// </exception>
    /// <exception cref="IOException">
    /// A file at <paramref name="path"/> is already open elsewhere with no sharing allowed, or
    /// <paramref name="path"/> is otherwise invalid for the file system, being over-long or a syntax
    /// the file system refuses.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// The document has already been written, or has no pages, or an element's input cannot be
    /// laid out, or PDF/A <see cref="Conformance"/> is combined with <see cref="Encrypt"/>. The
    /// file is open and truncated by the time any of these fires.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="PageSize"/> has a width or height that is not a positive finite number.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// <see cref="UseObjectStreams"/> was set and <see cref="Encrypt"/> was called.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// A running band set through <see cref="SetHeader"/> or <see cref="SetFooter"/> has a null
    /// <see cref="RunningBand.Template"/>. The constructor does not check it and the band resolves
    /// its template during the save (#531).
    /// </exception>
    /// <exception cref="OverflowException">
    /// A row's <see cref="Cell.ColSpan"/> values sum past <see cref="int.MaxValue"/>.
    /// </exception>
    /// <exception cref="OutOfMemoryException">
    /// A column count resolved from <see cref="Cell.ColSpan"/> asks for a per-column width array
    /// too large to allocate.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was already cancelled, or was cancelled before the
    /// layout pass started. Catch the base type: the layout pass raises the derived
    /// <see cref="TaskCanceledException"/>, and a cancellation during the write raises whatever
    /// the destination stream raises, which need not be the same type.
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
    public void Dispose() => _pdf.Dispose();
}
