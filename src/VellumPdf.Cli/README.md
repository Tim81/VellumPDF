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
