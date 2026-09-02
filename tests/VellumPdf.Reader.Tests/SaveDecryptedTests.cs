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

    /// <summary>
    /// ISO 32000-2 §12.7.4.1 makes <c>/FT</c> inheritable: a non-terminal field node can declare
    /// <c>/FT /Sig</c> once, with the actual <c>/V</c> living on a kid that carries no <c>/FT</c> of
    /// its own. A prior version of <c>CollectFieldSignatures</c> checked only the CURRENT node's own
    /// <c>/FT</c> and returned the instant it saw <c>/Sig</c> — whether or not that node had a
    /// <c>/V</c> — so it never descended to the kid that actually held one:
    /// <see cref="PdfDocumentReader.Signatures"/> reported zero, and the pre-fix guard (which trusted
    /// that count) let <c>SaveDecrypted</c> proceed without the opt-in, emitting the sig dict's
    /// <c>/Contents</c> ciphertext-if-any verbatim into "plaintext" output — the exact outcome the
    /// guard exists to prevent (#186 review round 2, defect 1).
    /// </summary>
    [Fact]
    public void SaveDecrypted_signatureReachedOnlyThroughInheritedFT_throwsWithoutOptIn()
    {
        var bytes = BuildDocumentWithInheritedFtSignatureField();
        using var reader = PdfReader.Open(bytes);

        // The public API's own correctness, not just the guard: the inheritance fix means
        // Signatures itself finds this signature now too.
        Assert.Single(reader.Signatures);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => reader.SaveDecrypted(ms));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void SaveDecrypted_signatureReachedOnlyThroughInheritedFT_savesWithOptIn()
    {
        var bytes = BuildDocumentWithInheritedFtSignatureField();
        using var reader = PdfReader.Open(bytes);

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms, new PdfSaveDecryptedOptions { AllowInvalidatingSignatures = true });

        Assert.True(ms.Length > 0);
    }

    // Object 4 is a NON-TERMINAL field: /FT /Sig, no /V of its own, one kid. Object 5 is the
    // TERMINAL kid: no /FT at all (inherited from object 4), carries the real /V. A field-tree walk
    // that only checks each node's OWN /FT misses the signature entirely — it lives one level below
    // the only node that names /FT /Sig.
    private static byte[] BuildDocumentWithInheritedFtSignatureField()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Fields [4 0 R] >>\nendobj\n");
        var o4 = (int)ms.Position;
        W("4 0 obj\n<< /FT /Sig /Kids [5 0 R] >>\nendobj\n");
        var o5 = (int)ms.Position;
        W("5 0 obj\n<< /Type /Annot /Subtype /Widget /Parent 4 0 R /V 6 0 R >>\nendobj\n");
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

    // ── Inline (non-indirect) signature value — the round-2 guard regression ───

    /// <summary>
    /// ISO 32000-2 Table 226 does not require a field's <c>/V</c> to be an indirect reference the
    /// way it requires <c>/Lock</c> and <c>/SV</c> in that same table to be, so a signature
    /// dictionary may legally sit INLINE inside its field. The field-tree walk
    /// (<see cref="PdfDocumentReader.Signatures"/>) already handles this fine —
    /// <c>ResolveValue</c> on a direct object just returns it — so <c>Signatures.Count</c> here is
    /// 1 either way; the round-2 guard rewrite's actual bug was dropping that check entirely
    /// rather than unioning it with the new object-graph scan (review round 3, high #1).
    /// </summary>
    [Fact]
    public void SaveDecrypted_inlineSignatureValue_plaintext_throwsWithoutOptIn()
    {
        var bytes = BuildDocumentWithInlineSignatureValue("<DEADBEEF>");
        using var reader = PdfReader.Open(bytes);
        Assert.Single(reader.Signatures);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => reader.SaveDecrypted(ms));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ms.Length);
    }

    /// <summary>
    /// The encrypted twin: a real <c>/V 4 /R 4</c> RC4 document whose signature dictionary's own
    /// <c>/Contents</c> is genuine ciphertext. <see cref="PdfDocumentReader"/>'s decrypt walk never
    /// decrypts a signature dictionary's <c>/Contents</c> on read (the exemption
    /// <c>IsSignatureDictionary</c> exists for) — inline or not, since the recursive walk checks the
    /// SAME structural shape at every nesting level — so if the guard failed to fire here,
    /// <c>SaveDecrypted</c> would copy this ciphertext verbatim into output that claims to be
    /// plaintext: the exact failure #186's signature guard exists to prevent.
    /// </summary>
    [Fact]
    public void SaveDecrypted_inlineSignatureValue_encrypted_throwsWithoutOptIn()
    {
        var plaintextMarker = "INLINE-SIG-DER-STANDIN-0123456789"u8.ToArray();
        var cipherContents = Rc4EncryptUnderV4Fixture(objectNumber: 4, generation: 0, plaintextMarker);
        var bytes = BuildEncryptedDocumentWithInlineSignatureValue(cipherContents);

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        Assert.Single(reader.Signatures);

        // Confirms the premise that makes this security-relevant: /Contents on this inline
        // signature dictionary is still ciphertext after the normal decrypt walk.
        var acroForm = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(new PdfName("AcroForm"))!));
        var fieldsArr = Assert.IsType<PdfArray>(reader.ResolveValue(acroForm.Get(new PdfName("Fields"))!));
        var field = Assert.IsType<PdfDictionary>(reader.ResolveValue(fieldsArr[0]));
        var vDict = Assert.IsType<PdfDictionary>(field.Get(new PdfName("V")));
        var contents = Assert.IsType<PdfHexString>(vDict.Get(PdfName.Contents));
        Assert.True(
            contents.Bytes.Span.SequenceEqual(cipherContents),
            "expected the signature-dictionary /Contents exemption to leave this as ciphertext on read");

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => reader.SaveDecrypted(ms));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ms.Length);
    }

    // Object 4's /V is a DIRECT (inline) dictionary literal, not "5 0 R" — the shape Table 226
    // permits and the old top-level-only AnyEmittedSignatureDictionary check could not see.
    private static byte[] BuildDocumentWithInlineSignatureValue(string contentsLiteral)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Fields [4 0 R] >>\nendobj\n");
        var o4 = (int)ms.Position;
        W("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /V << /Type /Sig "
          + $"/ByteRange [0 10 20 30] /Contents {contentsLiteral} >> >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 5\n");
        W("0000000000 65535 f \n");
        foreach (var o in new[] { o1, o2, o3, o4 })
            W($"{o:D10} 00000 n \n");
        W("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // Same shape as BuildDocumentWithInlineSignatureValue, plus a real /V 4 /R 4 RC4 /Encrypt
    // object (copied from enc-rc4-128-v4.pdf, same technique as the Crypt-strip KAT below).
    private static byte[] BuildEncryptedDocumentWithInlineSignatureValue(byte[] cipherContents)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Fields [4 0 R] >>\nendobj\n");
        var o4 = (int)ms.Position;
        W("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /V << /Type /Sig "
          + $"/ByteRange [0 10 20 30] /Contents <{Convert.ToHexString(cipherContents)}> >> >>\nendobj\n");
        var o5 = (int)ms.Position;
        W($"5 0 obj\n{Rc4V4EncryptDict}\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 6\n");
        W("0000000000 65535 f \n");
        foreach (var o in new[] { o1, o2, o3, o4, o5 })
            W($"{o:D10} 00000 n \n");
        // The first /ID element must match enc-rc4-128-v4.pdf's own — see the Crypt-strip KAT's
        // BuildHandBuiltRc4V4Document for why.
        W("trailer\n<< /Size 6 /Root 1 0 R /Encrypt 5 0 R "
          + "/ID [<000102030405060708090a0b0c0d0e0f><000102030405060708090a0b0c0d0e0f>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Signature guard is independent of /AcroForm reachability ────────────────

    /// <summary>
    /// A signature-shaped dictionary that <see cref="PdfDocumentReader.Signatures"/> cannot reach
    /// at all — no <c>/AcroForm</c> in this document — nested one level below the only object that
    /// reaches it, so only a RECURSIVE object-graph scan (not a top-level-only one) can still catch
    /// it. Isolates <c>AnyEmittedSignatureDictionary</c> as the sole line of defence: every guard
    /// test elsewhere in this file uses a document where <c>Signatures</c> ALSO finds the
    /// signature, so this is the one case that would have passed if the guard were reverted to
    /// <c>Signatures.Count &gt; 0</c> alone (review round 3, medium #3).
    /// </summary>
    [Fact]
    public void SaveDecrypted_signatureUnreachableFromAcroForm_stillThrowsWithoutOptIn()
    {
        var bytes = BuildDocumentWithOrphanedNestedSignature();
        using var reader = PdfReader.Open(bytes);
        Assert.Empty(reader.Signatures);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => reader.SaveDecrypted(ms));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ms.Length);

        using var ms2 = new MemoryStream();
        reader.SaveDecrypted(ms2, new PdfSaveDecryptedOptions { AllowInvalidatingSignatures = true });
        Assert.True(ms2.Length > 0);
    }

    // No /AcroForm anywhere. Object 2's OWN dictionary is not itself signature-shaped (no /Type
    // /Sig, no /ByteRange of its own); the signature dictionary lives one level down, under an
    // arbitrary key an /AcroForm walk would never think to look at.
    private static byte[] BuildDocumentWithOrphanedNestedSignature()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Wrapper 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /NotItselfASignature /Foo /Nested << /Type /Sig "
          + "/ByteRange [0 10 20 30] /Contents <DEADBEEF> >> >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 3\n");
        W("0000000000 65535 f \n");
        foreach (var o in new[] { o1, o2 })
            W($"{o:D10} 00000 n \n");
        W("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── #391: the Signatures.Count > 0 half of the guard is independently load-bearing ──────────

    /// <summary>
    /// A field-declared <c>/V</c> signature dictionary with no <c>/Type</c> and exactly one
    /// structural key, <c>/ByteRange</c> — no <c>/Contents</c> at all. <c>ExtractSignature</c> still
    /// records it (a byte range alone is enough, per its own "either field" check), so
    /// <c>Signatures.Count</c> is 1. <c>IsSignatureDictionary</c>'s <c>/Type</c>-less fallback needs
    /// BOTH a <c>/ByteRange</c> array AND a string <c>/Contents</c>, so this dictionary is invisible
    /// to <c>AnyEmittedSignatureDictionary</c>'s recursive scan. Reverting the guard to that scan
    /// alone — dropping its <c>Signatures.Count &gt; 0</c> half — would make <c>SaveDecrypted</c>
    /// proceed here without the opt-in, so this pins the throw the union depends on (#391).
    /// </summary>
    [Fact]
    public void SaveDecrypted_signatureValueMissingContents_stillThrowsWithoutOptIn()
    {
        var bytes = BuildDocumentWithByteRangeOnlySignatureValue();
        using var reader = PdfReader.Open(bytes);
        Assert.Single(reader.Signatures);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => reader.SaveDecrypted(ms));
        Assert.Contains("signature", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, ms.Length);

        using var ms2 = new MemoryStream();
        reader.SaveDecrypted(ms2, new PdfSaveDecryptedOptions { AllowInvalidatingSignatures = true });
        Assert.True(ms2.Length > 0);
    }

    // Object 6 carries neither /Type nor /Contents — /ByteRange is its only structural key.
    private static byte[] BuildDocumentWithByteRangeOnlySignatureValue()
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
        W("6 0 obj\n<< /ByteRange [0 10 20 30] >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 7\n");
        W("0000000000 65535 f \n");
        foreach (var o in new[] { o1, o2, o3, o4, o5, o6 })
            W($"{o:D10} 00000 n \n");
        W("trailer\n<< /Size 7 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── #391: the /V dedupe guards a widget-merged repeated /V target ────────────────────────────

    /// <summary>
    /// A widget-merged field: object 4 carries its own indirect <c>/V</c> AND a <c>/Kids</c> element
    /// (object 5) whose <c>/V</c> repeats the SAME target. Without
    /// <c>visitedSignatureValues</c> deduping by the <c>/V</c> target's object number,
    /// <c>CollectFieldSignatures</c> would record the one underlying signature twice — once from
    /// the parent, once from the kid inheriting <c>/FT /Sig</c> (#391).
    /// </summary>
    [Fact]
    public void Signatures_widgetMergedFieldRepeatingV_dedupesToOne()
    {
        var bytes = BuildWidgetMergedSignatureField();
        using var reader = PdfReader.Open(bytes);

        Assert.Single(reader.Signatures);
    }

    private static byte[] BuildWidgetMergedSignatureField()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R /AcroForm 3 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Fields [4 0 R] >>\nendobj\n");
        // Object 4 is both the field AND a widget carrying its own /V, with a /Kids entry (object
        // 5) whose /V names the SAME signature object 6 — the widget-merged shape.
        var o4 = (int)ms.Position;
        W("4 0 obj\n<< /Type /Annot /Subtype /Widget /FT /Sig /V 6 0 R /Kids [5 0 R] >>\nendobj\n");
        var o5 = (int)ms.Position;
        W("5 0 obj\n<< /Type /Annot /Subtype /Widget /Parent 4 0 R /V 6 0 R >>\nendobj\n");
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
        Assert.Contains($"object {badObjectNumber}", ex.Message, StringComparison.Ordinal);
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

    // ── Stream at a non-zero generation ─────────────────────────────────────

    /// <summary>
    /// The 17 encrypted fixtures are all generation 0 (qpdf normalises on write), and
    /// <c>Fixtures/ThirdParty/nonzero-generation.pdf</c>'s own non-zero object is a plain
    /// DICTIONARY (the catalog) — no committed fixture has a STREAM at a non-zero generation. That
    /// gap meant hard-coding generation 0 in <c>EmitStream</c> specifically (as opposed to
    /// <c>EmitObject</c>, which the catalog case already covers) passed every test in this suite: a
    /// generation-N&gt;0 stream would be written as <c>N 0 obj</c> while every reference to it stays
    /// <c>N G R</c>, producing a dangling reference nothing here would have caught (review round 3,
    /// medium #2). Hand-built rather than found, the same way the reconstructed-document and
    /// signature-policy cases above are.
    /// </summary>
    [Fact]
    public void SaveDecrypted_streamAtNonZeroGeneration_preservesTheGenerationInTheOutputXref()
    {
        var bytes = BuildDocumentWithStreamAtGeneration1();
        using var reader = PdfReader.Open(bytes);
        Assert.Equal(1, reader.GenerationOf(2)); // sanity-checks the fixture, not the method under test

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        Assert.Equal(1, reopened.GenerationOf(2));
    }

    // Object 2 is a STREAM recorded at generation 1 directly (no free-chain entry needed at
    // generation 0 first — this reader, like GenerationNumberTests's own hand-built fixtures,
    // accepts a live non-zero-generation row with no preceding free entry).
    private static byte[] BuildDocumentWithStreamAtGeneration1()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        var body = "STREAM-AT-GENERATION-ONE"u8.ToArray();

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Content 2 1 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W($"2 1 obj\n<< /Length {body.Length} >>\nstream\n");
        ms.Write(body);
        W("\nendstream\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 3\n");
        W("0000000000 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00001 n \n");
        W("trailer\n<< /Size 3 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    // ── Reconstructed documents are allowed (contrast: AppendRevision refuses) ──

    [Fact]
    public void SaveDecrypted_onAReconstructedDocument_producesADocumentThatReopensWithoutReconstruction()
    {
        using var doc = new PdfDocument();
        doc.AddPage(PageSize.A4);
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

    /// <summary>
    /// A whole-file <c>Contains("xpacket")</c> only proved the substring survived SOMEWHERE — it
    /// would pass just as well if the metadata stream were dropped and the bytes came from some
    /// other object entirely. Pinned to the actual <c>/Metadata</c> stream's decoded content instead
    /// (review round 2, low #14), byte-for-byte against the source reader's own copy.
    /// </summary>
    [Fact]
    public void SaveDecrypted_cleartextMetadataFixture_metadataStreamBytesSurviveExactly()
    {
        using var reader = OpenFixture("enc-256-cleartextmd.pdf", "u");
        var sourceMetadata = GetMetadataStream(reader);
        var sourceBytes = reader.GetDecodedStreamData(sourceMetadata)
            ?? reader.DecryptedStreamView(sourceMetadata).RawBody.ToArray();

        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        var reopenedMetadata = GetMetadataStream(reopened);
        var reopenedBytes = reopened.GetDecodedStreamData(reopenedMetadata)
            ?? reopened.DecryptedStreamView(reopenedMetadata).RawBody.ToArray();

        Assert.Equal(sourceBytes, reopenedBytes);
        Assert.Contains("xpacket", Encoding.ASCII.GetString(reopenedBytes), StringComparison.Ordinal);
    }

    private static ParsedStream GetMetadataStream(PdfDocumentReader reader)
    {
        var metadataRef = reader.Catalog.Get(new PdfName("Metadata"))!;
        return reader.ResolveStream(Assert.IsType<PdfIndirectReference>(metadataRef))
            ?? throw new InvalidOperationException("Metadata did not resolve to a stream.");
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

    /// <summary>
    /// Determinism must not depend on how much of the object graph a reader had already resolved
    /// (and therefore cached) before <c>SaveDecrypted</c> was called — a caller inspecting a document
    /// (reading <see cref="PdfDocumentReader.Signatures"/>, say) before deciding to save a decrypted
    /// copy is an ordinary usage pattern, not an edge case.
    /// </summary>
    [Fact]
    public void SaveDecrypted_warmReaderVsFreshReader_produceByteIdenticalOutput()
    {
        var bytes = Load("enc-aes-128.pdf");

        using var freshReader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        using var freshMs = new MemoryStream();
        freshReader.SaveDecrypted(freshMs);

        using var warmReader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        _ = warmReader.Signatures;
        foreach (var objectNumber in warmReader.ComputeEmitSet().Order().Take(3))
            warmReader.GenerationOf(objectNumber);
        using var warmMs = new MemoryStream();
        warmReader.SaveDecrypted(warmMs);

        Assert.Equal(freshMs.ToArray(), warmMs.ToArray());
    }

    // ── Null-argument guards ─────────────────────────────────────────────────

    [Fact]
    public void SaveDecrypted_nullDestination_throwsArgumentNull()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");
        Assert.Throws<ArgumentNullException>(() => reader.SaveDecrypted(null!));
        Assert.Throws<ArgumentNullException>(() => reader.SaveDecrypted(null!, new PdfSaveDecryptedOptions()));
    }

    [Fact]
    public void SaveDecrypted_nullOptions_throwsArgumentNull()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");
        using var ms = new MemoryStream();
        Assert.Throws<ArgumentNullException>(() => reader.SaveDecrypted(ms, null!));
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

    [Fact]
    public async Task SaveDecryptedAsync_onADisposedReader_throwsObjectDisposed()
    {
        var reader = OpenFixture("enc-aes-128.pdf", "u");
        reader.Dispose();

        using var ms = new MemoryStream();
        // ThrowsAsync, not Throws: an async method never throws synchronously to its caller, even
        // for a guard checked before the first await — the exception surfaces only when the
        // returned Task is observed.
        await Assert.ThrowsAsync<ObjectDisposedException>(() => reader.SaveDecryptedAsync(ms));
    }

    // ── Async twin ───────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="PdfDocumentReader.SaveDecryptedAsync(Stream)"/> and its 3-argument twin are both
    /// public API #186 mandated, and neither had any coverage before this (review round 2, defect
    /// 7). Serialisation is deterministic (see the warm/fresh case above), so byte-identical output
    /// against the sync overload is the whole correctness claim for the async path — it wraps the
    /// same core in <see cref="Task.Run(Action)"/> plus an async copy and adds nothing of its own to
    /// verify beyond that wiring and its cancellation behavior.
    /// </summary>
    [Fact]
    public async Task SaveDecryptedAsync_producesTheSameBytesAsTheSyncOverload()
    {
        using var syncReader = OpenFixture("enc-aes-128.pdf", "u");
        using var syncMs = new MemoryStream();
        syncReader.SaveDecrypted(syncMs);

        using var asyncReader = OpenFixture("enc-aes-128.pdf", "u");
        using var asyncMs = new MemoryStream();
        await asyncReader.SaveDecryptedAsync(asyncMs);

        Assert.Equal(syncMs.ToArray(), asyncMs.ToArray());
    }

    [Fact]
    public async Task SaveDecryptedAsync_noOptionsOverload_matchesExplicitDefaultOptions()
    {
        using var reader1 = OpenFixture("enc-aes-128.pdf", "u");
        using var ms1 = new MemoryStream();
        await reader1.SaveDecryptedAsync(ms1);

        using var reader2 = OpenFixture("enc-aes-128.pdf", "u");
        using var ms2 = new MemoryStream();
        await reader2.SaveDecryptedAsync(ms2, new PdfSaveDecryptedOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(ms1.ToArray(), ms2.ToArray());
    }

    /// <summary>
    /// A token already cancelled before <c>SaveDecryptedAsync</c> is even called must leave
    /// <paramref name="destination"/> untouched — <see cref="Task.Run(Action, CancellationToken)"/>
    /// returns a cancelled task without ever invoking the delegate when the token is already
    /// cancelled, so serialisation never starts.
    /// </summary>
    [Fact]
    public async Task SaveDecryptedAsync_preCancelledToken_throwsAndWritesNothing()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        using var ms = new MemoryStream();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.SaveDecryptedAsync(ms, new PdfSaveDecryptedOptions(), cts.Token));

        Assert.Equal(0, ms.Length);
    }

    /// <summary>
    /// The opposite of the pre-cancelled case above: cancellation that fires DURING the final
    /// buffer-to-destination copy, after serialisation has already succeeded. The doc comment on
    /// <see cref="PdfDocumentReader.SaveDecrypted(Stream)"/>'s "Cost" remarks says this can leave a
    /// genuinely-plaintext, truncated prefix already written — this pins that claim rather than
    /// leaving it undemonstrated (review round 3, low #6).
    /// </summary>
    [Fact]
    public async Task SaveDecryptedAsync_cancellationDuringFinalCopy_leavesAPlaintextPrefixWritten()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");
        using var cts = new CancellationTokenSource();
        using var underlying = new MemoryStream();
        var destination = new CancelAfterFirstWriteStream(underlying, cts);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.SaveDecryptedAsync(destination, new PdfSaveDecryptedOptions(), cts.Token));

        Assert.True(underlying.Length > 0, "expected at least a partial prefix to have reached the real destination");
    }

    /// <summary>
    /// Writes each chunk through to the real destination genuinely, THEN cancels — so the write
    /// itself always lands before the cancellation it triggers is observed. <c>Stream.CopyToAsync</c>
    /// checks the token again on its next <c>ReadAsync</c> call (even the one that only discovers
    /// end-of-stream), which is where the throw actually surfaces for a source small enough to copy
    /// in a single chunk.
    /// </summary>
    private sealed class CancelAfterFirstWriteStream(Stream inner, CancellationTokenSource cancelAfterWrite) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, CancellationToken.None).ConfigureAwait(false);
            cancelAfterWrite.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }

        // Stream.CopyToAsync's actual call chain reaches THIS byte[]-based overload, not the
        // ReadOnlyMemory<byte> one above — Stream's own base-class WriteAsync(byte[], ...) does
        // not delegate to it, so both need overriding for the cancellation to land where intended
        // instead of falling through to the synchronous Write(byte[], ...) below (which throws).
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => inner.Flush();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    [Fact]
    public async Task SaveDecryptedAsync_nullDestination_throwsArgumentNull()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");
        await Assert.ThrowsAsync<ArgumentNullException>(() => reader.SaveDecryptedAsync(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => reader.SaveDecryptedAsync(null!, new PdfSaveDecryptedOptions(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SaveDecryptedAsync_nullOptions_throwsArgumentNull()
    {
        using var reader = OpenFixture("enc-aes-128.pdf", "u");
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => reader.SaveDecryptedAsync(ms, null!, TestContext.Current.CancellationToken));
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
        var encryptedBytes = BuildDocumentWithDctImage(jpegBytes);

        using var encryptedReader = PdfReader.Open(encryptedBytes, new PdfReaderOptions { Password = "u" });

        // This document is built outside the SHA-pinned fixture corpus, so nothing else confirms
        // doc.Encrypt(...) actually took effect, or that the fixture carries the filter under test,
        // before trusting what SaveDecrypted did to it (review round 3, low #7).
        Assert.NotNull(encryptedReader.Encryption);
        var sourceImageStream = GetFirstImageStream(encryptedReader);
        var sourceFilter = Assert.IsType<PdfName>(sourceImageStream.Dictionary.Get(PdfName.Filter));
        Assert.True(sourceFilter.Equals(PdfName.DCTDecode), "expected the source fixture's own image to declare /Filter /DCTDecode");

        using var ms = new MemoryStream();
        encryptedReader.SaveDecrypted(ms);
        ms.Position = 0;

        using var decryptedReader = PdfReader.Open(ms.ToArray());
        Assert.Null(decryptedReader.Encryption);

        var decryptedImageStream = GetFirstImageStream(decryptedReader);
        var decryptedFilter = Assert.IsType<PdfName>(decryptedImageStream.Dictionary.Get(PdfName.Filter));
        Assert.True(decryptedFilter.Equals(PdfName.DCTDecode), "expected /Filter /DCTDecode to survive verbatim");

        // Against the literal known-answer bytes directly, not a second, separately-produced
        // never-encrypted artifact: comparing two PRODUCED documents to each other proves only that
        // they agree, which a shared bug in how both were built (e.g. RegisterImageXObject
        // corrupting the body the same way twice) would not disturb.
        Assert.True(
            decryptedImageStream.RawBody.Span.SequenceEqual(jpegBytes),
            "the decrypted image body must match the literal JPEG bytes byte-for-byte");
    }

    private static byte[] BuildDocumentWithDctImage(byte[] jpegBytes)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);
        var image = new PdfImageXObject(
            width: 8, height: 6, streamData: jpegBytes, filter: PdfName.DCTDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8);
        doc.RegisterImageXObject(page, image, "Im0");
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

    // ── Crypt-strip KAT, plus the indirect-target refusals and the non-first pin ──────────────

    /// <summary>
    /// A hand-built document whose content stream declares <c>/Filter [/Crypt /FlateDecode]</c> with
    /// a parallel <c>/DecodeParms</c> — the shape no real qpdf fixture carries (see the fixture
    /// README's "Known gaps"). Reuses <c>enc-rc4-128-v4.pdf</c>'s genuine <c>/V 4 /R 4</c> RC4
    /// <c>/Encrypt</c> dictionary and file key (RC4 is symmetric, so the same call that decrypts also
    /// encrypts) so the stream body is REAL ciphertext, not a stand-in. Pins the exact rebuilt
    /// <c>/DecodeParms</c> shape, not just that <c>/Crypt</c> is gone: a 2-element input array with
    /// the crypt filter's own parms first and a real filter's <c>null</c> placeholder second must
    /// come out as the 1-element array <c>[null]</c> — a stale, misaligned parms array (one that kept
    /// the crypt filter's own dictionary instead of dropping it, say) would still pass a check that
    /// only asked whether the body inflates.
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

        var stream = GetContentStream(reopened);

        var filter = stream.Dictionary.Get(PdfName.Filter);
        var filterNames = filter switch
        {
            PdfArray arr => Enumerable.Range(0, arr.Count).Select(i => ((PdfName)arr[i]).Value).ToList(),
            PdfName n => [n.Value],
            _ => throw new InvalidOperationException("unexpected /Filter shape"),
        };
        Assert.DoesNotContain("Crypt", filterNames);
        Assert.Equal(["FlateDecode"], filterNames);

        // The exact rebuilt shape (review round 2): [<< /Name /StdCF >> null] loses its FIRST
        // element (the crypt filter's own parms) and keeps the second (FlateDecode's, already
        // null — no predictor in play here), leaving a 1-element array holding null.
        var decodeParms = Assert.IsType<PdfArray>(stream.Dictionary.Get(new PdfName("DecodeParms")));
        Assert.Equal(1, decodeParms.Count);
        Assert.IsType<PdfNull>(decodeParms[0]);

        var decompressed = FlateDecompress(stream.RawBody.ToArray());
        Assert.Equal(plaintext, decompressed);
    }

    /// <summary>
    /// An indirect <c>/Filter</c> naming a chain beginning with <c>/Crypt</c> must refuse rather than
    /// rewrite it (a shared array could belong to another object too) — reachable and load-bearing,
    /// confirmed against a real <c>/V 4 /R 4</c> RC4 document rather than merely reasoned about.
    /// </summary>
    [Fact]
    public void SaveDecrypted_indirectFilterNeedingCryptStrip_throwsUnsupportedNamingTheObject()
    {
        var flateBody = FlateCompress("content"u8.ToArray());
        var cipherBody = Rc4EncryptUnderV4Fixture(objectNumber: 2, generation: 0, flateBody);
        var bytes = BuildHandBuiltRc4V4Document(cipherBody, filterEntry: "4 0 R", includeIndirectionTargets: true);

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        using var ms = new MemoryStream();
        var ex = Assert.Throws<UnsupportedPdfFeatureException>(() => reader.SaveDecrypted(ms));
        // "Object 2" alone is the prefix BOTH throw sites share — swapping which one fired would
        // still pass that check (review round 3, low #9), so assert the distinguishing remainder
        // that names /Filter specifically.
        Assert.Contains("Object 2: /Filter is an indirect reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ms.Length);
    }

    /// <summary>
    /// The indirect-<c>/DecodeParms</c> twin of the test above: <c>/Filter</c> is direct
    /// <c>[/Crypt /FlateDecode]</c>, but the parms paired with it are an indirect reference.
    /// </summary>
    [Fact]
    public void SaveDecrypted_indirectDecodeParmsNeedingCryptStrip_throwsUnsupportedNamingTheObject()
    {
        var flateBody = FlateCompress("content"u8.ToArray());
        var cipherBody = Rc4EncryptUnderV4Fixture(objectNumber: 2, generation: 0, flateBody);
        var bytes = BuildHandBuiltRc4V4Document(cipherBody, decodeParmsEntry: "5 0 R", includeIndirectionTargets: true);

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        using var ms = new MemoryStream();
        var ex = Assert.Throws<UnsupportedPdfFeatureException>(() => reader.SaveDecrypted(ms));
        // Same reasoning as the /Filter twin above: assert the distinguishing remainder, not just
        // the shared "Object 2" prefix.
        Assert.Contains("Object 2: /DecodeParms is an indirect reference", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, ms.Length);
    }

    /// <summary>
    /// ISO 32000-2 §7.4.10 requires <c>/Crypt</c> to be the FIRST filter when present; a document
    /// that puts it anywhere else is already malformed, and <c>RebuildStreamDictionary</c>
    /// deliberately does not special-case it — it is copied through unchanged rather than guessed
    /// at. Pinned so that behavior cannot drift silently: Table 26's own default (Identity for an
    /// unresolved crypt filter) is what makes leaving it in place benign rather than a leak.
    /// </summary>
    [Fact]
    public void SaveDecrypted_nonFirstCryptFilter_isCopiedThroughUnchanged()
    {
        var plaintext = "raw, unfiltered content"u8.ToArray();
        var cipherBody = Rc4EncryptUnderV4Fixture(objectNumber: 2, generation: 0, plaintext);
        var bytes = BuildHandBuiltRc4V4Document(
            cipherBody, filterEntry: "[/FlateDecode /Crypt]", decodeParmsEntry: "null");

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms); // must NOT throw — /Crypt here is malformed, not stripped
        ms.Position = 0;

        using var reopened = PdfReader.Open(ms.ToArray());
        var stream = GetContentStream(reopened);
        var filterArray = Assert.IsType<PdfArray>(stream.Dictionary.Get(PdfName.Filter));
        Assert.Equal(2, filterArray.Count);
        Assert.Equal("FlateDecode", Assert.IsType<PdfName>(filterArray[0]).Value);
        Assert.Equal("Crypt", Assert.IsType<PdfName>(filterArray[1]).Value);
    }

    private static ParsedStream GetContentStream(PdfDocumentReader reader)
    {
        var streamRef = Assert.IsType<PdfIndirectReference>(reader.Catalog.Get(new PdfName("Content")));
        return reader.ResolveStream(streamRef) ?? throw new InvalidOperationException("expected a stream");
    }

    // Object 1: catalog naming the content stream directly (no page tree needed — SaveDecrypted only
    // requires /Root to resolve to a dictionary). Object 2: the target stream, whose /Filter and
    // /DecodeParms entries are configurable to cover the direct, indirect-target, and non-first-
    // /Crypt shapes above. Object 3: the (genuine, copied) /Encrypt dictionary. Objects 4 and 5,
    // present only when an indirection target is needed, hold the array an indirect /Filter or
    // /DecodeParms points at.
    private static byte[] BuildHandBuiltRc4V4Document(
        byte[] cipherBody,
        string filterEntry = "[/Crypt /FlateDecode]",
        string decodeParmsEntry = "[<< /Name /StdCF >> null]",
        bool includeIndirectionTargets = false)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Content 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W($"2 0 obj\n<< /Filter {filterEntry} /DecodeParms {decodeParmsEntry} "
          + $"/Length {cipherBody.Length} >>\nstream\n");
        ms.Write(cipherBody);
        W("\nendstream\nendobj\n");
        var o3 = (int)ms.Position;
        W($"3 0 obj\n{Rc4V4EncryptDict}\nendobj\n");

        var offsets = new List<int> { o1, o2, o3 };
        var size = 4;
        if (includeIndirectionTargets)
        {
            var o4 = (int)ms.Position;
            W("4 0 obj\n[/Crypt /FlateDecode]\nendobj\n");
            var o5 = (int)ms.Position;
            W("5 0 obj\n[<< /Name /StdCF >> null]\nendobj\n");
            offsets.Add(o4);
            offsets.Add(o5);
            size = 6;
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {size}\n");
        W("0000000000 65535 f \n");
        foreach (var o in offsets)
            W($"{o:D10} 00000 n \n");
        // The first /ID element must match enc-rc4-128-v4.pdf's own, since Algorithm 2 folds it into
        // the file-key derivation this document's copied /O and /U were computed against.
        W($"trailer\n<< /Size {size} /Root 1 0 R /Encrypt 3 0 R "
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

    // Token scan over this document's own output (review round 2, low priority #9): unlike the
    // encrypted-fixture matrix, this document's /O and /U are KNOWN literal hex strings (copied
    // above), so a scan here checks specific bytes it can name, not merely "no token of this shape
    // survived by coincidence" the way a scan over an unrelated fixture's own /O/U would.
    [Fact]
    public void SaveDecrypted_output_containsNoEncryptionTokens_negativeControl()
    {
        var flateBody = FlateCompress("token scan negative control"u8.ToArray());
        var cipherBody = Rc4EncryptUnderV4Fixture(objectNumber: 2, generation: 0, flateBody);
        var bytes = BuildHandBuiltRc4V4Document(cipherBody);

        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = "u" });
        using var ms = new MemoryStream();
        reader.SaveDecrypted(ms);
        var output = Encoding.ASCII.GetString(ms.ToArray());

        // OrdinalIgnoreCase, not Ordinal: PdfHexString.WriteTo emits UPPERCASE hex (its Nibble
        // helper), so an Ordinal comparison against this LOWERCASE literal (copied verbatim from
        // the fixture's /Encrypt dictionary text) would never match regardless of whether /O and
        // /U actually survived — a vacuous control (review round 3, security lens).
        Assert.DoesNotContain(
            "2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1", output, StringComparison.OrdinalIgnoreCase);
    }

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
