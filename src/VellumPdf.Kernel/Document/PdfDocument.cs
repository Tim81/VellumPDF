// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.IO;

namespace VellumPdf.Document;

/// <summary>
/// Top-level document model. Owns pages, manages the object graph, and
/// serialises a complete single-revision PDF file.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private readonly List<PdfPage> _pages = [];
    private readonly Dictionary<Standard14, Fonts.PdfFontResource> _fontCache = new();

    // Per-page image registrations: page → list of (image, resourceName)
    private readonly Dictionary<PdfPage, List<(PdfImageXObject Image, string Name)>> _pageImages = new();

    // Embedded TrueType fonts: ordered list of known handles
    private readonly List<EmbeddedFontHandle> _embeddedFonts = [];

    // Per-page embedded font usage: page → set of handle resource names
    private readonly Dictionary<PdfPage, HashSet<string>> _pageEmbeddedFonts = new();

    private int _fontCounter;
    private int _ttFontCounter;
    private bool _disposed;

    public PdfDocumentInfo Info { get; } = new();

    /// <summary>Default page size for new pages. Defaults to A4.</summary>
    public PdfRectangle DefaultPageSize { get; set; } = PageSize.A4;

    public PdfPage AddPage() => AddPage(DefaultPageSize);

    public PdfPage AddPage(PdfRectangle size)
    {
        var page = new PdfPage(size);
        _pages.Add(page);
        return page;
    }

    public IReadOnlyList<PdfPage> Pages => _pages;

    /// <summary>
    /// Returns a <see cref="PdfFontResource"/> for a Standard-14 font, creating
    /// and caching one with an auto-assigned resource name (F1, F2, …) on first call.
    /// </summary>
    public Fonts.PdfFontResource UseFont(Standard14 font)
    {
        if (!_fontCache.TryGetValue(font, out var res))
        {
            res = new Fonts.PdfFontResource(font, $"F{++_fontCounter}");
            _fontCache[font] = res;
        }
        return res;
    }

    /// <summary>
    /// Registers a TrueType font for embedding and returns a handle.
    /// The handle's <see cref="EmbeddedFontHandle.ResourceName"/> is the PDF resource name
    /// (e.g. "TT1"). Call <see cref="RegisterEmbeddedFontUsage"/> during drawing to record
    /// per-page usage so <see cref="Save"/> wires each page's resource dictionary.
    /// </summary>
    public EmbeddedFontHandle UseTrueTypeFont(byte[] fontData)
    {
        var resourceName = $"TT{++_ttFontCounter}";
        var embedder = new TrueTypeFontEmbedder(fontData, resourceName);
        var handle = new EmbeddedFontHandle(embedder);
        _embeddedFonts.Add(handle);
        return handle;
    }

    /// <summary>
    /// Records that <paramref name="page"/> uses the embedded font identified by
    /// <paramref name="handle"/>. Called by the layout engine during the draw phase
    /// so that <see cref="Save"/> can register the font reference on the correct pages.
    /// </summary>
    public void RegisterEmbeddedFontUsage(PdfPage page, EmbeddedFontHandle handle)
    {
        if (!_pageEmbeddedFonts.TryGetValue(page, out var set))
        {
            set = [];
            _pageEmbeddedFonts[page] = set;
        }
        set.Add(handle.ResourceName);
    }

    /// <summary>
    /// Registers an image XObject on the given page, returning the resource name.
    /// The image stream (and its SMask if present) are allocated in the object
    /// registry and written during <see cref="Save"/>.
    /// </summary>
    public string RegisterImageXObject(PdfPage page, PdfImageXObject image, string resourceName)
    {
        if (!_pageImages.TryGetValue(page, out var list))
        {
            list = [];
            _pageImages[page] = list;
        }
        list.Add((image, resourceName));
        return resourceName;
    }

    /// <summary>Writes a complete PDF file to <paramref name="destination"/>.</summary>
    public void Save(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        var writer = new PdfWriter(destination);
        var xref = new CrossReferenceBuilder();
        var registry = new PdfObjectRegistry();

        // PDF header — "%PDF-2.0\n" + binary comment with raw bytes E2 E3 CF D3.
        writer.WriteAscii("%PDF-2.0\n%"u8);
        writer.WriteRaw([0xE2, 0xE3, 0xCF, 0xD3]);
        writer.WriteAscii("\n"u8);

        // ── Pre-allocate references for page-tree and catalog (forward refs) ──

        // Reserves object numbers in a single pass so each page dict can reference
        // the page-tree before it is written.

        // Content streams: one per page
        var pageContentRefs = new PdfIndirectReference[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
            pageContentRefs[i] = registry.Reserve();

        // Page dict refs
        var pageDictRefs = new PdfIndirectReference[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
            pageDictRefs[i] = registry.Reserve();

        // Page tree ref (reserved now so page dicts can reference it)
        var pageTreeRef = registry.Reserve();

        // Info dict ref
        var infoRef = registry.Reserve();

        // Catalog ref
        var catalogRef = registry.Reserve();

        // ── Fill content stream values ─────────────────────────────────────
        for (var i = 0; i < _pages.Count; i++)
        {
            var content = _pages[i].ContentBytes ?? [];
            registry.SetValue(pageContentRefs[i], new PdfStream(content));
        }

        // ── Register image XObjects for each page ──────────────────────────
        foreach (var page in _pages)
        {
            if (!_pageImages.TryGetValue(page, out var images)) continue;
            foreach (var (img, name) in images)
            {
                // Allocate SMask first (if any) so its ref is known when building image stream
                PdfIndirectReference? sMaskRef = null;
                if (img.SMask is not null)
                {
                    var sMaskObjRef = registry.Reserve();
                    var sMaskStream = img.SMask;
                    // Set SMask image dict fields
                    sMaskStream.Dictionary
                        .Set(PdfName.Type, new PdfName("XObject"))
                        .Set(PdfName.Subtype, new PdfName("Image"))
                        .Set(new PdfName("Width"), new PdfInteger(img.Width))
                        .Set(new PdfName("Height"), new PdfInteger(img.Height))
                        .Set(new PdfName("ColorSpace"), new PdfName("DeviceGray"))
                        .Set(new PdfName("BitsPerComponent"), new PdfInteger(8));
                    registry.SetValue(sMaskObjRef, sMaskStream);
                    sMaskRef = sMaskObjRef;
                }

                // Allocate and set image stream
                var imgObjRef = registry.Reserve();
                var imgStream = img.BuildStreamWithSMask(sMaskRef);
                registry.SetValue(imgObjRef, imgStream);
                page.RegisterXObject(name, imgObjRef);
            }
        }

        // ── Embed TrueType fonts (Type0/CIDFontType2) ─────────────────────
        // Build the full font object graph for each embedded font, then register
        // the Type0 reference on every page that used the font.
        //
        // Object graph per font (ref chain):
        //   Type0 dict → DescendantFonts array → CIDFontType2 dict → FontDescriptor → FontFile2
        //   Type0 dict → ToUnicode stream
        foreach (var handle in _embeddedFonts)
        {
            var emb = handle.Embedder;

            // Reserve all indirect references first (forward-reference pattern)
            var fontFileRef = registry.Reserve();       // FontFile2 stream
            var descriptorRef = registry.Reserve();      // FontDescriptor dict
            var cidFontRef = registry.Reserve();         // CIDFontType2 dict
            var descendantArrayRef = registry.Reserve(); // one-element PdfArray [cidFontRef]
            var toUnicodeRef = registry.Reserve();       // ToUnicode CMap stream
            var type0Ref = registry.Reserve();           // Type0 font dict

            // Set values (subset is built here — all glyphs have been registered by Draw)
            registry.SetValue(fontFileRef, emb.BuildFontFileStream());
            registry.SetValue(descriptorRef, emb.BuildFontDescriptor(fontFileRef));
            registry.SetValue(cidFontRef, emb.BuildCidFontDictionary(descriptorRef));

            // DescendantFonts is an array containing the CIDFont ref
            registry.SetValue(descendantArrayRef, new PdfArray([cidFontRef]));

            registry.SetValue(toUnicodeRef, emb.BuildToUnicodeCMap());
            registry.SetValue(type0Ref, emb.BuildFontDictionary(descendantArrayRef, toUnicodeRef));

            // Register on each page that used this font
            foreach (var page in _pages)
            {
                if (!_pageEmbeddedFonts.TryGetValue(page, out var usedNames)) continue;
                if (!usedNames.Contains(handle.ResourceName)) continue;
                page.RegisterFontRef(handle.ResourceName, type0Ref);
            }
        }

        // ── Fill page dict values ──────────────────────────────────────────
        for (var i = 0; i < _pages.Count; i++)
        {
            var dict = _pages[i].BuildDictionary(pageTreeRef, pageContentRefs[i]);
            registry.SetValue(pageDictRefs[i], dict);
        }

        // ── Fill page tree value ───────────────────────────────────────────
        var kids = new PdfArray(pageDictRefs.Cast<PdfObject>());
        var pageTree = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, kids)
            .Set(PdfName.Count, _pages.Count);
        registry.SetValue(pageTreeRef, pageTree);

        // ── Fill info dict value ───────────────────────────────────────────
        registry.SetValue(infoRef, Info.BuildDictionary());

        // ── Fill catalog value ─────────────────────────────────────────────
        var catalog = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef);
        registry.SetValue(catalogRef, catalog);

        // ── Write all objects in object-number order ───────────────────────
        registry.WriteAll(writer, xref);

        // ── Cross-reference table + trailer ───────────────────────────────
        xref.WriteXrefAndTrailer(writer, catalogRef, infoRef);
        writer.Flush();
    }

    public void Dispose() => _disposed = true;
}
