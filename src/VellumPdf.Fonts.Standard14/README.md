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
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs, encrypted ones given their password; exposes catalog and signatures. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A and PDF/UA preflight validation. |
| [VellumPdf.Cli](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| [VellumPdf.Barcodes](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | QR, Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC, and ITF-14 as vectors. |

## Roadmap

| Milestone | Scope |
| --- | --- |
| **2.0 — Breaking changes** | Strong-named assemblies (#53), an async I/O surface for `Save`/`Sign`/loaders (#54), and an external-signer API for cloud KMS and remote HSM signing (#165). Each changes assembly identity or the public contract, so they waited for a major version. |
| **2.1 — PDF reader (structural)** (this release) | `VellumPdf.Reader` reads classic tables, cross-reference and object streams, and hybrid-reference files, and opens encrypted documents given their password (#97) — the Standard security handler at `/V` 1, 2, 4 and 5, proven against a committed fixture corpus and third-party files (Epic #100). The 2.1 line continues: writing a decrypted copy (#186) and reader fuzzing (#99) are still open. |
| **2.2 — PDF content extraction** | Text and image extraction on top of the reader. |
| **3.0 — Read-modify-write** | A unified round-trip document model that supersedes the write-once `PdfDocument`, so existing PDFs can be opened, edited, and saved back (Epic #101). |

## License

Apache-2.0 for the code, and SIL OFL 1.1 for the bundled Liberation fonts (`Apache-2.0 AND OFL-1.1`). Source and issues: <https://github.com/Tim81/VellumPDF>
