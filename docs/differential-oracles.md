# Differential oracles: measured behaviour of Ghostscript and MuPDF

`VellumPdf.Conformance` is the authoritative normative implementation. Everything on this page is
an *oracle*: an external program whose output is evidence about a document, never a verdict on it.
Where an oracle and this library disagree, the ISO text decides, and the disagreement is recorded
rather than resolved by changing a rule to match a tool. That is the target, not the current state:
PDF/A-2 is the standing exception, since ISO 19005-2 is not held and veraPDF's profiles are both the
source those rules were authored from and the arbiter CI fails the build on (#418).

This page records what two new oracles actually do, measured rather than read. It exists because
both of them mislead you if you assume the obvious things — that a clean exit means a clean file,
or that diagnostics arrive on the stream you expect.

## Versions, and why neither comes from apt

| Tool | Pinned | On `ubuntu-24.04` via apt | Why apt is not used |
| --- | --- | --- | --- |
| Ghostscript | 10.07.1 (2026-05-19) | 10.02.1 | The only prebuilt Linux artifact Artifex publishes is a snap (`gs_10.07.1_amd64_snap.tgz`), so this is a source build from the pinned tag either way, to pin an exact build and stay off snap packaging; building also keeps it off the apt snapshot dependency in #410 |
| MuPDF | 1.28.0 (2026-06-26) | 1.23.10 | five minor versions behind: `mutool audit`, `grep` and `bake` do not exist in it, nor do `show -r`, `show -L`; `clean` itself has only 21 options against roughly 33 in the pinned build, lacking `-e`, `-t`, `-tt`, `-L`, `-v`, `-S`, `-Z`, `--structure`, the `--color-`/`--gray-`/`--bitonal-` families, and `--recompress-images-when` |

MuPDF pins 1.28.0 rather than the newer 1.28.3 because, in the 1.28 line, Artifex publishes a
Windows zip only for the `x.y.0` release: 1.28.1, 1.28.2 and 1.28.3 all 404, while
`mupdf-1.28.0-windows.zip` is live. That is not a general rule (`mupdf-1.26.2-windows.zip` is
live), but it holds for 1.28. One version number then covers Linux and Windows, which is what keeps
a developer's run comparable to CI's.

Checksums are committed literals, not values fetched at run time: fetching a sums file from the
same host as the tarball it describes only catches a truncated download, not a replaced one.
Ghostscript's pinned SHA-512 is
`480aef1284dbe4d059dd0f9cc60dd5772d5093239df68b838f7f4f7d2efd77de8f03a7baeffbdbe65126a660c1c169137dd7d8cc28dd3bab59cd286a24ca1491`;
MuPDF's pinned SHA-256 is
`21c7f064903154f1c3a7458bee81f130fc36f9b5147ea13328f9980e02d2dea2`.

The executable is `gs` on Linux and `gswin64c.exe` on Windows, unlike every other oracle here, so
resolution has to branch on platform.

## Ghostscript

### Measured, on WSL Ubuntu 24.04 with the pinned build

Five fixtures: the PDF/A-2b golden, that file truncated to 60%, a zero-byte file, the same golden
with its `startxref` keyword corrupted, and the same golden with `/Type /Catalog` corrupted.

| Fixture | Exit | Diagnostic, and where it lands |
| --- | --- | --- |
| valid | 0 | none |
| truncated | 0 | `no startxref token found`, `xref table was repaired` on stderr; `bad trailer dictionary` on stdout |
| truncated, `-dPDFSTOPONERROR` | 1 | `Error: /undefined in --runpdf--` on stdout, `Unrecoverable error, exit code 1` on stderr |
| zero bytes | 0 | 210-byte version banner only, on stdout; no diagnostic and no `%%BoundingBox` line, in both modes |
| corrupt `startxref` | 0 | `no startxref token found`, `xref table was repaired` on stderr |
| corrupt `startxref`, `-dPDFSTOPONERROR` | 1 | `/undefined in --runpdf--` |
| corrupt `/Type /Catalog` | 0 | `Document Catalog has incorrect /Type` on stderr |
| corrupt `/Type /Catalog`, `-dPDFSTOPONERROR` | 1 | `/typecheck in --runpdf--` |

### What that means for how it is invoked

Errors go to stderr and warnings go to stdout. The truncated file proves it: the error block and
the warning block arrive on different streams from one run. A test that captures only stderr
silently passes every warning-class defect. Pass `-sstdout=%stderr`, which the documentation
provides for exactly this.

Exit status is not the signal. All four damaged fixtures exit 0 without `-dPDFSTOPONERROR`.
Ghostscript's stated policy is to render broken files and print a warning so that it stays useful
as a sanity check, so the text is the finding and the exit code is not. `-dPDFSTOPONERROR` makes
the status mean something, and every observed failure under it exited 1, not the 255 the source's
error mapping suggests for non-fatal errors.

**Never pass `-q` or `-dQUIET`.** Ghostscript's error reporting returns immediately when quiet is
set. Measured: with `-q`, the truncated file, the corrupt-`startxref` file and the corrupt-catalog
file all produce output identical to the valid file. A quiet invocation cannot fail.

A clean run has to prove it did work. The zero-byte file exits 0 printing only its 210-byte version
banner, so "Ghostscript did not complain" is not evidence that Ghostscript read anything. Use
`-sDEVICE=bbox`, which emits `%%BoundingBox` and `%%HiResBoundingBox` on stderr for every page it
processed: that line, not the banner, is the proof-of-work signal, and its absence is what
distinguishes an empty file from a clean one. Prefer it over `nullpage`, which works but appears
nowhere in the documentation.

Flag rot is a live risk here. `-dNEWPDF` was removed in 10.01.0 with no release-note entry, and
Ghostscript accepts removed switches silently. Any flag this gate depends on can go inert while the
gate stays green, so each engine will need a canary once this is wired into the test suite: a
known-bad fixture asserted to fail.

Ghostscript is not a PDF/A oracle. It writes PDF/A-1, -2 and -3 at level b only, and its default
compatibility policy emits a file carrying PDF/A metadata that is not conformant, with a warning.
What it can report is that a file parses, and which of its 81 error and 94 warning classes the file
triggered. Nothing about conformance, of input or output.

Working invocation:

```
gs -dSAFER -dBATCH -dNOPAUSE -sDEVICE=bbox -sstdout=%stderr -o /dev/null <file>
```

with a second strict pass adding `-dPDFSTOPONERROR`.

## MuPDF

### Measured, same fixtures and build

| Fixture | `mutool show … trailer` | `mutool draw -F stext` |
| --- | --- | --- |
| valid | 0, trailer printed | 0 |
| truncated | 0, partial trailer without `/ID` or `/Info` | 0 |
| zero bytes | 1 | 1, `cannot draw …` |
| corrupt `startxref` | 0, trailer recovered with keys reordered | 0 |
| corrupt `/Type /Catalog` | 0, silent | 0, silent |

Diagnostics on the damaged files, all on stderr: `format error: cannot find startxref`,
`warning: trying to repair broken xref`, `warning: repairing PDF document`,
`warning: object missing 'endobj' token`, `format error: cannot find version marker`,
`format error: no objects found`. `mutool clean` on the truncated fixture additionally emits
`warning: PDF stream Length incorrect`, a diagnostic the other two invocations do not produce.

### What that means

Both warnings and errors go to stderr, unlike Ghostscript, and errors carry their type as a
prefix, so lines read `format error: cannot find startxref`.

Repairs are reported only as ambient warnings. There is no repair summary and no distinct exit
code, so detecting "this file needed repair" means matching `repairing PDF document` or
`trying to repair broken xref`. The CLI has no strict mode; the C API's repair-throwing switch is
not exposed by any flag.

**Never pass `mutool draw -i`.** It sets ignore-errors and leaves the exit code at 0.

Write `mutool clean` output to a real file rather than `/dev/null`. Doing the latter produces a
spurious `warning: skipping invalid page range` on every input, valid ones included.

`mutool draw -F stext` is the reason to add MuPDF. It emits structured text as XML or JSON with
a bounding box, quad, font name, size, colour and bidi level per character — a second independent
positional-text opinion, richer than `pdftotext -bbox-layout`. `mutool show` gives an object-level
opinion beside qpdf's, `mutool trace` gives per-page device calls, and `mutool sign -v` gives a
second opinion beside `pdfsig`. `mutool audit` is a space and operator-usage report and is not a
validator.

MuPDF makes no PDF/A or PDF/UA claim of any kind.

## Why both, rather than either

On a five-fixture set the two engines already cover each other's blind spots in both directions:

| Fixture | Ghostscript | MuPDF |
| --- | --- | --- |
| zero bytes | exit 0, banner only, no diagnostic — misses it | exit 1, `no objects found` — catches it |
| corrupt `/Type /Catalog` | `Document Catalog has incorrect /Type` — catches it | exit 0, silent — misses it |

Neither is a conformance checker, and neither keys its diagnostics to a specification clause. They
say a parser disliked something, not which clause was violated. That makes them a differential
signal and never a citation.

## Determinism

Ten runs per engine per fixture, same input, same build: output was byte-identical every time,
before any normalisation. Run-to-run stability is therefore not the problem.

Normalisation is still required, because output is not stable across *machines* or *versions*.
Ghostscript prints a four-line banner containing its version, a `Loading font … from %rom%…` line,
and on failure a PostScript operand and execution stack whose numbers are build-specific. MuPDF
echoes the input filename in `page <file> N`, in `cannot draw '<file>'`, and in the `stext`
document element. Strip all of that, sort, and compare the normalised set.

The input has to be stable first. `PdfDocument.Timestamp` and `PdfDocument.DocumentId` are both
settable and pinning them makes identical content produce identical bytes, which
`DeterminismTests` already proves. Fixtures that do not pin them change on every run.

## Reproducing this

`eng/oracles/install-oracles.sh` fetches both from their pinned upstream sources, verifies the
checksums, builds them, and asserts that each answers at the version it was asked for. It installs
under `~/tools` unless given a prefix. `build-essential` is required and is not present in a stock
WSL Ubuntu 24.04 image.

The five fixtures all derive from one golden file,
`tests/VellumPdf.Kernel.Tests/GoldenTests.PdfA2b_rawBytes.verified.pdf`:

- `valid.pdf` is that file, unmodified.
- `truncated.pdf` is its first 60% by byte count.
- `empty.pdf` is zero bytes.
- `badxref.pdf` replaces the `startxref` keyword with `startxrEf`.
- `badcatalog.pdf` replaces `/Type /Catalog` with `/Type /Cataloh`.

The last two have to preserve length, since a byte-count change shifts every offset the
cross-reference table records. `perl -0777 -pe 's/startxref/startxrEf/'` does an in-place,
length-preserving substitution; `sed` does not guarantee that across a binary file with embedded
newlines and null bytes.

The script prints `GHOSTSCRIPT_HOME` and `MUPDF_HOME` for the two install prefixes. Nothing reads
those variables yet: wiring them into `ExternalTool` alongside `QPDF_HOME` and `POPPLER_HOME`, so
they resolve ahead of `PATH`, is the next step and has not landed.
