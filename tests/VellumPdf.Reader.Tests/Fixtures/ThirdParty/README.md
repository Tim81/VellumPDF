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
tool at all. qpdf's own documentation says plainly: "We do not support creation of hybrid files."
Neither does poppler have an option to introduce one deliberately. The `/Length` mismatch is
separately out of reach: qpdf recomputes `/Length` on every write, and poppler has no lever for it
either. The other two hand-built fixtures need generations neither tool will leave alone — qpdf
normalizes every generation to 0 on write, so `freed-object-reuse.pdf`'s reused-at-generation-1
object and `nonzero-gen-base.pdf`'s nonzero-generation catalog both have to be built by hand. Five
fixtures are hand-built in total: `hybrid-spec-convention.pdf`, `hybrid-samesection-undefined.pdf`,
`freed-object-reuse.pdf`, `nonzero-gen-base.pdf`, and `length-mismatch.pdf`.

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
| `freed-object-reuse.pdf` | Hand-built | Object 5 live at generation 0, freed with next-generation 1 recorded, then reused as `5 1 obj`; object 7 freed the same way and never redefined — a reference's generation has to match the xref entry's recorded generation, an axis no other fixture here reaches |
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
alongside them so the commands above are reproducible verbatim. It is not itself embedded as a test
resource — the csproj glob for this folder only picks up `*.pdf` — so it plays no part in either
coverage guard below.

`ThirdPartyFixtureCorpusTests.EveryEmbeddedFixture_isCoveredByTheTheory` fails loudly if a `.pdf`
lands in this folder without a matching row above, mirroring the same guard in
`EncryptedFixtureCorpusTests`. Both guards filter on the `.pdf` extension first, so a non-`.pdf`
embedded resource dropped into either folder would be covered by neither guard if the csproj glob
were ever widened to embed it. Pre-existing, and low risk today: `attach-payload.txt` is the only
other file here, and it isn't embedded.

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
not a claim that this behavior is the only correct one. Which entry should win is an open erratum,
tracked in [pdf-association/pdf-issues#237](https://github.com/pdf-association/pdf-issues/issues/237)
("Conflicts between xref table and xref stream in hybrid-reference files"), unresolved at the time of
writing — but the discussion there so far favours the free entry winning, not VellumPdf's reading:
MatthiasValvekens argues the classic table's `f` entry wins and the object should be considered
`null`, and mkl-public agrees ("believes correctly"), quoting §7.5.8.4's rule that a cross-reference
stream is consulted only when an entry is *not found* in the classic table first — a free entry is
found. petervwyatt raises separate wording defects in the same clause but dissents from neither.
VellumPdf deliberately differs from that reading and pins its own behavior anyway, as a superset on
a contested construct rather than a settled one; tracked as
[#206](https://github.com/Tim81/VellumPDF/issues/206).

## Freed object number, reused at a bumped generation

`freed-object-reuse.pdf` is hand-built, three revisions, with two objects deleted along the way.
Object 5 is live at generation 0 in revision 1; revision 2 frees it with a classic-table entry
recording 1 as the next generation; revision 3 reuses the number as `5 1 obj`. Object 7 is live at
generation 0 in revision 1, freed the same way in revision 2, and never redefined — revision 3's own
xref table says nothing about it at all, so resolving it correctly depends entirely on revision 2's
deletion surviving the merge across revisions. Revision 2's free list is linked per ISO 32000-2
§7.5.4: the head entry (object 0) points at object 5, object 5's free entry points at object 7, and
object 7's free entry closes the chain back to 0.

No other fixture in this corpus reaches the axis this pins — a reference's generation has to match
the *xref entry's recorded generation*, not the "N G obj" header (ISO 32000-2 treats the xref as
authoritative, and `PdfDocumentReader` does not additionally require the header to agree when the
xref parsed cleanly; see its cache field comment). The two nonzero-generation fixtures elsewhere in
this corpus carry a generation that was never recycled, so neither exercises a generation actually
being reused after a deletion.

qpdf agrees on both deleted objects: `qpdf --show-object=5,1` yields the reused object,
`qpdf --show-object=5` (generation 0 by default) yields `null`, and so does
`qpdf --show-object=7`. That agreement on object 5 is not, by itself, evidence that the free entry
was honoured — a control file with no free entry anywhere (revision 1 defines `5 0 obj`, revision 2
defines `5 1 obj` directly, no deletion in between) gives qpdf and VellumPdf the same byte-identical
answer for object 5: the null comes from the merged xref simply mapping object 5 to generation 1,
the newest revision's entry, regardless of whether anything was ever freed. qpdf *is* discriminating
on object 7, though — it resolves object 7 in that same no-free-entry control, and returns `null`
for it here, where the free entry is real.

Mutation testing against the reader's deletion tracking (`freed.UnionWith(localFreed)` in
`XrefParser.ParseOneRevision`) confirms the same split. Removing it still kills the same two
pre-existing `GenerationNumberTests` as before, but now it also fails
`FreedObjectReuse_resolvesTheReusedObject_andNotTheDeletedGeneration`: object 7 resolves non-null
once the deletion stops being tracked (verified directly — removing the tracking changes the test
run's outcome). Object 5 alone still isn't load-bearing: revision 3's definition wins regardless of
whether its own deletion was ever recorded. Object 7 is what makes this fixture's own test
discriminate, on a real, third-party-shaped, three-revision document, rather than merely restating
what `GenerationNumberTests` already covers.

## Damaged files, and what each one should do

`truncated-tail.pdf` and `broken-startxref.pdf` are fatal: `PdfReader.Open` throws
`InvalidDataException`, the reader's vocabulary for a malformed file, and specifically not
`NullReferenceException`, `IndexOutOfRangeException`, `OutOfMemoryException`, or a stack overflow.

`length-mismatch.pdf` is not fatal. A wrong `/Length` is a common real-world producer bug —
`PdfObjectParser.ParseStreamBody` carries its own comment calling out "off by a few bytes, or stale
after an edit" — and the parser already recovers from it: `/Length` is honored only when the next
token after it, past any intervening whitespace or comments, is `endstream`; otherwise the parser
falls back to scanning for the marker. This fixture's declared length (64) is in range for the file
but fails that check. Three numbers describe it, and each is correct for what it measures: the
content the test asserts against, `BT /F1 24 Tf 40 100 Td (LENGTHMISMATCH) Tj ET`, is 45 bytes; the
gap from where the body starts to where `endstream` actually begins is 46 bytes, one more to cover
the trailing end-of-line before the keyword; qpdf's own recovery reports
that same 46 — `qpdf --check` prints "recovered stream length: 46"; and ISO 32000-2 §7.3.8.2 is
clear that `/Length` itself should carry the 45-byte reading, excluding the EOL. The fixture has
only one `endstream` after the body start, so what it actually pins is the `/Length`-preferred
branch's "verify `endstream` follows, else fall back" rule, not `ScanToEndstream` (#105)'s own
preference tiers — a file exercising those would need more than one candidate marker to choose
between. The corresponding test asserts the *full* recovered body, because a scan that stopped at
the wrong marker would still open the file successfully while silently truncating or extending the
content — opening alone would not catch that.

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
