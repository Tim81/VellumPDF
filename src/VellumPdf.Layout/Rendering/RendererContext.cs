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
public sealed class RendererContext
{
    private readonly PdfPage _page;
    private readonly PdfDocument _document;
    private readonly Dictionary<PdfImageXObject, string> _imageNames = new();
    private int _imageCounter;

    /// <summary>Creates a rendering context bound to the given page and its owning document.</summary>
    public RendererContext(PdfPage page, PdfDocument document)
    {
        _page = page;
        _document = document;
    }

    /// <summary>
    /// Registers an Image XObject on the current page and returns its resource name.
    /// Deduplicates: the same object instance always gets the same name.
    /// </summary>
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
    /// Idempotent — safe to call on every draw call for the same font.
    /// </summary>
    public void RegisterEmbeddedFontUsage(EmbeddedFontHandle handle) =>
        _document.RegisterEmbeddedFontUsage(_page, handle);
}
