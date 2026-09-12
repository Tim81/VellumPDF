# Where the conformance rules disagree with veraPDF, and why

`VellumPdf.Conformance` is the authoritative normative implementation. veraPDF is an oracle: its
output is evidence, not a verdict. Where the two disagree, the specification decides.

That principle needs somewhere to write the disagreements down, because the test suite currently
does the opposite. `ConformanceCatalogTests.Catalog_MatchesVeraPdfProfile` set-diffs our rule ids
against veraPDF's profile ids and fails in **both** directions, so a rule the standard requires and
the profile omits breaks the build exactly as a phantom rule would. Agreement with the tool is the
passing condition, which makes the profile authoritative by construction.

This file is the intended replacement for that posture. A disagreement recorded here is known and
accepted. A disagreement that appears and is not here is what should fail.

## How to read a row

Each row says what the standard requires, what any independent second reading found, what veraPDF
does, which one this library follows, and why. The Checkable field is the one that is easy to
skip and shouldn't be: it says whether a reader with only this repository can confirm the row, or
whether it rests on a document nobody here holds.

Two rows are **gated**. Correcting them makes this library disagree with veraPDF on a profile, which
fails the aggregate oracle and the id diff as they stand today. Those corrections wait for the
per-rule comparison in #419. The other two are not gated. D4's correction moves toward the standard
and the tool at once, so nothing holds it back but the care it needs, which is #458. D5 is corrected
already, and what remains of it is a check the profile does not carry.

---

## D1 — CIDSystemInfo `Supplement` compared in the wrong direction

- Rules: `FontStructureRule.CheckCidSystemInfo`, `UaCidSystemInfoRule`
- Clauses: ISO 19005-2 6.2.11.3.1, ISO 14289-1 7.21.3.1
- Status: gated on #419 · tracked in #428

The standard requires the CIDFont's `Supplement` to be **greater than or equal to** the CMap's. The
clause's own NOTE gives the reason: the font must contain glyphs for every CID the CMap can
reference. veraPDF tests the opposite, `CIDFontSupplement <= CMapSupplement`, in both `PDFUA-1.xml`
and `PDFA-2B.xml`, and both profile descriptions quote the clause as "less than or equal to". This
library matches veraPDF.

**Second reading.** Two standards say it independently. ISO 32000-1 Table 116 adds that `Supplement`
"shall not be used in determining compatibility between character collections" at all, so the base
specification imposes no direction and cannot be the source of the inverted one.

**Why it is wrong the way it is.** veraPDF's direction requires the font to be older than the CMap,
which defeats the purpose the NOTE states.

Checkable: ISO 14289-1 and the profile XML are both here, so the PDF/UA-1 half is confirmable
from a clone. The ISO 19005-2 half was read through a licensed viewer and is not.

## D2 — An Identity exemption keyed on the wrong thing

- Rules: `FontStructureRule.CheckCidSystemInfo`, `UaCidSystemInfoRule`
- Clauses: ISO 19005-2 6.2.11.3.1 and 6.2.11.3.3, ISO 14289-1 7.21.3.1 and 7.21.3.3
- Status: gated on #419 · tracked in #428

The standard grants one exemption, keyed on the `/Encoding` entry of the Type 0 font dictionary
being `Identity-H` or `Identity-V`. Both rules implement that correctly and then add a second
exemption keyed on something else: an embedded CMap stream whose own `/CMapName` is one of those
names. Those are different populations. When `/Encoding` is a stream rather than a name, the
standard's exemption does not apply whatever the stream calls itself.

`FontStructureRule` says where the second one came from, unprompted:

> An embedded CMap whose own /CMapName is Identity-H/V is exempt too — veraPDF keys the exemption
> on the CMap name, not the /Encoding reference

**Second reading.** `CMapName` occurs zero times in ISO 14289-1 and zero times in ISO 19005-2, the
latter validated against a control term returning seven hits so the zero means something. Neither
CMap clause licenses it.

**Why this one matters more than D1.** It fails in the direction that stays quiet. An over-broad
exemption suppresses a finding the standard wants raised, so a non-conformant file passes and
nothing announces it. D1 at least fires.

Checkable: the PDF/UA-1 half, yes. The PDF/A-2 half rests on the viewer.

## D4 — Font embedding ignores the rendering-mode-3 exemption

- Rule: `FontEmbeddingRule`
- Clause: ISO 19005-2 6.2.11.4.1
- Status: not gated · correction specified in #458

The clause scopes embedding to fonts "used for rendering", and its NOTE 2 exempts a font referenced
solely in text rendering mode 3, which is invisible. veraPDF exempts it too. This library exempts
nothing: `FontEmbeddingRule` enumerates through `PreflightContext.EnumerateUsedFonts()`, and that
set is populated in `ContentStreamUsage` on the `Tf` operator alone, with no reference to the
rendering mode the same pass already tracks.

**Why it is the odd row.** This is not inherited from anywhere. It is the one divergence where this
library is stricter than the standard *and* stricter than the profile at the same time, so
correcting it moves toward both.

**The correct pattern already exists in the tree twice.** `UaFontEmbeddingRule` walks
`usage.TextShows` and requires a positively determined mode other than 3, and it settles the
parse-gap case where the mode could not be determined. `GlyphPresenceRule` carries the same
exemption.

**Practical effect.** A scanned page with an invisible OCR text layer selects a font and draws it in
mode 3, so this rule demands embedding for a font the standard exempts. That is the commonest
mode-3 case in the wild.

Checkable: the code half, yes. The clause and its NOTE rest on the viewer.

## D5 — A correct severity that used to be justified by the tool

- Rule: `A2aContentItemTaggingRule`, via `RuleRegistry`
- Clause: ISO 19005-2 6.7.3.3
- Status: not gated · justification corrected, the check itself still diverges

The clause carries one requirement, that the structure hierarchy be rooted in `StructTreeRoot`, and
one recommendation, that a writer capture it to the finest granularity available. Nothing requires
every content item to be described. This library reports a warning, which is right.

The justification was not. `RuleRegistry` used to say:

> Warning, not error: veraPDF's PDF/A-2a profile implements no equivalent, so an error here would
> contradict the reference implementation.

The severity was derived from tool agreement, and veraPDF was called the reference implementation.
The real reason is that the clause states a recommendation rather than a requirement. The registry
comment and the rule's remarks now say that, and the diagnostic the caller reads no longer names
veraPDF; a conformance message should describe the document.

**What still diverges.** The check itself. veraPDF's PDF/A-2a profile carries no equivalent, so this
library reports something the profile does not. That is a real disagreement and it stays, at warning
severity, on the clause's own terms.

Checkable: the profile, yes — `PDFA-2A.xml` in the veraPDF jar has one rule at 6.7.3.3 and no
content-item rule, which is the divergence itself. The registry comment quoted above is the one this
change replaced, so it is checkable only in the history. The clause is the viewer's.

---

## D6 — A language tag ending in a newline is accepted

- Rules: `A2aLangSyntaxRule`, `UaLangSyntaxRule`
- Clauses: ISO 19005-2 6.7.4 · ISO 14289-1 7.2
- Status: not gated · a false accept, pattern unchanged pending #507

Both rules match `/Lang` against `^[a-zA-Z]{1,8}(-[a-zA-Z0-9]{1,8})*$`, and both name the veraPDF
`CosLang` predicate as a source. The predicate carries the same pattern as a JavaScript regular
expression. The two do not agree.

.NET's `$` matches before a single trailing newline. ECMAScript's `$`, with no `m` flag, asserts
end of input and nothing else. So a `/Lang` value of `en` followed by a line feed is accepted by
these rules and rejected by the predicate they cite. Measured both ways: `IsMatch("en\n")` is
`true` for the compiled, interpreted and non-backtracking engines alike, while `node` evaluating
the quoted predicate returns `false`. Only `\z` in place of `$` refuses it.

Reachable from a real file rather than only in principle. A literal string `/Lang (en\012)`, or a
raw end-of-line inside the parentheses, which ISO 32000-2 7.3.4.2 permits, decodes to exactly that
string, and the decoded value reaches the match with no trimming.

**Direction.** A false accept: a file veraPDF would fail, this library passes. That is the worse
direction of the two, which is why it is recorded here rather than left as a footnote.

Checkable: the two patterns, yes. The predicate's own semantics are a reading of ECMAScript rather
than a veraPDF run — veraPDF was not resolvable on the machine where this was measured, so what was
compared is the predicate as quoted in the rules' own documentation.

---

## Clauses neither implementation checks

These are not disagreements and do not belong in the table above, but they are the reason the table
alone is not enough. A diff between two implementations cannot surface a requirement both of them
omit, because they agree.

Measured against an inventory of ISO 19005-2 clause 6 by number and heading, 19 of its 73
file-scoped clauses are checked by neither this library nor veraPDF. The inventory is
`eng/data/iso19005-2-clauses.yml`, and `eng/clause-coverage.py` reports the split, so a clone that
has a veraPDF jar can recompute the figure. Without one the script says the profiles are unavailable
and reports the 20 clauses no rule cites, rather than silently counting every clause as one veraPDF
does not check.

Seven of the 19 are Level A only, and Level A is the accessibility level this library advertises:

- 6.7.2.1 Tagged PDF, general
- 6.7.3.1 Specification of artefacts
- 6.7.3.2 Word boundaries
- 6.7.5 Alternate descriptions
- 6.7.6 Non-textual annotations
- 6.7.7 Replacement text
- 6.7.8 Expansions of abbreviations and acronyms

Two cautions carry with that number. Entries titled "General" may be introductory rather than
independently checkable, and the detection is deliberately generous, crediting a rule that cites a
parent clause. So 19 is a floor on the coverage gap rather than a count of defects.

One clause sits in both places. **6.6.5, File identifiers**, is checked here and by no veraPDF rule,
so under the current two-directional diff being right registers as a phantom id.

## Adding and removing rows

A row goes in when a disagreement is established against the specification, not when it is suspected.
State which reading established it and whether a clone can confirm it.

Record the verdict, not the argument that produced it. "Matches 6.2.11.4.1" is a verdict; working
through why it matches, clause by clause, would restate the standard through the side door. The
"what the standard requires" sentence in each row above is one sentence per divergence, which is
quotation-scale; a document of them would not be.

A row comes out when the code and the tool agree again, which for the gated rows means after the
per-rule comparison lands and the correction is made. Removing a row is a claim that the divergence
is gone, so it belongs in the same commit as the change that closes it.
