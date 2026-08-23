// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Encryption;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Exercises the reader against the committed encrypted corpus (#99, #97): opens each fixture,
/// decrypts it, and compares against <c>plaintext-baseline.pdf</c> or a hand-verified expected
/// value — not merely that a read "succeeded". <see cref="EncryptedFixtureCorpusTests"/> guards the
/// fixture bytes themselves; this class exercises the decrypt path against them.
/// </summary>
public sealed class EncryptedReaderTests
{
    // Every row of the standard matrix, minus the two structurally-different fixtures
    // (enc-rc4-objstm.pdf has an object stream and cross-reference stream; enc-aes-128-nestedstrings.pdf
    // has an extra object and different numbering) — those two get their own dedicated tests below
    // because a plain "content stream equals baseline's" comparison does not fit either.
    public static TheoryData<string> StandardMatrixFixtures =>
    [
        "enc-rc4-40.pdf",
        "enc-rc4-128.pdf",
        "enc-rc4-128-v4.pdf",
        "enc-aes-128.pdf",
        "enc-aes-256-r5.pdf",
        "enc-aes-256-r6.pdf",
        "enc-aes-128-cleartextmd.pdf",
        "enc-256-cleartextmd.pdf",
    ];

    // ── Load-bearing: decrypted content matches the plaintext baseline ──────────────────────────

    [Theory]
    [MemberData(nameof(StandardMatrixFixtures))]
    public void Fixture_opensWithUserPassword_andPageContentMatchesBaseline(string name)
    {
        using var reader = PdfReader.Open(Load(name), "u");
        using var baseline = PdfReader.Open(Load("plaintext-baseline.pdf"));

        var content = GetPageContentBytes(reader);
        var baselineContent = GetPageContentBytes(baseline);

        Assert.Equal(baselineContent, content);
    }

    [Theory]
    [MemberData(nameof(StandardMatrixFixtures))]
    public void Fixture_opensWithOwnerPassword_andPageContentMatchesBaseline(string name)
    {
        using var reader = PdfReader.Open(Load(name), "o");
        using var baseline = PdfReader.Open(Load("plaintext-baseline.pdf"));

        Assert.Equal(GetPageContentBytes(baseline), GetPageContentBytes(reader));
    }

    /// <summary>
    /// The RC4 + object-stream + cross-reference-stream fixture, checked the same way as the
    /// classic-xref rows above. This also positively exercises "a cross-reference stream is never
    /// decrypted" — the xref stream must be read correctly (as plaintext) merely to LOCATE the
    /// objects this comparison resolves at all; if it were wrongly run through the decrypt path, the
    /// FlateDecode-compressed xref stream body would come out as decryption-corrupted garbage and
    /// fail to inflate, so the file would not open in the first place.
    /// </summary>
    [Fact]
    public void ObjectStreamAndXrefStreamFixture_opensWithUserPassword_andPageContentMatchesBaseline()
    {
        using var reader = PdfReader.Open(Load("enc-rc4-objstm.pdf"), "u");
        using var baseline = PdfReader.Open(Load("plaintext-baseline.pdf"));

        Assert.Equal(GetPageContentBytes(baseline), GetPageContentBytes(reader));
    }

    // ── /Info /Title decrypts to the exact expected text ─────────────────────────────────────────

    [Theory]
    [MemberData(nameof(StandardMatrixFixtures))]
    public void Fixture_infoTitle_decryptsToExactExpectedText(string name)
    {
        using var reader = PdfReader.Open(Load(name), "u");

        Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
    }

    /// <summary>
    /// The RC4 double-decryption canary. ISO 32000-2 §7.5.7: "strings occurring anywhere in an
    /// object stream shall not be separately encrypted" — the container is decrypted once, as a
    /// whole, and its compressed members must NOT be decrypted again individually. This fixture's
    /// /Info dictionary (with /Title) is itself a compressed object inside the object stream, so it
    /// is the one fixture that can catch a regression here.
    ///
    /// It has to be RC4, not AES: an AES double-decrypt throws (wrong padding/IV), which a test
    /// would also pass for the WRONG reason — "some exception happened" proves nothing about which
    /// layer is at fault. RC4 double-decryption is silent: XORing an already-plaintext string
    /// against a second, wrong keystream just produces different-looking garbage bytes, no
    /// exception at all. Only a value-level assertion on the actual decrypted text — not "it didn't
    /// throw" — can catch that, which is exactly what this asserts.
    /// </summary>
    [Fact]
    public void ObjectStreamFixture_infoTitle_decryptsToExactExpectedText_notDoubleDecrypted()
    {
        using var reader = PdfReader.Open(Load("enc-rc4-objstm.pdf"), "u");

        Assert.Equal("GoldenStandardFont", GetInfoTitle(reader));
    }

    // ── Owner-vs-user access reporting ────────────────────────────────────────────────────────────

    [Fact]
    public void UserPassword_reports_isOwnerAccess_false()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128.pdf"), "u");

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
    }

    [Fact]
    public void OwnerPassword_reports_isOwnerAccess_true()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128.pdf"), "o");

        Assert.NotNull(reader.Encryption);
        Assert.True(reader.Encryption.IsOwnerAccess);
    }

    [Theory]
    [InlineData("enc-rc4-40.pdf")]
    [InlineData("enc-aes-256-r6.pdf")]
    public void OwnerAndUserPasswords_reportDifferentAccess_onDifferentFixtures(string name)
    {
        using var userReader = PdfReader.Open(Load(name), "u");
        using var ownerReader = PdfReader.Open(Load(name), "o");

        Assert.False(userReader.Encryption!.IsOwnerAccess);
        Assert.True(ownerReader.Encryption!.IsOwnerAccess);
    }

    // ── Encryption metadata summary ───────────────────────────────────────────────────────────────

    [Fact]
    public void Encryption_reportsExpectedShape_forAes256R6Fixture()
    {
        using var reader = PdfReader.Open(Load("enc-aes-256-r6.pdf"), "u");

        Assert.NotNull(reader.Encryption);
        Assert.Equal(5, reader.Encryption.V);
        Assert.Equal(6, reader.Encryption.R);
        Assert.Equal(PdfCipherAlgorithm.Aes256, reader.Encryption.Cipher);
        Assert.Equal(256, reader.Encryption.KeyLengthBits);
        Assert.True(reader.Encryption.EncryptMetadata);
        Assert.Equal(PdfPermissions.All, reader.Encryption.Permissions);
    }

    [Fact]
    public void Encryption_reportsExpectedShape_forRc4_40_fixture()
    {
        using var reader = PdfReader.Open(Load("enc-rc4-40.pdf"), "u");

        Assert.NotNull(reader.Encryption);
        Assert.Equal(1, reader.Encryption.V);
        Assert.Equal(2, reader.Encryption.R);
        Assert.Equal(PdfCipherAlgorithm.Rc4, reader.Encryption.Cipher);
        Assert.Equal(40, reader.Encryption.KeyLengthBits);
    }

    [Fact]
    public void Encryption_encryptMetadataFalse_isReported()
    {
        using var reader = PdfReader.Open(Load("enc-256-cleartextmd.pdf"), "u");

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.EncryptMetadata);
    }

    // ── Empty user password ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void EmptyUserPasswordFixture_opens_withNoPasswordArgument()
    {
        // PdfReader.Open(bytes) with no password at all is the path most real encrypted PDFs need:
        // permissions restricted via the owner password, empty user password left open.
        using var reader = PdfReader.Open(Load("enc-aes-128-emptyuser.pdf"));
        using var baseline = PdfReader.Open(Load("plaintext-baseline.pdf"));

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
        Assert.Equal(GetPageContentBytes(baseline), GetPageContentBytes(reader));
    }

    [Fact]
    public void EmptyUserPasswordFixture_opens_withExplicitEmptyStringPassword()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128-emptyuser.pdf"), string.Empty);

        Assert.NotNull(reader.Encryption);
        Assert.False(reader.Encryption.IsOwnerAccess);
    }

    [Fact]
    public void EmptyUserPasswordFixture_opens_withNullPassword()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128-emptyuser.pdf"), password: null);

        Assert.NotNull(reader.Encryption);
    }

    // ── Wrong password ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void WrongPassword_throwsPdfPasswordException_exactType()
    {
        var bytes = Load("enc-aes-128.pdf");

        // Assert.Throws<T> in xUnit is an exact-type check, not "is-a" — this pins that the thrown
        // type is literally PdfPasswordException, not merely something that derives from Exception.
        Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes, "definitely-wrong"));
    }

    [Fact]
    public void NoPasswordSupplied_onAFixtureThatRequiresOne_throwsPdfPasswordException()
    {
        // enc-aes-128.pdf's user password is "u", not empty — opening with no password at all must
        // fail exactly like a wrong one, not silently succeed or throw something else.
        var bytes = Load("enc-aes-128.pdf");

        Assert.Throws<PdfPasswordException>(() => PdfReader.Open(bytes));
    }

    // ── /Filter /Adobe.PubSec (public-key handler) ────────────────────────────────────────────────

    [Fact]
    public void PublicKeySecurityHandler_throwsUnsupportedPdfFeatureException()
    {
        var bytes = BuildTrailerWithEncryptDict("<< /Filter /Adobe.PubSec /V 1 /R 2 >>");

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes));
    }

    [Fact]
    public void NonStandardNonPubSecFilter_throwsUnsupportedPdfFeatureException()
    {
        var bytes = BuildTrailerWithEncryptDict("<< /Filter /SomeVendorHandler /V 1 /R 2 >>");

        Assert.Throws<UnsupportedPdfFeatureException>(() => PdfReader.Open(bytes));
    }

    // ── The never-decrypt list, asserted positively ───────────────────────────────────────────────

    /// <summary>
    /// The trailer's /ID array is parsed once, by XrefParser, before a PdfDocumentReader (and
    /// therefore any decryptor) exists at all — so it structurally cannot be decrypted. This asserts
    /// it against the RAW FILE BYTES directly (not through any decrypt-aware accessor), so a
    /// regression that somehow ran /ID through string decryption would change this value and fail
    /// the comparison, rather than the test trivially matching whatever the reader happens to report.
    /// </summary>
    [Fact]
    public void TrailerId_firstElement_isNotDecrypted()
    {
        var bytes = Load("enc-aes-128.pdf");
        using var reader = PdfReader.Open(bytes, "u");

        var idArr = Assert.IsType<PdfArray>(reader.Trailer.Get(PdfName.ID));
        var id0 = Assert.IsType<PdfHexString>(idArr[0]);

        // Every fixture in this corpus shares the same first /ID element — plaintext-baseline.pdf's
        // own, carried through unchanged by qpdf (see the fixture README: only the SECOND element is
        // regenerated on each qpdf invocation). Comparing against the raw file bytes independently
        // confirms this, rather than trusting the reader's own parse of the same value.
        var text = Encoding.Latin1.GetString(bytes);
        var idKeyPos = text.IndexOf("/ID [<", StringComparison.Ordinal);
        Assert.True(idKeyPos >= 0, "expected the classic qpdf-formatted /ID array");
        var hexStart = idKeyPos + "/ID [<".Length;
        var expectedHex = text.Substring(hexStart, 32);

        Assert.Equal(expectedHex, Convert.ToHexStringLower(id0.Bytes.Span));
    }

    /// <summary>
    /// Strings inside the /Encrypt dictionary itself (/O, /U, /OE, /UE) must never be decrypted —
    /// they are the key material a decryptor is built FROM, not document content. Compared here
    /// against the digest-pinned raw hex in EncryptedFixtureCorpusTests, independent of the reader.
    /// </summary>
    [Fact]
    public void EncryptDictionary_ownStrings_areNotDecrypted()
    {
        var bytes = Load("enc-aes-128.pdf");
        using var reader = PdfReader.Open(bytes, "u");

        var encryptRef = Assert.IsType<PdfIndirectReference>(reader.Trailer.Get(new PdfName("Encrypt")));
        var encryptDict = Assert.IsType<PdfDictionary>(reader.Resolve(encryptRef));
        var oValue = Assert.IsType<PdfHexString>(encryptDict.Get(new PdfName("O")));

        var text = Encoding.Latin1.GetString(bytes);
        var oPos = text.IndexOf("/O <", StringComparison.Ordinal);
        Assert.True(oPos >= 0);
        var expectedHex = text.Substring(oPos + 4, 64); // 32 bytes = 64 hex digits

        Assert.Equal(expectedHex, Convert.ToHexStringLower(oValue.Bytes.Span));
    }

    // ── Signature /Contents exemption (spec-silent; documented choice) ───────────────────────────

    /// <summary>
    /// ISO 32000-1 and ISO 32000-2 are silent on whether a signature dictionary's /Contents is
    /// exempt from string encryption. PdfDocumentReader.DecryptObjectGraph documents the choice made
    /// here: never decrypt /Contents when the containing dictionary declares /Type /Sig, because a
    /// conformant signer patches those hex digits directly into already-serialized file bytes after
    /// computing the signature over the file's own bytes, so they were never run through the
    /// object-level string-encryption pipeline at write time regardless of document encryption.
    ///
    /// No signed+encrypted fixture exists in this corpus (or is practical to build without a real
    /// signing tool that also supports encryption), so this is a structural test of the exemption
    /// rule itself: a /Type /Sig dictionary's /Contents survives a decrypt pass unchanged, while its
    /// other strings (here /Reason) still decrypt normally. It is NOT an end-to-end proof against a
    /// real PDF signer's output.
    /// </summary>
    [Fact]
    public void SignatureDictionary_contents_isExemptFromStringDecryption_otherStringsAreNot()
    {
        // DecryptObjectGraph is private (an implementation detail of Resolve()'s decrypt walk, not
        // something worth widening the internal surface for just to test); a REAL armed reader
        // supplies the _decryptor/_fileKey state it needs, and reflection invokes it directly with a
        // synthetic /Type /Sig dictionary — this pins the exemption rule itself, independent of any
        // real signing tool's output (see this test's own remarks in the brief this implements).
        //
        // RC4 (enc-rc4-128.pdf), not AES: DecryptObjectGraph runs /Reason through real string
        // decryption using the reader's actual crypt filter, and these bytes were never genuinely
        // encrypted — AES-CBC would reject them (wrong length, or PKCS7 padding that does not
        // validate once "decrypted" under an unrelated key). RC4 is an unpadded stream cipher with
        // no block-size constraint, so any byte length round-trips through Decrypt without error,
        // which is all this test needs: proof the bytes changed, not that they mean anything.
        using var reader = PdfReader.Open(Load("enc-rc4-128.pdf"), "u");
        var method = typeof(PdfDocumentReader).GetMethod(
            "DecryptObjectGraph", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("DecryptObjectGraph not found by reflection.");

        // Arbitrary bytes standing in for a placeholder-patched signature's raw /Contents — the
        // exemption must hold regardless of what the bytes actually are, since decrypting them at
        // all (turning real signature bytes into garbage) is the bug this guards against.
        var contentsBytes = new byte[16];
        Array.Fill(contentsBytes, (byte)0xAB);
        var reasonBytes = Encoding.ASCII.GetBytes("Approved");

        var sigDict = new PdfDictionary()
            .Set(new PdfName("Type"), new PdfName("Sig"))
            .Set(PdfName.Contents, new PdfHexString(contentsBytes))
            .Set(new PdfName("Reason"), new PdfLiteralString(reasonBytes));

        var walked = (PdfObject)method.Invoke(reader, [sigDict, 5, 0])!;
        var walkedDict = Assert.IsType<PdfDictionary>(walked);

        var contentsAfter = Assert.IsType<PdfHexString>(walkedDict.Get(PdfName.Contents));
        Assert.Equal(contentsBytes, contentsAfter.Bytes.ToArray());

        // /Reason is NOT exempt: DecryptObjectGraph transforms it using the reader's real armed
        // decryptor — the meaningful assertion is that the bytes CHANGED, proving the walk did not
        // skip this entry the way it skipped /Contents.
        var reasonAfter = Assert.IsType<PdfLiteralString>(walkedDict.Get(new PdfName("Reason")));
        Assert.NotEqual(reasonBytes, reasonAfter.Bytes.ToArray());
    }

    // ── /Crypt filter with /Identity (end-to-end, not just resolver-level) ──────────────────────

    /// <summary>
    /// CryptFilterResolverTests pins the /Crypt-with-/Identity RESOLUTION logic in isolation; this
    /// pins that PdfDocumentReader actually WIRES it in — GetDecodedStreamData must return a
    /// stream's bytes unchanged (after the ordinary filter chain) when its /Filter names /Crypt with
    /// /DecodeParms /Name /Identity, even on a document whose document-wide /StmF would otherwise
    /// decrypt it.
    /// </summary>
    [Fact]
    public void CryptIdentityFilter_onAStream_bypassesDecryption_endToEnd()
    {
        // enc-aes-128.pdf's page content stream is normally AES-128 encrypted; patch its /Filter to
        // "/Crypt /FlateDecode" with /DecodeParms naming /Identity for the first entry, so this one
        // stream's body is (per the patched declaration) NOT encrypted — but its RAW bytes in the
        // file are UNCHANGED (still real ciphertext from before the patch). If GetDecodedStreamData
        // correctly honours /Crypt /Identity, it will try to FlateDecode raw ciphertext directly,
        // which is not valid zlib/deflate data, and DecodeStream must fail or return garbage — NOT
        // successfully produce the real (would-be-AES-decrypted) content. This proves the override
        // fires rather than merely not throwing.
        var original = Load("enc-aes-128.pdf");

        // enc-aes-128.pdf's /Metadata stream is ALSO /Filter /FlateDecode, so a bare
        // "/Filter /FlateDecode" search would find that one first. "/Length 96" is unique to the
        // page content stream's dictionary (confirmed against the committed bytes directly).
        //
        // The replacement MUST be the same byte length as what it replaces: every object's file
        // position after this point is recorded as an absolute byte offset in the classic xref
        // table below, which this patch does not (and must not need to) touch. /Filter /Crypt alone,
        // with no /DecodeParms at all, already resolves to Identity — CryptFilterResolver.
        // ResolveNamedMethod defaults a missing /Name to Identity, matching /StmF/StrF's own
        // documented default — so there is no need to fit an explicit /DecodeParms /Name /Identity
        // in the same space. The dropped /Length key is not a problem either: PdfObjectParser trusts
        // /Length only when it lands exactly on 'endstream', and falls back to scanning for the
        // marker otherwise (ParseStreamBody), which still finds it correctly since the stream BODY
        // bytes are untouched.
        var contentStreamDictBytes = "/Filter /FlateDecode /Length 96 >>"u8.ToArray();
        var replacementCore = "/Filter /Crypt>>"u8.ToArray();
        var replacement = new byte[contentStreamDictBytes.Length];
        replacementCore.CopyTo(replacement, 0);
        Array.Fill(replacement, (byte)' ', replacementCore.Length, replacement.Length - replacementCore.Length);

        var idx = IndexOf(original, contentStreamDictBytes);
        Assert.True(idx >= 0, "expected to find the page content stream's dictionary");
        var patched = (byte[])original.Clone();
        replacement.CopyTo(patched.AsSpan(idx));

        using var reader = PdfReader.Open(patched, "u");
        var content = GetPageContentBytes(reader);

        using var baseline = PdfReader.Open(Load("plaintext-baseline.pdf"));
        var baselineContent = GetPageContentBytes(baseline);

        // The Identity override means the raw (still-AES-ciphertext) bytes are handed to
        // FlateDecode directly. That is either a decode failure (content is null) or, if it happens
        // to inflate into something, content that does NOT match the real plaintext — either
        // outcome proves decryption was bypassed for this stream. Both are asserted so the test
        // fails loudly if some future FlateDecode change makes the malformed input silently succeed
        // with different-but-still-wrong bytes.
        Assert.True(content is null || !content.AsSpan().SequenceEqual(baselineContent));
    }

    // ── /StmF names a filter absent from /CF: loud failure, end to end ──────────────────────────

    /// <summary>
    /// CryptFilterResolverTests pins ResolveNamedMethod's Unsupported mapping in isolation; this
    /// pins the end-to-end consequence: a document whose /StmF names a /CF entry it does not define
    /// opens successfully (password authentication never touches /StmF or /CF) but throws when a
    /// stream is actually decoded — a loud failure, not silent ciphertext-as-plaintext.
    /// </summary>
    [Fact]
    public void StmFNamingUndefinedCfEntry_opensButThrowsOnDecode()
    {
        var original = Load("enc-aes-128.pdf");
        // "/StmF /StdCF" appears exactly once (the /CF dictionary's own key "StdCF" is a distinct
        // occurrence — "/CF << /StdCF ..." — and is left untouched, so /StrF still resolves and
        // password authentication, which never consults /StmF at all, is unaffected).
        var needle = "/StmF /StdCF"u8.ToArray();
        var idx = IndexOf(original, needle);
        Assert.True(idx >= 0, "expected to find /StmF /StdCF in the unencrypted /Encrypt dictionary bytes");

        var patched = (byte[])original.Clone();
        // Same length: "/StmF /Ghost" — Ghost (5 bytes) replaces StdCF (5 bytes), so no other file
        // offset shifts and the rest of the (still-valid) encrypted content is untouched.
        "/StmF /Ghost"u8.CopyTo(patched.AsSpan(idx));

        using var reader = PdfReader.Open(patched, "u");

        Assert.Throws<InvalidDataException>(() =>
        {
            var stream = GetFirstContentStream(reader);
            reader.GetDecodedStreamData(stream);
        });
    }

    // ── Nested string: Algorithm 1 step (a) containing-object identity ──────────────────────────

    /// <summary>
    /// ISO 32000-1 §7.6.2, Algorithm 1 step (a): "If the string is a direct object, use the
    /// identifier of the indirect object containing it." enc-aes-128-nestedstrings.pdf's object 3 is
    /// <c>&lt;&lt; /Outer &lt;&lt; /Strs [ (DirectArrayString) (SecondArrayString) ] &gt;&gt; &gt;&gt;</c> —
    /// two strings nested two levels deep (array, inside a dictionary, inside the indirect object's
    /// own dictionary). Both must decrypt using object 3's own identity, not the array's "position",
    /// not a hardcoded generation, and not object 0.
    /// </summary>
    [Fact]
    public void NestedStringsInsideArrayInsideDictionary_decryptUnderContainingObjectIdentity()
    {
        using var reader = PdfReader.Open(Load("enc-aes-128-nestedstrings.pdf"), "u");

        var custom = Assert.IsType<PdfDictionary>(reader.Catalog.Get(new PdfName("CustomTestData")) switch
        {
            PdfIndirectReference r => reader.Resolve(r),
            var v => v,
        });
        var outer = Assert.IsType<PdfDictionary>(custom.Get(new PdfName("Outer")));
        var strs = Assert.IsType<PdfArray>(outer.Get(new PdfName("Strs")));

        Assert.Equal(2, strs.Count);
        // qpdf may normalise a literal string to a hex string (or leave it alone) when recompressing
        // — either is a legal representation of the same bytes, so both must be accepted here; see
        // GetInfoTitle's own comment for the same qpdf behaviour on /Title.
        Assert.Equal("DirectArrayString", DecodeAsciiString(strs[0]));
        Assert.Equal("SecondArrayString", DecodeAsciiString(strs[1]));
    }

    private static string DecodeAsciiString(PdfObject obj) => obj switch
    {
        PdfLiteralString l => Encoding.ASCII.GetString(l.Bytes.Span),
        PdfHexString h => Encoding.ASCII.GetString(h.Bytes.Span),
        _ => throw new InvalidOperationException($"Expected a string object, got {obj.GetType().Name}."),
    };

    // ── Unencrypted regression ────────────────────────────────────────────────────────────────────

    [Fact]
    public void UnencryptedDocument_encryptionIsNull()
    {
        using var reader = PdfReader.Open(Load("plaintext-baseline.pdf"));

        Assert.Null(reader.Encryption);
    }

    [Fact]
    public void UnencryptedDocument_opensIdentically_withOrWithoutPasswordOverload()
    {
        var bytes = Load("plaintext-baseline.pdf");
        using var a = PdfReader.Open(bytes);
        using var b = PdfReader.Open(bytes, password: "irrelevant-for-an-unencrypted-file");

        Assert.Equal(GetPageContentBytes(a), GetPageContentBytes(b));
        Assert.Null(b.Encryption);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static ParsedStream GetFirstContentStream(PdfDocumentReader reader)
    {
        var pagesRef = reader.Catalog.Get(PdfName.Pages);
        var pages = Assert.IsType<PdfDictionary>(reader.ResolveValue(pagesRef!));
        var kids = Assert.IsType<PdfArray>(reader.ResolveValue(pages.Get(PdfName.Kids)!));
        var page = Assert.IsType<PdfDictionary>(reader.ResolveValue(kids[0]));
        var contentsRef = page.Get(PdfName.Contents)!;
        var stream = contentsRef is PdfIndirectReference r
            ? reader.ResolveStream(r)
            : null;
        return stream ?? throw new InvalidOperationException("Expected the page's /Contents to be an indirect stream reference.");
    }

    private static byte[] GetPageContentBytes(PdfDocumentReader reader)
    {
        var stream = GetFirstContentStream(reader);
        return reader.GetDecodedStreamData(stream)
            ?? throw new InvalidOperationException("Content stream did not fully decode.");
    }

    private static string GetInfoTitle(PdfDocumentReader reader)
    {
        var infoRef = Assert.IsType<PdfIndirectReference>(reader.Trailer.Get(PdfName.Info));
        var info = Assert.IsType<PdfDictionary>(reader.Resolve(infoRef));
        // qpdf writes /Title as a literal string for some fixtures and a hex string for others
        // (its own normalisation choice, not something this corpus controls) — both are legal PDF
        // string representations of the same bytes, so both must decrypt to the same text.
        var titleBytes = info.Get(new PdfName("Title")) switch
        {
            PdfLiteralString l => l.Bytes.Span,
            PdfHexString h => h.Bytes.Span,
            var other => throw new InvalidOperationException($"/Title was a {other?.GetType().Name}, not a string."),
        };
        return DecodeUtf16BeWithBom(titleBytes);
    }

    private static string DecodeUtf16BeWithBom(ReadOnlySpan<byte> bytes)
    {
        Assert.True(bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF, "expected a UTF-16BE BOM");
        var chars = new char[(bytes.Length - 2) / 2];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = (char)((bytes[2 + i * 2] << 8) | bytes[2 + i * 2 + 1]);
        return new string(chars);
    }

    private static byte[] BuildTrailerWithEncryptDict(string encryptDict)
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        Write("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");

        var xref = (int)ms.Position;
        Write("xref\n0 3\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{o1:D10} 00000 n \n");
        Write($"{o2:D10} 00000 n \n");
        Write($"trailer\n<< /Size 3 /Root 1 0 R /Encrypt {encryptDict} >>\n");
        Write($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

}
