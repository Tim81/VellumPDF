# VellumPdf Kernel power-user guide

This guide covers the low-level `VellumPdf.Kernel` API — the innermost layer of
the library described in [`docs/architecture.md`](architecture.md).  Use this
layer when you need direct control over the PDF object model, custom graphics
operators, or document structures that the high-level Layout API does not expose.
For everyday document generation (paragraphs, tables, pagination) the Layout API
is the better starting point.

## When to use the Kernel API

| Use the **Layout API** (`VellumPdf.Layout`) | Use the **Kernel API** (`VellumPdf.Kernel`) |
|---|---|
| Reports, invoices, letters | Custom graphic effects, shadings, clipping |
| Automatic pagination | Precise control of every PDF operator |
| Tagged PDF / accessibility | Custom annotation structures |
| Standard paragraph/table layout | Constructing arbitrary PDF dictionaries |

The Kernel API is a **supported, stable 1.0 public surface**.  All types
discussed here are in the `VellumPdf.Kernel` NuGet package.

---

## 1. A minimal document

```csharp
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

using var doc = new PdfDocument();

// AddPage() uses the DefaultPageSize (A4 at construction).
// Pass a PdfRectangle to specify a different size.
var page = doc.AddPage(PageSize.A4);

var font = doc.UseFont(Standard14.Helvetica);
var canvas = new PdfCanvas(page);

canvas
    .BeginText()
    .SetFont(font, 12)
    .SetTextMatrix(1, 0, 0, 1, 72, 720)   // identity matrix, origin at (72, 720)
    .ShowText("Hello, VellumPdf!")
    .EndText();

canvas.Finish();   // MUST be called before Save

using var output = new FileStream("hello.pdf", FileMode.Create);
doc.Save(output);
```

### Coordinate system and units

PDF user space has its **origin at the bottom-left corner**, with **Y increasing
upward**.  The unit is the **PDF point: 1 pt = 1/72 inch**.

A4 in points is 595.28 × 841.89 pt.  `PageSize.A4` returns a `PdfRectangle`
with those dimensions; `PageSize.Mm(width, height)` converts millimetres.

```csharp
// Custom page size: 200 mm × 100 mm
var page = doc.AddPage(PageSize.Mm(200, 100));
```

Available named sizes: `PageSize.A0`–`A6`, `PageSize.Letter`, `PageSize.Legal`,
`PageSize.Ledger`.

### Document metadata

```csharp
doc.Info.Title    = "Annual Report 2026";
doc.Info.Author   = "Finance Team";
doc.Info.Subject  = "Full-year results";
doc.Info.Keywords = "finance annual report";
doc.Info.Producer = "Acme PDF Service";
```

---

## 2. Drawing with `PdfCanvas`

`PdfCanvas` maps directly to PDF content-stream operators.  All methods return
`this` for fluent chaining.  **Call `canvas.Finish()` before `doc.Save()`**;
forgetting this is the most common mistake (see section 11).

### Paths and shapes

```csharp
var canvas = new PdfCanvas(page);

// Filled rectangle
canvas.Rectangle(50, 50, 200, 100).Fill();

// Stroked triangle
canvas
    .MoveTo(100, 200)
    .LineTo(200, 400)
    .LineTo(0, 400)
    .ClosePath()
    .Stroke();

// Filled and stroked with a Bézier curve
canvas
    .MoveTo(300, 100)
    .CurveTo(350, 200, 450, 200, 500, 100)
    .FillAndStroke();

canvas.Finish();
```

Path-ending operators: `Fill()`, `Stroke()`, `FillAndStroke()`,
`FillEvenOdd()`, `CloseAndStroke()`, `EndPath()`.

### Arcs and circles

PDF has no arc operator, so `AppendArc` approximates one with cubic Bézier
curves (one per 90° or less). Angles are in radians, measured counter-clockwise
from the +X axis. It appends to the current path and emits no `MoveTo`, so move
to the arc's start point first. A sweep whose end angle is below the start runs
clockwise.

```csharp
// Full circle: start at the right edge (angle 0) and sweep one full turn.
canvas
    .MoveTo(230, 150)                          // start point at (cx + r, cy)
    .AppendArc(150, 150, 80, 0, 2 * Math.PI)   // centre (150,150), radius 80
    .Stroke();

// A 90° pie wedge from the centre.
canvas
    .MoveTo(150, 150)                          // centre
    .LineTo(230, 150)                          // out to the rim at angle 0
    .AppendArc(150, 150, 80, 0, Math.PI / 2)
    .ClosePath()
    .Fill();
```

For whole pie charts, the `VellumPdf.Layout` package has a higher-level
`PieChart` element that handles slice angles, colours, and labels for you.

### Colors

```csharp
// RGB (components 0.0–1.0)
canvas.SetFillColorRgb(0.8, 0.2, 0.2);
canvas.SetStrokeColorRgb(0, 0, 0);

// CMYK (components 0.0–1.0)
canvas.SetFillColorCmyk(0, 0.5, 1.0, 0);
canvas.SetStrokeColorCmyk(0, 0, 0, 0.8);

// Greyscale (0.0 = black, 1.0 = white)
canvas.SetFillColorGray(0.5);
canvas.SetStrokeColorGray(0);
```

### Graphics state: save/restore and transforms

```csharp
canvas.SaveState();

// Concatenate a transformation matrix (scale ×2 in X and Y)
canvas.Concat(2, 0, 0, 2, 0, 0);

canvas.Rectangle(10, 10, 100, 50).Fill();

canvas.RestoreState();   // pops back to the saved state
```

### Line style

```csharp
canvas.SetLineWidth(2.5);
canvas.SetLineCap(1);           // 0=butt, 1=round, 2=projecting square
canvas.SetLineJoin(1);          // 0=miter, 1=round, 2=bevel
canvas.SetMiterLimit(10);

// Dashed line: [on off] pattern, phase
canvas.SetLineDash([5, 3], 0);

// Restore to solid
canvas.SetSolidLine();
```

### Transparency

```csharp
canvas.SetFillAlpha(0.7);     // 70% opaque fill
canvas.SetStrokeAlpha(0.4);   // 40% opaque stroke
```

Each unique alpha value registers a single `/ExtGState` resource; repeated
calls with the same value are deduplicated automatically.

### Clipping

```csharp
canvas.SaveState();
canvas.Rectangle(20, 20, 300, 200).Clip().EndPath();
// Everything drawn here is clipped to the rectangle
canvas.Rectangle(0, 0, 500, 500).Fill();
canvas.RestoreState();
```

Use `ClipEvenOdd()` for even-odd fill rule clipping.

### Gradients

```csharp
using VellumPdf.Graphics;

// Axial (linear) gradient — left to right, red to blue
canvas.PaintAxialGradient(0, 100, 300, 100,
    new KernelColor(1, 0, 0),   // c0: red at (0,100)
    new KernelColor(0, 0, 1));  // c1: blue at (300,100)

// Radial gradient — inner circle (radius 0) to outer (radius 80)
canvas.PaintRadialGradient(150, 150, 0, 150, 150, 80,
    new KernelColor(1, 1, 0),   // c0: yellow at centre
    new KernelColor(0, 0, 0));  // c1: black at rim

// Named shortcuts
canvas.PaintAxialGradient(0, 0, 200, 0, KernelColor.Black, KernelColor.White);
```

Gradient resources are deduplicated by value: identical parameters produce a
single `/Shading` resource.

---

## 3. Text and fonts

### Standard-14 fonts

The 14 built-in PDF fonts require no embedding.  Reference them with
`doc.UseFont(Standard14.*)`:

```csharp
var helvetica     = doc.UseFont(Standard14.Helvetica);
var helveticaBold = doc.UseFont(Standard14.HelveticaBold);
var courier       = doc.UseFont(Standard14.Courier);
var timesRoman    = doc.UseFont(Standard14.TimesRoman);
```

All 14 members: `Helvetica`, `HelveticaBold`, `HelveticaOblique`,
`HelveticaBoldOblique`, `TimesRoman`, `TimesBold`, `TimesItalic`,
`TimesBoldItalic`, `Courier`, `CourierBold`, `CourierOblique`,
`CourierBoldOblique`, `Symbol`, `ZapfDingbats`.

### Drawing Standard-14 text

```csharp
var font = doc.UseFont(Standard14.Helvetica);
var canvas = new PdfCanvas(page);

canvas
    .BeginText()
    .SetFont(font, 14)
    .SetTextMatrix(1, 0, 0, 1, 72, 700)   // position at (72, 700)
    .ShowText("Line one")
    .SetTextLeading(18)                    // 18 pt leading
    .NextLine()
    .ShowText("Line two")
    .SetCharSpacing(1.5)
    .SetWordSpacing(4)
    .NextLine()
    .ShowText("Line three — wider spacing")
    .EndText();

canvas.Finish();
```

### Measuring Standard-14 text

```csharp
using VellumPdf.Fonts;

double widthPts = Standard14Metrics.MeasureString(Standard14.Helvetica, "Hello", 12.0);
int rawWidth   = Standard14Metrics.GetWidth(Standard14.Helvetica, 'H'); // in 1/1000 em units
```

### Embedding a TrueType / OpenType font

The API accepts raw `.ttf` / `.otf` bytes.  Loading font bytes from disk is
intentionally left to the caller so the library imposes no platform-specific
path conventions.

```csharp
// Load font bytes — cross-platform: supply them however suits your host
byte[] fontData = File.ReadAllBytes(@"C:\Windows\Fonts\arial.ttf");

EmbeddedFontHandle handle = doc.UseTrueTypeFont(fontData);

// Register which pages use this font (required before Save)
doc.RegisterEmbeddedFontUsage(page, handle);

var canvas = new PdfCanvas(page);
canvas
    .BeginText()
    .SetFontByName(handle.ResourceName, 12)
    .SetTextMatrix(1, 0, 0, 1, 72, 700);

// Map Unicode code points → glyph IDs, then draw
var text = "Hello, world!";
var gids = new ushort[text.Length];
int count = handle.GetGlyphIds(text, gids);
canvas.ShowGlyphs(gids.AsSpan(0, count));

canvas.EndText();
canvas.Finish();
```

`doc.UseTrueTypeFont` accepts both TrueType (`.ttf`) and OpenType-CFF (`.otf`)
fonts.  The library subsets the font automatically and embeds only the glyphs
that were actually drawn.

### Measuring embedded-font text

```csharp
double widthPts = handle.MeasureString("Hello", 12.0);
ushort gid      = handle.GetGlyphId('A');   // single code point → GID
```

---

## 4. Images

Load image bytes and register the resulting `PdfImageXObject` with the document.
Five formats are supported:

| Format | Loader class |
|---|---|
| PNG (RGB, RGBA, indexed, 1/2/4/8-bit) | `PngImageLoader.Load(byte[])` |
| JPEG | `JpegImageLoader.Load(byte[])` |
| BMP (24-bit and 8-bit palette) | `BmpImageLoader.Load(byte[])` |
| GIF (with transparency) | `GifImageLoader.Load(byte[])` |
| TIFF | `TiffImageLoader.Load(byte[])` |

JPEG bytes are passed through as-is (`DCTDecode`); all other formats are
re-encoded with `FlateDecode`.  PNG images with an alpha channel automatically
produce an `/SMask` soft-mask stream.

```csharp
using VellumPdf.Canvas;
using VellumPdf.Images;

byte[] pngBytes = File.ReadAllBytes("logo.png");
PdfImageXObject image = PngImageLoader.Load(pngBytes);

using var doc = new PdfDocument();
var page = doc.AddPage();

// Register the image; the returned name is the /XObject resource key
string resourceName = doc.RegisterImageXObject(page, image, "Im1");

var canvas = new PdfCanvas(page);
canvas
    .SaveState()
    // Scale the image to 200 × 150 pt at position (100, 400)
    .Concat(200, 0, 0, 150, 100, 400)
    .DoXObject(resourceName)
    .RestoreState();

canvas.Finish();
```

`image.Width` and `image.Height` give the pixel dimensions.

---

## 5. Annotations and outlines

### URI link annotation

```csharp
using VellumPdf.Annotations;

doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
{
    Rect = new PdfRectangle(72, 700, 200, 714),   // hotspot in page coordinates
    Uri  = "https://example.com",
});
```

### Internal (cross-page) link

```csharp
var page2 = doc.AddPage();

doc.RegisterLinkAnnotation(page1, new PdfLinkAnnotation
{
    Rect     = new PdfRectangle(72, 650, 200, 664),
    DestPage = page2,
    DestLeft = 0,
    DestTop  = PageSize.A4.Height,   // top of page 2
});
```

### Document outline (bookmarks)

```csharp
doc.AddOutlineEntry(new PdfOutlineEntry
{
    Title    = "Chapter 1",
    DestPage = page1,
    DestLeft = 0,
    DestTop  = 800,
    Level    = 0,   // top-level bookmark
});

doc.AddOutlineEntry(new PdfOutlineEntry
{
    Title    = "Section 1.1",
    DestPage = page1,
    DestLeft = 0,
    DestTop  = 600,
    Level    = 1,   // child of the preceding Level-0 entry
});
```

Outline titles are stored as UTF-16BE and displayed verbatim by viewers.
Setting `Level` controls nesting: `0` is a root item; `1` is a child of the
nearest preceding `Level 0` entry, and so on.

---

## 6. The raw PDF object model

The `VellumPdf.Core` namespace exposes the PDF object model directly.  Use it
when you need to construct structures that the higher-level API does not yet
surface.

### Available types

| Type | PDF equivalent | Example |
|---|---|---|
| `PdfNull` | `null` | `PdfNull.Instance` |
| `PdfBoolean` | `true` / `false` | `PdfBoolean.True`, `PdfBoolean.False`, `PdfBoolean.Of(value)` |
| `PdfInteger` | integer | `new PdfInteger(42)` |
| `PdfReal` | real | `new PdfReal(3.14)` |
| `PdfName` | `/Name` | `new PdfName("FlateDecode")` |
| `PdfLiteralString` | `(string)` | `new PdfLiteralString(bytes)` |
| `PdfHexString` | `<hex>` | `new PdfHexString(bytes)` |
| `PdfArray` | `[...]` | `new PdfArray([obj1, obj2])` |
| `PdfDictionary` | `<<...>>` | `new PdfDictionary()` |
| `PdfStream` | stream object | `new PdfStream(data)` |
| `PdfIndirectReference` | `N 0 R` | `new PdfIndirectReference(5)` |
| `PdfIndirectObject` | `N 0 obj … endobj` | `new PdfIndirectObject(3, value)` |

Well-known name constants live on `PdfName` as `static readonly` fields:
`PdfName.Type`, `PdfName.Font`, `PdfName.FlateDecode`, `PdfName.DeviceRGB`, etc.

### Building and serializing dictionaries

```csharp
using VellumPdf.Core;
using VellumPdf.IO;

// Fluent builder — Set() returns the dictionary
var dict = new PdfDictionary()
    .Set(PdfName.Type,    PdfName.Font)
    .Set(PdfName.Subtype, "Type1")         // string overload creates a PdfName
    .Set(PdfName.Length,  1024L);          // long overload creates a PdfInteger

// Serialize to a stream for inspection
using var ms = new MemoryStream();
dict.WriteTo(new PdfWriter(ms));
// ms now contains: << /Type /Font /Subtype /Type1 /Length 1024 >>
```

### Constructing an array

```csharp
var arr = new PdfArray();
arr.Add(new PdfInteger(0));
arr.Add(new PdfInteger(0));
arr.Add(new PdfReal(595.28));
arr.Add(new PdfReal(841.89));
// equivalent to [0 0 595.28 841.89] — a MediaBox value
```

Or using the collection constructor:

```csharp
var arr = new PdfArray([new PdfInteger(1), new PdfName("Fit")]);
```

### When to reach for the raw object model

- You need a custom action dictionary (e.g., a JavaScript action) not exposed
  by the higher-level API.
- You are writing a round-trip test that must inspect the serialized byte output.
- You need to attach arbitrary metadata to a page or the catalog.

---

## 7. Encryption

AES-256 encryption (PDF V5/R6, Standard Security Handler) is applied before
`Save`:

```csharp
using VellumPdf.Encryption;

doc.Encrypt(new PdfEncryptionSettings
{
    UserPassword  = "open-me",
    OwnerPassword = "owner-secret",
    Permissions   = PdfPermissions.Print | PdfPermissions.Copy,
});

doc.Save(stream);
```

`PdfEncryptionSettings` members:

| Property | Type | Notes |
|---|---|---|
| `UserPassword` | `string?` | Password required to open the file |
| `OwnerPassword` | `string?` | Defaults to `UserPassword` when null |
| `Permissions` | `PdfPermissions` | Flags: `Print`, `Modify`, `Copy`, `Annotate`, `FillForms`, `Extract`, `Assemble`, `PrintHighRes`, `All`, `None` |
| `EncryptMetadata` | `bool` | Whether to encrypt the XMP metadata stream (default `true`) |

**Guard:** encryption is incompatible with PDF/A.  Setting both
`doc.Conformance = PdfConformance.PdfA2b` (or any PDF/A level) and calling
`doc.Encrypt(...)` will be caught at save time.

---

## 8. PDF/A conformance

```csharp
// Set before adding pages
doc.Conformance = PdfConformance.PdfA2b;

// Levels available:
// PdfConformance.None    — standard PDF 2.0 (default)
// PdfConformance.PdfA2b  — PDF/A-2b (no transparency allowed in content)
// PdfConformance.PdfA2u  — PDF/A-2u (Unicode mapping required)
// PdfConformance.PdfA2a  — PDF/A-2a (full tagged PDF required)
```

When any PDF/A level is set the document:

- Writes a `%PDF-1.7` header (PDF/A-2 is defined against PDF 1.7).
- Embeds an sRGB ICC `OutputIntent`.
- Writes `pdfaid:part` / `pdfaid:conformance` in the XMP metadata stream.
- Sets `/MarkInfo /Marked true` in the catalog.
- Rejects `doc.Encrypt(...)` (encryption is forbidden by PDF/A).

Fonts must be embedded for PDF/A compliance.  Standard-14 fonts are **not**
embedded and will cause a preflight failure; use `doc.UseTrueTypeFont` for
conforming documents.

---

## 9. Deterministic output

By default the document `/ID` is derived from the content and the current
timestamp, so two otherwise identical saves produce different bytes.  For
golden-file tests or reproducible builds, pin both:

```csharp
var doc = new PdfDocument
{
    Timestamp  = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
    DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
};
doc.Info.Title = "Reproducible Report";
var page = doc.AddPage(PageSize.A4);
// ... draw content ...
doc.Save(stream);
```

With the same `Timestamp` and `DocumentId`, identical content produces
byte-identical output across any number of invocations.

When only `Timestamp` is pinned (and `DocumentId` is left null) the ID is
derived from content and the pinned timestamp, so it is still stable across
repeated calls with the same input.

---

## 10. Gotchas and common mistakes

**Call `canvas.Finish()` before `doc.Save()`.**  `Finish()` seals the content
stream.  Forgetting it means the page has no content in the output file.

**Call `doc.RegisterEmbeddedFontUsage(page, handle)` for every page that draws
with an embedded font.**  Only the registered pages get the font resource in
their `/Resources` dictionary.

**Dispose the document.**  `PdfDocument` implements `IDisposable`.  Use a
`using` statement or `using var` declaration to ensure proper cleanup.

**`PdfReal` rejects NaN and infinity.**  `new PdfReal(double.NaN)` throws
`ArgumentException`.  Validate computed values before converting to PDF.

**PDF/A and encryption are mutually exclusive.**  Attempting both triggers a
guard at save time.

**Standard-14 fonts are not embedded.**  For PDF/A or environments where
viewers may not have the built-in fonts installed, use
`doc.UseTrueTypeFont(fontData)` instead.

**`PdfCanvas` methods are not thread-safe.**  Build each page's canvas on a
single thread.

**`doc.UseFont` deduplicates.**  Calling `doc.UseFont(Standard14.Helvetica)`
twice returns the same `PdfFontResource` instance.

**`SetFont` vs `SetFontByName`.**  Use `SetFont(PdfFontResource, size)` for
Standard-14 fonts; use `SetFontByName(handle.ResourceName, size)` when drawing
with an embedded `EmbeddedFontHandle`.
