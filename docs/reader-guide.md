# VellumPdf Reader guide

This guide covers `VellumPdf.Reader` — the package that opens an existing PDF rather than
building a new one. See [`docs/architecture.md`](architecture.md) for where it sits in the
layering, and [`docs/layout-guide.md`](layout-guide.md) for the document-generation side.

> **Preview.** The public surface below is Unshipped and can still move before it graduates —
> see the capability table at the end of this guide for what that graduation depends on.

## When to use the Reader API

Reach for `VellumPdf.Reader` when you have a PDF someone else produced and need its structure:
verifying a signature, checking whether a document is encrypted, or handing a decrypted copy to
something else in a pipeline. It does not build or edit PDFs — that is `VellumPdf.Layout` and
`VellumPdf.Kernel` (generation) and, from v3.0, `VellumPdf.Editing` (round-trip edits to an
existing file). The reader parses on top of a bounds-checked lexer and object parser: a malformed
or hostile file is expected input, not a bug report.

---

## 1. Opening a document

```csharp
using VellumPdf.Reader;

using var reader = PdfReader.Open(File.OpenRead("input.pdf"));

VellumPdf.Core.PdfDictionary catalog = reader.Catalog;
```

`PdfReader.Open` takes a `Stream` or a `byte[]`, each with an overload that also accepts
`PdfReaderOptions`. `PdfDocumentReader` implements `IDisposable`; dispose it (or wrap it in
`using`) once you are done reading.

An encrypted document needs a password up front, supplied through options:

```csharp
using var reader = PdfReader.Open(
    File.OpenRead("encrypted.pdf"),
    new PdfReaderOptions { Password = "open-me" });
```

Leave `Password` null for a document whose empty user password is enough — most encrypted PDFs
in the wild restrict permissions through the owner password while leaving the user password
empty, so `null` opens them without prompting for anything. Supplying the wrong password throws
`PdfPasswordException`, which is distinct from `NotSupportedException`: the reader understood the
document, it just wasn't given the right credentials.

The Standard security handler is supported at `/V` 1, 2, 4 and 5 and `/R` 2 through 6 — RC4-40
through RC4-128, AES-128, and AES-256.

---

## 2. `PdfReaderOptions`

Every setting on `Open` goes through one options object rather than a growing parameter list:

```csharp
var options = new PdfReaderOptions
{
    Password = "open-me",
    AllowReconstruction = true,
    MaxDecodedStreamBytes = 64 * 1024 * 1024,
    ReconstructionBudgetMultiplier = 4,
    MaxDiagnostics = 200,
};

using var reader = PdfReader.Open(File.OpenRead("input.pdf"), options);
```

**`AllowReconstruction`** (default `false`). When a document's `startxref` is missing or
unusable, the reader can rebuild the cross-reference table by scanning the file for object
headers instead — the recovery mechanism ISO 32000-2 Annex C.4 describes as informative, not a
required behaviour. It's off by default because a scan-and-rebuild is best-effort over structure
the file's own cross-reference table has already failed to describe correctly, and can land on
the wrong document catalog for a layout it doesn't fully understand; a caller opts into that
trade-off rather than receiving it silently on every malformed file. A document opened this way
reports it through `reader.WasReconstructed` and refuses a later incremental update: there's no
real `startxref` chain left for `/Prev` to extend, and a recovered trailer's `/ID` isn't reliable
enough to carry into a new revision. Reconstruction also refuses outright the instant it finds
any sign the document is encrypted, rather than guessing at a key.

**`MaxDecodedStreamBytes`**, **`ReconstructionBudgetMultiplier`**, and **`MaxDiagnostics`** are all
**tighten-only**. None is a spec requirement — ISO 32000-2 Annex C.1 notes that "a particular PDF
processor running on a particular device and in a particular operating environment will always have
practical limits", and Annex C.3 adds that available memory is "often much less in mobile devices
than desktop computers." The defaults (512 MiB decoded-stream ceiling, an ×8 multiplier on
reconstruction's `max(1 MiB, N × file length)` work budget, a 1000-entry diagnostics cap) are this
library's own choice for a desktop host, not something Annex C mandates. A caller on a more
constrained device, or hardening against a decompression bomb, a file engineered to burn CPU across
many decoy candidates, or a document that would otherwise report the same recoverable condition on
a huge number of objects, can lower any of the three. Raising any of them above its default throws
`ArgumentOutOfRangeException` at `Open` time: nothing above the shipped defaults has been exercised
as a safe ceiling, so these options can only make the reader stricter than it already is, never
looser.

---

## 3. What you can read

```csharp
PdfEncryptionInfo? encryption = reader.Encryption;   // null for an unencrypted document
if (encryption is not null)
{
    Console.WriteLine($"/V {encryption.V} /R {encryption.R}, {encryption.KeyLengthBits}-bit");
    Console.WriteLine($"Opened as {(encryption.IsOwnerAccess ? "owner" : "user")}");
}

foreach (PdfSignature signature in reader.Signatures)
    Console.WriteLine($"{signature.SubFilter} signed {signature.SigningTime}");

bool wasRebuilt = reader.WasReconstructed;
```

`Encryption` exposes the Standard security handler's settings as recorded in `/Encrypt` — cipher,
key length, permission flags, whether the opened password authenticated as owner or user — with
no key material: not the file encryption key, not `/O`/`/U`/`/OE`/`/UE`. `Signatures` reads each
signature's `/ByteRange`, `/Contents`, `/M` (as `SigningTime`), and `/SubFilter`; this is signature
*reading*, not *verification* — checking integrity, coverage, or a certificate chain is future
work (see the capability table below).

Page content (text runs, images) is not on the public surface yet; extracting it is the next
reader milestone. Locating the pages themselves is:

### Pages

```csharp
int count = reader.PageCount;                  // never trusts a node's own /Count
foreach (PdfReadPage page in reader.Pages)
    Console.WriteLine($"page {page.Index}: {page.MediaBox.Width}x{page.MediaBox.Height}, rotate {page.Rotate}");

PdfReadPage first = reader.GetPage(0);          // throws ArgumentOutOfRangeException outside [0, PageCount)
```

`PageCount`/`Pages`/`GetPage` walk the page tree (ISO 32000-2 §7.7.3) from `/Root` → `/Pages` →
`/Kids` on first access and cache the result (never in `PdfReader.Open` itself). The walk counts
what `/Kids` actually contains, not any node's `/Count`. Table 30 puts that obligation on the
writer, not the reader: the `/Kids` array and its descendants are what "definitively determines
the number of descendant pages", and real producers disagree with their own `/Count` often enough
that trusting it would misreport ordinary files, not just adversarial ones. A dictionary reached
through `/Kids` is classified by its own `/Type` first: `/Type /Pages` is always a node, whether
`/Type` names it explicitly or a usable `/Kids` array decides it structurally, and either way a
stray `/Contents` alongside it is reported as `PageTreeNodeMalformed`: that key is a Table 31
page-object entry with no row in Table 30's node listing, and §7.7.3.4 carries only inheritable
attributes down to descendants, so on a node it describes nothing at all (a meaningless entry, not
a conformance violation, but flagged anyway). `/Type /Page` is always a leaf; a stray `/Kids`
alongside it is both ignored and reported as `PageTreeNodeMalformed` the same way. `/Type
/Template` (Table 31's own Type row admits it as the other legal page `/Type`, §7.7.3.3) and
anything naming neither is skipped and reported as `PageTreeNodeMalformed` too: that same Table
31, in its Parent row, exempts Template from carrying one at all ("Objects of Type Template shall
have no Parent key"), a rule §12.7.7 repeats, so a Template page has no parent and can never
legitimately sit in any node's `/Kids`. A dictionary with no `/Type` at all falls back to whether
it has a `/Kids` array for that classification, silently either way, since plenty of real
producers omit `/Type /Page` on a genuine leaf. Absent, an explicit null, and a reference (or a
chain of references, ISO 32000-2 §7.3.10's 2020 NOTE) that resolves to either of those are the
same thing to the walker for a dictionary ENTRY throughout all of this: §7.3.9 makes a null value
equivalent to an absent key, and extends that to a reference resolving to nothing. This
equivalence is scoped to entries, not `/Kids` ARRAY ELEMENTS: `[3 0 R null]` or `[3 0 R 9 0 R]`
with object 9 undefined still reports `PageTreeKidNotDictionary` for that element rather than
silently skipping it, since Table 30's own `/Kids` row governs what counts as a valid child (a
page object or another page-tree node) separately from §7.3.9's null-equivalence rule.

The root (`/Root/Pages`) goes through the same `/Type`-and-structure classification: a root that
is itself a leaf, or that names something other than `/Type /Pages` (the catalog dictionary
reused as its own `/Pages` entry, say), reports `PageTreeMissing` instead of being walked, since
ISO 32000-2 §7.7.2 Table 29 requires `/Root/Pages` to be the root of the page tree and §7.7.3.2
defines what counts as one. Only a root that FAILS that classification skips its own
stray-`/Contents` check; a root that clears it always reaches that check, whether or not it also
has usable `/Kids` of its own, so a root with neither can report `PageTreeNodeMalformed` (the
stray `/Contents`) and `PageTreeMissing` (the unusable `/Kids`) together, not one instead of the
other. Any node's `/Kids` resolving to a present but empty array, root included, contributes zero
pages for that subtree with no diagnostic at all: §7.7.3 does not require a document to have at
least one page, so an empty tree is a valid zero-page document, not a defect.

Each `PdfReadPage` exposes `MediaBox`, `CropBox`, and `Rotate` already resolved through the
inheritance chain (§7.7.3.4) and normalised: corners ordered low-to-high, rotation folded to one
of 0/90/180/270. A malformed value anywhere in the chain is skipped in favour of the nearest
ancestor that supplies a valid one, each skip reported as `PageAttributeInvalid`; only once nothing
in the chain resolves at all does `MediaBox` fall back to US Letter (612 × 792 points, this
reader's own convention, since the specification names no default), `CropBox` to the page's own
`MediaBox`, and `Rotate` to 0. `CropBox` is additionally intersected with `MediaBox` per §14.11.2.1
once resolved: a crop region extending past the media box is clipped to it, not exposed as
written, and a crop box that collapses to zero width or height at the intersection is kept as that
zero-width (or zero-height) intersection rather than replaced, as long as it still touches the
media box (ISO 32000-2 §7.9.5's NOTE records that rectangles can have zero width or height); a
`CropBox` that shares no overlap with `MediaBox` at all (§14.11.2.1's intersection would be
empty) falls back to `MediaBox` with its own `PageAttributeInvalid` report. A merely absent,
optional `CropBox` or `Rotate` stays silent: that is the spec's own default, not a problem. A
page tree the walk cannot use at all reports `PageTreeMissing` and leaves `PageCount` at 0
rather than throwing; a node whose own `/Type` or `/Kids` shape is wrong is skipped, treated as a
leaf, or still walked as a node, per the rules described above, and reports
`PageTreeNodeMalformed` either way; a cycle (`PageTreeCycle`), a nesting depth past 256
(`PageTreeDepthExceeded`), more than 100,000 leaves (`PageTreeLeafLimitExceeded`), or more than
1,000,000 `/Kids` elements examined in total (`PageTreeNodeLimitExceeded`) each report their own
code and return whatever pages were found up to that point. `PageTreeCycle` also covers a repeat
reached through different aliases, not just a literal repeated reference: two `/Kids` elements
naming different objects that both resolve, one or more indirect references later, to the same
page or node still count as one repeated object, reported once as `PageTreeCycle` naming the
object they both resolve to, with the page (or node) itself listed once, under its own object
number. See `PdfReaderDiagnosticCode`'s `2xx` block for the full list.

### Diagnostics

```csharp
foreach (PdfReaderDiagnostic d in reader.Diagnostics)
    Console.WriteLine(d); // "{Severity} {Code} page {p} obj {n} {g}: {Message}"

var options = new PdfReaderOptions { MaxDiagnostics = 200 };
```

`Diagnostics` (#385) lists what the reader recovered from instead of aborting on: a
cross-reference table it had to rebuild, a filter chain entry that didn't resolve the way it
declared itself, a TIFF predictor applied at a bit depth this decoder doesn't undo correctly. Each
entry carries a `Code`, a `Severity` (`Info`/`Warning`/`Error`), a human-readable `Message`, and,
where the condition concerns one, an `ObjectNumber` and `Generation`. `PageIndex` is also on every
entry, populated by the page-tree walk's own `PageAttributeInvalid` reports against a page's own
dictionary (see Pages above); a malformed attribute found on an ancestor node instead reports the
same code once against that node with `PageIndex` null, so filtering diagnostics by page index
alone misses it. A caller must also look at reports with a null page index; every other code
either concerns no specific page or is reported before a page index is known.
`MaxDiagnostics` (default 1000) is tighten-only, matching `MaxDecodedStreamBytes` and
`ReconstructionBudgetMultiplier` above: past the cap, a single `DiagnosticsSuppressed` entry says
how many further reports were dropped rather than growing the list without bound. Exempt from
that cap: the `2xx` page-tree codes that say the page list found so far is incomplete
(`PageTreeLeafLimitExceeded`, `PageTreeNodeLimitExceeded`, and the first `PageTreeDepthExceeded`
of a walk), each retained past `MaxDiagnostics` rather than risk going silent on exactly the
input the cap exists to bound (see Pages above and `PdfReaderOptions.MaxDiagnostics`).

`Diagnostics` is a live view: streams decode lazily, so the list can still grow after `Open`
returns, as later calls resolve more of the document. Unlike the reader's other collections, which
never change after construction, this one can grow while you enumerate it — and doing so
invalidates the enumerator, throwing `InvalidOperationException` mid-loop. Call
`reader.Diagnostics.ToList()` first if you need a stable snapshot to hold onto or hand to another
thread.

---

## 4. Writing a decrypted copy

`PdfDocumentReader.SaveDecrypted` writes a complete, single-revision copy of the open document
with `/Encrypt` removed and every string and stream in plaintext — this library's equivalent of
`qpdf --decrypt`.

```csharp
using var reader = PdfReader.Open(
    File.OpenRead("encrypted.pdf"),
    new PdfReaderOptions { Password = "open-me" });

using var output = new FileStream("decrypted.pdf", FileMode.Create);
reader.SaveDecrypted(output);
```

An async twin exists for the same call: `SaveDecryptedAsync(destination)` and
`SaveDecryptedAsync(destination, options, cancellationToken)`. Serialization is CPU-bound, so it
runs on a thread-pool thread against an in-memory buffer, then copies that buffer to
`destination` with an asynchronous write; cancellation is honored before serialization starts and
during that final copy, not partway through serialization itself.

**What it does, structurally.** The output is a single classic cross-reference table with no
`/Prev` — `/ObjStm` containers, cross-reference streams, and a linearized input's parameter
dictionary are all dissolved into that one table. Object numbers and generations are preserved
from the input; a compressed `/ObjStm` member is re-emitted top-level at generation 0, since ISO
32000-2 §7.5.7 fixes such a member's generation regardless of what its container's own number
carries. An already-unencrypted document is accepted too — the method's postcondition already
holds for one, so the output degenerates to a normalized rewrite with every incremental update
collapsed. A document opened with `AllowReconstruction` is also accepted, unlike an incremental
update, which refuses one outright: a full rewrite doesn't depend on the base file's own byte
layout the way an incremental update does, since there's no `/Prev` chain to extend and no
`startxref` to trust.

**Signatures.** Re-serializing the object graph invalidates every digital signature by
construction: a fresh `/ByteRange` no longer names the region the original signature was computed
over, so a rewritten signature would verify as "document modified since signing" — the same
verdict a verifier gives for genuine tampering, with no way to tell the two apart from the output
alone. `SaveDecrypted` refuses by default when the source document has any signature:

```csharp
try
{
    reader.SaveDecrypted(output);
}
catch (InvalidOperationException)
{
    // Accept that every signature will read as invalidated.
    reader.SaveDecrypted(output, new PdfSaveDecryptedOptions
    {
        AllowInvalidatingSignatures = true,
    });
}
```

Setting `AllowInvalidatingSignatures` accepts that outcome; it does not fix it, and nothing can —
a signature's own signed bytes cannot survive a full rewrite by construction. Even with the
opt-in set, a signature's `/Contents` is copied verbatim rather than re-derived.

**What is deliberately not addressed.** A `/Perms`-restricted document (view-only, no printing,
and so on) is rewritten anyway: a caller who can already open the document already holds every
byte the owner does, so refusing here would protect nothing while breaking the common case of a
merely permission-restricted file.

**Cost.** The whole object graph is force-resolved and held in memory, and the output is built in
an internal buffer before anything reaches `destination` — peak memory runs roughly three times
the input file's size. A failure during serialization leaves `destination` completely untouched;
the final copy from that buffer to `destination` is not covered by the same guarantee, so a
failure or cancellation partway through it can leave a genuinely-plaintext, truncated prefix on
the stream. Writing to a temporary file and renaming it into place only after the call returns
gets an all-or-nothing result on disk.

---

## 5. Reader capability table

Also published in [the package README](https://github.com/Tim81/VellumPDF/blob/main/src/VellumPdf.Reader/README.md#capabilities);
a guard test keeps the two copies byte-identical.

<!-- capability-table:reader:start -->
| Capability | Status | Target milestone / ISO reference |
| --- | --- | --- |
| Classic cross-reference tables | ✅ Supported | ISO 32000-2 §7.5.4 |
| Cross-reference streams, object streams | ✅ Supported | ISO 32000-2 §7.5.7, §7.5.8 |
| Hybrid-reference files (`/XRefStm`) | ✅ Supported | ISO 32000-2 §7.5.8.4 (#206) |
| Cross-reference reconstruction for damaged files | ✅ Supported (opt-in) | ISO 32000-2 Annex C.4, informative (#184) |
| Configurable, tighten-only resource limits | ✅ Supported | ISO 32000-2 Annex C.1/C.3, informative (#376) |
| Decryption: Standard handler, `/V` 1/2/4/5, `/R` 2–6, RC4-40–128, AES-128/256 | ✅ Supported | ISO 32000-2 §7.6 |
| Reading the document catalog | ✅ Supported | ISO 32000-2 §7.7.2 |
| Page tree walk and page access (`PageCount`, `Pages`, `GetPage`) with inherited `/MediaBox`, `/CropBox`, `/Rotate` | ✅ Supported | ISO 32000-2 §7.7.3 (#98) |
| Reading digital signature metadata (`/ByteRange`, `/Contents`, `/M`, `/SubFilter`) | ✅ Supported (read only, not verified) | ISO 32000-2 §12.8 |
| Writing a decrypted copy (`SaveDecrypted`/`SaveDecryptedAsync`) | ✅ Supported | #186 |
| Lexer/parser hardened against malformed input (property-based fuzzing, round-trip oracle) | ✅ Supported | #99 |
| Diagnostics (`PdfDocumentReader.Diagnostics`) for conditions the reader recovers from instead of aborting on | ✅ Supported | ISO 32000-2 Annex I.2 (#385) |
| Text extraction | ⏳ Planned | v2.4 (#98) |
| Image extraction | ⏳ Planned | v2.4 (#98) |
| Graduating `VellumPdf.Reader` from Preview to Stable | ⏳ Planned | v2.4 (#187) |
| Reading a document that uses an ISO/TS 32001–32004 extension (AES-GCM, PDF-MAC, SHA-3, EdDSA) | ⚠️ Partial — AES-GCM is rejected (`UnsupportedPdfFeatureException`); PDF-MAC is ignored and SHA-3/EdDSA signatures read as opaque, none verified | v2.6 (#236, #237, #238, #239) |
| Signature verification (integrity, coverage, certificate chains, revocation, achieved PAdES level) | ⏳ Planned | v2.13 |
| Round-trip editing of an existing document | ⏳ Planned | v3.0, `VellumPdf.Editing` (Epic #101) |
<!-- capability-table:reader:end -->

---

## 6. Gotchas and common mistakes

**A `null` password is not the same as "no encryption."** `PdfReaderOptions.Password = null`
means "try the empty password" — the common case for a permission-restricted document. It still
throws `PdfPasswordException` if the document needs a real one.

**`AllowReconstruction` changes what you can do next, not just how the file opened.**
`WasReconstructed` documents refuse `AppendRevision`, and `SaveDecrypted` treats them as
best-effort: a wrong guess at the object graph during recovery produces a wrong, but internally
consistent, decrypted copy.

**`MaxDecodedStreamBytes`, `ReconstructionBudgetMultiplier`, and `MaxDiagnostics` only go down.**
Raising any of them above its shipped default throws at `Open` time rather than silently
clamping — there is no way to ask the reader to trust a file more than its own defaults do.

**`SaveDecrypted` throws on a signed document unless you opt in, and opting in does not preserve
the signature.** If your goal is a signed document you can still verify, this method is the wrong
tool; if your goal is the plaintext content, `AllowInvalidatingSignatures` gets you there at the
cost of every signature reading as invalidated.

**Dispose the reader.** `PdfDocumentReader` implements `IDisposable`; use `using var reader = ...`
so the underlying stream and any buffered state are released.
