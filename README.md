# VellumPdf

[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Kernel.svg?label=VellumPdf.Kernel)](https://www.nuget.org/packages/VellumPdf.Kernel)
[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Conformance.svg?label=VellumPdf.Conformance)](https://www.nuget.org/packages/VellumPdf.Conformance)

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
| `VellumPdf.Fonts.Standard14` | Stable | Optional, embeddable metric-compatible substitutes for the standard-14 fonts (Liberation Sans/Serif/Mono, SIL OFL 1.1, covering the Helvetica/Times/Courier families). The built-in `Standard14` fonts are not embedded — fine for ordinary PDFs but disallowed by PDF/A's font-embedding rule; `doc.EmbedStandard14Font(...)` registers a subset, embedded substitute so standard-14-style text is PDF/A-conformant without a caller-supplied font program. (Symbol and ZapfDingbats are not covered.) |
| `VellumPdf.Layout` | Stable | High-level document builder: paragraphs, headings, lists, tables, images, header/footer bands, bookmarks, and automatic pagination. |
| `VellumPdf.Signing` | Stable | PAdES / PKCS#7 detached digital signatures with RFC-3161 signature timestamps and long-term validation. Levels B-T, B-LT (embedded OCSP/CRL in a `/DSS`), and B-LTA (archive document timestamp), via pluggable timestamp and revocation clients. |
| `VellumPdf.Reader` | Preview | Opens existing PDFs (classic cross-reference tables, cross-reference and object streams, hybrid-reference files; unencrypted) and exposes the catalog, signatures, and decoded stream data. The basis for the signing LTV path, the conformance validator, and a general reader. |
| `VellumPdf.Conformance` | Preview | In-process PDF/A-2b/2u/2a and PDF/UA-1 preflight: runs clean-room conformance rules authored from the ISO specifications and returns machine-readable assertions (rule id, ISO clause, severity, object reference) — no external veraPDF Docker image needed. AOT- and trim-ready (rules registered explicitly, no reflection). Covers file structure, colour and output intents (including ICC profile validity and ICCBased-CMYK overprint), transparency, images and XObjects (including a JPEG2000 codestream parser), fonts (an in-process sfnt font-program parser for glyph presence and widths, embedded-CMap CID/WMode/usecmap checks), content streams (ISO 32000-1 operator, inline-image-filter, and graphics-state validation), digital signatures (a zero-dependency CMS/ASN.1 reader for §6.4.3), annotations, interactive forms, actions, and XMP metadata (via an in-process XMP parser), plus the 2u/2a deltas and a tagged-structure walker for the PDF/UA-1 (ISO 14289-1) accessibility checks. Build-verified veraPDF parity is ~90% for PDF/A-2b/2u, ~90% for 2a, and ~90% for PDF/UA-1. Each rule documents its deferred edges; every rule's positive and negative paths are cross-validated against veraPDF in CI. |
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
| Linearization (v1.8) | "Fast web view" object ordering. |
| Barcodes (v1.9) | The `VellumPdf.Barcodes` package — QR, PDF417, Code128, EAN. |
| Preflight CLI (v1.7.x) | Native-AOT `vellum-preflight` command-line binary over `VellumPdf.Conformance`, with per-platform release binaries — validate a PDF without a JVM or Docker. Capstone of the in-process preflight line, shipped once coverage is high enough (#130). |
| PDF reader (v2.1) | `VellumPdf.Reader` — read any PDF. xref streams, object streams, and hybrid-reference files shipped in v1.7; v2.1 grows it into a full structural parser including encrypted-file reading (Epic #100). |
| Content extraction (v2.2) | Text and image extraction on the reader (#98). |
| Editing existing PDFs (v3.0) | Unified read-modify-write document model; supersedes the write-once `PdfDocument` (Epic #101). |

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
