# VellumPdf Barcodes guide

This guide covers the optional `VellumPdf.Barcodes` package: eleven symbologies
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
  matrix cell), in points. Defaults to 2.0 for QR/Micro QR, PDF417, Data
  Matrix, and Aztec; 1.0 for Code 128, Code 39, EAN/UPC, and ITF-14.
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

### Kanji mode

```csharp
using VellumPdf.Barcodes;

var kanji = new QrCode("こんにちは世界") { ModuleSize = 4 };
doc.Add(kanji);
```

QR Kanji mode (ISO/IEC 18004 §7.4.6) packs eligible Shift-JIS X 0208 characters
at a fixed 13 bits each, denser than routing the same text through byte mode.
No API is needed to turn it on: the segmenter picks Kanji mode automatically,
alongside numeric, alphanumeric and byte mode, wherever it produces the
smallest symbol. Japanese content benefits most, since kana and common kanji
sit inside the two Shift-JIS blocks the mode covers.

Content that mixes a Kanji-eligible character with something representable
only in byte mode — an emoji, for example — falls back to encoding the whole
message in byte mode instead of interleaving Kanji and byte segments. The
reference decoder this package validates against misreads a Kanji segment
sitting next to a UTF-8 ECI-tagged byte segment, so the encoder avoids that
combination for decoder compatibility. A message that is entirely
Kanji-eligible is unaffected.

The Unicode-to-Shift-JIS lookup table is generated clean-room from the
Unicode Consortium's published SHIFTJIS.TXT mapping, filtered to code points
that round-trip through a CP932 decoder, so a scalar the table accepts always
decodes back to the same character.

### Structured Append

```csharp
using VellumPdf.Barcodes;

// Pre-split into parts, in reading order:
var parts = new[] { "Part one of the message.", "Part two of the message." };
var symbols = QrCode.StructuredAppend(parts);

// Or split a single string into a chosen number of roughly-equal parts:
var autoSplit = QrCode.StructuredAppend("A longer message spread across three symbols.", symbolCount: 3);

double y = 700;
foreach (var symbol in symbols)
{
    canvas.DrawBarcode(symbol, 50, y);
    y -= 150;
}
```

`QrCode.StructuredAppend` (ISO/IEC 18004 §8) splits a message across up to 16
linked QR Code symbols, each stamped with the set's shared sequence/parity
header so a reading application can reassemble them in order. Every returned
symbol is an ordinary `QrCode`; draw each one yourself through `DrawBarcode`
(or `doc.Add`) at whatever positions the layout calls for — nothing about
placement is automatic.

Two overloads are available: pass a pre-split `IReadOnlyList<string>` when
the split points matter (e.g. a word boundary), or pass a single string plus
a symbol count to split it automatically on Unicode scalar boundaries. Each
also has a form that adds an `ErrorCorrection` level and a `TextEncoding`
policy, applied to every symbol in the set: three arguments for the
pre-split overload, four for the auto-split overload (its extra
`symbolCount` argument comes first).

Micro QR has no Structured Append support.

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

### Compact (Truncated) format

```csharp
using VellumPdf.Barcodes;

var compact = new Pdf417Barcode("VellumPdf PDF417 example") { Compact = true };
doc.Add(compact);
```

Setting `Compact` (default `false`) renders the Compact (Truncated) format
from ISO/IEC 15438: the right row-indicator column is dropped and the
18-module stop pattern is replaced by a single dark module, narrowing the
symbol. The data codewords and their Reed-Solomon error correction are
unaffected, but the symbol loses the redundancy the dropped right-side
elements normally provide, so it tolerates less damage near its right edge.

### Macro PDF417

```csharp
using VellumPdf.Barcodes;

var parts = new[] { "Segment one.", "Segment two.", "Segment three." };
var segments = Pdf417Barcode.MacroSet(parts, fileId: 42);

// Or split a single string automatically:
var autoSplit = Pdf417Barcode.MacroSet("A file split across three PDF417 symbols.", symbolCount: 3, fileId: 42);

// Optional control-block fields, carried on the set's last symbol:
var withOptions = Pdf417Barcode.MacroSet(parts, fileId: 42, new MacroPdf417Options
{
    FileName = "report.txt",
    Timestamp = DateTimeOffset.UtcNow,
    FileSize = 4096,
});

double y = 700;
foreach (var segment in segments)
{
    canvas.DrawBarcode(segment, 50, y);
    y -= 150;
}
```

`Pdf417Barcode.MacroSet` (ISO/IEC 15438 Annex H) splits a payload across up
to 99999 linked PDF417 symbols sharing a `fileId` (0-899), each carrying a
Macro control block appended after its data codewords, so the symbol's own
error correction covers the control block too. `MacroPdf417Options` adds
optional fields — file name, segment count, timestamp, sender, addressee,
file size, checksum — carried on the set's last symbol only, matching the
convention decoders expect them under; `SegmentCount` defaults to the number
of parts when left unset. As with Structured Append, every returned symbol
is an ordinary `Pdf417Barcode` that the caller draws individually.

---

## Data Matrix

```csharp
using VellumPdf.Barcodes;

doc.Add(new DataMatrixBarcode("VellumPdf Data Matrix example") { ModuleSize = 3 });

// Raw bytes, carried verbatim in Base 256 mode:
doc.Add(new DataMatrixBarcode([0x00, 0x01, 0x02, 0xFF]));
```

ISO/IEC 16022 ECC 200: a square or rectangular matrix symbology with 24 square
sizes (10x10 to 144x144) and 6 rectangular sizes (8x18 to 16x48). Content is
compacted automatically across ASCII, C40, Text and Base 256 encodation.

- **`Shape`** (default `DataMatrixShape.Automatic`) resolves within the square
  family only, matching most generators' default and well-known worked
  examples. Set `DataMatrixShape.Rectangular` for a width/height-constrained
  label. Forcing one exact size among the 24/6 is deferred to a future release.
- **`Gs1`** (default `false`): FNC1 (codeword 232) in the first data-codeword
  position marks this as a GS1 Data Matrix symbol. Mirrors
  `Code128Barcode.Gs1`: any U+001D (group separator) elsewhere in the content
  also becomes FNC1, and the content itself is the raw digit/character stream
  (not the parenthesised-AI form) with separators already in place.
- `DataMatrixBarcode(byte[])` carries raw bytes verbatim in Base 256 mode.
  X12 and EDIFACT encodation are not supported: every ASCII-representable
  byte remains reachable through ASCII, C40 or Text, so this costs a little
  density on content those two modes would favour, never correctness.

```csharp
using VellumPdf.Barcodes;
using VellumPdf.Barcodes.Internal; // Gs1ElementString, for the raw payload form

// GS1 Data Matrix: FNC1 marks the symbol as GS1; embed real GS separators
// (not parentheses) between variable-length AI values, same convention as
// GS1-128.
var gs1 = new DataMatrixBarcode("0100012345678905") { Gs1 = true };
```

Placement (the diagonal "utah" sweep, its Annex F wrap-around at a symbol's
edges, and the four corner patterns some sizes substitute in) and
Reed-Solomon interleaving (round-robin block assignment for the 10 largest
square sizes, per ISO/IEC 16022 §5.3.2) are both verified exact against
ISO/IEC 16022's own published bit-placement figure and, for every size that
needs a corner substitution or multi-block interleaving, against a real
decode with zxing-cpp: all 30 sizes — every square size from 10x10 to
144x144 and all 6 rectangular sizes — round-trip through render, rasterize
and decode.

---

## Aztec Code

```csharp
using VellumPdf.Barcodes;

doc.Add(new AztecCode("VellumPdf Aztec Code example") { ModuleSize = 3 });

// Raw bytes, carried verbatim via binary shift:
doc.Add(new AztecCode([0x00, 0x01, 0x02, 0xFF]));
```

ISO/IEC 24778: a square matrix symbology with a central bullseye finder
pattern and no quiet zone requirement, in 4 compact sizes (1-4 layers,
15x15 to 27x27) and 32 full-range sizes (1-32 layers, 19x19 to 151x151).
Content is compacted automatically across five character modes
(upper-case, lower-case, mixed/control, punctuation, digit), with a binary
shift for bytes none of them reach directly.

- **`ErrorCorrectionPercent`** (default 23, ISO/IEC 24778's recommended
  level) is the percentage of the symbol's data-region capacity reserved
  for error correction, from 5 to 95. A higher value trades capacity for
  resilience to print defects and scan damage; the smallest symbol
  satisfying the requested percentage is chosen automatically.
- **`Format`** (default `AztecFormat.Automatic`) picks the smallest fitting
  compact size, falling back to full-range only when the content does not
  fit any compact size. Set `AztecFormat.Compact` or `AztecFormat.FullRange`
  to force one family. Forcing one exact layer count within a family is
  deferred to a future release.
- `AztecCode(byte[])` carries raw bytes verbatim via binary shift. GS1
  element strings are not supported by this release (unlike `QrCode.Gs1`
  and `DataMatrixBarcode.Gs1`).

Symbols round-trip through external readers across every compact and
full-range size. The data-layer placement follows ISO/IEC 24778 clauses
7.1-7.3 and the original public Aztec patent, US 5,591,956, which describes
its structural rules directly: the spiral of concentric layers, the 2-module
"domino" pairs, most-significant-bit-first ordering with the less significant
bit toward the finder, the reference grid at every 16th module, and the data
field displaced around that grid. ISO/IEC 24778's own coordinate figures
(Figures 4 and 5) are raster diagrams absent from the standard's freely
available preview pages, so the byte-exact geometry was pinned down against
module matrices generated by zxing-cpp — a decode/cross-check oracle standing
in for those figures, never copied — and the whole size range is exercised end
to end by render/rasterize/decode round-trips in the test suite.

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

Content must be Latin-1 (code points 0-255); a character above that throws
`ArgumentException`. Subset selection (A/B/C) and the mod-103 check character
are computed automatically to minimise the encoded length. GS1-128
(`Gs1 = true`) is stricter: a character above 127 throws `ArgumentException`
when the barcode is measured or drawn, since the GS1 General Specifications
disallow FNC4 in a GS1-128 symbol.

For GS1-128, the human-readable line wraps each Application Identifier in
parentheses, the `(01)...(17)...` form GS1 specifies for human-readable text.
Content flagged GS1 that is not a well-formed element string still encodes
into valid bars; its human-readable line falls back to the raw content instead.

### Extended Latin-1 (FNC4)

```csharp
using VellumPdf.Barcodes;

doc.Add(new Code128Barcode("café"));
```

Characters 128-255 are carried with FNC4 (ISO/IEC 15417): a lone extended
character is reached with a single FNC4, which shifts just the character
right after it, and a run of two or more latches FNC4 with a doubled FNC4
until a second doubled FNC4 switches it back off. Subset and shift/latch
selection happen automatically: nothing about `Code128Barcode`'s API changes
to use it.

GS1-128 (`Gs1 = true`) rejects any character above 127; FNC4 is not permitted
in a GS1-128 symbol. Not every scanner supports FNC4, so where broad
compatibility matters more than round-tripping the full Latin-1 range, keep
content within 0-127.

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
yourself. `QrCode.GetMatrix()`, `MicroQrCode.GetMatrix()`,
`Pdf417Barcode.GetMatrix()`, `DataMatrixBarcode.GetMatrix()`, and
`AztecCode.GetMatrix()` expose the raw `BarcodeMatrix` (a bit-packed
dark/light grid, `(0, 0)` at the top-left) for callers that want to inspect or
render the modules independently of `BarcodePainter`.

---

## Trademark note

QR Code is a registered trademark of DENSO WAVE INCORPORATED.
