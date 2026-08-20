# Third-party reader fixtures

Every reader fixture before this corpus came from VellumPdf's own writer, so the reader had only
ever been exercised against its own dialect of PDF: generation 0 everywhere, no hybrid-reference
file, no object-stream layout but its own. The #121 review found three defects that trace to
exactly this. Each one was caught only because a reviewer hand-built the adversarial fixture during
review, and that does not scale.

Tracked in #196. Generated once with qpdf 12.3.2 and poppler 25.07.0 and checked in, rather than
shelled out for at test time, for the same reason as `Fixtures/Encrypted` (#99): CI and local
development do not run the same tool versions, so committing keeps the corpus byte-identical
everywhere.

Two constructs here, the hybrid-reference files and the `/Length` mismatch, cannot come from either
tool at all. qpdf normalizes generations to 0 and recomputes `/Length` on every write; poppler has
no option to introduce either deliberately. All three are hand-built.

## Baseline

`baseline.pdf` is `tests/VellumPdf.Kernel.Tests/GoldenTests.StandardFont_rawBytes.verified.pdf`
normalized through qpdf, exactly as `Fixtures/Encrypted/plaintext-baseline.pdf` is built:

```sh
qpdf GoldenTests.StandardFont_rawBytes.verified.pdf baseline.pdf
```

The object-stream, linearized, incremental-update and damaged fixtures below all descend from it,
so their own transformation is the only delta on top of a shared, already-third-party-shaped file.
Its content stream decodes to `Hello, VellumPdf golden test!`, asserted directly in
`ThirdPartyReaderBehaviorTests` for every fixture that carries it unmodified.

`nonzero-gen-base.pdf` and `length-mismatch.pdf` do **not** descend from this baseline: both need
hand control over object generations or a stream's declared length, which normalizing through qpdf
would immediately undo.

## The corpus

| File | Producer / command | What it pins |
| --- | --- | --- |
| `hybrid-spec-convention.pdf` | Hand-built | The "hidden object" convention ISO 32000-2 §7.5.8.4 documents: object 3 free in revision 1's classic table, defined live in revision 2's /XRefStm |
| `hybrid-samesection-undefined.pdf` | Hand-built | The same free-then-redefine shape within a single revision, a case §7.5.8.4 does not describe |
| `baseline.pdf` | `qpdf` (see above) | Shared base; single revision, classic xref, no axis below applies yet |
| `objstm-xrefstream.pdf` | `qpdf --object-streams=generate baseline.pdf objstm-xrefstream.pdf` | Object streams plus a cross-reference stream; qpdf drops the classic xref table entirely, so this one fixture covers both axes |
| `linearized.pdf` | `qpdf --linearize baseline.pdf linearized.pdf` | `/Linearized` |
| `incremental-update.pdf` | `pdfattach baseline.pdf attach-payload.txt incremental-update.pdf` | An appended revision: two `%%EOF`, two `startxref`, a `/Prev` chain, and a byte-for-byte-preserved base revision |
| `nonzero-gen-base.pdf` | Hand-built | The catalog is `1 1 obj`, not `1 0 obj`: a reference at a nonzero generation read from a document rather than built in C# |
| `nonzero-generation.pdf` | `pdfattach nonzero-gen-base.pdf attach-payload.txt nonzero-generation.pdf` | An appended revision on a document whose catalog sits at a nonzero generation; poppler rewrites the catalog again in the new revision, still at generation 1 |
| `truncated-tail.pdf` | `baseline.pdf` truncated to its first 1200 bytes | No `startxref`, no `%%EOF`: an interrupted transfer |
| `broken-startxref.pdf` | `baseline.pdf` with its `startxref` offset changed from `1432` to `9999` | A `startxref` that points past end-of-file |
| `length-mismatch.pdf` | Hand-built | `/Length 64` on a stream whose real body (ending where `endstream` actually starts) is 41 bytes |

`attach-payload.txt` is the attachment poppler embeds in both `pdfattach` fixtures; it is committed
alongside them so the commands above are reproducible verbatim.

## Two hybrid fixtures, and only one has a third-party oracle

ISO 32000-2 §7.5.8.4 documents a "hidden object" convention: an object gets a free entry in some
earlier revision's classic xref table and a live definition in a later revision's cross-reference
stream. A pre-1.5 reader follows `/Prev`, reaches the free entry, and sees nothing; a reader that
understands cross-reference streams finds the live definition first and never reaches the free
entry at all. `hybrid-spec-convention.pdf` builds exactly that: object 3 is free in revision 1's
classic table and defined, via revision 2's `/XRefStm`, inside an object stream. All three readers
tested agree on it:

- **VellumPdf** resolves object 3 to `<< /Note (HIDDENVIAXREFSTM) /Type /ExData >>`.
- **qpdf** agrees: `qpdf --show-object=3 hybrid-spec-convention.pdf` prints the same dictionary.
- **poppler** agrees too: `pdftotext` on the file prints `BASE`, the unrelated page content that
  stays reachable throughout — confirming the hidden object doesn't corrupt anything around it.

`qpdf --check` reporting "No syntax or stream encoding errors found" is not, by itself, any of the
above — it only means the file parses, and a free object legitimately resolves to null just as
readily as a live one. The assertion that matters is `--show-object=3`, or `--json` content: the
same distinction that separates the two fixtures below.

`hybrid-samesection-undefined.pdf` puts the free entry and the `/XRefStm` definition in the *same*
revision instead of a `/Prev`-linked earlier one — the shape the original version of this fixture
used. §7.5.8.4 does not describe that case, and the three readers no longer agree:

- **VellumPdf** resolves object 4 from the `/XRefStm`, on the reasoning that a cross-reference
  stream's definition should still take precedence within its own revision.
- **qpdf** returns `null` for object 4 and reports the page's `/Contents` as empty.
- **poppler** prints `Internal Error: xref num 4 not found but needed, try to reconstruct` and only
  recovers the content by discarding the xref entirely and reconstructing from a full-file scan.

Neither qpdf nor poppler implements the precedence VellumPdf applies here, and neither is a
conformance verdict either way — the construct itself sits outside what the spec defines.
`HybridSameSection_object4_resolvesFromXRefStm_notTheClassicTableFreeEntry` in
`ThirdPartyReaderBehaviorTests` pins VellumPdf's current behavior so a regression is visible, not a
claim that this behavior is the only correct one. Erratum
[pdf-association/pdf-issues#146](https://github.com/pdf-association/pdf-issues/issues/146) clarifies
that `/XRefStm`'s value is the byte offset of *the* cross-reference stream for its own revision —
relevant to reading §7.5.8.4 correctly for the fixture above, but silent on the same-section case.

## Damaged files, and what each one should do

`truncated-tail.pdf` and `broken-startxref.pdf` are fatal: `PdfReader.Open` throws
`InvalidDataException`, the reader's vocabulary for a malformed file, and specifically not
`NullReferenceException`, `IndexOutOfRangeException`, `OutOfMemoryException`, or a stack overflow.

`length-mismatch.pdf` is not fatal. A wrong `/Length` is a common real-world producer bug — qpdf's
own comment in `PdfObjectParser.ParseStreamBody` calls out "off by a few bytes, or stale after an
edit" — and the parser already recovers from it: `/Length` is honored only when it lands exactly on
`endstream`; otherwise `ScanToEndstream` (#105) takes over. This fixture's declared length (64) is
in range for the file but does not land on the real `endstream`, so it exercises that fallback
rather than the immediate-scan path a wildly out-of-range length would take. The corresponding test
asserts the *full* recovered body, not merely that opening succeeded — a scan that stopped at the
wrong marker would still open the file while silently truncating or extending the content.

## Regenerating a fixture

**None of the commands above reproduce the committed bytes exactly, and for two different reasons
depending on the fixture.**

Every `qpdf`-derived fixture regenerates the trailer's second `/ID` array element on each run,
identically to `Fixtures/Encrypted` — confirmed here too: two consecutive `qpdf
--object-streams=generate` runs on the same input differ only in that one element. The digest table
in `ThirdPartyFixtureCorpusTests` is what identifies the file; the commands record provenance, not
a reproduction recipe.

`pdfattach` behaves the same way when the base document already carries an `/ID` —
`incremental-update.pdf` regenerates its second `/ID` element on every run, verified by running the
command twice and diffing. `nonzero-generation.pdf` does not: `nonzero-gen-base.pdf` has no `/ID`
at all, poppler does not add one when appending to a document that lacks one, and two consecutive
runs against it are byte-identical, confirmed the same way.

To legitimately replace a fixture: regenerate it with its command, confirm it still carries the
property in the table above, recompute its SHA-256, and update the corresponding row in
`ThirdPartyFixtureCorpusTests`. For anything built from `baseline.pdf`, also re-run
`ThirdPartyFixtureCorpusTests.IncrementalUpdate_beginsWithBaseline_verbatim` (or the
`nonzero-generation.pdf` / `broken-startxref.pdf` equivalent) — those pin the byte-prefix or
fixed-length-diff relationship a regenerated fixture must still satisfy against its own base.
