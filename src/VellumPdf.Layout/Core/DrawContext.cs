// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Fonts;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Passed to <see cref="IRenderer.Draw"/>. Provides access to the current page's
/// canvas, a per-page resource context, and the document font/resource provider.
///
/// Layout space: origin top-left, Y increases down, units = points.
/// PDF space:    origin bottom-left, Y increases up, units = points.
///
/// Flip formula: pdfY = pageHeight - layoutY
/// </summary>
/// <remarks>
/// Coordinates are not checked. A finite Y off the page is written into outline destinations
/// without clamping, and so is a finite box into an annotation. What the save refuses is the value
/// it writes, not the value passed. See <see cref="AddUriLinkAnnotation"/> and
/// <see cref="ToPdfY"/>. A box with a negative width is written as an inverted rectangle. Where a
/// renderer draws with a converted value on the canvas, a non-finite one is written as <c>NaN</c>
/// or <c>Infinity</c>.
/// <para>A coordinate is written in plain decimal, with no exponent. A finite one beyond the 64-bit
/// integer range therefore becomes an integer token that a 64-bit reader cannot hold. qpdf 12.4.1
/// drops the annotation or outline item holding <b>9.3e18</b> and keeps one holding 9.2e18.</para>
/// </remarks>
public sealed class DrawContext
{
    /// <summary>The content-stream canvas for the current page.</summary>
    /// <remarks>
    /// Stored from the constructor without a check, so it is null if you passed null. The
    /// document always passes the page's canvas.
    /// </remarks>
    public PdfCanvas Canvas { get; }

    /// <summary>The full page bounds in layout space (Y-down).</summary>
    /// <remarks>
    /// Stored without a check. <see cref="ToPdfY"/> and <see cref="ToPdfRect"/> read only its
    /// <see cref="LayoutBox.Height"/>.
    /// </remarks>
    public LayoutBox PageBounds { get; }   // full page in layout space

    /// <summary>The per-page resource registration context.</summary>
    /// <remarks>The same instance for every draw on this page.</remarks>
    public RendererContext RendererContext { get; }   // per-page resource registration
    private readonly PdfDocument _document;
    private readonly PdfPage _page;

    /// <summary>
    /// Whether tagged PDF output is enabled. When true, Draw implementations should
    /// wrap their content in <see cref="PdfCanvas.BeginMarkedContent"/> /
    /// <see cref="PdfCanvas.EndMarkedContent"/> and call <see cref="RegisterStructElem"/>.
    /// </summary>
    /// <remarks>
    /// Reads the kernel document, which reports true when tagging was requested and also when the
    /// conformance level is PDF/A-2a or PDF/UA-1. When it is false,
    /// <see cref="RegisterStructElemTree"/> and <see cref="StampStructElemPage"/> do nothing, and
    /// the document discards an element passed to <see cref="RegisterStructElem"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this getter when this context was constructed with a null document.
    /// </exception>
    public bool Tagged => _document.Tagged;

    /// <summary>Creates a draw context bound to the current page, its canvas, and the owning document.</summary>
    /// <remarks>
    /// Nothing is checked, and every argument is stored. A null <paramref name="page"/> leaves
    /// each structure element <see cref="RegisterStructElem"/> registers without a page, and the
    /// save writes it without a <c>/Pg</c> entry and without an exception.
    /// <see cref="RegisterStructElemTree"/> sets no page, so a root registered through it keeps
    /// the one the caller gave it, and the save writes that. Any null, the page included, can also
    /// surface later, from the first member that uses it; see the exception tags. The document
    /// constructs this type itself and never passes null; construct one yourself only to test a
    /// renderer.
    /// <para>A <paramref name="page"/> from a different document is accepted. A link annotation
    /// registered on it is left out of the saved file, so a box the save would otherwise refuse is
    /// written nowhere and raises nothing. An outline entry is written anyway, with a null
    /// destination page.</para>
    /// <para>Do not pass null. A later major version will throw <see cref="ArgumentNullException"/>
    /// from this constructor.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised later, not from this constructor: by a renderer that draws on a null
    /// <paramref name="canvas"/>, or by a member of this type that uses a null
    /// <paramref name="rendererContext"/> or <paramref name="document"/>.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised later, not from this constructor, when <paramref name="page"/> is null and
    /// <paramref name="document"/> is not: by <see cref="AddUriLinkAnnotation"/>, and by the
    /// document's save after a call to <see cref="AddOutlineEntry(string, int, double)"/> that passed a non-null title.
    /// <c>ParamName</c> is <c>key</c> in both cases.
    /// </exception>
    public DrawContext(PdfCanvas canvas, LayoutBox pageBounds, RendererContext rendererContext, PdfDocument document, PdfPage page)
    {
        Canvas = canvas;
        PageBounds = pageBounds;
        RendererContext = rendererContext;
        _document = document;
        _page = page;
    }

    /// <summary>
    /// Where the document's layout collects diagnostics. Null for a context a caller constructs, so
    /// a custom renderer that builds its own context reports nothing and does not throw.
    /// </summary>
    internal LayoutDiagnosticSink? Diagnostics { get; init; }

    /// <summary>Returns (or creates) a font resource on the current document.</summary>
    /// <remarks>
    /// This does not embed a file. Nothing is checked: a value the enumeration does not name is
    /// accepted here, and the save throws once the resource this returns is selected with
    /// <see cref="PdfCanvas.SetFont"/>, with or without text after it.
    /// </remarks>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when <paramref name="font"/> is not a named
    /// <see cref="Standard14"/> value and the resource this returns is selected with
    /// <see cref="PdfCanvas.SetFont"/>.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context was constructed with a null document.
    /// </exception>
    public PdfFontResource GetFont(Standard14 font) => _document.UseFont(font);

    /// <summary>
    /// Records that the current page uses the given embedded TrueType font handle
    /// and returns its PDF resource name so the canvas can select it.
    /// </summary>
    /// <remarks>
    /// This context's own page and document are not read here; its
    /// <see cref="RendererContext"/> is. The handle should come from the document that saves the page.
    /// A handle from another document is refused when the saving document defines no font under its
    /// resource name, at the save and not here; see
    /// <see cref="VellumPdf.Document.PdfDocument.RegisterEmbeddedFontUsage"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context's <see cref="RendererContext"/> is
    /// <see langword="null"/>, or was constructed with a null document.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised from this call when <paramref name="handle"/> is null, or when this context's
    /// <see cref="RendererContext"/> was constructed with a null page and a non-null document. The
    /// page is checked first, so a null page names <c>page</c> whether or not the handle is null;
    /// otherwise <c>ParamName</c> is <c>handle</c>.
    /// </exception>
    public string UseEmbeddedFont(EmbeddedFontHandle handle)
    {
        RendererContext.RegisterEmbeddedFontUsage(handle);
        return handle.ResourceName;
    }

    /// <summary>Converts a layout-space Y coordinate to PDF user-space Y.</summary>
    /// <remarks>
    /// Not refused. A non-finite <paramref name="layoutY"/> yields a non-finite PDF Y, and so does
    /// a finite one when <see cref="PageBounds"/>'s height is not finite or the subtraction
    /// overflows. A save refuses the Y it writes; see <see cref="AddOutlineEntry(string, int, double)"/>.
    /// </remarks>
    public double ToPdfY(double layoutY) => PageBounds.Height - layoutY;

    /// <summary>
    /// Converts layout-space (x, y, width, height) → PDF (x, pdfY-height, width, height).
    /// PDF rectangles are anchored at their lower-left corner.
    /// </summary>
    /// <remarks>
    /// A negative or empty <paramref name="box"/> is converted as given.
    /// </remarks>
    public (double x, double y, double w, double h) ToPdfRect(LayoutBox box) =>
        (box.X, PageBounds.Height - box.Bottom, box.Width, box.Height);

    /// <summary>
    /// Registers a URI /Link annotation on the current page.
    /// <paramref name="box"/> is in layout space (Y-down); the method converts to PDF space.
    /// </summary>
    /// <remarks>
    /// <paramref name="uri"/> is not validated. Empty and <c>not a uri</c> are written into
    /// <c>/URI</c> as given, and so is an absolute URI of any scheme, such as
    /// <c>javascript:alert(1)</c>. Non-ASCII characters are percent-encoded as UTF-8, and an
    /// unpaired surrogate becomes U+FFFD first. The link is written untagged and without an
    /// alternate description, whatever the conformance, and no exception reports it. In a document
    /// whose <c>Conformance</c> is PDF/UA-1, the link breaks ISO 14289-1, 7.18.5 (#550). A link
    /// whose rectangle lies wholly outside the page's crop box is exempt: clause 7.18.1 lifts the
    /// requirements of clause 7.18 for it. A null <paramref name="uri"/> writes a link annotation
    /// with no action, so the area is a link that goes nowhere.
    /// <para>A non-finite coordinate in <paramref name="box"/> is accepted here, and so are finite
    /// ones whose sum overflows. The save throws when it writes the annotation's rectangle.</para>
    /// <para>Do not pass a null or relative <paramref name="uri"/>. A later major version will
    /// refuse a value that is not an absolute URI.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when a value written into the rectangle is not finite.
    /// <paramref name="box"/> reaches it through <see cref="ToPdfRect"/>, so a finite box is
    /// refused when its X plus its width overflows, or when <see cref="PageBounds"/>'s height is
    /// not finite or an edge subtracted from it overflows. The message says PDF does not support
    /// NaN or Infinity as a real number.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context was constructed with a null document.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised from this call when this context was constructed with a null page and a non-null
    /// document, whether or not <paramref name="uri"/> is null. <c>ParamName</c> is <c>key</c>.
    /// </exception>
    public void AddUriLinkAnnotation(LayoutBox box, string uri)
    {
        var (x, y, w, h) = ToPdfRect(box);
        var annot = new PdfLinkAnnotation
        {
            Rect = new VellumPdf.Document.PdfRectangle(x, y, x + w, y + h),
            Uri = uri,
        };
        _document.RegisterLinkAnnotation(_page, annot);
    }

    /// <summary>The conformance level of the document this context draws into.</summary>
    internal VellumPdf.Document.PdfConformance Conformance =>
        _document?.Conformance ?? VellumPdf.Document.PdfConformance.None;

    /// <summary>
    /// Registers a link annotation to the named destination <paramref name="name"/>, as
    /// <see cref="AddUriLinkAnnotation"/> registers one to a URI: the same rectangle, the same
    /// registration, so a context with no page throws the same <see cref="ArgumentNullException"/>.
    /// A destination is named, not given as a GoTo action, because ISO 32000-1, 12.6.4.2 prefers
    /// the direct form.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The document claims PDF/UA-1, which a link to an anchor breaks without a Link structure
    /// element, or the layout in progress is for this document and no element of it holds
    /// the anchor.
    /// </exception>
    internal void AddAnchorLinkAnnotation(LayoutBox box, string name, LinkOwner owner = LinkOwner.Run)
    {
        GuardAnchorLink(name, owner);
        var (x, y, w, h) = ToPdfRect(box);
        var annot = new PdfLinkAnnotation
        {
            Rect = new VellumPdf.Document.PdfRectangle(x, y, x + w, y + h),
            DestName = name,
        };
        _document.RegisterLinkAnnotation(_page, annot);
    }

    /// <summary>
    /// Registers a link to <paramref name="uri"/> or to the anchor <paramref name="anchor"/> over
    /// <paramref name="box"/> for a table cell or a running band, and says whether it did. A
    /// context with no page writes none, and so does a box whose rectangle the annotation would
    /// write with a value that is not finite; neither is reported. Removed in 3.0 (#688), which
    /// refuses both where the box is made.
    /// </summary>
    internal bool TryAddLinkAnnotation(LayoutBox box, string? uri, string? anchor, LinkOwner owner)
    {
        if (_page is null) return false;
        var (x, y, w, h) = ToPdfRect(box);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(x + w) || !double.IsFinite(y + h))
            return false;
        if (anchor is not null)
        {
            GuardAnchorLink(anchor, owner);
            _document.RegisterLinkAnnotation(_page, new PdfLinkAnnotation
            {
                Rect = new VellumPdf.Document.PdfRectangle(x, y, x + w, y + h),
                DestName = anchor,
            });
            return true;
        }

        if (uri is null) return false;
        _document.RegisterLinkAnnotation(_page, new PdfLinkAnnotation
        {
            Rect = new VellumPdf.Document.PdfRectangle(x, y, x + w, y + h),
            Uri = uri,
        });
        return true;
    }

    private static string Describe(LinkOwner owner) => owner switch
    {
        LinkOwner.Cell => "a table cell",
        LinkOwner.Band => "a running band",
        _ => "a run of text",
    };

    // A link to an anchor is refused under PDF/UA-1, whose links need a Link structure element this
    // library does not write yet, and when the layout of this document knows the names its elements
    // hold and this is not one of them. With no layout in progress for this document the name is
    // left to the kernel document's save check.
    private void GuardAnchorLink(string name, LinkOwner owner)
    {
        if (_document.Conformance == VellumPdf.Document.PdfConformance.PdfUA1)
            throw new InvalidOperationException(
                $"A link in {Describe(owner)} to the anchor \"{name}\" is refused under a PDF/UA-1 claim, because it would be an " +
                "untagged link, which ISO 14289-1, 7.18.5 does not allow. Remove the link, or the claim.");
        if (ListLayoutRun.Current is { Navigation: { } plan } run
            && ReferenceEquals(run.Pdf, _document) && !plan.Declares(name))
            throw new InvalidOperationException(
                $"A link in {Describe(owner)} names the anchor \"{name}\", which no element of this document holds. " +
                "The layout has already run, so this document cannot be saved again; create a new " +
                "Document and give an element that anchor.");
    }

    /// <summary>
    /// Registers a document-outline (bookmark) entry pointing to the current page.
    /// <paramref name="layoutY"/> is the layout-space Y of the target position.
    /// </summary>
    /// <remarks>
    /// <paramref name="level"/> is not refused. Level 0 is a top-level entry. Any other level,
    /// negative included, nests under the most recent earlier entry one level up, and goes to the
    /// top level when there is none. For <see cref="int.MinValue"/> the level one up wraps to
    /// <see cref="int.MaxValue"/>.
    /// <para>A null <paramref name="title"/> and a non-finite <paramref name="layoutY"/> are
    /// accepted here and make the save throw when it writes the outline.</para>
    /// <para>Do not pass a null title or a non-finite position. A later major version will refuse
    /// both from this call. This overload is equivalent to
    /// <see cref="AddOutlineEntry(string, int, double, bool)"/> with <c>expanded</c> true, without
    /// that overload's refusals at the call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context was constructed with a null document. Also raised
    /// from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other save
    /// overloads, not from this call, when <paramref name="title"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this call, when this context was constructed with a non-null page,
    /// <paramref name="title"/> is not null, and the Y written for the destination is not finite.
    /// <paramref name="layoutY"/> reaches it through <see cref="ToPdfY"/>, so a finite one is
    /// refused when <see cref="PageBounds"/>'s height is not finite or the subtraction overflows.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised when this context was constructed with a null page and a non-null document, and
    /// <paramref name="title"/> is not null. It comes from the save of the
    /// <see cref="PdfDocument"/> this context was constructed with, not from this call.
    /// <c>ParamName</c> is <c>key</c>.
    /// </exception>
    public void AddOutlineEntry(string title, int level, double layoutY) =>
        AddOutlineEntryCore(title, level, layoutY, expanded: true);

    /// <summary>
    /// Registers a document-outline (bookmark) entry pointing to the current page, and says whether
    /// the entry is shown open or closed. <paramref name="layoutY"/> is the layout-space Y of the
    /// target position.
    /// </summary>
    /// <remarks>
    /// <b>A null title is refused.</b> This call throws <see cref="ArgumentNullException"/> for
    /// it, where the three-argument overload accepts it and fails later in the save.
    /// <b>A position that does not map to a finite destination is refused</b> the same way, with
    /// <see cref="ArgumentOutOfRangeException"/>, because a non-finite <c>/XYZ</c> operand is not a
    /// valid PDF number.
    /// <para><paramref name="level"/> is not refused. Level 0 is a top-level entry. Any other
    /// level, negative included, nests under the most recent earlier entry at exactly one level
    /// less, and goes to the top level when there is none. For <see cref="int.MinValue"/> the level
    /// one less wraps to <see cref="int.MaxValue"/>.</para>
    /// <para><paramref name="expanded"/> is not refused. <see langword="false"/> writes a negative
    /// <c>/Count</c> (ISO 32000-2:2020, 12.3.3, Table 151), but only on an entry that has
    /// children, and the value is not written otherwise. An entry has children when a later entry
    /// at its level + 1 finds it as the most recent earlier entry at its level, and level 0 never
    /// has a parent. So an entry at level -1 never has children: the entries after it at level 0
    /// are top-level.</para>
    /// <para>These refusals are raised from the <c>Draw</c> of the renderer that calls this
    /// method, which <see cref="VellumPdf.Layout.Document"/> runs inside its save, so a throw
    /// leaves that document unable to be saved again.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from this call when <paramref name="title"/> is <see langword="null"/>. Also raised
    /// from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other save
    /// overloads, not from this call, when this context was constructed with a null page and a
    /// non-null document. It comes from the save of the <see cref="PdfDocument"/> this context was
    /// constructed with. <c>ParamName</c> is <c>key</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this call when the Y written for the destination, <paramref name="layoutY"/>
    /// passed through <see cref="ToPdfY"/>, is not finite. A finite <paramref name="layoutY"/> is
    /// refused when <see cref="PageBounds"/>'s height is not finite or the subtraction overflows.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context was constructed with a null document.
    /// </exception>
    public void AddOutlineEntry(string title, int level, double layoutY, bool expanded)
    {
        ArgumentNullException.ThrowIfNull(title);
        if (!double.IsFinite(ToPdfY(layoutY)))
            throw new ArgumentOutOfRangeException(
                nameof(layoutY), layoutY, "The destination's Y is not finite.");
        AddOutlineEntryCore(title, level, layoutY, expanded);
    }

    internal void AddOutlineEntryCore(string title, int level, double layoutY, bool expanded)
    {
        var pdfY = ToPdfY(layoutY);
        _document.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = title,
            DestPage = _page,
            DestLeft = 0,
            DestTop = pdfY,
            Level = level,
            IsExpanded = expanded,
        });
    }

    /// <summary>
    /// Registers a structure element with the document's structure tree.
    /// The element's <see cref="PdfStructElem.Page"/> is automatically set to the current page.
    /// </summary>
    /// <remarks>
    /// Sets <paramref name="elem"/>.Page and hands the element to the document, which keeps it only
    /// when <see cref="Tagged"/> is true and otherwise discards it. A null
    /// <paramref name="elem"/> throws from this call whether or not the document is tagged.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when <paramref name="elem"/> is <see langword="null"/>, or when this
    /// context was constructed with a null document.
    /// </exception>
    public void RegisterStructElem(PdfStructElem elem)
    {
        elem.Page = _page;
        _document.RegisterStructElem(elem);
    }

    /// <summary>
    /// Registers a top-level structure element (e.g. Table, L) that contains nested
    /// child struct elems. The leaf descendants already have their Page set by
    /// <see cref="StampStructElemPage"/> calls; this method only adds the root to the tree.
    /// Only has an effect when <see cref="Tagged"/> is true.
    /// </summary>
    /// <remarks>
    /// When <see cref="Tagged"/> is false this returns without registering, so a null
    /// <paramref name="root"/> is accepted. When tagged, a null root is stored, and the save throws
    /// while it builds the structure tree.
    /// <para>Do not pass null. A later major version will throw <see cref="ArgumentNullException"/>
    /// from this call.</para>
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context was constructed with a null document, because
    /// <see cref="Tagged"/> reads it. Also raised from
    /// <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other save overloads,
    /// not from this call, when the document is tagged and <paramref name="root"/> is
    /// <see langword="null"/>.
    /// </exception>
    public void RegisterStructElemTree(PdfStructElem root)
    {
        if (Tagged)
            _document.RegisterStructElem(root);
    }

    /// <summary>
    /// Stamps <see cref="PdfStructElem.Page"/> on a leaf struct element (Mcid &gt;= 0)
    /// to the current page, without registering it as a top-level element.
    /// Used by table/list renderers to fill in page references on child elems.
    /// Only has an effect when <see cref="Tagged"/> is true.
    /// </summary>
    /// <remarks>
    /// When <see cref="Tagged"/> is false this returns without writing, so a null
    /// <paramref name="elem"/> is accepted. When tagged, a null <paramref name="elem"/> throws
    /// from this call.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when the document is tagged and <paramref name="elem"/> is
    /// <see langword="null"/>, or when this context was constructed with a null document, because
    /// <see cref="Tagged"/> reads it.
    /// </exception>
    public void StampStructElemPage(PdfStructElem elem)
    {
        if (Tagged)
            elem.Page = _page;
    }
}

/// <summary>
/// What a link annotation is drawn for. The registration takes it so the tagging of a link (#550)
/// can attach a Link structure element and its quad points to the right owner.
/// </summary>
internal enum LinkOwner
{
    /// <summary>A run of text, in a paragraph, heading, list item or cell.</summary>
    Run,

    /// <summary>A table cell's own style, over a line of a string cell.</summary>
    Cell,

    /// <summary>A running band.</summary>
    Band,
}
