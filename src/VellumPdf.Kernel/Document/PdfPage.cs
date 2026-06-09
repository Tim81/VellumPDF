// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Document;

/// <summary>
/// A single PDF page. Owns a content stream and a Resources dictionary.
/// The /Annots array (annotation/AcroForm seam) is populated on demand.
/// </summary>
public sealed class PdfPage
{
    private readonly PdfDictionary _fontResources = new();
    private readonly PdfDictionary _xObjectResources = new();
    private readonly PdfDictionary _extGStateResources = new();
    private readonly PdfDictionary _shadingResources = new();
    private readonly PdfArray _annots = new();
    private bool _hasAnnots;
    private bool _hasExtGState;
    private bool _hasShading;

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

    // Called by PdfCanvas.Finish() — font dicts are inline for Standard-14 (no embedding needed).
    internal void RegisterFont(string resourceName, PdfDictionary fontDict) =>
        _fontResources.Set(new PdfName(resourceName), fontDict);

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

    /// <summary>Adds an annotation reference (annotation/AcroForm seam).</summary>
    public void AddAnnotation(PdfIndirectReference annotRef)
    {
        _annots.Add(annotRef);
        _hasAnnots = true;
    }

    internal PdfDictionary BuildDictionary(
        PdfIndirectReference parentRef,
        PdfIndirectReference contentRef)
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

        var pageDict = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page)
            .Set(PdfName.Parent, parentRef)
            .Set(PdfName.MediaBox, MediaBox.ToArray())
            .Set(PdfName.Resources, resources)
            .Set(PdfName.Contents, contentRef);

        if (Rotate != 0)
            pageDict.Set(PdfName.Rotate, Rotate);

        if (_hasAnnots)
            pageDict.Set(PdfName.Annots, _annots);

        if (StructParentsKey >= 0)
            pageDict.Set(new PdfName("StructParents"), new PdfInteger(StructParentsKey));

        return pageDict;
    }
}
