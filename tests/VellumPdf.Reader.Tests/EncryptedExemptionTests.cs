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

        using var reader = PdfReader.Open(doc, "u");
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

        using var reader = PdfReader.Open(doc, "u");
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

        using var reader = PdfReader.Open(doc, "u");
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

        using var reader = PdfReader.Open(doc, "u");
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

        using var reader = PdfReader.Open(doc, "u");
        var stream = reader.ResolveStream(3)!;

        Assert.Equal(
            "STRING-VIA-RC4",
            Encoding.ASCII.GetString(((PdfHexString)stream.Dictionary.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("STREAM-VIA-AES", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
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

        using var reader = PdfReader.Open(doc, "u");

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

        using var reader = PdfReader.Open(doc, "u");
        var stream = reader.ResolveStream(3)!;

        Assert.Equal("abc", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));

        // And the dictionary around it still decrypts, which is what makes the exemption specific to
        // the body rather than to the whole object.
        Assert.Equal(
            "ext.dat",
            Encoding.ASCII.GetString(((PdfHexString)stream.Dictionary.Get(new PdfName("F"))!).Bytes.Span));
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
        var doc = BuildCatalogInObjectStream();

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal("Catalog", ((PdfName)reader.Catalog.Get(new PdfName("Type"))!).Value);
        var probe = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(5, 0)));
        Assert.Equal(
            "OBJSTM-CATALOG",
            Encoding.ASCII.GetString(((PdfHexString)probe.Get(new PdfName("Probe"))!).Bytes.Span));
    }

    // Object 1 is an /ObjStm holding the catalog (object 2) and the page tree (object 3); object 4 is
    // the cross-reference stream; object 5 is an ordinary encrypted object outside the container.
    private static byte[] BuildCatalogInObjectStream()
    {
        var members = "<< /Type /Catalog /Pages 3 0 R >> << /Type /Pages /Kids [] /Count 0 >>";
        var header = "2 0 3 34 ";
        var objStmBody = Encrypt(1, 0, Encoding.Latin1.GetBytes(header + members));

        var probe = Encrypt(5, 0, "OBJSTM-CATALOG"u8.ToArray());

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));
        W("%PDF-1.5\n");

        var o1 = (int)ms.Position;
        W($"1 0 obj\n<< /Type /ObjStm /N 2 /First {header.Length} /Length {objStmBody.Length} >>\nstream\n");
        ms.Write(objStmBody);
        W("\nendstream\nendobj\n");

        var o6 = (int)ms.Position;
        W($"6 0 obj\n{Rc4EncryptDict}\nendobj\n");

        var o5 = (int)ms.Position;
        W($"5 0 obj\n<< /Probe <{Convert.ToHexStringLower(probe)}> >>\nendobj\n");

        var rows = new List<byte>();
        void Row(byte type, int field2, int field3) => rows.AddRange(
        [
            type,
            (byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2,
            (byte)(field3 >> 8), (byte)field3,
        ]);

        var xrefOffset = (int)ms.Position;
        Row(0, 0, 65535);          // 0: free
        Row(1, o1, 0);             // 1: the object stream
        Row(2, 1, 0);              // 2: catalog, member 0 of object 1
        Row(2, 1, 1);              // 3: page tree, member 1 of object 1
        Row(1, xrefOffset, 0);     // 4: this cross-reference stream
        Row(1, o5, 0);             // 5: the probe object
        Row(1, o6, 0);             // 6: the /Encrypt dictionary

        var rowBytes = rows.ToArray();
        W($"4 0 obj\n<< /Type /XRef /Size 7 /W [1 4 2] /Root 2 0 R /Encrypt 6 0 R "
          + $"/ID [<{Convert.ToHexStringLower(Id0)}><{Convert.ToHexStringLower(Id0)}>] /Length {rowBytes.Length} >>\n"
          + "stream\n");
        ms.Write(rowBytes);
        W("\nendstream\nendobj\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

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
        using var reader = PdfReader.Open(Load(fixture), "u");

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

        using var reader = PdfReader.Open(ms.ToArray(), "u");

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
    /// <c>/EncryptMetadata</c> is "meaningful only when the value of V is 4" (ISO 32000-1 Table 20's
    /// standard handler row), and <c>StandardSecurityDecryptor</c> already gates the key-derivation
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

        using var reader = PdfReader.Open(doc, "u");

        Assert.Equal(
            "<?xpacket meaningless-below-v4 ?>",
            Encoding.ASCII.GetString(reader.GetDecodedStreamData(reader.ResolveStream(4)!)!));
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

        using var reader = PdfReader.Open(ms.ToArray(), "u");
        var dict = Assert.IsType<PdfDictionary>(reader.Resolve(new PdfIndirectReference(3, 5)));
        var stream = reader.ResolveStream(new PdfIndirectReference(3, 5))!;

        Assert.Equal(
            "STRING-AT-GEN-5",
            Encoding.ASCII.GetString(((PdfHexString)dict.Get(new PdfName("Probe"))!).Bytes.Span));
        Assert.Equal("BODY-AT-GEN-5", Encoding.ASCII.GetString(reader.GetDecodedStreamData(stream)!));
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

        using var reader = PdfReader.Open(ms.ToArray(), "u");

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
        var doc = BuildWith(Rc4EncryptDict,
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [] /Count 0 >>",
            // /Length 2 is wrong on purpose: it puts the parser exactly on the awkward byte.
            $"<< /Length 2 /Filter /Crypt /DecodeParms << /Name /Identity >> >>\n"
            + $"stream\n{body}\nendstream");

        using var reader = PdfReader.Open(doc, "u");
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
        using var reader = PdfReader.Open(Load(fixture), "u");
        var key = (byte[])typeof(PdfDocumentReader)
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return [.. key];
    }

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
