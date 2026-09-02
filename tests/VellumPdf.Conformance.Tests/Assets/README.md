# Conformance test assets

Fonts are third-party, permissively licensed, and documented by the `*-LICENSE.*` file beside each
one. The three PDF fixtures below are committed rather than built at test time; each section states
its own provenance.

## `jpx-encrypted-emptyuser.pdf`

Generated once with qpdf (empty user password, owner `o`, AES-128) from the exact bytes
`PdfPreflightTests.BuildJpxImagePdf(BuildJp2(nc: 2, bpc: 8).File)` produces. See
`Jpeg2000Rule`'s encrypted-document tests in `PdfPreflightTests.cs` for what it pins.

## `enc-aes-256-p-bit10-clear.pdf`

A §7.16-1 violator for `UaEncryptionPermissionsRuleTests`: its `/Encrypt` dictionary's `/P` entry
has bit 10 clear, which ISO 32000-2 Table 22 says a writer "shall always set". Built once with
this library's current writer and committed, because #397 ("Kernel: always set /P bit 10 in the
encryption dictionary") will make bit 10 unconditional and leave no way to produce this shape from
the writer once it lands.

The writer emits AES-256 (`/V 5 /R 6`): `StandardSecurityHandler` implements only one
Standard-security-handler configuration, so every document `PdfDocument.Encrypt` writes is
V=5/R=6 regardless of what permissions it carries. At R6, `/P` is not a key input (Algorithm 2
only feeds it in at R≤4), so this file's `/P` and its `/Perms` seal agree, and it opens the
same way any other well-formed R6 document does.

Provenance:

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

`Permissions = All & ~Extract` clears bit 10 (`PdfPermissions.Extract = 1 << 9`) while leaving every
other bit as `StandardSecurityHandler` would set it for `All`. By Table 22 arithmetic
(`P = (0xFFFFF0C0 | (enabledBits & 0xFFF)) & ~3`), that makes `/P` equal `-516` (`0xFFFFFDFC`) —
`UaEncryptionPermissionsRuleTests` asserts the committed file's own `/P` still reads `-516` before
trusting anything else about it, so a regenerated file with the bit accidentally set cannot make
the rule test vacuous.

SHA-256: `d7a788dc6463cc3f63325aaf27b0b71d56c0bc1501b1174e6334bad2fe66e324`

## `enc-aes-128-userpw-u.pdf`

A byte-identical copy of `VellumPdf.Reader.Tests/Fixtures/Encrypted/enc-aes-128.pdf` (AESv2,
user password `u`, owner `o`, `/P -4` — see that project's own `Fixtures/Encrypted/README.md` for
the qpdf command that built it). Copied rather than referenced because this project does not carry
a project reference to `VellumPdf.Reader.Tests`. Used by `PdfPreflightPasswordOverloadTests` to
exercise the four password-carrying `PdfPreflight` overloads against a document with a real
(non-empty) password, the shape none of the other fixtures in this directory cover.

SHA-256: `c525e277fdbfb1d332eda71df6f9894c80d1a11b34512967a44df1d81fd14f9a`
