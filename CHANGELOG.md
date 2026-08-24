# Changelog

All notable changes to VellumPdf will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **A committed corpus of encrypted PDFs, one per standard-security-handler `/V`+`/R` combination.**
  Generated once with qpdf and committed rather than shelled out for at test time, so the corpus is
  byte-identical on CI and locally and leaves no silently-skipped gate. A guard test pins each fixture
  by SHA-256 as well as `/V`, `/R` and `/CFM`: qpdf refuses to write RC4 without `--allow-weak-crypto`
  and still leaves a zero-byte file behind, and two fixtures are both `/V 4 /R 4`, differing only in
  the cipher — neither an existence check nor a `/V`+`/R` check would notice either. Groundwork for
  the decrypt side. (#99)

- **Hand-written RC4 and MD5 primitives for the legacy (`/V` 1–2) decryption path.** Internal only —
  no public surface yet, no reader wiring, no security handler; that is a separate change. The BCL
  has never shipped RC4, and its `MD5` type defers to the OS crypto library everywhere except Browser
  WASM, where MD5 is unsupported outright, so decrypting an old PDF under Blazor WASM would otherwise
  be a dead end. `PdfDocument`'s `/ID` generation (ISO 32000-2 §14.4) now goes through this MD5 too,
  in place of the BCL call it used before, so the codebase is actually clear of CA5351 (flags MD5 as
  weak) rather than clear of it only on the new, not-yet-wired decryption path — and Browser WASM
  document writing, not just decryption, no longer depends on a platform MD5 that isn't there. RC4 is
  verified against all three vectors in draft-kaukonen-cipher-arcfour-03 Appendix A, including the
  309-byte vector that runs the keystream past its first 256-byte cycle; MD5 against the full RFC
  1321 §A.5 suite plus a length sweep across the padding and block boundaries, and a differential
  sweep against the BCL. `/ID` itself is pinned by a known-answer test, because nothing else
  pinned it: every golden document sets its own id, and the computed one folds in a millisecond
  timestamp, so no snapshot could cover it. Groundwork for the decrypt side. (#97)

- **The decrypt side of the Standard security handler, covering every `/V`+`/R` combination the
  committed corpus (#99) exercises: `/V` 1/`/R` 2 (RC4-40) through `/V` 5/`/R` 6 (AES-256).**
  Internal only: no public surface, no `PdfReader` wiring, no `/Encrypt` gate; a decrypting reader
  is separate work. Covers Algorithm 2 (file key from a password), Algorithms 4/5 and 7 (verifying
  a user or owner password), Algorithm 2.A and the R6 permission check for `/V` 5, and the
  per-object key that folds in the object's generation number, which is why this had to wait for
  #121. Verified against all eight corpus fixtures: deriving the file key from both the correct and
  a wrong password for each one, and decrypting a real content stream to the exact bytes qpdf's own
  encryption produced — checked against an external tool's output, not only internal consistency.
  The two `/EncryptMetadata false` fixtures pin that Algorithm 2 step (f) shifts the derived key,
  not just `/U`. Most encrypted PDFs
  actually use an empty user password, a case absent from the committed corpus; that case is
  covered instead by independently computed vectors. (#97)

- **Decryption on read: `PdfReader.Open` takes a password and reads encrypted PDFs.** The Standard
  security handler at `/V` 1, 2, 4 and 5 and `/R` 2 through 6 — RC4-40 through RC4-128, AES-128
  (`/AESV2`) and AES-256 (`/AESV3`) — plus the `/Crypt` filter (ISO 32000-2 §7.4.10) and crypt
  filters naming different methods for strings and streams. Strings are decrypted in `Resolve` under
  the identity of the indirect object containing them (ISO 32000-1 §7.6.2, Algorithm 1) and stream
  bodies on the decode path; `ParsedStream.RawBody` still holds the verbatim file bytes, which
  `StreamRule` and `HexStringRule` need for byte offsets and lengths.

  The supplied password is tried as the owner password first and the user password second, so one
  that satisfies both reports the higher-privilege access. A wrong one throws the new
  `PdfPasswordException`. `PdfDocumentReader.Encryption` reports `/V`, `/R`, the stream and string
  ciphers, key length, permissions, `/EncryptMetadata` and which password authenticated — no key
  material, and no `/O`, `/U`, `/OE` or `/UE`.

  What is left in the clear, per the spec: the trailer `/ID`, the `/Encrypt` dictionary's own
  strings, cross-reference streams (§7.5.8.2, body and dictionary alike), streams whose data lives
  in an external file (§7.6.1), the document's metadata stream under `/EncryptMetadata false`
  (Table 20 — a page's or an XObject's metadata is not exempt), and a signature dictionary's
  `/Contents`, which ISO 32000-1 leaves unstated: a signer patches those hex digits into
  already-serialized bytes, so decrypting them would corrupt `/ByteRange` verification. That last
  exemption covers `/Type /Sig`, `/Type /DocTimeStamp`, and a `/Type`-less dictionary carrying a
  `/ByteRange` array with a string `/Contents`, since Table 252 makes `/Type` optional. qpdf agrees
  on the shape that matters most: it leaves a `/Type /Sig` dictionary's `/Contents` byte-identical
  while encrypting that same dictionary's `/Reason`, `/Location` and `/M`, and does so whether or not
  the dictionary is reachable from a signature field. Its rule keys on `/Type /Sig` alone, so it does
  encrypt `/Contents` on the other two shapes exempted here — meaning a document qpdf encrypted after
  signing can still hand back an archive timestamp's ciphertext. This exemption is the reading that
  cannot corrupt a signature, not a claim about what every producer does.

  At `/R` 5 and 6 the permissions come from `/Perms` (ISO 32000-2 Algorithm 13) — the copy sealed
  under the file key — rather than the dictionary's `/P`, which nothing protects at those revisions.

  Verified against the committed corpus (#99): for the nine rows built from the baseline with the
  `u`/`o` password pair, the page content decrypts to the baseline's bytes, `/Info /Title` to its
  exact expected text, and each opens under both passwords. The other rows take their own passwords
  or are not the baseline's object graph, and their own tests say what each pins. Seven fixtures were
  added for this work, covering an empty user password, an object stream with a cross-reference
  stream, nested strings, a 40-character password, one password serving as both roles, a non-ASCII
  password whose `/U` is PDFDocEncoding-derived, and an incremental update over an encrypted
  document. (#97)

- **A committed corpus of PDFs not produced by VellumPdf's own writer.** Test-only; nothing ships.
  Every reader fixture before this one came from VellumPdf's writer, which only ever emits
  generation 0 and never a hybrid-reference file or another producer's object-stream layout — the
  #121 review found three defects that shared exactly that root cause. Sourced from qpdf and
  poppler where a tool can produce the shape, hand-built where none can: qpdf recomputes `/Length`
  on every write, so it cannot produce a `/Length`-mismatched file, and separately its own
  documentation states "We do not support creation of hybrid files." Covers object streams,
  cross-reference streams, linearization, a poppler-produced incremental update, a
  nonzero-generation catalog surviving both a read and a poppler-appended revision, a freed object
  number reused at a bumped generation, and three damaged-file shapes (a truncated tail, an
  out-of-range `startxref`, and a `/Length` that disagrees with the real stream body). One fixture
  pins ISO 32000-2 §7.5.8.4's "hidden object" convention; qpdf is the independent oracle for the
  hidden object itself. A second, related fixture puts the same free-then-redefine shape in a
  single revision, a shape §7.5.8.4's normative sentence doesn't cover — tracked as an open erratum
  in pdf-association/pdf-issues#237, whose discussion so far favours a reading VellumPdf
  deliberately differs from (#206) — documented as pinning VellumPdf's current behavior on a
  contested construct, not a conformance claim. Mutation testing found every mutation the corpus
  could catch also broke a pre-existing synthetic test: it closes a dialect-confidence gap in the
  reader's coverage, not a gap in its logic. (#196)

### Changed

- **`PdfReader.Open` no longer rejects an encrypted document out of hand.** Every prior version threw
  `UnsupportedPdfFeatureException` on `/Encrypt`; it now reads the document, and the cases that
  remain unsupported are narrower: `/Filter /Adobe.PubSec`, `/V 3` (whose algorithm ISO 32000-1
  Table 20 leaves unpublished), and a `/StrF` naming a crypt filter method this library does not
  implement, all at `Open`. An unresolvable `/StmF` fails later, at the first decode, because a
  document whose streams cannot be decrypted still has readable strings.

  Callers that caught `NotSupportedException` to detect an encrypted file need to catch
  `PdfPasswordException` instead — it deliberately does not derive from `NotSupportedException`,
  since a password-protected file is not an unsupported feature.

  `PdfDocumentReader.Dispose` clears the file encryption key, where it used to do nothing, so a
  disposed reader is now unusable: resolving an object on one throws `ObjectDisposedException`
  rather than decrypting against a zeroed key.

  `vellum-preflight` reports a password-protected file as an error line rather than crashing, and
  `PdfPreflight.Validate` reports a document whose streams cannot be decoded as unevaluable instead
  of failing it against whichever clauses its rules happened to be checking. ISO 19005-2 §6.1.3
  forbids `/Encrypt`, which the reader used to enforce by refusing to open such files at all;
  `FileTrailerRule` checks it now. (#97)

- **Dependency versions across the board, none of which change what ships.** PublicApiAnalyzers
  moves to 5.6.0, Microsoft.NET.Test.Sdk to 18.9.0, Verify.XunitV3 to 31.28.0, CsCheck to 4.8.0,
  coverlet.collector to 10.0.1, and Microsoft.SourceLink.GitHub to 10.0.400. Every one is build- or
  test-time only. `System.Security.Cryptography.Pkcs`, the single third-party runtime dependency this
  repository ships, was already current at 10.0.11.

  SourceLink looks like the exception and is not. The SDK has imported it implicitly since .NET 8 and
  steps aside only when `GeneratePathProperty` is set, which this repository does not set — so
  packing at 10.0.400, at 8.0.0, and with the reference deleted yields byte-identical symbols and
  package metadata. Source stepping works, but not because of this version (#202).

  xunit stays on 3.x. 4.0.0 moves to Microsoft.Testing.Platform, which the .NET 10 SDK will not run
  through the VSTest target, so it needs its own migration (#200).

- **`PdfIndirectReference` and `PdfIndirectObject` honour generation, which changes four members
  on surfaces Stable/Shipped since 2.0.0.** `PdfIndirectReference.WriteTo` now emits the real
  generation instead of a hardcoded `0`; `Equals` narrowed, so `new PdfIndirectReference(5)` no
  longer equals a parsed `5 1 R` — a genuine break for anything keying a collection on this type;
  `GetHashCode` returns different (now deterministic, unlike `HashCode.Combine`'s per-process
  salt) values than in 2.0.0; and `PdfIndirectObject.Reference` returns `new(ObjectNumber,
  Generation)` rather than `new(ObjectNumber)`, unobservable unless the object was built through
  the new three-argument constructor. See Fixed, below, for why. (#121)

- **Encrypted documents now emit different `/P` and `/Perms` bytes.** Two reserved bits that
  ISO 32000-2 Table 22 requires set for R >= 3 were always emitted as 0; they are forced on now,
  so a byte-for-byte diff against a document encrypted with an earlier version will show this on
  every encrypted output. Permissions actually granted are unaffected. See Fixed, below, for why.
  (#189)

- **`EncryptMetadata = false` now genuinely leaves the metadata stream unencrypted.** If you
  already set this to false, upgrading changes what your output exposes: the whole XMP packet
  becomes readable without the password — `dc:title`, `dc:creator`, `dc:description`,
  `dc:language`, `xmp:CreatorTool`, `pdf:Producer`, and the creation and modification dates.
  See Fixed, below, for why. (#182)

### Fixed

- **PDFDocEncoding treated 0xA0 as Latin-1 does.** That byte is EURO SIGN in Annex D, not NO-BREAK
  SPACE, so a password containing `€` could not be encoded at all while one containing U+00A0 was
  encoded as a Euro sign. Both are now right: `€` reaches 0xA0, and U+00A0 has no representation, so
  a candidate containing it is dropped rather than silently altered. The code points Annex D marks
  Undefined — 0x7F, 0x9F, 0xAD and two dozen more — keep encoding as themselves: this encoding
  exists to reproduce the bytes a producer hashed, and dropping a candidate over one of them would
  stop a correct password from opening its document. (#97)

- **An indirect `/CFM` turned every stream in a valid document into "unsupported crypt filter".**
  §7.6.1 requires only the encryption dictionary's STRINGS to be direct objects, so a `/CF` entry's
  `/CFM` may be an indirect reference. The dereferenced copy the handler works on covered `/CF` and
  its per-filter dictionaries but stopped one level short of their values, and a filter whose `/CFM`
  read as missing is indistinguishable from one naming a cipher this handler does not implement —
  which is a hard failure on the first stream, after the document has already opened. (#97)

- **An encrypted document whose trailer `/ID` was absent or empty would not open.** Algorithm 2
  step (e) appends `/ID[0]` to the MD5 input, and appending nothing is well defined — the producer
  that omitted the entry hashed the same bytes the reader now does, so the derivation lands on its
  key. Table 15 does require `/ID` alongside `/Encrypt`, but qpdf and poppler both open such a file,
  and refusing it made a document readable everywhere except here. (#97)

- **A stream whose declared `/Length` landed on `)`, `{`, `}` or a lone `>` failed the parse.** The
  parser recovers from a wrong `/Length` by scanning for `endstream`, but the token read that
  detects the mismatch threw on those bytes instead of falling through to the scan. Encryption makes
  it ordinary rather than exotic: ciphertext is high-entropy, so a stale length lands on one of them
  a few percent of the time. (#97)

- **A reference's generation number is honoured instead of discarded.** `PdfIndirectReference`
  carried only an object number, and the parser dropped a parsed `N G R`'s middle field too, so
  every reference read from a document looked like generation 0. With an xref table keyed on
  object number alone, `10 2 R` resolved to whatever object 10 held at generation 0 instead of
  nothing, and a document with a legitimately nonzero generation anywhere — including its own
  `/Root` — either resolved the wrong object or failed to open. (#121)

- **A rewritten object at a nonzero generation now round-trips through `AppendRevision`.** The
  incremental-update writer hardcoded every re-emitted object, including the catalog, to
  generation 0. Long-term-validation and archive-timestamp signing both rewrite the catalog, so
  a base document whose catalog sat at a nonzero generation got a trailer `/Root` that disagreed
  with the object header and xref entry next to it and failed to reopen; rewriting any other
  object at the wrong generation failed the same way but silently, resolving to nothing with no
  exception. `PdfIndirectObject` gains a matching `Generation` and a three-argument constructor
  to carry the real value through. (#121)

- **A freed object number no longer resurfaces from an older revision.** Classic-table `f`
  entries and xref-stream type-0 rows were discarded instead of recorded, so an object deleted
  in the newest revision could still resolve from a stale entry in an older one. Both are now
  tracked, scoped per revision so a hybrid file's `/XRefStm` still resolves an object that an
  earlier section marks free. That pairing is how such a file hides an object from a
  classic-table-only reader: the older section's free entry is what a PDF 1.4 consumer finds,
  while a PDF 1.5 consumer takes the cross-reference stream's entry and ignores it. (#121)

- **A malformed generation field no longer takes down the document, or aliases onto the wrong
  object.** A sloppy but unambiguous field (space-padded rather than zero-padded) still parses.
  One that is genuinely unparseable, or exceeds the ISO 32000-2 §7.5.4 ceiling of 65535, is
  recorded as unknown rather than guessed at 0, so the object's header takes over instead of the
  object going unresolvable at every generation. A reference whose own token is unparseable,
  negative, or exceeds 65535 no longer aborts the document either; it simply matches no real
  xref entry, the same outcome an ordinary mismatch already produces. (#121)

- **An `/Encrypt` entry present only in a hybrid file's `XRefStm` dictionary is no longer
  missed.** The classic-trailer check alone can't see it. ISO 32000-2 §7.5.8.4 permits a
  hybrid-reference producer to put `/Encrypt` on the XRefStm dictionary instead, so such a file
  parsed as if it were plain — producing garbage rather than `UnsupportedPdfFeatureException`.
  (#183)
- **A stream body that happens to contain the literal bytes `endstream` no longer truncates
  there.** The endstream scan took the first occurrence with no check at all, so a binary stream
  (an embedded font subset, a compressed image) that contained those nine bytes lost everything
  past them, silently. It now prefers a candidate whose following bytes look like `endobj` or the
  next object's header, checked independently of whether an EOL precedes the marker — requiring
  the EOL first sent an earlier version of this fix past a real but non-conformant terminator into
  a later object's, silently absorbing everything in between. Falls back to an EOL-preceded match,
  then to the first literal occurrence, so it can never do worse than the naive scan it replaces.
  Bounded per stream so a file with many such streams can't turn recovery into quadratic work.
  (#105)
- **A `startxref` more than 2048 bytes from EOF is found again.** The backward search window was
  too tight for a file padded after `%%EOF` (some producers reserve a byte-range window for a
  signature added later); it's now 1 MiB. The search itself now scans backward from EOF too, so
  its cost tracks how far back the marker actually is instead of paying for the full window on
  every open. (#105)

- **`/P` bits 7-8 are now set, as ISO 32000-2 Table 22 requires for R >= 3.** `PdfPermissions`
  has no flag at `1 << 6` / `1 << 7` — the enum goes straight from `Annotate` to `FillForms` —
  so those two reserved bits were always emitted as 0 regardless of the comment above the code
  claiming otherwise. **This changes the `/P` and `/Perms` bytes emitted for every encrypted
  document** (`/Perms` wraps `/P`, so it moves too); a diff against a document encrypted with an
  earlier version will show this. Permissions actually granted are unaffected. (#189)
- **Encrypting a PDF/UA-1 document no longer fails with a PDF/A error.** The `Save()` guard
  tested `Conformance != PdfConformance.None`, so PDF/UA-1 (ISO 14289-1, which has no rule
  against encryption) was rejected under a message that named ISO 19005-2 §6.3.1, a clause it
  isn't subject to. PDF/UA-1 now has its own check instead: encrypting with permissions that
  omit content extraction (`PdfPermissions.Extract`) is rejected, because ISO 14289-1 §7.16
  requires that assistive technology be able to extract content, and `Save()` would otherwise
  emit a document that fails its own declared conformance by construction. (#188)
- **`/EncryptMetadata false` now actually exempts the metadata stream.** The flag was written
  into the `/Encrypt` dictionary and the `/Perms` block, but nothing stopped the metadata
  stream's own body from being encrypted anyway, contradicting ISO 32000-2 §7.6.2 and the flag
  sitting right next to it. **If you already set this to false, upgrading changes what your
  output exposes**: the whole XMP packet is now genuinely cleartext — `dc:title`, `dc:creator`,
  `dc:description`, `dc:language`, `xmp:CreatorTool`, `pdf:Producer`, and the creation and
  modification dates — where the bug previously encrypted it despite the flag. Leave it at the
  default `true` unless that exposure is a requirement you've weighed. (#182)

## [2.0.0] - 2026-08-17

The first major version since 1.0. Every package moves to 2.0.0 together, as usual.

Two things made a major version necessary: assemblies are strong-named, which changes their
identity, and the analyzer that was supposed to be locking the public API is now actually
locking it, which meant fixing the defects in that surface while doing so was still free.
Most of the rest is work that had to land before the surface froze.

**Read [Upgrading to 2.0](https://github.com/Tim81/VellumPDF#upgrading-to-20) first if you bind to an assembly
identity by hand** — a `PackageReference` needs no change, but a binding redirect, an
`InternalsVisibleTo`, or an `Assembly.Load` string does.

### Breaking changes

#### Assembly identity

- **All eight packages are strong-named** (`eng/VellumPdf.snk`), with public key token
  `b2757187a6d18ae5`. `AssemblyVersion` is pinned to `2.0.0.0` for the whole 2.x line, so
  servicing releases will not force another rebind. (#53)

#### Public API

| Change | Was | Now |
| --- | --- | --- |
| `PdfSignature.ByteRange` | `int[]` | `ReadOnlyMemory<long>` (#178) |
| `PdfLinkAnnotation.Flags` | `int` | `PdfAnnotationFlags` (#176) |
| `TextEncodingWarning` character | `char` | `System.Text.Rune` (#177) |
| `CcittImageLoader.Load` | two overloads, four positional knobs | one overload taking `CcittOptions` (#177) |
| `PdfPreflight.Validate(PdfDocumentReader, PdfConformance)` | public | internal (#176) |
| `HttpRevocationClient(HttpClient, TimeSpan)` | both arguments required | both optional, matching `HttpTimestampClient` (#177) |
| `PdfSignatureSettings.SubFilter` | any string accepted | only `ETSI.CAdES.detached` and `adbe.pkcs7.detached` (#176) |
| `SignaturePlaceholderOptions.SubFilter` | any string accepted | the same two values |

Each is explained under Added, Changed, or Fixed below.

#### Behaviour

- **A PAdES signature no longer carries a CMS `signing-time` signed attribute.** ETSI
  EN 319 142-1 admits only the signed attributes its table 1 lists, and `signing-time` is not
  among them — PAdES conveys the claimed time in the signature dictionary's `/M`, which this
  library already wrote from the same value. Emitting it anyway held every signature at
  PAdES-BES instead of PAdES-BASELINE-B. Code reading `signing-time` out of `SignerInfo` on a
  signature written with the default `/SubFilter ETSI.CAdES.detached` will no longer find it;
  `/M` still carries the value, and `adbe.pkcs7.detached` keeps the attribute, since it makes
  no ETSI claim. (#170)
- **A tagged document with no tagged content now emits `/StructTreeRoot`.** Setting
  `Tagged = true` and drawing nothing previously produced no structure tree at all, which
  failed PDF/A-2a and PDF/UA-1 validation. `Tagged` now means tagged. (#120)
- **A certificate with a non-minimally-encoded serial is rejected up front** on the
  in-process signing paths, with a message naming the offending bytes and the way forward,
  instead of an opaque `ArgumentException` raised from inside the BCL's CMS encoder. The
  exception type is unchanged, so a `catch (ArgumentException)` behaves as before. (#167)
- **`vellum-preflight --format json` reports check accounting differently.** `summary.total`
  used to be `failed + passed + notEvaluated`, adding a count of assertions to a count of
  checks; it is now the profile's catalog size. `summary.failedChecks`, `summary.inconclusive`,
  and the matching `failedChecks` and `inconclusive` arrays are new, and the text output gains
  an `INCONCLUSIVE` line. Exit codes and the conformance verdict are unchanged.
- **A `null` options argument now means "use the default"** rather than throwing, on
  `CcittImageLoader.Load` and the `HttpRevocationClient` constructor. With `= null` defaults
  there is no way to tell an omitted argument from an explicitly null one, so the old
  `ArgumentNullException` fired on exactly the call the default exists to serve. (#177)
- **External-signer CMS digest `AlgorithmIdentifier`s now match RFC 5754** — both
  `SignedData.digestAlgorithms` and `SignerInfo.digestAlgorithm` omit their parameters field
  instead of carrying a redundant DER NULL, per RFC 5754 §2 ("implementations MUST generate
  SHA2 AlgorithmIdentifiers with absent parameters"). `SignerInfo.signatureAlgorithm` was
  already correct and is unchanged. Neither change touches the signature value:
  AlgorithmIdentifiers sit outside the SignedAttrs digest. (#166)

### Added

- **Async I/O surface for `Save`, `Sign`, and `LoadTrueTypeFont`** — `PdfDocument.SaveAsync`,
  `Document.SaveAsync(Stream)` / `SaveAsync(string)`, `Document.LoadTrueTypeFontAsync`, and
  `SigningExtensions.SignAsync` (both overloads), each taking a `CancellationToken`. Existing
  sync methods are unchanged. `ITimestampClient` and `IRevocationClient` gain default-implemented
  `GetTimestampTokenAsync`/`GetRevocationDataAsync` members, so custom implementations keep
  compiling unchanged. (#54)
- **`IExternalSigner`** — a two-phase async external-signer API for a cloud KMS or remote HSM
  where the signing call itself is a network round-trip (Azure Key Vault, AWS KMS, GCP KMS). No
  BCL API supports this today, since `CmsSigner` only accepts a synchronous, in-process private
  key; VellumPdf computes the CMS signed-attributes digest itself, hands it to the caller's async
  signer, and assembles the resulting `SignerInfo` by hand. Set `PdfSignatureSettings.ExternalSigner`
  and sign with `SignAsync`; the synchronous `Sign` overloads throw, since there is no synchronous
  way to bridge a network call. `EcdsaSignatureConverter` is included for KMS providers, such as
  Azure Key Vault, that return a raw ECDSA signature rather than the DER encoding CMS requires. (#165)
- **`PdfSignatureSettings.ExternalPrivateKey`** — signs with a private key supplied separately
  from `Certificate`, for HSM/PKCS#11/cloud-KMS-backed certificates whose key isn't attached to
  the `X509Certificate2` (Azure Key Vault, AWS KMS, `Pkcs11Interop.X509Store`, and similar).
  Windows CNG-integrated smart cards and hardware tokens already work through the existing
  `Certificate`-only path and need no change. (#54)
- **The ESS `signing-certificate-v2` signed attribute** (RFC 5035) is now emitted on both
  signing paths, so a signature written with the default `/SubFilter ETSI.CAdES.detached`
  carries the attribute the ETSI profile expects rather than only claiming to. `hashAlgorithm`
  is omitted for SHA-256 (the DER `DEFAULT` rule) and written with absent parameters for
  SHA-384/512; `issuerSerial` is built from the same bytes as the `SignerInfo`, so the two
  cannot disagree. (#168)

  Together with the `signing-time` removal below, this is what makes the `/SubFilter` claim
  true rather than merely asserted. Measured with the EU DSS reference validator, against
  fixtures differing only in the code that signed them:

  | Signature | 1.11.0 | 2.0.0 |
  | --- | --- | --- |
  | B-B, `ETSI.CAdES.detached` | `PDF-NOT-ETSI` | **`PAdES-BASELINE-B`** |
  | B-T, with an RFC 3161 timestamp | `PAdES-BES` | **`PAdES-BASELINE-T`** |
  | `adbe.pkcs7.detached` | `PKCS7-B` | `PKCS7-B` (unchanged) |

  Either change alone reaches only `PAdES-BES`. (#168, #170)
- **A PDF/A-2a check for page content that no structure element describes** — reported at
  ISO 19005-2 clause 6.7.3.3, at Warning severity, since veraPDF's own PDF/A-2a profile
  implements no equivalent rule and the verdict must keep matching it. (#120)
- **`PdfAnnotationFlags`** — the ISO 32000-1 Table 165 annotation bitfield as an enum, so the
  §6.3.2 PDF/A requirement can be written as `PdfAnnotationFlags.Print` rather than `4`.
  Emitted bytes are unchanged, and a test pins that. (#176)
- **`SubFilterEtsiCAdESDetached` and `SubFilterAdbePkcs7Detached` constants** on both
  `PdfSignatureSettings` and `SignaturePlaceholderOptions`, so the two accepted values need not
  be hardcoded. (#176)

### Changed

- **`VellumPdf.Conformance` graduates from Preview to Stable.** `VellumPdf.Cli` was already
  Stable while the engine it wraps was Preview. veraPDF parity is about 99% across
  PDF/A-2b/2u/2a and PDF/UA-1, both paths of every rule are cross-validated against it in CI,
  and the remaining gaps are tracked as issues — better stated as known issues on a stable
  package than as a preview label on the whole engine. (#173)
- **The public API surface is now under the analyzer gate the README describes.** That README
  has claimed "the public API is locked (analyzer-enforced)" since 1.0, but
  `VellumPdf.Signing`'s `PublicAPI.Shipped.txt` was a zero-byte file and Kernel's had not been
  touched since 1.2.0, so 232 entries across five
  packages sat where the analyzer permits silent removal. They are recorded now, and every
  `PublicAPI.Unshipped.txt` is reset to its header, so a 2.x addition shows as a diff against
  an accurate baseline. `VellumPdf.Reader` is deliberately left Unshipped: it stays Preview
  through the v2.1 structural-reader work, and the convention is that a surface moves at
  graduation, not before. (#173)
- **The synchronous timestamp and revocation clients no longer block on an async call.**
  `HttpTimestampClient` and `HttpRevocationClient` already issued the request through
  `HttpClient.Send`; only the response body was read by blocking on `ReadAsByteArrayAsync`,
  which deadlocks on a synchronization context and starves the thread pool under load. That is
  `HttpContent.ReadAsStream` now, which is genuinely synchronous. No `GetAwaiter().GetResult()`
  remains anywhere in `src/`. The synchronous interface members stay: they are the required
  ones while the async counterparts are default-implemented, so removing them would break every
  existing implementation. (#177)
- **`System.Security.Cryptography.Pkcs` moves to 10.0.11**, matching the .NET 10 servicing band.

### Fixed

- **`/ByteRange` offsets are no longer truncated to `int`.** `PdfSignature.ByteRange` was
  `int[]` filled from a `long` through an unchecked cast, so an author-controlled offset past
  `int.MaxValue` wrapped silently — 4,294,967,296 became 0, which made `CheckByteRange` return
  early and skip the ISO 19005-2 §6.4.3-1 coverage check entirely. The same truncating parse
  existed in two independent places, and both are fixed. (#178)
- **A CRL that revokes the signing certificate is no longer treated as valid** when that
  certificate's serial is non-minimally encoded. The comparison held the certificate's raw
  serial bytes against the CRL's, which are always minimal because a real CA's CRL is DER, so
  it never matched and the revoking CRL was embedded in the `/DSS`. Both sides are normalized
  now. (#167)
- **A Warning no longer withdraws a passing claim** in the preflight report, and a failing rule
  now blames the check it names rather than every catalogued check sharing its clause number.
  Withdrawn checks used to appear in no section of the report at all.
- **`vellum-preflight` prints its findings when there are findings**, rather than only when the
  verdict is FAIL. `--fail-on warning` used to fail a run while listing nothing, and
  `--severity warning` listed nothing on a conformant document whose own header said a warning
  existed.
- **`/StructParents` keys no longer wrap into each other's key space** — the key and the
  `/Nums` entries are range-checked.
- **Long-term validation no longer reuses a live object number** when a document's `/Size` is
  smaller than the highest object number actually present.
- **An astral character is reported once, as itself**, in `TextEncodingWarning`. It used to be
  reported twice, as its two UTF-16 surrogate halves, with a code point in 0xD800–0xDFFF — a
  value no Unicode character has. An unpaired surrogate is reported as U+FFFD. Emitted bytes are
  deliberately unchanged: an astral character still writes two `?` bytes, because collapsing it
  to one would shift text a caller had already measured. (#177)
- **Decode-to-raster on a Group 3 1-D CCITT stream with byte-aligned rows is reachable** — the
  `ImageLoadOptions` overload could not carry the CCITT knobs at all, so that combination had no
  public expression. (#177)
- **Clearer `ExternalSignerCms` failure messages** — a `CheckSignature` failure now names
  RSASSA-PSS (unsupported — see `IExternalSigner`'s documentation) and a KMS key id pointing at
  the wrong key as likely causes, rather than pointing only at signature format. (#167)

## [1.11.0] - 2026-07-11

### Added

- **QR Kanji mode** (ISO/IEC 18004 §7.4.6) — `QrCode` now packs Shift-JIS X 0208 characters at a
  fixed 13 bits each instead of falling back to byte mode, shrinking the symbol for Japanese
  content. No API change: the segmenter chooses Kanji mode automatically wherever it beats the
  alternatives. The Unicode-to-Shift-JIS table is generated clean-room from the Unicode
  Consortium's SHIFTJIS.TXT mapping and filtered to code points that round-trip through a CP932
  decoder, so the encoder always agrees with what a real decoder reads back. (#155)
- **Compact (Truncated) PDF417** (ISO/IEC 15438) — a new `Pdf417Barcode.Compact` property drops
  the right row-indicator column and replaces the stop pattern with a single dark module,
  narrowing the symbol at the cost of the error-correction redundancy those dropped elements
  normally provide near the right edge. (#155)
- **QR Structured Append** (ISO/IEC 18004 §8) — `QrCode.StructuredAppend(...)` splits a message
  across up to 16 linked QR Code symbols, each stamped with the shared sequence/parity header a
  reading application needs to reassemble them. An explicit-parts overload takes a pre-split
  `IReadOnlyList<string>`; an auto-split overload takes a single string plus a symbol count. (#155)
- **Macro PDF417** (ISO/IEC 15438 Annex H) — `Pdf417Barcode.MacroSet(...)` splits a larger payload
  across linked PDF417 symbols, each carrying a Macro control block (file id and segment index,
  plus optional fields — file name, timestamp, sender, addressee, file size, checksum — via
  `MacroPdf417Options`) appended after its data codewords. (#155)
- **Code 128 FNC4 / extended Latin-1** (ISO/IEC 15417) — plain `Code128Barcode` now accepts the
  full Latin-1 range (code points 0-255) instead of throwing above 127. A lone extended character
  is reached with a single FNC4, and a run of two or more latches FNC4 with a doubled FNC4 until a
  second doubled FNC4 switches it back off. No API change: GS1-128 (`Gs1 = true`) still rejects
  any character above 127, since the GS1 General Specifications disallow FNC4 in a GS1-128
  symbol. (#155)
- All eight packages republish at **1.11.0** in lockstep, even though only `VellumPdf.Barcodes`
  changed — the repository versions every package together rather than independently.

## [1.10.0] - 2026-07-07

### Added

- **`VellumPdf.Barcodes` graduates to Stable.** Its public API surface moves from
  `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt` in full, and the package-family status
  table now lists it as Stable rather than Preview. Five new symbologies land alongside the
  graduation, each implemented clean-room from its governing ISO/IEC standard and, where the
  standard is patented, the original patent; a reference decoder (zxing-cpp) cross-checks every
  symbology's round-trip in CI, including the Aztec placement geometry (see
  [docs/barcodes-roadmap.md](docs/barcodes-roadmap.md) for the provenance record):
  - **Data Matrix and GS1 Data Matrix** (ISO/IEC 16022 ECC 200) — square and rectangular symbols
    with automatic size selection across five content-compaction modes. (#151)
  - **GS1-mode QR, including GS1 Digital Link** — `QrCode.Gs1` emits an FNC1-prefixed GS1 element
    string or a Digital Link URI inside a standard QR symbol. (#152)
  - **Aztec Code** (ISO/IEC 24778) — compact and full-range sizes with automatic size selection
    and a configurable error-correction percentage. (#153)
  - **Code 39, including Full ASCII** (ISO/IEC 16388) — the self-checking symbology long used in
    logistics and defense, with an optional check digit. (#154)
  - **UPC-E and GS1-128 parenthesized-AI human-readable text** — the zero-suppressed 6-digit form
    of UPC-A, and application-identifier-bracketed HRI for GS1-128. These are the two #155
    completeness-backlog items with the most real-world demand; QR Kanji mode, QR Structured
    Append, Compact/Macro PDF417, and Code 128 FNC4 remain deferred. (#155)
- All eight packages republish at **1.10.0** in lockstep, even though only `VellumPdf.Barcodes`
  changed — the repository versions every package together rather than independently.

## [1.9.0] - 2026-07-06

### Added

- **`VellumPdf.Barcodes` — QR, Micro QR, PDF417, Code 128/GS1-128, EAN-13/EAN-8/UPC-A with
  EAN-2/EAN-5 add-ons, and ITF-14.** A new optional package renders six barcode symbologies as
  vector rectangles, never a raster image, over two API tiers: a low-level `PdfCanvas.DrawBarcode`
  extension for precise placement, and a `Document.Add(Barcode)` flow element that handles sizing,
  pagination, alignment, and tagging automatically. QR Code chooses its version, data mask, and
  error-correction level automatically (all overridable) and supports an Auto/Latin-1/UTF-8/UTF-8+ECI
  charset policy for non-Latin-1 content; PDF417 compacts text, byte, and numeric content
  automatically. Every symbology is implemented clean-room, sourced solely from open specifications
  (ISO/IEC 18004, ISO/IEC 15438, ISO/IEC 15417, the GS1 General Specifications). Round-trip decoding
  is verified in CI for every symbology against zxing-cpp, which decodes each generated PDF after
  rasterising it with `pdftoppm`. See [docs/barcodes-guide.md](docs/barcodes-guide.md).
- **`Document.Add(IRenderer)`.** A new public overload on `VellumPdf.Layout.Document` accepts any
  `IRenderer`, opening the flow-layout pipeline to custom elements beyond the ones VellumPdf ships —
  `VellumPdf.Barcodes`' `BarcodeRenderer` is the first to use it.
- **`PdfCanvas.TextEncodingWarnings` and `Document.TextEncodingWarnings`.** `ShowText` now reports
  any character it could not represent in WinAnsiEncoding as a `TextEncodingWarning` (the character
  and its code point); `Document.TextEncodingWarnings` aggregates these across every page after
  `Save`. The character itself is still written as `?` in the PDF, matching the prior fallback; this
  just makes the substitution visible to callers instead of silent.

### Fixed

- **`ShowText` on Standard-14 fonts mangled `°`, `•`, `–`/`—`, and accented Latin-1 characters.**
  `PdfCanvas.ShowText` encoded strings with Latin-1, and the Standard-14 font dictionary declared no
  `/Encoding`, so viewers fell back to the font's built-in StandardEncoding. Under StandardEncoding,
  `°` (0xB0) is undefined and disappears, and bytes 0x80–0xFF map to different glyphs than WinAnsi:
  `é` rendered as `Ø`, for example. `•`, en dash, and em dash sit above U+00FF and collapsed to `?`
  under Latin-1 regardless of the font's encoding. `PdfFontResource.BuildDictionary` now declares
  `/Encoding /WinAnsiEncoding` for the 12 non-symbolic Standard-14 fonts (Symbol and ZapfDingbats
  keep their built-in symbolic encoding), `ShowText` encodes against WinAnsi instead of Latin-1, and
  `Standard14Metrics` fills in the advance widths for the 0x80–0x9F block so justified and aligned
  text using these characters measures correctly.
- **`Standard14Metrics` advance widths were built for the wrong encoding.** The six proportional
  Standard-14 tables (Helvetica, Helvetica-Bold, Times-Roman, Times-Bold, Times-Italic,
  Times-BoldItalic) carried Adobe StandardEncoding widths, not WinAnsiEncoding widths, so most of
  0xA0–0xFF and the ASCII `'`/`` ` `` codes measured the wrong glyph — for example Times-Roman `é`
  read as 278 (the width of `i`) instead of 444, and `Æ` read as 556 instead of 889. The
  Times-Bold, Times-Italic, and Times-BoldItalic arrays were also two elements short, so `þ` and
  `ÿ` fell off the end and measured 0 in those three faces. Justified and aligned text using
  accented Latin-1 characters, symbols, or `þ`/`ÿ` was positioned incorrectly as a result. The
  tables are now generated from the Adobe Core-14 AFM data mapped through WinAnsiEncoding, matching
  the `/Encoding /WinAnsiEncoding` fix above.
- **AcroForm text-field and push-button appearances now render WinAnsi correctly.**
  `AcroFormBuilder.EscapePdfString` threw for any field value or caption character above U+00FF,
  so a `•`, en dash, em dash, ellipsis, or curly quote in a text field value or push-button
  caption threw instead of rendering, even though the field's `/Helv` font dictionary already
  declares `/Encoding /WinAnsiEncoding` (the fix above) and can render it. `EscapePdfString` now
  maps that punctuation through `WinAnsiEncoding` instead of throwing, alongside the accented
  Latin-1 characters (`é`) it already handled; a character genuinely outside WinAnsi still
  throws, since rendering it needs an embedded font. ZapfDingbats checkbox (`(4)`) and
  radio-button (`(l)`) appearances are unaffected — they never call `EscapePdfString`.

## [1.8.2] - 2026-07-05

### Fixed

- **Standard-14 font dictionaries are now written as indirect objects.** Pages previously embedded
  each Standard-14 font dictionary directly in their `/Resources /Font` entry. That is legal
  under ISO 32000, but poppler-based tools (`pdffonts`, `pdftoppm`, Evince, Okular) track fonts by their
  indirect object reference and logged `Internal Error: xref num … not found` before falling back
  to cross-reference reconstruction on every such file. Font dictionaries are now allocated once
  per face as document-level indirect objects and shared by every page that uses them, which also
  removes the per-page duplication in multi-page documents.

### Changed

- Workflow actions updated to their current major versions: `actions/checkout` v7 and the GitHub
  Pages pair `actions/upload-pages-artifact` v5 / `actions/deploy-pages` v5 (bumped together).

## [1.8.1] - 2026-07-03

### Added

- **Linearization now handles outlines and AcroForm fields.** `Linearize` no longer rejects
  documents with a document outline (`AddOutlineEntry`) or interactive form fields (`AddTextField`,
  `AddCheckBox`, `AddRadioButtonGroup`). Outlines get a page-offset outline hint table
  (ISO 32000-2 §F.3.4) with the outline group written into the first page's section; form-field
  widgets and their appearance streams are placed with the document-level objects the way qpdf
  expects, so a page's recomputed object count and first-page end offset match the hint table.
  Output is verified against `qpdf --show-linearization` (no warnings) for text fields, check boxes,
  a radio group split across pages, an outline, and an outline-plus-form document. This resolves the
  v1.8.0 limitation tracked in #145 and #146.

## [1.8.0] - 2026-07-03

### Added

- **Linearization ("fast web view").** A new opt-in `PdfDocument.Linearize` property re-orders the
  output so a viewer can render the first page before the whole file downloads: the linearization
  parameter dictionary, a first-page cross-reference section, a primary hint stream (page-offset and
  shared-object hint tables), the first page's objects, then the remaining objects grouped per page,
  and the main cross-reference section. It defaults off, so existing byte output is unchanged. Output
  is verified against `qpdf --check` and `qpdf --show-linearization` (no warnings) for text, images,
  embedded fonts, cross-page links, and tagged PDFs, and round-trips through `VellumPdf.Reader`.
  Linearization uses the classic cross-reference table only; it cannot be combined with
  `UseObjectStreams` or `Encrypt()`, and the signing path ignores it. Documents with outlines or
  AcroForm fields are rejected for now (their hint tables need extra work — tracked in #145 and #146).

## [1.7.8] - 2026-07-02

### Added

- **`vellum-preflight` — a native command-line PDF/A and PDF/UA validator.** A new `VellumPdf.Cli`
  validates a PDF against PDF/A-2b/2u/2a and PDF/UA-1 with no JVM or Docker, over the in-process
  `VellumPdf.Conformance` engine. It ships two ways: a cross-platform .NET tool
  (`dotnet tool install -g VellumPdf.Cli`) and self-contained native binaries on the GitHub Release
  (Windows x64/Arm64, macOS Arm64, Linux x64). `vellum-preflight invoice.pdf` checks the file against
  the conformance level its XMP claims and reports, for every file, what failed (rule id, ISO clause,
  reason, and offending object), what passed, and what was not fully evaluated — so a clean result is
  never mistaken for an absolute guarantee. Text, JSON, and SARIF 2.1.0 output; file, glob, directory
  (`--recurse`), and stdin inputs; `--coverage` prints the exact implemented/partial/deferred check
  tally; exit codes are CI-friendly (`0` conformant, `1` non-conformant, `2` usage or I/O error). The
  tool is Native-AOT clean, guarded on every build by the AOT smoke test.

## [1.7.7] - 2026-07-02

### Added

- **`VellumPdf.Conformance` — near-complete PDF/A-2 and PDF/UA-1 coverage (~99%).**
  Build-verified veraPDF parity rises to about **99%** for PDF/A-2b/2u/2a and PDF/UA-1 (from ~92%
  and ~90%). Every rule is authored clean-room from the ISO text and cross-validated against veraPDF
  1.30.2. Only five checks are not fully covered, each tracked in a follow-up issue: three are
  partial — their common Identity/embedded-CMap paths are implemented and verified, but the
  predefined-CJK-CMap sub-condition can't be cross-validated clean-room without a conformant CJK font
  asset (6.1.13-10, 6.2.11.3.1-1, 7.21.3.1-1); two need a subsystem outside this release (6.8-5 a
  PDF/A-1 profile, 7.16-1 reader decryption). Nothing is left silently missing — the coverage catalog
  pins the exact partial/out-of-scope set and asserts no check is merely deferred.
  - **Fonts.** Glyph presence and advance width are now checked across every embedded font path —
    CIDFontType2 with a stream `/CIDToGIDMap`, `Type0` with an embedded non-Identity CMap, simple
    non-symbolic TrueType, CIDFontType0/CFF (a Type2 charstring width interpreter with Private-DICT
    widths and FDSelect/FDArray), and simple Type1 (charstring `hsbw`/`sbw`). Widths are FontMatrix-
    scaled to text space; an unresolvable width is skipped rather than misreported.
  - **Colour.** Device colour reached through an image `/ColorSpace`, a pattern space, or a colour
    space's `/Alternate` now requires an output intent (§6.2.4.3); Separation consistency walks image
    and alternate spaces (§6.2.4.4-2); inherited `/Pattern` and `/Properties` resource names are
    detected (§6.2.2-2).
  - **Metadata, structure, signatures.** Every metadata stream is validated, not just the catalog
    (§6.6.2.1-4); extension-schema `valueType` names must be defined (§6.6.2.3.3-8/-17); the output-
    intent `DestOutputProfile` ICC device class is checked (§6.2.3-1); structure-element type names
    are validated at arbitrary depth (§6.1.8-1) and must be defined types (§6.7.2.2-1); the `endobj`
    end-of-line and signature `ByteRange` under-coverage (against incremental-update revisions) are
    checked (§6.1.9-1, §6.4.3-1).
  - **PDF/UA-1.** The pdfuaid identification properties must use the `pdfuaid` prefix (§5-3/-4/-5);
    media-clip data dictionaries require `/CT` and `/Alt` (§7.18.6.2); form fields require `/TU` or a
    widget `/Alt` (§7.18.1-3); role-less `Form` structure elements must hold a single object reference
    (§7.18.4-2).
  - `VellumPdf.Reader` now surfaces object end-offsets and incremental-update revision boundaries
    (internal), which the layout/signature checks above build on.
- **Aligned text on the canvas.** `PdfCanvas.ShowTextAligned(text, x, y, align)` draws a Latin-1
  string so that `x` is the alignment edge — left edge, midpoint, or right edge for the
  `TextAlignment` value. The width is measured from the Standard-14 font set with `SetFont`, using
  the same metrics the layout engine renders with, so a right-aligned line ends exactly at `x`.
  For embedded fonts, `ShowGlyphsAligned(glyphIds, measuredWidth, x, y, align)` takes a width from
  `EmbeddedFontHandle.MeasureString` and positions the glyph run the same way. Both must be called
  between `BeginText` and `EndText`; `ShowTextAligned` throws if no measurable font is set.

## [1.7.6] - 2026-06-28

### Added

- **Arc drawing primitive.** `PdfCanvas.AppendArc(cx, cy, radius, startAngle, endAngle)` appends a
  circular arc to the current path, approximated by cubic Bézier segments (one per 90° or less).
  Angles are in radians, counter-clockwise from the +X axis in PDF space; a sweep where the end
  angle is below the start runs clockwise. It is append-only and emits no `m`, so the caller
  positions the current point at the arc start.
- **`PieChart` layout element.** Draws a pie chart as a set of filled wedges. Each `PieSlice`
  carries a value, a fill colour, and an optional label. The chart exposes a configurable start
  angle and sweep direction (clockwise by default), diameter, margins, horizontal alignment, and
  an optional per-wedge separator stroke. A lone slice is drawn as a full circle rather than a
  360° wedge, so it has no radial seam. In a tagged document the chart is written as a `/Figure`
  with alternate text, composed from the slice labels or set explicitly via `AltText`; set
  `Decorative` to mark it as an artifact instead when the data is already in accessible text
  nearby. Add it with `Document.Add(PieChart)`.

## [1.7.5] - 2026-06-23

### Added

- **`VellumPdf.Conformance` — PDF/A-2 coverage deepened across non-page content streams and XMP.**
  Build-verified veraPDF parity rises to **~92% for PDF/A-2b/2u/2a** (up from ~90%); PDF/UA-1 stays
  at **~90%**. Every rule is authored clean-room from the ISO text and cross-validated against
  veraPDF 1.30.2 in CI; adversarial false-positive sweeps across the new and changed rules found no
  over-rejections.
  - **A reusable reachable-content-stream collector** walks the non-page content streams reachable
    from each page — drawn Form XObjects (recursively, cycle-guarded), the glyph procedures of every
    Type 3 font selected by a `Tf` operator, and annotation `/AP /N` appearance streams (including
    keyed appearance sub-dictionaries). Its reachability policy is pinned empirically to veraPDF.
  - **Now applied to non-page streams** (previously page-content only): the ISO 32000-1 operator
    allowlist (§6.2.2-1) and inline-image filter check (§6.1.10-1) — both now **fully implemented**;
    plus the overprint/OPM check (§6.2.4.2-2), Separation-consistency check (§6.2.4.4-2), and the
    inherited-resource check (§6.2.2-2).
  - **PDF/A-2a logical structure:** the document structure tree presence check (§6.7.3.3-1) is now
    implemented.
  - **XMP extension schema (§6.6.2.3.3):** the PDF/A extension-schema containers are now validated
    for RDF container type and namespace prefix — `pdfaExtension:schemas` must be an `rdf:Bag`
    (§6.6.2.3.3-1), and the `property`, `valueType`, and `field` containers must be `rdf:Seq`
    (§6.6.2.3.3-5/-6/-15), honoring the XMP default-namespace (null-prefix) form.

### Fixed

- **`VellumPdf.Conformance` — two false positives eliminated** (a conformance rule must never reject
  a file veraPDF accepts):
  - **§6.2.4.3 (device colour spaces).** The check rejected any device colour used without a PDF/A
    output intent, ignoring `/Default*` colour spaces entirely — so a file using DeviceRGB with a
    `/DefaultRGB` colour space (and no output intent) was wrongly rejected. It now follows veraPDF's
    per-type semantics: DeviceRGB is satisfied by `/DefaultRGB` or an RGB output-intent profile,
    DeviceCMYK by `/DefaultCMYK` or a CMYK profile, and DeviceGray by `/DefaultGray` or any output
    intent.
  - **§6.2.2-2 (inherited resources).** A page lacking its own `/Resources` was flagged for *every*
    resource name it used, even names that are undefined in the inheritance chain (which veraPDF
    accepts). It now flags only names that are actually defined in the inherited resource scope.

## [1.7.4] - 2026-06-23

### Added

- **`VellumPdf.Conformance` — PDF/UA-1 and PDF/A-2a conformance deepened.** Build-verified veraPDF
  parity rises to **~90% for PDF/UA-1** (95 of 106 checks implemented, 1 partial — up from ~71%) and
  **~90% for PDF/A-2a** (up from ~87%). Every rule is authored clean-room from the ISO text and
  cross-validated against veraPDF 1.30.2 in CI; two adversarial false-positive sweeps (content-stream
  /marked-content rules and the new font rules, plus an earlier structure-rule sweep) found no
  over-rejections.
  - **A marked-content interpreter** over page content streams: BMC/BDC/EMC nesting, marked-content
    tags and properties, MCID resolution for both the inline (`/Tag << /MCID n >>`) and
    named-reference (`/Tag /Name` via `/Resources /Properties`) forms, artifact ancestry, and a
    content-item model per real-content operator. It underpins the §7.1 and §7.2 marked-content rules.
  - **Logical structure (PDF/UA-1):** table grid — cell intersection and row/column spans
    (§7.2-15/-41/-42/-43); heading nesting level (§7.4.2-1); table cell connected-header (§7.5-1/-2);
    non-standard structure types must role-map to a standard type (§7.1-5).
  - **Marked content (PDF/UA-1):** real content must be tagged or marked as Artifact, and the
    artifact/tagged-content nesting rules (§7.1-1/-2/-3); marked-content and outline natural-language
    determination (§7.2-2/-30/-31/-32/-33/-34), resolving language through the structure tree.
  - **Fonts (PDF/UA-1):** TrueType cmap and encoding requirements (§7.21.6-1/-2/-4), backed by an
    embedded copy of the Adobe Glyph List (BSD-3-Clause, attributed in `NOTICE`); glyph-width
    consistency between the font dictionary and the embedded program for Identity-H CIDFontType2
    fonts (§7.21.5-1).
  - **PDF/A-2a logical structure (§6.7):** non-standard structure-type role-mapping, RoleMap
    acyclicity, and standard-type remap rules (§6.7.3.4-1/-2/-3); `/Lang` BCP-47 syntax (§6.7.4-1).
  - Each rule documents its deferred edges; the remaining PDF/UA-1 checks are deferred with concrete
    reasons (each would either over-reject conformant files or needs a subsystem the library does not
    yet have).

## [1.7.3] - 2026-06-22

### Added

- **`VellumPdf.Conformance` — PDF/UA-1 (ISO 14289-1) accessibility conformance.** Build-verified
  veraPDF parity for PDF/UA-1 rises from **~7.5% to ~71%** (75 of 106 checks implemented, 1 partial).
  Every rule is authored clean-room from the ISO text, cross-validated against veraPDF 1.30.2 in CI,
  and a 37-fixture adversarial sweep across crop-box geometry, structure-parent resolution,
  multi-hop role-mapping, font usage-scoping, and indirect references found no over-rejections.
  - **A reusable tagged-structure walker** (`/StructTreeRoot` → `/K` → `StructElem`, with role-map
    resolution to ISO 32000-1 Table 333 standard types, a `/ParentTree` reverse index for
    annotation↔structure binding, and cycle/depth guards) underpins the structure rules.
  - **Document & metadata:** file header (§6.1-1), `/Suspects` (§7.1-4), `/Lang` BCP-47 syntax
    (§7.2-29), `/RoleMap` acyclic / no-standard-remap and `/P` presence (§7.1-6/-7/-12).
  - **Logical structure:** table / list / table-of-contents containment, count and caption-position
    (§7.2-3…40); Figure & Formula alternate text (§7.3-1, §7.7-1); Note IDs (§7.9-1/-2); heading
    H/Hn consistency (§7.4.4-1/-2/-3); natural-language determination (§7.2-21…25).
  - **Annotations:** alternate descriptions and structure nesting for general, Link, Widget, and
    PrinterMark annotations (§7.18.1-1/-2, §7.18.4-1, §7.18.5-1/-2, §7.18.8-1), TrapNet (§7.18.2);
    plus optional-content configurations (§7.10), embedded-file names (§7.11), dynamic XFA
    (§7.15), and reference XObjects (§7.20).
  - **Fonts:** a content-stream glyph-extraction pass (per-glyph text rendering mode via a q/Q
    graphics-state stack and shown character codes) drives `.notdef`-reference, glyph-presence,
    and embedding checks (§7.21.4.1-1/-2, §7.21.8-1); plus CIDToGIDMap, CMap, CharSet/CIDSet,
    CIDSystemInfo, symbolic-TrueType, and used-glyph ToUnicode checks (§7.21.3.x / 4.2.x / 6-3 / 7-2).

## [1.7.2] - 2026-06-22

### Added

- **`VellumPdf.Conformance` — digital signatures, JPEG2000, and colour/metadata checks.** Sixteen
  further PDF/A-2b/2u/2a preflight checks, taking build-verified parity to PDF/A-2b **90.3%**, 2u
  **90.4%**, 2a **87.3%** (from ~80%). All are cross-validated against veraPDF 1.30.2, and a
  whole-batch adversarial sweep against real fixtures (a genuine JPEG2000 image, B-B/B-LT/B-LTA
  signed PDFs, conformant writer output) found no over-rejections.
  - **Digital signatures** via a hand-rolled, zero-dependency CMS/ASN.1 reader (no
    `System.Security.Cryptography.Pkcs`): the signature must include an X.509 certificate
    (§6.4.3-2) and exactly one signer (§6.4.3-3); when `/Perms /DocMDP` is present the signature
    reference dictionary must not carry `DigestLocation`/`DigestMethod`/`DigestValue` (§6.1.12-2);
    and the `/ByteRange` coverage check (§6.4.3-1).
  - **JPEG2000** (`JPXDecode`): colour-channel count, colour-space-specification APPROX field,
    `colr` METH value, no CIEJab enumerated colour space, and bit-depth constraints (§6.2.8.3-1…5).
  - **Colour:** ICCBased profile device-class/colour-space/version validity (§6.2.4.2-1); overprint
    mode must be 0 when an ICCBased-CMYK space is used with overprinting, via a content-stream
    graphics-state interpreter (§6.2.4.2-2); same-named Separation colourants must share
    tintTransform and alternateSpace (§6.2.4.4-2).
  - **Fonts / metadata / structure:** embedded-CMap WMode consistency and `usecmap`
    predefined-only (§6.2.11.3.3-2/-3); font/colourant/structure-type names must be valid UTF-8
    (§6.1.8-1); XMP extension-schema property value-type match (§6.6.2.3.1-2).

### Fixed

- **`VellumPdf.Conformance` — two false positives on signed PDFs.** An invisible signature widget
  (a `/Widget` with a degenerate, zero-area `/Rect` and no `/AP`) is no longer flagged for lacking a
  normal appearance (§6.3); and a signature dictionary's `/Contents` (the CMS placeholder, which can
  exceed 32767 bytes) is no longer flagged by the string-length limit (§6.1.13). Both match veraPDF.

## [1.7.1] - 2026-06-21

### Added

- **`VellumPdf.Conformance` content-stream rules.** Four PDF/A-2b/2u/2a preflight checks driven by
  an in-process content-stream scan: content-stream operators must be defined in ISO 32000-1, even
  inside `BX`/`EX` (§6.2.2-1); a page that references named resources must have an explicitly
  associated `/Resources` dictionary rather than relying on an inherited one (§6.2.2-2); an inline
  image's filter must be one of the ISO 32000-1 Table 6 filters permitted for inline images, not
  LZW, Crypt, or JPXDecode (§6.1.10-1); and a composite (Type 0) font with an embedded CMap must not
  produce a CID greater than 65,535 (§6.1.13-10). Each is scoped to page content streams (form
  XObject, Type 3 glyph, and annotation appearance streams are deferred) and cross-validated against
  veraPDF 1.30.2. Parity coverage rises to PDF/A-2b 80.9%, 2u 81.2%, 2a 78.4%.

## [1.7.0] - 2026-06-21

### Added

- **`VellumPdf.Conformance` package.** In-process PDF/A and PDF/UA preflight validation, so callers
  can check conformance without the external veraPDF Docker image. `PdfPreflight.Validate` opens a
  PDF through `VellumPdf.Reader` and runs a registry of clean-room conformance rules authored from
  the ISO specifications, returning a `PreflightResult` of machine-readable assertions (rule id, ISO
  clause, severity, object reference). Rules are registered explicitly — no reflection — so the
  package is AOT- and trim-ready. Each rule documents its deferred edges (e.g. resources nested in
  form XObjects, the ParentTree↔MCID bijection). Coverage:
  - **PDF/A-2b (ISO 19005-2)** — file structure: header and binary marker (§6.1.2), trailer `/ID`
    (§6.1.3), no external streams (§6.1.7.1); graphics: output intents and device colour
    (§6.2.3/§6.2.4.3), graphics-state `/TR`,`/TR2`,`/HTP` (§6.2.5), rendering intents (§6.2.6),
    forbidden image and form-XObject keys including PostScript and reference XObjects
    (§6.2.8/§6.2.9), blend modes (§6.2.10); fonts: embedding (§6.2.11.4.1), subtype and Widths
    consistency (§6.2.11.2), CIDToGIDMap (§6.2.11.3.2), TrueType encoding (§6.2.11.6), and — via an
    in-process sfnt font-program parser — glyph presence (§6.2.11.4.1), glyph-width consistency
    (§6.2.11.5), and `.notdef` references (§6.2.11.8); annotations: flags, appearance, forbidden
    subtypes (§6.3); interactive forms: widget/field actions, `NeedAppearances`, `NeedsRendering`,
    XFA (§6.4); actions: forbidden and named actions, catalog/page additional-actions (§6.5); and —
    via an in-process XMP packet parser — metadata: serialisation (§6.6.2.1), property provenance
    and extension-schema structure (§6.6.2.3), and the PDF/A identification schema (§6.6.4).
  - **PDF/A-2u / PDF/A-2a** — character-to-Unicode (§6.2.11.7) and tagged logical structure (§6.8).
  - **PDF/UA-1 (ISO 14289-1)** — identification, tagging, natural language, document title, and tab
    order.

  Every rule's positive and negative paths are cross-validated against veraPDF 1.30.2 in CI through
  a corpus of writer-produced fixtures. (#50)
- **`VellumPdf.Reader` cross-reference and object streams.** The reader now parses cross-reference
  streams (§7.5.8), hybrid-reference files, and object streams (§7.5.7), resolving objects packed in
  object streams. It decodes the FlateDecode / LZWDecode / ASCIIHexDecode / ASCII85Decode /
  RunLengthDecode filter chain with PNG and TIFF predictors, with decompression-size guards. (#107)

## [1.6.0] - 2026-06-17

### Added

- **PAdES long-term validation (B-LT and B-LTA).** `PdfSignatureSettings.Level` selects the
  signature level: `B_B` (baseline), `B_T` (signature timestamp), `B_LT` (embedded revocation
  evidence), and `B_LTA` (archive timestamp). At `B_LT` and above, signing gathers the signer
  and timestamp-authority certificate chains, fetches OCSP/CRL revocation data through
  `PdfSignatureSettings.RevocationClient`, and writes a `/DSS` (Document Security Store) with
  per-signature `/VRI` as an incremental revision. `B_LTA` adds a `/DocTimeStamp`
  (`/SubFilter /ETSI.RFC3161`) over that revision, then a final cumulative DSS so the archive
  timestamp's own certificate chain and revocation are embedded too. The original signature is
  left byte-for-byte intact, so it stays valid. (#49)
- **`IRevocationClient` and `HttpRevocationClient`.** A pluggable revocation surface mirroring
  the timestamp client. The default HTTP client reads the OCSP responder (AIA) and CRL
  distribution points from a certificate and fetches the evidence over HTTP. Before embedding,
  it validates a CRL (correct issuer, and the certificate not listed as revoked) and requires a
  successful OCSP response status. The abstraction keeps the core offline and the tests
  deterministic.
- **`VellumPdf.Reader` package.** Opens an existing signed PDF (classic cross-reference tables,
  unencrypted) and exposes its catalog and signatures. It is the foundation the LTV path builds
  on, and the first slice of a general reader (see the roadmap). Cross-reference streams, object
  streams, and encryption are not supported yet and raise a clear error.

### Fixed

- **Signed PDF/UA-1 tab order.** A page that carries an annotation now declares `/Tabs /S` under
  PDF/UA-1 (ISO 14289-1 §7.18.3). Signing adds a signature (and, at B-LTA, a document-timestamp)
  widget annotation, so without this a signed PDF/UA-1 document was rejected by veraPDF. Signed
  B-LTA output now validates as PDF/A-2b, PDF/A-2u, PDF/A-2a, and PDF/UA-1.

## [1.5.6] - 2026-06-16

### Fixed

- **Structure tree allocation guard.** A hand-built tagged structure tree whose
  `PdfStructElem.Mcid` is set to a very large value (or `int.MaxValue`) now raises a clear
  exception instead of overflowing or attempting a multi-gigabyte ParentTree allocation. The
  per-page ParentTree array is indexed by MCID; documents tagged through the canvas are
  unaffected (their MCIDs are dense and sequential).

## [1.5.5] - 2026-06-16

Closes the residual hardening items from the 2026-06-12 full-library review (#83, #84).

### Added

- **Signature widget page.** `PdfSignatureSettings.SignaturePage` (0-based, default 0) chooses
  which page carries the invisible signature widget; an out-of-range index is rejected.

### Fixed

- **Tagged-PDF MCID range.** The per-page structure ParentTree is sized by the highest MCID on
  the page rather than the leaf-element count, so non-contiguous MCIDs (for example when the MCID
  counter is shared with container elements) produce a valid sparse array instead of aborting the
  save. The marked-content-to-structure mapping is unchanged.
- **Form field text encoding.** AcroForm field names, values, and choice options are written as
  proper PDF text strings — Latin-1 when representable, otherwise UTF-16BE with a byte-order mark —
  instead of silently replacing non-Latin-1 characters with `?`. Field text that the Standard-14
  appearance font cannot render now raises a clear error rather than writing `?` into the appearance.
- **Word wrap.** Wrapping handles `\r\n` and lone `\r` as line breaks and splits on Unicode
  whitespace, so Windows line endings no longer leave a stray carriage-return glyph and tabs and
  runs of spaces wrap correctly.
- **Nested list markers.** A nested ordered list uses its configured numbering scheme
  (alphabetic, roman, decimal) instead of always falling back to decimal.
- **Justified text.** Word-gap counting uses one tokenization for both measurement and drawing,
  so justified spacing is consistent between embedded and Standard-14 fonts.

## [1.5.4] - 2026-06-13

### Fixed

- **JPEG 2000 in PDF/A.** A JPEG 2000 image in a PDF/A-2 document now carries the JP2 box
  structure (`ihdr`/`colr`) that veraPDF reads for clause 6.2.8.3, rather than only the bare
  codestream — which reported 0 colour channels and 0 bit depth and failed validation. The
  codestream is preserved byte-for-byte: for a JP2 source only ancillary metadata boxes are
  dropped, so the embedded image never grows and usually shrinks; a raw `.j2k` codestream is
  wrapped in a minimal JP2. For a `/JPXDecode` image, `/BitsPerComponent` is emitted only when
  its value is one PDF/A permits (1, 2, 4, 8, 16) — the codestream still defines the bit depth.

### Changed

- **JBIG2 embedding.** A JBIG2 image no longer writes an empty `/DecodeParms` dictionary when it
  has no global segments, and the end-of-page segment is dropped from the embedded stream
  (alongside the end-of-file segment) to match the PDF embedded organisation. The end-of-stripe
  segment is retained because it carries image data for striped pages.

## [1.5.3] - 2026-06-13

### Fixed

- **Signature byte-range coverage.** A signed PDF no longer writes a comment between the
  `/Contents` key and its hex-string value, so the value is a direct hex string as signature
  validators expect. veraPDF 1.30+ rejected the previous output on clause 6.4.3-1
  (`doesByteRangeCoverEntireDocument`) even though the byte range and the CMS signature were
  correct. The internal placeholder is now located by anchoring on the `/ByteRange` placeholder,
  which keeps the patch resistant to crafted `Reason`/`Location` metadata.

## [1.5.2] - 2026-06-12

### Fixed

- **Link URIs with non-BMP characters.** A hyperlink URL containing a character above the Basic
  Multilingual Plane (for example an emoji) is now percent-encoded as its full UTF-8 byte sequence
  rather than two `U+FFFD` replacement characters. URLs without such characters are unaffected.

## [1.5.1] - 2026-06-12

A hardening release from a full-library review: bug fixes, malformed-input robustness, and a
few small additions. No public API was removed.

### Added

- **PNG transparency.** The `tRNS` chunk is now applied — palette images gain an alpha `/SMask`
  and greyscale/truecolour images gain a colour-key `/Mask` — instead of the transparency being
  dropped.
- **Outline open/closed state.** `PdfOutlineEntry.IsExpanded` (default `true`) controls whether a
  bookmark renders expanded or collapsed and is reflected in the ISO 32000 signed `/Count`.

### Fixed

- **Layout pagination.** A list item taller than a page no longer loops or duplicates content — it
  resumes on the next page via the content overflow, and the item marker is drawn once. Table cells
  whose text wraps are drawn wrapped instead of overlapping the next row; automatic column widths
  use the cell's own font, including embedded fonts; row spans are no longer split across a page
  break; and the total-page count behind the `{pages}` footer token matches the rendered output.
  Paragraph wrapping honours embedded newlines.
- **Document integrity.** Writing the same `PdfDocument` twice, or writing a document with no
  pages, now throws instead of producing a duplicated or invalid file. Signing a document that also
  has form fields keeps those fields, including fields on pages other than the first.
- **Fonts.** A malformed `hmtx` or `name` table now fails with `InvalidDataException` rather than an
  unexpected exception, and a CID-keyed CFF that falls back to whole-font embedding no longer
  advertises a subset tag.

### Security

- **Malformed-input robustness.** The CCITT, GIF, and TIFF decoders and the font tables reject
  corrupt, truncated, or out-of-range input with `InvalidDataException` instead of over-reading,
  looping, or crashing with an out-of-range exception; the opt-in CCITT raster path now advances
  correctly. Cross-reference byte offsets are bounded, and an offset too large for the format is
  rejected rather than silently truncated.
- **Output escaping.** Caller-supplied resource and marked-content names are escaped so they cannot
  inject content-stream operators; XML-illegal control characters are stripped from XMP metadata;
  non-ASCII link URIs are percent-encoded; and duplicate form-field names and the reserved radio
  `Off` export value are rejected.
- **Signing.** The signature `/Contents` placeholder is located by a unique sentinel and fails
  closed on an ambiguous match, so signature metadata can no longer derail the patch; the `/M` date
  and the CMS signing-time now share a single value. An encryption decrypt round-trip is now
  exercised on CI.

## [1.5.0] - 2026-06-12

### Added

- **PAdES B-T signature timestamps.** A signature can now carry an RFC-3161 timestamp over the
  signature value, embedded as a CMS signature-timestamp unsigned attribute, to reach PAdES B-T.
  Set `PdfSignatureSettings.TimestampClient` to an `ITimestampClient`; the supplied
  `HttpTimestampClient` requests a token from any RFC-3161 Time Stamping Authority over HTTP, or a
  caller can plug in their own client. When no timestamp client is set the signature is unchanged
  (PAdES B-B). The reserved `/Contents` space is enlarged automatically for a timestamped
  signature left at the default size.

## [1.4.0] - 2026-06-11

### Added

- **JBIG2 images.** `Jbig2ImageLoader` reads JBIG2 bilevel images and embeds them as 1-bit `/JBIG2Decode`. A standalone JBIG2 file is parsed and split into its page segments and a `/JBIG2Globals` side-stream (symbol and pattern dictionaries and tables), as the PDF embedded organisation requires; a file with no global segments stays self-contained.
- **JPEG 2000 images.** `JpxImageLoader` reads JP2 box files and raw codestreams (`.j2k`/`.j2c`), takes width, height, component count, and bit depth from the `ihdr`/`SIZ` header and colour space from the `colr` box, and embeds the codestream as `/JPXDecode`.
- **CCITT Group 3 TIFF.** The TIFF loader now reads Compression 2 (Modified Huffman) and 3 (Group 3 / T.4) in addition to Group 4, mapping the `T4Options` tag to the `/CCITTFaxDecode` `/DecodeParms` (`K`, `EncodedByteAlign`, `EndOfLine`). `CcittImageLoader.Load` gained an `endOfLine` parameter for the Group 3 end-of-line convention.
- **Opt-in raster decode.** A new `ImageLoadOptions.DecodeMode` (`Passthrough` by default, or `DecodeToRaster`) decodes a codestream to pixels and re-encodes it losslessly with FlateDecode for viewers without the native codec. Raster decode covers CCITT Group 3 one-dimensional data and JBIG2 MMR generic regions; the other variants (CCITT two-dimensional and Group 4, JBIG2 arithmetic, symbol, text, and halftone segments, and all JPEG 2000) report `NotSupportedException` when raster decode is requested and continue to pass through unchanged. Passthrough stays the default and is always lossless.

### Security

- **Image-codec hardening.** The JBIG2 segment parser, the JPEG 2000 box and marker walker, and the CCITT decoder bound every offset and length against the input, cap segment counts and decoded-output size, and reject truncated, malformed, or oversized data with `InvalidDataException`/`NotSupportedException` rather than over-reading, looping, or exhausting memory. Valid images are unaffected.

## [1.3.0] - 2026-06-11

### Added

- **Interlaced (Adam7) PNG.** Interlaced PNGs now load; the loader de-interlaces the seven Adam7 passes instead of rejecting the file.
- **16-bit image fidelity.** 16-bit-per-channel PNG and TIFF images keep their full bit depth by default (`BitsPerComponent 16`) rather than being reduced to 8 bits. Pass `new ImageLoadOptions { BitDepth = ImageBitDepth.ReduceToEight }` to the new `PngImageLoader.Load` / `TiffImageLoader.Load` overloads to opt into 8-bit downsampling for smaller output. Images that must be transcoded are always re-encoded losslessly with FlateDecode; JPEG and CCITT data is embedded verbatim with no re-encoding.
- **More TIFF compressions.** The TIFF loader now reads LZW (including the horizontal-differencing predictor), new-style JPEG (single strip, any photometric including YCbCr, embedded as DCTDecode), Group-4 fax (embedded as CCITTFaxDecode), and planar (`PlanarConfiguration 2`) images, in addition to the existing uncompressed and PackBits. `FillOrder 2` data is normalised to MSB-first.
- **CCITT Group 3/4 passthrough.** `CcittImageLoader` wraps raw CCITT-compressed bytes as a 1-bit `/CCITTFaxDecode` image with the matching `/DecodeParms` (K, Columns, Rows, BlackIs1) without decoding; the viewer decodes at render time. Single-strip Group-4 TIFFs are routed through it, with polarity taken from the TIFF photometric. The new image paths are checked on CI with veraPDF under PDF/A-2b.

### Security

- **Image-codec hardening.** The TIFF-LZW decoder and the interlaced-PNG and TIFF strip readers bound their reads and reject corrupt, truncated, or oversized input — invalid LZW codes, output-length mismatches, decompression bombs, out-of-range strip offsets, and hostile dimensions — with `InvalidDataException`/`NotSupportedException` rather than over-reading, looping, or exhausting memory. Valid images are unaffected.

## [1.2.0] - 2026-06-11

### Added

- **OpenType-CFF font subsetting.** CFF (`.otf`) fonts are now subsetted rather than embedded whole. Used charstrings are kept verbatim; unused glyphs and unreachable global and local subroutines are dropped, which cuts a typical small-glyph subset by roughly 70%. CID-keyed or unparseable CFF falls back to whole-font embedding.
- **DeviceCMYK and ICC-based colour.** `PdfDocument.SetPdfAOutputIntent` and `UseCmykOutputIntent` set the PDF/A output intent (the default stays sRGB). `RegisterIccBasedColorSpace` registers an ICCBased colour space, painted with the new `PdfCanvas.SetFillColorSpace`/`SetStrokeColorSpace` and `SetFillColor`/`SetStrokeColor` operators. `IccProfiles.Srgb` and `IccProfiles.GenericCmyk` supply built-in profiles for callers without their own. DeviceCMYK content validates as PDF/A once a CMYK output intent is set; both paths are checked on CI with veraPDF.
- **`ColorCmyk`** colour type in the layout API, with `FromRgb` and `ToRgbApproximate` conversions.
- **cmap subtable formats 0 and 6.** Fonts whose character map uses these formats, in addition to format 4, now embed and extract text correctly.

### Security

- **Font-parser hardening.** The CFF subsetter and cmap parser bound operand-stack depth and subroutine nesting, use overflow-safe offset checks, and reject negative or zero INDEX offsets and out-of-range cmap ranges. A malformed font falls back to whole-font embedding or fails with a clear error instead of throwing an unhandled exception or exhausting the stack. Valid fonts are unaffected.

## [1.1.0] - 2026-06-10

### Added

- **PDF/A-2a (level A) conformance**, validated on every CI run with strict veraPDF.
- **PDF/UA-1 (ISO 14289-1) conformance** via `PdfConformance.PdfUA1`, validated on CI with strict veraPDF — emits the `pdfuaid` XMP schema, `/ViewerPreferences << /DisplayDocTitle true >>`, and marks decorative content (table borders/fills, separators, running header/footer bands) as `/Artifact`.
- **Document and per-element language.** A `Language` property on the layout `Document`, `Paragraph`, `Heading`, `ListItem`, and table `Cell` (and on kernel `PdfDocument` / `PdfStructElem`) emits catalog `/Lang` and XMP `dc:language`.
- **`PdfCanvas.BeginArtifactMarkedContent`** — marks decorative content as a PDF `/Artifact` (no MCID).
- **Accessible tables.** `PdfStructElem.TableHeaderScope` emits `/A << /O /Table /Scope … >>` on header cells so assistive tech can resolve column headers.

### Changed

- The tagged-PDF structure tree now writes an MCID-validated `/ParentTree` and no longer emits a self-referential `/RoleMap` (a circular role mapping, which PDF/UA-1 forbids).

## [1.0.0] - 2026-06-09

### Added

- **Public-API surface lock.** `Microsoft.CodeAnalysis.PublicApiAnalyzers` is
  wired to every shippable project. Any accidental addition, removal, or rename
  of a public symbol is a build error unless the corresponding
  `PublicAPI.Unshipped.txt` baseline is updated, guarding the API contract ahead
  of 1.0.
- **veraPDF PDF/A-2b/2u CI gate.** The official `verapdf/cli` Docker image is
  pulled on every CI run and exercises the generated archival documents (embedded
  font, table, image, and tagged variants) under strict PDF/A-2b and PDF/A-2u
  profiles. A non-compliant report fails the build with the full rule list
  attached.

### Changed

- **Deterministic output.** Document identifiers (`/ID`) and producer timestamps
  are now pinnable at the call site, so bytes produced from identical inputs are
  bit-for-bit identical across builds. This is required for reliable golden-file
  snapshot tests and for reproducible NuGet packages.

### Security

- **Font-parser hardening.** The TrueType/OpenType parser now fails cleanly on
  malformed or hostile input — throwing `InvalidDataException` on corrupt or
  truncated data and `NotSupportedException` on unsupported variants — rather than
  crashing with an unexpected exception, hanging, or exhausting memory.
- **Image-parser hardening.** The PNG, JPEG, BMP, GIF, and TIFF parsers apply the
  same defensive posture: bounded reads, early rejection of structurally invalid
  headers, and no unbounded allocations driven by attacker-controlled length
  fields.

[Unreleased]: https://github.com/Tim81/VellumPDF/compare/v2.0.0...HEAD
[2.0.0]: https://github.com/Tim81/VellumPDF/releases/tag/v2.0.0
[1.11.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.11.0
[1.10.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.10.0
[1.9.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.9.0
[1.8.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.8.0
[1.7.2]: https://github.com/Tim81/VellumPDF/releases/tag/v1.7.2
[1.7.1]: https://github.com/Tim81/VellumPDF/releases/tag/v1.7.1
[1.7.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.7.0
[1.6.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.6.0
[1.5.6]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.6
[1.5.5]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.5
[1.5.4]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.4
[1.5.3]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.3
[1.5.2]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.2
[1.5.1]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.1
[1.5.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.5.0
[1.4.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.4.0
[1.3.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.3.0
[1.2.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.2.0
[1.1.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.1.0
[1.0.0]: https://github.com/Tim81/VellumPDF/releases/tag/v1.0.0
