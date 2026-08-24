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

        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption!.StreamCipher);
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

        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption!.StreamCipher);
    }

    /// <summary>
    /// §7.5.8.2's exemption reaches a hybrid file's <c>/XRefStm</c> object too. The test above only
    /// shows the document opening, which it would do even if that stream were exempted for the wrong
    /// reason: <c>XrefParser</c> reads it before a decryptor exists, so nothing in the open path can
    /// tell. A caller that resolves the same object afterwards — a preflight rule walking every
    /// object does exactly that — goes through the decrypt path, and the exemption there is keyed on
    /// the OFFSETS the parser recorded. Miss the hybrid one and the rows come back as RC4 noise.
    /// </summary>
    [Fact]
    public void HybridXRefStmObject_resolvedAfterOpening_isNotDecrypted()
    {
        var bytes = BuildHybrid(
            classicTrailerExtra: "",
            xrefStreamExtra: $"/Encrypt {Rc4EncryptDict}",
            listXrefStreamObject: true);

        using var reader = PdfReader.Open(bytes, "u");
        var rows = reader.GetDecodedStreamData(reader.ResolveStream(5)!)!;

        // One /W [1 4 2] row: an in-use entry whose 4-byte offset must still point at "4 0 obj".
        Assert.Equal(7, rows.Length);
        Assert.Equal(1, rows[0]);
        var offset = (rows[1] << 24) | (rows[2] << 16) | (rows[3] << 8) | rows[4];
        Assert.Equal(
            "4 0 obj",
            Encoding.Latin1.GetString(bytes.AsSpan(offset, 7)));
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
    // Every /V 4 row below declares a top-level /Length of 40 on purpose. That is the value the
    // fallback would use if the crypt filter path returned nothing, and 40 bits is 5 bytes — so a row
    // expecting anything else fails the moment the clause under test stops answering. With the 128
    // these rows used to carry, the fallback produced 16 bytes too and most of them could not fail.
    //
    // A cryptFilterLength of 0 means "a /CF entry with a /CFM and NO /Length", the shape Table 25
    // explicitly permits and the one where only the cipher can settle the key size.
    //
    // V=1 is always 40-bit RC4 whatever /Length claims (ISO 32000-1 Table 20).
    [InlineData(1, 3, 128, null, 5)]
    [InlineData(1, 2, 128, null, 5)]
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
    [InlineData(4, 4, 40, 32, 16)]
    // No /Length under the crypt filter at all: each cipher has exactly one answer, and AESV3's is
    // the one a reader that assumed 128-bit AES would get wrong.
    [InlineData(4, 4, 40, 0, 16, "AESV2")]
    [InlineData(4, 4, 40, 0, 32, "AESV3")]
    [InlineData(4, 4, 40, 0, 16, "V2")]
    // A declared length no cipher could use falls back to the cipher, not to the top-level entry.
    [InlineData(4, 4, 40, 3, 32, "AESV3")]
    // RC4 under a crypt filter is the one cipher with a RANGE to declare (Table 20: 40 to 128 bits),
    // so it is the only /CFM that can show the bytes-or-bits reading and the clamp doing anything.
    // 40 reads as bits — a legal byte count stops at 32 — and 5 reads as bytes.
    [InlineData(4, 4, 128, 40, 5, "V2")]
    [InlineData(4, 4, 128, 5, 5, "V2")]
    // Out of range either way: neither 3 bytes nor 3 bits is a key, so the cipher's own answer wins.
    [InlineData(4, 4, 40, 3, 16, "V2")]
    // 100 is out of the byte range and inside the bit range, but 100 bits is not a whole number of
    // bytes — the divisibility test is the only thing standing between it and a 12-byte key.
    [InlineData(4, 4, 40, 100, 16, "V2")]
    // Readable as a byte count, but 20 bytes is 160-bit RC4 and Table 20 stops at 128 — so the value
    // passes the range test and is then turned away by the cipher's own limit, which is a different
    // guard and the one a plausible-looking declaration reaches.
    [InlineData(4, 4, 40, 20, 16, "V2")]
    // A /CFM this handler has no key size for — /None, /Identity, or one from a future edition —
    // leaves the declared length as the only thing said about the key, so it stands.
    [InlineData(4, 4, 40, 16, 16, "None")]
    // ...up to 32 bytes, the top of Table 25's byte range and the largest key any cipher in the spec
    // uses. One less and the value reads as a bit count instead, which 32 is not a legal one of.
    [InlineData(4, 4, 40, 32, 32, "None")]
    public void KeyLengthBytes_followsTheRuleThatOverridesLength(
        int v, int r, int lengthBits, int? cryptFilterLength, int expectedBytes, string cfm = "AESV2")
    {
        var encryptDict = new PdfDictionary()
            .Set(new PdfName("Length"), new PdfInteger(lengthBits));

        if (cryptFilterLength is { } cfLength)
        {
            var filter = new PdfDictionary().Set(new PdfName("CFM"), new PdfName(cfm));
            if (cfLength != 0)
                filter.Set(new PdfName("Length"), new PdfInteger(cfLength));

            encryptDict
                .Set(new PdfName("StmF"), new PdfName("StdCF"))
                .Set(new PdfName("CF"), new PdfDictionary().Set(new PdfName("StdCF"), filter));
        }

        var method = typeof(EncryptionSetup).GetMethod(
            "LegacyKeyLengthBytes", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("LegacyKeyLengthBytes not found by reflection.");

        Assert.Equal(expectedBytes, (int)method.Invoke(null, [encryptDict, v, r, null])!);
    }

    /// <summary>
    /// Which crypt filter is in force when neither <c>/StmF</c> nor <c>/StrF</c> names one: a single
    /// <c>/CF</c> entry is the document's only filter and its length applies, while two leave nothing
    /// to choose between and the length falls to what <c>/V</c> 4 implies. Neither case can be
    /// reached through <c>PdfReader.Open</c> — the corpus's <c>/O</c> and <c>/U</c> authenticate only
    /// at 16 bytes, so a document exercising the 5-byte answer cannot also be opened.
    /// </summary>
    [Theory]
    [InlineData(1, 5)]    // the sole entry's /Length, in bytes
    [InlineData(2, 16)]   // ambiguous: /V 4's own default, 128 bits
    public void KeyLengthBytes_withNoStmFOrStrF_usesTheSoleCryptFilterOrTheVersionDefault(
        int cryptFilterCount, int expectedBytes)
    {
        var cf = new PdfDictionary();
        for (var i = 0; i < cryptFilterCount; i++)
        {
            cf.Set(new PdfName($"Filter{i}"), new PdfDictionary()
                .Set(new PdfName("CFM"), new PdfName("V2"))
                .Set(new PdfName("Length"), new PdfInteger(5)));
        }

        var encryptDict = new PdfDictionary().Set(new PdfName("CF"), cf);

        var method = typeof(EncryptionSetup).GetMethod(
            "LegacyKeyLengthBytes", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(expectedBytes, (int)method.Invoke(null, [encryptDict, 4, 4, null])!);
    }

    /// <summary>
    /// <c>/StmF /Identity</c> names no crypt filter to look up, so the length has to come from
    /// <c>/StrF</c>'s. Driven directly, and with a length that is NOT 128 bits, because every other
    /// route to an answer here — the single-entry fallback, <c>/V</c> 4's own default — produces 128:
    /// a test expecting that number passes with the fallback removed. A document could not make this
    /// assertion either, since a 5-byte key authenticates against no <c>/O</c> and <c>/U</c> the
    /// corpus has.
    /// </summary>
    [Fact]
    public void KeyLengthBytes_withStmFIdentity_takesTheLengthFromStrF()
    {
        var encryptDict = new PdfDictionary()
            .Set(new PdfName("StmF"), new PdfName("Identity"))
            .Set(new PdfName("StrF"), new PdfName("StrCF"))
            .Set(new PdfName("CF"), new PdfDictionary()
                .Set(new PdfName("Unused"), new PdfDictionary()
                    .Set(new PdfName("CFM"), new PdfName("V2"))
                    .Set(new PdfName("Length"), new PdfInteger(16)))
                .Set(new PdfName("StrCF"), new PdfDictionary()
                    .Set(new PdfName("CFM"), new PdfName("V2"))
                    .Set(new PdfName("Length"), new PdfInteger(5))));

        var method = typeof(EncryptionSetup).GetMethod(
            "LegacyKeyLengthBytes", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(5, (int)method.Invoke(null, [encryptDict, 4, 4, null])!);
    }

    /// <summary>
    /// AES-256 has one legal key size too, so a crypt filter declaring anything else loses to the
    /// cipher — the AESV2 half of this rule has its own test; this is the <c>/AESV3</c> half.
    /// </summary>
    [Theory]
    [InlineData(16)]
    [InlineData(5)]
    public void AesV3CryptFilterLengthTheCipherCannotUse_isIgnored(int declaredLength)
    {
        var encryptDict = new PdfDictionary()
            .Set(new PdfName("StmF"), new PdfName("StdCF"))
            .Set(new PdfName("CF"), new PdfDictionary()
                .Set(new PdfName("StdCF"), new PdfDictionary()
                    .Set(new PdfName("CFM"), new PdfName("AESV3"))
                    .Set(new PdfName("Length"), new PdfInteger(declaredLength))));

        var method = typeof(EncryptionSetup).GetMethod(
            "LegacyKeyLengthBytes", BindingFlags.NonPublic | BindingFlags.Static)!;

        Assert.Equal(32, (int)method.Invoke(null, [encryptDict, 4, 4, null])!);
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
    // listXrefStreamObject adds a second classic subsection covering object 5, the cross-reference
    // stream itself. A hybrid file need not list it — nothing resolves it during an ordinary open —
    // but a caller walking every object in the table does, and that is the only way to reach it
    // through the decrypt path.
    private static byte[] BuildHybrid(
        string classicTrailerExtra, string xrefStreamExtra, bool listXrefStreamObject = false)
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
        if (listXrefStreamObject)
            W($"5 1\n{xrefStreamOffset:D10} 00000 n \n");

        W($"trailer\n<< /Size 6 /Root 1 0 R /XRefStm {xrefStreamOffset} {classicTrailerExtra} "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{classicXrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }
}
