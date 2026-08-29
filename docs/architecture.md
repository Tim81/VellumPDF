# VellumPdf — Architecture

> Living design document. Captures the decisions behind the codebase so they are
> not lost. Update it when the architecture changes.

## Goal

A full-featured PDF **generation** library for .NET 10, comparable in capability
to a mature commercial PDF SDK, implemented **clean-room** from the open
**ISO 32000** standard.

Reading existing PDFs is being added incrementally. v1.6 introduced `VellumPdf.Reader`
for the signing LTV path (#49); it now also backs the conformance validator and
handles cross-reference streams, object streams, hybrid-reference files, and encrypted
documents (the Standard security handler, given the password). The full general
reader is roadmapped as v2.1 (structural parser, Epic #100) and v2.2 (text and
image extraction). Editing existing PDFs lands at v3.0 as a
unified read-modify-write model that supersedes the write-once document API
(Epic #101) — a breaking change, hence the major bump.

## Clean-room policy (non-negotiable)

- The implementation is written from open published specifications (ISO
  32000, OpenType/TrueType, WOFF, XMP, PKCS, etc.) and, for barcode
  symbologies whose governing standard is patented, the original patent (for
  example US 5,591,956 for Aztec Code).
- **No** source code from any third-party PDF or barcode library is copied or
  referenced. A reference decoder, zxing-cpp, is used only as an
  interoperability cross-check in the test suite, never as a source of
  implementation; this includes verifying the exact Aztec placement
  coordinates, since the relevant ISO/IEC 24778 figures are not freely
  available.
- The names of disallowed reference libraries must not appear anywhere in the
  repository. This is enforced in CI by `eng/clean-room-check.ps1`.

**Provenance.** The specifications themselves are held locally and read directly:
ISO 32000-2:2020 with Errata Collection 3, the ISO/TS 32001–32005 extension series,
ISO 14289-2 (PDF/UA-2), WTPDF 1.0 and the PDF 2.0 Application Notes. So clause citations
in this codebase point at text that was actually consulted rather than at a clause number
carried over from a secondary source, and a reviewer can check any of them. The copies are
licensed to one person and are excluded from the repository via `.git/info/exclude`; see
`CLAUDE.md` for how to query them.

Holding a standard is not implementing one, so this licenses no conformance claim. See
[PDF 2.0 conformance](pdf20-conformance.md) for what the library actually does, and note it
still emits three keys ISO 32000-2 deprecates (#325).

## Architecture style

A **layered, modular class library** — "library-flavoured Clean Architecture":
strict **inward-only**, **acyclic** dependencies; the kernel depends on nothing
but the .NET base class library.

```
(innermost — BCL only)
  VellumPdf.Kernel        object model · writer (+ incremental-update seam) · filters ·
                          document structure · low-level Canvas · fonts (parse/subset/embed) ·
                          images · metadata (Info + XMP) · marked-content / annotation /
                          AcroForm / conformance PRIMITIVES (the design-in seams)
        ▲
  VellumPdf.Layout        element tree (Paragraph/List/Table/Image) · IRenderer engine ·
                          two-phase measure/draw · automatic pagination · tagging integration
        ▲
(optional feature packages — depend inward only)
  VellumPdf.Reader        lexer · object parser · xref tables, xref/object streams,
                          hybrid-reference files · catalog and signature navigation
  VellumPdf.Signing       incremental update + PKCS#7 / PAdES (+ LTV) · reads via Reader
  VellumPdf.Conformance   PDF/A-2 (b/u/a) · PDF/UA-1 · preflight validator · reads via Reader
  VellumPdf.Barcodes      QR (+ Micro QR, GS1, Structured Append) · PDF417 (+ Compact, Macro) ·
                          Data Matrix · Aztec · Code 39 · Code 128/GS1-128 · EAN/UPC · ITF-14
  VellumPdf.Fonts.Standard14  embeddable metric-compatible standard-14 substitutes (Liberation)

(tool)
  VellumPdf.Cli           `vellum-preflight`, native-AOT · text / JSON / SARIF reports
```

`VellumPdf.Forms` and `VellumPdf.Fonts.Shaping` are not packages. AcroForm field support
lives in the Kernel, and complex-script shaping is still an open design question — see the
dependency note below.

## Dependency philosophy

- **Zero runtime dependencies** in the core. TrueType/OpenType parsing and
  subsetting, PNG decoding, and XMP are implemented in-house.
- The BCL supplies the hard primitives: `System.IO.Compression.ZLibStream`
  (FlateDecode — note: **not** `DeflateStream`, which omits the zlib header),
  and `System.Security.Cryptography(.Pkcs)` (AES-256, SHA, RSA/ECDSA, PKCS#7).
- JPEG needs no decoder: bytes are passed through as `DCTDecode`.
- Complex-script shaping (Arabic/Indic/bidi) is the one subsystem worth a
  permissive optional dependency (HarfBuzzSharp, MIT), gated behind an interface
  so the core never hard-depends on it.

## The four design-in seams

These were chosen up front because they are cheap to reserve now and very costly
to retrofit:

1. **Append-only / incremental writer** (for PAdES signing). The serializer
   models a file as one or more revisions, each with its own cross-reference
   section linked by `/Prev`, plus a signature `/Contents` placeholder and exact
   `/ByteRange` backfill.
2. **Marked-content + structure-tree channel** (for PDF/UA and PDF/A-2a). The
   low-level canvas exposes marked-content operators; renderers register
   structure elements (P, H1–H6, Table/TR/TD, Figure+Alt, L/LI, Link, Artifact)
   in reading order as they draw. Tagging off = no-ops.
3. **Annotation + widget/AcroForm plumbing** (for interactive forms). Pages own
   an `/Annots` collection; the catalog can hold `/AcroForm`. Hyperlinks reuse
   the same substrate immediately.
4. **Conformance profile** (for PDF/A). A document-level profile
   (`None`/`PdfA2b`/`PdfA2u`/`PdfA2a`/`PdfUA1`) gates disallowed features, forces
   font embedding, requires XMP + ICC OutputIntent, and drives preflight.

## Key technical notes

- **Coordinate system.** PDF user space is origin-bottom-left, Y-up, 1 unit =
  1/72 inch. The layout engine computes top-down and flips to PDF space at a
  **single** boundary in the draw context.
- **FlateDecode** is the only filter required for v1.
- **Fonts** are the largest subsystem: parse sfnt tables, subset `glyf`/`loca`
  (keep-GID + null-unused + composite closure + checksum fix-up + `ABCDEF+`
  tag), emit Type0 / CIDFontType2 / Identity-H + `ToUnicode`. Whole-CFF
  embedding is the fallback for OpenType-CFF fonts until CFF subsetting lands.
- **Tables** are the largest layout element and are phased: fixed-width →
  auto-width → spanning → cross-page split + repeating headers → collapsed
  borders.
- **Linearization** (opt-in `PdfDocument.Linearize`) re-orders objects for
  fast-web-view. It measures each object's length, builds the primary hint
  stream from those lengths in a coordinate system where the hint stream has
  zero length (so hint offsets are independent of it), then writes the file
  once with fixed-width placeholders for the absolute offsets in the
  linearization dictionary and first-page cross-reference table and patches
  them in a second pass. Verified against `qpdf --show-linearization`; the
  encoders are pinned to qpdf's exact bytes by golden tests.

## Defaults & conventions

- Default page size **A4**; default units **metric (millimetres)**.
- Target framework **net10.0** only (and later).
- Modern C# 14: `readonly record struct` for value types (Rect/Matrix/Color),
  `ReadOnlySpan<byte>` + UTF-8 (`u8`) literals for fixed tokens,
  `SearchValues<byte>` for delimiter scanning, `FrozenDictionary` for metric
  tables, primary constructors, collection expressions, the `field` keyword.
- Invariant formatting everywhere (PDF reals always use '.').

## Testing & conformance validation

- Golden-file/snapshot tests (Verify) on serialized bytes; property-based tests
  (CsCheck) on escaping and cross-reference offsets.
- **External validators as oracles in CI** (invoked as tools, never linked or
  shipped, so they do not affect the library's license-clean runtime):
  veraPDF 1.30.2 (PDF/A-2b/2u/2a + PDF/UA-1, via the official Docker image),
  `qpdf --check` and `--show-linearization` (structural and linearization),
  `pdftotext` (text round-trip → proves `ToUnicode`), `pdfsig` (PAdES signature
  validity), and zxing-cpp (decode round-trip for every barcode symbology, via a
  rasterized `pdftoppm` page).
- **A missing tool fails the build rather than skipping the test**: almost every oracle
  test routes through a shared `OracleGate`, which calls `Assert.Fail` instead of
  `Assert.Skip` when `CI`, `GITHUB_ACTIONS`, or `REQUIRE_ORACLES` is set — the one exception
  is `ConformanceCatalogTests.Catalog_MatchesVeraPdfProfile`, which calls `Assert.Skip`
  directly and does not consult the gate. `REQUIRE_VERAPDF` and `REQUIRE_BARCODE_ORACLE` do
  the same as the three global switches, scoped to the veraPDF and barcode-decode oracles
  respectively. Locally the same tests skip, so a contributor without Docker or poppler
  installed still gets a green run. A tool that resolves to something other than what it
  claims — a bare `pdftotext` answering as Xpdf instead of poppler, say — is gated the same
  way, through `ExternalTool`'s own identity check. A single slow identity probe skips rather
  than fails, since a lone timeout says nothing about the tool's identity; a tool whose probe
  times out three times in a row is treated as confirmed-absent instead, so a persistently
  stuck oracle still fails the build under CI rather than skipping forever.

## Milestones

The original M1–M4 plan is done through M3, and most of M4: kernel and font engine, layout
with pagination and tagging, PDF/A-2b/2u/2a and PDF/UA-1 with an in-process preflight,
AES-256 encryption, barcodes, xref and object streams, linearization, PAdES signing with
LTV, and AcroForm fields.

Everything the paragraph above used to list as open is now tracked. Complex-script shaping
and SVG→PDF are epics in the Layout track; HTML→PDF is a Backlog epic with its dependencies
named; the reader work leads into the v3.x editing line.

Scope past 2.5 runs as **two parallel tracks**: Kernel and conformance (v2.6 to v2.13), and
Layout (A to I). Auditing the layout engine turned up more work than the PDF 2.0 conformance
gap did, and splitting them keeps either from blocking the other. Both ship from the same
version numbers, since the packages version in lockstep.

v3.0 adds a ninth package, `VellumPdf.Editing`, depending on Kernel, Reader and Layout. It
holds the round-trip document model and supplies high-level editing as extension methods over
Layout types, the pattern `VellumPdf.Signing` already uses, so Layout itself gains no
dependency and generate-only consumers pay nothing for it.

Live scope lives in one place — the roadmap table in [README.md](../README.md#roadmap),
tracked as [GitHub milestones](https://github.com/Tim81/VellumPDF/milestones). A second copy
here would only drift, which is how the list above came to describe a target the library had
long since passed.
