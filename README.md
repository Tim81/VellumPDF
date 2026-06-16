# VellumPdf

[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Kernel.svg?label=VellumPdf.Kernel)](https://www.nuget.org/packages/VellumPdf.Kernel)

A modern, **dependency-free PDF generation library for .NET 10**, implemented
clean-room from the open **ISO 32000** standard.

> **Status: stable.** The public API is locked (analyzer-enforced) and
> the library targets .NET 10. Core features are CI-validated — including
> PDF/A-2a/2b/2u and PDF/UA-1 conformance proven on every push with veraPDF.

## Why VellumPdf

- **Zero runtime dependencies.** The core is built entirely on the .NET base
  class library — no native binaries, no third-party packages. (The optional
  signing package is the sole exception: it uses `System.Security.Cryptography.Pkcs`.)
- **AOT- and trim-ready.** Ships as managed DLLs; ideal for Native AOT,
  trimming, containers, and serverless. A Native-AOT smoke test guards this.
- **Unicode-first text.** Embeds and subsets TrueType and OpenType-CFF fonts,
  emits composite (CID) fonts with subset tags, and writes `ToUnicode` maps so
  output stays searchable and copy-paste-able.
- **Two API tiers.** A low-level canvas for precise drawing, and a high-level
  document/layout engine with paragraphs, headings, lists, tables, images, and
  automatic pagination.
- **Built for the hard standards.** PDF/A-2a (accessible archival) and PDF/UA-1
  (universal accessibility) are implemented and CI-validated with veraPDF, alongside
  PDF/A-2b/2u, PAdES digital signatures, and interactive AcroForms.
- **Permissive license.** Apache-2.0 — free to use in proprietary products.

## Packages

| Package | Status | Description |
| --- | --- | --- |
| `VellumPdf.Kernel` | Stable | Object model, canvas, Standard-14 fonts, TrueType/OpenType embedding + subsetting, images (JPEG/PNG/BMP/GIF/TIFF/JBIG2/JPEG 2000), AES-256 encryption, object/cross-reference streams, AcroForm fields, tagged-PDF structure tree, PDF/A-2 metadata, and DeviceCMYK and ICC-based colour with configurable output intents. |
| `VellumPdf.Layout` | Stable | High-level document builder: paragraphs, headings, lists, tables, images, header/footer bands, bookmarks, and automatic pagination. |
| `VellumPdf.Signing` | Stable | PAdES / PKCS#7 detached digital signatures with RFC-3161 signature timestamps and long-term validation. Levels B-T, B-LT (embedded OCSP/CRL in a `/DSS`), and B-LTA (archive document timestamp), via pluggable timestamp and revocation clients. |
| `VellumPdf.Reader` | Preview | Opens existing signed PDFs (classic cross-reference, unencrypted) and exposes the catalog and signatures; the basis for the signing LTV path and the first slice of a general reader. |
| _(roadmap)_ `VellumPdf.Conformance` | Planned | In-process PDF/A and PDF/UA preflight validator. |
| _(roadmap)_ `VellumPdf.Barcodes` | Planned | QR, PDF417, Code128, EAN. |

## Quick start

```shell
dotnet add package VellumPdf.Layout
```

```csharp
using VellumPdf.Document;          // PdfConformance
using VellumPdf.Fonts;             // Standard14
using VellumPdf.Layout;            // Document
using VellumPdf.Layout.Core;       // TextStyle
using VellumPdf.Layout.Elements;   // Paragraph, Heading
using VellumPdf.Layout.Elements.Table; // TableElement

// Basic document — defaults to A4.
using var doc = new Document();
doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
doc.Add(new Heading("Hello, world!"));
doc.Add(new Paragraph("Generated with VellumPdf — no native dependencies."));
doc.Save("hello.pdf");
```

```csharp
// PDF/A-2b archival document. PDF/A requires every glyph to come from an
// embedded font, so load a TrueType font and use it for all text.
using var archive = new Document { Conformance = PdfConformance.PdfA2b };
var font = archive.LoadTrueTypeFont("/path/to/DejaVuSans.ttf");
var style = new TextStyle { FontRef = font, FontSize = 12 };

archive.Add(new Paragraph("This document validates as PDF/A-2b.", style));

var table = new TableElement { DefaultCellStyle = style };
table.SetColumnWidths(200, 200);
table.AddHeaderRow().AddCell("Item").AddCell("Value");
table.AddRow().AddCell("Format").AddCell("PDF/A-2b");
archive.Add(table);

archive.Save("archive.pdf");
```

### Low-level Kernel API

For precise canvas control, bypass the Layout engine and write directly to the
PDF content stream:

```csharp
using VellumPdf.Canvas;    // PdfCanvas
using VellumPdf.Document;  // PdfDocument, PageSize
using VellumPdf.Fonts;     // Standard14

using var doc = new PdfDocument();
var page = doc.AddPage(PageSize.A4);
var font = doc.UseFont(Standard14.Helvetica);
var canvas = new PdfCanvas(page);

canvas
    .BeginText()
    .SetFont(font, 12)
    .SetTextMatrix(1, 0, 0, 1, 72, 720)
    .ShowText("Hello from the Kernel API!")
    .EndText();

canvas.Finish();

using var stream = File.OpenWrite("kernel-hello.pdf");
doc.Save(stream);
```

For a full walkthrough of the canvas, graphics primitives, and font handling,
see [docs/kernel-guide.md](docs/kernel-guide.md).

## Conventions

- **Units.** All coordinates and sizes are in PDF user-space **points** (1 pt = 1/72 inch).
  `PageSize` provides the common ISO-A sizes plus a `PageSize.Mm(width, height)` helper for
  custom millimetre dimensions.
- **Synchronous I/O.** Saving, signing, and the font/image loaders are synchronous by design
  for 1.0 — there is no `async` surface. Offload to `Task.Run` if you need to keep a thread free.

## Validation & CI

Correctness is enforced on every push by running real external validators as CI
oracles — a missing tool fails the build, so the gates can never silently skip:

- **`qpdf --check`** — structural integrity of every generated document type.
- **`pdftotext`** (poppler) — text-extraction round-trip proving `ToUnicode` maps.
- **`pdfsig`** (poppler) — signature validity for PAdES documents.
- **veraPDF** (official `verapdf/cli` Docker image) — strict **PDF/A-2a/2b/2u and PDF/UA-1**
  conformance over embedded-font, table, image, and tagged documents. A
  non-compliant report fails CI with the full rule list attached.

## Not yet / roadmap

| Area | Notes |
| --- | --- |
| In-process preflight | `VellumPdf.Conformance` PDF/A and PDF/UA validator package. |
| Linearization | "Fast web view" object ordering. |
| PDF reader (v2.1) | `VellumPdf.Reader` — read any PDF. Grows the v1.6 LTV MVP reader into a full structural parser: xref streams, object streams, encryption (Epic #100). |
| Content extraction (v2.2) | Text and image extraction on the reader (#98). |
| Editing existing PDFs (v3.0) | Unified read-modify-write document model; supersedes the write-once `PdfDocument` (Epic #101). |
| Barcodes | The `VellumPdf.Barcodes` package. |

## Building

Requires the .NET 10 SDK.

```bash
dotnet build VellumPdf.slnx
dotnet test  VellumPdf.slnx
```

The veraPDF conformance gate runs automatically in CI; to reproduce it locally,
install [veraPDF](https://verapdf.org) (or use its Docker image) so the
`verapdf` CLI is on your `PATH`, then run the oracle tests.

## License & provenance

Licensed under the [Apache License 2.0](LICENSE). VellumPdf is an original,
independent implementation written solely from open published specifications;
see [NOTICE](NOTICE) and [docs/architecture.md](docs/architecture.md).
