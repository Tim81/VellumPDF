# Encrypted reader fixtures

Generated once with qpdf and checked in, rather than generated at test time. CI installs qpdf from apt on `ubuntu-latest` (11.9.0 at the time of writing, but nothing pins it)
while local development uses 12.3.2; checking the files in makes the corpus
byte-identical everywhere and keeps qpdf out of the test-execution path, so there is no
`GateOnCi` skip hole on the core corpus.

Tracked in #99. Used by the decryption work in #97.

## Baseline

`plaintext-baseline.pdf` is `tests/VellumPdf.Kernel.Tests/GoldenTests.StandardFont_rawBytes.verified.pdf`
normalized through qpdf so that **encryption is the only delta** between it and each fixture:

```sh
qpdf GoldenTests.StandardFont_rawBytes.verified.pdf plaintext-baseline.pdf
```

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

`--allow-weak-crypto` is **required** for the three RC4 rows. Without it qpdf refuses:

> qpdf: refusing to write a file with RC4, a weak cryptographic algorithm

and still creates the output file **at zero bytes**. A check that only asks whether the fixture exists
will not notice. Do not confuse this flag with `--allow-insecure`, which concerns empty owner passwords
on 256-bit encryption.

## What the tests should assert

Two assertions, in this order:

1. **Prove the fixture carries the feature** before trusting any decrypt result. `--show-encryption`
   reports `R`, `P` and the per-stream/string method, but **not** `/V` — for that use
   `qpdf --password=u --show-object=trailer` or `--json`, or read the `/Encrypt` dictionary directly.
   `EncryptedFixtureCorpusTests` pins each fixture by SHA-256 as well as `/V`, `/R` and `/CFM`, because
   `enc-aes-128` and `enc-rc4-128-v4` are both `/V 4 /R 4` and differ only in `/CFM` — swapping them
   is otherwise invisible.
2. **Compare decrypted output to `plaintext-baseline.pdf`.** `qpdf --password=u --decrypt` on all seven
   fixtures reproduces the baseline byte-for-byte **except the second `/ID` array element**, which qpdf
   regenerates on every invocation; the first element is preserved.

   Normalize that whole element — all 32 hex digits, at bytes 1684-1715 of the 1743-byte baseline. Do
   **not** write a known-answer test that tolerates a fixed *number* of differing bytes: two random
   16-byte IDs collide in a byte or two by chance, so the count varies per run (29 to 32 observed) even
   though the region never does.

`enc-256-cleartextmd.pdf` is externally checkable too: `xpacket` appears in its raw bytes and does not
appear in `enc-aes-256-r6.pdf`.

## Known gaps

The matrix above is complete along the `/V`+`/R`+`/CFM` axis. It is deliberately **not** complete along the
structural axis, and #97 will need more than this:

- **Every object is generation 0, and every file is single-revision.** So the corpus cannot exercise the
  coupling that makes #97 depend on #121 — a decryptor that hardcodes generation 0 in the per-object key
  passes all of it. qpdf normalises generations when it rewrites, so these need hand-building.
- **No owner-password-only file**, no empty user password. Worth adding: veraPDF can open those (it tries
  the empty user password) where it refuses a user-password file outright, which is what makes them the
  right shape for #138.
- **No object stream, no cross-reference stream, no incremental update.** The xref-stream case matters
  particularly: cross-reference streams must *not* be decrypted, which is a classic trap.
- **No `/EFF`, no `/StrF` differing from `/StmF`, no `/Crypt` filter entry**, and no non-ASCII password
  exercising R6's SASLprep handling.

Both `--cleartext-metadata` rows are present on purpose. At R5/R6 the flag never enters key derivation —
the file key is random and unwrapped from `/UE`/`/OE` — so only the **R4** row exercises ISO 32000-1
Algorithm 2 step (f), where an unencrypted-metadata file appends `0xFFFFFFFF` to the MD5 and getting it
wrong yields a completely wrong key.
