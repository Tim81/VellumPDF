# VellumPdf.Kernel

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Kernel.svg)](https://www.nuget.org/packages/VellumPdf.Kernel)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

The low-level PDF generation kernel of **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10 implemented clean-room from ISO 32000. This is the object model and content-stream writer everything else builds on. It has **zero runtime dependencies** and is AOT- and trim-ready.

- A `PdfCanvas` for precise drawing: text, paths, images, colour, and graphics state.
- Fonts: the built-in Standard-14 faces plus TrueType and OpenType-CFF embedding and subsetting, with `ToUnicode` maps so text stays searchable and copy-paste-able.
- Images: JPEG, PNG, BMP, GIF, TIFF, JBIG2, and JPEG 2000.
- AES-256 encryption, object and cross-reference streams, AcroForm fields, a tagged-PDF structure tree, PDF/A-2 metadata, DeviceCMYK and ICC colour with output intents, and opt-in linearization.

## Install

```shell
dotnet add package VellumPdf.Kernel
```

## Usage

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

For most documents the high-level `VellumPdf.Layout` builder is easier; reach for the Kernel when you need direct control of the content stream.

## Documentation

Kernel walkthrough: <https://github.com/Tim81/VellumPDF/blob/main/docs/kernel-guide.md>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| **VellumPdf.Kernel** (this package) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
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
| **2.1 — PDF reader (structural)** | `VellumPdf.Reader` reads classic tables, cross-reference and object streams, and hybrid-reference files, and opens encrypted documents given their password (#97) — the Standard security handler at `/V` 1, 2, 4 and 5, proven against a committed fixture corpus and third-party files (Epic #100). |
| **2.2 — Encryption and parser hardening** (this release) | An empty owner password no longer produces a document anyone opens at owner privilege (#211), and building a very large dictionary is no longer quadratic on the path that runs before a password is checked (#208). Carries a documented breaking change, so it is a minor rather than a patch release. |
| **2.3 — PDF content extraction** | Text and image extraction on top of the reader. Writing a decrypted copy (#186), reader fuzzing (#99), and graduating `VellumPdf.Reader` from Preview (#187) ride along. |
| **2.4 — PDF/A-1 profile** | A full PDF/A-1 (ISO 19005-1) rule set, which unblocks recursive validation of embedded PDF/A-1 files (#140). |
| **3.0 — Read-modify-write** | A unified round-trip document model that supersedes the write-once `PdfDocument`, so existing PDFs can be opened, edited, and saved back (Epic #101). |

## License

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
