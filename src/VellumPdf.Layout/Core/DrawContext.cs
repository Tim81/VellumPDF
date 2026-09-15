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
/// Coordinates are not validated. A box or Y that is off the page is written into
/// annotations and outline destinations as given.
/// </remarks>
public sealed class DrawContext
{
    /// <summary>The content-stream canvas for the current page.</summary>
    /// <remarks>The canvas this draw is writing to. Not null after construction.</remarks>
    public PdfCanvas Canvas { get; }

    /// <summary>The full page bounds in layout space (Y-down).</summary>
    /// <remarks>Used by <see cref="ToPdfY"/> and <see cref="ToPdfRect"/>. Not validated here.</remarks>
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
    /// Forwards <see cref="Document.Tagged"/>. Structure registration still runs when this is
    /// false; whether the kernel keeps the element is a tagged-document question.
    /// </remarks>
    public bool Tagged => _document.Tagged;

    /// <summary>Creates a draw context bound to the current page, its canvas, and the owning document.</summary>
    /// <remarks>
    /// Arguments are stored as given. A null canvas or document is not refused here; the throw
    /// is from the first member that dereferences it.
    /// </remarks>
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
    /// Every <see cref="Standard14"/> value is accepted. This does not embed a file.
    /// </remarks>
    public PdfFontResource GetFont(Standard14 font) => _document.UseFont(font);

    /// <summary>
    /// Records that the current page uses the given embedded TrueType font handle
    /// and returns its PDF resource name so the canvas can select it.
    /// </summary>
    /// <remarks>
    /// A null handle throws <see cref="NullReferenceException"/> from
    /// <c>handle.ResourceName</c>.
    /// </remarks>
    public string UseEmbeddedFont(EmbeddedFontHandle handle)
    {
        RendererContext.RegisterEmbeddedFontUsage(handle);
        return handle.ResourceName;
    }

    /// <summary>Converts a layout-space Y coordinate to PDF user-space Y.</summary>
    /// <remarks>
    /// Not refused. A non-finite <paramref name="layoutY"/> yields a non-finite PDF Y.
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
    /// <paramref name="uri"/> is not validated. Empty, <c>not a uri</c>, and
    /// <c>javascript:alert(1)</c> are written into <c>/URI</c> as given, the same as
    /// <see cref="TextStyle.LinkUri"/>.
    /// </remarks>
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
    /// <paramref name="level"/> is not refused. A negative level reaches the outline builder as
    /// given, the same as <see cref="VellumPdf.Layout.Elements.Heading.Level"/>.
    /// </remarks>
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
    /// Only has an effect when <see cref="Tagged"/> is true.
    /// The element's <see cref="PdfStructElem.Page"/> is automatically set to the current page.
    /// </summary>
    /// <remarks>
    /// When <see cref="Tagged"/> is false this still sets <paramref name="elem"/>.Page and
    /// forwards the element. Whether the kernel keeps it is a tagged-document question, not a
    /// throw.
    /// </remarks>
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
    /// When <see cref="Tagged"/> is false this returns without registering. A null
    /// <paramref name="root"/> is not checked when untagged; when tagged it is forwarded.
    /// </remarks>
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
    /// When <see cref="Tagged"/> is false this returns without writing. A null
    /// <paramref name="elem"/> then does not throw; when tagged it is dereferenced.
    /// </remarks>
    public void StampStructElemPage(PdfStructElem elem)
    {
        if (Tagged)
            elem.Page = _page;
    }
}
