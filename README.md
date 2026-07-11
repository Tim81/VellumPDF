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

| Package | NuGet | Status | Description |
| --- | --- | --- | --- |
| `VellumPdf.Kernel` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Kernel.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Object model, canvas, Standard-14 fonts, TrueType/OpenType embedding + subsetting, images (JPEG/PNG/BMP/GIF/TIFF/JBIG2/JPEG 2000), AES-256 encryption, object/cross-reference streams, AcroForm fields, tagged-PDF structure tree, PDF/A-2 metadata, DeviceCMYK and ICC-based colour with configurable output intents, and opt-in linearization (`Linearize`) for fast-web-view first-page rendering. |
| `VellumPdf.Fonts.Standard14` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Fonts.Standard14.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Optional, embeddable metric-compatible substitutes for the standard-14 fonts (Liberation Sans/Serif/Mono, SIL OFL 1.1, covering the Helvetica/Times/Courier families). The built-in `Standard14` fonts are not embedded — fine for ordinary PDFs but disallowed by PDF/A's font-embedding rule; `doc.EmbedStandard14Font(...)` registers a subset, embedded substitute so standard-14-style text is PDF/A-conformant without a caller-supplied font program. (Symbol and ZapfDingbats are not covered.) |
| `VellumPdf.Layout` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Layout.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, headings, lists, tables, images, pie charts, header/footer bands, bookmarks, and automatic pagination. |
| `VellumPdf.Signing` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Signing.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 detached digital signatures with RFC-3161 signature timestamps and long-term validation. Levels B-T, B-LT (embedded OCSP/CRL in a `/DSS`), and B-LTA (archive document timestamp), via pluggable timestamp and revocation clients. |
| `VellumPdf.Reader` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Reader.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs (classic cross-reference tables, cross-reference and object streams, hybrid-reference files; unencrypted) and exposes the catalog, signatures, and decoded stream data. The basis for the signing LTV path, the conformance validator, and a general reader. |
| `VellumPdf.Conformance` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Conformance.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Conformance) | Preview | In-process PDF/A-2b/2u/2a and PDF/UA-1 preflight: runs clean-room conformance rules authored from the ISO specifications and returns machine-readable assertions (rule id, ISO clause, severity, object reference) — no external veraPDF Docker image needed. AOT- and trim-ready (rules registered explicitly, no reflection). Covers file structure, colour and output intents (including ICC profile validity and ICCBased-CMYK overprint), transparency, images and XObjects (including a JPEG2000 codestream parser), fonts (an in-process sfnt font-program parser for glyph presence and widths, embedded-CMap CID/WMode/usecmap checks), content streams (ISO 32000-1 operator, inline-image-filter, and graphics-state validation), digital signatures (a zero-dependency CMS/ASN.1 reader for §6.4.3), annotations, interactive forms, actions, and XMP metadata (via an in-process XMP parser), plus the 2u/2a deltas and a tagged-structure walker for the PDF/UA-1 (ISO 14289-1) accessibility checks. Build-verified veraPDF parity is about 99% for PDF/A-2b/2u/2a and PDF/UA-1; the only gaps are five checks tracked in follow-up issues — three with a predefined-CJK-CMap sub-condition that needs a conformant CJK font asset to cross-validate, and two that need a subsystem outside this release (a PDF/A-1 profile, reader decryption). Every rule's positive and negative paths are cross-validated against veraPDF in CI. |
| `VellumPdf.Cli` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Cli.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | The `vellum-preflight` command-line validator: checks a PDF against PDF/A-2b/2u/2a and PDF/UA-1 with the in-process `VellumPdf.Conformance` engine — no JVM or Docker. Ships as a cross-platform .NET tool (`dotnet tool install -g VellumPdf.Cli`) and self-contained Native-AOT binaries. Text, JSON, and SARIF 2.1.0 output; file, glob, directory, and stdin inputs; exit codes `0` (conformant), `1` (non-conformant), `2` (usage or I/O error). |
| `VellumPdf.Barcodes` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Barcodes.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | Eleven symbologies: QR (including Micro QR and GS1 mode with Digital Link), Data Matrix (including GS1 Data Matrix), Aztec, PDF417, Code 128 (plain and GS1-128), Code 39 (including Full ASCII), EAN-13/EAN-8/UPC-A/UPC-E with EAN-2/EAN-5 add-ons, and ITF-14, rendered as vector rectangles through a low-level `PdfCanvas` extension or the `Document.Add` flow API. Round-trip decoding is verified in CI against zxing-cpp. |

### Install

```shell
# Core
dotnet add package VellumPdf.Layout
dotnet add package VellumPdf.Kernel

# Add-ons
dotnet add package VellumPdf.Signing
dotnet add package VellumPdf.Fonts.Standard14
dotnet add package VellumPdf.Barcodes

# Preview
dotnet add package VellumPdf.Reader
dotnet add package VellumPdf.Conformance

# Tooling (.NET global tool)
dotnet tool install -g VellumPdf.Cli
```

`VellumPdf.Layout` pulls in `VellumPdf.Kernel` as a dependency, so most apps only need the
first line. [Quick start](#quick-start) below shows `VellumPdf.Layout`/`VellumPdf.Kernel` in
use, and [Barcodes](#barcodes) shows `VellumPdf.Barcodes`.

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

### Barcodes

```shell
dotnet add package VellumPdf.Barcodes
```

```csharp
using VellumPdf.Barcodes;

// Flow API: adds a QR Code to the document like any other element.
doc.Add(new QrCode("https://example.com") { TargetWidth = 120 });

// Low-level API: draws directly onto a PdfCanvas, with human-readable text below the bars.
canvas.DrawBarcode(new EanBarcode(EanSymbology.Ean13, "400638133393"), 72, 700, font);
```

QR (including Micro QR), PDF417, Code 128, EAN-13/EAN-8/UPC-A, and ITF-14 are all covered; see
[docs/barcodes-guide.md](docs/barcodes-guide.md) for sizing, human-readable text, GS1/FNC1, and
the QR charset policy. QR Code is a registered trademark of DENSO WAVE INCORPORATED.

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
- **zxing-cpp** — decode round-trip for every barcode symbology: each generated PDF is
  rasterized with `pdftoppm` and decoded back, so the CI oracle catches encoding regressions a
  unit test's own vectors could miss.

## Command-line preflight

`vellum-preflight` validates a PDF against PDF/A-2b/2u/2a and PDF/UA-1 in-process — no JVM or Docker.
Install it as a cross-platform .NET tool, or download a self-contained native binary (Windows x64/Arm64,
macOS Arm64, Linux x64) from the [latest release](https://github.com/Tim81/VellumPDF/releases/latest).

```shell
dotnet tool install -g VellumPdf.Cli   # then run: vellum-preflight <file>
```

```shell
# Does the PDF honour the conformance level it claims? (pass/fail, with reasons)
vellum-preflight invoice.pdf

# A specific profile, machine-readable output
vellum-preflight invoice.pdf -p 2u -f json -o report.json

# Validate a whole tree; fail CI on any error
vellum-preflight ./out --recurse --fail-on error -q

# Several profiles at once
vellum-preflight report.pdf -p 2b,2a,ua1

# See exactly what the tool checks for a profile
vellum-preflight --coverage 2b
```

Every report states three things: what failed (rule id, ISO clause, reason, offending object), what
passed, and what was not fully evaluated — so a clean result is never mistaken for an absolute
guarantee. Exit codes are `0` (conformant), `1` (non-conformant), and `2` (usage or I/O error).

## Roadmap

Planned direction, tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones).
These are scopes, not commitments — the milestones carry no due dates, and nothing past 1.11.0
has shipped yet.

| Milestone | Scope |
| --- | --- |
| **1.11 — Barcodes completeness** (this release) | Closes the #155 completeness backlog: QR Kanji mode, QR Structured Append, Compact (Truncated) PDF417, Macro PDF417, and Code 128 FNC4 / extended Latin-1. |
| **2.0 — Breaking changes** | Strong-named assemblies (#53) and an async I/O surface for `Save`/`Sign`/loaders (#54); both change assembly identity or the public contract, so they wait for a major version. |
| **2.1 — PDF reader (structural)** | `VellumPdf.Reader` grows classic and cross-reference-stream parsing, object streams, and encryption support, with a fixture corpus proving it against real-world files (Epic #100). |
| **2.2 — PDF content extraction** | Text and image extraction on top of the reader. |
| **3.0 — Read-modify-write** | A unified round-trip document model that supersedes the write-once `PdfDocument`, so existing PDFs can be opened, edited, and saved back (Epic #101). |

`VellumPdf.Reader` and `VellumPdf.Conformance` are marked Preview in the table above; expect
their public surfaces to settle as these milestones land.

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
independent implementation written from open published specifications and,
for patented barcode symbologies, the original patents; no third-party source
is copied. See [NOTICE](NOTICE) and [docs/architecture.md](docs/architecture.md)
for the full provenance statement.
