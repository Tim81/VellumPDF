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

Planned direction, tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones).
These are scopes, not commitments — the milestones carry no due dates, and nothing past 2.2.0
has shipped yet.

Scope past 2.5 runs as **two parallel tracks**, because auditing the layout engine turned up more
work than the PDF 2.0 conformance gap did. Both ship from the same version numbers.

**Kernel and conformance**

| Milestone | Scope |
| --- | --- |
| **2.3 — Reader robustness** | Cross-reference reconstruction for damaged files (#184), a decrypted-copy writer (#186), the `/XRefStm` precedence decision (#206), reader fuzzing (#99), and the CI and oracle debt that makes all of it verifiable. |
| **2.4 — PDF content extraction** | Text and image extraction on top of the reader (#98), and graduating `VellumPdf.Reader` from Preview (#187). |
| **2.5 — PDF/A-4 and PDF/A-1 profiles** | PDF/A-4 (#222) so conformance output stops downgrading to `%PDF-1.7`, PDF/A-1 (#218), and dropping the keys ISO 32000-2 deprecates (#325). |
| **2.6 — ISO/TS extensions to PDF 2.0** | The four Technical Specifications that amend PDF 2.0: AES-GCM (#236), PDF MAC integrity (#237), SHA-3 (#238) and EdDSA (#239). The reader currently rejects every AES-GCM file, so this closes an interoperability bug as well as adding features. |
| **2.7 — Embedded files and associated files** | Attachments, the `/Names` tree, the missing annotation subtypes, and ISO 32000-2 §14.13 associated files. Unblocks PDF/A-4F. |
| **2.8 — Colour and paint** | PDF functions, the full shading model, tiling and shading patterns, and the colour spaces beyond the device ones and ICCBased. |
| **2.9 — Rendering and resources** | Transparency and blend modes, the complete ExtGState, form XObjects, inline images, optional content, and the page boundary boxes. |
| **2.10 — Font completeness** | Type 1, bare CFF, simple TrueType and Type 3 fonts; predefined CMaps and vertical writing; kerning; and enforcing the `fsType` embedding permission the library currently parses and ignores. |
| **2.11 — Tagged PDF 2.0 writer** | Structure namespaces, structure destinations, and the ISO/TS 32005 containment matrix. |
| **2.12 — PDF/UA-2 and WTPDF** | The `ua2`, `wt1a` and `wt1r` rule sets. |
| **2.13 — Signature verification** | Verifying signatures, not only producing them: integrity, coverage, chains, revocation and achieved PAdES level. |

**Layout**

| Milestone | Scope |
| --- | --- |
| **Layout A — Foundations** | Sections, a containment model, inline runs in every element, a unit type, named styles, renderer extensibility, and fixing a defect where an element spanning a page break emits two structure elements instead of one. |
| **Layout B — Accessibility structure** | The PDF/UA-2 element set, generated contents and indexes, captions, table header semantics, footnotes and endnotes, automatic numbering, tab stops with leaders, and internal links. |
| **Layout C — Forms and semantics** | Form elements that carry their own labels, ListNumbering fidelity, artifact property lists, and build-time accessibility diagnostics. |
| **Layout D — Typography** | Text decoration, optimal line breaking, widow and orphan control, hyphenation and floats, cell borders, and inline content such as images in a line of text. |
| **Layout E — Composition** | Composite elements and templates, page breaks, anchoring and rotation, page backgrounds, running bands that hold more than a single string, and prepress bleed and printer marks. |
| **Layout F — Advanced layout** | Multi-column flow, nested tables, a chart family, streaming generation, and the SVG import epic. |
| **Layout G — International text** | Unicode bidi and right-to-left, vertical and CJK writing, and the complex-script shaping epic. |

**Editing existing documents.** The only breaking milestone is 3.0.

| Milestone | Scope |
| --- | --- |
| **3.0 — Round-trip document model** | A new round-trip type alongside the write-once `PdfDocument`, a memory-mapped backing store, and the new `VellumPdf.Editing` package (Epic #101). |
| **3.1 — Sign and fill existing documents** | Signing a PDF this library did not write, and filling its form fields. Neither is possible today. |
| **3.2 — Append-only content edits** | Annotations, metadata, watermarks, attachments, and appending flowed content. |
| **3.3 — Full-rewrite operations** | Revision-history discard, re-encryption, and signature removal. |
| **3.4 — Structural editing** | Page operations, merge and split, optimise and sanitise. |
| **3.5 — Redaction** | Marking regions and applying them. Deliberately isolated: marking alone is not redaction. |
| **3.6 — Content editing** | Reflowing text edits, and structure-tree round-trip editing of tagged documents. |

What this library implements of ISO 32000-2 today is inventoried, reference by reference, in
[PDF 2.0 conformance](../../docs/pdf20-conformance.md). It emits a `%PDF-2.0` header, which is not the
same as conformance, and the table says which is which.

`VellumPdf.Reader` is marked Preview in the table above; expect its public surface to settle
as these milestones land. `VellumPdf.Conformance` graduated to Stable in 2.0.

## License

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
