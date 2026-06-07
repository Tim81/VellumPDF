// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Per-page rendering context that manages resource registration
/// (XObjects, embedded fonts) on the current page.
/// </summary>
public sealed class RendererContext
{
    private readonly PdfPage _page;
    private readonly Dictionary<PdfImageXObject, string> _imageNames = new();
    private int _imageCounter;

    public RendererContext(PdfPage page) => _page = page;

    /// <summary>
    /// Registers an Image XObject on the current page and returns its resource name.
    /// Deduplicates: the same object instance always gets the same name.
    /// </summary>
    public string RegisterImageXObject(PdfImageXObject image)
    {
        if (_imageNames.TryGetValue(image, out var name)) return name;
        name = $"Im{++_imageCounter}";
        _imageNames[image] = name;
        // Image XObjects need indirect references from the document writer;
        // for Phase 3, we embed the stream inline (Phase 4 will add deduplication).
        // The page resource registration happens via PdfPage.RegisterXObject,
        // but we need to write the stream and get a ref first — deferred to DocumentRenderer.
        return name;
    }
}
