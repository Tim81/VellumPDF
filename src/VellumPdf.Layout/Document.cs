// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Encryption;
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

    /// <summary>
    /// Requested PDF/A conformance level. Forwarded to the underlying <see cref="PdfDocument"/>.
    /// PDF/A-2a implies <see cref="Tagged"/> = true.
    /// </summary>
    public PdfConformance Conformance
    {
        get => _pdf.Conformance;
        set => _pdf.Conformance = value;
    }

    /// <summary>
    /// When true, paragraphs and headings are wrapped in marked-content sequences
    /// and a /StructTreeRoot is written. Default is false.
    /// Forwarded to the underlying <see cref="PdfDocument"/>.
    /// </summary>
    public bool Tagged
    {
        get => _pdf.Tagged;
        set => _pdf.Tagged = value;
    }

    public EdgeInsets Margins { get; set; } = new EdgeInsets(72); // 1 inch

    /// <summary>
    /// Optional header band drawn at the top of every page.
    /// Set via <see cref="SetHeader"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    public RunningBand? Header { get; set; }

    /// <summary>
    /// Optional footer band drawn at the bottom of every page.
    /// Set via <see cref="SetFooter"/> for a fluent API.
    /// Supports {page} and {pages} tokens.
    /// </summary>
    public RunningBand? Footer { get; set; }

    public Document SetDefaultFont(TextStyle style) { _defaultStyle = style; return this; }

    /// <summary>Sets a header band with optional style and alignment. Returns this document for chaining.</summary>
    public Document SetHeader(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Header = new RunningBand(template, style, alignment);
        return this;
    }

    /// <summary>Sets a footer band with optional style and alignment. Returns this document for chaining.</summary>
    public Document SetFooter(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Footer = new RunningBand(template, style, alignment);
        return this;
    }

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

    public Document Add(Heading heading)
    {
        _content.Add(new HeadingRenderer(heading));
        return this;
    }

    public Document Add(string text, TextStyle? style = null)
        => Add(new Paragraph(text, style ?? _defaultStyle));

    // ── Encryption ──────────────────────────────────────────────────────────

    /// <summary>
    /// Configures AES-256 encryption for this document.
    /// Delegates to <see cref="PdfDocument.Encrypt"/>.
    /// Must be called before <see cref="Save(Stream)"/>.
    /// </summary>
    public Document Encrypt(PdfEncryptionSettings settings)
    {
        _pdf.Encrypt(settings);
        return this;
    }

    // ── Output ───────────────────────────────────────────────────────────────

    public void Save(Stream destination)
    {
        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
        };
        foreach (var r in _content) renderer.Add(r);
        renderer.Render(destination);
    }

    public void Save(string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Save(fs);
    }

    // ── Signing seam (VellumPdf.Signing only) ────────────────────────────────

    /// <summary>
    /// Runs the layout pass and delegates to
    /// <see cref="PdfDocument.PrepareForSigning"/> to produce the unsigned placeholder bytes.
    /// Called exclusively by <c>VellumPdf.Signing.SigningExtensions</c>.
    /// </summary>
    internal byte[] PrepareForSigning(SignaturePlaceholderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var renderer = new DocumentRenderer(_pdf, _pdf.DefaultPageSize, Margins)
        {
            Header = Header,
            Footer = Footer,
        };
        foreach (var r in _content) renderer.Add(r);
        renderer.RunLayout();
        return _pdf.PrepareForSigning(options);
    }

    public void Dispose() => _pdf.Dispose();
}
