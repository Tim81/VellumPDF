# VellumPdf Barcodes guide

This guide covers the optional `VellumPdf.Barcodes` package: seven symbologies
rendered as vector rectangles over `VellumPdf.Kernel.PdfCanvas` and
`VellumPdf.Layout.Document`, described in [`docs/architecture.md`](architecture.md).
Every symbol is drawn as filled rectangles (never a raster image), so it stays
crisp at any zoom level and prints cleanly.

```shell
dotnet add package VellumPdf.Barcodes
```

## Two API tiers

Every symbology is a plain data object (a `Barcode`, `QrCode`, `EanBarcode`,
and so on) that describes what to encode and how to size and colour it. You
place it on a page through one of two extension methods:

- **Flow API** (`document.Add(barcode)`): adds the barcode as an element in a
  `VellumPdf.Layout.Document`, alongside paragraphs, tables, and images.
  Sizing, pagination, alignment, and tagging are handled for you.
- **Low-level canvas API** (`canvas.DrawBarcode(barcode, x, y, textFont)`):
  draws directly into a `PdfCanvas` content stream, with the footprint's
  lower-left corner (quiet zone included) at `(x, y)` in PDF user space.

```csharp
using VellumPdf.Barcodes;
using VellumPdf.Layout;

using var doc = new Document();
doc.Add(new QrCode("https://example.com") { TargetWidth = 120 });
doc.Save("qr.pdf");
```

```csharp
using VellumPdf.Barcodes;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

using var doc = new PdfDocument();
var page = doc.AddPage(PageSize.A4);
var font = doc.UseFont(Standard14.Helvetica);
var canvas = new PdfCanvas(page);

canvas.DrawBarcode(new EanBarcode(EanSymbology.Ean13, "400638133393"), 72, 700, font);
canvas.Finish();
```

`textFont` is only required for a 1D symbology with `ShowText` enabled (the
default); 2D symbologies and 1D symbologies drawn with `ShowText = false`
ignore it.

---

## Sizing: `ModuleSize` vs `TargetWidth`

Every barcode has two mutually exclusive sizing options:

- **`ModuleSize`**: the width of one module (the narrowest bar/space or
  matrix cell), in points. Defaults to 2.0 for QR/Micro QR and PDF417, 1.0 for
  Code 128, Code 39, EAN/UPC, and ITF-14.
- **`TargetWidth`**: the desired overall rendered width, in points; the
  module size is derived from it.

Setting both throws `ArgumentException` when the barcode is measured or drawn.

```csharp
new QrCode("https://example.com") { ModuleSize = 3 };   // fixed module width
new QrCode("https://example.com") { TargetWidth = 120 }; // fixed overall width
```

**Quiet zones.** `IncludeQuietZone` (default `true`) reserves the specification's
required clear margin around the symbol as part of its measured and drawn
footprint. Leave it on unless the surrounding layout already guarantees an
equivalent margin; turning it off shrinks the footprint but risks unreliable
scanning.

**Colour.** `Foreground` (default black) colours the dark bars/modules;
`Background` (default `null`, transparent) optionally fills behind the symbol.

---

## QR Code

```csharp
using VellumPdf.Barcodes;

var qr = new QrCode("https://example.com/vellumpdf")
{
    ErrorCorrection = QrErrorCorrection.Q,
    ModuleSize = 3,
};
doc.Add(qr);

// Or inspect the raw module grid yourself:
BarcodeMatrix matrix = qr.GetMatrix();
bool topLeftFinderIsDark = matrix.IsDark(0, 0);
```

`QrCode` picks the smallest version (1-40) and best data mask automatically;
override either with `Version` (1-40) and `Mask` (0-7). `ErrorCorrection`
defaults to `M` (`L`/`M`/`Q`/`H` available).

### Charset and ECI policy

`QrCode(string)` segments text across numeric, alphanumeric, and byte mode
automatically. `TextEncoding` controls how byte-mode content is encoded:

| `TextEncoding` | Behaviour |
| --- | --- |
| `Auto` (default) | ISO/IEC 8859-1 (Latin-1) with no ECI header when the text is fully representable in Latin-1; otherwise UTF-8 with an ECI 26 header. |
| `Latin1` | Forces Latin-1, no ECI. Throws `FormatException` for non-Latin-1 text. |
| `Utf8` | Forces UTF-8 with no ECI header (for legacy scanners that assume UTF-8 by convention). |
| `Utf8Eci` | Forces UTF-8 with an explicit ECI 26 header. |

`Auto` matches how widely-used decoders behave: an ECI header is honoured
when present, and byte-mode content without one is guessed (typically as
ISO-8859-1 or UTF-8). Both halves of `Auto` round-trip correctly as a result.

`QrCode(byte[])` bypasses this policy entirely: the bytes are carried
verbatim in byte mode, one codeword per byte, ignoring `TextEncoding`.

### GS1 mode

```csharp
using VellumPdf.Barcodes;

// GS1 element string: FNC1 in first position marks this as a GS1 symbol.
// Field separators between variable-length AI values are handled automatically.
var gs1Qr = new QrCode("(01)09501101020917(17)261231(10)ABC123")
{
    Gs1 = QrGs1Mode.ElementString,
};

// GS1 Digital Link: the same data, rewritten as a resolvable URI and encoded
// as a plain QR Code (no FNC1, no special mode indicator).
var digitalLinkQr = new QrCode("(01)09501101020917(17)261231(10)ABC123")
{
    Gs1 = QrGs1Mode.DigitalLink,
};
```

`Gs1` (default `QrGs1Mode.None`) accepts either the raw digit/character stream
(field separators as U+001D) or the parenthesised-AI form shown above; both
normalize to the same encoded content. Only the string constructor supports
it — `QrCode(byte[])` with `Gs1` set throws `ArgumentException`.

- **`ElementString`** parses the content as a GS1 element string and writes
  the FNC1-in-first-position mode indicator (ISO/IEC 18004 §7.4.8.2) ahead of
  the data, which is how a reading application recognizes GS1 content and
  splits it back into Application Identifiers. Field separators required
  between variable-length AI values are inserted automatically; a caller
  never has to place them.
- **`DigitalLink`** rewrites the content as its canonical
  `https://id.gs1.org/...` GS1 Digital Link URI and encodes that URI as an
  ordinary QR Code, with no FNC1 indicator — the symbol carries a URL that
  can be resolved by any web-connected reader, not only a GS1-aware scanner.
- Both modes throw `FormatException` for content that is not well-formed GS1
  element-string data (unknown Application Identifier, wrong fixed-length
  value, and so on).
- 2D symbologies draw no visible text, so the human-readable (parenthesised
  AI) form appears only in the `Figure`'s alternate text, not on the page.

---

## Micro QR

```csharp
using VellumPdf.Barcodes;

doc.Add(new MicroQrCode("12345") { ErrorCorrection = QrErrorCorrection.M });
```

A compact QR variant (versions M1-M4) with a single finder pattern instead of
three, for short messages where a full QR Code's three finders would waste
space. Restrictions:

- **No ECI support.** Content must be representable in ISO/IEC 8859-1
  (Latin-1); anything else throws `FormatException`.
- **Per-version mode limits.** M1 is numeric-only and always provides error
  *detection* rather than correction, regardless of `ErrorCorrection`. M2 adds
  alphanumeric mode; M3 and M4 add byte mode too. M2/M3 support error
  correction levels `L`/`M`; M4 adds level `Q`. Level `H` is never available
  in Micro QR.
- The smallest version that fits the content and requested level is chosen
  automatically unless `Version` (1-4, meaning M1-M4) is set.

---

## PDF417

```csharp
using VellumPdf.Barcodes;

var pdf417 = new Pdf417Barcode("VellumPdf PDF417 example")
{
    PreferredAspectRatio = 3.0,
    RowHeight = 3.0,
};
doc.Add(pdf417);
```

A stacked linear barcode with 3-90 rows of 1-30 data columns. Content is
compacted automatically across text, byte, and numeric sub-modes.

- **Dimensioning.** Leave `Columns` and `Rows` unset to let the symbol solve
  both from `PreferredAspectRatio` (default 3.0, width:height); set either (or
  both) to force a specific layout.
- **`RowHeight`** is in modules, not points: it scales with `ModuleSize`/
  `TargetWidth` like everything else. The specification's recommended minimum
  is 3.0 (the default); taller rows make each row easier to scan at the cost
  of overall height.
- **`ErrorCorrectionLevel`** (0-8; each level doubles the error-correction
  codewords) defaults to `-1`, which follows the specification's recommended
  level for the content's size. Set it explicitly for a denser or more
  error-resistant symbol.
- `Pdf417Barcode(byte[])` carries raw bytes verbatim in byte compaction mode.
  Macro PDF417 (splitting content across several symbols) is not supported.

---

## Code 128 and GS1-128

```csharp
using VellumPdf.Barcodes;

// Plain Code 128: subset A/B/C chosen automatically.
doc.Add(new Code128Barcode("VELLUM-1234"));

// GS1-128: FNC1 immediately after the start code marks this as a GS1-128
// symbol; any U+001D (group separator) in the content becomes an embedded FNC1.
doc.Add(new Code128Barcode("0100012345678905") { Gs1 = true });
```

Content must be ASCII (code points 0-127); a non-ASCII character throws
`ArgumentException`. Subset selection (A/B/C) and the mod-103 check character
are computed automatically to minimise the encoded length. Extended Latin-1
(code points 128-255, which Code 128 can carry only through FNC4) is not
supported: FNC4 is handled inconsistently by scanners and is disallowed in
GS1-128.

For GS1-128, the human-readable line prints the encoded data as a single run.
It does not yet wrap each Application Identifier in parentheses (the
`(01)...(17)...` form GS1 specifies for human-readable text); this affects the
printed caption only, not the scanned bars. Parenthesised HRI is tracked in the
barcode completeness backlog.

---

## Code 39

```csharp
using VellumPdf.Barcodes;

var code39 = new Code39Barcode("VELLUM-1234")
{
    CheckDigit = true,
    WideNarrowRatio = 3.0,
};
doc.Add(code39);
```

ISO/IEC 16388, the self-checking symbology long used in logistics, defense
(LOGMARS) and healthcare (HIBC) item marking. By default, content must be
drawn from the 43-character standard set: the digits, the uppercase letters,
space, and `-.$/+%`; any other character throws `ArgumentException` when the
barcode is measured or drawn.

- **`CheckDigit`** (default `false`) appends a modulo-43 check character
  before the stop character. It is computed, not validated — `Code39Barcode`
  has no equivalent of `EanBarcode`'s "supply the check digit yourself" mode.
- **`FullAscii`** (default `false`), Extended Code 39, accepts any ASCII
  character (0-127); each is mapped to its one- or two-character
  representation (AIM USS-39 precedence codes `$`, `/`, `%` and `+`). A
  character above U+007F throws `ArgumentException`. The human-readable text
  always shows the original content, not the expanded shift-pair form.
- **`WideNarrowRatio`** (default 2.5) must fall within ISO/IEC 16388's
  2.0-3.0 range.

Not every Code 39 reader is configured to validate the check character or
decode Extended mode — both are sender/receiver agreements layered on top of
the base symbology, not something a scanner detects automatically.

---

## EAN-13, EAN-8, UPC-A, UPC-E, and add-ons

```csharp
using VellumPdf.Barcodes;

// The check digit is computed when 12 digits are supplied, or validated when 13 are.
var ean13 = new EanBarcode(EanSymbology.Ean13, "400638133393");
doc.Add(ean13);

// An EAN-5 (or EAN-2) add-on, printed above the main symbol.
doc.Add(new EanBarcode(EanSymbology.Ean13, "400638133393") { AddOn = "12345" });

var ean8 = new EanBarcode(EanSymbology.Ean8, "1234567");
var upcA = new EanBarcode(EanSymbology.UpcA, "03600029145");
```

`EanBarcode.Digits` returns the canonical digit string including the check
digit (13 for EAN-13, 8 for EAN-8, 12 for UPC-A, 8 for UPC-E). Read it back
when you need the exact value that was encoded, e.g. for a caption elsewhere
in the document.

An `AddOn` (2 or 5 digits, per GS1 General Specifications §5.2) is drawn above
the main symbol with its own 9-module gap and its own, shorter HRI text band.

UPC-A is drawn as an EAN-13 symbol with an implicit leading `0`: that is the
physical relationship between the two symbologies, and how UPC-A scanners
have always interpreted the mark.

### UPC-E

```csharp
// The 6 compressed digits; number system 0 is assumed.
var upcE = new EanBarcode(EanSymbology.UpcE, "654321");
doc.Add(upcE);

// Number system 1, or an existing UPC-A number that compresses to UPC-E,
// also work:
var withSystem = new EanBarcode(EanSymbology.UpcE, "1654321");
var fromUpcA = new EanBarcode(EanSymbology.UpcE, "065100004327");
```

UPC-E is the zero-suppressed 6-digit form of a UPC-A, valid only for number
system 0 or 1 with a manufacturer/product code that has a qualifying pattern
of trailing/leading zeros. `EanBarcode.Digits` accepts four input shapes:

- **6 digits** — the compressed data alone; number system 0 is assumed.
- **7 digits** — a leading number-system digit (0 or 1) plus the 6 compressed digits.
- **8 digits** — as 7, plus a trailing check digit, which is validated.
- **11 or 12 digits** — a UPC-A number, compressed to UPC-E if a qualifying
  zero-suppression pattern exists.

UPC-E carries no check digit of its own: whichever form is supplied, the
check digit is always derived from the expanded 12-digit UPC-A. A value that
cannot be represented as UPC-E — the wrong number system, or a
manufacturer/product code with no suppressible zero pattern — throws
`FormatException`.

---

## ITF-14

```csharp
using VellumPdf.Barcodes;

// 13 digits: the check digit is computed. 14 digits: it is validated.
doc.Add(new Itf14Barcode("1234567890123")
{
    BearerBars = ItfBearerBarStyle.Frame,
    WideNarrowRatio = 2.5,
});
```

Interleaved 2-of-5, typically used on cartons and pallets (GS1 General
Specifications §5.3). `BearerBars` (`Frame` by default, or `Horizontal`/`None`)
adds the thick border lines that protect the symbol from print-plate damage;
they contribute to the barcode's measured footprint. Their thickness is a
proportional two modules; printing directly on corrugated board (plate or
flexo), where GS1 calls for a fixed bearer thickness rather than a proportional
one, is outside the scope of a general encoder. `WideNarrowRatio` (default 2.5)
must fall within GS1's 2.25-3.0 range.

---

## Human-readable text (1D symbologies)

Every `Barcode1D` (Code 128, Code 39, EAN/UPC, ITF-14) can print its encoded
digits or text below the bars:

| Property | Default | Notes |
| --- | --- | --- |
| `ShowText` | `true` | Set `false` to omit the HRI text entirely. |
| `TextFont` | `Standard14.Helvetica` | Any `Standard14` face. |
| `TextSize` | `0` (auto) | `0` derives a legible size from `BarHeight`; set explicitly to override. |

The low-level `DrawBarcode` extension requires a `textFont` argument whenever
`ShowText` is `true` on a `Barcode1D` — omitting it throws
`InvalidOperationException`. The flow API resolves the font for you.

EAN/UPC add-on text is drawn in its own band **above** the main symbol, matching
the layout used in GS1's own published examples; the main symbol's HRI text
stays below the bars as usual.

---

## Tagged PDF and accessibility

- **Flow API.** `document.Add(barcode)` tags a data-bearing barcode as a
  `/Figure` structure element carrying alternate text (`AltText`, or a
  symbology-specific default composed from the encoded content when unset).
  This is required: PDF/UA rule 7.3-1 fails a `Figure` without `/Alt`. Set
  `Decorative = true` to mark it as an artifact instead, omitted from the
  structure tree entirely. Use this only when the encoded data is already
  available as accessible text nearby (e.g. a caption printing the same
  digits).
- **Low-level canvas API.** `DrawBarcode` draws unmarked content; in a tagged
  document, bracket the call yourself with
  `canvas.BeginArtifactMarkedContent()` / `canvas.EndMarkedContent()` for a
  decorative barcode, or `canvas.BeginMarkedContent("Figure")` plus a
  registered `PdfStructElem` carrying `AltText` otherwise.

---

## Low-level details

`Barcode.Measure()` returns the symbol's full footprint (`BarcodeSize`,
including quiet zones, HRI text, and bearer bars), validating content and
sizing options. Call it directly if you need to lay out surrounding content
yourself. `QrCode.GetMatrix()`, `MicroQrCode.GetMatrix()`, and
`Pdf417Barcode.GetMatrix()` expose the raw `BarcodeMatrix` (a bit-packed
dark/light grid, `(0, 0)` at the top-left) for callers that want to inspect or
render the modules independently of `BarcodePainter`.

---

## Trademark note

QR Code is a registered trademark of DENSO WAVE INCORPORATED.
