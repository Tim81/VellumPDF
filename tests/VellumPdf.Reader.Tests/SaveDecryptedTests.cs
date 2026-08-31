// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Images;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Policy and edge-case coverage for <see cref="PdfDocumentReader.SaveDecrypted(Stream)"/> (#186)
/// that the fixture matrix in <see cref="SaveDecryptedFixtureRoundTripTests"/> cannot exercise:
/// the signature refusal, fail-loud resolve failure, reconstructed-document support, determinism,
/// and the two known-answer passthrough/strip cases the issue calls out by name.
/// </summary>
public sealed class SaveDecryptedTests
{
    // ── Signature policy ─────────────────────────────────────────────────────

    [Fact]
    public void SaveDecrypted_documentWithASignature_throwsWithoutOptIn()
    {
        var bytes = BuildDocumentWithOneSignatureField();
        using var reader = PdfReader.Open(bytes);
        Assert.Single(reader.Signatures);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => reader.SaveDecrypted(ms));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void SaveDecrypted_documentWithASignature_savesWithOptIn()
    {
        var bytes = BuildDocumentWithOneSignatureField();
        using var reader = PdfReader.Open(bytes);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms, new PdfSaveDecryptedOptions { AllowInvalidatingSignatures = true });

        Assert.True(ms.Length > 0);
        ms.Position = 0;
        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Single(reopened.Signatures);
    }

    // A minimal, unencrypted, single-revision document: a signature field with a /V dictionary
    // carrying /ByteRange and a hex /Contents — the shape CollectFieldSignatures/ExtractSignature
    // recognise (PdfDocumentReader.cs). The signature policy applies regardless of encryption
    // (#186: re-serialisation breaks /ByteRange either way), so this document is deliberately plain.
    private static byte[] BuildDocumentWithOneSignatureField()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 5 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Annots [4 0 R] >>\nendobj\n");
        var o4 = (int)ms.Position;
        W("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /V 6 0 R >>\nendobj\n");
        var o5 = (int)ms.Position;
        W("5 0 obj\n<< /Fields [4 0 R] >>\nendobj\n");
        var o6 = (int)ms.Position;
        W("6 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
          + "/ByteRange [0 10 20 30] /Contents <DEADBEEF> >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 7\n");
        W("0000000000 65535 f \n");
        foreach (var o in new[] { o1, o2, o3, o4, o5, o6 })
            W($"{o:D10} 00000 n \n");
        W("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Fail-loud on an unresolvable object ──────────────────────────────────

    [Fact]
    public void SaveDecrypted_objectWithAnUnresolvableOffset_throwsNamingTheObject_andWritesNothing()
    {
        var bytes = BuildDocumentWithOneBadOffset(out var badObjectNumber);

        // Opens fine — the constructor only resolves /Root, and object 3 is not it.
        using var reader = PdfReader.Open(bytes);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidDataException>(() => reader.SaveDecrypted(ms));
        Assert.Contains(badObjectNumber.ToString(), ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ms.Length);
    }

    private static byte[] BuildDocumentWithOneBadOffset(out int badObjectNumber)
    {
        badObjectNumber = 3;
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        // Object 3's real bytes exist in the file, but its xref entry (below) names an offset the
        // parser cannot use — the "object the cross-reference table declares but cannot be parsed"
        // case ComputeEmitSet and EmitObject both name in their own doc comments.
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 4\n");
        W("0000000000 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W("0000100000 00000 n \n"); // in range for the field, out of range for this tiny file
        W("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Reconstructed documents are allowed (contrast: AppendRevision refuses) ──

    [Fact]
    public void SaveDecrypted_onAReconstructedDocument_producesADocumentThatReopensWithoutReconstruction()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);
        _ = page;
        using var built = new MemoryStream();
        doc.Save(built);
        var builtBytes = built.ToArray();
        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(builtBytes);
        var damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(builtBytes, start, length);

        using var reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true });
        Assert.True(reader.WasReconstructed);

        // The contrast the design calls for: AppendRevision refuses a reconstructed document...
        Assert.Throws<InvalidOperationException>(() =>
            reader.AppendRevision([(1, 0, new PdfDictionary())]));

        // ...but SaveDecrypted does not.
        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray()); // no AllowReconstruction needed
        Assert.False(reopened.WasReconstructed);
        Assert.NotNull(reopened.Catalog);
    }

    private static (int Start, int Length) GetStartxrefDigits(byte[] bytes) =>
        XrefReconstructionTests.FindLastStartxrefDigits(bytes);

    // ── Unencrypted input round-trips ────────────────────────────────────────

    [Fact]
    public void SaveDecrypted_unencryptedInput_reopenedGraphMatchesTheSource()
    {
        using var reader = OpenFixture("plaintext-baseline.pdf", null);
        Assert.Null(reader.Encryption);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Null(reopened.Encryption);
        SaveDecryptedGraphComparer.AssertCatalogsEqual(reader, reopened);
    }

    // ── Cleartext metadata survives ──────────────────────────────────────────

    [Fact]
    public void SaveDecrypted_cleartextMetadataFixture_keepsXpacketBytes()
    {
        using var reader = OpenFixture("enc-256-cleartextmd.pdf", "u");

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var text = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("xpacket", text, StringComparison.Ordinal);
    }

    // ── Determinism ───────────────────────────────────────────────────────────

    [Fact]
    public void SaveDecrypted_calledTwice_producesByteIdenticalOutput()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");

        using var first = new MemoryStream();
        reader.SaveDecrypted(first);

        using var second = new MemoryStream();
        reader.SaveDecrypted(second);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    // ── Disposed reader ──────────────────────────────────────────────────────

    [Fact]
    public void SaveDecrypted_onADisposedReader_throwsObjectDisposed()
    {
        var reader = OpenFixture("enc-aes-128.pdf", "u");
        reader.Dispose();

        using var ms = new MemoryStream();
        Assert.Throws<ObjectDisposedException>(() => reader.SaveDecrypted(ms));
    }

    // ── DCT passthrough KAT ──────────────────────────────────────────────────

    /// <summary>
    /// An encrypted document containing a passthrough (DCTDecode) image, built with this library's
    /// own writer so the comparison has a genuinely unencrypted twin to check against — pins that
    /// <c>SaveDecrypted</c> emits the stream directly (never through <c>PdfStream.WriteTo</c>, which
    /// would re-Flate and corrupt it) and that the filter chain and body survive verbatim.
    /// </summary>
    [Fact]
    public void SaveDecrypted_passthroughDctImage_preservesFilterAndBodyVerbatim()
    {
        var jpegBytes = "NOT-A-REAL-JPEG-BUT-A-RECOGNISABLE-PASSTHROUGH-BODY-0123456789"u8.ToArray();

        var encryptedBytes = BuildDocumentWithDctImage(jpegBytes, encrypt: true);
        var plainBytes = BuildDocumentWithDctImage(jpegBytes, encrypt: false);

        using var encryptedReader = PdfReader.Open(encryptedBytes, new PdfReaderOptions { Password = "u" });
        using var plainReader = PdfReader.Open(plainBytes);

        using var ms = new MemoryStream();
        encryptedReader.SaveDecrypted(ms);
        ms.Position = 0;

        using var decryptedReader = PdfReader.Open(ms.ToArray());
        Assert.Null(decryptedReader.Encryption);

        var decryptedImageStream = GetFirstImageStream(decryptedReader);
        var plainImageStream = GetFirstImageStream(plainReader);

        var decryptedFilter = Assert.IsType<PdfName>(decryptedImageStream.Dictionary.Get(PdfName.Filter));
        Assert.True(decryptedFilter.Equals(PdfName.DCTDecode), "expected /Filter /DCTDecode to survive verbatim");
        Assert.True(
            decryptedImageStream.RawBody.Span.SequenceEqual(plainImageStream.RawBody.Span),
            "the decrypted image body must match the never-encrypted document's body byte-for-byte");
    }

    private static byte[] BuildDocumentWithDctImage(byte[] jpegBytes, bool encrypt)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);
        var image = new PdfImageXObject(
            width: 8, height: 6, streamData: jpegBytes, filter: PdfName.DCTDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8);
        doc.RegisterImageXObject(page, image, "Im0");

        if (encrypt)
            doc.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static ParsedStream GetFirstImageStream(PdfDocumentReader reader)
    {
        var pages = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!));
        var kids = Assert.IsType<PdfArray>(reader.ResolveValue(pages.Get(PdfName.Kids)!));
        var page = Assert.IsType<PdfDictionary>(reader.ResolveValue(kids[0]));
        var resources = Assert.IsType<PdfDictionary>(reader.ResolveValue(page.Get(PdfName.Resources)!));
        var xobjects = Assert.IsType<PdfDictionary>(reader.ResolveValue(resources.Get(PdfName.XObject)!));
        var imageRef = xobjects.Get(new PdfName("Im0"))!;
        return reader.ResolveStream(Assert.IsType<PdfIndirectReference>(imageRef))
            ?? throw new InvalidOperationException("Im0 did not resolve to a stream.");
    }

    // ── Crypt-strip KAT ──────────────────────────────────────────────────────

    /// <summary>
    /// A hand-built document whose content stream declares <c>/Filter [/Crypt /FlateDecode]</c> with
    /// a parallel <c>/DecodeParms</c> — the shape no real qpdf fixture carries (see the fixture
    /// README's "Known gaps"). Reuses <c>enc-rc4-128-v4.pdf</c>'s genuine <c>/V 4 /R 4</c> RC4
    /// <c>/Encrypt</c> dictionary and file key (RC4 is symmetric, so the same call that decrypts also
    /// encrypts) so the stream body is REAL ciphertext, not a stand-in.
    /// </summary>
    [Fact]
    public void SaveDecrypted_cryptFilterFirstInAnArray_stripsTheEntryAndKeepsTheBodyInflatable()
    {
        var plaintext = "BT /F1 12 Tf (Crypt strip KAT) Tj ET"u8.ToArray();
        var flateBody = FlateCompress(plaintext);
        var cipherBody = Rc4EncryptUnderV4Fixture(objectNumber: 2, generation: 0, flateBody);

        var bytes = BuildHandBuiltRc4V4Document(cipherBody);
        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Null(reopened.Encryption);

        var catalog = reopened.Catalog;
        var streamRef = Assert.IsType<PdfIndirectReference>(catalog.Get(new PdfName("Content")));
        var stream = reopened.ResolveStream(streamRef) ?? throw new InvalidOperationException("expected a stream");

        var filter = stream.Dictionary.Get(PdfName.Filter);
        var filterNames = filter switch
        {
            PdfArray arr => Enumerable.Range(0, arr.Count).Select(i => ((PdfName)arr[i]).Value).ToList(),
            PdfName n => [n.Value],
            _ => throw new InvalidOperationException("unexpected /Filter shape"),
        };
        Assert.DoesNotContain("Crypt", filterNames);

        var decompressed = FlateDecompress(stream.RawBody.ToArray());
        Assert.Equal(plaintext, decompressed);
    }

    // Object 1: catalog naming the content stream directly (no page tree needed — SaveDecrypted only
    // requires /Root to resolve to a dictionary). Object 2: the target stream. Object 3: the
    // (genuine, copied) /Encrypt dictionary.
    private static byte[] BuildHandBuiltRc4V4Document(byte[] cipherBody)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Content 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W($"2 0 obj\n<< /Filter [/Crypt /FlateDecode] /DecodeParms [<< /Name /StdCF >> null] "
          + $"/Length {cipherBody.Length} >>\nstream\n");
        ms.Write(cipherBody);
        W("\nendstream\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 0 obj\n{Rc4V4EncryptDict}\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 4\n");
        W("0000000000 65535 f \n");
        foreach (var o in new[] { o1, o2, o3 })
            W($"{o:D10} 00000 n \n");
        // The first /ID element must match enc-rc4-128-v4.pdf's own, since Algorithm 2 folds it into
        // the file-key derivation this document's copied /O and /U were computed against.
        W("trailer\n<< /Size 4 /Root 1 0 R /Encrypt 3 0 R "
          + "/ID [<000102030405060708090a0b0c0d0e0f><000102030405060708090a0b0c0d0e0f>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // Copied verbatim out of enc-rc4-128-v4.pdf's object 8 — /V 4 /R 4, RC4 via /CF /StdCF /CFM /V2.
    private const string Rc4V4EncryptDict =
        "<< /CF << /StdCF << /AuthEvent /DocOpen /CFM /V2 /Length 16 >> >> /Filter /Standard "
        + "/Length 128 /O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> /OE <> "
        + "/P -4 /R 4 /StmF /StdCF /StrF /StdCF "
        + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /UE <> /V 4 >>";

    // RC4 is symmetric: the same operation that decrypts enc-rc4-128-v4.pdf's own content encrypts
    // ours, under the SAME file key, once the /Encrypt dict, /ID, and password all match (they do —
    // Rc4V4EncryptDict and the /ID above are copied verbatim). Reaches the reader's private
    // decryptor/key via reflection, the same technique HandBuiltEncryptedDocuments.Encrypt uses for
    // its own (V2) fixture.
    private static byte[] Rc4EncryptUnderV4Fixture(int objectNumber, int generation, byte[] plaintext)
    {
        using var reader = PdfReader.Open(Load("enc-rc4-128-v4.pdf"), new PdfReaderOptions { Password = "u" });
        var type = typeof(PdfDocumentReader);
        var decryptor = (StandardSecurityDecryptor)type
            .GetField("_decryptor", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        var fileKey = (byte[])type
            .GetField("_fileKey", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(reader)!;
        return decryptor.DecryptString(fileKey, objectNumber, generation, plaintext);
    }

    private static byte[] FlateCompress(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] FlateDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new System.IO.Compression.ZLibStream(input, System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PdfDocumentReader OpenFixture(string name, string? password) =>
        PdfReader.Open(Load(name), new PdfReaderOptions { Password = password });

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
