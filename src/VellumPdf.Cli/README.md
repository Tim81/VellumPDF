# VellumPdf.Cli

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Cli.svg)](https://www.nuget.org/packages/VellumPdf.Cli)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

`vellum-preflight` — the command-line PDF/A and PDF/UA validator from **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10. It checks a PDF against PDF/A-2b/2u/2a and PDF/UA-1 with the in-process `VellumPdf.Conformance` engine, with no JVM and no Docker.

- Ships as a cross-platform .NET tool and as self-contained Native-AOT binaries.
- Text, JSON, and SARIF 2.1.0 output; file, glob, directory, and stdin inputs.
- Exit codes: `0` (conformant), `1` (non-conformant), `2` (usage or I/O error).

## Install

```shell
dotnet tool install -g VellumPdf.Cli
```

Or download a self-contained native binary (Windows x64/Arm64, macOS Arm64, Linux x64) from the [latest release](https://github.com/Tim81/VellumPDF/releases/latest).

## Usage

```shell
# Does the PDF honour the conformance level it claims? (pass/fail, with reasons)
vellum-preflight invoice.pdf

# A specific profile, machine-readable output
vellum-preflight invoice.pdf -p 2u -f json -o report.json

# Validate a whole tree; fail CI on any error
vellum-preflight ./out --recurse --fail-on error -q

# Several profiles at once
vellum-preflight report.pdf -p 2b,2a,ua1
```

## Documentation

Command-line reference: <https://github.com/Tim81/VellumPDF#command-line-preflight>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs; exposes catalog, signatures, and streams. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A and PDF/UA preflight validation. |
| **VellumPdf.Cli** (this package) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| [VellumPdf.Barcodes](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | QR, Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC, and ITF-14 as vectors. |

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
