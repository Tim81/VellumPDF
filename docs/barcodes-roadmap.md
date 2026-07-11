# Barcodes: verification record and roadmap

A maintainer note, not user-facing documentation; it is deliberately left out of
`toc.yml`. It records the outcome of the post-1.9.0 verification pass over
`VellumPdf.Barcodes` (July 2026) and the roadmap that came out of it, so the
reasoning is not lost between releases.

## Verification outcome

The six shipped symbologies were checked against their ISO/GS1 specifications.
**Nothing shipped is broken.** Every verifiable numeric detail matched the
standard: QR ECI 26 for UTF-8, the Code 128 code values / modulo-103 check /
start-stop patterns, the GTIN check digit, the EAN quiet zones (including the
asymmetric EAN-13 11-left / 7-right that several libraries get wrong), the EAN
add-on gap and parity, and the ITF-14 wide/narrow ratio range. The gaps are
missing symbologies and a handful of optional density features, not defects.

Standards the shipped encoders track (cite the current edition in any future
work):

| Symbology | Standard | Current edition |
| --- | --- | --- |
| QR Code | ISO/IEC 18004 | 2024 (Ed. 4) |
| PDF417 | ISO/IEC 15438 | 2015 |
| Code 128 | ISO/IEC 15417 | 2007 |
| EAN/UPC, GS1-128, ITF-14 | GS1 General Specifications | Release 25.0 (2025) |

## Prioritized future symbologies (delivered in 1.10.0)

Ranked by real-world demand (evidence and citations live in each issue). All four are shipped.

1. **Data Matrix / GS1 Data Matrix** — [#151](https://github.com/Tim81/VellumPDF/issues/151), delivered. ISO/IEC 16022:2024. Closed the single largest gap: the only 2D format missing from every comparable general-purpose .NET library, and mandated for pharma serialization (EU FMD, US DSCSA), FDA UDI, industrial part marking, and GS1 Sunrise 2027 retail POS.
2. **GS1-mode QR** — [#152](https://github.com/Tim81/VellumPDF/issues/152), delivered. Reuses the QR engine already in the package; one of the two formats GS1 promotes for Sunrise 2027, including a GS1 Digital Link mode.
3. **Aztec** — [#153](https://github.com/Tim81/VellumPDF/issues/153), delivered. ISO/IEC 24778:2024. The other universally-shipped 2D format; dominant in transport ticketing (IATA boarding passes, European rail).
4. **Code 39** — [#154](https://github.com/Tim81/VellumPDF/issues/154), delivered. ISO/IEC 16388:2023. The most universal 1D symbology that was still missing (7 of 8 comparable libraries ship it), including Full ASCII mode.
5. GS1 DataBar, then MaxiCode and Codabar — niche; not yet ticketed.

## Completeness backlog

Optional features inside the shipped symbologies, tracked together in
[#155](https://github.com/Tim81/VellumPDF/issues/155). Shipped in 1.10.0: UPC-E
and GS1-128 parenthesized-AI human-readable text, the two items with the most
real-world demand. Also delivered, completing #155 in full: QR Kanji mode
(ISO/IEC 18004 §7.4.6, its Shift-JIS lookup table generated clean-room from
the Unicode Consortium's SHIFTJIS.TXT mapping and filtered to
CP932-round-trippable code points), QR Structured Append (ISO/IEC 18004 §8),
Compact PDF417, Macro PDF417 (ISO/IEC 15438 Annex H), and Code 128 FNC4 /
extended Latin-1 (ISO/IEC 15417): plain Code 128 now carries the full Latin-1
range (128-255) via FNC4, while GS1-128 still rejects it, since the GS1
General Specifications disallow FNC4 in a GS1-128 symbol.

## Decisions on record

- **Generation only, no decoding:** the highest-volume .NET barcode packages are
  generation-only, and the package's purpose is producing PDFs, not scanning
  them. Decoding is a different problem and stays out of scope.
- **`PublicAPI.Shipped.txt` is promoted as of 1.10.0:** the whole surface sat in
  `PublicAPI.Unshipped.txt` while Barcodes was Preview, on the basis that Preview
  means the surface can still move. With #151-155 landed and the package
  graduated to Stable in 1.10.0, the full surface, including the five new
  symbologies, moved to `PublicAPI.Shipped.txt`, and `Unshipped.txt` was reset
  to its header. Any further change to the Barcodes public API is now a
  breaking change subject to the same analyzer gate as every other Stable
  package.
- **The QR unmarked-Latin-1 default is standard-conformant:** the QR standard's
  default interpretation of unmarked byte data is ISO-8859-1, so emitting no ECI
  for Latin-1 text is both correct and the most interoperable choice. An explicit
  ECI-3 opt-in could be added later if a caller needs it; it is not required.
- **Aztec placement geometry, provenance:** the data-layer placement (ISO/IEC
  24778 clauses 7.1-7.3) was authored from the standard's structural rules plus
  the original public Aztec patent (US 5,591,956), which describes the same
  layout directly: the spiral of concentric layers, 2-module domino pairs,
  most-significant-bit-first ordering, the reference grid at every 16th module,
  and the data field's displacement around it. ISO/IEC 24778's own coordinate
  figures (Figures 4 and 5) are raster diagrams absent from the standard's
  freely available preview pages, so the exact coordinate convention was
  verified against module matrices generated by zxing-cpp as an
  interoperability cross-check, not as an implementation source. See
  [docs/barcodes-guide.md](barcodes-guide.md#aztec-code) for the full citation.

## Maintenance note: README duplication

Each package ships its own `README.md` (see `src/Directory.Build.targets`). Every
one of those repeats two blocks verbatim (the package-family/status table and
the roadmap table) because NuGet renders a single flattened Markdown file per
package with no include mechanism. When the family list or the roadmap changes,
update all nine copies: the root `README.md` and the eight `src/*/README.md`
files.
