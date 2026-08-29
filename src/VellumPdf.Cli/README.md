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
    "passed": 132,                          // catalogue checks satisfied
    "failedChecks": 0,                      // catalogue checks a rule named by test id
    "inconclusive": 9,                      // clause failed, specific check unidentified
    "partial": 2, "deferred": 0,            // subsets of notEvaluated
    "total": 144                            // passed + failedChecks + inconclusive + notEvaluated
  },
  "failed": [
    { "ruleId": "ISO19005-2:6.3.4-font-embedding",
      "clause": "ISO 19005-2:2011, 6.3.4",
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
| [VellumPdf.Reader](https://www.nuget.org/packages/VellumPdf.Reader) | Preview | Opens existing PDFs, encrypted ones given their password; exposes catalog and signatures. |
| [VellumPdf.Conformance](https://www.nuget.org/packages/VellumPdf.Conformance) | Stable | In-process PDF/A and PDF/UA preflight validation. |
| **VellumPdf.Cli** (this package) | Stable | `vellum-preflight` command-line PDF/A and PDF/UA validator. |
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
