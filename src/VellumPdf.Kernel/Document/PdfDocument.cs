// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Fonts;
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
    private int _fontCounter;
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

    /// <summary>Writes a complete PDF file to <paramref name="destination"/>.</summary>
    public void Save(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);

        var writer = new PdfWriter(destination);
        var xref   = new CrossReferenceBuilder();

        // PDF header with binary comment (hints readers to treat as binary)
        writer.WriteAscii("%PDF-2.0\n%\xE2\xE3\xCF\xD3\n"u8);

        // ── Page content streams ───────────────────────────────────────────
        var pageContentRefs = new PdfIndirectReference[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
        {
            var content = _pages[i].ContentBytes ?? [];
            var stream  = new PdfStream(content);
            var objNum  = xref.ReserveObjectNumber(writer.Position);
            new PdfIndirectObject(objNum, stream).WriteTo(writer);
            writer.WriteByte((byte)'\n');
            pageContentRefs[i] = new PdfIndirectReference(objNum);
        }

        // ── Page tree node  ────────────────────────────────────────────────
        // We need the page-tree object number before writing page dicts
        // (each page dict's /Parent must reference it).
        // Allocate page-dict object numbers first, then the tree node.
        var pageDictObjNums = new int[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
            pageDictObjNums[i] = xref.NextObjectNumber + i;

        var pageTreeObjNum = xref.NextObjectNumber + _pages.Count;
        var pageTreeRef    = new PdfIndirectReference(pageTreeObjNum);

        // Write page dicts
        var pageDictRefs = new PdfIndirectReference[_pages.Count];
        for (var i = 0; i < _pages.Count; i++)
        {
            var dict   = _pages[i].BuildDictionary(pageTreeRef, pageContentRefs[i]);
            var objNum = xref.ReserveObjectNumber(writer.Position);
            new PdfIndirectObject(objNum, dict).WriteTo(writer);
            writer.WriteByte((byte)'\n');
            pageDictRefs[i] = new PdfIndirectReference(objNum);
        }

        // Write page tree node
        var kids = new PdfArray(pageDictRefs.Cast<PdfObject>());
        var pageTree = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, kids)
            .Set(PdfName.Count, _pages.Count);
        var actualPageTreeObjNum = xref.ReserveObjectNumber(writer.Position);
        new PdfIndirectObject(actualPageTreeObjNum, pageTree).WriteTo(writer);
        writer.WriteByte((byte)'\n');

        // ── Info dictionary ────────────────────────────────────────────────
        var infoObjNum = xref.ReserveObjectNumber(writer.Position);
        new PdfIndirectObject(infoObjNum, Info.BuildDictionary()).WriteTo(writer);
        writer.WriteByte((byte)'\n');
        var infoRef = new PdfIndirectReference(infoObjNum);

        // ── Catalog ────────────────────────────────────────────────────────
        var catalog = new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef);

        var catalogObjNum = xref.ReserveObjectNumber(writer.Position);
        new PdfIndirectObject(catalogObjNum, catalog).WriteTo(writer);
        writer.WriteByte((byte)'\n');
        var catalogRef = new PdfIndirectReference(catalogObjNum);

        // ── Cross-reference table + trailer ───────────────────────────────
        xref.WriteXrefAndTrailer(writer, catalogRef, infoRef);
        writer.Flush();
    }

    public void Dispose() => _disposed = true;
}
