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
/// it writes, not the value passed: a Y reaches it through the flip above, so a finite one is
/// refused when <see cref="PageBounds"/>'s height is not finite or the subtraction overflows, and
/// an X plus a width is refused when that sum does. See <see cref="AddUriLinkAnnotation"/> and
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
    /// Nothing is checked, and every argument is stored. A null <paramref name="page"/> leaves each
    /// structure element this type registers without a page, and the save writes it without a
    /// <c>/Pg</c> entry and without an exception. Any null, the page included, can also surface
    /// later, from the first member that uses it; see the exception tags. A <paramref name="page"/>
    /// that belongs to a different document is accepted. A link annotation registered on it is left
    /// out of the saved file, so a box the save would otherwise refuse is written nowhere and
    /// raises nothing; an outline entry is written anyway, with a null destination page. The
    /// document constructs this type itself and never passes null; construct one yourself only to
    /// test a renderer.
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
    /// document's save after a call to <see cref="AddOutlineEntry"/> that passed a non-null title.
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
    /// <see cref="RendererContext"/> is. A handle from a different document is accepted;
    /// <see cref="FontReference(EmbeddedFontHandle)"/> says what the page then shows.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context's <see cref="RendererContext"/> is
    /// <see langword="null"/> or was constructed with a null document, or when
    /// <paramref name="handle"/> is null and that renderer context was constructed with a non-null
    /// page.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised from this call when this context's <see cref="RendererContext"/> was constructed with
    /// a null page and a non-null document, whether or not <paramref name="handle"/> is null.
    /// <c>ParamName</c> is <c>key</c>.
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
    /// overflows. A save refuses the Y it writes, so this is where a finite argument is refused;
    /// see <see cref="AddOutlineEntry"/>.
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
    /// <para>A non-finite coordinate in <paramref name="box"/>, or finite ones whose sum overflows,
    /// is accepted here. The save throws when it writes the annotation's rectangle.</para>
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
    /// both from this call.</para>
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
    /// Raised when the <see cref="PdfDocument"/> this context was constructed with is saved, not
    /// from this call, when this context was constructed with a null page and a non-null document
    /// and <paramref name="title"/> is not null. <c>ParamName</c> is <c>key</c>.
    /// </exception>
    public void AddOutlineEntry(string title, int level, double layoutY)
    {
        var pdfY = ToPdfY(layoutY);
        _document.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = title,
            DestPage = _page,
            DestLeft = 0,
            DestTop = pdfY,
            Level = level,
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
