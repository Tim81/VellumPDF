# VellumPdf.Signing

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Signing.svg)](https://www.nuget.org/packages/VellumPdf.Signing)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

The digital-signature add-on for **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10 implemented clean-room from ISO 32000. It applies PAdES / PKCS#7 detached CMS signatures to a `VellumPdf.Kernel.PdfDocument` or a `VellumPdf.Layout.Document`.

- PAdES levels B-B, B-T (RFC-3161 signature timestamp), B-LT (embedded OCSP/CRL in a `/DSS`), and B-LTA (archive document timestamp).
- Pluggable timestamp (`ITimestampClient`) and revocation (`IRevocationClient`) clients, with HTTP implementations included.
- Signs with HSM/PKCS#11/cloud-KMS certificates: `PdfSignatureSettings.ExternalPrivateKey` for a local synchronous key, or `ExternalSigner` (an `IExternalSigner`, via `SignAsync`) for a KMS whose signing call is a real network round-trip (Azure Key Vault, AWS KMS, GCP KMS).
- Keeps the core zero-dependency: this package is the only one that references `System.Security.Cryptography.Pkcs`.

## Install

```shell
dotnet add package VellumPdf.Signing
```

## Usage

```csharp
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Fonts;        // Standard14
using VellumPdf.Layout;       // Document
using VellumPdf.Layout.Core;  // TextStyle
using VellumPdf.Layout.Elements;
using VellumPdf.Signing;

using var doc = new Document();
doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
doc.Add(new Paragraph("Signed with VellumPdf."));

var settings = new PdfSignatureSettings
{
    Certificate = X509CertificateLoader.LoadPkcs12FromFile("signer.pfx", "password"),
    Level = PadesLevel.B_T,
    TimestampClient = new HttpTimestampClient(new Uri("https://timestamp.example/tsa")),
    Reason = "Approved",
};

using var output = File.OpenWrite("signed.pdf");
doc.Sign(output, settings);
```

## Documentation

Package family and project home: <https://github.com/Tim81/VellumPDF>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| **VellumPdf.Signing** (this package) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
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
