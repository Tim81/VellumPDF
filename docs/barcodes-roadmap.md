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

## Prioritized future symbologies

Ranked by real-world demand (evidence and citations live in each issue).

1. **Data Matrix / GS1 Data Matrix** — [#151](https://github.com/Tim81/VellumPDF/issues/151). ISO/IEC 16022:2024. The single largest gap: the only 2D format missing from every comparable general-purpose .NET library, and mandated for pharma serialization (EU FMD, US DSCSA), FDA UDI, industrial part marking, and GS1 Sunrise 2027 retail POS.
2. **GS1-mode QR** — [#152](https://github.com/Tim81/VellumPDF/issues/152). Best effort-to-value: it reuses the QR engine already in the package and is one of the two formats GS1 promotes for Sunrise 2027.
3. **Aztec** — [#153](https://github.com/Tim81/VellumPDF/issues/153). ISO/IEC 24778:2024. The other universally-shipped 2D format; dominant in transport ticketing (IATA boarding passes, European rail).
4. **Code 39** — [#154](https://github.com/Tim81/VellumPDF/issues/154). ISO/IEC 16388:2023. The most universal 1D symbology still missing (7 of 8 comparable libraries ship it); low effort.
5. GS1 DataBar, then MaxiCode and Codabar — niche; not yet ticketed.

## Completeness backlog

Optional features inside the shipped symbologies, tracked together in
[#155](https://github.com/Tim81/VellumPDF/issues/155): QR Kanji mode and
Structured Append, UPC-E, Compact and Macro PDF417, GS1-128 parenthesized-AI
human-readable text, and Code 128 FNC4 / extended Latin-1.

## Decisions on record

- **Generation only, no decoding:** the highest-volume .NET barcode packages are
  generation-only, and the package's purpose is producing PDFs, not scanning
  them. Decoding is a different problem and stays out of scope.
- **`PublicAPI.Shipped.txt` stays unpromoted while Barcodes is Preview:** the
  whole surface sits in `PublicAPI.Unshipped.txt` on purpose. Preview means the
  surface is still allowed to move; promote it when the package graduates to
  Stable, not before.
- **The QR unmarked-Latin-1 default is standard-conformant:** the QR standard's
  default interpretation of unmarked byte data is ISO-8859-1, so emitting no ECI
  for Latin-1 text is both correct and the most interoperable choice. An explicit
  ECI-3 opt-in could be added later if a caller needs it; it is not required.

## Maintenance note: README duplication

Each package ships its own `README.md` (see `src/Directory.Build.targets`). Every
one of those repeats two blocks verbatim (the package-family/status table and
the roadmap table) because NuGet renders a single flattened Markdown file per
package with no include mechanism. When the family list or the roadmap changes,
update all nine copies: the root `README.md` and the eight `src/*/README.md`
files.
