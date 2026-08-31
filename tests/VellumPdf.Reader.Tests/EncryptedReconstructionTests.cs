// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// #184 PR3: cross-reference reconstruction lifts the blanket refusal
/// <see cref="XrefReconstructionTests"/> used to pin for every encrypted fixture — a document whose
/// <c>/Encrypt</c> recovers and whose password is correct now opens instead of throwing
/// <see cref="UnsupportedPdfFeatureException"/> outright. What stays refused: a wrong or unrecoverable
/// key (<see cref="PdfPasswordException"/>), and a handler this library does not implement
/// (<see cref="UnsupportedPdfFeatureException"/> again, from <c>EncryptionSetup.Authenticate</c>'s
/// existing non-Standard-handler branch — PR3 adds no new refusal there, only a way to reach that
/// branch on a document reconstruction would previously never even discover was encrypted).
///
/// <para>
/// T1–T10 below are pinned against the PR3 design report's own T-list, not against the
/// implementation: this file was written without reading it. Every case is either a committed
/// fixture from Fixtures/Encrypted (undamaged self as the oracle — see
/// <see cref="XrefReconstructionTests.AssertAgreementOverResolvableObjects"/>) or a document built in
/// memory; nothing new is committed as a binary. The one exception is
/// <see cref="HandBuiltEncryptedDocuments.BuildCatalogInObjectStream"/> (T6), which no tool here can
/// produce as a fixture (see Fixtures/Encrypted/README.md, "Known gaps").
/// </para>
/// </summary>
public sealed class EncryptedReconstructionTests
{
    // ── Fixture loading and the password map ────────────────────────────────────────────────────

    private static byte[] Load(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded fixture '{name}' not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // Every fixture uses password "u" (README's matrix) EXCEPT the rows that deliberately test a
    // different password shape — see Fixtures/Encrypted/README.md's own account of each one.
    private static readonly IReadOnlyDictionary<string, string> NonDefaultPasswords =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enc-aes-128-emptyuser.pdf"] = "",
            ["enc-aes-128-tworevisions.pdf"] = "",
            ["enc-aes-128-longpassword.pdf"] = "0123456789abcdefghijklmnopqrstuvwxyzABCD",
            ["enc-aes-128-samepassword.pdf"] = "same",
            ["enc-aes-128-pdfdocpassword.pdf"] = "pässwörd",
        };

    private static string PasswordFor(string fixtureName) =>
        NonDefaultPasswords.TryGetValue(fixtureName, out var password) ? password : "u";

    // ── T1: every committed encrypted fixture, under M1, opens and agrees with itself ──────────────

    public static TheoryData<string> AllEncryptedFixtureNames =>
    [
        "enc-256-cleartextmd.pdf",
        "enc-256-linearized-objstm-cleartextmd.pdf",
        "enc-aes-128-cleartextmd.pdf",
        "enc-aes-128-emptyuser.pdf",
        "enc-aes-128-linearized.pdf",
        "enc-aes-128-longpassword.pdf",
        "enc-aes-128-nestedstrings.pdf",
        "enc-aes-128-pdfdocpassword.pdf",
        "enc-aes-128-samepassword.pdf",
        "enc-aes-128-tworevisions.pdf",
        "enc-aes-128.pdf",
        "enc-aes-256-r5.pdf",
        "enc-aes-256-r6.pdf",
        "enc-rc4-128-v4.pdf",
        "enc-rc4-128.pdf",
        "enc-rc4-40.pdf",
        "enc-rc4-objstm.pdf",
    ];

    /// <summary>
    /// M1 (startxref digits rewritten out of range) destroys the entry point without touching a
    /// single object byte, so every fixture in the corpus has to reconstruct AND authenticate under
    /// its own real password — the replacement for the blanket-refusal theory PR2 pinned here.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllEncryptedFixtureNames))]
    public void T1_EveryEncryptedFixture_underM1Damage_opensAndAgreesOverResolvableObjects(string fixtureName)
    {
        var original = Load(fixtureName);
        var password = PasswordFor(fixtureName);
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = password });

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0, $"{fixtureName}: expected a 'startxref' to corrupt");
        if (!XrefReconstructionTests.CanApplyM1(original, length))
        {
            // Same structural constraint as the plaintext matrix: a linearized fixture's last
            // startxref names its (early, short) front hint section (Annex F.3.5), so its digit
            // count is too small to represent any out-of-range, same-digit-count offset.
            Assert.Skip($"{fixtureName}: the recorded startxref has only {length} digit(s), too few " +
                $"to represent a same-digit-count value outside this {original.Length}-byte file.");
            return;
        }
        var damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, password);
        Assert.NotNull(reconstructed.Encryption);
        XrefReconstructionTests.AssertAgreementOverResolvableObjects(undamaged, reconstructed);
    }

    // ── T2: differential M1–M5, per revision — M4 diverges by revision ─────────────────────────────

    // R3/R4/R6, the revisions the PR3 design report names, plus R2 (enc-rc4-40.pdf costs nothing
    // extra to include and is the only R2 row in the corpus).
    private static readonly (string Fixture, int Revision)[] T2Fixtures =
    [
        ("enc-rc4-40.pdf", 2),
        ("enc-rc4-128.pdf", 3),
        ("enc-aes-128.pdf", 4),
        ("enc-aes-256-r6.pdf", 6),
    ];

    public static TheoryData<string, int, XrefReconstructionTests.DamageMode> T2Cases
    {
        get
        {
            var data = new TheoryData<string, int, XrefReconstructionTests.DamageMode>();
            foreach (var (fixture, revision) in T2Fixtures)
                foreach (var mode in Enum.GetValues<XrefReconstructionTests.DamageMode>())
                    data.Add(fixture, revision, mode);
            return data;
        }
    }

    /// <summary>
    /// The executable proof that <c>/ID</c> actually participates in key derivation, not just a
    /// citation: M4 (truncate at the last xref) destroys the SAME bytes regardless of revision, but
    /// Algorithm 2 step (e) reads <c>/ID[0]</c> only at R≤4, while Algorithm 2.A (R6) does not read
    /// it at all (NOTE 2). Identical damage, opposite outcomes, split on one number.
    /// </summary>
    [Theory]
    [MemberData(nameof(T2Cases))]
    public void T2_DamageMode_matchesTheExpectedOutcome_byRevision(
        string fixtureName, int revision, XrefReconstructionTests.DamageMode mode)
    {
        var original = Load(fixtureName);
        var password = PasswordFor(fixtureName);
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = password });

        switch (mode)
        {
            case XrefReconstructionTests.DamageMode.TrailingJunk:
                AssertM5DoesNotReconstruct(original, password);
                return;
            case XrefReconstructionTests.DamageMode.TruncatedAtLastXref:
                AssertM4OutcomeByRevision(original, undamaged, password, fixtureName, revision);
                return;
            default:
                AssertKeywordDamageRecovers(mode, original, undamaged, password, fixtureName);
                return;
        }
    }

    private static void AssertKeywordDamageRecovers(
        XrefReconstructionTests.DamageMode mode, byte[] original, PdfDocumentReader undamaged,
        string password, string fixtureName)
    {
        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        if (length == 0)
        {
            Assert.Skip($"{fixtureName} has no 'startxref' digits at all — {mode} has nothing to corrupt.");
            return;
        }

        byte[] damaged;
        switch (mode)
        {
            case XrefReconstructionTests.DamageMode.OutOfRangeOffset:
                if (!XrefReconstructionTests.CanApplyM1(original, length))
                {
                    Assert.Skip($"{fixtureName}: no same-digit-count value can be out of range for " +
                        $"this {original.Length}-byte file.");
                    return;
                }
                damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);
                break;
            case XrefReconstructionTests.DamageMode.InRangeNonXrefOffset:
                damaged = XrefReconstructionTests.ApplyM2_InRangeNonXrefStartxref(original, start, length);
                break;
            case XrefReconstructionTests.DamageMode.CorruptedKeyword:
                damaged = XrefReconstructionTests.ApplyM3_CorruptStartxrefKeyword(original);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        // As in XrefReconstructionTests's own plaintext matrix: verify the damage is real (rather
        // than an earlier revision's own intact entry point surviving) before treating this as a
        // reconstruction case at all.
        try
        {
            using var stillOpensNormally = PdfReader.Open(damaged, new PdfReaderOptions { Password = password });
            Assert.Skip($"{fixtureName}/{mode}: the damaged bytes still open through the ordinary path.");
            return;
        }
        catch (InvalidDataException)
        {
            // Genuinely damaged — proceed.
        }

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, password);
        Assert.NotNull(reconstructed.Encryption);
        XrefReconstructionTests.AssertAgreementOverResolvableObjects(undamaged, reconstructed);
    }

    private static void AssertM4OutcomeByRevision(
        byte[] original, PdfDocumentReader undamaged, string password, string fixtureName, int revision)
    {
        var cut = undamaged.StartXrefOffset;
        var damaged = original.AsSpan(0, cut).ToArray();

        try
        {
            using var stillOpensNormally = PdfReader.Open(damaged, new PdfReaderOptions { Password = password });
            Assert.Skip($"{fixtureName}: truncating at {cut} left an earlier revision intact.");
            return;
        }
        catch (InvalidDataException)
        {
            // Genuinely damaged — proceed.
        }

        if (revision <= 4)
        {
            // Truncation removes the trailer wholesale, so nothing left in the file declares /ID —
            // GetId0 falls back to [], Algorithm 2 step (e) contributes nothing, and the key
            // recomputed from that poisoned input does not match the producer's, even with the
            // right password. A clean, fail-closed PdfPasswordException, never plaintext.
            Assert.Throws<PdfPasswordException>(() =>
                PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = password }));
            return;
        }

        // R6: Algorithm 2.A never reads /ID (NOTE 2), so the identical truncation that poisons
        // R≤4's key derivation costs this revision nothing.
        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, password);
        Assert.NotNull(reconstructed.Encryption);
        XrefReconstructionTests.AssertAgreementOverResolvableObjects(undamaged, reconstructed);
    }

    private static void AssertM5DoesNotReconstruct(byte[] original, string password)
    {
        var damaged = XrefReconstructionTests.ApplyM5_TrailingJunk(original);

        using var plainOpen = PdfReader.Open(damaged, new PdfReaderOptions { Password = password });
        Assert.False(plainOpen.WasReconstructed);
        Assert.NotNull(plainOpen.Encryption);

        using var withReconstructionAllowed = PdfReader.Open(
            damaged, new PdfReaderOptions { AllowReconstruction = true, Password = password });
        Assert.False(withReconstructionAllowed.WasReconstructed,
            "M5 is the negative control: trailing junk must not trigger reconstruction merely because " +
            "AllowReconstruction is set.");
    }

    // ── T3: /ID[0] recovers off the xref-stream dict, byte for byte ────────────────────────────────

    private static byte[] Id0Bytes(PdfDocumentReader reader)
    {
        var id = Assert.IsType<PdfArray>(reader.Trailer.Get(PdfName.ID));
        var first = Assert.IsType<PdfHexString>(id[0]);
        return first.Bytes.ToArray();
    }

    /// <summary>
    /// <c>enc-rc4-objstm.pdf</c> carries no classic trailer at all — <c>/ID</c> lives only on the
    /// cross-reference STREAM's own dictionary (ISO 32000-2 Table 15, direct and unencrypted). M1
    /// leaves that dictionary reachable; M4 truncates it away entirely, which — being R4 — is
    /// another R≤4 <see cref="PdfPasswordException"/> proof alongside T2's.
    /// </summary>
    [Fact]
    public void T3_EncRc4Objstm_underM1_recoversId0ByteForByte_andM4RefusesWithPasswordException()
    {
        const string FixtureName = "enc-rc4-objstm.pdf";
        var original = Load(FixtureName);
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = "u" });
        var undamagedId0 = Id0Bytes(undamaged);

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        Assert.True(XrefReconstructionTests.CanApplyM1(original, length));
        var m1Damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(m1Damaged, "u");
        var reconstructedId0 = Id0Bytes(reconstructed);
        Assert.Equal(undamagedId0, reconstructedId0);

        var cut = undamaged.StartXrefOffset;
        var m4Damaged = original.AsSpan(0, cut).ToArray();
        Assert.Throws<PdfPasswordException>(() =>
            PdfReader.Open(m4Damaged, new PdfReaderOptions { AllowReconstruction = true, Password = "u" }));
    }

    // ── T4: the cross-reference-stream exemption, asserted on bytes ────────────────────────────────

    private static IReadOnlySet<long> ReflectCrossReferenceStreamOffsets(PdfDocumentReader reader) =>
        (IReadOnlySet<long>)typeof(PdfDocumentReader)
            .GetField("_crossReferenceStreamOffsets", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(reader)!;

    /// <summary>
    /// A cross-reference stream is exempt from decryption (ISO 32000-2 §7.5.8.2), so a WRONG
    /// recovered extent would not fail with a decryption error at all under RC4 — it would just hand
    /// back whatever raw bytes happen to sit at the wrong offset, which may or may not even fail to
    /// inflate. "Resolution did not throw" proves nothing here; byte equality against the undamaged
    /// parse is what actually proves A4 recorded the extent's header start correctly.
    /// </summary>
    [Fact]
    public void T4_EncRc4Objstm_underM1_xrefStreamBytesEqualUndamaged_andOffsetsAreRecovered()
    {
        const string FixtureName = "enc-rc4-objstm.pdf";
        const int XrefStreamObjectNumber = 10; // per EncryptedExemptionTests's own use of this fixture

        var original = Load(FixtureName);
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = "u" });
        var undamagedStream = undamaged.ResolveStream(XrefStreamObjectNumber);
        Assert.NotNull(undamagedStream);

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        Assert.True(XrefReconstructionTests.CanApplyM1(original, length));
        var damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, "u");

        var offsets = ReflectCrossReferenceStreamOffsets(reconstructed);
        Assert.True(offsets.Count > 0, "expected A4 to have recovered at least one cross-reference stream offset");

        var reconstructedStream = reconstructed.ResolveStream(XrefStreamObjectNumber);
        Assert.NotNull(reconstructedStream);
        Assert.Equal(
            XrefReconstructionTests.WriteToBytes(undamagedStream!.Dictionary),
            XrefReconstructionTests.WriteToBytes(reconstructedStream!.Dictionary));
        Assert.Equal(undamagedStream.RawBody.ToArray(), reconstructedStream.RawBody.ToArray());
    }

    // ── T5: /Metadata XMP equality — Phase B decodes an ObjStm before the catalog exists ───────────

    /// <summary>
    /// Zero production-code change backs this exemption (the plan's own verification): the
    /// <c>Catalog is null → false</c> guard in <c>IsDocumentMetadataStream</c> already sits before
    /// the memoisation latch, so this is a value-level check that reconstruction does not disturb
    /// it, not a test written to force a fix.
    /// </summary>
    [Fact]
    public void T5_LinearizedObjstmCleartextMetadata_underM1_recoversTheSameXmp()
    {
        const string FixtureName = "enc-256-linearized-objstm-cleartextmd.pdf";
        var original = Load(FixtureName);
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = "u" });
        var undamagedMetadataRef = Assert.IsType<PdfIndirectReference>(undamaged.Catalog.Get(new PdfName("Metadata")));
        var undamagedXmp = Encoding.UTF8.GetString(
            undamaged.GetDecodedStreamData(undamaged.ResolveStream(undamagedMetadataRef)!)!);
        Assert.StartsWith("<?xpacket", undamagedXmp, StringComparison.Ordinal);

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        if (!XrefReconstructionTests.CanApplyM1(original, length))
        {
            Assert.Skip($"{FixtureName}: the recorded startxref has only {length} digit(s) — the " +
                "linearized front hint section, too short for M1.");
            return;
        }
        var damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, "u");
        var reconstructedMetadataRef = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(new PdfName("Metadata")));
        var reconstructedXmp = Encoding.UTF8.GetString(
            reconstructed.GetDecodedStreamData(reconstructed.ResolveStream(reconstructedMetadataRef)!)!);

        Assert.Equal(undamagedXmp, reconstructedXmp);
    }

    // ── T6: the hand-built packed-catalog document — the only route is Phase B decryption ─────────

    // A real ISO 32000-2 §7.3.8-conformant top-level object header always begins its own line;
    // random ciphertext lining up with one by chance is astronomically unlikely, so this is a
    // faithful guard against a future edit to the builder accidentally adding one.
    private static void AssertNoTopLevelHeaderForCatalog(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);
        Assert.DoesNotMatch(@"(?m)^2 0 obj\b", text);
    }

    [Theory]
    [InlineData(XrefReconstructionTests.DamageMode.OutOfRangeOffset)]
    [InlineData(XrefReconstructionTests.DamageMode.InRangeNonXrefOffset)]
    [InlineData(XrefReconstructionTests.DamageMode.CorruptedKeyword)]
    public void T6_PackedCatalogHandBuiltDocument_recoversTheCatalogThroughPhaseBDecryption(
        XrefReconstructionTests.DamageMode mode)
    {
        var original = HandBuiltEncryptedDocuments.BuildCatalogInObjectStream();
        AssertNoTopLevelHeaderForCatalog(original);

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0);

        byte[] damaged;
        switch (mode)
        {
            case XrefReconstructionTests.DamageMode.OutOfRangeOffset:
                Assert.True(XrefReconstructionTests.CanApplyM1(original, length));
                damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);
                break;
            case XrefReconstructionTests.DamageMode.InRangeNonXrefOffset:
                damaged = XrefReconstructionTests.ApplyM2_InRangeNonXrefStartxref(original, start, length);
                break;
            case XrefReconstructionTests.DamageMode.CorruptedKeyword:
                damaged = XrefReconstructionTests.ApplyM3_CorruptStartxrefKeyword(original);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, "u");

        // The only route to /Root here is Phase B's object-stream decryption: nothing in the file
        // declares "2 0 obj" at the top level for a header scan to find on its own.
        Assert.Equal("Catalog", Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type)).Value);
        var pagesRef = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        var pages = Assert.IsType<PdfDictionary>(reconstructed.Resolve(pagesRef));
        Assert.Equal("Pages", Assert.IsType<PdfName>(pages.Get(PdfName.Type)).Value);
    }

    /// <summary>Another R≤4 proof (<see cref="HandBuiltEncryptedDocuments.Rc4EncryptDict"/> is /R 3).</summary>
    [Fact]
    public void T6_PackedCatalogHandBuiltDocument_underM4Truncation_refusesWithPasswordException()
    {
        var original = HandBuiltEncryptedDocuments.BuildCatalogInObjectStream();
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = "u" });

        var cut = undamaged.StartXrefOffset;
        var damaged = original.AsSpan(0, cut).ToArray();

        Assert.Throws<PdfPasswordException>(() =>
            PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = "u" }));
    }

    // ── T7: /Encrypt declared only on the /XRefStm cross-reference stream ─────────────────────────

    /// <summary>
    /// The ISO 32000-2 §7.5.8.4 hybrid-reference layout, hand-built because no tool in this corpus
    /// writes one (Fixtures/Encrypted/README.md): a classic trailer that names no <c>/Encrypt</c> at
    /// all, alongside a cross-reference STREAM — named only via the classic trailer's own
    /// <c>/XRefStm</c> entry, same revision, no <c>/Prev</c> — whose dictionary is where
    /// <c>/Encrypt</c> actually lives. A reader that only ever looked at classic trailers would open
    /// this as plaintext.
    /// </summary>
    private static byte[] BuildHybridEncryptOnXRefStmOnly()
    {
        var id = Convert.ToHexStringLower(HandBuiltEncryptedDocuments.Id0);

        var ms = new MemoryStream();
        void W(string t) => ms.Write(Encoding.Latin1.GetBytes(t));

        W("%PDF-1.5\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        var o4 = (int)ms.Position;
        W($"4 0 obj\n{HandBuiltEncryptedDocuments.Rc4EncryptDict}\nendobj\n");

        var rows = new List<byte>();
        void Row(byte type, int field2, int field3) => rows.AddRange(
        [
            type,
            (byte)(field2 >> 24), (byte)(field2 >> 16), (byte)(field2 >> 8), (byte)field2,
            (byte)(field3 >> 8), (byte)field3,
        ]);

        var xrefStreamOffset = (int)ms.Position;
        Row(0, 0, 65535);
        Row(1, o1, 0);
        Row(1, o2, 0);
        Row(1, o3, 0);
        Row(1, o4, 0);
        Row(1, xrefStreamOffset, 0); // the xref stream's own entry, for a reader that trusts only it

        var rowBytes = rows.ToArray();
        // The only place /Encrypt is declared anywhere in this file.
        W($"5 0 obj\n<< /Type /XRef /Size 6 /W [1 4 2] /Root 1 0 R /Encrypt 4 0 R "
          + $"/ID [<{id}><{id}>] /Length {rowBytes.Length} >>\nstream\n");
        ms.Write(rowBytes);
        W("\nendstream\nendobj\n");

        var classicXrefOffset = (int)ms.Position;
        W("xref\n0 6\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W($"{o3:D10} 00000 n \n");
        W($"{o4:D10} 00000 n \n");
        W($"{xrefStreamOffset:D10} 00000 n \n");
        // /ID has to be here too, not only on the xref-stream dict: XrefParser's existing hybrid
        // merge (predates #184) folds /Encrypt from /XRefStm into the classic trailer but nothing
        // else, so a classic trailer with no /ID of its own would derive the file key over an empty
        // /ID[0] and reject the real password. No /Encrypt here, though — a spec-conformant classic
        // trailer on its own, naming the xref stream only through /XRefStm (ISO 32000-2 §7.5.8.4).
        W($"trailer\n<< /Size 6 /Root 1 0 R /XRefStm {xrefStreamOffset} /ID [<{id}><{id}>] >>\n");
        W($"startxref\n{classicXrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void T7_HybridEncryptOnXRefStmOnly_underDamagedStartxref_doesNotOpenAsPlaintext()
    {
        var original = BuildHybridEncryptOnXRefStmOnly();

        // The undamaged premise: an unbroken reader honouring /XRefStm already finds /Encrypt there.
        using (var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = "u" }))
            Assert.NotNull(undamaged.Encryption);

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        Assert.True(XrefReconstructionTests.CanApplyM1(original, length));
        var damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);

        // No password: must fail closed on authentication, never on "no evidence of encryption".
        PdfDocumentReader? reader = null;
        Assert.Throws<PdfPasswordException>(() =>
            reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(damaged, "u");
        Assert.NotNull(reconstructed.Encryption);
    }

    // ── T8: public-key structural detection, trailer destroyed ─────────────────────────────────────

    // Table 20 common entries only (/Filter, /V) plus /SubFilter — no /O, /U, /R at all, which is
    // exactly the shape a rule keyed on those three is blind to (the corrected citation the plan
    // calls out: /Adobe.PubSec is only ever an EXAMPLE name in a NOTE, ISO 32000-2 §7.6.5.2 — this
    // uses a different, equally legal handler name on purpose). No xref, no trailer, no startxref
    // at all: A5's structural last resort is the only way to reach this dictionary.
    private static byte[] BuildPublicKeyHandlerNoTrailer(string subFilter)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W($"9 0 obj\n<< /Filter /Adobe.PPKLite /V 4 /SubFilter /{subFilter} >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Theory]
    [InlineData("adbe.pkcs7.s5")]
    [InlineData("adbe.pkcs7.s3")]
    public void T8_PublicKeyHandler_structurallyDetected_refusesNamingTheHandler(string subFilter)
    {
        var bytes = BuildPublicKeyHandlerNoTrailer(subFilter);

        PdfDocumentReader? reader = null;
        var ex = Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);

        // Detection (ClassifyEncryptionDictionary, PR3's own change) only recognises the dictionary
        // and synthesizes /Encrypt into the recovered trailer; the actual refusal, and its message,
        // come from EncryptionSetup.Authenticate's pre-existing non-Standard-handler branch, which
        // names the /Filter it read.
        Assert.Contains("Adobe.PPKLite", ex.Message, StringComparison.Ordinal);
    }

    // ── T9: ordinary plaintext /Filter and /V usage is not mistaken for encryption ─────────────────

    // #184 PR3 (security-conservative revision, overriding the plan's literal T9): a standalone
    // top-level dictionary shaped like `<< /Filter /X /V n >>` (name + integer, not a signature
    // dict) NEVER opens as plaintext any more, even lacking /O/U/R and a recognised /SubFilter — it
    // could be a custom or proprietary handler over real ciphertext, and there is no way to tell
    // that apart from a coincidence (see Row2 in XrefReconstructionTests.cs, which now pins the
    // refusal that shape gets). So that construction no longer demonstrates a false-positive guard;
    // what still needs proving is that the classifier does not fire on ordinary plaintext
    // constructs that happen to carry one of its two keys ALONE: a stream's own /Filter (no /V — a
    // stream dictionary is never encryption-shaped by this rule) and a form field's /V (no
    // /Filter). No xref/trailer at all, so this can only be read through reconstruction's
    // structural last resort — exactly where a false positive would fire if the rule keyed on
    // either half alone.
    private static byte[] BuildPlaintextWithOrdinaryFilterAndVUsage()
    {
        var plaintext = "Ordinary Flate content, not encryption evidence."u8.ToArray();
        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            deflate.Write(plaintext);
        var body = compressed.ToArray();

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        // An ordinary compressed stream: /Filter, no /V at all.
        W($"4 0 obj\n<< /Filter /FlateDecode /Length {body.Length} >>\nstream\n");
        ms.Write(body);
        W("\nendstream\nendobj\n");
        // An ordinary form field: /V, no /Filter at all.
        W("5 0 obj\n<< /FT /Tx /V (some text) >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void T9_OrdinaryStreamFilterAndFormFieldV_areNotMistakenForEncryption_stillReconstructsAsPlaintext()
    {
        var bytes = BuildPlaintextWithOrdinaryFilterAndVUsage();

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(bytes);

        Assert.Null(reconstructed.Encryption);
        Assert.Equal("Catalog", Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type)).Value);

        var stream = reconstructed.ResolveStream(4);
        Assert.NotNull(stream);
        var decoded = reconstructed.GetDecodedStreamData(stream!);
        Assert.NotNull(decoded);
        Assert.Equal("Ordinary Flate content, not encryption evidence.", Encoding.ASCII.GetString(decoded!));

        var field = Assert.IsType<PdfDictionary>(reconstructed.Resolve(5));
        var value = Assert.IsType<PdfLiteralString>(field.Get(new PdfName("V")));
        Assert.Equal("some text", Encoding.ASCII.GetString(value.Bytes.Span));
    }

    // ── T10: the missing-/ID message enrichment ─────────────────────────────────────────────────────

    /// <summary>
    /// Pins the message enrichment the plan records as approved alongside PR3 (2026-08-30): at R≤4
    /// with no <c>/ID</c> left to read, the generic "wrong password" message becomes indistinguishable
    /// from an actually-wrong one, so <c>EncryptionSetup.Authenticate</c> is meant to say the trailer
    /// carries no <c>/ID</c> instead. <c>enc-rc4-128.pdf</c> is R3.
    /// </summary>
    [Fact]
    public void T10_MissingIdAfterM4Truncation_passwordExceptionMessageMentionsId()
    {
        const string FixtureName = "enc-rc4-128.pdf";
        var original = Load(FixtureName);
        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = "u" });
        var cut = undamaged.StartXrefOffset;
        var damaged = original.AsSpan(0, cut).ToArray();

        var ex = Assert.Throws<PdfPasswordException>(() =>
            PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = "u" }));
        Assert.Contains("/ID", ex.Message, StringComparison.Ordinal);
    }

    // ── Review finding (HIGH): A4 flate-bomb DoS ────────────────────────────────────────────────

    // A4 (the xref-stream-candidate decode step that runs once the recovered trailer carries
    // /Encrypt) inflated every candidate's declared body without charging the reconstruction cost
    // budget — a run of tiny, highly compressible fake xref streams could burn unbounded CPU
    // decompressing each one before ever reaching Filters' own 512 MiB per-decode cap. The fix
    // bounds and charges that decode work, so a run of Flate bombs trips the fail-closed cost
    // budget instead of running for minutes.
    private static byte[] BuildFlateBombInflatingPastTheDecodeCap()
    {
        // 600 MiB of zero bytes deflates to roughly 600 KB (measured) — small enough to embed
        // many copies in an in-memory fixture, and past the 512 MiB ReaderLimits.MaxDecodedBytes
        // default cap a single decode call already enforces, so this targets the COST of getting
        // there across several candidates, not whether any one of them eventually fails.
        const long InflatedBytes = 600L * 1024 * 1024;

        var compressed = new MemoryStream();
        using (var deflate = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            long written = 0;
            while (written < InflatedBytes)
            {
                var chunkLength = (int)Math.Min(zeros.Length, InflatedBytes - written);
                deflate.Write(zeros, 0, chunkLength);
                written += chunkLength;
            }
        }
        return compressed.ToArray();
    }

    // A genuinely encrypted document (HandBuiltEncryptedDocuments.Rc4EncryptDict, declared on a
    // real classic trailer) so A4 actually runs, padded with xref-stream-shaped Flate-bomb
    // candidates. Each declares a small, DIRECT /Length equal to the bomb's own compressed size,
    // so extent resolution never has to search for 'endstream' — the cost under test is entirely
    // A4 attempting to inflate each candidate's body, not locating it.
    private static byte[] BuildEncryptedDocumentPaddedWithXrefStreamFlateBombs(int bombCount)
    {
        var flateBomb = BuildFlateBombInflatingPastTheDecodeCap();
        var id = Convert.ToHexStringLower(HandBuiltEncryptedDocuments.Id0);

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W($"9 0 obj\n{HandBuiltEncryptedDocuments.Rc4EncryptDict}\nendobj\n");

        for (var i = 0; i < bombCount; i++)
        {
            W($"{100 + i} 0 obj\n<< /Type /XRef /W [1 2 1] /Size 1 /Filter /FlateDecode "
              + $"/Length {flateBomb.Length} >>\nstream\n");
            ms.Write(flateBomb);
            W("\nendstream\nendobj\n");
        }

        var xrefOffset = (int)ms.Position;
        W("xref\n0 4\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W($"{o3:D10} 00000 n \n");
        W($"trailer\n<< /Size 10 /Root 1 0 R /Encrypt 9 0 R /ID [<{id}><{id}>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// The DoS proof is the STOPWATCH, not just the exception type: a budget that eventually
    /// refuses after minutes of decompression is still a defect, just a slower one. 20 candidates
    /// each inflating to 600 MiB is 12 GiB of decompression work if none of it is charged; bounded
    /// and charged, refusal has to land in a small fraction of that.
    /// </summary>
    [Fact]
    public void FindingHigh_A4FlateBombPadding_refusesQuickly_viaCostBudget()
    {
        const int BombCount = 20;
        var original = BuildEncryptedDocumentPaddedWithXrefStreamFlateBombs(BombCount);

        var (start, length) = XrefReconstructionTests.FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        Assert.True(XrefReconstructionTests.CanApplyM1(original, length));
        var damaged = XrefReconstructionTests.ApplyM1_OutOfRangeStartxref(original, start, length);

        var stopwatch = Stopwatch.StartNew();
        PdfDocumentReader? reader = null;
        Assert.Throws<InvalidDataException>(() =>
            reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true }));
        stopwatch.Stop();

        Assert.Null(reader);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"expected the cost budget to refuse quickly; took {stopwatch.Elapsed}.");
    }

    // ── Review finding (MEDIUM): a public-key dict inside a walk-swallowed region ───────────────

    // ISO 32000-2 §7.6.5.2 names /SubFilter values adbe.pkcs7.s3/s4/s5 for the public-key
    // handler (Table 27); the whole-file structural sweep that exists to catch encryption
    // evidence sitting somewhere the ordinary walk never tokenizes did not fingerprint them, so a
    // public-key dictionary inside a region an over-long /Length swallows to EOF escaped
    // detection entirely — the same silent-plaintext failure class the /Encrypt-token sweep
    // exists to close, but for a dictionary that never declares /Encrypt itself and so relies
    // entirely on the trailer naming it, which a broken startxref plus a swallowed body both
    // remove. The fix adds the adbe.pkcs7.s* fingerprint to that sweep.
    private static byte[] BuildSwallowedRegionProbe(string? subFilter)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        // A wildly over-long, unverifiable /Length with no real 'endstream' anywhere in the rest
        // of the file: extent resolution can't verify the declared length, falls through to a
        // scan for the terminator, finds none, and — fail closed — runs the body to EOF,
        // swallowing everything written after this point as this stream's own content.
        W("4 0 obj\n<< /Length 9000000 >>\nstream\n");
        W(subFilter is null
            ? "9 0 obj\n<< /Type /Marker /Value (NotEncryptionEvidence) >>\nendobj\n"
            : $"9 0 obj\n<< /Filter /Adobe.PPKLite /V 4 /SubFilter /{subFilter} >>\nendobj\n");
        // Deliberately no 'endstream' anywhere.
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Theory]
    [InlineData("adbe.pkcs7.s5")]
    [InlineData("adbe.pkcs7.s3")]
    public void FindingMedium_PublicKeyDictInsideSwallowedRegion_stillRefuses(string subFilter)
    {
        var bytes = BuildSwallowedRegionProbe(subFilter);

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The negative control the finding itself calls for: the identical swallowed-region shape,
    /// but with an ordinary marker object instead of a public-key dictionary. Proves the new
    /// adbe.pkcs7.s* fingerprint stays narrow — it must not fire on a region merely because it was
    /// swallowed, only because a real public-key marker sits inside it.
    /// </summary>
    [Fact]
    public void FindingMedium_OrdinaryObjectInsideSwallowedRegion_stillReconstructsAsPlaintext()
    {
        var bytes = BuildSwallowedRegionProbe(subFilter: null);

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(bytes);

        Assert.Null(reconstructed.Encryption);
        Assert.Equal("Catalog", Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type)).Value);
    }

    // ── Review finding (round 2, MEDIUM): a #XX-escaped /SubFilter evades a length-capped sweep ─

    // ISO 32000-2 §7.3.5 lets a name spell any character as a #XX hex escape. The fingerprint
    // the fix above added skipped any name token whose RAW length exceeded 32 bytes, but
    // "adbe.pkcs7.s5" (13 bytes plain) written with every byte escaped is 39 raw bytes — the
    // parser itself decodes #XX with no length limit, so an escaped /SubFilter authenticates
    // normally through the ordinary path while still dodging a length-capped sweep. The same
    // escape-evasion class #184 PR2 round 2 already closed for /Encrypt (decode #XX before
    // matching; don't cap the raw token first). The fix raises the sweep's cap to at least 3x
    // the longest fingerprint target, wide enough that a fully-escaped token still fits.
    private static byte[] BuildSwallowedRegionProbeWithEscapedSubFilter(string escapedSubFilterToken)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        // Same swallowed-region shape as the plain-token case above: an over-long, unverifiable
        // /Length with no real 'endstream' anywhere in the rest of the file runs the body to EOF.
        W("4 0 obj\n<< /Length 9000000 >>\nstream\n");
        W($"9 0 obj\n<< /Filter /Adobe.PPKLite /V 4 /SubFilter /{escapedSubFilterToken} >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    public static TheoryData<string, int> EscapedSubFilterCases
    {
        get
        {
            var data = new TheoryData<string, int>();
            // Fully escaped: every one of the 13 characters in "adbe.pkcs7.s5" as #XX — raw 39
            // bytes, the strongest form and the one the finding names directly.
            data.Add("#61#64#62#65#2E#70#6B#63#73#37#2E#73#35", 39);
            // Partially escaped: only the 10 characters after "adb" — raw 33 bytes, one byte
            // past the old 32-byte cap. Pins the boundary itself, not just a token far beyond it.
            data.Add("adb#65#2E#70#6B#63#73#37#2E#73#35", 33);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EscapedSubFilterCases))]
    public void FindingMedium_EscapedPublicKeySubFilterInsideSwallowedRegion_stillRefuses(
        string escapedSubFilterToken, int expectedRawLength)
    {
        // Guards the case's own premise: if the escaped token were shorter than intended, this
        // would stop pinning the boundary it claims to.
        Assert.Equal(expectedRawLength, escapedSubFilterToken.Length);

        var bytes = BuildSwallowedRegionProbeWithEscapedSubFilter(escapedSubFilterToken);

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The negative control the finding calls for: <c>adbe.pkcs7.detached</c> and
    /// <c>adbe.pkcs7.sha1</c> are ISO 32000-2 §12.8.3.3's ordinary signature <c>/SubFilter</c>
    /// values, not the public-key encryption handler's — sharing the <c>adbe.pkcs7.</c> prefix
    /// with s3/s4/s5 (§7.6.5.2) is coincidence, not kinship. A real signature dictionary
    /// commonly carries <c>/Filter /Adobe.PPKLite</c> too (the same literal name the public-key
    /// ENCRYPTION handler uses), so this is the closest a genuine document gets to the shape
    /// above without becoming it — the fingerprint has to match the whole token, not the shared
    /// prefix.
    /// </summary>
    private static byte[] BuildDamagedPlaintextWithASignedSubFilterDetached()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("9 0 obj\n<< /Type /Sig /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.detached "
          + "/ByteRange [0 100 200 300] /Contents <00112233445566778899aabbccddeeff> >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void FindingMedium_SignedPlaintextWithAdbePkcs7Detached_stillReconstructsAsPlaintext()
    {
        var bytes = BuildDamagedPlaintextWithASignedSubFilterDetached();

        using var reconstructed = XrefReconstructionTests.OpenReconstructed(bytes);

        Assert.Null(reconstructed.Encryption);
        Assert.Equal("Catalog", Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type)).Value);
    }
}
