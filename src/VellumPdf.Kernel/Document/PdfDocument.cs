// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Core;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Forms;
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

    // AcroForm fields registered via AddTextField / AddCheckBox / AddChoiceField
    private readonly List<PdfFormField> _formFields = [];

    // Document outline (bookmark) entries, in insertion order
    private readonly List<PdfOutlineEntry> _outlineEntries = [];

    // Structure tree — populated by RegisterStructElem during layout/draw
    private readonly PdfStructureTree _structureTree = new();
    private bool _tagged;

    private int _fontCounter;
    private int _ttFontCounter;
    private bool _disposed;

    // Encryption settings supplied via Encrypt(). Null = no encryption.
    private PdfEncryptionSettings? _encryptionSettings;

    /// <summary>Document metadata (title, author, producer, …) written to the /Info dictionary.</summary>
    public PdfDocumentInfo Info { get; } = new();

    /// <summary>Default page size for new pages. Defaults to A4.</summary>
    public PdfRectangle DefaultPageSize { get; set; } = PageSize.A4;

    /// <summary>
    /// Optional fixed timestamp used for XMP CreateDate/ModifyDate and document ID computation.
    /// When null, <see cref="DateTimeOffset.UtcNow"/> at the time of the first <see cref="Save"/>
    /// call is used. Set to a fixed value for deterministic output.
    /// </summary>
    public DateTimeOffset? Timestamp { get; set; }

    private byte[]? _documentId;

    /// <summary>
    /// Optional fixed document <c>/ID</c> (the permanent element of the PDF <c>/ID</c> array;
    /// 16 bytes is conventional). When null, the ID is derived from the document content and
    /// <see cref="Timestamp"/>. Pin this together with <see cref="Timestamp"/> for byte-reproducible
    /// output.
    ///
    /// <para>Encrypted and signed documents stay non-deterministic regardless of these pins:
    /// each save draws fresh random salts/IVs for encryption, and a signature is time- and
    /// key-dependent.</para>
    /// </summary>
    /// <remarks>The array is defensively copied on get and set, so mutating it afterwards has
    /// no effect on the output.</remarks>
    public byte[]? DocumentId
    {
        get => _documentId is null ? null : (byte[])_documentId.Clone();
        set => _documentId = value is null ? null : (byte[])value.Clone();
    }

    /// <summary>
    /// Requested PDF/A conformance level.
    /// When non-<see cref="PdfConformance.None"/>:
    /// XMP pdfaid schema is included, /ID is written, and /MarkInfo /Marked true is set.
    /// PDF/A-2a additionally implies <see cref="Tagged"/> = true.
    /// </summary>
    public PdfConformance Conformance { get; set; } = PdfConformance.None;

    /// <summary>
    /// When true, <see cref="Save"/> uses PDF 1.5+ object streams (§7.5.7) and a
    /// cross-reference stream (§7.5.8) instead of the classic xref table.
    /// This produces smaller output by compressing non-stream indirect objects.
    ///
    /// Default is false; the default (classic xref) path is byte-for-byte unchanged.
    ///
    /// <para>
    /// Restrictions:
    /// <list type="bullet">
    ///   <item>Cannot be combined with <see cref="Encrypt"/> — throws
    ///         <see cref="NotSupportedException"/>.</item>
    ///   <item>Cannot be combined with <see cref="PrepareForSigning"/> — the signing path
    ///         always uses the classic xref for byte-range compatibility.</item>
    /// </list>
    /// </para>
    /// </summary>
    public bool UseObjectStreams { get; set; } = false;

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

    /// <summary>Adds a new page using <see cref="DefaultPageSize"/> and returns it.</summary>
    public PdfPage AddPage() => AddPage(DefaultPageSize);

    /// <summary>Adds a new page of the given <paramref name="size"/> and returns it.</summary>
    public PdfPage AddPage(PdfRectangle size)
    {
        var page = new PdfPage(size);
        _pages.Add(page);
        return page;
    }

    /// <summary>The pages in this document, in order.</summary>
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

    /// <summary>
    /// Adds an absolute-positioned text-input AcroForm field to <paramref name="page"/>.
    /// The field is serialised during <see cref="Save"/>.
    /// </summary>
    public void AddTextField(
        PdfPage page,
        string name,
        PdfRectangle rect,
        string value = "",
        FormFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rect);
        _formFields.Add(new PdfFormField.TextField(page, name, rect, value, options ?? new()));
    }

    /// <summary>
    /// Adds an absolute-positioned checkbox AcroForm field to <paramref name="page"/>.
    /// </summary>
    public void AddCheckBox(
        PdfPage page,
        string name,
        PdfRectangle rect,
        bool checkedState = false,
        FormFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rect);
        _formFields.Add(new PdfFormField.CheckBoxField(page, name, rect, checkedState, options ?? new()));
    }

    /// <summary>
    /// Adds an absolute-positioned dropdown or listbox AcroForm field to <paramref name="page"/>.
    /// </summary>
    public void AddChoiceField(
        PdfPage page,
        string name,
        PdfRectangle rect,
        IReadOnlyList<string> options,
        string? selected = null,
        bool combo = true,
        FormFieldOptions? fieldOptions = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rect);
        ArgumentNullException.ThrowIfNull(options);
        _formFields.Add(new PdfFormField.ChoiceField(page, name, rect, options, selected, combo, fieldOptions ?? new()));
    }

    /// <summary>
    /// Adds a radio button group AcroForm field. Each element of <paramref name="options"/>
    /// specifies the page, rectangle, and export value for one radio button widget.
    /// The optional <paramref name="selectedExportValue"/> sets the initially selected button;
    /// pass <see langword="null"/> to leave all buttons unselected (/V /Off).
    /// </summary>
    public void AddRadioButtonGroup(
        string name,
        IReadOnlyList<RadioOption> options,
        string? selectedExportValue = null,
        FormFieldOptions? fieldOptions = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Count == 0)
            throw new ArgumentException("A radio button group must have at least one option.", nameof(options));
        _formFields.Add(new PdfFormField.RadioGroupField(name, options, selectedExportValue, fieldOptions ?? new()));
    }

    /// <summary>
    /// Adds a push button AcroForm field to <paramref name="page"/>.
    /// The button displays <paramref name="caption"/> centred in the widget rectangle.
    /// </summary>
    public void AddPushButton(
        PdfPage page,
        string name,
        PdfRectangle rect,
        string caption,
        FormFieldOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(rect);
        ArgumentNullException.ThrowIfNull(caption);
        _formFields.Add(new PdfFormField.PushButtonField(page, name, rect, caption, options ?? new()));
    }

    /// <summary>
    /// Configures AES-256 encryption (Standard security handler V=5, R=6) for this document.
    /// Must be called before <see cref="Save"/>. When called, <see cref="Save"/> will:
    /// <list type="bullet">
    ///   <item>Generate the /Encrypt dictionary and write it as an unencrypted indirect object.</item>
    ///   <item>Encrypt all string and stream content in the document body.</item>
    ///   <item>Add /Encrypt to the trailer (unencrypted).</item>
    /// </list>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="settings"/> is <see langword="null"/>.</exception>
    public void Encrypt(PdfEncryptionSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        _encryptionSettings = settings;
    }

    /// <summary>Writes a complete PDF file to <paramref name="destination"/>.</summary>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="destination"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><see cref="UseObjectStreams"/> is combined with <see cref="Encrypt"/>.</exception>
    /// <exception cref="InvalidOperationException">A PDF/A <see cref="Conformance"/> is set together with <see cref="Encrypt"/>.</exception>
    public void Save(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        if (UseObjectStreams && _encryptionSettings is not null)
            throw new NotSupportedException(
                "UseObjectStreams cannot be combined with Encrypt(). " +
                "Object-stream encryption is not supported. Remove one of these options.");

        // PDF/A prohibits encryption (ISO 19005-2 §6.3.1). Fail fast rather than emit
        // a document that claims conformance but can never validate.
        if (Conformance != PdfConformance.None && _encryptionSettings is not null)
            throw new InvalidOperationException(
                "PDF/A prohibits encryption (ISO 19005-2 §6.3.1). " +
                "Remove Encrypt() or clear Conformance before calling Save().");

        var writer = new PdfWriter(destination);
        var xref = new CrossReferenceBuilder();
        var registry = new PdfObjectRegistry();

        // PDF header + binary comment (raw bytes >= 128, ISO 32000 7.5.2). PDF/A-2 is
        // defined against PDF 1.7, so conformance documents declare %PDF-1.7 (veraPDF
        // rule 6.1.2); other documents use the 2.0 baseline.
        writer.WriteAscii("%PDF-"u8);
        if (Conformance == PdfConformance.None)
            writer.WriteAscii("2.0"u8);
        else
            writer.WriteAscii("1.7"u8);
        writer.WriteAscii("\n%"u8);
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

        // ── Build AcroForm (interactive form fields) ──────────────────────
        // MUST happen before page dicts are built so that page.AddAnnotation()
        // calls populate _annots before BuildDictionary consumes them.
        var helveticaForForms = UseFont(Standard14.Helvetica);
        var acroFormDict = AcroFormBuilder.Build(_formFields, registry, pageRefMap, helveticaForForms);

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

        // ── Build sRGB ICC OutputIntent (PDF/A-2 §6.2.2) ─────────────────
        // An /OutputIntents array with a GTS_PDFA1 entry referencing an sRGB ICC
        // stream is required for all PDF/A-2 conformance levels.
        PdfIndirectReference? outputIntentsRef = null;
        if (Conformance != PdfConformance.None)
            outputIntentsRef = BuildOutputIntents(registry);

        // ── Build document /ID (MD5 over title + producer + page count + timestamp) ─
        var documentId = _documentId ?? ComputeDocumentId(ts);

        // ── Build /Encrypt dictionary (AES-256, V5/R6) and arm the encryptor ──
        // The /Encrypt object itself MUST be written BEFORE calling WriteAll,
        // and MUST be written with writer.Encryptor == null (not encrypted).
        // All other objects (written by WriteAll below) will be encrypted.
        PdfIndirectReference? encryptRef = null;
        if (_encryptionSettings is { } encSettings)
        {
            var handler = new StandardSecurityHandler(encSettings);
            encryptRef = registry.Reserve();
            var encDict = BuildEncryptDictionary(handler, encSettings);
            registry.SetValue(encryptRef, encDict);
            // Arm the writer — all strings/streams from WriteAll onward are encrypted.
            writer.Encryptor = handler;
        }

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

        if (acroFormDict is not null)
            catalog.Set(new PdfName("AcroForm"), acroFormDict);

        if (outputIntentsRef is not null)
            catalog.Set(new PdfName("OutputIntents"), new PdfArray([outputIntentsRef]));

        registry.SetValue(catalogRef, catalog);

        if (UseObjectStreams)
        {
            // ── Compressed path: ObjStm + XRef stream ─────────────────────────
            // The registry holds objects 1..ObjectCount. We allocate two extra numbers
            // (not in the registry) for the ObjStm and the XRef stream object.
            var objStmObjNum = registry.ObjectCount + 1;
            var xrefObjNum = registry.ObjectCount + 2;

            var xrefStream = registry.WriteAllCompressed(writer, objStmObjNum, xrefObjNum);
            xrefStream.WriteXRefStream(writer, xrefObjNum, catalogRef, infoRef, documentId: documentId);
            writer.Flush();
        }
        else
        {
            // ── Classic path (unchanged) ───────────────────────────────────────
            // The /Encrypt object is written here too; because it is a plain PdfDictionary
            // (no strings/streams), and strings inside it are PdfHexString values that were
            // created without going through the encryptor path (they are raw bytes), this is
            // safe. However, to be absolutely correct we momentarily disable the encryptor
            // when WriteAll processes the /Encrypt object's slot.
            // Implementation note: WriteAll writes objects in slot order. The /Encrypt ref
            // was reserved LAST (after the catalog), so its slot is at the end. We use a
            // per-object encryptor-disable approach: the /Encrypt dict's WriteTo is a
            // PdfDictionary, which never calls the encryptor directly — its children
            // (PdfHexString values) do. We disable the encryptor for those writes by
            // temporarily patching writer.Encryptor inside WriteAllWithEncryptExempt.
            WriteAllWithEncryptExempt(writer, xref, registry, encryptRef);

            // ── Cross-reference table + trailer ───────────────────────────────
            // Trailer /ID must NOT be encrypted. Ensure encryptor is null during trailer write.
            writer.Encryptor = null;
            xref.WriteXrefAndTrailer(writer, catalogRef, infoRef, documentId: documentId, encryptRef: encryptRef);
            writer.Flush();
        }
    }

    /// <summary>
    /// Writes this document to an in-memory buffer with AcroForm signature-field placeholders
    /// and returns the raw bytes. The returned array contains a structurally valid PDF whose
    /// <c>/ByteRange</c> and <c>/Contents</c> values are fixed-width placeholders ready for
    /// in-place patching by <c>VellumPdf.Signing</c>.
    ///
    /// <para>Encryption and signing are mutually exclusive; throws
    /// <see cref="NotSupportedException"/> when <see cref="Encrypt"/> has been called.</para>
    /// </summary>
    /// <exception cref="ObjectDisposedException">The document has been disposed.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="NotSupportedException"><see cref="Encrypt"/> has been called; encryption and signing cannot be combined.</exception>
    public byte[] PrepareForSigning(SignaturePlaceholderOptions options)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(options);

        if (_encryptionSettings is not null)
            throw new NotSupportedException(
                "PDF encryption and digital signatures cannot be combined. " +
                "Remove the Encrypt() call or use Sign() without Encrypt().");

        var ms = new MemoryStream();
        WriteWithSignaturePlaceholders(ms, options);
        return ms.ToArray();
    }

    /// <summary>
    /// Writes the PDF with signature field placeholders to <paramref name="destination"/>.
    /// The output contains valid PDF structure but with placeholder /ByteRange and /Contents.
    /// </summary>
    private void WriteWithSignaturePlaceholders(Stream destination, SignaturePlaceholderOptions options)
    {
        var writer = new PdfWriter(destination);
        var xref = new CrossReferenceBuilder();
        var registry = new PdfObjectRegistry();

        // PDF header — conformance documents declare %PDF-1.7 (veraPDF rule 6.1.2).
        writer.WriteAscii("%PDF-"u8);
        if (Conformance == PdfConformance.None)
            writer.WriteAscii("2.0"u8);
        else
            writer.WriteAscii("1.7"u8);
        writer.WriteAscii("\n%"u8);
        writer.WriteRaw([0xE2, 0xE3, 0xCF, 0xD3]);
        writer.WriteAscii("\n"u8);

        // ── Pre-allocate references ────────────────────────────────────────────
        var pageContentRefs = new PdfIndirectReference[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
            pageContentRefs[i] = registry.Reserve();

        var pageDictRefs = new PdfIndirectReference[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
            pageDictRefs[i] = registry.Reserve();

        var pageTreeRef = registry.Reserve();
        var infoRef = registry.Reserve();
        var catalogRef = registry.Reserve();

        // ── Signature-specific refs ────────────────────────────────────────────
        // sigDictRef = the /Sig value object (contains /ByteRange, /Contents)
        // sigFieldRef = the widget annotation / signature field
        var sigDictRef = registry.Reserve();
        var sigFieldRef = registry.Reserve();

        // ── Content streams ────────────────────────────────────────────────────
        for (var i = 0; i < _pages.Count; i++)
        {
            var content = _pages[i].ContentBytes ?? [];
            registry.SetValue(pageContentRefs[i], new PdfStream(content));
        }

        // ── Image XObjects ─────────────────────────────────────────────────────
        var imageRefs = new Dictionary<PdfImageXObject, PdfIndirectReference>(ReferenceEqualityComparer.Instance);
        foreach (var page in _pages)
        {
            if (!_pageImages.TryGetValue(page, out var images)) continue;
            foreach (var (img, name) in images)
            {
                if (!imageRefs.TryGetValue(img, out var imgObjRef))
                {
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

        // ── Embedded TrueType fonts ────────────────────────────────────────────
        foreach (var handle in _embeddedFonts)
        {
            var emb = handle.Embedder;
            var fontFileRef = registry.Reserve();
            var descriptorRef = registry.Reserve();
            var cidFontRef = registry.Reserve();
            var toUnicodeRef = registry.Reserve();
            var type0Ref = registry.Reserve();
            registry.SetValue(fontFileRef, emb.BuildFontFileStream());
            registry.SetValue(descriptorRef, emb.BuildFontDescriptor(fontFileRef));
            registry.SetValue(cidFontRef, emb.BuildCidFontDictionary(descriptorRef));
            registry.SetValue(toUnicodeRef, emb.BuildToUnicodeCMap());
            registry.SetValue(type0Ref, emb.BuildFontDictionary(cidFontRef, toUnicodeRef));
            foreach (var page in _pages)
            {
                if (!_pageEmbeddedFonts.TryGetValue(page, out var usedNames)) continue;
                if (!usedNames.Contains(handle.ResourceName)) continue;
                page.RegisterFontRef(handle.ResourceName, type0Ref);
            }
        }

        // ── Page→ref lookup ────────────────────────────────────────────────────
        var pageRefMap = new Dictionary<PdfPage, PdfIndirectReference>(_pages.Count);
        for (var i = 0; i < _pages.Count; i++)
            pageRefMap[_pages[i]] = pageDictRefs[i];

        // ── Link annotations ───────────────────────────────────────────────────
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

        // ── Structure tree ─────────────────────────────────────────────────────
        PdfIndirectReference? structTreeRootRef = null;
        if (Tagged && !_structureTree.IsEmpty)
        {
            structTreeRootRef = _structureTree.Build(registry, pageRefMap, out var pageStructParents);
            foreach (var (page, key) in pageStructParents)
                page.StructParentsKey = key;
        }

        // ── Add signature widget annotation to page 1 ──────────────────────────
        // The invisible widget is added to the first page's /Annots.
        if (_pages.Count > 0)
            _pages[0].AddAnnotation(sigFieldRef);

        // ── Page dicts ────────────────────────────────────────────────────────
        for (var i = 0; i < _pages.Count; i++)
        {
            var dict = _pages[i].BuildDictionary(pageTreeRef, pageContentRefs[i]);
            registry.SetValue(pageDictRefs[i], dict);
        }

        // ── Page tree ─────────────────────────────────────────────────────────
        var kids = new PdfArray(pageDictRefs.Cast<PdfObject>());
        var pageTree = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, kids)
            .Set(PdfName.Count, _pages.Count);
        registry.SetValue(pageTreeRef, pageTree);

        // ── Info dict ─────────────────────────────────────────────────────────
        registry.SetValue(infoRef, Info.BuildDictionary());

        // ── XMP metadata ──────────────────────────────────────────────────────
        var ts = Timestamp ?? DateTimeOffset.UtcNow;
        var xmpBytes = XmpMetadataWriter.BuildPacket(Info, Conformance, ts);
        var metadataStream = new UncompressedPdfStream(xmpBytes);
        metadataStream.Dictionary
            .Set(PdfName.Type, new PdfName("Metadata"))
            .Set(PdfName.Subtype, new PdfName("XML"));
        var metadataRef = registry.Reserve();
        registry.SetValue(metadataRef, metadataStream);

        // ── Document ID ───────────────────────────────────────────────────────
        var documentId = _documentId ?? ComputeDocumentId(ts);

        // ── Outline tree ──────────────────────────────────────────────────────
        PdfIndirectReference? outlinesRef = null;
        if (_outlineEntries.Count > 0)
            outlinesRef = BuildOutlineTree(registry, pageRefMap);

        // ── Signature dictionary (/V) ──────────────────────────────────────────
        // /ByteRange and /Contents must be placeholders with fixed byte width so
        // they can be patched in-place without shifting any offsets.
        var signingTime = options.SigningTime ?? DateTimeOffset.UtcNow;
        var pdfDate = PdfSignatureHelper.FormatPdfDate(signingTime);

        var sigDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Sig"))
            .Set(PdfName.Filter, new PdfName("Adobe.PPKLite"))
            .Set(new PdfName("SubFilter"), new PdfName(options.SubFilter))
            .Set(new PdfName("M"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(pdfDate)))
            .Set(new PdfName("ByteRange"), new PdfRawBytesObject(PdfSignatureHelper.GetByteRangePlaceholderString()))
            .Set(PdfName.Contents, new PdfRawBytesObject(PdfSignatureHelper.GetContentsPlaceholder(options.EstimatedSignatureSizeBytes)));

        // Optional fields — only include if non-null/non-empty
        if (!string.IsNullOrEmpty(options.SignerName))
            sigDict.Set(new PdfName("Name"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(options.SignerName)));
        if (!string.IsNullOrEmpty(options.Reason))
            sigDict.Set(new PdfName("Reason"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(options.Reason)));
        if (!string.IsNullOrEmpty(options.Location))
            sigDict.Set(new PdfName("Location"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(options.Location)));
        if (!string.IsNullOrEmpty(options.ContactInfo))
            sigDict.Set(new PdfName("ContactInfo"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes(options.ContactInfo)));

        registry.SetValue(sigDictRef, sigDict);

        // ── Signature field / widget annotation ────────────────────────────────
        // This is both the AcroForm field (/FT /Sig) and the invisible widget annotation
        // (/Subtype /Widget /Rect [0 0 0 0]). Per ISO 32000-2, a signature field may
        // merge the field and its widget annotation into one dictionary.
        var page1Ref = _pages.Count > 0 ? pageDictRefs[0] : (PdfObject)PdfNull.Instance;

        var sigFieldDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Annot"))
            .Set(PdfName.Subtype, new PdfName("Widget"))
            .Set(new PdfName("FT"), new PdfName("Sig"))
            .Set(new PdfName("T"), new PdfLiteralString(System.Text.Encoding.Latin1.GetBytes("Signature1")))
            .Set(new PdfName("Rect"), new PdfArray([new PdfInteger(0), new PdfInteger(0), new PdfInteger(0), new PdfInteger(0)]))
            .Set(new PdfName("F"), new PdfInteger(132))
            .Set(new PdfName("P"), page1Ref)
            .Set(new PdfName("V"), sigDictRef);

        registry.SetValue(sigFieldRef, sigFieldDict);

        // ── Catalog ────────────────────────────────────────────────────────────
        var acroFormDict = new PdfDictionary()
            .Set(new PdfName("Fields"), new PdfArray([sigFieldRef]))
            .Set(new PdfName("SigFlags"), new PdfInteger(3));

        var catalog = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef)
            .Set(new PdfName("Metadata"), metadataRef)
            .Set(new PdfName("AcroForm"), acroFormDict);

        if (outlinesRef is not null)
        {
            catalog
                .Set(new PdfName("Outlines"), outlinesRef)
                .Set(new PdfName("PageMode"), new PdfName("UseOutlines"));
        }

        var needsMarkInfo = Tagged || Conformance != PdfConformance.None;
        if (needsMarkInfo)
        {
            var markInfo = new PdfDictionary()
                .Set(new PdfName("Marked"), PdfBoolean.True);
            catalog.Set(new PdfName("MarkInfo"), markInfo);
        }

        if (structTreeRootRef is not null)
            catalog.Set(new PdfName("StructTreeRoot"), structTreeRootRef);

        // PDF/A output intent (sRGB ICC, §6.2.2) — required when signing a conformance
        // document, just as on the regular Save path.
        if (Conformance != PdfConformance.None)
            catalog.Set(new PdfName("OutputIntents"), new PdfArray([BuildOutputIntents(registry)]));

        registry.SetValue(catalogRef, catalog);

        // ── Write all objects ──────────────────────────────────────────────────
        registry.WriteAll(writer, xref);

        // ── Cross-reference table + trailer ───────────────────────────────────
        xref.WriteXrefAndTrailer(writer, catalogRef, infoRef, documentId: documentId);
        writer.Flush();
    }

    /// <summary>
    /// Builds and registers the /OutputIntents array entry referencing an sRGB ICC profile stream.
    /// Required by PDF/A-2 (ISO 19005-2 §6.2.2) for all conformance levels.
    ///
    /// <para>
    /// The ICC stream uses FlateDecode compression and carries <c>/N 3</c> (3 colour components,
    /// RGB). The OutputIntent dictionary uses <c>/S /GTS_PDFA1</c> and identifies the profile
    /// as "sRGB IEC61966-2.1".
    /// </para>
    /// </summary>
    /// <remarks>
    /// <strong>PDF/A output requirements (all must be satisfied by the caller):</strong>
    /// <list type="bullet">
    ///   <item>Use <see cref="UseTrueTypeFont"/> for all fonts — Standard-14 unembedded fonts fail
    ///         the PDF/A font-embedding rule (ISO 19005-2 §6.3.3).</item>
    ///   <item>Do not use <see cref="Encrypt"/> — PDF/A prohibits encryption (ISO 19005-2 §6.3.1).</item>
    ///   <item>Set <see cref="Tagged"/> = true (or use <see cref="PdfConformance.PdfA2a"/>) for
    ///         conformance level A.</item>
    /// </list>
    /// </remarks>
    private static PdfIndirectReference BuildOutputIntents(PdfObjectRegistry registry)
    {
        var iccBytes = SrgbIccProfile.Bytes;
        var iccStream = new PdfStream(iccBytes);
        iccStream.Dictionary
            .Set(new PdfName("N"), new PdfInteger(3));

        var iccRef = registry.Reserve();
        registry.SetValue(iccRef, iccStream);

        var intentDict = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("OutputIntent"))
            .Set(new PdfName("S"), new PdfName("GTS_PDFA1"))
            .Set(new PdfName("OutputConditionIdentifier"), new PdfLiteralString(
                System.Text.Encoding.Latin1.GetBytes("sRGB IEC61966-2.1")))
            .Set(new PdfName("Info"), new PdfLiteralString(
                System.Text.Encoding.Latin1.GetBytes("sRGB IEC61966-2.1")))
            .Set(new PdfName("DestOutputProfile"), iccRef);

        var intentRef = registry.Reserve();
        registry.SetValue(intentRef, intentDict);
        return intentRef;
    }

    /// <summary>
    /// Builds the /Encrypt dictionary for a V5/R6 AES-256 Standard security handler.
    /// The string values (/O, /U, /OE, /UE, /Perms) are written as hex strings
    /// (PdfHexString). They are raw computed bytes — NOT encrypted themselves.
    /// </summary>
    private static PdfDictionary BuildEncryptDictionary(
        Encryption.StandardSecurityHandler handler,
        PdfEncryptionSettings settings)
    {
        // /CF sub-dictionary: one crypt filter "StdCF" using AESv3 (256-bit AES).
        var stdCfDict = new PdfDictionary()
            .Set(new PdfName("CFM"), new PdfName("AESV3"))
            .Set(new PdfName("AuthEvent"), new PdfName("DocOpen"));

        var cfDict = new PdfDictionary()
            .Set(new PdfName("StdCF"), stdCfDict);

        return new PdfDictionary()
            .Set(new PdfName("Filter"), new PdfName("Standard"))
            .Set(new PdfName("V"), new PdfInteger(5))
            .Set(new PdfName("R"), new PdfInteger(6))
            .Set(new PdfName("Length"), new PdfInteger(256))
            .Set(new PdfName("CF"), cfDict)
            .Set(new PdfName("StmF"), new PdfName("StdCF"))
            .Set(new PdfName("StrF"), new PdfName("StdCF"))
            .Set(new PdfName("O"), new PdfHexString(handler.O))
            .Set(new PdfName("U"), new PdfHexString(handler.U))
            .Set(new PdfName("OE"), new PdfHexString(handler.OE))
            .Set(new PdfName("UE"), new PdfHexString(handler.UE))
            .Set(new PdfName("P"), new PdfInteger(handler.PValue))
            .Set(new PdfName("Perms"), new PdfHexString(handler.Perms))
            .Set(new PdfName("EncryptMetadata"), settings.EncryptMetadata ? PdfBoolean.True : PdfBoolean.False);
    }

    /// <summary>
    /// Writes all registered indirect objects, temporarily disabling the encryptor
    /// for the /Encrypt object's slot (its data is already computed encrypted values —
    /// they must NOT be double-encrypted).
    ///
    /// The /Encrypt dict contains PdfHexString values that would normally go through
    /// the encryptor if it is active. We disable the encryptor just for that one object.
    /// </summary>
    private static void WriteAllWithEncryptExempt(
        PdfWriter writer,
        CrossReferenceBuilder xref,
        PdfObjectRegistry registry,
        PdfIndirectReference? encryptRef)
    {
        if (encryptRef is null)
        {
            // No encryption — delegate to the normal path.
            registry.WriteAll(writer, xref);
            return;
        }

        registry.WriteAll(writer, xref, objectNumber =>
        {
            // Disable the encryptor while writing the /Encrypt dict itself.
            if (objectNumber == encryptRef.ObjectNumber)
            {
                var saved = writer.Encryptor;
                writer.Encryptor = null;
                return () => writer.Encryptor = saved;
            }
            return null;
        });
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

    /// <summary>Releases the document; subsequent <see cref="Save"/> or <see cref="Encrypt"/> calls throw.</summary>
    public void Dispose() => _disposed = true;
}
