# VellumPdf.Reader

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Reader.svg)](https://www.nuget.org/packages/VellumPdf.Reader)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

The PDF reader of **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10 and .NET 11 implemented clean-room from ISO 32000. It opens an existing PDF on a bounds-checked lexer and object parser and exposes its structure. BCL-only, with zero runtime dependencies.

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
  `ReconstructionBudgetMultiplier`, `MaxDiagnostics`, `MaxFormXObjectDepth`) for a caller hardening
  against a decompression bomb, a file engineered to burn CPU, or a deeply nested Form XObject
  graph: each can only lower the shipped default, never raise it.
- Exposes the document catalog and digital signatures, and extracts images (`ExtractImages`, since
  2.3.2) and text from pages that use simple fonts (`ExtractText`, since 2.3.2).
- Writes a decrypted copy of an encrypted document (`PdfDocumentReader.SaveDecrypted`), refusing a
  signed document unless the caller opts into invalidating its signatures.
- The foundation for the signing long-term-validation path, the `VellumPdf.Conformance` validator, and a growing general reader.

> **Preview.** The public surface is still settling; image extraction and text extraction from
> simple fonts shipped in 2.3.2; text extraction from composite fonts (#394) and graduation from
> Preview (#187) are milestone v2.5. See the roadmap below, and the [Reader guide](https://github.com/Tim81/VellumPDF/blob/main/docs/reader-guide.md) for a capability table and worked examples.

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

## Capabilities

Built from the public API and the test suite, not from memory — a row is only marked Supported
where a test proves it. See the [Reader guide](https://github.com/Tim81/VellumPDF/blob/main/docs/reader-guide.md)
for the narrative walkthrough this table summarizes.

<!-- capability-table:reader:start -->
| Capability | Status | Target milestone / ISO reference |
| --- | --- | --- |
| Classic cross-reference tables | ✅ Supported | ISO 32000-2 §7.5.4 |
| Cross-reference streams, object streams | ✅ Supported | ISO 32000-2 §7.5.7, §7.5.8 |
| Hybrid-reference files (`/XRefStm`) | ✅ Supported | ISO 32000-2 §7.5.8.4 (#206) |
| Cross-reference reconstruction for damaged files | ✅ Supported (opt-in) | ISO 32000-2 Annex C.4, informative (#184) |
| Configurable, tighten-only resource limits | ✅ Supported | ISO 32000-2 Annex C.1/C.3, informative (#376) |
| Decryption: Standard handler, `/V` 1/2/4/5, `/R` 2–6, RC4-40–128, AES-128/256 | ✅ Supported | ISO 32000-2 §7.6 |
| Reading the document catalog | ✅ Supported | ISO 32000-2 §7.7.2 |
| Page tree walk and page access (`PageCount`, `Pages`, `GetPage`) with inherited `/MediaBox`, `/CropBox`, `/Rotate` | ✅ Supported | ISO 32000-2 §7.7.3 (#98) |
| Reading digital signature metadata (`/ByteRange`, `/Contents`, `/M`, `/SubFilter`) | ✅ Supported (read only, not verified) | ISO 32000-2 §12.8 |
| Writing a decrypted copy (`SaveDecrypted`/`SaveDecryptedAsync`) | ✅ Supported | #186 |
| Lexer/parser hardened against malformed input (property-based fuzzing, round-trip oracle) | ✅ Supported | #99 |
| Diagnostics (`PdfDocumentReader.Diagnostics`) for conditions the reader recovers from instead of aborting on | ✅ Supported | ISO 32000-2 Annex I.2 (#385) |
| Text extraction (`ExtractText`) | ⚠️ Partial — simple fonts (Type1, MMType1, TrueType) only, positioned per ISO 32000-2 §9.4.4, page content only (not annotation appearances); line grouping is rotation-aware; no space is synthesised for a positioning gap (`[(Hello) -500 (World)] TJ` extracts as `HelloWorld`); `/ToUnicode`, predefined CMaps, Type0/Type3 fonts, `/ActualText`, `/ReversedChars`, and word/paragraph-aware line grouping not yet implemented | ISO 32000-2 §9.4.4 (#98) |
| Image extraction (`ExtractImages`; DCT, JPX, JBIG2 and CCITT payloads returned undecoded) | ✅ Supported | ISO 32000-2 §8.9, §7.4.7, §7.4.9 (#98) |
| Graduating `VellumPdf.Reader` from Preview to Stable | ⏳ Planned | v2.5 (#187) |
| Reading a document that uses an ISO/TS 32001–32004 extension (AES-GCM, PDF-MAC, SHA-3, EdDSA) | ⚠️ Partial — AES-GCM is rejected (`UnsupportedPdfFeatureException`); PDF-MAC is ignored and SHA-3/EdDSA signatures read as opaque, none verified | v2.7 (#236, #237, #238, #239) |
| Signature verification (integrity, coverage, certificate chains, revocation, achieved PAdES level) | ⏳ Planned | v2.14 |
| Round-trip editing of an existing document | ⏳ Planned | v3.0, `VellumPdf.Editing` (Epic #101) |
<!-- capability-table:reader:end -->

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

<!-- roadmap:begin (generated by eng/generate-roadmap.py; do not edit) -->

Planned direction, tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones).
These are scopes, not commitments — the milestones carry no due dates. 2.3.4 is the latest
published release.

Scope past 2.5 runs as **two parallel tracks**, because auditing the layout engine turned up more
work than the PDF 2.0 conformance gap did. Both ship from the same version numbers.

**Kernel and conformance**

| Milestone | Scope |
| --- | --- |
| **2.3 — Reader robustness** *(released)* | Cross-reference reconstruction for damaged files (#184), a decrypted-copy writer (#186), the /XRefStm precedence decision (#206), reader fuzzing (#99), and the CI and oracle debt that makes all of it verifiable. |
| **2.4 — Hardening and navigable documents** | Hardening across the packages (non-finite and null arguments, image loaders, table and list layout, resource bounds) and navigable documents that stay within PDF/A-2u: internal links, named destinations, page labels, a table of contents and an index with page numbers, leaders, page breaks and keep-with-next, preformatted text, font fallback, and a diagnostics channel with an opt-in strict mode. |
| **2.5 — Accessible documents, extraction and Reader graduation** | PDF/A-2a and PDF/UA-1 tagging fixes (links, artifacts, one structure element per logical element, table headers, language changes, list labels), text and image extraction (#98, #394), graduating VellumPdf.Reader from Preview to Stable (#187), image encoders (#491, #495), settling whether a CCITT Group 4 encoder is worth building (#494), and full JBIG2 support: decoding (#434), encoding (#436), loading files with the T.88 Annex D header (#570) and the raster fixes (#619, #620, #621), each checked against every T.88 rule that applies. |
| **2.6 — PDF/A-4 and PDF/A-1 profiles** | PDF/A-4 (ISO 19005-4, PDF 2.0; #222) so conformance output stops downgrading to %PDF-1.7, PDF/A-1 (ISO 19005-1; #218) to unblock recursive validation of embedded PDF/A-1 files, and dropping the keys ISO 32000-2 deprecates (#325). |
| **2.7 — ISO/TS extensions to PDF 2.0** | The four Technical Specifications that amend PDF 2.0, on the ISO 32000-2 §7.12 developer-extensions foundation: SHA-3 (TS 32001, #238), EdDSA and the extended curve set (TS 32002, #239), AES-GCM (TS 32003, #236) and PDF MAC integrity (TS 32004, #237). Security-relevant: the reader rejects every AES-GCM file today. |
| **2.8 — Embedded files and associated files** | File specifications and embedded file streams, on the /Names name tree that v2.4 adds (#241); the missing annotation subtypes; and §14.13 associated files. Unblocks PDF/A-4F. |
| **2.9 — Colour and paint** | PDF functions, the full shading model, tiling and shading patterns, and the colour spaces beyond Device* and ICCBased. |
| **2.10 — Rendering and resources** | Transparency and blend modes, the complete ExtGState, public form XObjects, inline images, optional content, the missing content-stream operators, and the page boundary boxes. |
| **2.11 — Font completeness** | Type 1, bare CFF, simple TrueType and Type 3 fonts (the programs and encodings ISO 32000-2 §9.6–9.9 defines that the writer cannot produce); predefined CMaps and vertical writing; kerning; a real /StemV; and enforcing the fsType embedding permission the library currently parses and ignores. |
| **2.12 — Tagged PDF 2.0 writer** | Structure namespaces, the PdfStructElem surface PDF/UA-2 needs, structure destinations, and the ISO/TS 32005 containment matrix as generated data. |
| **2.13 — PDF/UA-2 and WTPDF** | The ua2, wt1a and wt1r rule sets, consuming the containment matrix from v2.12. |
| **2.14 — Signature verification** | Verifying signatures, not just producing them: integrity, coverage, chain building, revocation and PAdES level detection. Independent of the editing line. |

**Layout**

| Milestone | Scope |
| --- | --- |
| **Layout A — Foundations** | The structural blockers in the layout engine: no containment model (sections with their own bands, numbering and page size) and table cells that hold strings, not elements. Plus the API shape everything later inherits, since retrofitting it gets more expensive with each issue: a unit type, named styles and renderer extensibility. Everything else in the Layout track depends on these. |
| **Layout B — Accessibility structure** | The PDF/UA-2 element set, and the layout behaviour that makes it usable: lists of figures and tables and the full index range (#599), captions as a real containment relationship, footnotes and endnotes, automatic numbering counters, and internal links as PDF 2.0 structure destinations (#600). The table of contents, the index, leaders and internal links ship in v2.4; table header semantics in v2.5. |
| **Layout C — Forms and semantics** | Form elements that place their own widgets and carry the labels PDF/UA-2 requires, and build-time accessibility diagnostics with a strict mode (#304), on the general diagnostics channel that v2.4 adds (#607). |
| **Layout D — Typography** | Text decoration and scripts, optional paragraph-optimal line breaking, hyphenation and floats, per-cell borders and vertical alignment, plus inline content: images in a line of text, and convenience image loading. |
| **Layout E — Composition** | Composite elements and document templates, fit modes, anchored elements and rotation, page backgrounds and borders, running bands that hold more than one string, and prepress bleed and printer marks. |
| **Layout F — Advanced layout** | Multi-column flow, nested and richer tables, a chart family, streaming generation for very large documents, and the SVG import epic, which lands on the v2.9 and v2.10 graphics work. |
| **Layout G — International text** | Unicode bidi and RTL, vertical writing and CJK line breaking, culture-aware formatting, and the complex-script shaping epic. |

**Editing existing documents.** The only breaking milestone is 3.0.

| Milestone | Scope |
| --- | --- |
| **3.0 — Round-trip document model** | Two things, not one. The new round-trip document type alongside the write-once PdfDocument, the memory-mapped backing store, save modes, and the VellumPdf.Editing package. And the library's breaking changes generally, not only the editing ones: the contract tightening the 2.3.x patches can only report rather than refuse, collected in #484. The only breaking milestone in the line, so everything after it is additive. |
| **3.1 — Sign and fill existing documents** | Signing a PDF the library did not write, and filling its form fields, both as incremental updates. Plus /SV seed values, /Lock, /FieldMDP and /DocMDP enforcement. |
| **3.2 — Append-only content edits** | Annotations, metadata and XMP, watermark overlay, attachments, and appending flowed Layout content to an existing document. |
| **3.3 — Full-rewrite operations** | Grouped by blast radius: revision-history discard, changing encryption, preserving linearization, and removing a signature. None can be an incremental update. |
| **3.4 — Structural editing** | Page operations, merge and split, optimise and sanitise, and page geometry. |
| **3.5 — Redaction** | Marking regions and applying them. Deliberately isolated so a half-implementation never ships — marking alone is not redaction. |
| **3.6 — Content editing** | Reflowing text edits over a parsed content stream, structure-tree round-trip for tagged documents, and the editing CLI. |

What this library implements of ISO 32000-2 today is inventoried, reference by reference, in
[PDF 2.0 conformance](https://github.com/Tim81/VellumPDF/blob/main/docs/pdf20-conformance.md). It emits a `%PDF-2.0` header, which is not the
same as conformance, and the table says which is which. The [Reader guide](https://github.com/Tim81/VellumPDF/blob/main/docs/reader-guide.md)
and [Layout guide](https://github.com/Tim81/VellumPDF/blob/main/docs/layout-guide.md) each carry a capability table doing the same job for
their own package, checked against the code and test suite rather than against this roadmap.

`VellumPdf.Reader` is marked Preview in the table above; expect its public surface to settle
as these milestones land. `VellumPdf.Conformance` graduated to Stable in 2.0.
<!-- roadmap:end -->

## License

Apache-2.0 for the code. The bundled Adobe Glyph List and ZapfDingbats glyph list are
BSD-3-Clause; their copyright notice and disclaimer are in the `NOTICE` file shipped in this
package. Source and issues: <https://github.com/Tim81/VellumPDF>
