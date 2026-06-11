# Changelog

All notable changes to VellumPdf will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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

[1.1.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.1.0
[1.0.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.0.0
