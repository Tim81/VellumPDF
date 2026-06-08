// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout;

/// <summary>
/// High-level document builder. The primary entry point for the layout engine.
///
/// <code>
/// using var doc = new Document();
/// doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
/// doc.Add(new Paragraph("Hello, world!"));
/// doc.Save("output.pdf");
/// </code>
/// </summary>
public sealed class Document : IDisposable
{
    private readonly PdfDocument _pdf = new();
    private readonly List<IRenderer> _content = [];
    private TextStyle _defaultStyle = TextStyle.Default;

    public PdfDocumentInfo Info => _pdf.Info;

    public PdfRectangle PageSize
    {
        get => _pdf.DefaultPageSize;
        set => _pdf.DefaultPageSize = value;
    }

    public EdgeInsets Margins { get; set; } = new EdgeInsets(72); // 1 inch

    public Document SetDefaultFont(TextStyle style) { _defaultStyle = style; return this; }

    // ── Embedded font registration ───────────────────────────────────────────

    /// <summary>
    /// Registers a TrueType font for embedding and returns a handle that can be
    /// used in <see cref="TextStyle.FontRef"/>.
    /// </summary>
    public EmbeddedFontHandle UseTrueTypeFont(byte[] fontData) =>
        _pdf.UseTrueTypeFont(fontData);

    /// <summary>
    /// Loads a TrueType font file from disk, registers it for embedding, and returns a handle.
    /// </summary>
    public EmbeddedFontHandle LoadTrueTypeFont(string path) =>
        UseTrueTypeFont(File.ReadAllBytes(path));

    // ── Content methods ──────────────────────────────────────────────────────

    public Document Add(Paragraph paragraph)
    {
        _content.Add(new ParagraphRenderer(paragraph));
        return this;
    }

    public Document Add(LineSeparator separator)
    {
        _content.Add(new LineSeparatorRenderer(separator));
        return this;
    }

    public Document Add(TableElement table)
    {
        _content.Add(new TableRenderer(table));
        return this;
    }

    public Document Add(LayoutImage image)
    {
        _content.Add(new LayoutImageRenderer(image));
        return this;
    }

    public Document Add(ListElement list)
    {
        _content.Add(new ListRenderer(list));
        return this;
    }

    public Document Add(string text, TextStyle? style = null)
        => Add(new Paragraph(text, style ?? _defaultStyle));

    // ── Output ───────────────────────────────────────────────────────────────

    public void Save(Stream destination)
    {
        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins);
        foreach (var r in _content) renderer.Add(r);
        renderer.Render(destination);
    }

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(fs);
    }

    public void Dispose() => _pdf.Dispose();
}
