// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Structural arrangements around <c>/Encrypt</c> that no producer in the corpus emits, so no
/// fixture can pin them: which trailer's <c>/Encrypt</c> wins in a hybrid-reference file, the
/// <c>/Crypt</c> filter written the way ISO 32000-1 §7.6.5's own example writes it, and the key
/// length rules that only bite when <c>/Length</c> and the algorithm version disagree.
/// </summary>
public sealed class EncryptionStructureTests
{
    private static readonly byte[] Id0 = [.. Enumerable.Range(0, 16).Select(i => (byte)i)];

    private const string Rc4EncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 3 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 2 >>";

    // ── Hybrid-reference files (ISO 32000-2 §7.5.8.4) ───────────────────────────────────────────

    /// <summary>
    /// A hybrid file may put <c>/Encrypt</c> on its cross-reference stream's dictionary, which is
    /// the only place a PDF 1.5-aware producer can put it where a pre-1.5 reader — falling back to
    /// the classic table — would still find the document readable. <c>XrefParser</c> merges it onto
    /// the classic trailer for that reason. When BOTH declare one, the classic trailer's
    /// wins: it is what every other trailer key is taken from, and a document declaring two is
    /// malformed either way. Nothing enforced the precedence, because no producer writes both.
    /// </summary>
    [Fact]
    public void HybridFileDeclaringEncryptTwice_takesTheClassicTrailers()
    {
        // The classic trailer names the Standard handler this reader implements; the cross-reference
        // stream names a public-key one it does not. Whichever wins decides whether this opens.
        var bytes = BuildHybrid(
            classicTrailerExtra: $"/Encrypt {Rc4EncryptDict}",
            xrefStreamExtra: "/Encrypt << /Filter /Adobe.PubSec /V 1 /R 2 >>");

        using var reader = PdfReader.Open(bytes, "u");

        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption!.Cipher);
    }

    /// <summary>
    /// The other direction, and the case that actually occurs: only the cross-reference stream
    /// declares <c>/Encrypt</c>, and the document has to decrypt anyway.
    /// </summary>
    [Fact]
    public void HybridFileDeclaringEncryptOnlyOnTheXrefStream_stillDecrypts()
    {
        var bytes = BuildHybrid(classicTrailerExtra: "", xrefStreamExtra: $"/Encrypt {Rc4EncryptDict}");

        using var reader = PdfReader.Open(bytes, "u");

        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption!.Cipher);
    }

    // ── The /Crypt filter as ISO 32000-1 §7.6.5's example writes it ─────────────────────────────

    /// <summary>
    /// §7.6.5's example leaves the metadata stream in the clear with
    /// <c>/Filter [/Crypt] /DecodeParms &lt;&lt; /Type /CryptFilterDecodeParms /Name /Identity &gt;&gt;</c>
    /// — an array <c>/Filter</c>, a <c>/DecodeParms</c> DICTIONARY, and the <c>/Type</c> key present.
    /// The existing tests cover a bare-name <c>/Filter</c> and an array <c>/Filter</c> with no
    /// <c>/DecodeParms</c> at all; the canonical combination of the three was untested.
    ///
    /// <para>
    /// Both rows are needed, and the Identity one alone would prove nothing: a reader that cannot
    /// read a dictionary-shaped <c>/DecodeParms</c> at all falls back to a missing <c>/Name</c>,
    /// whose documented default is Identity — the right answer for the wrong reason. The
    /// <c>/StdCF</c> row is the one that fails in that case, because there the dictionary is what
    /// says to decrypt.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Identity", false)]
    [InlineData("StdCF", true)]
    public void CryptFilterWrittenTheWayTheSpecExampleWritesIt_isRead(string filterName, bool bodyIsEncrypted)
    {
        var plaintext = "CANONICAL-CRYPT-FILTER"u8.ToArray();
        var body = bodyIsEncrypted ? Rc4(3, 0, plaintext) : plaintext;

        var doc = BuildWith(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 /CF << /StdCF << /CFM /V2 /Length 16 >> >> "
            + "/StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /Filter [/Crypt] "
            + $"/DecodeParms << /Type /CryptFilterDecodeParms /Name /{filterName} >> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, "u");
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("CANONICAL-CRYPT-FILTER", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    // ── Key length: /Length, /V and /R disagreeing ──────────────────────────────────────────────

    /// <summary>
    /// Three separate rules decide the file key length, and two of them override <c>/Length</c>
    /// outright. Driving <c>LegacyKeyLengthBytes</c> directly is the only way to reach the V=1 row:
    /// a V=1 document declaring 128 bits would need an <c>/O</c> and <c>/U</c> computed at n=5 with
    /// R≥3, and no tool in reach emits that combination — the corpus's own V=1 fixture declares 40
    /// bits, which makes the rule and the declaration agree and the rule therefore invisible.
    /// </summary>
    [Theory]
    // V=1 is always 40-bit RC4 whatever /Length claims (ISO 32000-1 Table 20).
    [InlineData(1, 3, 128, null, 5)]
    [InlineData(1, 2, 40, null, 5)]
    // R=2 is always n=5, whatever /Length claims (Algorithm 2 step (i)).
    [InlineData(2, 2, 128, null, 5)]
    // V=2 honours /Length, in bits.
    [InlineData(2, 3, 128, null, 16)]
    [InlineData(2, 3, 40, null, 5)]
    // V=4 takes the crypt filter's own /Length, which Table 25 measures in BYTES...
    [InlineData(4, 4, 40, 16, 16)]
    // ...but tolerates a producer that wrote bits there, since the two ranges cannot overlap.
    [InlineData(4, 4, 40, 128, 16)]
    // A crypt filter /Length the cipher cannot use is the document contradicting itself. The cipher
    // wins, because it is what will actually be applied — 32 bytes is not an AES-128 key.
    [InlineData(4, 4, 128, 32, 16)]
    public void KeyLengthBytes_followsTheRuleThatOverridesLength(
        int v, int r, int lengthBits, int? cryptFilterLength, int expectedBytes)
    {
        var encryptDict = new PdfDictionary()
            .Set(new PdfName("Length"), new PdfInteger(lengthBits));

        if (cryptFilterLength is { } cfLength)
        {
            encryptDict
                .Set(new PdfName("StmF"), new PdfName("StdCF"))
                .Set(new PdfName("CF"), new PdfDictionary()
                    .Set(new PdfName("StdCF"), new PdfDictionary()
                        .Set(new PdfName("CFM"), new PdfName("AESV2"))
                        .Set(new PdfName("Length"), new PdfInteger(cfLength))));
        }

        var method = typeof(EncryptionSetup).GetMethod(
            "LegacyKeyLengthBytes", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LegacyKeyLengthBytes not found by reflection.");

        Assert.Equal(expectedBytes, (int)method.Invoke(null, [encryptDict, v, r])!);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    // RC4 is symmetric, so the reader's own decrypt path doubles as the encryptor: the object key
    // derivation is the same under /V 2 and a /V 4 /CFM /V2 crypt filter, so ciphertext made with
    // the fixture's armed decryptor is what this document's own /Encrypt dictionary expects.
    private static byte[] Rc4(int objectNumber, int generation, byte[] plaintext)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("enc-rc4-128.pdf")!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);

        using var reader = PdfReader.Open(ms.ToArray(), "u");
        var type = typeof(PdfDocumentReader);
        var decryptor = (StandardSecurityDecryptor)type
            .GetField("_decryptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        var fileKey = (byte[])type
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return decryptor.DecryptString(fileKey, objectNumber, generation, plaintext);
    }

    private static byte[] Build(params string[] objBodies) => BuildWith(Rc4EncryptDict, objBodies);

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
        foreach (var offset in offsets)
            W($"{offset:D10} 00000 n \n");
        W($"trailer\n<< /Size {encryptObjectNumber + 1} /Root 1 0 R /Encrypt {encryptObjectNumber} 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// A hybrid-reference file: a classic cross-reference table whose trailer carries
    /// <c>/XRefStm</c>, plus a cross-reference stream that defines object 4. Both trailers can be
    /// given extra entries, which is how the two <c>/Encrypt</c> arrangements above are built.
    /// </summary>
    private static byte[] BuildHybrid(string classicTrailerExtra, string xrefStreamExtra)
    {
        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));

        W("%PDF-1.5\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");
        var o4 = (int)ms.Position;
        W("4 0 obj\n<< /HybridOnly true >>\nendobj\n");

        // One /W [1 4 2] row: type 1, the offset of object 4, generation 0.
        byte[] body = [1, (byte)(o4 >> 24), (byte)(o4 >> 16), (byte)(o4 >> 8), (byte)o4, 0, 0];
        var xrefStreamOffset = (int)ms.Position;
        W($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 2] /Index [4 1] {xrefStreamExtra} /Length {body.Length} >>\nstream\n");
        ms.Write(body);
        W("\nendstream\nendobj\n");

        var classicXrefOffset = (int)ms.Position;
        W("xref\n0 4\n");
        W($"{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n{o3:D10} 00000 n \n");
        W($"trailer\n<< /Size 6 /Root 1 0 R /XRefStm {xrefStreamOffset} {classicTrailerExtra} "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{classicXrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }
}
