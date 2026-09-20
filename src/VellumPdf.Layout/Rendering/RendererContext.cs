// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Images;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Per-page rendering context that manages resource registration
/// (XObjects, embedded fonts) on the current page.
///
/// Images are registered on both the <see cref="PdfDocument"/> (so the kernel
/// allocates indirect objects for them during <c>Save</c>) and on the page's
/// resource dictionary so content streams can reference them by name.
/// </summary>
/// <remarks>
/// The document creates one per page. The constructor checks nothing; each method says what a
/// null argument does.
/// </remarks>
public sealed class RendererContext
{
    private readonly PdfPage _page;
    private readonly PdfDocument _document;
    private readonly Dictionary<PdfImageXObject, string> _imageNames = new();
    private int _imageCounter;

    /// <summary>Creates a rendering context bound to the given page and its owning document.</summary>
    /// <remarks>
    /// Nothing is checked. A null <paramref name="page"/> or <paramref name="document"/> makes the
    /// methods throw as their own exception tags describe. A <paramref name="page"/> that belongs
    /// to a different document is accepted. An image registered on it is left out of the saved
    /// file. An embedded font registered on it is recorded for no page, though the font file is
    /// written anyway, as a registered font is even when unused.
    /// <para>A second context on the same page names its images from <c>Im1</c> again. The page's
    /// resource entry for a shared name keeps the image registered last under it, so the image
    /// registered first is written but not drawn.</para>
    /// <para>Do not pass null, a page from another document, or a page another context already
    /// draws on. A later major version will throw from this constructor.</para>
    /// </remarks>
    public RendererContext(PdfPage page, PdfDocument document)
    {
        _page = page;
        _document = document;
    }

    /// <summary>
    /// Registers an Image XObject on the current page and returns its resource name. Deduplicates:
    /// the same object instance always gets the same name from this context.
    /// </summary>
    /// <remarks>
    /// A null <paramref name="image"/> throws <see cref="ArgumentNullException"/> from this
    /// call (<c>ParamName</c> is <c>key</c>).
    /// <para>The name is recorded before the image is registered with the document. If that
    /// registration throws, a second call with the same image returns the name without
    /// registering the image.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from this call when <paramref name="image"/> is <see langword="null"/>, or when this
    /// context was constructed with a null page and a non-null document. <c>ParamName</c> is
    /// <c>key</c> in both cases.
    /// </exception>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when this context was constructed with a null document and
    /// <paramref name="image"/> is not <see langword="null"/>.
    /// </exception>
    public string RegisterImageXObject(PdfImageXObject image)
    {
        if (_imageNames.TryGetValue(image, out var name)) return name;
        name = $"Im{++_imageCounter}";
        _imageNames[image] = name;
        // Register with the document so the Save path writes the indirect object
        // and registers the resource on the page.
        _document.RegisterImageXObject(_page, image, name);
        return name;
    }

    /// <summary>
    /// Records that the current page uses the given embedded TrueType font.
    /// Idempotent: safe to call on every draw call for the same font.
    /// </summary>
    /// <remarks>
    /// A handle from a different document is accepted; see
    /// <see cref="VellumPdf.Layout.Core.FontReference.FontReference(EmbeddedFontHandle)"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// Raised from this call when <paramref name="handle"/> is <see langword="null"/> and this
    /// context was constructed with a non-null page, or when this context was constructed with a
    /// null document.
    /// </exception>
    /// <exception cref="ArgumentNullException">
    /// Raised from this call when this context was constructed with a null page and a non-null
    /// document, whether or not <paramref name="handle"/> is null. <c>ParamName</c> is <c>key</c>.
    /// </exception>
    public void RegisterEmbeddedFontUsage(EmbeddedFontHandle handle) =>
        _document.RegisterEmbeddedFontUsage(_page, handle);
}
