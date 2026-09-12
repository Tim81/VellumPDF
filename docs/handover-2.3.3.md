# Handover: continuing into v2.3.3

Written 2026-09-12, the day v2.3.2 was tagged. This is for whoever picks up the hardening
milestone, human or agent. It covers the state v2.3.2 left behind, the two pull requests already
open against 2.3.3, and the standard the maintainer has set for public documentation, which is the
part most likely to be got wrong.

## Where v2.3.2 left things

Tagged at `486622d` on `release/2.3.2`, 32 commits past v2.3.1. Every gate green on that commit:
build, format, clean-room, the PDF 2.0 inventory, and 7,006 tests across six projects.

It carried features despite the patch number, which was a deliberate call: the work was ready and
the next minor is milestone v2.4, whose own scope would have held it back. So the release commit
also promoted 32 public API entries from unshipped to shipped. Twenty-four in Layout for
band-truncation reporting, 4 in Kernel for the table span attributes, 4 in Conformance for the preflight password
overloads. `VellumPdf.Reader`'s 208 entries stay unshipped on purpose: it remains Preview until
#187 graduates it, so its surface can still move.

The showcase pins 2.3.1 and carries three caps this release affects. Tell them, and say which:

| cap in `SpecLimits` | value | what changed |
|---|---|---|
| `MaxRunningBandTemplateLength` | 200 | a band is truncated and reported now, so the cap is no longer load-bearing |
| `MaxListNestingDepth` | 64 | does nothing observable, since depths past two were always discarded (#479, #483) |
| `MaxTableRows` | 2,000 | the pagination recursion is gone, so the reason for the cap is too |

## The two open pull requests

Both are documentation only, both CI-green, both rebased onto main. Neither changes behaviour.

**#503, Layout boundaries.** Nineteen members gain a boundary paragraph, thirteen carry an
`<exception>` tag where one did. The four save overloads and `PageSize` are covered.

**#504, Kernel boundaries.** The five image loaders, the object registry, the writer's position
counter, the barcode matrix, the timestamp interface.

Read the commit messages on both before touching them. They record which claims were corrected and
why, and three of those corrections were themselves wrong on a later pass.

## The documentation standard the maintainer set

This is the important part. It is in `CONTRIBUTING.md` under "Public XML documentation states the
boundary", and in `CLAUDE.md`. Both say the same thing. `CONTRIBUTING.md` is the one that ships, so that is
the authority.

**Every public member documents where its boundary is and what a caller must not do, not only what
it is.** Write it in the same commit as the member. The pattern to copy is `TextStyle.FontSize` in
`src/VellumPdf.Layout/Core/TextStyle.cs`.

The shape:

- `<summary>` says what the member is.
- An `<exception>` tag for every type a call can raise, **including when the throw lands in a later
  call rather than this one**. In the layout engine that usually means `Document.Save`. List each type
  separately. `NotSupportedException` does not derive from `InvalidDataException`, so a caller
  who catches one and not the other has a crash waiting.
- `<remarks>` opens with what is **refused**, which call throws it, and the reason. Not just the
  fact.
- Then a paragraph for input that is accepted today but should not be relied on: what it does now,
  and which major version will reject it.

Two rules about the writing itself, both learned by getting them wrong:

- **Measure each non-finite value separately.** `NaN`, positive infinity and negative infinity take
  different branches, and a sentence about "a non-finite value" measured on one of them is usually
  wrong about the other two. A positive-infinity image width is clamped and accepted while `NaN` is
  refused; a non-finite document margin is refused only when it is positive infinity.
- **Check the member does not already carry a `<remarks>` block.** Two on one member compile
  without a warning and renderers show one of the two, so a contradiction between them is invisible
  until a reader hits it. That happened on four Layout members, and on one of them the two blocks disagreed.

## The voice the maintainer wants

**Write like the Swiss Ephemeris programmer's reference**, <https://www.astro.com/swisseph/swephprg.htm>.
That is an explicit instruction, not a suggestion. What it means concretely:

- **Bold marks one operative word, never a clause.** The reference writes "After `swe_close()`,
  **no** Swiss Ephemeris functions should be used" and "`swe_fixstar()` **does not compute
  speeds**". It never bolds a whole sentence as a label. Bold a negation or an obligation and
  nothing else.
- **Mark severity with a prefix, not emphasis.** `Attention:` for a trap a caller will fall into.
  `NOTE:` for a fact about history or a version. That is the reference's own device and it replaces
  bold as the signal.
- **Address the reader directly, and functionally.** "If you want the mean equinox, you can turn
  nutation off." The reader is a competent peer with a job to do. Anything a caller has to handle
  itself should say so in the second person: "you have to check this yourself", "if you want no
  rule, leave the element out".
- **Constraint first, in one short sentence. The reason follows as its own sentence.** Not fused
  into a single long one. The reference is blunt: "`swe_calc()` returns a 32-bit integer value.
  This value is >= 0, if the function call was successful, and < 0, if a fatal error has occurred."
- **Use "must", "should" and "have to" deliberately**, and say which the specification used. A
  `should` is not a `shall`, and the difference decides whether a deviation is a defect.

One thing deliberately **not** imitated: the reference writes "Be aware, that the user will have to
handle this case in his program." That comma before "that" is its author's German habit. In a .NET
public API it reads as a typo rather than as voice, so keep the construction and drop the comma.

Measured on the v2.3.2 documentation before and after the voice pass, for calibration:

| | before | after |
|---|---|---|
| bold tags per 1,000 words | 13 to 16 | 7 to 8 |
| bold "Do not" labels | 26 | 4 |
| direct address | 4 | 48 |
| median sentence, words | 14 to 15 | 11 to 12 |
| em dashes | 14 | 0 |

## Run the avoid-ai-writing skill

**On every changelog entry, README, documentation page, and on code comments too, XML-doc and
inline.** This is standing instruction, in `CLAUDE.md` and repeated by the maintainer.

What it actually caught on this release's documentation, so you know what to expect: no vocabulary
problems and no hedging, but heavy bold overuse and the inline-header pattern, where a bold clause
opens a paragraph and the next sentence repeats it. Also sixteen instances of a bold label ended
with a period where a person writes a colon, and fourteen em dashes nobody had noticed across three
prose rounds.

Run it in `detect` mode first with `--voice technical --context docs`. Fix what it flags, then read
the result against the voice notes above, The skill and the voice are different axes. The skill removes
machine-writing tells; the voice decides what the prose sounds like once they are gone.

## The failure mode to avoid, stated plainly

Three rounds of documentation review on v2.3.2 went: reviewers find false sentences, the corrections
are wrong, the corrections to those miss the copies. Ten HIGH findings in total, every one a false sentence rather
than broken code.

The mechanism, named by the reviewer that caught it: **the sentence a reviewer points at gets
corrected and its copies are left asserting what has just been shown false.** A claim about the loaders lived in five places and one
was corrected. A claim on a one-argument overload was corrected
and the two-argument overload kept the old text, so a reader of one got the opposite answer from a
reader of the other.

Before editing a documentation claim:

1. **Grep for every copy of it first** and write the count down. Assert the count in whatever
   script does the edit, and fail loudly if a copy turns up that the list did not expect.
2. **Check afterwards** that no copy of the corrected claim is still standing. That check caught
   two copies on the last pass that the edit list had missed, and flagged two more that turned out to be
   correct in context. That is the right way for it to fail, because it forces a look.
3. **Verify each claim by running it**, not by reasoning about it. Every one of the ten HIGH
   findings would have been caught by a probe that took under a minute.
4. A figure from a reviewer is a lead, not evidence. Two figures went into a code comment on a
   reviewer's word and one of them was out by a factor of four. If you cannot reproduce a number,
   say what both measurements gave, or quote none.
5. **Check the shelf, not the index.** A divergence row claimed veraPDF was not resolvable on this
   machine. It is installed at `C:\Users\Timothy\tools\verapdf`, the path recorded in the notes,
   and running it settled the question in one command.

## What is on the milestone

Twenty-three open issues. The ones that are defects rather than documentation, roughly in order of
how much they can cost a consumer:

- **#508**, a failed `Save(string)` destroys the file that was already there. The layout runs
  after the file is opened, so a regeneration over a previous report loses it.
- **#506**, an XMP `Real` value can make a preflight run take seconds per property. Quadratic
  pattern, no timeout, and the document chooses both the type and the value.
- **#505**, a JPEG-compressed TIFF strip bypasses the pixel cap the loader documents. 145 bytes
  returns 4,294,836,225 pixels.
- **#511**, a raw CRLF in a literal string keeps both bytes where 7.3.4.2 requires one line feed.
  Sequence this **before** #507: it is currently the only thing making the CRLF form of that bug
  fail.
- **#507**, both `/Lang` rules accept a tag ending in a line feed. A false accept, recorded as D6
  in `docs/conformance-divergences.md`. Remove the row when this lands, per that file's own
  instructions.
- **#509**, a non-finite colour channel reaches the content stream as an invalid token. Same
  class as #478, which fixed it for image extents and missed colour.
- **#512**, a negative TIFF `ColorMap` offset throws `IndexOutOfRangeException` where its
  sibling readers check and refuse the file.
- **#502**, a non-finite `Document.Margins` inset reports the wrong cause in two of three forms.
- **#498** and **#499**, the GIF logical screen and the version byte. #498 needs a decision
  first. The specification says the uncovered area takes the background colour, the reference
  implementation paints nothing, and browsers treat it as transparent. Decide before coding.

Then the documentation remainder: **#510** names sixteen Layout members with a measured boundary and no
documentation. Not in it, and worth adding: the canvas has 63 public members writing non-finite
values straight into the content stream, `CcittImageLoader` has six reachable exception types and no
tags, and two uncatchable stack overflows are reachable from public API through the structure tree
and the outline.

## Practical notes for whoever continues

- **`dotnet test` must run bare.** Any extra option, `--nologo` or `-m:2` or a thread cap, gives
  "Zero tests ran" and exit 5, because the test platform hands them to the test app.
- **Run parallel agents in separate worktrees.** Agents sharing one tree have produced false
  failures here, and a mutation-testing agent needs its own tree absolutely.
- **Build the mutation worktree from a commit, not from `HEAD` with uncommitted work.** That
  mistake reported a mutant as uncaught twice in one day, and it is indistinguishable from a real
  gap until you look.
- **Git Bash resolves the wrong `pdftotext`.** It finds Xpdf at `/mingw64/bin`. Poppler is at
  `C:\msys64\mingw64\bin`, which is what `POPPLER_HOME` points at. Use the full path or PowerShell.
- **`gh pr merge` needs `--admin` and `--body-file`.** Squash is set to use every branch commit
  body otherwise, and main requires a review the owner bypasses.
- **Set `core.hooksPath eng/hooks` in every worktree before committing.** It is a relative path
  resolved per worktree, so a fresh one starts unprotected, and the hook is what keeps AI
  attribution out of commit messages.
