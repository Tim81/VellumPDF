# VellumPdf.Conformance

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Conformance.svg)](https://www.nuget.org/packages/VellumPdf.Conformance)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

In-process PDF/A and PDF/UA preflight validation for **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10. It opens a PDF through `VellumPdf.Reader` and runs a registry of clean-room conformance rules authored from the ISO specifications, with no external veraPDF Docker image required.

- Validates PDF/A-2b/2u/2a and PDF/UA-1.
- Returns machine-readable assertions: rule id, ISO clause, severity, and object reference.
- BCL-only, AOT- and trim-ready — rules are registered explicitly, with no reflection.
- Build-verified veraPDF parity is about 99%; every rule's positive and negative paths are cross-checked against veraPDF in CI.

> **Preview.** Rule coverage is still growing toward complete PDF/A-2b/2u/2a and PDF/UA-1.

## Install

```shell
dotnet add package VellumPdf.Conformance
```

## Usage

```csharp
using VellumPdf.Conformance;

var result = PdfPreflight.Validate(File.OpenRead("input.pdf"), PdfConformance.PdfA2B);

if (!result.IsCompliant)
    foreach (var assertion in result.Assertions)
        Console.WriteLine($"{assertion.Severity} {assertion.RuleId} ({assertion.Clause}): {assertion.Message}");
```

For a ready-made command-line validator, install [`VellumPdf.Cli`](https://www.nuget.org/packages/VellumPdf.Cli) (`vellum-preflight`).

## Documentation

Architecture and validator scope: <https://github.com/Tim81/VellumPDF/blob/main/docs/architecture.md>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs; exposes catalog, signatures, and streams. |
| **VellumPdf.Conformance** (this package) | Preview | In-process PDF/A and PDF/UA preflight validation. |
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

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
