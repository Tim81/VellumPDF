# Fuzz-crash regression corpus

Every minimized input `ParserFuzzTests`' oracle has ever rejected, one `.pdf` per finding. Empty
until the first finding lands — an empty directory here is not a claim that fuzzing found nothing on
a given run; see below for how to tell the two apart.

## The capture rule

A CsCheck fuzz run that finds a crash-class exception is not, by itself, a fix. Issue #99 requires
three things together, in order:

1. **Minimize** the failing input — CsCheck's own shrinker does this; the printed seed lets a
   developer reproduce the exact failing case locally (`CsCheck_Seed`, see `ParserFuzzTests`'s class
   doc) and hand its shrunk byte array to a debugger.
2. **Fix** the underlying defect in the reader, not the fuzz test.
3. **Commit the minimized input here**, SHA-256-pinned and token-scanned the same way
   `Fixtures/Encrypted` and `Fixtures/ThirdParty` are (see `FuzzCorpusTests`), and add it to
   `ParserFuzzTests`'s seed corpus so the exact regression is fuzzed forever after, not just replayed
   once.

Skipping step 3 leaves "found nothing this run" and "found something, fixed it, forgot to capture
it" indistinguishable from the outside — both look like a green build with an empty folder. That is
the failure mode this rule exists to close: a fix without a fixture is not verifiable six months
later, when nobody remembers which commit quietly patched a crash CsCheck happened to hit once.

## What "the oracle" means here

`ParserFuzzTests` is a **robustness** oracle, not a conformance oracle. It asserts that no
crash-class exception (`IndexOutOfRangeException`, `NullReferenceException`,
`OutOfMemoryException`, `OverflowException`, and their kin) ever escapes `PdfLexer.NextToken`,
`PdfObjectParser.ParseObject`, or `PdfReader.Open`. It does **not** assert that throwing one of the
allowed types (`InvalidDataException`, `UnsupportedPdfFeatureException`, `PdfPasswordException`) is
always the *correct* outcome for a given input, because ISO 32000-2 sometimes requires the opposite:
§7.3.10 says an indirect reference to an undefined object "shall not be considered an error" and
§7.3.9 makes a null-valued dictionary entry "equivalent to omitting the entry entirely", so
degrading to null on that family of input is normatively required, and a value-level known-answer
test elsewhere in this project (not this fuzz harness) pins that a document exercising it opens
successfully. Whether a given mutated input *should* recover or *should* error is exactly the kind
of question a byte-level mutation can't answer on its own — the corpus KATs answer it, one shape at
a time; this harness only guards that whichever way the reader goes, it goes there through the
declared vocabulary, not a crash.
