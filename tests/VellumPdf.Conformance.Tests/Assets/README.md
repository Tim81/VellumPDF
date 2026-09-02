# Conformance test assets

Fonts are third-party, permissively licensed, and documented by the `*-LICENSE.*` file beside each
one. The PDF fixtures below are committed rather than built at test time; each section states
its own provenance.

`EncryptedFixtureDigestTests.cs` pins the four §7.16-1 encryption fixtures below
(`enc-aes-256-p-bit10-clear.pdf`, `enc-aes-256-emptyuser-p-all.pdf`, `enc-aes-256-userpw-u-p-all.pdf`,
`enc-aes-128-userpw-u.pdf`) to the SHA-256 values recorded in their own sections, so the "re-check
with veraPDF before updating the SHA" rule stated below fails the build rather than relying on
someone remembering to follow it.

## `jpx-encrypted-emptyuser.pdf`

Generated once with qpdf (empty user password, owner `o`, AES-128) from the exact bytes
`PdfPreflightTests.BuildJpxImagePdf(BuildJp2(nc: 2, bpc: 8).File)` produces. See
`Jpeg2000Rule`'s encrypted-document tests in `PdfPreflightTests.cs` for what it pins.

## `enc-aes-256-p-bit10-clear.pdf`

A §7.16-1 violator for `UaEncryptionPermissionsRuleTests`: its `/Encrypt` dictionary's `/P` entry
has bit 10 clear, which ISO 32000-2 Table 22 says a writer "shall always set". Built once with
the pre-#397 writer and committed, because #397 ("Kernel: always set /P bit 10 in the encryption
dictionary") made bit 10 unconditional, so there is no longer a way to produce this shape from
the writer itself.

The writer emits AES-256 (`/V 5 /R 6`): `StandardSecurityHandler` implements only one
Standard-security-handler configuration, so every document `PdfDocument.Encrypt` writes is
V=5/R=6 regardless of what permissions it carries. At R6, `/P` is not a key input (Algorithm 2
only feeds it in at R≤4), so this file's `/P` and its `/Perms` seal agree, and it opens the
same way any other well-formed R6 document does.

Provenance (run against the pre-#397 writer at `1a85a66`; the current writer produces `/P -4` from
the same recipe, so this block documents the file rather than reproducing it):

```csharp
using var doc = new PdfDocument();
doc.AddPage();
doc.Encrypt(new PdfEncryptionSettings
{
    UserPassword = "",
    OwnerPassword = "vellum-fixture-owner",
    Permissions = PdfPermissions.All & ~PdfPermissions.Extract,
});
doc.Save(stream);
```

Under that writer's mask, `Permissions = All & ~Extract` cleared bit 10 (`PdfPermissions.Extract =
1 << 9`) while leaving every other bit as `StandardSecurityHandler` set it for `All`. By Table 22
arithmetic with the pre-#397 mask (`P = (0xFFFFF0C0 | (enabledBits & 0xFFF)) & ~3`), that makes
`/P` equal `-516` (`0xFFFFFDFC`). `UaEncryptionPermissionsRuleTests` asserts the committed file's
own `/P` still reads `-516` before trusting anything else about it, so a regenerated file with the
bit accidentally set cannot make the rule test vacuous.

SHA-256: `d7a788dc6463cc3f63325aaf27b0b71d56c0bc1501b1174e6334bad2fe66e324`

## `enc-aes-256-emptyuser-p-all.pdf` and `enc-aes-256-userpw-u-p-all.pdf`

Used by `UaEncryptionPermissionsVeraPdfTests`. Both are R6 documents with `Permissions = All`
(`/P -4`), committed instead of built by the test itself.

The reason is a difference between this library's Algorithm 2.B and veraPDF's own. ISO 32000-2
§7.6.4.3.4 runs the hash loop for 64 rounds, then keeps going "while the last byte of E is greater
than round number minus 32", so it stops once `E[last] <= completedRounds - 32`. That is what
`StandardSecurityHandler.Hash2B` does, and it agrees with qpdf and pdf.js. veraPDF's own
`EncryptionToolsRevision5_6.computeHash` (in `veraPDF-parser`, package
`org.verapdf.tools`) instead exits on `E[last] <= rounds - 32`, with `rounds` counted from 0 — in
the spec's completed-rounds frame (`completedRounds = rounds + 1`) that is
`E[last] <= completedRounds - 33`, one round later than the spec text. The two readings only
disagree when `E[last]` lands exactly on `completedRounds - 32`: veraPDF then runs one extra
round, so it derives a different hash from the
same password and salt, and its own `/U` check on the file it just opened fails. That makes it
refuse the document outright (exit 8, "appears to be an encrypted PDF file and could not be
processed"), even though qpdf and poppler open the same bytes without complaint.

Because the writer draws its salts from `RandomNumberGenerator`, whether a freshly written file
lands on that exact boundary is chance, not something the recipe controls. Measured at 6
refusals out of 60 freshly built files. Building the fixture at test time therefore makes the test
itself flaky against veraPDF for a reason that has nothing to do with the rule under test, so these
two files are generated once, checked with veraPDF, and only committed once accepted. The writer is
not being worked around here: `StandardSecurityHandler` implements what the spec says.

Provenance (identical for both, only the encryption settings differ):

```csharp
using var doc = new PdfDocument();
doc.AddPage();
doc.Encrypt(new PdfEncryptionSettings
{
    UserPassword = "",                       // enc-aes-256-emptyuser-p-all.pdf
    OwnerPassword = "vellum-test-owner",
    Permissions = PdfPermissions.All,
});
doc.Save(stream);
```

```csharp
using var doc = new PdfDocument();
doc.AddPage();
doc.Encrypt(new PdfEncryptionSettings
{
    UserPassword = "u",                      // enc-aes-256-userpw-u-p-all.pdf
    OwnerPassword = null,
    Permissions = PdfPermissions.All,
});
doc.Save(stream);
```

Each candidate was checked with `verapdf.bat --flavour ua1 --format xml <file>` (adding
`--password u` for the user-password file) before being committed: the run has to produce a
`<validationReport ` element (with the trailing space — a refused, exit-8 run's XML still contains
the batch-level `<validationReports compliant=… nonCompliant=… failedJobs=…>` summary, which
`<validationReport` without the space also matches) rather than exit 8. Both files here passed on
the first attempt.

If either file is ever regenerated, it must go through the same check before its SHA-256 below is
updated. A regenerated file that has not been re-checked against veraPDF can reintroduce the
one-in-ten refusal this section exists to remove.

SHA-256 `enc-aes-256-emptyuser-p-all.pdf`:
`ac213bd477b160d4fa0c6dbdbc5a59492045b58b38d562b8da2fd41fde26fc8b`

SHA-256 `enc-aes-256-userpw-u-p-all.pdf`:
`6f4a6b3fdd247842faf1712144392d81137d95990f185dec104b543ae11868f6`

## `enc-aes-128-userpw-u.pdf`

A byte-identical copy of `VellumPdf.Reader.Tests/Fixtures/Encrypted/enc-aes-128.pdf` (AESv2,
user password `u`, owner `o`, `/P -4` — see that project's own `Fixtures/Encrypted/README.md` for
the qpdf command that built it). Copied rather than referenced because this project does not carry
a project reference to `VellumPdf.Reader.Tests`. Used by `PdfPreflightPasswordOverloadTests` to
exercise the four password-carrying `PdfPreflight` overloads against a document with a real
(non-empty) password, the shape none of the other fixtures in this directory cover.

SHA-256: `c525e277fdbfb1d332eda71df6f9894c80d1a11b34512967a44df1d81fd14f9a`
