# Third-party reader fixtures

Every reader fixture before this corpus came from VellumPdf's own writer, so the reader had only
ever been exercised against its own dialect of PDF: generation 0 everywhere, no hybrid-reference
file, no object-stream layout but its own. The #121 review found three defects that trace to
exactly this. Each one was caught only because a reviewer hand-built the adversarial fixture during
review, and that does not scale. This corpus makes that adversarial coverage permanent, confirming
the same mechanisms VellumPdf already implements against another producer's dialect. Mutation
testing against it found every mutation it could construct also killed a pre-existing synthetic
test — expected, since the goal was closing a dialect-confidence hole, not finding a new one in
the reader's logic that the synthetic suite had missed.

Tracked in #196. Generated once with qpdf 12.3.2 and poppler 25.07.0 and checked in, rather than
shelled out for at test time, for the same reason as `Fixtures/Encrypted` (#99): CI and local
development do not run the same tool versions, so committing keeps the corpus byte-identical
everywhere.

Two constructs here, the hybrid-reference files and the `/Length` mismatch, cannot come from either
tool at all. qpdf normalizes generations to 0 and recomputes `/Length` on every write; poppler has
no option to introduce either deliberately. Five fixtures are hand-built:
`hybrid-spec-convention.pdf`, `hybrid-samesection-undefined.pdf`, `freed-object-reuse.pdf`,
`nonzero-gen-base.pdf`, and `length-mismatch.pdf`.

## Baseline

`baseline.pdf` is `tests/VellumPdf.Kernel.Tests/GoldenTests.StandardFont_rawBytes.verified.pdf`
normalized through qpdf, exactly as `Fixtures/Encrypted/plaintext-baseline.pdf` is built:

```sh
qpdf GoldenTests.StandardFont_rawBytes.verified.pdf baseline.pdf
```

The object-stream, linearized, and incremental-update fixtures below all descend from it, and so do
two of the three damaged fixtures, `truncated-tail.pdf` and `broken-startxref.pdf` — their own
transformation is the only delta on top of a shared, already-third-party-shaped file.
`length-mismatch.pdf` is the exception among the damaged set; see below for why. Baseline's content
stream decodes to `Hello, VellumPdf golden test!`, asserted directly in
`ThirdPartyReaderBehaviorTests` for every fixture that carries it unmodified.

`nonzero-gen-base.pdf` and `length-mismatch.pdf` do **not** descend from this baseline: both need
hand control over object generations or a stream's declared length, which normalizing through qpdf
would immediately undo.

## The corpus

| File | Producer / command | What it pins |
| --- | --- | --- |
| `hybrid-spec-convention.pdf` | Hand-built | The "hidden object" convention ISO 32000-2 §7.5.8.4 documents: object 3 free in revision 1's classic table, defined live in revision 2's /XRefStm |
| `hybrid-samesection-undefined.pdf` | Hand-built | The same free-then-redefine shape within a single revision, a case §7.5.8.4's normative sentence does not cover |
| `freed-object-reuse.pdf` | Hand-built | Object 5 live at generation 0, freed with next-generation 1 recorded, then reused as `5 1 obj` in a third revision — an xref entry whose generation must match the object header, an axis no other fixture here reaches |
| `baseline.pdf` | `qpdf` (see above) | Shared base; single revision, classic xref, no axis below applies yet |
| `objstm-xrefstream.pdf` | `qpdf --object-streams=generate baseline.pdf objstm-xrefstream.pdf` | Object streams plus a cross-reference stream; qpdf drops the classic xref table entirely, so this one fixture covers both axes |
| `linearized.pdf` | `qpdf --linearize baseline.pdf linearized.pdf` | `/Linearized` |
| `incremental-update.pdf` | `pdfattach baseline.pdf attach-payload.txt incremental-update.pdf` | An appended revision: two `%%EOF`, two `startxref`, a `/Prev` chain, and a byte-for-byte-preserved base revision |
| `nonzero-gen-base.pdf` | Hand-built | The catalog is `1 1 obj`, not `1 0 obj`: a reference at a nonzero generation read from a document rather than built in C# |
| `nonzero-generation.pdf` | `pdfattach nonzero-gen-base.pdf attach-payload.txt nonzero-generation.pdf` | An appended revision on a document whose catalog sits at a nonzero generation; poppler rewrites the catalog again in the new revision, still at generation 1 |
| `truncated-tail.pdf` | `baseline.pdf` truncated to its first 1200 bytes | No `startxref`, no `%%EOF`: an interrupted transfer |
| `broken-startxref.pdf` | `baseline.pdf` with its `startxref` offset changed from `1432` to `9999` | A `startxref` that points past end-of-file |
| `length-mismatch.pdf` | Hand-built | `/Length 64` on a stream whose real body (ending where `endstream` actually starts) is 46 bytes |

`attach-payload.txt` is the attachment poppler embeds in both `pdfattach` fixtures; it is committed
alongside them so the commands above are reproducible verbatim.

## Two hybrid fixtures, and only one has a third-party oracle

ISO 32000-2 §7.5.8.4 documents a "hidden object" convention: an object gets a free entry in some
earlier revision's classic xref table and a live definition in a later revision's cross-reference
stream. A pre-1.5 reader follows `/Prev`, reaches the free entry, and sees nothing; a reader that
understands cross-reference streams finds the live definition first and never reaches the free
entry at all. `hybrid-spec-convention.pdf` builds exactly that: object 3 is free in revision 1's
classic table and defined, via revision 2's `/XRefStm`, inside an object stream.

- **VellumPdf** resolves object 3 to `<< /Note (HIDDENVIAXREFSTM) /Type /ExData >>`.
- **qpdf** agrees: `qpdf --show-object=3 hybrid-spec-convention.pdf` prints the same dictionary.
  This is the real third-party oracle for the hidden object itself.
- **poppler** looks like it agrees, but it isn't a real oracle here. `pdftotext` on the file
  prints `BASE`, the unrelated page content that stays reachable throughout. Poppler reads
  cross-reference streams too, though, so it can't stand in for the pre-1.5 reader the convention
  targets, and it says nothing about object 3 either way. The page surviving is a structural fact
  about the file — poppler's output has no bearing on the hidden object at all.

`qpdf --check` reporting "No syntax or stream encoding errors found" is not, by itself, evidence
either — it only means the file parses, and a free object legitimately resolves to null just as
readily as a live one. The assertion that matters is `--show-object=3`, or `--json` content: the
same distinction that separates the two fixtures below.

`hybrid-samesection-undefined.pdf` puts the free entry and the `/XRefStm` definition in the *same*
revision instead of a `/Prev`-linked earlier one — the shape the original version of this fixture
used. §7.5.8.4's normative sentence covers a free entry in a *previous* section; the same-section
case sits outside what that sentence covers, and the three readers no longer agree:

- **VellumPdf** resolves object 4 from the `/XRefStm`, on the reasoning that a cross-reference
  stream's definition should still take precedence within its own revision.
- **qpdf** returns `null` for object 4 and reports the page's `/Contents` as empty.
- **poppler** prints `Internal Error: xref num 4 not found but needed, try to reconstruct` and only
  recovers the content by discarding the xref entirely and reconstructing from a full-file scan.

Neither qpdf nor poppler implements the precedence VellumPdf applies here, and neither result is a
conformance verdict. `HybridSameSection_object4_resolvesFromXRefStm_notTheClassicTableFreeEntry` in
`ThirdPartyReaderBehaviorTests` pins VellumPdf's current behavior so a regression is visible; it is
not a claim that this behavior is the only correct one. Which entry should win is an open question,
tracked in [pdf-association/pdf-issues#237](https://github.com/pdf-association/pdf-issues/issues/237)
("Conflicts between xref table and xref stream in hybrid-reference files"), unresolved at the time
of writing. Read VellumPdf's choice as a deliberate superset on a construct the spec leaves
contested — nobody has settled this in either direction, expert opinion included.

## Freed object number, reused at a bumped generation

`freed-object-reuse.pdf` is hand-built, three revisions: object 5 is live at generation 0 in
revision 1, revision 2 frees it with a classic-table entry recording 1 as the next generation, and
revision 3 reuses the number as `5 1 obj`. No other fixture in this corpus reaches that axis — the
two nonzero-generation files below carry a generation that was never recycled, so nothing else
exercises an xref entry whose generation has to match an object header for the reference to
resolve. qpdf agrees on both halves: `qpdf --show-object=5,1` yields the reused object, and
`qpdf --show-object=5` (generation 0 by default) yields `null`, because that generation really was
deleted.

Mutation testing against the reader's deletion tracking (`freed.UnionWith(localFreed)` in
`XrefParser.ParseOneRevision`) found the fixture's own test doesn't discriminate beyond
`GenerationNumberTests`' existing coverage: removing the tracking still kills two pre-existing tests
there, but not `FreedObjectReuse_resolvesTheReusedObject_andNotTheDeletedGeneration`, because
revision 3's definition wins regardless of whether the free entry was ever recorded. What the
fixture actually adds is confidence that the mechanism holds end-to-end on a real,
third-party-shaped, three-revision document — not a failure mode the unit tests were missing.

## Damaged files, and what each one should do

`truncated-tail.pdf` and `broken-startxref.pdf` are fatal: `PdfReader.Open` throws
`InvalidDataException`, the reader's vocabulary for a malformed file, and specifically not
`NullReferenceException`, `IndexOutOfRangeException`, `OutOfMemoryException`, or a stack overflow.

`length-mismatch.pdf` is not fatal. A wrong `/Length` is a common real-world producer bug —
`PdfObjectParser.ParseStreamBody` carries its own comment calling out "off by a few bytes, or stale
after an edit" — and the parser already recovers from it: `/Length` is honored only when it lands
exactly on `endstream`; otherwise the parser falls back to scanning for the marker. This fixture's
declared length (64) is in range for the file but does not land on the real `endstream`, whose true
body is 46 bytes — qpdf's own recovery agrees: `qpdf --check` reports "recovered stream length:
46". The fixture has only one `endstream` after the body start, so what it actually pins is the
`/Length`-preferred branch's "verify `endstream` follows, else fall back" rule, not
`ScanToEndstream` (#105)'s own preference tiers — a file exercising those would need more than one
candidate marker to choose between. The corresponding test asserts the *full* recovered body,
because a scan that stopped at the wrong marker would still open the file successfully while
silently truncating or extending the content — opening alone would not catch that.

## Regenerating a fixture

**One command above reproduces the committed bytes exactly; the rest come close but drift by a few
bytes on regeneration.** `pdfattach nonzero-gen-base.pdf attach-payload.txt` is the exception —
`nonzero-generation.pdf` comes out byte for byte identical, run after run, because its base carries
no `/ID` for poppler to touch.

Every `qpdf`-derived fixture regenerates the trailer's second `/ID` array element on each run,
identically to `Fixtures/Encrypted` — confirmed here too: two consecutive `qpdf
--object-streams=generate` runs on the same input differ only in that one element. The digest table
in `ThirdPartyFixtureCorpusTests` is what identifies the file; the commands record provenance, not
a reproduction recipe.

`pdfattach` follows the same pattern when the base document already carries an `/ID`: poppler seeds
that second `/ID` element from wall-clock seconds, so `incremental-update.pdf` regenerates it on
every run. Confirming this needs the two runs a second or more apart — two runs inside the same
wall-clock second land on the same seed and come out byte-identical, which would misread as
reproducibility. `nonzero-generation.pdf` is the exception named above: `nonzero-gen-base.pdf` has
no `/ID` at all, poppler does not add one when appending to a document that lacks one, and the
command reproduces this fixture's bytes exactly regardless of timing — so for this one fixture, a
digest mismatch on regeneration is a real signal, not a clock artifact.

To legitimately replace a fixture: regenerate it with its command, confirm it still carries the
property in the table above, recompute its SHA-256, and update the corresponding row in
`ThirdPartyFixtureCorpusTests`. For anything built from `baseline.pdf`, also re-run
`ThirdPartyFixtureCorpusTests.IncrementalUpdate_beginsWithBaseline_verbatim` (or the
`nonzero-generation.pdf` / `broken-startxref.pdf` equivalent) — those pin the byte-prefix or
fixed-length-diff relationship a regenerated fixture must still satisfy against its own base.
