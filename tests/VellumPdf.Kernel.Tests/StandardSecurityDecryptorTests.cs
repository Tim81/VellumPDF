// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using VellumPdf.Encryption;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Known-answer tests for <see cref="StandardSecurityDecryptor"/> against the committed encrypted
/// corpus (#99, <c>tests/VellumPdf.Reader.Tests/Fixtures/Encrypted</c>) plus synthetic vectors for
/// shapes the corpus cannot cover (an empty user password — every committed fixture uses "u"/"o").
///
/// The corpus lives in <c>VellumPdf.Reader.Tests</c>, not here, and is read from disk by relative
/// path rather than duplicated: a second copy of a digest-pinned corpus is a defect waiting to
/// happen, since nothing would keep the two copies byte-identical after the first hand-edit of
/// either one. <see cref="FindRepoRoot"/> mirrors the same lookup
/// <c>ZxingDecodeOracleTests.FindRepoRoot</c> uses for <c>eng/barcode-decode.py</c>.
///
/// Every assertion here is pinned to exact bytes: which fileKey a password derives, whether it
/// matches the stored validation hash, and whether a decrypted stream reproduces the byte-for-byte
/// plaintext another tool (qpdf) encrypted. A test that only checked "did something come out" would
/// pass on garbage. See CLAUDE.md on oracles that pass while dodging the feature.
/// </summary>
public sealed class StandardSecurityDecryptorTests
{
    private const string UserPassword = "u";
    private const string OwnerPassword = "o";
    private const string WrongPassword = "not-the-password";

    // Every fixture's content stream (object 6) is unaffected by /EncryptMetadata and identical in
    // object number and plaintext across all eight fixtures and the baseline (verified against the
    // baseline dump: same object graph, only /Encrypt and the ciphertext differ) — see
    // Decrypted_contentStream_matches_plaintextBaseline. Object 3 (Metadata) is not used for the
    // baseline comparison: the baseline's copy is stored uncompressed while the fixtures'
    // /EncryptMetadata-true copies are Flate-compressed, so object 3's raw bytes legitimately
    // differ between the two even after correct decryption.
    private const int ContentStreamObjectNumber = 6;
    private const int Generation = 0;

    public static TheoryData<string> FixtureNames =>
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

    // ── Assertion 1: user and owner password, both directions ───────────────

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void UserPassword_derivesFileKey_wrongPassword_failsCleanly(string fixtureName)
    {
        var decryptor = BuildDecryptor(fixtureName, out var info);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(UserPassword, out var fileKey));
        Assert.NotNull(fileKey);
        Assert.Equal(info.KeyLengthBytes, fileKey.Length);

        Assert.False(decryptor.TryComputeFileKeyFromUserPassword(WrongPassword, out var none));
        Assert.Null(none);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void OwnerPassword_derivesFileKey_wrongPassword_failsCleanly(string fixtureName)
    {
        var decryptor = BuildDecryptor(fixtureName, out _);

        Assert.True(decryptor.TryComputeFileKeyFromOwnerPassword(OwnerPassword, out var fileKey));
        Assert.NotNull(fileKey);

        Assert.False(decryptor.TryComputeFileKeyFromOwnerPassword(WrongPassword, out var none));
        Assert.Null(none);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void OwnerPassword_andUserPassword_deriveTheSameFileKey(string fixtureName)
    {
        // Both are supposed to reach the same file encryption key by construction — Algorithm 7
        // exists precisely to recover the user password's key from the owner password.
        var decryptor = BuildDecryptor(fixtureName, out _);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(UserPassword, out var userKey));
        Assert.True(decryptor.TryComputeFileKeyFromOwnerPassword(OwnerPassword, out var ownerKey));

        Assert.Equal(userKey, ownerKey);
    }

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void TryComputeFileKey_triesUserPassword_thenFallsBackToOwnerPassword(string fixtureName)
    {
        // The owner password ("o") is not a valid user password for any fixture, so this only
        // succeeds by falling through to TryComputeFileKeyFromOwnerPassword — the combinator
        // PdfReader.Open will lean on for an empty-user-password file in the next PR.
        var decryptor = BuildDecryptor(fixtureName, out _);

        Assert.False(decryptor.TryComputeFileKeyFromUserPassword(OwnerPassword, out _));
        Assert.True(decryptor.TryComputeFileKey(
            StandardSecurityHandler.PasswordBytes(OwnerPassword), out var combined));
        Assert.True(decryptor.TryComputeFileKeyFromOwnerPassword(OwnerPassword, out var direct));
        Assert.Equal(direct, combined);
    }

    // ── Assertion 2: decrypt a real stream, compare to the plaintext baseline ──

    [Theory]
    [MemberData(nameof(FixtureNames))]
    public void Decrypted_contentStream_matches_plaintextBaseline(string fixtureName)
    {
        var fixtureBytes = LoadFixture(fixtureName);
        var decryptor = BuildDecryptor(fixtureBytes, out _);
        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(UserPassword, out var fileKey));

        var cipherText = ExtractStreamRawBytes(fixtureBytes, ContentStreamObjectNumber);
        var plainText = decryptor.DecryptStream(fileKey, ContentStreamObjectNumber, Generation, cipherText);

        var baselineBytes = LoadBaseline();
        var expected = ExtractStreamRawBytes(baselineBytes, ContentStreamObjectNumber);

        Assert.Equal(expected, plainText);
    }

    // ── Assertion 2b: DecryptStream and DecryptString each use their own filter ──
    //
    // Every fixture in the corpus has /StmF and /StrF naming the same /CFM, and every test above
    // passes the same fileKey/data through both methods, so nothing so far distinguishes
    // DecryptStream from DecryptString: swapping the two constructor assignments (StreamFilter
    // and StringFilter) survives, and gutting DecryptString to `return data.ToArray()` survives
    // too. RC4's own correctness is pinned elsewhere (against the RFC 8, 40, 256 and 309-byte
    // vectors, per CHANGELOG #97); these two tests exist only to pin the dispatch — DecryptStream
    // reaches StreamFilter and DecryptString reaches StringFilter, not the other way round — using
    // deliberately mismatched filters in both directions so a swap cannot pass by accident.

    [Fact]
    public void DecryptStream_usesStreamFilter_decryptString_usesStringFilter_identityThenRc4()
    {
        byte[] fileKey = [0x01, 0x02, 0x03, 0x04, 0x05];
        var decryptor = new StandardSecurityDecryptor(
            v: 2, r: 3, keyLengthBytes: 5, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Identity, stringFilter: CryptFilterMethod.Rc4);

        // Identity: the "ciphertext" IS the plaintext, unchanged. If StreamFilter/StringFilter
        // were swapped, this would be RC4-"decrypted" into garbage instead.
        byte[] streamPlaintext = "STREAM BYTES, NEVER RC4-TRANSFORMED"u8.ToArray();
        var streamOut = decryptor.DecryptStream(fileKey, objectNumber: 7, generation: 0, streamPlaintext);
        Assert.Equal(streamPlaintext, streamOut);

        // RC4: ciphertext built from the exact per-object key DecryptString itself derives, so
        // this pins DecryptString's dispatch and key selection, not RC4 (that's covered
        // elsewhere) — RC4 is its own inverse, so encrypting and decrypting are the same call.
        byte[] stringPlaintext = "string bytes, deliberately different from the stream ones"u8.ToArray();
        var objectKey = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 7, generation: 0, useAesSalt: false);
        var stringCiphertext = Rc4.Transform(objectKey, stringPlaintext);
        Assert.NotEqual(stringPlaintext, stringCiphertext); // otherwise a swap couldn't be caught below

        var stringOut = decryptor.DecryptString(fileKey, objectNumber: 7, generation: 0, stringCiphertext);
        Assert.Equal(stringPlaintext, stringOut);
    }

    [Fact]
    public void DecryptStream_usesStreamFilter_decryptString_usesStringFilter_rc4ThenIdentity()
    {
        // The reverse pairing of the test above: StreamFilter is RC4 and StringFilter is Identity
        // this time, so a swap in either direction still gets caught by one of the two tests.
        byte[] fileKey = [0x0A, 0x0B, 0x0C, 0x0D, 0x0E];
        var decryptor = new StandardSecurityDecryptor(
            v: 2, r: 3, keyLengthBytes: 5, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Identity);

        byte[] streamPlaintext = "stream bytes, this time under rc4"u8.ToArray();
        var objectKey = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 9, generation: 0, useAesSalt: false);
        var streamCiphertext = Rc4.Transform(objectKey, streamPlaintext);
        Assert.NotEqual(streamPlaintext, streamCiphertext);

        var streamOut = decryptor.DecryptStream(fileKey, objectNumber: 9, generation: 0, streamCiphertext);
        Assert.Equal(streamPlaintext, streamOut);

        byte[] stringPlaintext = "STRING BYTES, THIS TIME UNDER IDENTITY"u8.ToArray();
        var stringOut = decryptor.DecryptString(fileKey, objectNumber: 9, generation: 0, stringPlaintext);
        Assert.Equal(stringPlaintext, stringOut);
    }

    [Fact]
    public void DecryptStream_identityFilter_returnsInputUnchanged_forNonEmptyData()
    {
        // `case Identity: return data.ToArray()` could regress to always returning `[]` and every
        // test that only checks a stream/string round trip through a non-Identity filter would
        // stay green, because none of them exercise Identity with data present.
        var decryptor = new StandardSecurityDecryptor(
            v: 1, r: 2, keyLengthBytes: 5, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Identity, stringFilter: CryptFilterMethod.Identity);

        byte[] data = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x7F];
        var streamOut = decryptor.DecryptStream(new byte[5], objectNumber: 1, generation: 0, data);
        var stringOut = decryptor.DecryptString(new byte[5], objectNumber: 1, generation: 0, data);

        Assert.Equal(data, streamOut);
        Assert.Equal(data, stringOut);
    }

    // ── Assertion 3: /EncryptMetadata false shifts the derived key (Algorithm 2 step (f)) ──

    [Fact]
    public void EncryptMetadataFalse_changesTheDerivedFileKey_atR4()
    {
        // The R4 pair is the one that exercises step (f): at R5/R6 the file key is random and
        // unwrapped from /UE, so /EncryptMetadata never enters key derivation there (see
        // Fixtures/Encrypted/README.md). /O is shared because Algorithm 3 never sees the file
        // key or /EncryptMetadata; /U differs because Algorithm 2's hash input does.
        var plainInfo = LoadEncryptInfo("enc-aes-128.pdf");
        var cleartextInfo = LoadEncryptInfo("enc-aes-128-cleartextmd.pdf");

        Assert.Equal(plainInfo.O, cleartextInfo.O);
        Assert.NotEqual(plainInfo.U, cleartextInfo.U);

        var plain = BuildDecryptor(plainInfo);
        var cleartext = BuildDecryptor(cleartextInfo);
        Assert.True(plain.TryComputeFileKeyFromUserPassword(UserPassword, out var plainKey));
        Assert.True(cleartext.TryComputeFileKeyFromUserPassword(UserPassword, out var cleartextKey));

        Assert.NotEqual(plainKey, cleartextKey);
    }

    [Fact]
    public void EncryptMetadataFalse_writtenPerms_verifiesFalse_throughTheWriteAndReadSides()
    {
        // The first place the write side (StandardSecurityHandler.ComputePerms) and the read
        // side (RecoverAuthenticatedPermissions) meet on /Perms with /EncryptMetadata false. Every other R6
        // /Perms test — corpus-driven and EncryptionTests's own white-box round trip alike — only
        // ever runs with EncryptMetadata true, so ComputePerms hardcoding block[8] = 'T' instead
        // of branching on the setting would survive both suites.
        var handler = new StandardSecurityHandler(new PdfEncryptionSettings
        {
            UserPassword = "metaoff",
            Permissions = PdfPermissions.Print,
            EncryptMetadata = false,
        });

        var userPwBytes = StandardSecurityHandler.PasswordBytes("metaoff");
        var intermediateKey = StandardSecurityHandler.Hash2B(userPwBytes, handler.U.AsSpan(40, 8), []);
        var fileKey = AesCbcDecryptNoPaddingForTest(intermediateKey, new byte[16], handler.UE);

        var correctFlag = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: handler.O, u: handler.U, oe: handler.OE, ue: handler.UE,
            p: handler.PValue, id0: [0x00], encryptMetadata: false,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);
        Assert.Equal(handler.PValue, correctFlag.RecoverAuthenticatedPermissions(fileKey, handler.Perms));

        // Byte 8 of the block is the /EncryptMetadata flag, and the read side deliberately does not
        // compare it (see RecoverAuthenticatedPermissions): a producer that writes it inconsistently
        // has a bookkeeping bug, not tampered permissions. The WRITE side must still get it right,
        // so this asserts the byte directly rather than through a verdict that no longer covers it.
        var block = AesEcbDecryptForTest(fileKey, handler.Perms);
        Assert.Equal((byte)'F', block[8]);
    }

    // /Perms is a single AES-256-ECB block with no padding. The test decrypts it itself rather than
    // going through the reader, because the byte it is about (8, the /EncryptMetadata flag) is one
    // the read side deliberately does not look at.
    /// <summary>
    /// <c>/R</c> outside 2..6 is a revision this handler has no algorithm for. Every other malformed
    /// value in the constructor is pinned; this row was not, so widening the range would have gone
    /// unnoticed — and an out-of-range revision reaching the algorithm split decides between the
    /// R&lt;=4 and R&gt;=5 paths on a value that means neither.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(0)]
    public void Constructor_rejectsARevisionOutsideTwoThroughSix(int r)
    {
        var ex = Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 4, r: r, keyLengthBytes: 16, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes128, stringFilter: CryptFilterMethod.Aes128));

        Assert.Contains("/R", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Algorithm 1 keys on the object number and generation, and neither can be negative in a
    /// well-formed file. The guards exist because a cross-reference table that could not be parsed
    /// hands back <c>XrefEntry.UnknownGeneration</c>, which is -1: without them that reaches the key
    /// derivation and shifts every byte of the object key.
    /// </summary>
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(3, -1)]
    public void ComputeObjectKey_rejectsNegativeIdentity(int objectNumber, int generation)
    {
        Assert.Throws<InvalidDataException>(
            () => StandardSecurityDecryptor.ComputeObjectKey(new byte[16], objectNumber, generation, useAesSalt: false));
    }

    private static byte[] AesEcbDecryptForTest(byte[] key, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] AesCbcDecryptNoPaddingForTest(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aes.Key, iv);
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    // ── Assertion 4: per-object key, exact bytes, arbitrary but fixed fileKey/objNum/gen ──
    //
    // Independently computed (not by running StandardSecurityDecryptor and recording the output —
    // a KAT generated from the code it verifies proves nothing): MD5(fileKey || objNum low 3 bytes
    // LE || gen low 2 bytes LE [|| "sAlT"]), truncated to min(fileKey.Length + 5, 16).

    [Fact]
    public void ComputeObjectKey_fiveByteFileKey_noSalt()
    {
        byte[] fileKey = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] expected = [0xC1, 0x6E, 0xCA, 0x53, 0x3C, 0xB5, 0x7D, 0xE2, 0x37, 0xEF];

        var actual = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 3, generation: 0, useAesSalt: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeObjectKey_sixteenByteFileKey_largeObjectAndGeneration_noSalt()
    {
        // objectNumber and generation both near their practical ceiling (a 3- and 2-byte field
        // respectively), so a byte-order or truncation bug in either would show up here rather
        // than being masked by small values.
        byte[] fileKey = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F];
        byte[] expected = [0xBD, 0x2F, 0x5D, 0x0E, 0x6A, 0x04, 0xC0, 0xCC, 0x58, 0xF8, 0xF9, 0x1E, 0x85, 0xE5, 0x18, 0x2C];

        var actual = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 65535, generation: 7, useAesSalt: false);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeObjectKey_sixteenByteFileKey_largeObjectAndGeneration_withAesSalt()
    {
        byte[] fileKey = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F];
        byte[] expected = [0x59, 0xE7, 0x70, 0xAF, 0xB2, 0xDF, 0x42, 0x0B, 0x3F, 0x89, 0xDA, 0xC3, 0x80, 0x02, 0xD0, 0xEF];

        var actual = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 65535, generation: 7, useAesSalt: true);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeObjectKey_withAesSalt_differsFromWithoutSalt_forTheSameObjectAndGeneration()
    {
        byte[] fileKey = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F];
        byte[] expectedWithSalt = [0xCD, 0x9D, 0x4D, 0xF3, 0xF6, 0x37, 0xD7, 0x05, 0x22, 0x00, 0x54, 0x8F, 0x2D, 0xB9, 0x45, 0x69];

        var withSalt = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 1, generation: 0, useAesSalt: true);
        var withoutSalt = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 1, generation: 0, useAesSalt: false);

        Assert.Equal(expectedWithSalt, withSalt);
        Assert.NotEqual(withoutSalt, withSalt);
    }

    [Fact]
    public void ComputeObjectKey_sevenByteFileKey_withAesSalt_truncatesToTwelveBytes()
    {
        // min(fileKey.Length + 5, 16) and min(fileKey.Length * 2, 16) agree at every key length
        // this file's other vectors use (5 -> 10, 16 -> 16), so a regression to the latter formula
        // would pass both. 7 is the smallest length where they diverge: +5 gives 12, *2 gives 14.
        // Independently computed (Python hashlib.md5, not by running the code under test).
        byte[] fileKey = [0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F, 0x10];
        byte[] expected = [0x0D, 0xD2, 0x48, 0xED, 0x3C, 0x1E, 0xC6, 0x5E, 0xB6, 0xB0, 0x06, 0x15];

        var actual = StandardSecurityDecryptor.ComputeObjectKey(fileKey, objectNumber: 12345, generation: 3, useAesSalt: true);

        Assert.Equal(12, actual.Length);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ComputeObjectKey_emptyFileKey_throwsArgumentException()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => StandardSecurityDecryptor.ComputeObjectKey([], objectNumber: 1, generation: 0, useAesSalt: false));

        Assert.Equal("fileKey", ex.ParamName);
    }

    // ── AES key-length guard (a hostile /CFM + /Length pairing) ──────────────
    //
    // /V 4 /Length 40 /CF <</StdCF <</CFM /AESV2>>>> is a legal-looking dictionary that a crafted
    // file can present. It yields a 5-byte file key, and ComputeObjectKey's own
    // min(fileKey.Length + 5, 16) then produces a 10-byte per-object key — not a legal AES key
    // size (16/24/32). AES-CBC decryption has to turn that into the same InvalidDataException a
    // malformed ciphertext would, not let CryptographicException escape from deep inside the BCL.

    [Fact]
    public void DecryptStream_aesFilter_withAKeyLengthThatCannotFormALegalAesKey_throwsInvalidDataException()
    {
        var decryptor = new StandardSecurityDecryptor(
            v: 4, r: 4, keyLengthBytes: 5, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes128, stringFilter: CryptFilterMethod.Aes128);

        byte[] fileKey = [0x01, 0x02, 0x03, 0x04, 0x05]; // 5 bytes -> a 10-byte object key
        byte[] data = new byte[32]; // 16-byte IV + one whole ciphertext block; content is irrelevant

        var ex = Assert.Throws<InvalidDataException>(
            () => decryptor.DecryptStream(fileKey, objectNumber: 1, generation: 0, data));
        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    // ── AES-CBC length guard: at least 16 bytes (the IV), then whole 16-byte blocks ──

    [Theory]
    [InlineData(15)] // shorter than the IV alone
    [InlineData(31)] // has the IV, but leaves 15 bytes of ciphertext — not a whole block
    public void DecryptStream_aesFilter_withDataThatIsNotIvPlusWholeBlocks_throwsInvalidDataException(int length)
    {
        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: new byte[48], u: new byte[48], oe: new byte[32], ue: new byte[32],
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        Assert.Throws<InvalidDataException>(
            () => decryptor.DecryptStream(new byte[32], objectNumber: 1, generation: 0, new byte[length]));
    }

    // ── AES-CBC padding-failure path ──────────────────────────────────────────
    //
    // The only existing test aimed at DecryptAesCbcWithIvPrefix's catch block
    // (DecryptStream_aesFilter_withAKeyLengthThatCannotFormALegalAesKey_throwsInvalidDataException,
    // above) trips aes.Key's setter on a too-short key — CryptographicException from key length,
    // not from TransformFinalBlock rejecting bad padding. Moving TransformFinalBlock outside the
    // try would still pass that test. A legal-size (32-byte) key that is simply the wrong one
    // reaches TransformFinalBlock, and 16 bytes of essentially arbitrary "plaintext" has only a
    // 1-in-256-ish chance of ending in valid PKCS#7 padding, so this reaches the padding failure
    // instead.

    [Fact]
    public void DecryptStream_aesFilter_wrongKey_throwsInvalidDataException_fromPaddingFailure()
    {
        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: new byte[48], u: new byte[48], oe: new byte[32], ue: new byte[32],
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        byte[] wrongKey =
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x1B, 0x1C, 0x1D, 0x1E, 0x1F,
        ];
        byte[] ivAndCiphertext =
        [
            0xA5, 0x5A, 0x3C, 0xC3, 0x91, 0x19, 0x77, 0x7A, 0xE8, 0x2D, 0x4B, 0x66, 0xFA, 0xB2, 0x0D, 0x50,
            0x6F, 0x94, 0x1D, 0xC8, 0x2E, 0x71, 0x08, 0xB9, 0x3A, 0xE4, 0x5C, 0x27, 0x99, 0x60, 0x8D, 0xF1,
        ];

        var ex = Assert.Throws<InvalidDataException>(
            () => decryptor.DecryptStream(wrongKey, objectNumber: 1, generation: 0, ivAndCiphertext));
        Assert.IsType<CryptographicException>(ex.InnerException);
    }

    // ── Assertion 5 (empty user password) + Algorithm 2/4/5/2.A synthetic vectors ──
    //
    // None of the eight committed fixtures uses an empty user password (the corpus README lists
    // that as a known gap — all eight use "u"/"o"), so this is not measured against the corpus.
    // Independently computed with arbitrary but fixed O/P/ID0, the same way
    // Rc4Md5PrimitiveTests.Md5_incremental_matches_BCL_over_algorithm2_shaped_pieces pins Algorithm
    // 2's input shape: not a claim that any real-world encrypted file looks like this, only that
    // Algorithm 2/4/5 (R2–R4) and Algorithm 2.A (R5/R6) accept an empty password and derive exactly
    // the key/hash the specification says they should.

    // R>=3's fifty-round MD5 tail (Algorithm 2's user-password path, and Algorithm 7's owner-key
    // derivation via Algorithm 3 steps (a)-(d)) truncates each round's input to _keyLengthBytes.
    // ISO 32000-1 Algorithm 3 step (c) does not say to truncate — only Algorithm 2 step (h) does,
    // in near-identical wording that happens to add "the first n bytes". Real files settle it:
    // built both ways as actual R3 /Length 40 files, the literal (non-truncating) Algorithm 3
    // reading makes qpdf 12.3.2 report "invalid password" and poppler 25.07 "Incorrect password"
    // for a correct owner password, while truncating — what this code does — makes both accept
    // it. So this pins the truncating behaviour as correct for interoperability, not because the
    // ISO text says so for the owner branch; see the comment above RecoverUserPasswordFromOwner.
    //
    // Every other R3/R4 vector in this file — corpus and synthetic alike — uses a 16-byte key, so
    // a regression that hardcoded that truncation to 16 would pass every one of them: the only
    // 5-byte fixture is enc-rc4-40.pdf, which is R=2 and never enters the round loop at all. /R 3
    // /Length 40 is a real Adobe Reader-compatible combination, so this covers it. This test is the
    // sole guard on BOTH fifty-round truncations at a key length other than 16 — weakening or
    // deleting it removes the only regression coverage for a bug that would silently break every
    // 40-120 bit R3 file. Independently computed (Python, hashlib.md5 + a from-scratch RC4
    // keystream, not by running the code under test), exercising both loops: the user path through
    // Algorithm 2 alone, and the owner path through Algorithm 7's key derivation followed by the
    // same Algorithm 2 call on the recovered password.
    [Fact]
    public void KeyLengthFiveBytes_R3_userAndOwnerPasswords_deriveTheSameFileKey()
    {
        byte[] o =
        [
            0x99, 0x24, 0xE2, 0xC7, 0xB4, 0x9E, 0xEF, 0xEC, 0x68, 0x3C, 0x66, 0x51, 0x00, 0xFE, 0x6B, 0xDD,
            0xFE, 0x1F, 0x70, 0x7C, 0xEA, 0x3E, 0xE6, 0x50, 0x87, 0x78, 0xC8, 0xF1, 0x02, 0x73, 0x8A, 0x01,
        ];
        byte[] id0 =
        [
            0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x6F, 0x70, 0x71, 0x72, 0x73,
        ];
        byte[] u32 =
        [
            0xDF, 0xD8, 0xC1, 0x29, 0x90, 0x8E, 0x5E, 0xD8, 0xC4, 0x19, 0x8C, 0x64, 0x29, 0x4A, 0xA0, 0x83,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        ];
        byte[] expectedFileKey = [0x0E, 0x99, 0x5B, 0xAB, 0x23];

        var decryptor = new StandardSecurityDecryptor(
            v: 2, r: 3, keyLengthBytes: 5, o: o, u: u32, oe: null, ue: null,
            p: -3904, id0: id0, encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword("usr", out var userFileKey));
        Assert.Equal(expectedFileKey, userFileKey);

        Assert.True(decryptor.TryComputeFileKeyFromOwnerPassword("own", out var ownerFileKey));
        Assert.Equal(expectedFileKey, ownerFileKey);
    }

    [Fact]
    public void EmptyUserPassword_R3_derivesExpectedFileKey_andMatchesU()
    {
        byte[] o =
        [
            0x03, 0x0A, 0x11, 0x18, 0x1F, 0x26, 0x2D, 0x34, 0x3B, 0x42, 0x49, 0x50, 0x57, 0x5E, 0x65, 0x6C,
            0x73, 0x7A, 0x81, 0x88, 0x8F, 0x96, 0x9D, 0xA4, 0xAB, 0xB2, 0xB9, 0xC0, 0xC7, 0xCE, 0xD5, 0xDC,
        ];
        byte[] id0 =
        [
            0x01, 0x0C, 0x17, 0x22, 0x2D, 0x38, 0x43, 0x4E, 0x59, 0x64, 0x6F, 0x7A, 0x85, 0x90, 0x9B, 0xA6,
        ];
        byte[] u =
        [
            0xFC, 0xF6, 0x59, 0x16, 0x4F, 0xEB, 0x28, 0xF3, 0xAD, 0xB9, 0x6E, 0xE1, 0xC8, 0x11, 0x7F, 0x5B,
        ];
        byte[] expectedFileKey =
        [
            0xE8, 0x7C, 0x9E, 0xAC, 0xB1, 0xEA, 0x9B, 0x25, 0x2E, 0x04, 0x60, 0x95, 0x67, 0x82, 0x60, 0x45,
        ];
        var u32 = new byte[32];
        u.CopyTo(u32, 0); // Algorithm 5's first 16 bytes are what's checked; the rest is unused padding here.

        var decryptor = new StandardSecurityDecryptor(
            v: 2, r: 3, keyLengthBytes: 16, o: o, u: u32, oe: null, ue: null,
            p: -44, id0: id0, encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(string.Empty, out var fileKey));
        Assert.Equal(expectedFileKey, fileKey);
    }

    [Fact]
    public void EmptyUserPassword_R4_withEncryptMetadataFalse_derivesExpectedFileKey_andMatchesU()
    {
        byte[] o =
        [
            0x05, 0x12, 0x1F, 0x2C, 0x39, 0x46, 0x53, 0x60, 0x6D, 0x7A, 0x87, 0x94, 0xA1, 0xAE, 0xBB, 0xC8,
            0xD5, 0xE2, 0xEF, 0xFC, 0x09, 0x16, 0x23, 0x30, 0x3D, 0x4A, 0x57, 0x64, 0x71, 0x7E, 0x8B, 0x98,
        ];
        byte[] id0 =
        [
            0x09, 0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x80, 0x91, 0xA2, 0xB3, 0xC4, 0xD5, 0xE6, 0xF7, 0x08,
        ];
        byte[] u =
        [
            0xB6, 0x59, 0xA0, 0x20, 0x99, 0x9A, 0xF8, 0x1A, 0x62, 0x8D, 0x69, 0x76, 0xEF, 0xB3, 0x23, 0x28,
        ];
        byte[] expectedFileKey =
        [
            0x41, 0xC7, 0x92, 0xCE, 0xD0, 0x58, 0x3C, 0xE7, 0x28, 0x76, 0x1D, 0x6F, 0xDA, 0xA1, 0xC7, 0x51,
        ];
        var u32 = new byte[32];
        u.CopyTo(u32, 0);

        var decryptor = new StandardSecurityDecryptor(
            v: 4, r: 4, keyLengthBytes: 16, o: o, u: u32, oe: [], ue: [],
            p: -3904, id0: id0, encryptMetadata: false,
            streamFilter: CryptFilterMethod.Aes128, stringFilter: CryptFilterMethod.Aes128);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(string.Empty, out var fileKey));
        Assert.Equal(expectedFileKey, fileKey);
    }

    [Fact]
    public void EmptyUserPassword_R5_unwrapsExpectedFileKey()
    {
        byte[] fileKey32 =
        [
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        ];
        byte[] u =
        [
            0x08, 0x65, 0xC1, 0xBE, 0x25, 0x5B, 0x33, 0xB6, 0x9C, 0x4C, 0x1B, 0x7D, 0xF3, 0x64, 0x6C, 0xD2,
            0xB7, 0xFE, 0xAB, 0x36, 0xF5, 0x95, 0x04, 0x4A, 0xB1, 0x91, 0x17, 0x6F, 0x16, 0x68, 0xD9, 0xAD,
            0x03, 0x0A, 0x11, 0x18, 0x1F, 0x26, 0x2D, 0x34, 0x0B, 0x18, 0x25, 0x32, 0x3F, 0x4C, 0x59, 0x66,
        ];
        byte[] ue =
        [
            0x01, 0xED, 0xF7, 0xDB, 0xB8, 0x69, 0x24, 0xFA, 0x51, 0x37, 0x44, 0x77, 0x6E, 0x97, 0x4B, 0x5F,
            0xFE, 0x8A, 0x16, 0x7F, 0xA0, 0xF7, 0xA4, 0x77, 0x10, 0x4C, 0x59, 0x1E, 0x31, 0x4D, 0x1F, 0xCF,
        ];
        var o = new byte[48]; // not exercised by this test: only the user-password path runs

        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 5, keyLengthBytes: 32, o: o, u: u, oe: new byte[32], ue: ue,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(string.Empty, out var fileKey));
        Assert.Equal(fileKey32, fileKey);
    }

    [Fact]
    public void EmptyUserPassword_R6_unwrapsExpectedFileKey()
    {
        byte[] fileKey32 =
        [
            0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x29, 0x2A, 0x2B, 0x2C, 0x2D, 0x2E, 0x2F,
            0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
        ];
        byte[] u =
        [
            0xE8, 0x27, 0xE2, 0x6A, 0x32, 0x83, 0x7F, 0x62, 0x49, 0xE4, 0xE2, 0x77, 0x56, 0xB6, 0xC3, 0x1F,
            0xBC, 0x66, 0xEF, 0x80, 0xCB, 0x5C, 0xCB, 0x2B, 0x48, 0xF9, 0x42, 0x08, 0x05, 0xB9, 0xE9, 0x32,
            0x01, 0x04, 0x07, 0x0A, 0x0D, 0x10, 0x13, 0x16, 0x02, 0x07, 0x0C, 0x11, 0x16, 0x1B, 0x20, 0x25,
        ];
        byte[] ue =
        [
            0x52, 0xC3, 0x73, 0x20, 0x6F, 0x88, 0x04, 0xFE, 0x44, 0x73, 0x62, 0xDF, 0xB6, 0xFD, 0x22, 0xF2,
            0xC3, 0xE9, 0x46, 0xFA, 0x6F, 0xEF, 0x31, 0x20, 0x0A, 0x95, 0x69, 0x32, 0xEF, 0x3E, 0xD5, 0xF7,
        ];
        byte[] o =
        [
            0x78, 0x3D, 0x56, 0x7F, 0xF1, 0xF4, 0xD5, 0xAA, 0x6B, 0xCC, 0xBD, 0xB3, 0xEA, 0x44, 0x80, 0x1C,
            0x71, 0x2D, 0xCA, 0x82, 0x24, 0x5D, 0x7A, 0x62, 0x48, 0x0B, 0x38, 0x96, 0x2E, 0x7A, 0x73, 0x50,
            0x04, 0x0D, 0x16, 0x1F, 0x28, 0x31, 0x3A, 0x43, 0x06, 0x19, 0x2C, 0x3F, 0x52, 0x65, 0x78, 0x8B,
        ];
        byte[] oe =
        [
            0x6C, 0x42, 0xB0, 0x65, 0xEB, 0xF6, 0xFE, 0x79, 0xD0, 0x44, 0x9B, 0xE7, 0x0D, 0xD4, 0x3F, 0x8A,
            0xD4, 0x2F, 0x0C, 0x20, 0x6F, 0x05, 0x48, 0xBB, 0x50, 0xF4, 0x9C, 0x8E, 0x6C, 0xAD, 0xB9, 0xA1,
        ];

        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: o, u: u, oe: oe, ue: ue,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(string.Empty, out var userFileKey));
        Assert.Equal(fileKey32, userFileKey);

        // Same synthetic file, a different (non-empty) owner password ("owner-pw"), same /U folded
        // in as udata per Algorithm 2.A's owner branch — exercises the owner path at R6 without
        // relying on the corpus, which only ever pairs "u" with "o".
        Assert.True(decryptor.TryComputeFileKeyFromOwnerPassword("owner-pw", out var ownerFileKey));
        Assert.Equal(fileKey32, ownerFileKey);

        Assert.False(decryptor.TryComputeFileKeyFromOwnerPassword(WrongPassword, out _));
    }

    // ── R6 /Perms consistency check (ISO 32000-2 §7.6.4.4.12, Algorithm 13) ──

    [Theory]
    [InlineData("enc-aes-256-r6.pdf")]
    [InlineData("enc-256-cleartextmd.pdf")]
    public void RecoverAuthenticatedPermissions_R6Fixture_recoversPWithTheRightFileKey_nothingWithAnother(string fixtureName)
    {
        var info = LoadEncryptInfo(fixtureName);
        Assert.NotNull(info.Perms);
        var decryptor = BuildDecryptor(info);
        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(UserPassword, out var fileKey));

        Assert.Equal(info.P, decryptor.RecoverAuthenticatedPermissions(fileKey, info.Perms));

        // A wrong key turns the block into noise, and the "adb" marker is what says so.
        var wrongKey = (byte[])fileKey.Clone();
        wrongKey[0] ^= 0xFF;
        Assert.Null(decryptor.RecoverAuthenticatedPermissions(wrongKey, info.Perms));
    }

    [Fact]
    public void RecoverAuthenticatedPermissions_survivesATamperedPValue()
    {
        // /Perms is decrypted with the file key alone, so a tampered /P doesn't change what
        // /Perms decrypts to — it changes what this decryptor now expects that plaintext to
        // contain. Same fileKey (R6's key derivation never touches /P), same /Perms bytes, a
        // second decryptor built with /P edited after the fact.
        //
        // Flips a single permission bit rather than every bit: XORing with -1 (as this test used
        // to) moves every byte of the 4-byte block[0..4] comparison at once, so narrowing that
        // comparison to a single byte would still pass. A single-bit flip only shows up if the
        // byte it lands in is actually part of the comparison.
        var info = LoadEncryptInfo("enc-aes-256-r6.pdf");
        var decryptor = BuildDecryptor(info);
        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(UserPassword, out var fileKey));
        Assert.Equal(info.P, decryptor.RecoverAuthenticatedPermissions(fileKey, info.Perms!));

        // The dictionary's /P edited after the fact: /Perms is unchanged, decrypts with the file key
        // alone, and still yields what the producer sealed in. Recovering the ORIGINAL value from a
        // document whose /P now says otherwise is exactly what makes the edit detectable.
        var tampered = BuildDecryptor(info with { P = info.P ^ 0x0800 });
        Assert.Equal(info.P, tampered.RecoverAuthenticatedPermissions(fileKey, info.Perms!));
        Assert.NotEqual(info.P ^ 0x0800, tampered.RecoverAuthenticatedPermissions(fileKey, info.Perms!));
    }

    [Fact]
    public void RecoverAuthenticatedPermissions_atRLessThan5_returnsNull_ratherThanThrowing()
    {
        // /Perms doesn't exist below R5, and a 5-16 byte R<=4 file key isn't a legal AES-256 key.
        // Before the R<5 guard, aes.Key's setter threw CryptographicException here instead of
        // this method reporting "not applicable" the way every other invalid input does.
        var decryptor = new StandardSecurityDecryptor(
            v: 1, r: 2, keyLengthBytes: 5, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4);

        Assert.Null(decryptor.RecoverAuthenticatedPermissions(new byte[5], new byte[16]));
    }

    [Fact]
    public void RecoverAuthenticatedPermissions_atRevision4_withASixteenByteKey_returnsNull_ratherThanRunningAesEcb()
    {
        // A 16-byte key is a legal AES-128 key size, so without the R<5 guard this would run ECB
        // decryption on bytes that were never a /Perms block and return a meaningless boolean
        // instead of "not applicable at this revision".
        var decryptor = new StandardSecurityDecryptor(
            v: 4, r: 4, keyLengthBytes: 16, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes128, stringFilter: CryptFilterMethod.Aes128);

        Assert.Null(decryptor.RecoverAuthenticatedPermissions(new byte[16], new byte[16]));
    }

    [Fact]
    public void RecoverAuthenticatedPermissions_atRevision6_withAWrongSizedKey_returnsNull()
    {
        // R>=5 always carries a 32-byte AES-256 file key; anything else at R>=5 is malformed
        // input, not just a wrong password, so this is rejected before the AES key setter runs.
        var info = LoadEncryptInfo("enc-aes-256-r6.pdf");
        var decryptor = BuildDecryptor(info);

        Assert.Null(decryptor.RecoverAuthenticatedPermissions(new byte[16], info.Perms!));
    }

    [Fact]
    public void RecoverAuthenticatedPermissions_r6Fixture_decryptsToTheDocumentedPBytes()
    {
        // Pins the exact decrypted /Perms block for enc-aes-256-r6.pdf, not just the boolean
        // RecoverAuthenticatedPermissions reads: bytes 0-3 are /P little-endian (-4), bytes 4-7 are 0xFF
        // padding, byte 8 is 'T' for /EncryptMetadata, bytes 9-11 are the "adb" marker Algorithm
        // 10 (StandardSecurityHandler.ComputePerms) writes, and bytes 12-15 are qpdf's own random
        // fill — not reproducible, so excluded from the pin.
        var info = LoadEncryptInfo("enc-aes-256-r6.pdf");
        var decryptor = BuildDecryptor(info);
        Assert.True(decryptor.TryComputeFileKeyFromUserPassword(UserPassword, out var fileKey));

        var block = DecryptPermsBlockForTest(fileKey, info.Perms!);

        Assert.Equal([0xFC, 0xFF, 0xFF, 0xFF], block[..4]); // P = -4, little-endian
        Assert.Equal([0xFF, 0xFF, 0xFF, 0xFF], block[4..8]);
        Assert.Equal((byte)'T', block[8]); // EncryptMetadata true
        Assert.Equal("adb"u8.ToArray(), block[9..12]);
    }

    private static byte[] DecryptPermsBlockForTest(byte[] fileKey, byte[] perms)
    {
        using var aes = Aes.Create();
        aes.Key = fileKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var decryptor = aes.CreateDecryptor(aes.Key, null);
        return decryptor.TransformFinalBlock(perms, 0, perms.Length);
    }

    [Fact]
    public void RecoverAuthenticatedPermissions_rejectsACorruptedAdbMarker_withACorrectP()
    {
        // Deleting the "adb" marker check survives every other test here, because the
        // wrong-fileKey tests already fail on an unrelated comparison (the decrypted block is
        // garbage, so /P mismatches too) and the tamper tests above only ever corrupt /P or
        // /EncryptMetadata, never the marker. Build a /Perms block by hand with a correct /P and
        // /EncryptMetadata but a corrupted marker, so the marker check is the only thing that can
        // make this fail.
        byte[] fileKey = new byte[32];
        RandomNumberGenerator.Fill(fileKey);
        const int p = -44;

        var block = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(block, p);
        block[4] = block[5] = block[6] = block[7] = 0xFF;
        block[8] = (byte)'T';
        block[9] = (byte)'x';
        block[10] = (byte)'y';
        block[11] = (byte)'z'; // corrupted: should be "adb"
        RandomNumberGenerator.Fill(block.AsSpan(12));
        var perms = EncryptPermsBlockForTest(fileKey, block);

        var decryptor = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: new byte[48], u: new byte[48], oe: new byte[32], ue: new byte[32],
            p: p, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);

        Assert.Null(decryptor.RecoverAuthenticatedPermissions(fileKey, perms));
    }

    private static byte[] EncryptPermsBlockForTest(byte[] fileKey, byte[] block)
    {
        using var aes = Aes.Create();
        aes.Key = fileKey;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor(aes.Key, null);
        return encryptor.TransformFinalBlock(block, 0, block.Length);
    }

    // ── Constructor validation ────────────────────────────────────────────────

    [Fact]
    public void Constructor_rejectsEmptyId0_atRLessThan5()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 1, r: 2, keyLengthBytes: 5, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    [Fact]
    public void Constructor_allowsEmptyId0_atRevision6()
    {
        // R>=5's Algorithm 2.A never reads /ID[0] — only R<=4's Algorithm 2/5 do — so an empty
        // /ID here is not this constructor's business to reject.
        _ = new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: new byte[48], u: new byte[48], oe: new byte[32], ue: new byte[32],
            p: -4, id0: [], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256);
    }

    [Fact]
    public void Constructor_rejectsWrongLengthO()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 1, r: 2, keyLengthBytes: 5, o: new byte[16], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    [Fact]
    public void Constructor_rejectsV3()
    {
        // /V 3 is Adobe's undocumented algorithm, never specified by ISO 32000-1 or -2. It has no
        // key-derivation or crypt-filter behaviour of its own here — StreamFilter/StringFilter are
        // constructor parameters this class never derives from /V — so letting it through would
        // silently run the caller's chosen filter under a key length /V 3 was never validated
        // against, rather than failing on a value this handler doesn't support at all.
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 3, r: 3, keyLengthBytes: 16, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    [Fact]
    public void Constructor_atRevision5_requiresOe()
    {
        // /O and /U must both already be 48 bytes here so the wrong guard (the length check a few
        // lines above this one) can't be what throws instead of the /OE check being pinned. r: 5,
        // not 6, so the test name matches what the constructor actually sees — the guard is on
        // R>=5 generally, not R6 specifically, and Constructor_allowsEmptyId0_atRevision6 already
        // covers R6.
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 5, r: 5, keyLengthBytes: 32, o: new byte[48], u: new byte[48], oe: null, ue: new byte[32],
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256));
    }

    [Fact]
    public void Constructor_atRevision5_requiresUe()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 5, r: 5, keyLengthBytes: 32, o: new byte[48], u: new byte[48], oe: new byte[32], ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256));
    }

    // ── /V and /R range guards ─────────────────────────────────────────────────
    //
    // `v is < 1 or > 5 or 3` narrowed to `v is 3` still rejects the one value these tests already
    // covered (Constructor_rejectsV3) while letting every out-of-range value through; V=0 and V=6
    // are the two ends of the actual range this guards. Deleting the /R guard entirely leaves
    // every other test green, since R<5's own field-shape checks (below) never fire for an R this
    // far out of range with otherwise-valid field shapes.

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    public void Constructor_rejectsVOutsideSupportedRange(int v)
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: v, r: 3, keyLengthBytes: 16, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void Constructor_rejectsROutsideSupportedRange(int r)
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 2, r: r, keyLengthBytes: 16, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    // ── Field-length guards, one per branch ───────────────────────────────────
    //
    // Constructor_rejectsWrongLengthO (above) already pins /O at R<5; these five fill in the rest
    // of the shape checks, none of which had a test of their own before. The R>=5 /U guard is the
    // sharpest: without it, TryComputeFileKeyFromUserPassword's `_u.AsSpan(32, 8)` on a 32-byte
    // /U throws ArgumentOutOfRangeException from deep inside the password path instead of failing
    // cleanly at construction.

    [Fact]
    public void Constructor_rejectsWrongLengthU_atRLessThan5()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 1, r: 2, keyLengthBytes: 5, o: new byte[32], u: new byte[16], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    [Theory]
    [InlineData(4)] // one below the 5-byte (40-bit) floor
    [InlineData(17)] // one above the 16-byte (128-bit) ceiling
    public void Constructor_rejectsKeyLengthBytesOutOfRange_atRLessThan5(int keyLengthBytes)
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 2, r: 3, keyLengthBytes: keyLengthBytes, o: new byte[32], u: new byte[32], oe: null, ue: null,
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Rc4, stringFilter: CryptFilterMethod.Rc4));
    }

    [Fact]
    public void Constructor_rejectsWrongLengthO_atRevision5Plus()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: new byte[32], u: new byte[48], oe: new byte[32], ue: new byte[32],
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256));
    }

    [Fact]
    public void Constructor_rejectsWrongLengthU_atRevision5Plus()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 32, o: new byte[48], u: new byte[32], oe: new byte[32], ue: new byte[32],
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256));
    }

    [Fact]
    public void Constructor_rejectsKeyLengthBytesNot32_atRevision5Plus()
    {
        Assert.Throws<InvalidDataException>(() => new StandardSecurityDecryptor(
            v: 5, r: 6, keyLengthBytes: 16, o: new byte[48], u: new byte[48], oe: new byte[32], ue: new byte[32],
            p: -4, id0: [0x00], encryptMetadata: true,
            streamFilter: CryptFilterMethod.Aes256, stringFilter: CryptFilterMethod.Aes256));
    }

    // ── Fixture access: read tests/VellumPdf.Reader.Tests/Fixtures/Encrypted from disk ──────

    private static byte[] LoadFixture(string name) => File.ReadAllBytes(FixturePath(name));

    private static byte[] LoadBaseline() => LoadFixture("plaintext-baseline.pdf");

    private static string FixturePath(string name) => Path.Combine(
        FindRepoRoot(), "tests", "VellumPdf.Reader.Tests", "Fixtures", "Encrypted", name);

    /// <summary>Locates the repository root by walking up from the test assembly's directory to
    /// find <c>VellumPdf.slnx</c>, matching <c>ZxingDecodeOracleTests.FindRepoRoot</c>.</summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VellumPdf.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate VellumPdf.slnx by walking up from AppContext.BaseDirectory.");
    }

    // ── Fixture /Encrypt dictionary extraction ────────────────────────────────
    //
    // Not a general PDF parser: anchored to what this specific eight-file corpus is shaped like
    // (every object generation 0, qpdf's own sorted-key dictionary output, a single /Filter
    // /Standard dictionary with an optional nested /CF /StdCF sub-dictionary that always sorts
    // before it) — the same trade-off EncryptedFixtureCorpusTests.HexEntry in
    // VellumPdf.Reader.Tests makes for the same reason. Reading the dictionary properly is
    // PdfReader's job, wired up in the next PR; this exists only to hand StandardSecurityDecryptor
    // real, unmodified bytes out of a real qpdf-produced file.

    private sealed record EncryptInfo(
        string FixtureName,
        int V,
        int R,
        int KeyLengthBytes,
        byte[] O,
        byte[] U,
        byte[]? Oe,
        byte[]? Ue,
        int P,
        byte[]? Perms,
        byte[] Id0,
        bool EncryptMetadata,
        CryptFilterMethod Filter);

    private static StandardSecurityDecryptor BuildDecryptor(string fixtureName, out EncryptInfo info)
    {
        info = LoadEncryptInfo(fixtureName);
        return BuildDecryptor(info);
    }

    private static StandardSecurityDecryptor BuildDecryptor(byte[] fixtureBytes, out EncryptInfo info)
    {
        info = ParseEncryptInfo(fixtureBytes);
        return BuildDecryptor(info);
    }

    private static StandardSecurityDecryptor BuildDecryptor(EncryptInfo info) => new(
        info.V, info.R, info.KeyLengthBytes, info.O, info.U, info.Oe, info.Ue,
        info.P, info.Id0, info.EncryptMetadata, info.Filter, info.Filter);

    private static EncryptInfo LoadEncryptInfo(string fixtureName)
    {
        var info = ParseEncryptInfo(LoadFixture(fixtureName));
        return info with { FixtureName = fixtureName };
    }

    private static EncryptInfo ParseEncryptInfo(byte[] data)
    {
        var encryptObjectNumber = FindTrailerEncryptObjectNumber(data);
        var dictText = FindObjectDictText(data, encryptObjectNumber);

        // /CF's own nested /Length (bytes, for the crypt filter) sorts before /Filter and would
        // collide with the top-level /Length (bits, for the whole handler) under a naive first
        // match — anchor every field lookup except /EncryptMetadata (which sorts *before*
        // /Filter) to start at /Filter /Standard, past the nested dictionary entirely.
        var filterIndex = dictText.IndexOf("/Filter /Standard", StringComparison.Ordinal);
        if (filterIndex < 0)
            throw new InvalidOperationException("no /Filter /Standard entry in the /Encrypt dictionary");
        var tail = dictText[filterIndex..];

        var v = GetRequiredInt(tail, "/V");
        var r = GetRequiredInt(tail, "/R");
        var lengthBits = GetOptionalInt(tail, "/Length", defaultValue: 40);
        var p = GetRequiredInt(tail, "/P");
        var o = GetHex(tail, "/O") ?? throw new InvalidOperationException("missing /O");
        var u = GetHex(tail, "/U") ?? throw new InvalidOperationException("missing /U");
        var oe = NullIfEmpty(GetHex(tail, "/OE"));
        var ue = NullIfEmpty(GetHex(tail, "/UE"));
        var perms = NullIfEmpty(GetHex(tail, "/Perms"));
        var encryptMetadata = !dictText.Contains("/EncryptMetadata false", StringComparison.Ordinal);
        var id0 = FindTrailerId0(data);

        CryptFilterMethod filter;
        if (dictText.Contains("/CFM /AESV3", StringComparison.Ordinal)) filter = CryptFilterMethod.Aes256;
        else if (dictText.Contains("/CFM /AESV2", StringComparison.Ordinal)) filter = CryptFilterMethod.Aes128;
        else if (dictText.Contains("/CFM /V2", StringComparison.Ordinal)) filter = CryptFilterMethod.Rc4;
        else filter = CryptFilterMethod.Rc4; // V<4: no /CF, RC4 is the only method there is.

        var keyLengthBytes = r >= 5 ? 32 : lengthBits / 8;

        return new EncryptInfo(string.Empty, v, r, keyLengthBytes, o, u, oe, ue, p, perms, id0, encryptMetadata, filter);
    }

    private static byte[]? NullIfEmpty(byte[]? value) => value is { Length: 0 } ? null : value;

    private static int FindTrailerEncryptObjectNumber(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        const string marker = "/Encrypt ";
        var i = text.LastIndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            throw new InvalidOperationException("no /Encrypt entry found in the trailer");
        var start = i + marker.Length;
        var end = start;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return int.Parse(text[start..end]);
    }

    private static byte[] FindTrailerId0(byte[] data)
    {
        var text = Encoding.Latin1.GetString(data);
        const string marker = "/ID [<";
        var i = text.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0)
            throw new InvalidOperationException("no /ID array found in the trailer");
        var start = i + marker.Length;
        var end = text.IndexOf('>', start);
        if (end < 0)
            throw new InvalidOperationException("unterminated /ID hex string");
        return Convert.FromHexString(text[start..end]);
    }

    private static int GetRequiredInt(string text, string key)
    {
        var value = GetOptionalInt(text, key, defaultValue: null);
        return value ?? throw new InvalidOperationException($"{key} not found");
    }

    private static int GetOptionalInt(string text, string key, int defaultValue) =>
        GetOptionalInt(text, key, (int?)defaultValue) ?? defaultValue;

    private static int? GetOptionalInt(string text, string key, int? defaultValue)
    {
        var i = text.IndexOf(key + " ", StringComparison.Ordinal);
        if (i < 0) return defaultValue;
        var start = i + key.Length + 1;
        var end = start;
        if (end < text.Length && text[end] == '-') end++;
        while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
        return int.Parse(text[start..end]);
    }

    private static byte[]? GetHex(string text, string key)
    {
        var i = text.IndexOf(key + " <", StringComparison.Ordinal);
        if (i < 0) return null;
        var start = i + key.Length + 2;
        var end = text.IndexOf('>', start);
        if (end < 0)
            throw new InvalidOperationException($"unterminated {key} hex string");
        var hex = text[start..end];
        return hex.Length == 0 ? [] : Convert.FromHexString(hex);
    }

    /// <summary>
    /// Locates <c>"{objectNumber} 0 obj"</c> (every object in this corpus is generation 0 — see
    /// Fixtures/Encrypted/README.md) and returns the byte range of its dictionary text, the
    /// balanced <c>&lt;&lt;</c> ... <c>&gt;&gt;</c> content including the delimiters.
    /// </summary>
    private static string FindObjectDictText(byte[] data, int objectNumber) => FindObject(data, objectNumber).DictText;

    /// <summary>
    /// The exact ciphertext between "stream" and "endstream" for <paramref name="objectNumber"/>,
    /// with the single mandatory EOL after each keyword trimmed (ISO 32000-2 §7.3.8.1).
    /// </summary>
    private static byte[] ExtractStreamRawBytes(byte[] data, int objectNumber) =>
        FindObject(data, objectNumber).StreamRaw
            ?? throw new InvalidOperationException($"object {objectNumber} has no stream");

    private static (string DictText, byte[]? StreamRaw) FindObject(byte[] data, int objectNumber)
    {
        var marker = Encoding.ASCII.GetBytes($"{objectNumber} 0 obj");
        var markerStart = -1;
        for (var searchFrom = 0; ;)
        {
            var idx = IndexOfBytes(data, marker, searchFrom);
            if (idx < 0)
                throw new InvalidOperationException($"'{objectNumber} 0 obj' not found");
            if (idx == 0 || data[idx - 1] == (byte)'\n')
            {
                markerStart = idx;
                break;
            }

            searchFrom = idx + 1;
        }

        var i = markerStart + marker.Length;
        while (data[i] is (byte)'\r' or (byte)'\n') i++;
        if (data[i] != (byte)'<' || data[i + 1] != (byte)'<')
            throw new InvalidOperationException($"object {objectNumber} does not begin with a dictionary");

        var dictStart = i;
        var depth = 0;
        while (true)
        {
            if (data[i] == (byte)'<' && data[i + 1] == (byte)'<') { depth++; i += 2; continue; }
            if (data[i] == (byte)'>' && data[i + 1] == (byte)'>')
            {
                depth--;
                i += 2;
                if (depth == 0) break;
                continue;
            }

            i++;
        }

        var dictText = Encoding.Latin1.GetString(data, dictStart, i - dictStart);

        var j = i;
        while (j < data.Length && data[j] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') j++;

        byte[] streamKeyword = "stream"u8.ToArray();
        if (j + streamKeyword.Length > data.Length ||
            !data.AsSpan(j, streamKeyword.Length).SequenceEqual(streamKeyword))
        {
            return (dictText, null);
        }

        var streamStart = j + streamKeyword.Length;
        if (data[streamStart] == (byte)'\r' && data[streamStart + 1] == (byte)'\n') streamStart += 2;
        else if (data[streamStart] == (byte)'\n') streamStart += 1;

        var endIndex = IndexOfBytes(data, "endstream"u8.ToArray(), streamStart);
        if (endIndex < 0)
            throw new InvalidOperationException($"object {objectNumber} stream has no endstream");

        var rawEnd = endIndex;
        if (rawEnd >= 2 && data[rawEnd - 2] == (byte)'\r' && data[rawEnd - 1] == (byte)'\n') rawEnd -= 2;
        else if (rawEnd >= 1 && data[rawEnd - 1] == (byte)'\n') rawEnd -= 1;

        return (dictText, data[streamStart..rawEnd]);
    }

    private static int IndexOfBytes(byte[] haystack, byte[] needle, int from)
    {
        for (var i = from; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }

        return -1;
    }
}
