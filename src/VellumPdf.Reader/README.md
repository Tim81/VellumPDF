# VellumPdf.Reader

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Reader.svg)](https://www.nuget.org/packages/VellumPdf.Reader)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

The PDF reader of **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10 implemented clean-room from ISO 32000. It opens an existing PDF on a bounds-checked lexer and object parser and exposes its structure. BCL-only, with zero runtime dependencies.

- Classic cross-reference tables, cross-reference and object streams, and hybrid-reference files.
- Encrypted documents: the Standard security handler at `/V` 1, 2, 4 and 5 and `/R` 2 through 6 —
  RC4-40 through RC4-128, AES-128 and AES-256 — opened by passing a `PdfReaderOptions` whose
  `Password` is set.
- Optional cross-reference reconstruction (`PdfReaderOptions.AllowReconstruction`) for a document
  whose `startxref` is missing or broken: rebuilds the table by scanning the file for object
  headers, plaintext documents only, refusing outright the instant it finds any sign the document
  is encrypted rather than guessing at a key. `PdfDocumentReader.WasReconstructed` reports whether
  a given document took this path.
- Tighten-only resource limits (`PdfReaderOptions.MaxDecodedStreamBytes`,
  `ReconstructionBudgetMultiplier`) for a caller hardening against a decompression bomb or a file
  engineered to burn CPU — both can only lower the shipped default, never raise it.
- Exposes the document catalog and digital signatures. Stream decoding is internal for now: the
  public surface reads structure and signatures, not page content — see the roadmap below.
- Writes a decrypted copy of an encrypted document (`PdfDocumentReader.SaveDecrypted`), refusing a
  signed document unless the caller opts into invalidating its signatures.
- The foundation for the signing long-term-validation path, the `VellumPdf.Conformance` validator, and a growing general reader.

> **Preview.** The public surface is still settling; text and image extraction is the next reader
> milestone, v2.4 (#98). See the roadmap below, and the [Reader guide](https://github.com/Tim81/VellumPDF/blob/main/docs/reader-guide.md) for a capability table and worked examples.

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

Reader guide, with a capability table and worked examples: <https://github.com/Tim81/VellumPDF/blob/main/docs/reader-guide.md>

Architecture and reader scope: <https://github.com/Tim81/VellumPDF/blob/main/docs/architecture.md>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| **VellumPdf.Reader** (this package) | Preview | Opens existing PDFs, encrypted ones given their password; exposes catalog and signatures; writes a decrypted copy with configurable resource limits. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A and PDF/UA preflight validation. |
| [VellumPdf.Cli](https://www.nuget.org/packages/VellumPdf.Cli) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| [VellumPdf.Barcodes](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | QR, Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC, and ITF-14 as vectors. |

## Roadmap

Planned direction, tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones).
These are scopes, not commitments — the milestones carry no due dates. 2.2.0 is the latest
published release; 2.3 is merged to main and pending release.

Scope past 2.5 runs as **two parallel tracks**, because auditing the layout engine turned up more
work than the PDF 2.0 conformance gap did. Both ship from the same version numbers.

**Kernel and conformance**

| Milestone | Scope |
| --- | --- |
| **2.3 — Reader robustness** *(merged to main, pending release)* | Cross-reference reconstruction for damaged files (#184), a decrypted-copy writer (#186), the `/XRefStm` precedence decision (#206), reader fuzzing (#99), and the CI and oracle debt that makes all of it verifiable. |
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
same as conformance, and the table says which is which. The [Reader guide](../../docs/reader-guide.md)
and [Layout guide](../../docs/layout-guide.md) each carry a capability table doing the same job for
their own package, checked against the code and test suite rather than against this roadmap.

`VellumPdf.Reader` is marked Preview in the table above; expect its public surface to settle
as these milestones land. `VellumPdf.Conformance` graduated to Stable in 2.0.

## License

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
