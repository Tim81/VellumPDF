// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
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

return 0;
