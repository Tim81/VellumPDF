# VellumPdf.Fonts.Standard14

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Fonts.Standard14.svg)](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

Embeddable, metric-compatible substitutes for the PDF standard-14 fonts, an add-on for **[VellumPdf](https://github.com/Tim81/VellumPDF)** (a dependency-free PDF library for .NET 10).

The built-in `Standard14` faces are not embedded, which is fine for ordinary PDFs but disallowed by PDF/A's font-embedding rule. This package bundles the Liberation fonts (Sans/Serif/Mono, SIL OFL 1.1), which cover the Helvetica, Times, and Courier families, and adds a `doc.EmbedStandard14Font(...)` extension that registers a subset, embedded substitute. So standard-14-style text becomes PDF/A-conformant without a caller-supplied font program. (Symbol and ZapfDingbats are not covered.)

## Install

```shell
dotnet add package VellumPdf.Fonts.Standard14
```

## Usage

```csharp
using VellumPdf.Document;   // PdfDocument
using VellumPdf.Fonts;      // Standard14

using var doc = new PdfDocument();

// A subset, embedded Liberation substitute for a standard-14 face.
var helvetica = doc.EmbedStandard14Font(Standard14.Helvetica);

// Draw with `helvetica` on a PdfCanvas: the glyphs are embedded and subset, so the
// text satisfies PDF/A's font-embedding rule without a caller-supplied font file.
```

## Documentation

Package family and project home: <https://github.com/Tim81/VellumPDF>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| **VellumPdf.Fonts.Standard14** (this package) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs; exposes catalog, signatures, and streams. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Preview | In-process PDF/A and PDF/UA preflight validation. |
| [VellumPdf.Cli](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| [VellumPdf.Barcodes](https://www.nuget.org/packages/VellumPdf.Barcodes) | Preview | QR, PDF417, Code 128/GS1-128, EAN/UPC, and ITF-14 as vectors. |

## Roadmap

| Milestone | Scope |
| --- | --- |
| **1.9 — Barcodes** (this release) | `VellumPdf.Barcodes` (#51): QR, Micro QR, PDF417, Code 128/GS1-128, EAN/UPC, and ITF-14. |
| **2.0 — Breaking changes** | Strong-named assemblies (#53) and an async I/O surface for `Save`/`Sign`/loaders (#54); both change assembly identity or the public contract, so they wait for a major version. |
| **2.1 — PDF reader (structural)** | `VellumPdf.Reader` grows classic and cross-reference-stream parsing, object streams, and encryption support, with a fixture corpus proving it against real-world files (Epic #100). |
| **2.2 — PDF content extraction** | Text and image extraction on top of the reader. |
| **3.0 — Read-modify-write** | A unified round-trip document model that supersedes the write-once `PdfDocument`, so existing PDFs can be opened, edited, and saved back (Epic #101). |
| **Barcodes — GS1 & 2D expansion** | GS1 Data Matrix (#151), GS1-mode QR (#152), Aztec (#153), and Code 39 (#154), with smaller completeness items tracked in #155. |

## License

Apache-2.0 for the code, and SIL OFL 1.1 for the bundled Liberation fonts (`Apache-2.0 AND OFL-1.1`). Source and issues: <https://github.com/Tim81/VellumPDF>
