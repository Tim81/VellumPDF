# VellumPdf.Cli

[![NuGet](https://img.shields.io/nuget/v/VellumPdf.Cli.svg)](https://www.nuget.org/packages/VellumPdf.Cli)
[![CI](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml/badge.svg)](https://github.com/Tim81/VellumPDF/actions/workflows/ci.yml)

`vellum-preflight` — the command-line PDF/A and PDF/UA validator from **[VellumPdf](https://github.com/Tim81/VellumPDF)**, a dependency-free PDF library for .NET 10 and .NET 11. It checks a PDF against PDF/A-2b/2u/2a and PDF/UA-1 with the in-process `VellumPdf.Conformance` engine, with no JVM and no Docker.

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

## JSON report shape

**The shape changed in 2.0.** `summary.total` used to be `failed + passed + notEvaluated`, which
added a count of rule assertions to a count of catalogue checks. It is now the size of the
profile's check catalogue, and `failedChecks` and `inconclusive` are new.

```jsonc
{
  "tool": "vellum-preflight",
  "toolVersion": "2.0.0",
  "file": "invoice.pdf",
  "profile": "PDF/A-2b",
  "profileSource": "explicit",   // "explicit" or "auto" (from the PDF's own claim)
  "conformant": false,
  "summary": {
    "error": 4, "warning": 0, "info": 0,   // rule assertions, by severity
    "passed": 130,                          // catalogue checks satisfied
    "failedChecks": 0,                      // catalogue checks a rule named by test id
    "inconclusive": 11,                     // clause failed, specific check unidentified
    "partial": 2, "deferred": 0,            // subsets of notEvaluated
    "total": 144                            // passed + failedChecks + inconclusive + notEvaluated
  },
  "failed": [
    { "ruleId": "ISO19005-2:6.2.11.4.1-font-embedding",
      "clause": "ISO 19005-2:2011, 6.2.11.4.1",
      "severity": "ERROR",
      "message": "The font /Helvetica is not embedded; ..." }
    // "objectRef" is present only when the rule identified an object
  ],
  "passed":       [ { "testId": "6.1.3-1", "clause": "6.1.3" } ],
  "failedChecks": [],
  "inconclusive": [ { "testId": "6.1.2-1", "clause": "6.1.2" } ],
  "notEvaluated": [ { "testId": "6.1.13-10", "clause": "6.1.13",
                      "status": "Partial", "note": "..." } ]
}
```

`failed` lists rule assertions; the four check arrays partition the catalogue, so every catalogued
check appears in exactly one of them. A check lands in `inconclusive` when a rule failed in its ISO
clause without a test id identifying which check it corresponds to — that check can then be neither
claimed as passing nor blamed for the failure. Before 2.0 those checks were dropped from the report
entirely.

Several input files, or several profiles for one file, produce `{ "results": [ ... ] }` with one
object of the above shape per result.

## Documentation

Command-line reference: <https://github.com/Tim81/VellumPDF#command-line-preflight>

## The VellumPdf family

| Package | Status | Summary |
| --- | --- | --- |
| [VellumPdf.Kernel](https://www.nuget.org/packages/VellumPdf.Kernel) | Stable | Low-level PDF object model, canvas, fonts, images, encryption. |
| [VellumPdf.Fonts.Standard14](https://www.nuget.org/packages/VellumPdf.Fonts.Standard14) | Stable | Embeddable standard-14 font substitutes for PDF/A text. |
| [VellumPdf.Layout](https://www.nuget.org/packages/VellumPdf.Layout) | Stable | High-level document builder: paragraphs, tables, images, pagination. |
| [VellumPdf.Signing](https://www.nuget.org/packages/VellumPdf.Signing) | Stable | PAdES / PKCS#7 digital signatures with timestamps and LTV. |
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs, encrypted ones given their password; exposes catalog and signatures; writes a decrypted copy with configurable resource limits. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A and PDF/UA preflight validation. |
| **VellumPdf.Cli** (this package) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
| [VellumPdf.Barcodes](https://www.nuget.org/packages/VellumPdf.Barcodes) | Stable | QR, Data Matrix, Aztec, PDF417, Code 128/GS1-128, Code 39, EAN/UPC, and ITF-14 as vectors. |

## Roadmap

<!-- roadmap:begin (generated by eng/generate-roadmap.py; do not edit) -->

Planned direction, tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones).
These are scopes, not commitments — the milestones carry no due dates. 2.3.3 is the latest
published release.

Scope past 2.5 runs as **two parallel tracks**, because auditing the layout engine turned up more
work than the PDF 2.0 conformance gap did. Both ship from the same version numbers.

**Kernel and conformance**

| Milestone | Scope |
| --- | --- |
| **2.3 — Reader robustness** *(released)* | Cross-reference reconstruction for damaged files (#184), a decrypted-copy writer (#186), the /XRefStm precedence decision (#206), reader fuzzing (#99), and the CI and oracle debt that makes all of it verifiable. |
| **2.4 — Hardening and navigable documents** | Hardening (the issues of the v2.3.4 to v2.3.9 patch milestones) and navigable documents that stay within PDF/A-2u: internal links, named destinations, page labels, a table of contents and an index with page numbers, leaders, page breaks and keep-with-next, preformatted text, font fallback, and a diagnostics channel with an opt-in strict mode. |
| **2.5 — Accessible documents, extraction and Reader graduation** | PDF/A-2a and PDF/UA-1 tagging fixes (links, artifacts, one structure element per logical element, table headers, language changes, list labels), text and image extraction (#98, #394), graduating VellumPdf.Reader from Preview to Stable (#187), and image encoders (#491, #494, #495). |
| **2.6 — PDF/A-4 and PDF/A-1 profiles** | PDF/A-4 (ISO 19005-4, PDF 2.0; #222) so conformance output stops downgrading to %PDF-1.7, PDF/A-1 (ISO 19005-1; #218) to unblock recursive validation of embedded PDF/A-1 files, and dropping the keys ISO 32000-2 deprecates (#325). |
| **2.7 — ISO/TS extensions to PDF 2.0** | The four Technical Specifications that amend PDF 2.0, on the ISO 32000-2 §7.12 developer-extensions foundation: SHA-3 (TS 32001, #238), EdDSA and the extended curve set (TS 32002, #239), AES-GCM (TS 32003, #236) and PDF MAC integrity (TS 32004, #237). Security-relevant: the reader rejects every AES-GCM file today. |
| **2.8 — Embedded files and associated files** | File specifications and embedded file streams, on the /Names name tree that v2.4 adds (#241); the missing annotation subtypes; and §14.13 associated files. Unblocks PDF/A-4F. |
| **2.9 — Colour and paint** | PDF functions, the full shading model, tiling and shading patterns, and the colour spaces beyond Device* and ICCBased. |
| **2.10 — Rendering and resources** | Transparency and blend modes, the complete ExtGState, public form XObjects, inline images, optional content, the missing content-stream operators, and the page boundary boxes. |
| **2.11 — Font completeness** | Type 1, bare CFF, simple TrueType and Type 3 fonts (the programs and encodings ISO 32000-2 §9.6–9.9 defines that the writer cannot produce); predefined CMaps and vertical writing; kerning; a real /StemV; and enforcing the fsType embedding permission the library currently parses and ignores. |
| **2.12 — Tagged PDF 2.0 writer** | Structure namespaces, the PdfStructElem surface PDF/UA-2 needs, structure destinations, and the ISO/TS 32005 containment matrix as generated data. |
| **2.13 — PDF/UA-2 and WTPDF** | The ua2, wt1a and wt1r rule sets, consuming the containment matrix from v2.12. Measured at 1727 rules, of which 1648 are containment. |
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
[PDF 2.0 conformance](../../docs/pdf20-conformance.md). It emits a `%PDF-2.0` header, which is not the
same as conformance, and the table says which is which. The [Reader guide](../../docs/reader-guide.md)
and [Layout guide](../../docs/layout-guide.md) each carry a capability table doing the same job for
their own package, checked against the code and test suite rather than against this roadmap.

`VellumPdf.Reader` is marked Preview in the table above; expect its public surface to settle
as these milestones land. `VellumPdf.Conformance` graduated to Stable in 2.0.
<!-- roadmap:end -->

## License

Apache-2.0. Source and issues: <https://github.com/Tim81/VellumPDF>
