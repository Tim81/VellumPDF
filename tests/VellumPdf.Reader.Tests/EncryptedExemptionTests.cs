// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
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

    // The same /O and /U again, at /R 4 with an RC4 crypt filter — the shape a /Crypt specifier needs,
    // since Table 20 makes the whole crypt filter mechanism meaningful only at /V 4, while keeping
    // the cipher RC4 so this file's own encryptor still produces what the reader expects.
    private const string Rc4V4EncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 4 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 4 "
        + "/CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF >>";

    // The same /O and /U at /R 4 with an AESV2 crypt filter. Algorithm 2 takes neither /R nor /V as
    // input, so the file key — and with it the /U check — is unchanged; only the cipher differs.
    private const string AesEncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 4 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /V 4 "
        + "/CF << /StdCF << /CFM /AESV2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF >>";

    // The same /P, /R and /V as Rc4EncryptDict, with /O and /U derived for an EMPTY /ID[0]. /O does
    // not take the ID as input and is byte-identical; /U and the file key do, so both differ.
    private const string EmptyIdEncryptDict =
        "<< /Filter /Standard /Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
        + "/P -4 /R 3 /U <06fe1801286e1d3d5e48258101f589cf00000000000000000000000000000000> /V 2 >>";

    // ── The stream's own /Crypt specifier, below /V 4 ───────────────────────────────

    /// <summary>
    /// The resolver takes "are crypt filters in force" as a parameter, and <c>CryptFilterResolverTests</c>
    /// pins what it does with each value — but not what the READER passes. This is the same document
    /// from the other side: a <c>/V</c> 2 file whose stream carries <c>/Filter /Crypt</c> with
    /// <c>/Name /Identity</c>. Table 20 scopes the whole crypt filter mechanism to <c>/V</c> 4, so
    /// the specifier says nothing here and the body is RC4 like every other stream. Hand the resolver
    /// a constant instead of the document's <c>/V</c> and this comes back as ciphertext.
    /// </summary>
    [Fact]
    public void CryptSpecifierBelowV4_isIgnoredByTheReader_notJustByTheResolver()
    {
        var body = Encrypt(3, 0, "BELOW-V4-STILL-RC4"u8.ToArray());

        var doc = BuildWith(
            Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /Filter [/Crypt] /DecodeParms << /Name /Identity >> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        Assert.Equal(
            "BELOW-V4-STILL-RC4",
            Encoding.ASCII.GetString(reader.GetDecodedStreamData(reader.ResolveStream(3)!)!));
    }

    // ── /EncryptMetadata in the key derivation (Algorithm 2 step (f)) ────────────────────────

    /// <summary>
    /// Algorithm 2 step (f) is scoped to "security handlers of revision 4 or greater": only there
    /// does an unencrypted-metadata document append 0xFFFFFFFF to the key input. A reader that
    /// applies it at <c>/R</c> 3 derives a different key from the one the producer used and rejects
    /// the correct password.
    ///
    /// <para>The <c>/O</c> and <c>/U</c> here are the corpus's, derived at <c>/R</c> 3 without those
    /// four bytes. <c>/V</c> is 4 so that <c>/EncryptMetadata</c> is read at all — Table 21 scopes the
    /// entry there — which makes this the one shape that separates the revision test from the flag:
    /// gate on <c>/V</c> instead of <c>/R</c>, or drop the test entirely, and this document stops
    /// opening.</para>
    /// </summary>
    [Fact]
    public void V4R3_withEncryptMetadataFalse_doesNotExtendTheKeyInput()
    {
        var doc = BuildWith(
            "<< /Filter /Standard /Length 128 /V 4 /R 3 /EncryptMetadata false "
            + "/CF << /StdCF << /CFM /V2 /Length 16 >> >> /StmF /StdCF /StrF /StdCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/P -4 /U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> >>",
            "<< /Type /Catalog /Pages 2 0 R /Probe 3 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Probe <{Convert.ToHexStringLower(Encrypt(3, 0, "R3-NO-FFFFFFFF"u8.ToArray()))}> >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        var probe = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        Assert.Equal(
            "R3-NO-FFFFFFFF",
            Encoding.ASCII.GetString(((PdfHexString)probe.Get(new PdfName("Probe"))!).Bytes.Span));
    }

    // ── The trailer /ID (ISO 32000-1 §7.6.3.3, Algorithm 2 step (e)) ────────────────────────────

    /// <summary>
    /// Table 15 requires <c>/ID</c> once <c>/Encrypt</c> is present, and Algorithm 2 step (e) appends
    /// its first element to the MD5 input. A producer that wrote no <c>/ID</c>, or an empty one,
    /// appended nothing — so the same derivation run here reaches the same file key and the document
    /// is readable. Refusing it on the missing entry alone would leave a file qpdf and poppler both
    /// open (qpdf silently for <c>[&lt;&gt;&lt;&gt;]</c>, with a warning for the absent array) failing
    /// here and nowhere else, which is the wrong trade for a malformation that costs nothing to
    /// tolerate.
    /// </summary>
    /// <remarks>
    /// The <c>/O</c> and <c>/U</c> in <see cref="EmptyIdEncryptDict"/> were derived outside this
    /// library, from Algorithms 2, 3 and 5 written against the spec text, so this pins the
    /// derivation rather than the reader agreeing with itself.
    /// </remarks>
    [Theory]
    [InlineData("")]                    // no /ID entry at all
    [InlineData("/ID [<><>] ")]         // present, both elements empty
    [InlineData("/ID [<>] ")]           // one element, empty — Count > 0, so the first is still read
    public void EncryptedDocumentWithNoUsableTrailerId_stillOpens(string idEntry)
    {
        var doc = BuildWithTrailerId(
            EmptyIdEncryptDict,
            idEntry,
            "<< /Type /Catalog /Pages 2 0 R /Probe 3 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Probe <cf0ef39eac6f37> >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        var probe = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        Assert.Equal(
            "ID-LESS",
            Encoding.ASCII.GetString(((PdfHexString)probe.Get(new PdfName("Probe"))!).Bytes.Span));
    }

    /// <summary>
    /// A one-element <c>/ID</c>. Table 15 calls for two, and every producer writes two, but Algorithm
    /// 2 step (e) reads only the first — so a truncated array is still perfectly usable and this
    /// document's key comes out identical to the corpus's. Requiring two elements would fall back to
    /// an empty <c>/ID[0]</c>, derive a different key, and report the correct password as wrong.
    /// </summary>
    /// <remarks>
    /// The rows above cannot show this: their <c>/ID[0]</c> is empty either way, so demanding a
    /// second element changes nothing about what gets hashed. This one carries real bytes.
    /// </remarks>
    [Fact]
    public void TrailerIdWithASingleElement_isStillReadAsTheFirstElement()
    {
        var doc = BuildWithTrailerId(
            Rc4EncryptDict,
            $"/ID [<{Convert.ToHexStringLower(Id0)}>] ",
            "<< /Type /Catalog /Pages 2 0 R /Probe 3 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Probe <{Convert.ToHexStringLower(Encrypt(3, 0, "ONE-ELEMENT-ID"u8.ToArray()))}> >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        var probe = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        Assert.Equal(
            "ONE-ELEMENT-ID",
            Encoding.ASCII.GetString(((PdfHexString)probe.Get(new PdfName("Probe"))!).Bytes.Span));
    }

    /// <summary>
    /// A revision that declared <c>/Encrypt</c> with a newer one that does not is unreadable either
    /// way — as plaintext every stream decodes to ciphertext, and as ciphertext the newest revision's
    /// own objects do — so the reader refuses it rather than guessing. The guard accumulates across
    /// every revision it walks, and this document is what makes the accumulation matter: the
    /// declaration is in the MIDDLE revision, so a guard that keeps only the last value it saw (the
    /// chain is walked newest to oldest, so that is the OLDEST revision) sees nothing and opens the
    /// file as plaintext.
    /// </summary>
    /// <remarks>
    /// Two revisions cannot show it — with the declaration in the older of two, "accumulated" and
    /// "last seen" are the same value.
    /// </remarks>
    [Fact]
    public void MiddleRevisionDeclaredEncrypt_andTheNewestDoesNot_isRefused()
    {
        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        var id = Convert.ToHexStringLower(Id0);

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        // Revision 1: no /Encrypt.
        var xref1 = (int)ms.Position;
        W($"xref\n0 3\n{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /ID [<{id}><{id}>] >>\nstartxref\n{xref1}\n%%EOF\n");

        // Revision 2: appends the encryption dictionary AND declares it.
        var o4 = (int)ms.Position;
        W($"4 0 obj\n{Rc4EncryptDict}\nendobj\n");
        var xref2 = (int)ms.Position;
        W($"xref\n4 1\n{o4:D10} 00000 n \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R /Prev {xref1} /ID [<{id}><{id}>] >>\n"
          + $"startxref\n{xref2}\n%%EOF\n");

        // Revision 3: appends an ordinary object and drops /Encrypt.
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Probe 1 >>\nendobj\n");
        var xref3 = (int)ms.Position;
        W($"xref\n3 1\n{o3:D10} 00000 n \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /Prev {xref2} /ID [<{id}><{id}>] >>\n"
          + $"startxref\n{xref3}\n%%EOF\n");

        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" }));

        Assert.Contains("earlier revision declares /Encrypt", ex.Message, StringComparison.Ordinal);
    }

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
        using var reader = PdfReader.Open(Load("enc-rc4-objstm.pdf"), new PdfReaderOptions { Password = "u" });

        var xrefStream = reader.ResolveStream(10);
        Assert.NotNull(xrefStream);
        Assert.Equal("XRef", ((PdfName)xrefStream!.Dictionary.Get(new PdfName("Type"))!).Value);

        // The load-bearing part: the body inflates, AND its rows are the ones the reader navigated
        // by. "It inflated" alone only proves the bytes were not RC4'd — it would accept any
        // inflatable wrong answer — so a row is read back and checked against the file.
        var decoded = reader.GetDecodedStreamData(xrefStream);
        Assert.NotNull(decoded);

        var w = (PdfArray)xrefStream.Dictionary.Get(new PdfName("W"))!;
        int Width(int i) => (int)((PdfInteger)w[i]!).Value;
        var rowLength = Width(0) + Width(1) + Width(2);
        Assert.Equal(0, decoded!.Length % rowLength);

        // Every type-1 row's field 2 is a byte offset, and "N 0 obj" must start there. On noise those
        // offsets land anywhere, so this is what separates real cross-reference bytes from bytes that
        // merely inflated.
        var file = Load("enc-rc4-objstm.pdf");
        var checkedRows = 0;
        for (var i = 0; i < decoded.Length; i += rowLength)
        {
            if (ReadField(decoded, i, Width(0)) != 1)
                continue;

            var offset = (int)ReadField(decoded, i + Width(0), Width(1));
            Assert.InRange(offset, 0, file.Length - 8);
            Assert.Matches(@"^\d+ \d+ obj", Encoding.Latin1.GetString(file, offset, 8));
            checkedRows++;
        }

        Assert.True(checkedRows > 0, "the decoded cross-reference stream held no in-use rows to check");
    }

    private static long ReadField(byte[] rows, int offset, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
            value = (value << 8) | rows[offset + i];
        return value;
    }

    /// <summary>
    /// The other half of §7.5.8.2, and the reason it is there: §7.5.5 has the trailer /ID readable
    /// without decrypting the file, so the copy of it in the cross-reference stream dictionary has
    /// to stay plaintext too.
    /// </summary>
    [Theory]
    [InlineData(false)]  // Resolve first
    [InlineData(true)]   // ResolveStream first — the order the Conformance package actually uses,
                         // and the one the exemption's own comment calls the dangerous one
    public void CrossReferenceStreamDictionary_idString_isNotDecrypted(bool streamFirst)
    {
        using var reader = PdfReader.Open(Load("enc-rc4-objstm.pdf"), new PdfReaderOptions { Password = "u" });

        if (streamFirst)
            _ = reader.ResolveStream(10);

        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(10, 0)));
        var id = Assert.IsType<PdfArray>(dict.Get(PdfName.ID));
        var first = Assert.IsType<PdfHexString>(id[0]);

        Assert.Equal("000102030405060708090a0b0c0d0e0f", Convert.ToHexStringLower(first.Bytes.Span));
    }

    /// <summary>
    /// The exemption belongs to the objects <c>XrefParser</c> actually read as cross-reference
    /// streams, not to anything carrying <c>/Type /XRef</c> — that key is the document author's to
    /// write. A page's content stream mislabelled this way would otherwise be handed to every
    /// content rule as ciphertext while a conforming viewer decrypted and rendered it: a way to hide
    /// what is really drawn from preflight.
    /// </summary>
    [Fact]
    public void OrdinaryStreamMislabelledAsCrossReferenceStream_isStillDecrypted()
    {
        var body = Encrypt(3, 0, "BT (secret) Tj ET"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Type /XRef /Length {body.Length} >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("BT (secret) Tj ET", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// The same, for the string half of the exemption: §7.5.8.2 exempts the cross-reference stream's
    /// own dictionary, not every dictionary that happens to be nested inside an object and claims
    /// <c>/Type /XRef</c>. Skipping the subtree would let a document keep arbitrarily much of itself
    /// out of the decrypt walk.
    /// </summary>
    [Fact]
    public void NestedDictionaryClaimingTypeXRef_stringsAreStillDecrypted()
    {
        var hidden = Encrypt(3, 0, "HIDDEN-TEXT"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Type /Page /Sneaky << /Type /XRef /S <{Convert.ToHexStringLower(hidden)}> >> >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        var nested = Assert.IsType<PdfDictionary>(dict.Get(new PdfName("Sneaky")));

        Assert.Equal(
            "HIDDEN-TEXT",
            Encoding.ASCII.GetString(((PdfHexString)nested.Get(new PdfName("S"))!).Bytes.Span));
    }

    /// <summary>
    /// The exemption belongs to the objects the MERGED cross-reference table resolves to a
    /// cross-reference stream — not to every object number that ever was one. A revision walk sees
    /// superseded revisions too, and an incremental update is free to reuse the number an older
    /// revision gave its cross-reference stream for ordinary encrypted content. Keyed on the number
    /// alone, that object stays exempt for the life of the reader and its content comes back as
    /// ciphertext with nothing to report.
    /// </summary>
    [Theory]
    [InlineData(4)]  // the update reuses the superseded cross-reference stream's number
    [InlineData(5)]  // the control: object 4 was never a cross-reference stream
    public void ObjectNumberReusedFromASupersededCrossReferenceStream_isStillDecrypted(int xrefStreamObjectNumber)
    {
        var body = Encrypt(4, 0, "REUSED-CONTENT"u8.ToArray());
        var doc = BuildSupersededXrefStreamThenReuse(xrefStreamObjectNumber, body);

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(4)!;

        Assert.Equal("REUSED-CONTENT", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    // Revision 1 ends in a cross-reference stream numbered `xrefStreamObjectNumber`; revision 2 is a
    // classic incremental update defining object 4 as an ordinary encrypted stream, chained back
    // with /Prev. Where the two numbers coincide, object 4's entry in the merged table points at
    // revision 2's offset rather than at the cross-reference stream's.
    private static byte[] BuildSupersededXrefStreamThenReuse(int xrefStreamObjectNumber, byte[] body)
    {
        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.5\n");

        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 0 obj\n{Rc4EncryptDict}\nendobj\n");

        var rows = new List<byte>();
        void Row(byte type, int field2, int field3) => rows.AddRange(
        [
            type,
            (byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2,
            (byte)(field3 >> 8), (byte)field3,
        ]);

        var xrefStreamOffset = (int)ms.Position;
        var size = Math.Max(xrefStreamObjectNumber, 4) + 1;
        Row(0, 0, 65535);
        Row(1, o1, 0);
        Row(1, o2, 0);
        Row(1, o3, 0);
        for (var n = 4; n < size; n++)
            Row(1, n == xrefStreamObjectNumber ? xrefStreamOffset : 0, 0);

        var rowBytes = rows.ToArray();
        W($"{xrefStreamObjectNumber} 0 obj\n<< /Type /XRef /Size {size} /W [1 4 2] /Root 1 0 R /Encrypt 3 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] /Length {rowBytes.Length} >>\n"
          + "stream\n");
        ms.Write(rowBytes);
        W("\nendstream\nendobj\n");
        W($"startxref\n{xrefStreamOffset}\n%%EOF\n");

        var o4 = (int)ms.Position;
        W($"4 0 obj\n<< /Length {body.Length} >>\nstream\n{Encoding.Latin1.GetString(body)}\nendstream\nendobj\n");
        var updateXref = (int)ms.Position;
        W($"xref\n4 1\n{o4:D10} 00000 n \n");
        W($"trailer\n<< /Size {Math.Max(size, 5)} /Root 1 0 R /Encrypt 3 0 R /Prev {xrefStreamOffset} "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{updateXref}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// A dictionary claiming <c>/Type /XRef</c> at the TOP level of an object — the shape the
    /// exemption's own design note argues against, since that key is the document author's to write.
    /// The sibling tests cover the stream body and a nested dictionary; this is the third shape, the
    /// object's own strings coming back through <c>Resolve</c>.
    /// </summary>
    [Fact]
    public void TopLevelDictionaryClaimingTypeXRef_stringsAreStillDecrypted()
    {
        var hidden = Encrypt(3, 0, "HIDDEN-AT-TOP-LEVEL"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Type /XRef /S <{Convert.ToHexStringLower(hidden)}> >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));

        Assert.Equal(
            "HIDDEN-AT-TOP-LEVEL",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("S"))!).Bytes.Span));
    }

    // ── /StrF and /StmF naming different crypt filters (ISO 32000-2 Table 20) ───────────────────

    /// <summary>
    /// A document may use one cipher for strings and another for streams, and the two have to be
    /// applied to the right halves. Reporting them separately is not enough — <c>Encryption.StreamCipher</c>
    /// and <c>StringCipher</c> can both be right while <c>DecryptString</c> reaches for the stream
    /// filter — so this asserts the plaintext of a real string and a real stream in one document.
    ///
    /// <para>
    /// The string's ciphertext comes from this test's own RC4 and its own Algorithm 1 key
    /// derivation, not from the reader's decryptor: built the convenient way, a decryptor that picks
    /// the wrong filter would pick it for the test's encryptor too, and the error would cancel.
    /// </para>
    /// </summary>
    [Fact]
    public void StringsAndStreamsUnderDifferentCryptFilters_eachUseTheirOwn()
    {
        var probe = EncryptIndependently(3, 0, "STRING-VIA-RC4"u8.ToArray());
        var body = EncryptAes(3, 0, "STREAM-VIA-AES"u8.ToArray());

        var doc = BuildWith(
            "<< /Filter /Standard /V 4 /R 4 /Length 128 "
            + "/CF << /StmCF << /CFM /AESV2 /Length 16 >> /StrCF << /CFM /V2 /Length 16 >> >> "
            + "/StmF /StmCF /StrF /StrCF "
            + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
            + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>",
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /Probe <{Convert.ToHexStringLower(probe)}> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal(
            "STRING-VIA-RC4",
            Encoding.ASCII.GetString(((PdfHexString)stream.Dictionary.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("STREAM-VIA-AES", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// The signature exemption applies to signature dictionaries, and <c>/Contents</c> is a common
    /// key elsewhere: ISO 32000-1 Table 168 makes an annotation's <c>/Contents</c> a text string, and
    /// a commented or form-filled document is full of them. Exempting on the strength of any
    /// <c>/Type</c> at all would hand every note's text back as ciphertext.
    /// </summary>
    [Fact]
    public void AnnotationContents_isNotMistakenForASignature()
    {
        var note = Encrypt(3, 0, "reviewer comment"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Type /Annot /Subtype /Text /Rect [0 0 1 1] /Contents <{Convert.ToHexStringLower(note)}> >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var annot = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));

        Assert.Equal(
            "reviewer comment",
            Encoding.ASCII.GetString(((PdfHexString)annot.Get(PdfName.Contents)!).Bytes.Span));
    }

    // ── One decrypt walk, whichever accessor gets there first ───────────────────────────────────

    /// <summary>
    /// <c>Resolve</c> and <c>ResolveStream</c> are two ways into the same object, and both populate
    /// the object cache. If only one of them decrypts, whichever the caller happens to use first
    /// decides for the life of the reader whether that stream dictionary's strings are plaintext or
    /// ciphertext — and the Conformance package resolves streams before objects, so the ciphertext
    /// ordering was the usual one. Both orders are asserted here; neither may return ciphertext, and
    /// neither may decrypt twice (RC4 double-decryption is silent — it returns the input).
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StreamDictionaryStrings_decryptOnce_regardlessOfAccessorOrder(bool streamFirst)
    {
        var probe = Encrypt(3, 0, "PROBE-STRING"u8.ToArray());
        var body = Encrypt(3, 0, "BODY"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /Probe <{Convert.ToHexStringLower(probe)}> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        string FromStream() =>
            Encoding.ASCII.GetString(
                ((PdfHexString)reader.ResolveStream(3)!.Dictionary.Get(new PdfName("Probe"))!).Bytes.Span);
        string FromResolve() =>
            Encoding.ASCII.GetString(
                ((PdfHexString)((PdfDictionary)reader.Resolve(new PdfIndirectReference(3, 0))!)
                    .Get(new PdfName("Probe"))!).Bytes.Span);

        var first = streamFirst ? FromStream() : FromResolve();
        var second = streamFirst ? FromResolve() : FromStream();

        Assert.Equal("PROBE-STRING", first);
        Assert.Equal("PROBE-STRING", second);
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
        var fileName = Encrypt(3, 0, "ext.dat"u8.ToArray());
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /F <{Convert.ToHexStringLower(fileName)}> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
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
        // /F names the external file and is itself an ordinary string, so on an encrypted document
        // it is encrypted like any other — only the stream's CONTENTS are exempt. Left in the clear
        // this document would be malformed, and the test would fail on the file name rather than on
        // the body it means to exercise.
        var fileName = EncryptAes(3, 0, "ext.dat"u8.ToArray());
        var body = "abc"u8.ToArray();
        var doc = BuildWith(AesEncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /F <{Convert.ToHexStringLower(fileName)}> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("abc", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));

        // And the dictionary around it still decrypts, which is what makes the exemption specific to
        // the body rather than to the whole object.
        Assert.Equal(
            "ext.dat",
            Encoding.ASCII.GetString(((PdfHexString)stream.Dictionary.Get(new PdfName("F"))!).Bytes.Span));
    }

    /// <summary>
    /// The other half of "a string or a dictionary", which nothing covered: both external-file tests
    /// above use a string, and the only dictionary row in the suite is the REJECTING direction
    /// (<see cref="StreamWithNonFileSpecF_isStillDecrypted"/>, whose /F is a number). Dropping
    /// <c>or PdfDictionary</c> from the clause therefore left the whole solution green, while turning
    /// every attachment whose /F is a full file specification into a stream the reader decrypts.
    /// </summary>
    /// <remarks>
    /// A file specification is a dictionary whenever it carries anything beyond the path — /FS, /EF,
    /// /Desc (ISO 32000-1 §7.11.3) — so this is not an exotic shape. Under AES the mistake is loud,
    /// which is why this mirrors the AES test rather than the RC4 one: a three-byte body is not an IV
    /// followed by whole blocks, so the mutant throws on a legal document instead of quietly handing
    /// back noise.
    /// </remarks>
    [Fact]
    public void ExternalFileStreamNamedByAFileSpecificationDictionary_underAes_doesNotThrow()
    {
        // The file name is a string inside the stream's own dictionary, so it is encrypted like any
        // other string in that object — only the stream's CONTENTS are exempt.
        var fileName = EncryptAes(3, 0, "ext.dat"u8.ToArray());
        var body = "abc"u8.ToArray();
        var doc = BuildWith(AesEncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {body.Length} /F << /Type /Filespec /F <{Convert.ToHexStringLower(fileName)}> >> >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("abc", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));

        // And the specification's own string still decrypts, so the exemption is the body's alone.
        var spec = (PdfDictionary)stream.Dictionary.Get(new PdfName("F"))!;
        Assert.Equal(
            "ext.dat",
            Encoding.ASCII.GetString(((PdfHexString)spec.Get(new PdfName("F"))!).Bytes.Span));
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

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("REAL-CONTENT", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// <c>/Crypt</c> is a no-op in the ordinary filter chain — the decryption it names happens
    /// before the chain runs — so a chain of <c>[/Crypt /FlateDecode]</c> has to decode to exactly
    /// what <c>[/FlateDecode]</c> would. On an UNENCRYPTED document that is observable by value,
    /// which is the point: <c>EncryptedReaderTests.CryptIdentityFilter_onAStream_bypassesDecryption_endToEnd</c>
    /// tolerates an exception as a pass, so it cannot tell "/Crypt handled, Flate rejected
    /// ciphertext" from "/Crypt not handled at all" — with the passthrough removed, it still passes.
    /// </summary>
    [Fact]
    public void CryptFilter_inAChain_isANoOp_andTheRestOfTheChainStillDecodes()
    {
        var plaintext = "Hello from behind a /Crypt filter."u8.ToArray();

        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(plaintext);
        var body = compressed.ToArray();

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 0 obj\n<< /Length {body.Length} /Filter [/Crypt /FlateDecode] >>\nstream\n");
        ms.Write(body);
        W("\nendstream\nendobj\n");
        var xref = (int)ms.Position;
        W($"xref\n0 4\n{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n{o3:D10} 00000 n \n");
        W("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray());
        var stream = reader.ResolveStream(3)!;

        Assert.Equal(
            "Hello from behind a /Crypt filter.",
            Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// The catalog inside an object stream — the layout every modern producer emits, and one no
    /// fixture here can carry because qpdf writes the catalog uncompressed whenever it encrypts.
    /// Getting to <c>/Root</c> decodes a stream, so anything the decode path consults about the
    /// document as a whole has to cope with being asked before the catalog exists.
    /// </summary>
    [Fact]
    public void EncryptedDocumentWithItsCatalogInsideAnObjectStream_opens()
    {
        var doc = HandBuiltEncryptedDocuments.BuildCatalogInObjectStream();

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        Assert.Equal("Catalog", ((PdfName)reader.Catalog.Get(new PdfName("Type"))!).Value);
        var probe = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(5, 0)));
        Assert.Equal(
            "OBJSTM-CATALOG",
            Encoding.ASCII.GetString(((PdfHexString)probe.Get(new PdfName("Probe"))!).Bytes.Span));
    }

    /// <summary>
    /// The object-stream container obeys the same identity rule as every other stream when its own
    /// header generation disagrees with the cross-reference table's: ISO 32000-1 §7.6.2 Algorithm 1
    /// has one identity per object, and the container's body is decrypted under whatever the
    /// <c>ParsedStream</c> carries. Both directions of the rule are here, and each was crossed by
    /// nothing.
    /// </summary>
    /// <remarks>
    /// <see cref="XrefGenerationDiffersFromObjectHeader_dictionaryAndBodyShareOneIdentity"/> builds
    /// the same disagreement on an ordinary stream, which never reaches the object-stream loader's
    /// own copy of the rule; <see cref="EncryptedDocumentWithItsCatalogInsideAnObjectStream_opens"/>
    /// reaches that copy but is generation 0 throughout, so the header value and the table value are
    /// the same answer and neither row can fail. Between them the joint went untested: gutting the
    /// restamp left the whole solution green, on the very clause whose comment claims a document
    /// "decodes correctly through ResolveStream and incorrectly here". So did forcing the parser to
    /// stamp every stream generation 0, which only the second row below can see.
    /// </remarks>
    [Theory]
    // The table can express a generation, so the table wins (#192) and the header's 5 is ignored.
    [InlineData(5, 0L)]
    // The table's field cannot hold 65536, so XrefParser records it as unknown and the object's own
    // header is all that is left to go on. This is the only row in the suite where the generation
    // the parser stamped on the stream is the one actually used.
    [InlineData(2, 65536L)]
    public void ObjectStreamContainerWithADisagreeingHeaderGeneration_usesOneIdentityForItsBody(
        int headerGeneration, long xrefGeneration)
    {
        var doc = HandBuiltEncryptedDocuments.BuildCatalogInObjectStream(headerGeneration, xrefGeneration);

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        // Reaching the catalog at all means the container decrypted and its members parsed; keyed on
        // the other generation the body is noise and /Type is not there to read.
        Assert.Equal("Catalog", ((PdfName)reader.Catalog.Get(new PdfName("Type"))!).Value);
    }

    // BuildCatalogInObjectStream moved to HandBuiltEncryptedDocuments — #184 PR3's reconstruction
    // tests need the same document, and this is the one shape no committed fixture can carry (see
    // that class's own doc comment for why).

    // ── /EncryptMetadata false (ISO 32000-1 §7.6.5) ─────────────────────────────────────────────

    /// <summary>
    /// Both cleartext-metadata fixtures existed, and both were verified byte-for-byte — but nothing
    /// ever read that stream back THROUGH the reader, so the flag was decoration: hardcoding
    /// <c>_encryptMetadata = true</c> passed the whole suite while making this document unreadable
    /// (a 665-byte plaintext body is not an IV plus whole AES blocks, so decoding it throws).
    /// The pair below is the point: the same XMP comes out of the fixture that leaves the metadata
    /// in the clear and the one that encrypts it, which fails in both directions if the flag is
    /// ignored either way.
    /// </summary>
    [Theory]
    [InlineData("enc-aes-128-cleartextmd.pdf")]
    [InlineData("enc-aes-128.pdf")]
    public void MetadataStream_decodesToTheSameXmp_whetherOrNotItIsEncrypted(string fixture)
    {
        using var reader = PdfReader.Open(Load(fixture), new PdfReaderOptions { Password = "u" });

        var metadataRef = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(new PdfName("Metadata")));
        var metadata = reader.ResolveStream(metadataRef)!;
        var xmp = Encoding.UTF8.GetString(reader.GetDecodedStreamData(metadata)!);

        Assert.StartsWith("<?xpacket", xmp, StringComparison.Ordinal);
        Assert.Contains("<x:xmpmeta", xmp, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/EncryptMetadata</c> false exempts the DOCUMENT's metadata stream — the object the catalog
    /// names — and nothing else. A page, an XObject or a form field may carry metadata of its own,
    /// with the same <c>/Type</c>, and those stay encrypted: qpdf's <c>--cleartext-metadata</c>
    /// leaves the catalog's in the clear and encrypts the rest. Exempting by <c>/Type</c> hands a
    /// page's metadata back as ciphertext under RC4, and throws under AES.
    ///
    /// <para>
    /// Built by appending a revision to the real fixture rather than synthesising a document:
    /// <c>/EncryptMetadata false</c> feeds Algorithm 2 at R4, so a hand-built dictionary reusing
    /// another fixture's <c>/O</c> and <c>/U</c> could not authenticate at all.
    /// </para>
    /// </summary>
    [Fact]
    public void ComponentMetadataStream_isDecrypted_evenWhenEncryptMetadataIsFalse()
    {
        const string ComponentXmp = "<?xpacket page-level ?>";

        var fixture = Load("enc-aes-128-cleartextmd.pdf");
        var componentBody = EncryptAesWith(FileKeyOf("enc-aes-128-cleartextmd.pdf"), 9, 0,
            Encoding.ASCII.GetBytes(ComponentXmp));

        var ms = new MemoryStream();
        ms.Write(fixture);
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));

        var o9 = (int)ms.Position;
        W($"9 0 obj\n<< /Type /Metadata /Subtype /XML /Length {componentBody.Length} >>\nstream\n");
        ms.Write(componentBody);
        W($"\nendstream\nendobj\n");

        var previousStartxref = int.Parse(
            Encoding.Latin1.GetString(fixture).Split("startxref")[^1].Trim().Split('%')[0].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        var xref = (int)ms.Position;
        W($"xref\n9 1\n{o9:D10} 00000 n \n");
        W($"trailer\n<< /Size 10 /Root 1 0 R /Encrypt 8 0 R /Prev {previousStartxref} "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" });

        // The component's metadata: encrypted like anything else.
        Assert.Equal(
            ComponentXmp,
            Encoding.ASCII.GetString(reader.GetDecodedStreamData(reader.ResolveStream(9)!)!));

        // The catalog's: left in the clear at write time, so it must not be decrypted.
        var documentMetadata = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(new PdfName("Metadata")));
        var xmp = Encoding.UTF8.GetString(reader.GetDecodedStreamData(reader.ResolveStream(documentMetadata)!)!);
        Assert.StartsWith("<?xpacket", xmp, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>/EncryptMetadata</c> is "meaningful only when the value of V is 4" (ISO 32000-1 Table 21,
    /// the standard security handler's own entries), and <c>StandardSecurityDecryptor</c> already gates the key-derivation
    /// half of it on the revision. A stray copy on a <c>/V 2</c> document — a producer downgrading
    /// and carrying the key across — says nothing, and honouring it would hand the metadata stream
    /// back as ciphertext.
    /// </summary>
    [Fact]
    public void EncryptMetadataFalse_onAV2Document_saysNothing_andTheMetadataStreamDecrypts()
    {
        var body = Encrypt(4, 0, "<?xpacket meaningless-below-v4 ?>"u8.ToArray());

        var doc = BuildWith(
            Rc4EncryptDict.Replace(" /V 2 >>", " /V 2 /EncryptMetadata false >>", StringComparison.Ordinal),
            "<< /Type /Catalog /Pages 2 0 R /Metadata 4 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            "<< /Probe 1 >>",
            $"<< /Type /Metadata /Subtype /XML /Length {body.Length} >>\n"
            + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });

        Assert.Equal(
            "<?xpacket meaningless-below-v4 ?>",
            Encoding.ASCII.GetString(reader.GetDecodedStreamData(reader.ResolveStream(4)!)!));
    }

    /// <summary>
    /// The two halves of round three's fix, in the one configuration where they interact: a catalog
    /// inside an object stream AND a live <c>/EncryptMetadata false</c> exemption. Reaching the
    /// catalog decodes the object stream, which asks whether that stream is the document's metadata
    /// — with no catalog yet to answer from. The bare null-guard is pinned by the sibling test above,
    /// which also decodes an object-stream catalog; what only this document can show is that the "no"
    /// forced by that guard is not the memoised answer. The lookup is cached on first use, so a fix
    /// that set <c>_documentMetadataResolved</c> before the catalog existed would leave the exemption
    /// switched off for the rest of the document's life, and the metadata stream below would come
    /// back as noise rather than XMP.
    /// </summary>
    [Fact]
    public void CatalogInsideAnObjectStream_withEncryptMetadataFalse_opens()
    {
        var fixture = Load("enc-aes-128-cleartextmd.pdf");
        var fileKey = FileKeyOf("enc-aes-128-cleartextmd.pdf");

        // Object 10 is an object stream holding a replacement catalog (object 1), still naming the
        // fixture's own cleartext metadata stream; object 11 is the cross-reference stream that puts
        // object 1 inside it.
        var members = "<< /Type /Catalog /Pages 4 0 R /Metadata 3 0 R >>";
        var header = "1 0 ";
        var objStmBody = EncryptAesWith(fileKey, 10, 0, Encoding.Latin1.GetBytes(header + members));

        var ms = new MemoryStream();
        ms.Write(fixture);
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));

        var o10 = (int)ms.Position;
        W($"10 0 obj\n<< /Type /ObjStm /N 1 /First {header.Length} /Length {objStmBody.Length} >>\nstream\n");
        ms.Write(objStmBody);
        W("\nendstream\nendobj\n");

        var previousStartxref = int.Parse(
            Encoding.Latin1.GetString(fixture).Split("startxref")[^1].Trim().Split('%')[0].Trim(),
            System.Globalization.CultureInfo.InvariantCulture);

        var rows = new List<byte>();
        void Row(byte type, int field2, int field3) => rows.AddRange(
        [
            type,
            (byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2,
            (byte)(field3 >> 8), (byte)field3,
        ]);

        var xref = (int)ms.Position;
        Row(2, 10, 0);          // object 1: member 0 of object stream 10
        Row(1, o10, 0);         // object 10: the object stream
        Row(1, xref, 0);        // object 11: this cross-reference stream

        var rowBytes = rows.ToArray();
        W($"11 0 obj\n<< /Type /XRef /Size 12 /W [1 4 2] /Index [1 1 10 2] /Root 1 0 R /Encrypt 8 0 R "
          + $"/Prev {previousStartxref} "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] /Length {rowBytes.Length} >>\n"
          + "stream\n");
        ms.Write(rowBytes);
        W("\nendstream\nendobj\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" });

        Assert.Equal("Catalog", ((PdfName)reader.Catalog.Get(new PdfName("Type"))!).Value);

        // And the exemption still works once the catalog exists: the metadata stream is cleartext.
        var metadata = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(new PdfName("Metadata")));
        var xmp = Encoding.UTF8.GetString(reader.GetDecodedStreamData(reader.ResolveStream(metadata)!)!);
        Assert.StartsWith("<?xpacket", xmp, StringComparison.Ordinal);
    }

    // ── One identity per object (ISO 32000-1 §7.6.2, Algorithm 1) ───────────────────────────────

    /// <summary>
    /// The generation is half of the identity Algorithm 1 keys on, and nothing in the corpus proved
    /// the reader used it: every fixture object is generation 0, so a decryptor that hardcoded 0 in
    /// the per-object key passed the entire suite. The test below pins that the dictionary and the
    /// body AGREE on a generation; this one pins that the value is the document's. Object 3 is at
    /// generation 5 in its header and in the cross-reference table alike, and its string and body
    /// were encrypted under (3, 5).
    /// </summary>
    [Fact]
    public void ObjectAtANonZeroGeneration_decryptsUnderThatGeneration()
    {
        var probe = EncryptIndependently(3, 5, "STRING-AT-GEN-5"u8.ToArray());
        var body = EncryptIndependently(3, 5, "BODY-AT-GEN-5"u8.ToArray());

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 5 obj\n<< /Length {body.Length} /Probe <{Convert.ToHexStringLower(probe)}> >>\n"
          + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream\nendobj\n");
        var o4 = (int)ms.Position;
        W($"4 0 obj\n{Rc4EncryptDict}\nendobj\n");
        var xref = (int)ms.Position;
        W($"xref\n0 5\n{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n"
          + $"{o3:D10} 00005 n \n{o4:D10} 00000 n \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" });
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 5)));
        var stream = reader.ResolveStream(new PdfIndirectReference(3, 5))!;

        Assert.Equal(
            "STRING-AT-GEN-5",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("BODY-AT-GEN-5", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// The generation a STREAM's dictionary is decrypted under, when <c>ResolveStream</c> runs first.
    /// There are two copies of the decrypt walk — one in <c>Resolve</c>, one in the object-stream
    /// load path — and the suite crossed each on a different axis: the non-zero-generation test below
    /// calls <c>Resolve</c> first, so the other copy never runs, and the theory that does take this
    /// path uses an authoritative generation of 0, where the real value and a hardcoded 0 are the
    /// same thing. Hardcoding 0 in the second copy therefore passed everything.
    ///
    /// <para>That combination — stream first, non-zero generation — is the ordering
    /// <c>PreflightContext</c> actually uses, and getting it wrong keys the dictionary on generation
    /// 0 while the body uses 5, which is exactly what <c>Restamped</c> exists to prevent. Under RC4
    /// it returns plausible bytes and reports nothing.</para>
    /// </summary>
    [Fact]
    public void StreamAtANonZeroGeneration_resolvedAsAStreamFirst_decryptsUnderThatGeneration()
    {
        var body = EncryptIndependently(3, 5, "BODY-GEN5"u8.ToArray());
        var probe = EncryptIndependently(3, 5, "STR-GEN5"u8.ToArray());

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 5 obj\n<< /Length {body.Length} /Probe <{Convert.ToHexStringLower(probe)}> >>\n"
          + $"stream\n{Encoding.Latin1.GetString(body)}\nendstream\nendobj\n");
        var o4 = (int)ms.Position;
        W($"4 0 obj\n{Rc4EncryptDict}\nendobj\n");
        var xref = (int)ms.Position;
        W($"xref\n0 5\n{0:D10} 65535 f \n{o1:D10} 00000 n \n{o2:D10} 00000 n \n{o3:D10} 00005 n \n{o4:D10} 00000 n \n");
        W($"trailer\n<< /Size 5 /Root 1 0 R /Encrypt 4 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" });

        // Stream FIRST: this is what routes the dictionary through the second copy of the walk.
        var stream = reader.ResolveStream(new PdfIndirectReference(3, 5))!;
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 5)));

        Assert.Equal(
            "STR-GEN5",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("BODY-GEN5", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// An object whose header generation disagrees with the cross-reference table's. The table is
    /// the authority (#192), and it has to be the authority for both halves of the object: the
    /// dictionary is decrypted in Resolve, the body in DecryptedStreamView, and Algorithm 1 keys on
    /// one object number and one generation, not one per half. Keyed differently, one of the two
    /// comes out as noise.
    /// </summary>
    [Theory]
    [InlineData(false)]  // Resolve first, then the stream
    [InlineData(true)]   // ResolveStream first — the order PreflightContext actually uses, and the
                         // one that skips Resolve's restamping entirely
    public void XrefGenerationDiffersFromObjectHeader_dictionaryAndBodyShareOneIdentity(bool streamFirst)
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

        using var reader = PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" });

        ParsedStream stream;
        PdfDictionary dict;
        if (streamFirst)
        {
            stream = reader.ResolveStream(3)!;
            dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
        }
        else
        {
            dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 0)));
            stream = reader.ResolveStream(3)!;
        }

        Assert.Equal(
            "STR-GEN0",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("BODY-GEN0", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    /// <summary>
    /// A stream's <c>/Filter</c> and <c>/DecodeParms</c> may be indirect references, and the crypt
    /// filter resolution has to dereference them: reading them raw sees a reference where it expects
    /// a name and falls through to <c>/StmF</c>, which decrypts a stream the document asked to be
    /// left alone. The resolver takes a callback for exactly this and nothing exercised it.
    /// </summary>
    [Fact]
    public void StreamWithAnIndirectFilterAndDecodeParms_stillResolvesItsCryptFilter()
    {
        var plaintext = "INDIRECTLY-EXEMPT"u8.ToArray();

        var doc = BuildWith(Rc4V4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            $"<< /Length {plaintext.Length} /Filter 4 0 R /DecodeParms 5 0 R >>\n"
            + $"stream\n{Encoding.Latin1.GetString(plaintext)}\nendstream",
            "[/Crypt]",
            "<< /Type /CryptFilterDecodeParms /Name /Identity >>");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("INDIRECTLY-EXEMPT", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
    }

    // ── Recovering from a stale /Length ─────────────────────────────────────────────────────────

    /// <summary>
    /// A wrong <c>/Length</c> is an ordinary producer bug, and the parser is built to recover: if the
    /// declared length does not land on <c>endstream</c>, it scans for the marker instead. The
    /// recovery has to survive the byte it lands on being one the lexer refuses outright — <c>)</c>,
    /// <c>{</c>, <c>}</c>, a lone <c>&gt;</c> — which encryption turns from exotic into ordinary:
    /// ciphertext is high-entropy, so a stale length hits one of them a few percent of the time.
    /// Seen for real on poppler <c>pdfattach</c> output over an AES-256 document.
    /// </summary>
    [Theory]
    [InlineData(")")]
    [InlineData("{")]
    [InlineData("}")]
    [InlineData(">")]
    public void StreamWhoseDeclaredLengthLandsOnAByteTheLexerRefuses_isStillRecovered(string byteAtLength)
    {
        var body = $"AB{byteAtLength}CDEFGH";
        var doc = BuildWith(Rc4V4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            // /Length 2 is wrong on purpose: it puts the parser exactly on the awkward byte.
            $"<< /Length 2 /Filter /Crypt /DecodeParms << /Name /Identity >> >>\n"
            + $"stream\n{body}\nendstream");

        using var reader = PdfReader.Open(doc, new PdfReaderOptions { Password = "u" });
        var stream = reader.ResolveStream(3)!;

        Assert.Equal(body, Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
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
    // AES is not symmetric, so the AES document's strings cannot be produced the way the RC4 ones
    // are. The object key still comes from the library — ComputeObjectKey, the derivation under test
    // — and only the CBC encryption itself is the platform's, which is what a producer would use.
    private static byte[] EncryptAes(int objectNumber, int generation, byte[] plaintext)
        => EncryptAesWith(FileKeyOf("enc-rc4-128.pdf"), objectNumber, generation, plaintext);

    private static byte[] EncryptAesWith(byte[] fileKey, int objectNumber, int generation, byte[] plaintext)
    {
        var objectKey = StandardSecurityDecryptor.ComputeObjectKey(
            fileKey, objectNumber, generation, useAesSalt: true);

        using var aes = Aes.Create();
        aes.Key = objectKey;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        return [.. aes.IV, .. encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length)];
    }

    // Deliberately NOT the library's own key derivation, unlike Encrypt above. Every other document
    // here only needs ciphertext the reader will accept, and running the reader's decryptor backwards
    // is the honest way to get it. The generation test needs something else: it has to fail if the
    // reader mixes the WRONG generation into the object key, and a helper built on the same
    // derivation cancels that error out on both sides — the mutant survives and the test proves
    // nothing. So the object key here is computed from ISO 32000-1 §7.6.2 Algorithm 1 against the
    // platform's MD5, with a five-line RC4 (RFC-less, but the algorithm is four lines of state
    // permutation and is pinned by its own known-answer tests in the Kernel suite).
    private static byte[] EncryptIndependently(int objectNumber, int generation, byte[] plaintext)
    {
        var fileKey = FileKeyOf("enc-rc4-128.pdf");

        // Algorithm 1 step (b): file key || the low three bytes of the object number || the low two
        // of the generation, MD5'd, truncated to min(fileKey.Length + 5, 16).
        var input = new byte[fileKey.Length + 5];
        fileKey.CopyTo(input, 0);
        input[fileKey.Length] = (byte)objectNumber;
        input[fileKey.Length + 1] = (byte)(objectNumber >> 8);
        input[fileKey.Length + 2] = (byte)(objectNumber >> 16);
        input[fileKey.Length + 3] = (byte)generation;
        input[fileKey.Length + 4] = (byte)(generation >> 8);

        var objectKey = MD5.HashData(input)[..Math.Min(fileKey.Length + 5, 16)];
        return Rc4(objectKey, plaintext);
    }

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = new byte[256];
        for (var i = 0; i < 256; i++)
            s[i] = (byte)i;

        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }

        var output = new byte[data.Length];
        for (int n = 0, x = 0, y = 0; n < data.Length; n++)
        {
            x = (x + 1) & 0xFF;
            y = (y + s[x]) & 0xFF;
            (s[x], s[y]) = (s[y], s[x]);
            output[n] = (byte)(data[n] ^ s[(s[x] + s[y]) & 0xFF]);
        }

        return output;
    }

    // Copied out before the reader is disposed: Dispose zeroes the file key, so handing back the
    // array itself would hand back sixteen zero bytes.
    private static byte[] FileKeyOf(string fixture)
    {
        using var reader = PdfReader.Open(Load(fixture), new PdfReaderOptions { Password = "u" });
        var key = (byte[])typeof(PdfDocumentReader)
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return [.. key];
    }

    private static byte[] Encrypt(int objectNumber, int generation, byte[] plaintext)
    {
        using var reader = PdfReader.Open(Load("enc-rc4-128.pdf"), new PdfReaderOptions { Password = "u" });
        var type = typeof(PdfDocumentReader);
        var decryptor = (StandardSecurityDecryptor)type
            .GetField("_decryptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        var fileKey = (byte[])type
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return decryptor.DecryptString(fileKey, objectNumber, generation, plaintext);
    }

    private static byte[] BuildWith(string encryptDict, params string[] objBodies) =>
        BuildWithTrailerId(
            encryptDict,
            $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] ",
            objBodies);

    private static byte[] BuildWithTrailerId(string encryptDict, string idEntry, params string[] objBodies)
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
          + $"{idEntry}>>\n");
        W($"startxref\n{xref}\n%%EOF\n");
        return ms.ToArray();
    }
}
