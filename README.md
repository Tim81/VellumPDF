# VellumPdf

A modern, **dependency-free PDF generation library for .NET 10**, implemented
clean-room from the open **ISO 32000** standard.

> **Status: early development.** The public API is taking shape and will change.

## Why VellumPdf

- **Zero runtime dependencies.** The core is built entirely on the .NET base
  class library — no native binaries, no third-party packages.
- **AOT- and trim-ready.** Ships as a single managed DLL; ideal for Native AOT,
  trimming, containers, and serverless.
- **Unicode-first text.** Embeds and subsets TrueType/OpenType fonts, emits
  composite (CID) fonts, and writes `ToUnicode` maps so output stays searchable
  and copy-paste-able.
- **Two API tiers.** A low-level canvas for precise drawing, and a high-level
  document/layout engine with paragraphs, lists, tables, images, and automatic
  pagination.
- **Built for the hard standards.** The architecture reserves seams for
  PDF/A (archival), PDF/UA (tagged/accessible), PAdES digital signatures, and
  interactive AcroForms from day one.
- **Permissive license.** Apache-2.0 — free to use in proprietary products.

## Packages

| Package | Description |
| --- | --- |
| `VellumPdf.Kernel` | Low-level object model, document structure, content streams, fonts, images. |
| `VellumPdf.Layout` | High-level document and layout engine (paragraphs, tables, pagination, tagging). |
| _(roadmap)_ `VellumPdf.Signing` | Incremental update + PKCS#7 / PAdES signatures. |
| _(roadmap)_ `VellumPdf.Forms` | Interactive AcroForm fields and appearance generation. |
| _(roadmap)_ `VellumPdf.Conformance` | PDF/A and PDF/UA profiles and preflight. |
| _(roadmap)_ `VellumPdf.Barcodes` | QR, PDF417, Code128, EAN. |

## Planned API (not yet implemented)

```csharp
using VellumPdf;
using VellumPdf.Layout;

using var document = new PdfDocument();      // defaults: A4, millimetres
document.Add(new Paragraph("Hello, world.").FontSize(12));
document.Save("hello.pdf");
```

Defaults are **A4** page size and **metric (millimetre)** units.

## Building

Requires the .NET 10 SDK.

```bash
dotnet build VellumPdf.slnx
dotnet test  VellumPdf.slnx
```

## License & provenance

Licensed under the [Apache License 2.0](LICENSE). VellumPdf is an original,
independent implementation written solely from open published specifications;
see [NOTICE](NOTICE) and [docs/architecture.md](docs/architecture.md).
