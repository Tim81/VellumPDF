// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Barcodes;
using VellumPdf.Encryption;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Reader;
using VellumPdf.Signing;

// Exercises the public generation path under Native AOT: layout engine,
// pagination, Standard-14 fonts, FlateDecode, and the PDF writer.
using var doc = new Document();
doc.Info.Title = "VellumPdf AOT Smoke";
doc.Add(new Paragraph("VellumPdf AOT smoke test — Hello, world!"));
doc.Add(new LineSeparator());
for (var i = 0; i < 80; i++)
    doc.Add(new Paragraph($"Paragraph {i + 1}: the quick brown fox jumps over the lazy dog."));

using var ms = new MemoryStream();
doc.Save(ms);
var bytes = ms.ToArray();

if (bytes.Length < 100)
{
    Console.Error.WriteLine($"FAIL: PDF too small ({bytes.Length} bytes)");
    return 1;
}

var header = Encoding.ASCII.GetString(bytes, 0, 8);
if (header != "%PDF-2.0")
{
    Console.Error.WriteLine($"FAIL: unexpected header '{header}'");
    return 1;
}

Console.WriteLine($"OK: Native AOT generated a {bytes.Length}-byte PDF.");

// Exercise the embedded-font path so AOT compiles/validates the sfnt parser,
// glyf subsetter, and Type0/Identity-H writer. Reachable at compile time (so it
// is AOT-analysed) but guarded at runtime so it is a no-op without a system font.
const string fontPath = @"C:\Windows\Fonts\arial.ttf";
if (File.Exists(fontPath))
{
    using var fdoc = new Document();
    var font = fdoc.LoadTrueTypeFont(fontPath);
    fdoc.Add(new Paragraph("Embedded TrueType under AOT — cafe resume.",
        new TextStyle { FontRef = font, FontSize = 12 }));
    using var fms = new MemoryStream();
    fdoc.Save(fms);
    if (fms.Length < 100)
    {
        Console.Error.WriteLine("FAIL: embedded-font PDF too small");
        return 1;
    }
    Console.WriteLine($"OK: embedded-font Native AOT PDF = {fms.Length} bytes.");
}
else
{
    Console.WriteLine("(embedded-font path AOT-compiled; runtime-skipped — no system font present)");
}

// Exercise the new VellumPdf.Reader parser under Native AOT.
using (var reader = PdfReader.Open(bytes))
{
    if (reader.Catalog is null)
    {
        Console.Error.WriteLine("FAIL: reader returned a null catalog");
        return 1;
    }
    Console.WriteLine($"OK: Reader parsed the PDF under AOT ({reader.Signatures.Count} signatures).");
}

// Encryption, both directions, under AOT. The write side runs AES-256 and SHA-2 through Algorithms
// 8-10; the read side runs Algorithm 2.A back over what it wrote and then decrypts a real stream.
// Neither uses reflection, so the risk was never high — but this is the branch's headline feature
// and it sat outside the gate that is supposed to prove the library AOT-safe.
using (var edoc = new Document())
{
    edoc.Add(new Paragraph("VellumPdf AOT smoke — encrypted."));
    edoc.Encrypt(new PdfEncryptionSettings
    {
        UserPassword = "aot-user",
        OwnerPassword = "aot-owner",
        Permissions = PdfPermissions.Print,
    });

    using var ems = new MemoryStream();
    edoc.Save(ems);
    var encrypted = ems.ToArray();

    if (Encoding.Latin1.GetString(encrypted).Contains("/Encrypt", StringComparison.Ordinal) is false)
    {
        Console.Error.WriteLine("FAIL: the encrypted document carries no /Encrypt");
        return 1;
    }

    using var ereader = PdfReader.Open(encrypted, "aot-user");
    if (ereader.Encryption is null || ereader.Encryption.KeyLengthBits != 256)
    {
        Console.Error.WriteLine($"FAIL: expected a 256-bit key, got {ereader.Encryption?.KeyLengthBits}");
        return 1;
    }

    if (ereader.Catalog is null)
    {
        Console.Error.WriteLine("FAIL: the encrypted document decrypted to a null catalog");
        return 1;
    }

    // And the wrong password is refused rather than returning noise.
    try
    {
        using var wrong = PdfReader.Open(encrypted, "not-the-password");
        Console.Error.WriteLine("FAIL: a wrong password opened the document");
        return 1;
    }
    catch (PdfPasswordException)
    {
    }

    Console.WriteLine(
        $"OK: Reader decrypted an AES-{ereader.Encryption.KeyLengthBits} document under AOT "
        + $"(owner access: {ereader.Encryption.IsOwnerAccess}).");
}

// Exercise the in-process conformance validator under Native AOT. The rule registry is
// reflection-free, so every rule must be reachable without trimming surprises; running the
// PDF/A-2b profile end-to-end here proves the Conformance package is AOT-safe.
var preflight = VellumPdf.Conformance.PdfPreflight.Validate(
    bytes, VellumPdf.Conformance.PdfConformance.PdfA2B);
Console.WriteLine(
    $"OK: Conformance preflight ran under AOT (compliant={preflight.IsCompliant}, "
    + $"{preflight.Assertions.Count} assertions).");

// Exercise the Signing CMS path (PAdES B-B, self-signed) plus a signed round-trip under AOT.
using var rsa = RSA.Create(2048);
var certReq = new CertificateRequest("CN=VellumPdf AOT Smoke", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
using var smokeCert = certReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

using var signDoc = new Document();
signDoc.Add(new Paragraph("Signed under Native AOT."));
using var signMs = new MemoryStream();
signDoc.Sign(signMs, new PdfSignatureSettings { Certificate = smokeCert });
var signedBytes = signMs.ToArray();

using (var signedReader = PdfReader.Open(signedBytes))
{
    if (signedReader.Signatures.Count != 1)
    {
        Console.Error.WriteLine($"FAIL: expected 1 signature, got {signedReader.Signatures.Count}");
        return 1;
    }
}
Console.WriteLine($"OK: Signing + Reader round-trip under AOT ({signedBytes.Length}-byte signed PDF).");

// Exercise the VellumPdf.Barcodes package under Native AOT: the QR matrix encoder directly,
// then the Document flow path (extension method, BarcodeRenderer, painter, and the EAN HRI
// text draw, which goes through the embedded-font-free Standard-14 canvas path).
var qr = new QrCode("VellumPdf AOT");
var matrix = qr.GetMatrix();
if (matrix.Width != matrix.Height || matrix.Width < 21)
{
    Console.Error.WriteLine($"FAIL: unexpected QR matrix size {matrix.Width}x{matrix.Height}");
    return 1;
}

var last = matrix.Width - 1;
if (!matrix.IsDark(0, 0) || !matrix.IsDark(last, 0) || !matrix.IsDark(0, last))
{
    Console.Error.WriteLine("FAIL: a QR finder pattern corner is not dark");
    return 1;
}

Console.WriteLine($"OK: QrCode.GetMatrix() produced a {matrix.Width}x{matrix.Height} matrix under AOT.");

using var barcodeDoc = new Document();
barcodeDoc.Add(new QrCode("VellumPdf AOT") { TargetWidth = 80 });
barcodeDoc.Add(new EanBarcode(EanSymbology.Ean13, "400638133393"));
barcodeDoc.Add(new AztecCode("AZTEC"));
using var barcodeMs = new MemoryStream();
barcodeDoc.Save(barcodeMs);
var barcodeBytes = barcodeMs.ToArray();

if (barcodeBytes.Length < 100)
{
    Console.Error.WriteLine($"FAIL: barcode document PDF too small ({barcodeBytes.Length} bytes)");
    return 1;
}

var barcodeHeader = Encoding.ASCII.GetString(barcodeBytes, 0, 8);
if (barcodeHeader != "%PDF-2.0")
{
    Console.Error.WriteLine($"FAIL: unexpected barcode document header '{barcodeHeader}'");
    return 1;
}

Console.WriteLine($"OK: Barcodes Document flow (QR + EAN-13 with HRI) under AOT = {barcodeBytes.Length} bytes.");

return 0;
