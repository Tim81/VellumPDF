# Changelog

All notable changes to VellumPdf will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.2.0] - 2026-06-11

### Added

- **OpenType-CFF font subsetting.** CFF (`.otf`) fonts are now subsetted rather than embedded whole. Used charstrings are kept verbatim; unused glyphs and unreachable global and local subroutines are dropped, which cuts a typical small-glyph subset by roughly 70%. CID-keyed or unparseable CFF falls back to whole-font embedding.
- **DeviceCMYK and ICC-based colour.** `PdfDocument.SetPdfAOutputIntent` and `UseCmykOutputIntent` set the PDF/A output intent (the default stays sRGB). `RegisterIccBasedColorSpace` registers an ICCBased colour space, painted with the new `PdfCanvas.SetFillColorSpace`/`SetStrokeColorSpace` and `SetFillColor`/`SetStrokeColor` operators. `IccProfiles.Srgb` and `IccProfiles.GenericCmyk` supply built-in profiles for callers without their own. DeviceCMYK content validates as PDF/A once a CMYK output intent is set; both paths are checked on CI with veraPDF.
- **`ColorCmyk`** colour type in the layout API, with `FromRgb` and `ToRgbApproximate` conversions.
- **cmap subtable formats 0 and 6.** Fonts whose character map uses these formats, in addition to format 4, now embed and extract text correctly.

### Security

- **Font-parser hardening.** The CFF subsetter and cmap parser bound operand-stack depth and subroutine nesting, use overflow-safe offset checks, and reject negative or zero INDEX offsets and out-of-range cmap ranges. A malformed font falls back to whole-font embedding or fails with a clear error instead of throwing an unhandled exception or exhausting the stack. Valid fonts are unaffected.

## [1.1.0] - 2026-06-10

### Added

- **PDF/A-2a (level A) conformance**, validated on every CI run with strict veraPDF.
- **PDF/UA-1 (ISO 14289-1) conformance** via `PdfConformance.PdfUA1`, validated on CI with strict veraPDF — emits the `pdfuaid` XMP schema, `/ViewerPreferences << /DisplayDocTitle true >>`, and marks decorative content (table borders/fills, separators, running header/footer bands) as `/Artifact`.
- **Document and per-element language.** A `Language` property on the layout `Document`, `Paragraph`, `Heading`, `ListItem`, and table `Cell` (and on kernel `PdfDocument` / `PdfStructElem`) emits catalog `/Lang` and XMP `dc:language`.
- **`PdfCanvas.BeginArtifactMarkedContent`** — marks decorative content as a PDF `/Artifact` (no MCID).
- **Accessible tables.** `PdfStructElem.TableHeaderScope` emits `/A << /O /Table /Scope … >>` on header cells so assistive tech can resolve column headers.

### Changed

- The tagged-PDF structure tree now writes an MCID-validated `/ParentTree` and no longer emits a self-referential `/RoleMap` (a circular role mapping, which PDF/UA-1 forbids).

## [1.0.0] - 2026-06-09

### Added

- **Public-API surface lock.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` is
  wired to every shippable project. Any accidental addition, removal, or rename
  of a public symbol is a build error unless the corresponding
  `PublicAPI.Unshipped.txt` baseline is updated, guarding the API contract ahead
  of 1.0.
- **veraPDF PDF/A-2b/2u CI gate.** The official `verapdf/cli` Docker image is
  pulled on every CI run and exercises the generated archival documents (embedded
  font, table, image, and tagged variants) under strict PDF/A-2b and PDF/A-2u
  profiles. A non-compliant report fails the build with the full rule list
  attached.

### Changed

- **Deterministic output.** Document identifiers (`/ID`) and producer timestamps
  are now pinnable at the call site, so bytes produced from identical inputs are
  bit-for-bit identical across builds. This is required for reliable golden-file
  snapshot tests and for reproducible NuGet packages.

### Security

- **Font-parser hardening.** The TrueType/OpenType parser now fails cleanly on
  malformed or hostile input — throwing `InvalidDataException` on corrupt or
  truncated data and `NotSupportedException` on unsupported variants — rather than
  crashing with an unexpected exception, hanging, or exhausting memory.
- **Image-parser hardening.** The PNG, JPEG, BMP, GIF, and TIFF parsers apply the
  same defensive posture: bounded reads, early rejection of structurally invalid
  headers, and no unbounded allocations driven by attacker-controlled length
  fields.

[1.1.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.1.0
[1.0.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.0.0
