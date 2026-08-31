# VellumPdf Layout guide

`VellumPdf.Layout` is the high-level document builder — the element tree (paragraphs, tables,
lists, images) and pagination engine described in [`docs/architecture.md`](architecture.md).
Most applications only need this package; reach for [`VellumPdf.Kernel`](kernel-guide.md)
directly when you need a PDF operator, structure, or graphic effect this layer doesn't expose.

```csharp
using VellumPdf.Fonts;             // Standard14
using VellumPdf.Layout;            // Document
using VellumPdf.Layout.Core;       // TextStyle
using VellumPdf.Layout.Elements;   // Heading, Paragraph

using var doc = new Document();
doc.SetDefaultFont(new TextStyle { Font = Standard14.Helvetica, FontSize = 11 });
doc.Add(new Heading("Hello, world!"));
doc.Add(new Paragraph("Generated with VellumPdf — no native dependencies."));
doc.Save("hello.pdf");
```

`Document` also exposes `PageSize`, `Margins`, `Conformance`, `Tagged`, `SetHeader`/`SetFooter`,
`Encrypt(PdfEncryptionSettings)`, and `UseTrueTypeFont`/`LoadTrueTypeFont` for embedding a font.
For everyday reports, invoices, and letters this is the whole surface you need; the Kernel guide
covers the lower layer these types are built on.

`VellumPdf.Layout` is a **Stable** package — its entire public surface is in
`PublicAPI.Shipped.txt`, unlike the still-Preview `VellumPdf.Reader`.

---

## Capability table

Built from `src/VellumPdf.Layout/PublicAPI.Shipped.txt` and the test suite, not from the roadmap
prose — a row is only marked Supported where a test or a public member proves it. Two rows are
marked for reviewer verification rather than guessed: see the note below the table. Also published
in [the package README](https://github.com/Tim81/VellumPDF/blob/main/src/VellumPdf.Layout/README.md#capabilities);
a guard test keeps the two copies byte-identical.

<!-- capability-table:layout:start -->
| Capability | Status | Target milestone / ISO reference |
| --- | --- | --- |
| Paragraph flow: wrapping, alignment (left/center/right/justify), mixed-style inline runs | ✅ Supported | — |
| Tables: rows, columns, row spanning, borders | ✅ Supported | — |
| Lists: ordered (decimal/alpha/roman), unordered, nested | ✅ Supported | — |
| Headers and footers with page-number templates | ✅ Supported | — |
| Images: JPEG, PNG (incl. interlaced and 16-bit), CCITT G4, TIFF-LZW | ✅ Supported | — |
| Font embedding and subsetting: TrueType (`glyf`) and OpenType/CFF | ✅ Supported | — |
| Links (URI) and document outline/bookmarks | ✅ Supported | — |
| Pie charts, including PDF/UA figure vs. decorative-artifact tagging | ✅ Supported | — |
| PDF/A conformance: 2a, 2b, 2u | ✅ Supported | ISO 19005-2 (PDF/A-2 profiles) |
| PDF/UA-1 accessibility tagging | ✅ Supported | ISO 14289-1 |
| Document metadata, XMP packet, document ID | ✅ Supported | — |
| CMYK / ICC output intents via `Document.UseCmykOutputIntent`/`SetPdfAOutputIntent` | ✅ Supported | delegates to Kernel; veraPDF-proven |
| Encryption via `Document.Encrypt` | ✅ Supported | ISO 32000-2 §7.6 (delegates to Kernel) |
| Barcode symbols as a Layout element | ❌ Not yet (only an `IRenderer` extension seam exists) | no tracked milestone |
| Form elements with their own labels; build-time accessibility diagnostics | ⏳ Planned | Layout C |
| PDF/A-1 and PDF/A-4 | ⏳ Planned | v2.5 (#218, #222) |
| PDF/UA-2 element set (generated contents/indexes, captions, table header semantics) | ⏳ Planned | Layout B |
| Footnotes and endnotes | ⏳ Planned | Layout B |
| Floats and hyphenation | ⏳ Planned | Layout D |
| Widow and orphan control | ⏳ Planned | Layout D |
| Multi-column flow | ⏳ Planned | Layout F |
| Additional chart types beyond pie | ⏳ Planned | Layout F |
| SVG import | ⏳ Planned | Layout F (epic) |
| Unicode bidi, right-to-left, and complex-script shaping | ⏳ Planned | Layout G (epic) |
<!-- capability-table:layout:end -->

---

## See also

- [Kernel guide](kernel-guide.md) — the lower-level API this package builds on.
- [Reader guide](reader-guide.md) — opening a PDF this library, or something else, already wrote.
- [Barcodes guide](barcodes-guide.md) — the standalone symbology package referenced above.
