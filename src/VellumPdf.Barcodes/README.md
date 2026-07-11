# VellumPdf.Barcodes

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Barcodes.svg)](https://www.nuget.org/packages/VellumPdf.Barcodes)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

The barcode add-on for **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10. Eleven symbologies rendered as vector rectangles (never a raster image), so they stay crisp at any zoom and print resolution.

- QR (including Micro QR M1–M4 and GS1-mode QR with GS1 Digital Link), PDF417, Code 128 (plain and GS1-128), Code 39 (including Full ASCII), EAN-13/EAN-8/UPC-A/UPC-E with EAN-2/EAN-5 add-ons, ITF-14, Data Matrix (including GS1 Data Matrix), and Aztec Code.
- QR chooses version, data mask, and error-correction level automatically, and supports an Auto / Latin-1 / UTF-8 / UTF-8+ECI charset policy; PDF417 compacts text, byte, and numeric content automatically.
- Two API tiers: a low-level `PdfCanvas.DrawBarcode` for precise placement, and a `Document.Add(Barcode)` flow element that handles sizing, pagination, alignment, and tagging.
- Round-trip decoding is verified in CI for every symbology against zxing-cpp.

## Install

```shell
dotnet add package VellumPdf.Barcodes
```

## Usage

```csharp
using VellumPdf.Barcodes;
using VellumPdf.Layout;

using var doc = new Document();

// Flow API: add a barcode like any other document element.
doc.Add(new QrCode("https://example.com") { TargetWidth = 120 });

// Low-level API draws straight onto a PdfCanvas, with human-readable text below the bars:
//   canvas.DrawBarcode(new EanBarcode(EanSymbology.Ean13, "400638133393"), 72, 700, font);

doc.Save("barcodes.pdf");
```

## Documentation

Barcodes guide (sizing, human-readable text, GS1/FNC1, QR charset policy): <https://github.com/Tim81/VellumPDF/blob/main/docs/barcodes-guide.md>

QR Code is a registered trademark of DENSO WAVE INCORPORATED.

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs; exposes catalog, signatures, and streams. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Preview | In-process PDF/A and PDF/UA preflight validation. |
| [VellumPdf.Cli](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| **VellumPdf.Barcodes** (this package) | Stable | QR, Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC, and ITF-14 as vectors. |

## Roadmap

| Milestone | Scope |
| --- | --- |
| **1.11 — Barcodes completeness** (this release) | Closes the #155 completeness backlog: QR Kanji mode, QR Structured Append, Compact (Truncated) PDF417, Macro PDF417, and Code 128 FNC4 / extended Latin-1. |
| **2.0 — Breaking changes** | Strong-named assemblies (#53) and an async I/O surface for `Save`/`Sign`/loaders (#54); both change assembly identity or the public contract, so they wait for a major version. |
| **2.1 — PDF reader (structural)** | `VellumPdf.Reader` grows classic and cross-reference-stream parsing, object streams, and encryption support, with a fixture corpus proving it against real-world files (Epic #100). |
| **2.2 — PDF content extraction** | Text and image extraction on top of the reader. |
| **3.0 — Read-modify-write** | A unified round-trip document model that supersedes the write-once `PdfDocument`, so existing PDFs can be opened, edited, and saved back (Epic #101). |

## License

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
