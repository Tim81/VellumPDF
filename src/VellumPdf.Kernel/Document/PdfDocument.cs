// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using VellumPdf.Annotations;
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

    // Dedup cache: font content hash → handle, so loading the same font twice shares one subset.
    private readonly Dictionary<string, EmbeddedFontHandle> _embeddedFontByHash = new();

    // Per-page embedded font usage: page → set of handle resource names
    private readonly Dictionary<PdfPage, HashSet<string>> _pageEmbeddedFonts = new();

    // Per-page link annotations
    private readonly Dictionary<PdfPage, List<PdfLinkAnnotation>> _pageAnnotations = new();

    // Document outline (bookmark) entries, in insertion order
    private readonly List<PdfOutlineEntry> _outlineEntries = [];

    // Structure tree — populated by RegisterStructElem during layout/draw
    private readonly PdfStructureTree _structureTree = new();
    private bool _tagged;

    private int _fontCounter;
    private int _ttFontCounter;
    private bool _disposed;

    public PdfDocumentInfo Info { get; } = new();

    /// <summary>Default page size for new pages. Defaults to A4.</summary>
    public PdfRectangle DefaultPageSize { get; set; } = PageSize.A4;

    /// <summary>
    /// Optional fixed timestamp used for XMP CreateDate/ModifyDate and document ID computation.
    /// When null, <see cref="DateTimeOffset.UtcNow"/> at the time of the first <see cref="Save"/>
    /// call is used. Set to a fixed value for deterministic output.
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    /// <summary>
    /// Requested PDF/A conformance level.
    /// When non-<see cref="PdfConformance.None"/>:
    /// XMP pdfaid schema is included, /ID is written, and /MarkInfo /Marked true is set.
    /// PDF/A-2a additionally implies <see cref="Tagged"/> = true.
    /// </summary>
    public PdfConformance Conformance { get; set; } = PdfConformance.None;

    /// <summary>
    /// When true, a /StructTreeRoot is written and marked-content sequences
    /// around paragraphs and headings are registered as /StructElem objects.
    /// Default is false; set to true explicitly or implied by <see cref="PdfConformance.PdfA2a"/>.
    /// </summary>
    public bool Tagged
    {
        get => _tagged || Conformance == PdfConformance.PdfA2a;
        set => _tagged = value;
    }

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
        // Dedup by content so loading the same font twice shares one embedded subset.
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fontData));
        if (_embeddedFontByHash.TryGetValue(hash, out var existing))
            return existing;

        var resourceName = $"TT{++_ttFontCounter}";
        var embedder = new TrueTypeFontEmbedder(fontData, resourceName);
        var handle = new EmbeddedFontHandle(embedder);
        _embeddedFonts.Add(handle);
        _embeddedFontByHash[hash] = handle;
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

    /// <summary>
    /// Registers a /Link annotation on the given page.
    /// The annotation is written as an indirect object during <see cref="Save"/>.
    /// </summary>
    public void RegisterLinkAnnotation(PdfPage page, PdfLinkAnnotation annotation)
    {
        if (!_pageAnnotations.TryGetValue(page, out var list))
        {
            list = [];
            _pageAnnotations[page] = list;
        }
        list.Add(annotation);
    }

    /// <summary>
    /// Adds a bookmark entry that will be written into the document outline tree.
    /// Entries are rendered in the order they are added.
    /// </summary>
    public void AddOutlineEntry(PdfOutlineEntry entry) => _outlineEntries.Add(entry);

    /// <summary>
    /// Registers a structure element for the structure tree (PDF §14.7).
    /// Only has an effect when <see cref="Tagged"/> is true; ignored otherwise.
    /// </summary>
    public void RegisterStructElem(PdfStructElem elem)
    {
        if (Tagged)
            _structureTree.AddStructElem(elem);
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

        // ── Register image XObjects (deduplicated by image identity) ───────
        // The same image instance used on multiple pages is written once and shared.
        var imageRefs = new Dictionary<PdfImageXObject, PdfIndirectReference>(ReferenceEqualityComparer.Instance);
        foreach (var page in _pages)
        {
            if (!_pageImages.TryGetValue(page, out var images)) continue;
            foreach (var (img, name) in images)
            {
                if (!imageRefs.TryGetValue(img, out var imgObjRef))
                {
                    // Allocate SMask first (if any) so its ref is known when building the image stream.
                    PdfIndirectReference? sMaskRef = null;
                    if (img.SMask is not null)
                    {
                        var sMaskObjRef = registry.Reserve();
                        var sMaskStream = img.SMask;
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

                    imgObjRef = registry.Reserve();
                    registry.SetValue(imgObjRef, img.BuildStreamWithSMask(sMaskRef));
                    imageRefs[img] = imgObjRef;
                }
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
            var fontFileRef = registry.Reserve();   // FontFile2 stream
            var descriptorRef = registry.Reserve(); // FontDescriptor dict
            var cidFontRef = registry.Reserve();    // CIDFontType2 dict
            var toUnicodeRef = registry.Reserve();  // ToUnicode CMap stream
            var type0Ref = registry.Reserve();      // Type0 font dict

            // Set values (subset is built here — all glyphs have been registered by Draw)
            registry.SetValue(fontFileRef, emb.BuildFontFileStream());
            registry.SetValue(descriptorRef, emb.BuildFontDescriptor(fontFileRef));
            registry.SetValue(cidFontRef, emb.BuildCidFontDictionary(descriptorRef));

            // /DescendantFonts is written as an inline array inside the Type0 dict (PDF/A-compliant).
            registry.SetValue(toUnicodeRef, emb.BuildToUnicodeCMap());
            registry.SetValue(type0Ref, emb.BuildFontDictionary(cidFontRef, toUnicodeRef));

            // Register on each page that used this font
            foreach (var page in _pages)
            {
                if (!_pageEmbeddedFonts.TryGetValue(page, out var usedNames)) continue;
                if (!usedNames.Contains(handle.ResourceName)) continue;
                page.RegisterFontRef(handle.ResourceName, type0Ref);
            }
        }

        // ── Build page→ref lookup (used by annotations and outlines) ─────
        var pageRefMap = new Dictionary<PdfPage, PdfIndirectReference>(_pages.Count);
        for (var i = 0; i < _pages.Count; i++)
            pageRefMap[_pages[i]] = pageDictRefs[i];

        // ── Write link annotations as indirect objects ─────────────────────
        // For each page that has annotations, build each annotation dict as an
        // indirect object and call AddAnnotation so the page dict gets /Annots.
        foreach (var page in _pages)
        {
            if (!_pageAnnotations.TryGetValue(page, out var annots)) continue;
            foreach (var annot in annots)
            {
                PdfIndirectReference? destRef = annot.DestPage is not null
                    ? pageRefMap.GetValueOrDefault(annot.DestPage)
                    : null;
                var annotDict = annot.BuildDictionary(destRef);
                var annotRef = registry.Reserve();
                registry.SetValue(annotRef, annotDict);
                page.AddAnnotation(annotRef);
            }
        }

        // ── Build structure tree (tagged PDF) ─────────────────────────────
        // MUST happen before the page-dict build so that page.StructParentsKey is set
        // before BuildDictionary is called (otherwise /StructParents is never written).
        PdfIndirectReference? structTreeRootRef = null;
        if (Tagged && !_structureTree.IsEmpty)
        {
            structTreeRootRef = _structureTree.Build(registry, pageRefMap, out var pageStructParents);
            // Stamp /StructParents on each page that has tagged content.
            foreach (var (page, key) in pageStructParents)
                page.StructParentsKey = key;
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

        // ── Build XMP metadata stream ──────────────────────────────────────
        var ts = Timestamp ?? DateTimeOffset.UtcNow;
        var xmpBytes = XmpMetadataWriter.BuildPacket(Info, Conformance, ts);
        var metadataStream = new UncompressedPdfStream(xmpBytes);
        metadataStream.Dictionary
            .Set(PdfName.Type, new PdfName("Metadata"))
            .Set(PdfName.Subtype, new PdfName("XML"));
        var metadataRef = registry.Reserve();
        registry.SetValue(metadataRef, metadataStream);

        // ── Build document /ID (MD5 over title + producer + page count + timestamp) ─
        var documentId = ComputeDocumentId(ts);

        // ── Build outline tree (bookmarks) ────────────────────────────────
        PdfIndirectReference? outlinesRef = null;
        if (_outlineEntries.Count > 0)
            outlinesRef = BuildOutlineTree(registry, pageRefMap);

        // ── Fill catalog value ─────────────────────────────────────────────
        var catalog = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef)
            .Set(new PdfName("Metadata"), metadataRef);

        if (outlinesRef is not null)
        {
            catalog
                .Set(new PdfName("Outlines"), outlinesRef)
                .Set(new PdfName("PageMode"), new PdfName("UseOutlines"));
        }

        // /MarkInfo — required when Tagged or PDF/A conformance is requested
        var needsMarkInfo = Tagged || Conformance != PdfConformance.None;
        if (needsMarkInfo)
        {
            var markInfo = new PdfDictionary()
                .Set(new PdfName("Marked"), PdfBoolean.True);
            catalog.Set(new PdfName("MarkInfo"), markInfo);
        }

        if (structTreeRootRef is not null)
            catalog.Set(new PdfName("StructTreeRoot"), structTreeRootRef);

        registry.SetValue(catalogRef, catalog);

        // ── Write all objects in object-number order ───────────────────────
        registry.WriteAll(writer, xref);

        // ── Cross-reference table + trailer ───────────────────────────────
        xref.WriteXrefAndTrailer(writer, catalogRef, infoRef, documentId: documentId);
        writer.Flush();
    }

    /// <summary>
    /// Computes a 16-byte document identifier via MD5 over the document's
    /// identifying attributes (title, producer, page count, timestamp).
    /// Using MD5 for fingerprinting is explicitly recommended by ISO 32000-2 §14.4;
    /// this is NOT a security hash.
    /// </summary>
    private byte[] ComputeDocumentId(DateTimeOffset ts)
    {
        var sb = new StringBuilder();
        sb.Append(Info.Title ?? string.Empty);
        sb.Append('|');
        sb.Append(Info.Producer ?? "VellumPdf");
        sb.Append('|');
        sb.Append(_pages.Count);
        sb.Append('|');
        sb.Append(ts.ToUnixTimeMilliseconds());
        var input = Encoding.UTF8.GetBytes(sb.ToString());
        return MD5.HashData(input);
    }

    /// <summary>
    /// Builds the full /Outlines indirect object tree from the flat list of
    /// <see cref="_outlineEntries"/>, allocating all refs in <paramref name="registry"/>.
    /// Returns the /Outlines root ref.
    /// </summary>
    private PdfIndirectReference BuildOutlineTree(
        PdfObjectRegistry registry,
        Dictionary<PdfPage, PdfIndirectReference> pageRefMap)
    {
        // We model outline items at each level as a doubly-linked list.
        // Algorithm: walk entries in order; maintain a stack of the last open item
        // at each level so /Parent, /Prev, /Next can be wired.

        // Reserve refs for every item up front so we can set back-links.
        var itemRefs = new PdfIndirectReference[_outlineEntries.Count];
        for (var i = 0; i < _outlineEntries.Count; i++)
            itemRefs[i] = registry.Reserve();

        var outlinesRef = registry.Reserve();

        // We'll track, for each level, the ref of the last item at that level so
        // /Prev / /Next links work correctly without a second pass.
        // Stack: index = level, value = index into _outlineEntries of the last open entry at that level.
        var lastAtLevel = new Dictionary<int, int>(); // level → entry index of last item at that level
        var firstAtLevel = new Dictionary<int, int>(); // level → entry index of first item at that level

        // Children: each item that has children gets /First, /Last, /Count set.
        // We accumulate a list of direct children refs per parent entry index.
        var childrenOf = new Dictionary<int, List<int>>(); // parent entry index → list of child entry indices

        // Assign parents: each entry's parent is the last entry at (level-1),
        // or the root outlines dict if level == 0.
        var parentItemIndex = new int[_outlineEntries.Count]; // -1 means root
        for (var i = 0; i < _outlineEntries.Count; i++)
        {
            var level = _outlineEntries[i].Level;
            if (level == 0)
            {
                parentItemIndex[i] = -1; // root
            }
            else
            {
                // Find last entry at level-1
                var parentLevel = level - 1;
                parentItemIndex[i] = lastAtLevel.TryGetValue(parentLevel, out var pi) ? pi : -1;
            }

            // Register as child of parent
            var pid = parentItemIndex[i];
            if (!childrenOf.TryGetValue(pid, out var list))
            {
                list = [];
                childrenOf[pid] = list;
            }
            list.Add(i);

            lastAtLevel[level] = i;
            if (!firstAtLevel.ContainsKey(level))
                firstAtLevel[level] = i;
        }

        // Wire /Prev and /Next among siblings (entries that share the same parent).
        // Group siblings by parentItemIndex.
        var siblingsByParent = new Dictionary<int, List<int>>(); // parent → ordered sibling list
        for (var i = 0; i < _outlineEntries.Count; i++)
        {
            var pid = parentItemIndex[i];
            if (!siblingsByParent.TryGetValue(pid, out var siblings))
            {
                siblings = [];
                siblingsByParent[pid] = siblings;
            }
            siblings.Add(i);
        }

        // Now build each item dict.
        for (var i = 0; i < _outlineEntries.Count; i++)
        {
            var entry = _outlineEntries[i];
            var title = PdfLiteralString.FromUnicode(entry.Title);

            // /Dest [pageRef /XYZ left top null]
            pageRefMap.TryGetValue(entry.DestPage, out var destPageRef);
            var dest = new PdfArray([
                destPageRef ?? (PdfObject)PdfNull.Instance,
                new PdfName("XYZ"),
                new PdfReal(entry.DestLeft),
                new PdfReal(entry.DestTop),
                PdfNull.Instance,
            ]);

            // /Parent ref — either the outlines root or a parent item
            var pid = parentItemIndex[i];
            PdfObject parentRef = pid == -1 ? (PdfObject)outlinesRef : itemRefs[pid];

            var itemDict = new PdfDictionary()
                .Set(new PdfName("Title"), title)
                .Set(new PdfName("Parent"), parentRef)
                .Set(new PdfName("Dest"), dest);

            // /Prev and /Next from sibling list
            var siblings = siblingsByParent[pid];
            var sibIdx = siblings.IndexOf(i);
            if (sibIdx > 0)
                itemDict.Set(new PdfName("Prev"), itemRefs[siblings[sibIdx - 1]]);
            if (sibIdx < siblings.Count - 1)
                itemDict.Set(new PdfName("Next"), itemRefs[siblings[sibIdx + 1]]);

            // /First, /Last, /Count for items that have children.
            // /Count is the total number of ALL open descendants (recursive), per ISO 32000-2 §12.3.3.
            if (childrenOf.TryGetValue(i, out var myChildren) && myChildren.Count > 0)
            {
                itemDict
                    .Set(new PdfName("First"), itemRefs[myChildren[0]])
                    .Set(new PdfName("Last"), itemRefs[myChildren[^1]])
                    .Set(new PdfName("Count"), new PdfInteger(CountAllDescendants(i, childrenOf)));
            }

            registry.SetValue(itemRefs[i], itemDict);
        }

        // Build the /Outlines root dict.
        var rootChildren = siblingsByParent.TryGetValue(-1, out var rootSiblings) ? rootSiblings : [];
        // Root /Count = total open (visible) items = number of direct top-level children.
        var rootCountAll = rootChildren.Count;

        var outlinesDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Outlines"));

        if (rootChildren.Count > 0)
        {
            outlinesDict
                .Set(new PdfName("First"), itemRefs[rootChildren[0]])
                .Set(new PdfName("Last"), itemRefs[rootChildren[^1]])
                .Set(new PdfName("Count"), new PdfInteger(rootCountAll));
        }

        registry.SetValue(outlinesRef, outlinesDict);
        return outlinesRef;
    }

    /// <summary>
    /// Recursively counts all descendants of outline item <paramref name="itemIndex"/>
    /// (i.e. children + their children + …) to produce the correct ISO 32000-2 §12.3.3 /Count.
    /// </summary>
    private static int CountAllDescendants(int itemIndex, Dictionary<int, List<int>> childrenOf)
    {
        if (!childrenOf.TryGetValue(itemIndex, out var children) || children.Count == 0)
            return 0;
        var total = children.Count;
        foreach (var child in children)
            total += CountAllDescendants(child, childrenOf);
        return total;
    }

    public void Dispose() => _disposed = true;
}
