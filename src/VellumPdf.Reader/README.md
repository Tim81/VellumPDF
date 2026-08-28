# VellumPdf.Reader

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Reader.svg)](https://www.nuget.org/packages/VellumPdf.Reader)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

The PDF reader of **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10 implemented clean-room from ISO 32000. It opens an existing PDF on a bounds-checked lexer and object parser and exposes its structure. BCL-only, with zero runtime dependencies.

- Classic cross-reference tables, cross-reference and object streams, and hybrid-reference files.
- Encrypted documents: the Standard security handler at `/V` 1, 2, 4 and 5 and `/R` 2 through 6 —
  RC4-40 through RC4-128, AES-128 and AES-256 — opened with `PdfReader.Open(bytes, password)`.
- Exposes the document catalog and digital signatures. Stream decoding is internal for now: the
  public surface reads structure and signatures, not page content — see the roadmap below.
- The foundation for the signing long-term-validation path, the `VellumPdf.Conformance` validator, and a growing general reader.

> **Preview.** The public surface is still settling; content extraction is on the roadmap (Epic #100). See the roadmap below.

## Install

```shell
dotnet add package VellumPdf.Reader
```

## Usage

```csharp
using VellumPdf.Reader;

using var reader = PdfReader.Open(File.OpenRead("input.pdf"));

VellumPdf.Core.PdfDictionary catalog = reader.Catalog;   // the document catalog
foreach (var signature in reader.Signatures)             // any digital signatures
    Console.WriteLine(signature.SubFilter);
```

## Documentation

Architecture and reader scope: <https://github.com/Tim81/VellumPDF/blob/main/docs/architecture.md>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| **VellumPdf.Reader** (this package) | Preview | Opens existing PDFs, encrypted ones given their password; exposes catalog and signatures. |
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
