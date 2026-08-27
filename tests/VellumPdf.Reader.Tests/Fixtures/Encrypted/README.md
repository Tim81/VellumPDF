# Encrypted reader fixtures

Generated once with qpdf and checked in, rather than generated at test time. CI installs qpdf
from apt on `ubuntu-latest` (11.9.0 at the time of writing, but nothing pins it) while local
development uses 12.3.2; checking the files in makes the corpus
byte-identical everywhere and keeps qpdf out of the test-execution path, so there is no
`GateOnCi` skip hole on the core corpus.

Tracked in #99. Used by the decryption work in #97.

## Baseline

`plaintext-baseline.pdf` is `tests/VellumPdf.Kernel.Tests/GoldenTests.StandardFont_rawBytes.verified.pdf`
normalized through qpdf so that **encryption is the only delta** between it and each fixture:

```sh
qpdf GoldenTests.StandardFont_rawBytes.verified.pdf plaintext-baseline.pdf
```

**That command does not reproduce the committed file.** qpdf regenerates the second `/ID` array
element on every invocation, so two consecutive runs differ from each other and from what is checked
in, across that whole 32-byte element. The command records *provenance*; the digest in
`EncryptedFixtureCorpusTests` is what identifies the file.

Regenerating it costs less than it looks like it should. The second `/ID` is the only thing that
changes, and that is exactly what the fixture procedure below already tolerates — so the fixtures
built with the `u`/`o` password pair still decrypt to a regenerated baseline, and do **not** need
rebuilding. Measured: each decrypts to within that one element of a freshly generated baseline.

So the whole procedure is:

1. Regenerate the baseline with the command above.
2. Recompute its digest with `sha256sum plaintext-baseline.pdf` and update the literal in
   `PlaintextBaseline_isPresent_andNotEncrypted`.
3. Re-run `dotnet test` on `VellumPdf.Reader.Tests`.

Rebuilding the fixtures from the new baseline is optional tidiness, and nothing enforces it
either way. Note that nothing checks the decrypt-to-baseline property automatically yet; that arrives
with #97.

## The matrix

Every fixture uses user password `u` and owner password `o`, and covers one row of the standard
security handler support matrix in #97.

| File | qpdf arguments | V | R | Cipher |
| --- | --- | --- | --- | --- |
| `enc-rc4-40.pdf` | `--allow-weak-crypto --encrypt u o 40 --` | 1 | 2 | RC4-40 |
| `enc-rc4-128.pdf` | `--allow-weak-crypto --encrypt u o 128 --use-aes=n --` | 2 | 3 | RC4-128 |
| `enc-rc4-128-v4.pdf` | `--allow-weak-crypto --encrypt u o 128 --force-V4 --use-aes=n --` | 4 | 4 | RC4 via `/CF` `/V2` |
| `enc-aes-128.pdf` | `--encrypt u o 128 --use-aes=y --` | 4 | 4 | AESv2 |
| `enc-aes-256-r5.pdf` | `--encrypt u o 256 --force-R5 --` | 5 | 5 | AESv3 (deprecated R5) |
| `enc-aes-256-r6.pdf` | `--encrypt u o 256 --` | 5 | 6 | AESv3 |
| `enc-aes-128-cleartextmd.pdf` | `--encrypt u o 128 --use-aes=y --cleartext-metadata --` | 4 | 4 | AESv2, metadata in clear |
| `enc-256-cleartextmd.pdf` | `--encrypt u o 256 --cleartext-metadata --` | 5 | 6 | AESv3, metadata in clear |
| `enc-rc4-objstm.pdf` | `--allow-weak-crypto --encrypt u o 128 --force-V4 --use-aes=n -- --object-streams=generate` | 4 | 4 | RC4 via `/CF` `/V2`, object stream + xref stream |
| `enc-aes-128-emptyuser.pdf` | `--encrypt "" o 128 --use-aes=y --` | 4 | 4 | AESv2, EMPTY user password |
| `enc-aes-128-nestedstrings.pdf` | see below | 4 | 4 | AESv2, extra object with a nested array-of-strings |
| `enc-aes-128-longpassword.pdf` | `--encrypt 0123456789abcdefghijklmnopqrstuvwxyzABCD o 128 --use-aes=y --` | 4 | 4 | AESv2, 40-character user password |
| `enc-aes-128-samepassword.pdf` | `--encrypt same same 128 --use-aes=y --` | 4 | 4 | AESv2, one password for both roles |
| `enc-aes-128-pdfdocpassword.pdf` | `--encrypt "pässwörd" o 128 --use-aes=y --` | 4 | 4 | AESv2, non-ASCII password |
| `enc-aes-128-tworevisions.pdf` | see below | 4 | 4 | AESv2, empty user password, two revisions |
| `enc-aes-128-linearized.pdf` | `--encrypt u o 128 --use-aes=y -- --linearize` | 4 | 4 | AESv2, linearized |
| `enc-256-linearized-objstm-cleartextmd.pdf` | `--encrypt u o 256 --cleartext-metadata -- --linearize --object-streams=generate` | 5 | 6 | AESv3, linearized + object streams + metadata in clear |

`enc-rc4-objstm.pdf` covers three gaps at once: an object stream (compressed objects), a
cross-reference stream, and — because its `/Info` dictionary (with `/Title`) is itself a compressed
member — the one fixture that can catch a decryptor that wrongly re-decrypts an object-stream
member individually (ISO 32000-2 §7.5.7). It has to be this row and not an AES one: RC4
double-decryption is silent (XORing an already-plaintext string against a second, wrong keystream
just produces different-looking garbage, no exception), where AES throws on the second pass
regardless of whether the first one was wrong. An AES fixture would pass this particular test for
the wrong reason.

The two linearized rows carry a cross-reference layout nothing else here does. A linearized file
puts its first-page section AHEAD of the body and a second at the end, and `startxref` points at the
front one — so the reader enters at the first section and follows `/Prev` forwards to the last,
the opposite direction from every other row, with `/Encrypt` declared on a trailer it may reach in
either order. There is a hint stream too, which is ordinary encrypted content. Acrobat writes this
layout by default. (`enc-aes-128-tworevisions.pdf` also has two sections, but as an ordinary
incremental update; `enc-rc4-objstm.pdf` has none at all, only cross-reference streams.) The
AES-256 one adds object streams
and `--cleartext-metadata` on top: reaching the catalog decodes an object stream, which asks whether
that stream is the document's metadata before there is a catalog to answer from. No other fixture
puts those three in one file.

`enc-aes-128-nestedstrings.pdf` is not `plaintext-baseline.pdf` unmodified: it adds one extra
top-level object, `<< /Outer << /Strs [ (DirectArrayString) (SecondArrayString) ] >> >>`,
referenced from the catalog as `/CustomTestData`, before encrypting. Built by decompressing
`plaintext-baseline.pdf` with `qpdf --qdf --object-streams=disable`, inserting that object and the
catalog reference as plain text, recompacting with a bare `qpdf in out` pass (which also
renumbers objects — the custom object ends up as object 3), then encrypting the result the same
way as `enc-aes-128.pdf`. Exists to pin ISO 32000-1 §7.6.2 Algorithm 1 step (a): a string nested
two levels deep (array, inside a dictionary, inside the containing indirect object's own
dictionary) must decrypt under THAT indirect object's identity, not the array's position or a
hardcoded generation — `/Info /Title` alone can't catch this, since it's only one level deep.
Nothing in it is comparable to the baseline, not even the page content stream: the `qpdf --qdf`
round-trip that inserted the extra object also rewrote the line endings, so that stream decrypts to
74 bytes with CRLF against the baseline's 69 with LF. That is why the fixture is excluded from
`StandardMatrixFixtures` and has its own test, which reads the nested strings by value instead.

The rows below `enc-aes-128-nestedstrings.pdf` exist because the ones above cannot distinguish
certain behaviours from their opposites: each names a password shape, a revision structure or a
cross-reference layout that no earlier row has.

`enc-aes-128-longpassword.pdf`'s password is 40 characters. Algorithm 2 step (a) pads or truncates
to exactly 32 bytes, and with every other password one character long, moving that truncation point
changes nothing anywhere. This one opens under its first 32 characters and refuses the first 31.

`enc-aes-128-samepassword.pdf` uses `same` for both roles. `EncryptionSetup.TryAuthenticate`
deliberately tries the owner password first, and the reason given is what to report when one
password satisfies both checks — a case no other fixture contains, so the order was unenforced.

`enc-aes-128-pdfdocpassword.pdf` has the user password `pässwörd`. qpdf derives `/U` for an R≤4
document from **PDFDocEncoding** bytes, not UTF-8, so this fixture does not open on the UTF-8
attempt: it is the only one that exercises the PDFDocEncoding retry in
`EncryptionSetup.CandidatePasswordEncodings`. Its R6 counterpart would not — at R≥5 the password is
UTF-8 either way, which is also why the retry is gated on `r <= 4`. Non-ASCII arguments have to
reach qpdf as UTF-8 for this to reproduce; a shell that hands it the local code page instead
produces a different (and differently-passworded) file.

`enc-aes-128-tworevisions.pdf` is an incremental update over an encrypted document, which qpdf
cannot produce — it rewrites whole files. poppler's `pdfattach` appends instead, and takes no
password argument, which is why this row's user password is empty:

```sh
qpdf --encrypt "" o 128 --use-aes=y -- plaintext-baseline.pdf tworev-base.pdf
printf 'attachment payload
' > attach.txt
pdfattach tworev-base.pdf attach.txt enc-aes-128-tworevisions.pdf
```

The result has two `%%EOF` markers, a `/Prev` chain, and `/Encrypt` repeated in the newer trailer,
which is what makes it the only row where revision chaining and decryption meet.

`--allow-weak-crypto` is **required** for the RC4 rows. Without it qpdf refuses:

> qpdf: refusing to write a file with RC4, a weak cryptographic algorithm

and still creates the output file **at zero bytes**. A check that only asks whether the fixture exists
will not notice. Do not confuse this flag with `--allow-insecure`, which concerns empty owner passwords
on 256-bit encryption.

## What the tests should assert

Two assertions, in this order:

1. **Prove the fixture carries the feature** before trusting any decrypt result. `--show-encryption`
   reports `R`, `P` and the per-stream/string method, but **not** `/V`. Nor does
   `--show-object=trailer`: it prints an indirect reference. Use
   `qpdf --password=u --json --json-key=encrypt` to see the dictionary itself. **Not
   `--show-object=8`**: the encryption dictionary is object 8 in the original eight fixtures and
   object 6 or 9 in four of the later ones, and asking for the wrong object number prints some other
   dictionary and exits 0 — a check that reports success while looking at the wrong thing, which is
   the failure this step exists to prevent.
   `EncryptedFixtureCorpusTests` pins each fixture by SHA-256 as well as `/V`, `/R` and `/CFM`, because
   `enc-aes-128` and `enc-rc4-128-v4` are both `/V 4 /R 4` and differ only in `/CFM` — swapping them
   is otherwise invisible.
2. **Compare decrypted output to `plaintext-baseline.pdf`.** `qpdf --password=u --decrypt` reproduces
   the baseline byte-for-byte **except the second `/ID` array element**, which qpdf regenerates on
   every invocation; the first element is preserved.

   `enc-rc4-objstm.pdf` needs `--object-streams=disable` on that command: `--decrypt` alone preserves
   the object streams, so the output differs from the baseline from byte 36 onward and looks broken
   when it is not.

   This works for the eleven rows built from the baseline with the `u`/`o` pair. It does **not** apply
   to the other six, and each for its own reason: `enc-aes-128-emptyuser.pdf` and
   `enc-aes-128-tworevisions.pdf` take an empty user password, `enc-aes-128-longpassword.pdf`,
   `enc-aes-128-samepassword.pdf` and `enc-aes-128-pdfdocpassword.pdf` take their own, and
   `enc-aes-128-nestedstrings.pdf` is not the baseline's object graph at all. The two-revision row
   additionally has a whole extra revision appended, so its decrypted form is 2172 bytes against the
   baseline's 1743.

   Normalize that whole element — all 32 hex digits, at bytes 1684-1715 of the 1743-byte baseline. Do
   **not** write a known-answer test that tolerates a fixed *number* of differing bytes: two random
   16-byte IDs collide in a byte or two by chance, so the count varies from run to run while the
   region never does.

`enc-256-cleartextmd.pdf` is externally checkable too: `xpacket` appears in its raw bytes and does not
appear in `enc-aes-256-r6.pdf`.

## Known gaps

The matrix above is complete along the `/V`+`/R`+`/CFM` axis. It is deliberately **not** complete along the
structural axis. #97 closed most of the structural gaps (the nine rows below the original eight);
what remains:

- **Every fixture is qpdf's output.** This is the largest gap in the corpus and the hardest to
  close: producers differ in exactly the places this code has to decide. A crypt filter `/Length` in
  bytes rather than bits, no `/Length` at all, `/EFF`, a `/StrF` differing from `/StmF`, a non-zero
  generation, the shape of `/Perms` — every one of those is a producer choice, and one producer's
  choices are what the whole corpus tests. Acrobat, Word, LibreOffice and the other .NET PDF
  libraries all encrypt differently, and none is available here. The structural rules they would
  exercise are pinned by hand-built documents in `EncryptionStructureTests` and
  `EncryptionParameterTests` instead, which is not the same as having read one of their files.

- **Every object is generation 0.** qpdf normalises generations when it rewrites, so a fixture
  cannot carry a non-zero one; the coupling that makes #97 depend on #121 is pinned instead by a
  hand-built document in `EncryptedExemptionTests`, whose ciphertext is produced independently of
  this library's own key derivation so that a hardcoded generation 0 cannot cancel out on both
  sides.
- **No owner-password-only file.** `enc-aes-128-emptyuser.pdf` covers the empty-user-password case;
  a file whose owner password differs and whose user password is a NON-empty, deliberately-wrong
  value (so only the owner path can open it at all) is still missing.
- **No attachment that omits `/Type /EmbeddedFile`.** `/EFF` names the crypt filter for embedded
  file streams, and the reader recognises one by that key — which Table 45 makes optional, and which
  poppler's `pdfattach` omits. An attachment without it is read under `/StmF` instead, and that is
  wrong in both directions: with `/StmF /Identity` and `/EFF` naming a real filter — the "encrypt
  only the attachments" arrangement — the raw ciphertext comes back as the file, and with `/StmF`
  naming the filter and `/EFF /Identity` a cleartext attachment is decrypted into noise. Both are
  silent under RC4; AES throws on the second.

  A second, separate disagreement sits underneath this one and applies to attachments that DO carry
  `/Type /EmbeddedFile`: whether a READER should honour `/EFF` at all. Table 20's `/StmF` row says
  every stream but a cross-reference stream and one carrying its own `/Crypt` entry takes `/StmF`,
  while the `/EFF` row addresses conforming WRITERS. qpdf takes the literal reading and routes
  attachments through `/StmF`; this reader honours `/EFF`, which is the reading that lets "encrypt
  only the attachments" mean anything. On a document that sets the two differently the two readers
  disagree in both directions, and no fixture here can settle which is right.

  Identifying an attachment that omits `/Type` positionally, from the `/EF` entries that reference
  it, is a name-tree walk plus the `/AF` and annotation `/FS` file specifications — considerably more than the single
  `/Metadata` lookup `IsDocumentMetadataStream` does, which is why it is deferred rather than
  written alongside it. In practice the two halves of the hazard have not been seen together: the
  producer that omits `/Type` (`pdfattach`) keys its own writing on `/StmF` and never emits `/EFF`,
  so the fallback agrees with it, while a producer that applies `/EFF` has already classified the
  stream as an attachment and writes the `/Type`.

- **No file whose trailer `/ID` is absent or empty.** Every qpdf-written fixture carries one, and
  qpdf cannot be made to omit it. A hand-built document in `EncryptedExemptionTests` covers both
  shapes: Algorithm 2 step (e) appends nothing for them, and the reader tolerates that rather than
  refusing a file qpdf and poppler open.
- **No `/AuthEvent /EFOpen` crypt filter.** The entry is read past entirely; neither value changes
  key derivation for the Standard handler, so nothing depends on it today.

- **No revision carrying its own distinct `/Encrypt` object.** `enc-aes-128-tworevisions.pdf` is a
  genuine incremental update, but both of its trailers point at the same object 8, so "the newest
  trailer's `/Encrypt` is the one authenticated" is still untested in the direction that matters —
  as is a later revision that changes permissions.
- **No `/EFF`, no `/StrF` differing from `/StmF`, no `/Crypt` filter entry naturally present in a
  real qpdf-produced file.** `EncryptedReaderTests` covers the `/Crypt`-filter and absent-`/CF`-entry
  cases with a same-length byte patch on `enc-aes-128.pdf` instead, since qpdf itself never emits
  either shape.
- **No password that SASLprep would change**, and none is coming: the missing fixture would only
  document a limit of the implementation rather than test it. R6 hashes the password after SASLprep
  normalisation (RFC 4013), which this library applies on neither the read nor the write path, so a
  password whose NFC form differs from what the caller typed fails to authenticate against a
  conformant producer's `/U`. `PdfPasswordException`'s documentation says so and names the
  workaround. Passwords that are entirely ASCII — every one in this corpus, plus
  `enc-aes-128-pdfdocpassword.pdf`'s Latin-1 characters, which SASLprep leaves alone — are
  unaffected.

Both `--cleartext-metadata` rows are present on purpose. At R5/R6 the flag never enters key derivation —
the file key is random and unwrapped from `/UE`/`/OE` — so only the **R4** row exercises ISO 32000-1
Algorithm 2 step (f), where an unencrypted-metadata file appends `0xFFFFFFFF` to the MD5 and getting it
wrong yields a completely wrong key.

## Regenerating a fixture

**The commands above do not reproduce the committed bytes, and cannot.** qpdf regenerates the trailer's
second `/ID` on every invocation, and the AES rows additionally use a fresh random IV per string and
stream — so re-running a command yields a file that is cryptographically equivalent and byte-different.
The three RC4 rows differ only in the `/ID`; the AES rows differ from roughly byte 100 to EOF.

That means **the digest table in `EncryptedFixtureCorpusTests` is the source of truth, not the commands.**
Re-running a command to "check" a fixture will fail the guard, and that is working as intended.

To legitimately replace a fixture:

1. Regenerate it with the command from the table above.
2. Confirm it carries what its row claims — `qpdf --password=u --json --json-key=encrypt` for `/V`
   and `/CFM` (the trailer only holds an indirect reference, and the object number it points at is
   not the same in every fixture), and `--show-encryption` for `/R`, the cipher and
   **the permission list**, which is what catches a fixture regenerated with narrowed permissions.
3. Confirm it still decrypts to the baseline: `qpdf --password=u --decrypt <file> out.pdf`, then diff
   against `plaintext-baseline.pdf` and check every difference falls inside the second `/ID` element.
4. Recompute the digests with `sha256sum *.pdf` — it prints one line per fixture plus the
   baseline. The fixtures belong in the `Corpus` table that drives
   `Fixture_isExactlyTheFileItClaimsToBe`; the baseline's is a separate literal in its own test.
   Neither lives in the matrix table above.

Steps 2 and 3 catch different things, and neither covers the other.

Step 3 proves the content is right. It does **not** prove the permissions are: a file regenerated with
`--modify=form` carries `/P -44`, and still decrypts to the baseline with only the `/ID` region
differing. Step 2's `--show-encryption` is what surfaces that, which is why it is listed there.

Step 2 alone is not enough either — a wrong password reproduces every declared field. qpdf refuses to
open such a file outright, so step 3 is where that surfaces.

The digest cannot arbitrate any of this: it is exactly what the person regenerating the fixture
updates. Once #97 lands, its decrypt-to-baseline test becomes step 3 automatically.
