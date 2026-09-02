// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Barcodes;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Reader;
using VellumPdf.Signing;

// Exercises the public generation path under Native AOT: layout engine, pagination, the
// Kernel's built-in Standard-14 AFM metrics (Standard14Metrics, reached through Layout — not
// the VellumPdf.Fonts.Standard14 package, covered separately below), FlateDecode, and the
// PDF writer.
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

// Exercise VellumPdf.Fonts.Standard14 under Native AOT (#219). The package embeds its 12
// Liberation TTFs as EmbeddedResource and loads them via Assembly.GetManifestResourceStream(
// logicalName) — a lookup by string, which is exactly what trimming breaks silently. Layout's
// Document has no public escape hatch to the Kernel PdfDocument it wraps, so EmbedStandard14Font
// (an extension on PdfDocument) is exercised the same way Standard14SubstituteTests does: via the
// Kernel Document/Canvas API directly.
using (var std14Doc = new VellumPdf.Document.PdfDocument())
{
    var std14Page = std14Doc.AddPage();
    var std14Handle = std14Doc.EmbedStandard14Font(Standard14.Helvetica);
    std14Doc.RegisterEmbeddedFontUsage(std14Page, std14Handle);

    var std14Canvas = new VellumPdf.Canvas.PdfCanvas(std14Page);
    std14Canvas.BeginText().SetFontByName(std14Handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
    var std14Gids = new ushort[3];
    var std14GidCount = std14Handle.GetGlyphIds("Abc", std14Gids);
    if (std14GidCount != 3 || Array.IndexOf(std14Gids, (ushort)0, 0, std14GidCount) >= 0)
    {
        Console.Error.WriteLine("FAIL: Standard14 substitute returned a .notdef glyph for 'Abc'");
        return 1;
    }
    std14Canvas.ShowGlyphs(std14Gids.AsSpan(0, std14GidCount));
    std14Canvas.EndText();
    std14Canvas.Finish();

    using var std14Ms = new MemoryStream();
    std14Doc.Save(std14Ms);
    var std14Bytes = std14Ms.ToArray();
    var std14Text = Encoding.Latin1.GetString(std14Bytes);

    if (!std14Text.Contains("/FontFile2", StringComparison.Ordinal)
        || !std14Text.Contains("/Type0", StringComparison.Ordinal)
        || !std14Text.Contains("/Identity-H", StringComparison.Ordinal))
    {
        Console.Error.WriteLine("FAIL: Standard14 substitute did not embed a Type0/FontFile2 font");
        return 1;
    }

    // Inflate the FontFile2 stream itself (FlateDecode is RFC 1950 zlib, so ZLibStream reads it
    // directly) and check the sfnt version. This is the real proof the manifest-resource lookup
    // returned the actual Liberation TTF under AOT — a truncated or missing resource would still
    // satisfy a bare "/FontFile2" substring match.
    var fontProgram = ExtractFontFile2(std14Bytes, std14Text);
    if (fontProgram is null || fontProgram.Length < 4
        || BinaryPrimitives.ReadUInt32BigEndian(fontProgram) != 0x00010000)
    {
        Console.Error.WriteLine("FAIL: the embedded FontFile2 stream is not a valid sfnt (TrueType) font program");
        return 1;
    }

    Console.WriteLine(
        $"OK: Standard14 substitute embedded a {fontProgram.Length}-byte TrueType font program under AOT.");
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
// 8-10; the read side runs Algorithm 2.A back over what it wrote, decrypts /Perms under the file
// key, and refuses a wrong password. Neither uses reflection, so the risk was never high — but this
// is the branch's headline feature and it sat outside the gate meant to prove the library AOT-safe.
//
// What this CANNOT check is a decrypted stream or string: GetDecodedStreamData and ResolveStream are
// internal, so nothing outside the assembly can reach the bytes. That is a real limit of the gate,
// and it is why the unit tests — which can reach them — carry the end-to-end assertions instead.
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

    using var ereader = PdfReader.Open(encrypted, new PdfReaderOptions { Password = "aot-user" });
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

    // /Perms, decrypted. Reading Permissions off the untouched document proves nothing — the reader
    // falls back to the dictionary's /P when the seal fails, and /P carries the same value, so the
    // check passed even with the /Perms decryption stubbed to return zeroes. Editing /P in the
    // written bytes is what separates the two sources: the edit claims everything, and a reader
    // that reads the seal still reports what was sealed. That is Print plus Extract, not Print
    // alone: since #397 the writer sets /P bit 10 whatever the caller asked for (ISO 32000-2
    // Table 22 has writers always set it), and the reader reports the sealed bit as Extract.
    var text = Encoding.Latin1.GetString(encrypted);
    var pAt = text.IndexOf("/P -", StringComparison.Ordinal);
    if (pAt < 0)
    {
        Console.Error.WriteLine("FAIL: no /P found in the encrypted document");
        return 1;
    }

    var pEnd = pAt + 3;
    while (pEnd < text.Length && (text[pEnd] == '-' || char.IsAsciiDigit(text[pEnd])))
        pEnd++;

    var digits = pEnd - (pAt + 3);                               // the "-NNNN" this replaces
    var widened = "-" + "1".PadLeft(digits - 1, '0');            // -1, padded to that same width
    var tampered = Encoding.Latin1.GetBytes(text[..(pAt + 3)] + widened + text[pEnd..]);
    if (tampered.Length != encrypted.Length)
    {
        Console.Error.WriteLine("FAIL: the /P edit changed the file length");
        return 1;
    }

    using (var sealedReader = PdfReader.Open(tampered, new PdfReaderOptions { Password = "aot-user" }))
    {
        if (sealedReader.Encryption!.Permissions != (PdfPermissions.Print | PdfPermissions.Extract))
        {
            Console.Error.WriteLine(
                $"FAIL: /Perms did not override an edited /P — got {sealedReader.Encryption.Permissions}");
            return 1;
        }
    }

    // And the wrong password is refused rather than returning noise.
    try
    {
        using var wrong = PdfReader.Open(encrypted, new PdfReaderOptions { Password = "not-the-password" });
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

// Finds the object referenced by "/FontFile2 N 0 R", reads its /Length, and inflates the stream
// body that follows. PdfStream (VellumPdf.Kernel) always serialises a stream object as
// "N 0 obj\n<dict with /Length>\nstream\n<flate body>\nendstream\nendobj", so the dictionary
// portion (everything up to "stream") is plain ASCII and can be scanned without decoding anything.
static byte[]? ExtractFontFile2(byte[] pdfBytes, string pdfText)
{
    const string refKey = "/FontFile2 ";
    var refAt = pdfText.IndexOf(refKey, StringComparison.Ordinal);
    if (refAt < 0)
        return null;

    var numStart = refAt + refKey.Length;
    var numEnd = numStart;
    while (numEnd < pdfText.Length && char.IsAsciiDigit(pdfText[numEnd]))
        numEnd++;
    if (numEnd == numStart)
        return null;

    var objAt = IndexOfObjectHeader(pdfText, pdfText[numStart..numEnd]);
    if (objAt < 0)
        return null;

    const string lengthKey = "/Length ";
    var lengthAt = pdfText.IndexOf(lengthKey, objAt, StringComparison.Ordinal);
    if (lengthAt < 0)
        return null;

    var lenStart = lengthAt + lengthKey.Length;
    var lenEnd = lenStart;
    while (lenEnd < pdfText.Length && char.IsAsciiDigit(pdfText[lenEnd]))
        lenEnd++;
    if (lenEnd == lenStart || !int.TryParse(pdfText[lenStart..lenEnd], out var length))
        return null;

    const string streamMarker = "\nstream\n";
    var streamAt = pdfText.IndexOf(streamMarker, lenEnd, StringComparison.Ordinal);
    if (streamAt < 0)
        return null;

    var bodyStart = streamAt + streamMarker.Length;
    if (bodyStart + length > pdfBytes.Length)
        return null;

    using var compressed = new MemoryStream(pdfBytes, bodyStart, length);
    using var zlib = new ZLibStream(compressed, CompressionMode.Decompress);
    using var output = new MemoryStream();
    zlib.CopyTo(output);
    return output.ToArray();
}

// "{objNum} 0 obj" as a bare substring risks matching inside a longer number (e.g. "1 0 obj"
// inside "21 0 obj"), so this rejects a hit unless the preceding character is not a digit.
static int IndexOfObjectHeader(string text, string objNum)
{
    var marker = objNum + " 0 obj";
    var from = 0;
    while (true)
    {
        var at = text.IndexOf(marker, from, StringComparison.Ordinal);
        if (at < 0 || at == 0 || !char.IsAsciiDigit(text[at - 1]))
            return at;
        from = at + 1;
    }
}
