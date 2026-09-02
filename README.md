# VellumPdf

[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Kernel.svg?label=VellumPdf.Kernel)](https://www.nuget.org/packages/VellumPdf.Kernel)
[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Conformance.svg?label=VellumPdf.Conformance)](https://www.nuget.org/packages/VellumPdf.Conformance)

A modern, **dependency-free PDF generation library for .NET 10**, implemented
clean-room from the open **ISO 32000** standard.

> **Status: stable.** The public API is locked (analyzer-enforced) and
> the library targets .NET 10. Core features are CI-validated — including
> PDF/A-2a/2b/2u and PDF/UA-1 conformance proven on every push with veraPDF.

## Why VellumPdf

- **Zero runtime dependencies.** The core is built entirely on the .NET base
  class library — no native binaries, no third-party packages. (The optional
  signing package is the sole exception: it uses `System.Security.Cryptography.Pkcs`.)
- **AOT- and trim-ready.** Ships as managed DLLs; ideal for Native AOT,
  trimming, containers, and serverless. A Native-AOT smoke test guards this.
- **Unicode-first text.** Embeds and subsets TrueType and OpenType-CFF fonts,
  emits composite (CID) fonts with subset tags, and writes `ToUnicode` maps so
  output stays searchable and copy-paste-able.
- **Two API tiers.** A low-level canvas for precise drawing, and a high-level
  document/layout engine with paragraphs, headings, lists, tables, images, and
  automatic pagination.
- **Built for the hard standards.** PDF/A-2a (accessible archival) and PDF/UA-1
  (universal accessibility) are implemented and CI-validated with veraPDF, alongside
  PDF/A-2b/2u, PAdES digital signatures, and interactive AcroForms.
- **Permissive license.** Apache-2.0 — free to use in proprietary products.

## Packages

| Package | NuGet | Status | Description |
| --- | --- | --- | --- |
| `VellumPdf.Kernel` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Kernel.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Object model, canvas, Standard-14 fonts, TrueType/OpenType embedding + subsetting, images (JPEG/PNG/BMP/GIF/TIFF/JBIG2/JPEG 2000), AES-256 encryption, object/cross-reference streams, AcroForm fields, tagged-PDF structure tree, PDF/A-2 metadata, DeviceCMYK and ICC-based colour with configurable output intents, and opt-in linearization (`Linearize`) for fast-web-view first-page rendering. |
| `VellumPdf.Fonts.Standard14` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Fonts.Standard14.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Optional, embeddable metric-compatible substitutes for the standard-14 fonts (Liberation Sans/Serif/Mono, SIL OFL 1.1, covering the Helvetica/Times/Courier families). The built-in `Standard14` fonts are not embedded — fine for ordinary PDFs but disallowed by PDF/A's font-embedding rule; `doc.EmbedStandard14Font(...)` registers a subset, embedded substitute so standard-14-style text is PDF/A-conformant without a caller-supplied font program. (Symbol and ZapfDingbats are not covered.) |
| `VellumPdf.Layout` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Layout.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, headings, lists, tables, images, pie charts, header/footer bands, bookmarks, and automatic pagination. |
| `VellumPdf.Signing` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Signing.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 detached digital signatures with RFC-3161 signature timestamps and long-term validation. Levels B-T, B-LT (embedded OCSP/CRL in a `/DSS`), and B-LTA (archive document timestamp), via pluggable timestamp and revocation clients. |
| `VellumPdf.Reader` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Reader.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs (classic cross-reference tables, cross-reference and object streams, hybrid-reference files, and encrypted documents given their password), with optional cross-reference reconstruction and tighten-only resource limits for damaged or hostile input, and exposes the catalog and digital signatures. Writes a decrypted copy of an encrypted document (`SaveDecrypted`). The basis for the signing LTV path, the conformance validator, and a general reader. |
| `VellumPdf.Conformance` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Conformance.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A-2b/2u/2a and PDF/UA-1 preflight: runs clean-room conformance rules authored from the ISO specifications and returns machine-readable assertions (rule id, ISO clause, severity, object reference) — no external veraPDF Docker image needed. AOT- and trim-ready (rules registered explicitly, no reflection). Covers file structure, colour and output intents (including ICC profile validity and ICCBased-CMYK overprint), transparency, images and XObjects (including a JPEG2000 codestream parser), fonts (an in-process sfnt font-program parser for glyph presence and widths, embedded-CMap CID/WMode/usecmap checks), content streams (ISO 32000-1 operator, inline-image-filter, and graphics-state validation), digital signatures (a zero-dependency CMS/ASN.1 reader for §6.4.3), annotations, interactive forms, actions, and XMP metadata (via an in-process XMP parser), plus the 2u/2a deltas and a tagged-structure walker for the PDF/UA-1 (ISO 14289-1) accessibility checks. Build-verified veraPDF parity is about 99% for PDF/A-2b/2u/2a and PDF/UA-1; the only gaps are four checks tracked in follow-up issues — three with a predefined-CJK-CMap sub-condition that needs a conformant CJK font asset to cross-validate (#139), and one that needs a PDF/A-1 profile to validate embedded PDF/A-1 recursively (#140). Every rule's positive and negative paths are cross-validated against veraPDF in CI. |
| `VellumPdf.Cli` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Cli.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | The `vellum-preflight` command-line validator: checks a PDF against PDF/A-2b/2u/2a and PDF/UA-1 with the in-process `VellumPdf.Conformance` engine — no JVM or Docker. Ships as a cross-platform .NET tool (`dotnet tool install -g VellumPdf.Cli`) and self-contained Native-AOT binaries. Text, JSON, and SARIF 2.1.0 output; file, glob, directory, and stdin inputs; exit codes `0` (conformant), `1` (non-conformant), `2` (usage or I/O error). |
| `VellumPdf.Barcodes` | [![NuGet](https://img.shields.io/nuget/v/VellumPdf.Barcodes.svg?label=%20)](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | Eleven symbologies: QR (including Micro QR and GS1 mode with Digital Link), Data Matrix (including GS1 Data Matrix), Aztec, PDF417, Code 128 (plain and GS1-128), Code 39 (including Full ASCII), EAN-13/EAN-8/UPC-A/UPC-E with EAN-2/EAN-5 add-ons, and ITF-14, rendered as vector rectangles through a low-level `PdfCanvas` extension or the `Document.Add` flow API. Round-trip decoding is verified in CI against zxing-cpp. |

### Install

```shell
# Core
dotnet add package VellumPdf.Layout
dotnet add package VellumPdf.Kernel

# Add-ons
dotnet add package VellumPdf.Signing
dotnet add package VellumPdf.Fonts.Standard14
dotnet add package VellumPdf.Barcodes
dotnet add package VellumPdf.Conformance

# Preview
dotnet add package VellumPdf.Reader

# Tooling (.NET global tool)
dotnet tool install -g VellumPdf.Cli
```

`VellumPdf.Layout` pulls in `VellumPdf.Kernel` as a dependency, so most apps only need the
first line. [Quick start](#quick-start) below shows `VellumPdf.Layout`/`VellumPdf.Kernel` in
use, and [Barcodes](#barcodes) shows `VellumPdf.Barcodes`.

## Upgrading to 2.0

Every package is strong-named from 2.0 onward, which changes assembly identity. The new
identity is:

```
VellumPdf.Kernel, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b2757187a6d18ae5
```

All eight packages share that public key token, and `AssemblyVersion` is pinned to `2.0.0.0`
for the whole 2.x line, so servicing releases do not force a rebind.

Ordinary `PackageReference` consumers need to do nothing — the SDK resolves the new identity
on restore. Rebinding is only needed where an assembly identity is written down by hand:

- an `<AssemblyIdentity>` binding redirect or `<dependentAssembly>` element naming
  `PublicKeyToken="null"`;
- `[assembly: InternalsVisibleTo("...")]` targeting a VellumPdf assembly, which must now carry
  the full public key;
- `Assembly.Load` with an explicit `PublicKeyToken=null` in the string;
- a plugin host or serializer configured with fully-qualified type names.

A 1.x and a 2.x assembly are different identities, so both can be loaded side by side if a
transitive dependency still needs the old one.

## Quick start

```shell
dotnet add package VellumPdf.Layout
```

```csharp
using VellumPdf.Document;          // PdfConformance
using VellumPdf.Fonts;             // Standard14
using VellumPdf.Layout;            // Document
using VellumPdf.Layout.Core;       // TextStyle
using VellumPdf.Layout.Elements;   // Paragraph, Heading
using VellumPdf.Layout.Elements.Table; // TableElement

// Basic document — defaults to A4.
using var doc = new Document();
doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
doc.Add(new Heading("Hello, world!"));
doc.Add(new Paragraph("Generated with VellumPdf — no native dependencies."));
doc.Save("hello.pdf");
```

```csharp
// PDF/A-2b archival document. PDF/A requires every glyph to come from an
// embedded font, so load a TrueType font and use it for all text.
using var archive = new Document { Conformance = PdfConformance.PdfA2b };
var font = archive.LoadTrueTypeFont("/path/to/DejaVuSans.ttf");
var style = new TextStyle { FontRef = font, FontSize = 12 };

archive.Add(new Paragraph("This document validates as PDF/A-2b.", style));

var table = new TableElement { DefaultCellStyle = style };
table.SetColumnWidths(200, 200);
table.AddHeaderRow().AddCell("Item").AddCell("Value");
table.AddRow().AddCell("Format").AddCell("PDF/A-2b");
archive.Add(table);

archive.Save("archive.pdf");
```

### Low-level Kernel API

For precise canvas control, bypass the Layout engine and write directly to the
PDF content stream:

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

For a full walkthrough of the canvas, graphics primitives, and font handling,
see [docs/kernel-guide.md](docs/kernel-guide.md).

### Barcodes

```shell
dotnet add package VellumPdf.Barcodes
```

```csharp
using VellumPdf.Barcodes;

// Flow API: adds a QR Code to the document like any other element.
doc.Add(new QrCode("https://example.com") { TargetWidth = 120 });

// Low-level API: draws directly onto a PdfCanvas, with human-readable text below the bars.
canvas.DrawBarcode(new EanBarcode(EanSymbology.Ean13, "400638133393"), 72, 700, font);
```

QR (including Micro QR), PDF417, Code 128, EAN-13/EAN-8/UPC-A, and ITF-14 are all covered; see
[docs/barcodes-guide.md](docs/barcodes-guide.md) for sizing, human-readable text, GS1/FNC1, and
the QR charset policy. QR Code is a registered trademark of DENSO WAVE INCORPORATED.

## Conventions

- **Units.** All coordinates and sizes are in PDF user-space **points** (1 pt = 1/72 inch).
  `PageSize` provides the common ISO-A sizes plus a `PageSize.Mm(width, height)` helper for
  custom millimetre dimensions.
- **Synchronous and asynchronous I/O.** `Save`, `Sign`, and `LoadTrueTypeFont` each have an
  `*Async` counterpart (`SaveAsync`, `SignAsync`, `LoadTrueTypeFontAsync`) that accepts a
  `CancellationToken`. Document serialization is CPU-bound, so the async methods offload it to
  a thread-pool thread; the token is honoured before that work starts and during the final
  write, but does not interrupt serialization already in progress. `SignAsync` performs
  non-blocking network calls for PAdES B-T/B-LT/B-LTA timestamp and revocation lookups. Image
  loaders remain synchronous: they parse in-memory bytes and never touch disk or the network.
- **Signing with HSM/PKCS#11/cloud-KMS certificates.** `PdfSignatureSettings.Certificate` must
  normally include its own private key, but `PdfSignatureSettings.ExternalPrivateKey` accepts a
  separate `RSA`/`ECDsa` object for certificates whose key isn't attached to the
  `X509Certificate2` — for example a certificate fetched from Azure Key Vault, AWS KMS, or a
  PKCS#11 device (via a library such as `Pkcs11Interop.X509Store`). `Certificate` is still
  required in that case, for the public key, subject, and certificate chain. Windows-attached
  smart cards and CNG-integrated hardware tokens already work without this: loading the
  certificate from `X509Store` returns a CNG-backed private key that plugs into the normal
  `Certificate`-only path.
- **Signing with an async cloud KMS or remote HSM.** `ExternalPrivateKey` still needs a
  synchronous `RSA`/`ECDsa` object, so a KMS whose signing call is a network round-trip (Azure
  Key Vault, AWS KMS, GCP KMS) can only be bridged by blocking a thread. To avoid that, set
  `PdfSignatureSettings.ExternalSigner` to an `IExternalSigner` and sign with `SignAsync`:
  VellumPdf computes the CMS signed-attributes digest, hands it to the async signer, and
  assembles the resulting signature itself. The synchronous `Sign` overloads throw when
  `ExternalSigner` is set. `EcdsaSignatureConverter.RawToDer` converts the raw ECDSA signature
  format Azure Key Vault returns into the DER encoding CMS requires.

## Validation & CI

Correctness is enforced on every push by running real external validators as CI
oracles — a missing tool fails the build, so the gates can never silently skip:

- **`qpdf --check`** — structural integrity of every generated document type.
- **`pdftotext`** (poppler) — text-extraction round-trip proving `ToUnicode` maps.
- **`pdfsig`** (poppler) — signature validity for PAdES documents.
- **veraPDF** (official `verapdf/cli` Docker image) — strict **PDF/A-2a/2b/2u and PDF/UA-1**
  conformance over embedded-font, table, image, and tagged documents. A
  non-compliant report fails CI with the full rule list attached.
- **zxing-cpp** — decode round-trip for every barcode symbology: each generated PDF is
  rasterized with `pdftoppm` and decoded back, so the CI oracle catches encoding regressions a
  unit test's own vectors could miss.

## Command-line preflight

`vellum-preflight` validates a PDF against PDF/A-2b/2u/2a and PDF/UA-1 in-process — no JVM or Docker.
Install it as a cross-platform .NET tool, or download a self-contained native binary (Windows x64/Arm64,
macOS Arm64, Linux x64) from the [latest release](https://github.com/Tim81/VellumPDF/releases/latest).

```shell
dotnet tool install -g VellumPdf.Cli   # then run: vellum-preflight <file>
```

```shell
# Does the PDF honour the conformance level it claims? (pass/fail, with reasons)
vellum-preflight invoice.pdf

# A specific profile, machine-readable output
vellum-preflight invoice.pdf -p 2u -f json -o report.json

# Validate a whole tree; fail CI on any error
vellum-preflight ./out --recurse --fail-on error -q

# Several profiles at once
vellum-preflight report.pdf -p 2b,2a,ua1

# See exactly what the tool checks for a profile
vellum-preflight --coverage 2b
```

Every report accounts for each check in the profile's catalogue exactly once, so a clean result is
never mistaken for an absolute guarantee:

| Bucket | Meaning |
| --- | --- |
| `failed` | Rules that reported a problem: rule id, ISO clause, reason, offending object. |
| `passed` | Catalogued checks this file satisfies. |
| `failedChecks` | Catalogued checks a failing rule named directly, by test id. |
| `inconclusive` | A rule failed in the same ISO clause but did not say which catalogued check it corresponds to, so the check can be neither claimed nor blamed. |
| `notEvaluated` | Checks the library implements only partially, defers, or puts out of scope. |

`failed` counts rule assertions; the other four count catalogue entries and sum to `summary.total`.
Exit codes are `0` (conformant), `1` (non-conformant), and `2` (usage or I/O error).

## Roadmap

Planned direction, tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones).
These are scopes, not commitments — the milestones carry no due dates. 2.3.0 is the latest
published release.

Scope past 2.5 runs as **two parallel tracks**, because auditing the layout engine turned up more
work than the PDF 2.0 conformance gap did. Both ship from the same version numbers.

**Kernel and conformance**

| Milestone | Scope |
| --- | --- |
| **2.3 — Reader robustness** *(released)* | Cross-reference reconstruction for damaged files (#184), a decrypted-copy writer (#186), the `/XRefStm` precedence decision (#206), reader fuzzing (#99), and the CI and oracle debt that makes all of it verifiable. |
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
[PDF 2.0 conformance](docs/pdf20-conformance.md). It emits a `%PDF-2.0` header, which is not the
same as conformance, and the table says which is which. The [Reader guide](docs/reader-guide.md)
and [Layout guide](docs/layout-guide.md) each carry a capability table doing the same job for
their own package, checked against the code and test suite rather than against this roadmap.

`VellumPdf.Reader` is marked Preview in the table above; expect its public surface to settle
as these milestones land. `VellumPdf.Conformance` graduated to Stable in 2.0.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build VellumPdf.slnx
dotnet test  VellumPdf.slnx
```

The veraPDF conformance gate runs automatically in CI; to reproduce it locally,
install [veraPDF](https://verapdf.org) (or use its Docker image) and either put
the `verapdf` CLI on your `PATH` or point `VERAPDF_HOME` at the install
directory (required on Windows, where the installer's launcher is
`verapdf.bat`, which a bare `verapdf` on `PATH` cannot resolve directly), then
run the oracle tests with `REQUIRE_VERAPDF=1` set.

## License & provenance

Licensed under the [Apache License 2.0](LICENSE). VellumPdf is an original,
independent implementation written from open published specifications and,
for patented barcode symbologies, the original patents; no third-party source
is copied. See [NOTICE](NOTICE) and [docs/architecture.md](docs/architecture.md)
for the full provenance statement.
