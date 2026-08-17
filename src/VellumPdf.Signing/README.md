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
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs; exposes catalog, signatures, and streams. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A and PDF/UA preflight validation. |
| [VellumPdf.Cli](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| [VellumPdf.Barcodes](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | QR, Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC, and ITF-14 as vectors. |

## Roadmap

| Milestone | Scope |
| --- | --- |
| **2.0 — Breaking changes** (this release) | Strong-named assemblies (#53), an async I/O surface for `Save`/`Sign`/loaders (#54), and an external-signer API for cloud KMS and remote HSM signing (#165). Each changes assembly identity or the public contract, so they waited for a major version. |
| **2.1 — PDF reader (structural)** | `VellumPdf.Reader` grows classic and cross-reference-stream parsing, object streams, and encryption support, with a fixture corpus proving it against real-world files (Epic #100). |
| **2.2 — PDF content extraction** | Text and image extraction on top of the reader. |
| **3.0 — Read-modify-write** | A unified round-trip document model that supersedes the write-once `PdfDocument`, so existing PDFs can be opened, edited, and saved back (Epic #101). |

## License

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
