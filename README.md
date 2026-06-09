# VellumPdf

A modern, **dependency-free PDF generation library for .NET 10**, implemented
clean-room from the open **ISO 32000** standard.

> **Status: beta.** Core features are implemented and CI-validated — including
> PDF/A-2b/2u conformance proven on every push with veraPDF. The public API may
> still change before 1.0.

## Why VellumPdf

- **Zero runtime dependencies.** The core is built entirely on the .NET base
  class library — no native binaries, no third-party packages. (The optional
  signing package is the sole exception: it uses `System.Security.Cryptography.Pkcs`.)
- **AOT- and trim-ready.** Ships as managed DLLs; ideal for Native AOT,
  trimming, containers, and serverless. A Native-AOT smoke test guards this.
- **Unicode-first text.** Embeds and subsets TrueType fonts, embeds OpenType-CFF,
  emits composite (CID) fonts with subset tags, and writes `ToUnicode` maps so
  output stays searchable and copy-paste-able.
- **Two API tiers.** A low-level canvas for precise drawing, and a high-level
  document/layout engine with paragraphs, headings, lists, tables, images, and
  automatic pagination.
- **Built for the hard standards.** PDF/A (archival) metadata, output intents,
  and font embedding; a tagged-PDF structure-tree channel; PAdES digital
  signatures; and interactive AcroForms are all implemented.
- **Permissive license.** Apache-2.0 — free to use in proprietary products.

## Packages

| Package | Status | Description |
| --- | --- | --- |
| `VellumPdf.Kernel` | Stable | Object model, canvas, Standard-14 fonts, TrueType/OpenType embedding + subsetting, images (JPEG/PNG/BMP/GIF/TIFF), AES-256 encryption, object/cross-reference streams, AcroForm fields, tagged-PDF structure tree, and the PDF/A-2 metadata + sRGB output-intent scaffold. |
| `VellumPdf.Layout` | Stable | High-level document builder: paragraphs, headings, lists, tables, images, header/footer bands, bookmarks, and automatic pagination. |
| `VellumPdf.Signing` | Beta | PAdES / PKCS#7 detached digital signatures over an incremental-update revision. |
| _(roadmap)_ `VellumPdf.Conformance` | Planned | In-process PDF/A and PDF/UA preflight validator. |
| _(roadmap)_ `VellumPdf.Barcodes` | Planned | QR, PDF417, Code128, EAN. |

## Quick start

```csharp
using VellumPdf.Document;          // PdfConformance
using VellumPdf.Fonts;             // Standard14
using VellumPdf.Layout;            // Document
using VellumPdf.Layout.Core;       // TextStyle
using VellumPdf.Layout.Elements;   // Paragraph, Heading
using VellumPdf.Layout.Elements.Table; // TableElement

// Basic document — defaults to A4.
using var doc = new Document();
doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
doc.Add(new Heading("Hello, world!"));
doc.Add(new Paragraph("Generated with VellumPdf — no native dependencies."));
doc.Save("hello.pdf");
```

```csharp
// PDF/A-2b archival document. PDF/A requires every glyph to come from an
// embedded font, so load a TrueType font and use it for all text.
using var archive = new Document { Conformance = PdfConformance.PdfA2b };
var font = archive.LoadTrueTypeFont("/path/to/DejaVuSans.ttf");
var style = new TextStyle { FontRef = font, FontSize = 12 };

archive.Add(new Paragraph("This document validates as PDF/A-2b.", style));

var table = new TableElement { DefaultCellStyle = style };
table.SetColumnWidths(200, 200);
table.AddHeaderRow().AddCell("Item").AddCell("Value");
table.AddRow().AddCell("Format").AddCell("PDF/A-2b");
archive.Add(table);

archive.Save("archive.pdf");
```

## Conventions

- **Units.** All coordinates and sizes are in PDF user-space **points** (1 pt = 1/72 inch).
  `PageSize` provides the common ISO-A sizes plus a `PageSize.Mm(width, height)` helper for
  custom millimetre dimensions.
- **Synchronous I/O.** Saving, signing, and the font/image loaders are synchronous by design
  for 1.0 — there is no `async` surface. Offload to `Task.Run` if you need to keep a thread free.

## Validation & CI

Correctness is enforced on every push by running real external validators as CI
oracles — a missing tool fails the build, so the gates can never silently skip:

- **`qpdf --check`** — structural integrity of every generated document type.
- **`pdftotext`** (poppler) — text-extraction round-trip proving `ToUnicode` maps.
- **`pdfsig`** (poppler) — signature validity for PAdES documents.
- **veraPDF** (official `verapdf/cli` Docker image) — strict PDF/A-2b/2u
  conformance over embedded-font, table, image, and tagged documents. A
  non-compliant report fails CI with the full rule list attached.

## Not yet / roadmap

| Area | Notes |
| --- | --- |
| PDF/A-2a (level A) | Metadata is emitted; full accessible-tagging conformance (catalog `/Lang`, role map, validated marked-content↔structure linkage) is in progress. |
| PDF/UA-1 | Structure tree exists; the accessibility validation gate is not yet wired. |
| In-process preflight | `VellumPdf.Conformance` PDF/A and PDF/UA validator package. |
| CFF subsetting | OpenType-CFF fonts are currently embedded whole (not subsetted). |
| Image codecs | JBIG2, JPEG 2000, and CCITT Group 3/4 decoders. |
| Colour management | DeviceRGB and DeviceGray today; CMYK and ICC-based colour. |
| Linearization | "Fast web view" object ordering. |
| Signature LTV | Long-term validation data (OCSP/CRL) for archival PAdES. |
| Barcodes | The `VellumPdf.Barcodes` package. |

## Building

Requires the .NET 10 SDK.

```bash
dotnet build VellumPdf.slnx
dotnet test  VellumPdf.slnx
```

The veraPDF conformance gate runs automatically in CI; to reproduce it locally,
install [veraPDF](https://verapdf.org) (or use its Docker image) so the
`verapdf` CLI is on your `PATH`, then run the oracle tests.

## License & provenance

Licensed under the [Apache License 2.0](LICENSE). VellumPdf is an original,
independent implementation written solely from open published specifications;
see [NOTICE](NOTICE) and [docs/architecture.md](docs/architecture.md).
