# Changelog

All notable changes to VellumPdf will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [1.5.6] - 2026-06-16

### Fixed

- **Structure tree allocation guard.** A hand-built tagged structure tree whose
  `PdfStructElem.Mcid` is set to a very large value (or `int.MaxValue`) now raises a clear
  exception instead of overflowing or attempting a multi-gigabyte ParentTree allocation. The
  per-page ParentTree array is indexed by MCID; documents tagged through the canvas are
  unaffected (their MCIDs are dense and sequential).

## [1.5.5] - 2026-06-16

Closes the residual hardening items from the 2026-06-12 full-library review (#83, #84).

### Added

- **Signature widget page.** `PdfSignatureSettings.SignaturePage` (0-based, default 0) chooses
  which page carries the invisible signature widget; an out-of-range index is rejected.

### Fixed

- **Tagged-PDF MCID range.** The per-page structure ParentTree is sized by the highest MCID on
  the page rather than the leaf-element count, so non-contiguous MCIDs (for example when the MCID
  counter is shared with container elements) produce a valid sparse array instead of aborting the
  save. The marked-content-to-structure mapping is unchanged.
- **Form field text encoding.** AcroForm field names, values, and choice options are written as
  proper PDF text strings — Latin-1 when representable, otherwise UTF-16BE with a byte-order mark —
  instead of silently replacing non-Latin-1 characters with `?`. Field text that the Standard-14
  appearance font cannot render now raises a clear error rather than writing `?` into the appearance.
- **Word wrap.** Wrapping handles `\r\n` and lone `\r` as line breaks and splits on Unicode
  whitespace, so Windows line endings no longer leave a stray carriage-return glyph and tabs and
  runs of spaces wrap correctly.
- **Nested list markers.** A nested ordered list uses its configured numbering scheme
  (alphabetic, roman, decimal) instead of always falling back to decimal.
- **Justified text.** Word-gap counting uses one tokenization for both measurement and drawing,
  so justified spacing is consistent between embedded and Standard-14 fonts.

## [1.5.4] - 2026-06-13

### Fixed

- **JPEG 2000 in PDF/A.** A JPEG 2000 image in a PDF/A-2 document now carries the JP2 box
  structure (`ihdr`/`colr`) that veraPDF reads for clause 6.2.8.3, rather than only the bare
  codestream — which reported 0 colour channels and 0 bit depth and failed validation. The
  codestream is preserved byte-for-byte: for a JP2 source only ancillary metadata boxes are
  dropped, so the embedded image never grows and usually shrinks; a raw `.j2k` codestream is
  wrapped in a minimal JP2. For a `/JPXDecode` image, `/BitsPerComponent` is emitted only when
  its value is one PDF/A permits (1, 2, 4, 8, 16) — the codestream still defines the bit depth.

### Changed

- **JBIG2 embedding.** A JBIG2 image no longer writes an empty `/DecodeParms` dictionary when it
  has no global segments, and the end-of-page segment is dropped from the embedded stream
  (alongside the end-of-file segment) to match the PDF embedded organisation. The end-of-stripe
  segment is retained because it carries image data for striped pages.

## [1.5.3] - 2026-06-13

### Fixed

- **Signature byte-range coverage.** A signed PDF no longer writes a comment between the
  `/Contents` key and its hex-string value, so the value is a direct hex string as signature
  validators expect. veraPDF 1.30+ rejected the previous output on clause 6.4.3-1
  (`doesByteRangeCoverEntireDocument`) even though the byte range and the CMS signature were
  correct. The internal placeholder is now located by anchoring on the `/ByteRange` placeholder,
  which keeps the patch resistant to crafted `Reason`/`Location` metadata.

## [1.5.2] - 2026-06-12

### Fixed

- **Link URIs with non-BMP characters.** A hyperlink URL containing a character above the Basic
  Multilingual Plane (for example an emoji) is now percent-encoded as its full UTF-8 byte sequence
  rather than two `U+FFFD` replacement characters. URLs without such characters are unaffected.

## [1.5.1] - 2026-06-12

A hardening release from a full-library review: bug fixes, malformed-input robustness, and a
few small additions. No public API was removed.

### Added

- **PNG transparency.** The `tRNS` chunk is now applied — palette images gain an alpha `/SMask`
  and greyscale/truecolour images gain a colour-key `/Mask` — instead of the transparency being
  dropped.
- **Outline open/closed state.** `PdfOutlineEntry.IsExpanded` (default `true`) controls whether a
  bookmark renders expanded or collapsed and is reflected in the ISO 32000 signed `/Count`.

### Fixed

- **Layout pagination.** A list item taller than a page no longer loops or duplicates content — it
  resumes on the next page via the content overflow, and the item marker is drawn once. Table cells
  whose text wraps are drawn wrapped instead of overlapping the next row; automatic column widths
  use the cell's own font, including embedded fonts; row spans are no longer split across a page
  break; and the total-page count behind the `{pages}` footer token matches the rendered output.
  Paragraph wrapping honours embedded newlines.
- **Document integrity.** Writing the same `PdfDocument` twice, or writing a document with no
  pages, now throws instead of producing a duplicated or invalid file. Signing a document that also
  has form fields keeps those fields, including fields on pages other than the first.
- **Fonts.** A malformed `hmtx` or `name` table now fails with `InvalidDataException` rather than an
  unexpected exception, and a CID-keyed CFF that falls back to whole-font embedding no longer
  advertises a subset tag.

### Security

- **Malformed-input robustness.** The CCITT, GIF, and TIFF decoders and the font tables reject
  corrupt, truncated, or out-of-range input with `InvalidDataException` instead of over-reading,
  looping, or crashing with an out-of-range exception; the opt-in CCITT raster path now advances
  correctly. Cross-reference byte offsets are bounded, and an offset too large for the format is
  rejected rather than silently truncated.
- **Output escaping.** Caller-supplied resource and marked-content names are escaped so they cannot
  inject content-stream operators; XML-illegal control characters are stripped from XMP metadata;
  non-ASCII link URIs are percent-encoded; and duplicate form-field names and the reserved radio
  `Off` export value are rejected.
- **Signing.** The signature `/Contents` placeholder is located by a unique sentinel and fails
  closed on an ambiguous match, so signature metadata can no longer derail the patch; the `/M` date
  and the CMS signing-time now share a single value. An encryption decrypt round-trip is now
  exercised on CI.

## [1.5.0] - 2026-06-12

### Added

- **PAdES B-T signature timestamps.** A signature can now carry an RFC-3161 timestamp over the
  signature value, embedded as a CMS signature-timestamp unsigned attribute, to reach PAdES B-T.
  Set `PdfSignatureSettings.TimestampClient` to an `ITimestampClient`; the supplied
  `HttpTimestampClient` requests a token from any RFC-3161 Time Stamping Authority over HTTP, or a
  caller can plug in their own client. When no timestamp client is set the signature is unchanged
  (PAdES B-B). The reserved `/Contents` space is enlarged automatically for a timestamped
  signature left at the default size.

## [1.4.0] - 2026-06-11

### Added

- **JBIG2 images.** `Jbig2ImageLoader` reads JBIG2 bilevel images and embeds them as 1-bit `/JBIG2Decode`. A standalone JBIG2 file is parsed and split into its page segments and a `/JBIG2Globals` side-stream (symbol and pattern dictionaries and tables), as the PDF embedded organisation requires; a file with no global segments stays self-contained.
- **JPEG 2000 images.** `JpxImageLoader` reads JP2 box files and raw codestreams (`.j2k`/`.j2c`), takes width, height, component count, and bit depth from the `ihdr`/`SIZ` header and colour space from the `colr` box, and embeds the codestream as `/JPXDecode`.
- **CCITT Group 3 TIFF.** The TIFF loader now reads Compression 2 (Modified Huffman) and 3 (Group 3 / T.4) in addition to Group 4, mapping the `T4Options` tag to the `/CCITTFaxDecode` `/DecodeParms` (`K`, `EncodedByteAlign`, `EndOfLine`). `CcittImageLoader.Load` gained an `endOfLine` parameter for the Group 3 end-of-line convention.
- **Opt-in raster decode.** A new `ImageLoadOptions.DecodeMode` (`Passthrough` by default, or `DecodeToRaster`) decodes a codestream to pixels and re-encodes it losslessly with FlateDecode for viewers without the native codec. Raster decode covers CCITT Group 3 one-dimensional data and JBIG2 MMR generic regions; the other variants (CCITT two-dimensional and Group 4, JBIG2 arithmetic, symbol, text, and halftone segments, and all JPEG 2000) report `NotSupportedException` when raster decode is requested and continue to pass through unchanged. Passthrough stays the default and is always lossless.

### Security

- **Image-codec hardening.** The JBIG2 segment parser, the JPEG 2000 box and marker walker, and the CCITT decoder bound every offset and length against the input, cap segment counts and decoded-output size, and reject truncated, malformed, or oversized data with `InvalidDataException`/`NotSupportedException` rather than over-reading, looping, or exhausting memory. Valid images are unaffected.

## [1.3.0] - 2026-06-11

### Added

- **Interlaced (Adam7) PNG.** Interlaced PNGs now load; the loader de-interlaces the seven Adam7 passes instead of rejecting the file.
- **16-bit image fidelity.** 16-bit-per-channel PNG and TIFF images keep their full bit depth by default (`BitsPerComponent 16`) rather than being reduced to 8 bits. Pass `new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight }` to the new `PngImageLoader.Load` / `TiffImageLoader.Load` overloads to opt into 8-bit downsampling for smaller output. Images that must be transcoded are always re-encoded losslessly with FlateDecode; JPEG and CCITT data is embedded verbatim with no re-encoding.
- **More TIFF compressions.** The TIFF loader now reads LZW (including the horizontal-differencing predictor), new-style JPEG (single strip, any photometric including YCbCr, embedded as DCTDecode), Group-4 fax (embedded as CCITTFaxDecode), and planar (`PlanarConfiguration 2`) images, in addition to the existing uncompressed and PackBits. `FillOrder 2` data is normalised to MSB-first.
- **CCITT Group 3/4 passthrough.** `CcittImageLoader` wraps raw CCITT-compressed bytes as a 1-bit `/CCITTFaxDecode` image with the matching `/DecodeParms` (K, Columns, Rows, BlackIs1) without decoding; the viewer decodes at render time. Single-strip Group-4 TIFFs are routed through it, with polarity taken from the TIFF photometric. The new image paths are checked on CI with veraPDF under PDF/A-2b.

### Security

- **Image-codec hardening.** The TIFF-LZW decoder and the interlaced-PNG and TIFF strip readers bound their reads and reject corrupt, truncated, or oversized input — invalid LZW codes, output-length mismatches, decompression bombs, out-of-range strip offsets, and hostile dimensions — with `InvalidDataException`/`NotSupportedException` rather than over-reading, looping, or exhausting memory. Valid images are unaffected.

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

[1.5.2]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.2
[1.5.1]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.1
[1.5.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.0
[1.4.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.4.0
[1.3.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.3.0
[1.2.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.2.0
[1.1.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.1.0
[1.0.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.0.0
