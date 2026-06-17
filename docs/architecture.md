# VellumPdf — Architecture

> Living design document. Captures the decisions behind the codebase so they are
> not lost. Update it when the architecture changes.

## Goal

A full-featured PDF **generation** library for .NET 10, comparable in capability
to a mature commercial PDF SDK, implemented **clean-room** from the open
**ISO 32000** standard.

Reading existing PDFs is being added incrementally: v1.6 introduces an MVP reader
(`VellumPdf.Reader`) for the signing LTV path (classic xref, unencrypted; see #49).
The full general reader is roadmapped as v2.1 (structural parser, Epic #100) and
v2.2 (text/image extraction). Editing existing PDFs lands at v3.0 as a unified
read-modify-write model that supersedes the write-once document API (Epic #101) —
a breaking change, hence the major bump.

## Clean-room policy (non-negotiable)

- The implementation is written **solely** from open published specifications
  (ISO 32000, OpenType/TrueType, WOFF, XMP, PKCS, etc.).
- **No** source code from any third-party PDF library is copied or referenced.
- The names of disallowed reference libraries must not appear anywhere in the
  repository. This is enforced in CI by `eng/clean-room-check.ps1`.

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
  VellumPdf.Signing       incremental update + PKCS#7 / PAdES
  VellumPdf.Forms         AcroForm fields + appearance-stream generation
  VellumPdf.Conformance   PDF/A-2 (b/u/a) · PDF/UA-1 · preflight validator
  VellumPdf.Barcodes      QR · PDF417 · Code128 · EAN
  VellumPdf.Fonts.Shaping optional HarfBuzz adapter (off by default; honours zero-dep core)
```

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
  `qpdf --check` (structural), veraPDF (PDF/A + UA), PDFBox Preflight,
  `pdftotext`/pdfcpu (text round-trip → proves `ToUnicode`), and a render-diff
  via pdfium/Ghostscript.
- Cross-reader smoke tests against pdf.js.

## Milestones

- **M1 — Core + high-level layout** (current target): kernel, font engine,
  images, layout engine with pagination and the tagging channel, tables.
- **M2** — PDF/A-2b/2u + preflight, AES-256 encryption, barcodes, xref/object
  stream optimization.
- **M3** — Tagged PDF / PDF-UA-1 + PDF/A-2a, basic PAdES signing.
- **M4** — Interactive AcroForms, PAdES-LTV, optional shaping, SVG→PDF,
  HTML→PDF (separate Chromium-shelling package).
