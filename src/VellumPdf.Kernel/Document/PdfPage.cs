// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Fonts;

namespace VellumPdf.Document;

/// <summary>
/// A single PDF page. Owns a content stream and a Resources dictionary.
/// The /Annots array (annotation/AcroForm seam) is populated on demand.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfDictionary _fontResources = new();
    private readonly List<(string ResourceName, PdfFontResource Font)> _pendingStandard14Fonts = [];
    private readonly PdfDictionary _xObjectResources = new();
    private readonly PdfDictionary _extGStateResources = new();
    private readonly PdfDictionary _shadingResources = new();
    private readonly PdfDictionary _colorSpaceResources = new();
    private readonly PdfArray _annots = new();
    private bool _hasAnnots;
    private bool _hasExtGState;
    private bool _hasShading;
    private bool _hasColorSpace;

    /// <summary>
    /// /StructParents integer key for the ParentTree. Set by <see cref="PdfDocument.Save"/>
    /// when tagged content is present on this page. -1 means "not set".
    /// </summary>
    internal int StructParentsKey { get; set; } = -1;

    /// <summary>The page's media box (its physical dimensions in PDF user space).</summary>
    public PdfRectangle MediaBox { get; }

    /// <summary>Page rotation in degrees clockwise (must be a multiple of 90). Default 0.</summary>
    public int Rotate { get; set; } = 0;

    /// <summary>Creates a page with the given media box.</summary>
    public PdfPage(PdfRectangle mediaBox) => MediaBox = mediaBox;

    /// <summary>Raw PDF content stream bytes. Set by <see cref="Canvas.PdfCanvas.Finish"/>.</summary>
    public byte[]? ContentBytes { get; set; }

    // Called by PdfCanvas.Finish() — the font dict itself is materialised as a shared
    // indirect object (once per face, document-wide) by PdfDocument.Save() before the
    // page dictionary is built; see RegisterFontRef and PendingStandard14Fonts below.
    internal void RegisterStandard14FontUsage(string resourceName, PdfFontResource font) =>
        _pendingStandard14Fonts.Add((resourceName, font));

    // Consumed by PdfDocument.Save() to allocate the shared font objects and wire them
    // in via RegisterFontRef.
    internal IReadOnlyList<(string ResourceName, PdfFontResource Font)> PendingStandard14Fonts =>
        _pendingStandard14Fonts;

    // Called when embedding custom fonts — the font is its own indirect object.
    internal void RegisterFontRef(string resourceName, PdfIndirectReference fontRef) =>
        _fontResources.Set(new PdfName(resourceName), fontRef);

    internal void RegisterXObject(string resourceName, PdfIndirectReference xObjRef) =>
        _xObjectResources.Set(new PdfName(resourceName), xObjRef);

    /// <summary>
    /// Registers an inline /ExtGState entry (e.g. <c>&lt;&lt; /ca 0.5 &gt;&gt;</c>).
    /// Called by <see cref="Canvas.PdfCanvas"/> when setting alpha.
    /// </summary>
    internal void RegisterExtGState(string resourceName, PdfDictionary stateDict)
    {
        _extGStateResources.Set(new PdfName(resourceName), stateDict);
        _hasExtGState = true;
    }

    /// <summary>
    /// Registers an inline /Shading entry for a gradient shading dictionary.
    /// Called by <see cref="Canvas.PdfCanvas"/> when painting a gradient.
    /// </summary>
    internal void RegisterShading(string resourceName, PdfDictionary shadingDict)
    {
        _shadingResources.Set(new PdfName(resourceName), shadingDict);
        _hasShading = true;
    }

    /// <summary>
    /// Registers a /ColorSpace resource entry for an ICCBased or other colour space.
    /// Called by <see cref="PdfDocument"/> during materialisation.
    /// </summary>
    internal void RegisterColorSpace(string resourceName, PdfObject colorSpace)
    {
        _colorSpaceResources.Set(new PdfName(resourceName), colorSpace);
        _hasColorSpace = true;
    }

    /// <summary>Adds an annotation reference (annotation/AcroForm seam).</summary>
    public void AddAnnotation(PdfIndirectReference annotRef)
    {
        _annots.Add(annotRef);
        _hasAnnots = true;
    }

    internal PdfDictionary BuildDictionary(
        PdfIndirectReference parentRef,
        PdfIndirectReference contentRef,
        bool structureTabOrder = false)
    {
        var procSet = new PdfArray([PdfName.PDF, PdfName.Text, PdfName.ImageB, PdfName.ImageC, PdfName.ImageI]);
        var resources = new PdfDictionary()
            .Set(PdfName.ProcSet, procSet)
            .Set(PdfName.Font, _fontResources)
            .Set(PdfName.XObject, _xObjectResources);

        if (_hasExtGState)
            resources.Set(PdfName.ExtGState, _extGStateResources);

        if (_hasShading)
            resources.Set(PdfName.Shading, _shadingResources);

        if (_hasColorSpace)
            resources.Set(PdfName.ColorSpace, _colorSpaceResources);

        var pageDict = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page)
            .Set(PdfName.Parent, parentRef)
            .Set(PdfName.MediaBox, MediaBox.ToArray())
            .Set(PdfName.Resources, resources)
            .Set(PdfName.Contents, contentRef);

        if (Rotate != 0)
            pageDict.Set(PdfName.Rotate, Rotate);

        if (_hasAnnots)
        {
            pageDict.Set(PdfName.Annots, _annots);

            // PDF/UA-1 (ISO 14289-1 §7.18.3): a page with annotations must declare a tab
            // order; /Tabs /S means "follow the structure tree" — the accessible choice.
            if (structureTabOrder)
                pageDict.Set(new PdfName("Tabs"), new PdfName("S"));
        }

        if (StructParentsKey >= 0)
            pageDict.Set(new PdfName("StructParents"), new PdfInteger(StructParentsKey));

        return pageDict;
    }
}
