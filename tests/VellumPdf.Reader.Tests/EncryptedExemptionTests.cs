// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// The objects an encrypted document leaves in the clear, and the one identity each object is
/// decrypted under. <see cref="EncryptedReaderTests"/> covers the ordinary path — content that IS
/// encrypted, decrypting to the right bytes; this class covers what has to survive untouched, where
/// getting it wrong is silent (a decrypted cross-reference stream fails to inflate, an "encrypted"
/// external-file body decodes to noise) rather than an obvious wrong-password failure.
/// </summary>
public sealed class EncryptedExemptionTests
{
    // Copied verbatim out of enc-rc4-128.pdf so the synthetic documents below authenticate under the
    // same file encryption key: /O, /U, /P, /R and the trailer /ID are all inputs to Algorithm 2.
    private static readonly byte[] Id0 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    private const string Rc4EncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 3 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 2 >>";

    // The same /O and /U at /R 4 with an AESV2 crypt filter. Algorithm 2 takes neither /R nor /V as
    // input, so the file key — and with it the /U check — is unchanged; only the cipher differs.
    private const string AesEncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 4 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 4 "
        + "/CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF >>";

    // ── Cross-reference streams (ISO 32000-1 §7.5.8.2) ──────────────────────────────────────────

    /// <summary>
    /// "The cross-reference stream shall not be encrypted and strings appearing in the
    /// cross-reference stream dictionary shall not be encrypted" (ISO 32000-1 §7.5.8.2).
    /// <c>XrefParser</c> honours this for free — it reads the stream before a decryptor exists — but
    /// a caller resolving that same object through the public reader does not, and a preflight rule
    /// walking every object in the file does exactly that. Decrypting it turns a well-formed file
    /// into a FlateDecode failure.
    /// </summary>
    [Fact]
    public void CrossReferenceStream_resolvedAsAnOrdinaryObject_isNotDecrypted()
    {
        using var reader = PdfReader.Open(Load("enc-rc4-objstm.pdf"), "u");

        var xrefStream = reader.ResolveStream(10);
        Assert.NotNull(xrefStream);
        Assert.Equal("XRef", ((PdfName)xrefStream!.Dictionary.Get(new PdfName("Type"))!).Value);

        // The load-bearing part: the body inflates. Decrypted first, it does not.
        var decoded = reader.GetDecodedStreamData(xrefStream);
        Assert.NotNull(decoded);
        Assert.NotEmpty(decoded!);
    }

    /// <summary>
    /// The other half of §7.5.8.2, and the reason it is there: §7.5.5 has the trailer /ID readable
    /// without decrypting the file, so the copy of it in the cross-reference stream dictionary has
    /// to stay plaintext too.
    /// </summary>
    [Fact]
    public void CrossReferenceStreamDictionary_idString_isNotDecrypted()
    {
        using var reader = PdfReader.Open(Load("enc-rc4-objstm.pdf"), "u");

        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(10, 0)));
        var id = Assert.IsType<PdfArray>(dict.Get(PdfName.ID));
        var first = Assert.IsType<PdfHexString>(id[0]);

        Assert.Equal("000102030405060708090a0b0c0d0e0f", Convert.ToHexStringLower(first.Bytes.Span));
    }

    // ── External-file streams (ISO 32000-1 §7.6.1) ──────────────────────────────────────────────

    /// <summary>
    /// "When a PDF stream object refers to an external file, the stream's contents shall not be
    /// encrypted, since they are not part of the PDF file itself" (ISO 32000-1 §7.6.1). RC4 makes
    /// that failure silent — decrypting plaintext yields noise and no error — so this asserts the
    /// exact bytes rather than that decoding merely succeeded.
    /// </summary>
    [Fact]
    public void ExternalFileStream_body_isNotDecrypted()
    {
        var body = "PLAINTEXT!"u8.ToArray();
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /F (ext.dat) >>\nstream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, "u");
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("PLAINTEXT!", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// The same exemption under AES, where getting it wrong is loud instead of silent: AES-CBC
    /// rejects anything that is not an IV followed by whole blocks, so a three-byte external-file
    /// body throws — a legal document the reader would refuse to read at all.
    /// </summary>
    [Fact]
    public void ExternalFileStream_underAes_doesNotThrow()
    {
        var body = "abc"u8.ToArray();
        var doc = BuildWith(AesEncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /F (ext.dat) >>\nstream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, "u");
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("abc", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// /F names an external file only when it is a file specification — a string or a dictionary
    /// (ISO 32000-1 Table 5). Anything else must not exempt the stream, or a producer using the key
    /// for something of its own would be handed ciphertext as if it were content.
    /// </summary>
    [Fact]
    public void StreamWithNonFileSpecF_isStillDecrypted()
    {
        var body = Encrypt(3, 0, "REAL-CONTENT"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /F 42 >>\nstream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, "u");
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("REAL-CONTENT", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    // ── One identity per object (ISO 32000-1 §7.6.2, Algorithm 1) ───────────────────────────────

    /// <summary>
    /// An object whose header generation disagrees with the cross-reference table's. The table is
    /// the authority (#192), and it has to be the authority for both halves of the object: the
    /// dictionary is decrypted in Resolve, the body in DecryptedStreamView, and Algorithm 1 keys on
    /// one object number and one generation, not one per half. Keyed differently, one of the two
    /// comes out as noise.
    /// </summary>
    [Fact]
    public void XrefGenerationDiffersFromObjectHeader_dictionaryAndBodyShareOneIdentity()
    {
        var body = Encrypt(3, 0, "BODY-GEN0"u8.ToArray());
        var probe = Encrypt(3, 0, "STR-GEN0"u8.ToArray());

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        // The object header says generation 3; the cross-reference table below says 0.
        W($"3 3 obj\n<< /Length {body.Length} /Probe <{Convert.ToHexStringLower(probe)}> >>\n"
          + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream\nendobj\n");
        var o4 = (int)ms.Position;
        W($"4 0 obj\n{Rc4EncryptDict}\nendobj\n");
        var xref = (int)ms.Position;
        W($"xref\n0 5\n{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n{o3:D10} 00000 n \n{o4:D10} 00000 n \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), "u");
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        var stream = reader.ResolveStream(3)!;

        Assert.Equal(
            "STR-GEN0",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("BODY-GEN0", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // RC4 is symmetric, so the reader's own decrypt path doubles as the encryptor these documents
    // need: open the fixture whose /Encrypt dictionary they copy, take its armed decryptor and file
    // key, and run the plaintext through it. Producing the ciphertext any other way would mean a
    // second, hand-rolled RC4 in the test project — a copy of the thing under test.
    private static byte[] Encrypt(int objectNumber, int generation, byte[] plaintext)
    {
        using var reader = PdfReader.Open(Load("enc-rc4-128.pdf"), "u");
        var type = typeof(PdfDocumentReader);
        var decryptor = (StandardSecurityDecryptor)type
            .GetField("_decryptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        var fileKey = (byte[])type
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return decryptor.DecryptString(fileKey, objectNumber, generation, plaintext);
    }

    private static byte[] BuildWith(string encryptDict, params string[] objBodies)
    {
        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");
        var offsets = new List<int>();
        for (var i = 0; i < objBodies.Length; i++)
        {
            offsets.Add((int)ms.Position);
            W($"{i + 1} 0 obj\n{objBodies[i]}\nendobj\n");
        }

        var encryptObjectNumber = objBodies.Length + 1;
        offsets.Add((int)ms.Position);
        W($"{encryptObjectNumber} 0 obj\n{encryptDict}\nendobj\n");

        var xref = (int)ms.Position;
        W($"xref\n0 {encryptObjectNumber + 1}\n{0:D10} 65535 f \n");
        foreach (var o in offsets)
            W($"{o:D10} 00000 n \n");
        W($"trailer\n<< /Size {encryptObjectNumber + 1} /Root 1 0 R /Encrypt {encryptObjectNumber} 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
