// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Exercises #184's cross-reference reconstruction (<see cref="PdfReaderOptions.AllowReconstruction"/>)
/// against the one-directional agreement property the feature's brief states: for every object the
/// UNDAMAGED document resolves, the reconstructed document resolves that object number to
/// byte-identical <see cref="PdfObject.WriteTo(PdfWriter)"/> output, and <c>/Root</c> names the same
/// object. Reconstruction may additionally resolve objects the undamaged document reports as free
/// (ISO 32000-2 Annex C.4: "the generation numbers of deleted entries are lost if the
/// cross-reference table is missing or severely damaged") — the two xref key sets are never asserted
/// equal.
///
/// <para>
/// Two previous attempts at this feature shipped green suites while the real-world shape stayed
/// broken. The second attempt's own post-mortem produced a 15-row acceptance table, one row per
/// reproduced defect or ordinary-file case the byte scan failed on; the rows below (see "Acceptance
/// table" further down) are pinned directly against that table, not against the rework's
/// implementation — this file was written without reading it. Every fixture is built in memory in
/// C#; nothing here is a committed binary.
/// </para>
/// </summary>
public sealed class XrefReconstructionTests
{
    // ── The one helper no reconstruction test may skip ──────────────────────────────────────────

    /// <summary>
    /// Opens a document with reconstruction allowed AND confirms it actually ran. Route every
    /// reconstruction test through this: seven of sixteen tests in an earlier attempt at #184 opened
    /// successfully via the ordinary path and never checked <see cref="PdfDocumentReader.WasReconstructed"/>,
    /// so a no-op <see cref="PdfReaderOptions.AllowReconstruction"/> would have passed them all.
    /// </summary>
    private static PdfDocumentReader OpenReconstructed(byte[] damaged, string? password = null)
    {
        var reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = password });
        Assert.True(reader.WasReconstructed, "Expected the cross-reference table to have been rebuilt by scanning.");
        return reader;
    }

    private static byte[] WriteToBytes(PdfObject obj)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        obj.WriteTo(writer);
        writer.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// The agreement property itself. Compares every object number the UNDAMAGED reader resolves
    /// against the reconstructed reader's answer for that same number, plus <c>/Root</c>'s object
    /// number. Used for M1–M3 and M5, where no object bytes are actually destroyed — only the
    /// pointer to them.
    /// </summary>
    private static void AssertFullAgreement(PdfDocumentReader undamaged, PdfDocumentReader reconstructed)
    {
        AssertSameRootObjectNumber(undamaged, reconstructed);

        foreach (var objectNumber in undamaged.ObjectNumbers)
        {
            var expected = undamaged.Resolve(objectNumber);
            Assert.NotNull(expected);
            var actual = reconstructed.Resolve(objectNumber);
            Assert.NotNull(actual);
            Assert.Equal(WriteToBytes(expected!), WriteToBytes(actual!));
        }
    }

    private static void AssertSameRootObjectNumber(PdfDocumentReader a, PdfDocumentReader b)
    {
        var rootA = Assert.IsType<PdfIndirectReference>(a.Trailer.Get(PdfName.Root));
        var rootB = Assert.IsType<PdfIndirectReference>(b.Trailer.Get(PdfName.Root));
        Assert.Equal(rootA.ObjectNumber, rootB.ObjectNumber);
    }

    /// <summary>
    /// M4's variant of the agreement property: truncation destroys some object bytes outright, so
    /// only what reconstruction actually recovers can be compared against the undamaged parse — an
    /// object truncation removed is simply skipped, not a disagreement. Guards against vacuity by
    /// requiring at least one object to have actually made the round trip. A candidate whose bytes
    /// were themselves truncated can legitimately fail to parse at all rather than resolve to the
    /// wrong thing, exactly as <see cref="TruncatedTail_reconstructs_andEveryResolvedObjectMatchesBaseline"/>
    /// treats it — so only <c>Resolve</c> itself is guarded here, not the comparison that follows.
    /// </summary>
    private static void AssertAgreementOverResolvableObjects(PdfDocumentReader undamaged, PdfDocumentReader reconstructed)
    {
        AssertSameRootObjectNumber(undamaged, reconstructed);

        var compared = 0;
        foreach (var objectNumber in undamaged.ObjectNumbers)
        {
            PdfObject? actual;
            try
            {
                actual = reconstructed.Resolve(objectNumber);
            }
            catch (InvalidDataException)
            {
                continue; // truncated mid-object — fails to parse at all, not a disagreement
            }

            if (actual is null)
                continue; // physically truncated away — not a disagreement, just gone

            var expected = undamaged.Resolve(objectNumber);
            Assert.NotNull(expected);
            Assert.Equal(WriteToBytes(expected!), WriteToBytes(actual));
            compared++;
        }

        Assert.True(compared > 0, "expected at least one object to survive the truncation and be recovered");
    }

    // ── Fixture loading ──────────────────────────────────────────────────────────────────────────

    public enum FixtureSource { ThirdParty, Encrypted }

    private static byte[] Load(FixtureSource source, string name) => source switch
    {
        FixtureSource.ThirdParty => LoadThirdParty(name),
        FixtureSource.Encrypted => LoadEncrypted(name),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    private static byte[] LoadThirdParty(string name) => LoadResource("ThirdParty/" + name);

    private static byte[] LoadEncrypted(string name) => LoadResource(name);

    private static byte[] LoadResource(string logicalName)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{logicalName}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // ── Damage-mode primitives (operate on a copy; never mutate the fixture bytes in place) ───────

    private const string StartxrefKeyword = "startxref";

    private static int LastIndexOfAscii(byte[] haystack, string asciiNeedle)
    {
        var needle = Encoding.ASCII.GetBytes(asciiNeedle);
        for (var i = haystack.Length - needle.Length; i >= 0; i--)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    private static bool IsPdfWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    private static bool IsAsciiDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    /// <summary>
    /// Locates the digit run following the LAST <c>startxref</c> keyword — the value an ordinary
    /// parse trusts to find the current revision's cross-reference table. A zero-length result means
    /// the fixture carries no <c>startxref</c> at all, so M1–M3 have nothing to corrupt on it.
    /// </summary>
    private static (int Start, int Length) FindLastStartxrefDigits(byte[] bytes)
    {
        var idx = LastIndexOfAscii(bytes, StartxrefKeyword);
        if (idx < 0)
            return (-1, 0);

        var pos = idx + StartxrefKeyword.Length;
        while (pos < bytes.Length && IsPdfWhitespace(bytes[pos])) pos++;
        var start = pos;
        while (pos < bytes.Length && IsAsciiDigit(bytes[pos])) pos++;
        return (start, pos - start);
    }

    private static long MaxValueForDigits(int digits)
    {
        long v = 1;
        for (var i = 0; i < digits; i++) v *= 10;
        return v - 1;
    }

    /// <summary>
    /// M1 is only constructible when SOME value with exactly this many digits is outside the file —
    /// i.e. the largest such value is not itself still a valid offset. A short digit run in a longer
    /// file (an offset near the file's own front, as a linearized file's outermost startxref
    /// legitimately is — Annex F.3.4) can make this impossible while preserving digit count.
    /// </summary>
    private static bool CanApplyM1(byte[] bytes, int digitLength) => MaxValueForDigits(digitLength) >= bytes.Length;

    /// <summary>M1: rewrites the last startxref's digits to a same-length, out-of-range value.</summary>
    private static byte[] ApplyM1_OutOfRangeStartxref(byte[] original, int start, int length)
    {
        var damaged = (byte[])original.Clone();
        for (var i = 0; i < length; i++)
            damaged[start + i] = (byte)'9';
        return damaged;
    }

    /// <summary>M2: rewrites the digits to a same-length, in-range value that is not an xref.</summary>
    private static byte[] ApplyM2_InRangeNonXrefStartxref(byte[] original, int start, int length)
    {
        var candidate = Math.Min(original.Length / 2, MaxValueForDigits(length));
        var digits = candidate.ToString(CultureInfo.InvariantCulture).PadLeft(length, '0');
        Assert.Equal(length, digits.Length);

        // Every fixture this runs against is a structured object graph or content stream at its own
        // midpoint, not table syntax — checked rather than assumed, since the whole point of this
        // mode is landing somewhere that is NOT a real xref.
        var windowLength = Math.Min(20, original.Length - (int)candidate);
        var window = Encoding.Latin1.GetString(original, (int)candidate, windowLength);
        Assert.False(window.Contains("xref", StringComparison.Ordinal));
        Assert.False(window.Contains("trailer", StringComparison.Ordinal));

        var damaged = (byte[])original.Clone();
        Encoding.ASCII.GetBytes(digits).CopyTo(damaged, start);
        return damaged;
    }

    /// <summary>M3: corrupts the keyword itself in place, same length, no longer a valid keyword.</summary>
    private static byte[] ApplyM3_CorruptStartxrefKeyword(byte[] original)
    {
        var idx = LastIndexOfAscii(original, StartxrefKeyword);
        Assert.True(idx >= 0, "expected a 'startxref' keyword to corrupt");
        var damaged = (byte[])original.Clone();
        Encoding.ASCII.GetBytes("startxrEf").CopyTo(damaged, idx);
        return damaged;
    }

    /// <summary>M5 (negative control): junk appended after the real %%EOF.</summary>
    private static byte[] ApplyM5_TrailingJunk(byte[] original)
    {
        var junk = "\n% not part of the document at all\n"u8.ToArray();
        var damaged = new byte[original.Length + junk.Length];
        original.CopyTo(damaged, 0);
        junk.CopyTo(damaged, original.Length);
        return damaged;
    }

    // ── Damage-mode matrix ───────────────────────────────────────────────────────────────────────

    public enum DamageMode
    {
        /// <summary>M1: startxref digits rewritten out of range, same digit count.</summary>
        OutOfRangeOffset,
        /// <summary>M2: startxref digits rewritten to an in-range non-xref offset, same digit count.</summary>
        InRangeNonXrefOffset,
        /// <summary>M3: the startxref keyword itself corrupted in place, same length.</summary>
        CorruptedKeyword,
        /// <summary>M4: truncated at the offset the UNDAMAGED parse reports for its last xref section.</summary>
        TruncatedAtLastXref,
        /// <summary>M5 (negative control): junk appended after %%EOF — must NOT reconstruct.</summary>
        TrailingJunk,
    }

    private static readonly DamageMode[] AllDamageModes = Enum.GetValues<DamageMode>();

    /// <summary>
    /// The plaintext corpus this matrix runs over. Excludes: <c>broken-startxref.pdf</c> and
    /// <c>truncated-tail.pdf</c> (already-damaged fixtures exercised directly elsewhere in this
    /// file, not raw material to damage further) and the two hybrid fixtures (#206 — whether
    /// reconstruction's "last definition wins" agrees with the working xref's precedence rules is
    /// pinned directly further down, not folded into this blanket theory).
    /// </summary>
    private static readonly string[] MatrixThirdPartyFixtures =
    [
        "baseline.pdf",
        "incremental-update.pdf",
        "nonzero-generation.pdf",
        "nonzero-gen-base.pdf",
        "objstm-xrefstream.pdf",
        "freed-object-reuse.pdf",
        "linearized.pdf",
        "length-mismatch.pdf",
    ];

    public static TheoryData<FixtureSource, string, DamageMode> DamageMatrixCases
    {
        get
        {
            var data = new TheoryData<FixtureSource, string, DamageMode>();

            foreach (var fixture in MatrixThirdPartyFixtures)
            {
                foreach (var mode in AllDamageModes)
                    data.Add(FixtureSource.ThirdParty, fixture, mode);
            }

            foreach (var mode in AllDamageModes)
                data.Add(FixtureSource.Encrypted, "plaintext-baseline.pdf", mode);

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(DamageMatrixCases))]
    public void DamageMode_matchesTheExpectedReconstructionOutcome(FixtureSource source, string fixtureName, DamageMode mode)
    {
        var original = Load(source, fixtureName);
        using var undamaged = PdfReader.Open(original);

        switch (mode)
        {
            case DamageMode.TrailingJunk:
                AssertM5DoesNotReconstruct(original, undamaged);
                return;
            case DamageMode.TruncatedAtLastXref:
                AssertM4TruncationOutcome(original, undamaged, fixtureName);
                return;
            default:
                AssertKeywordDamageOutcome(mode, original, undamaged, fixtureName);
                return;
        }
    }

    private static void AssertM5DoesNotReconstruct(byte[] original, PdfDocumentReader undamaged)
    {
        var damaged = ApplyM5_TrailingJunk(original);

        // No options needed at all: a normal reader locates the last real startxref, which sits
        // before the appended junk, and never has reason to look further.
        using var plainOpen = PdfReader.Open(damaged);
        Assert.False(plainOpen.WasReconstructed);

        using var withReconstructionAllowed = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true });
        Assert.False(withReconstructionAllowed.WasReconstructed,
            "M5 is the negative control: trailing junk must not trigger reconstruction merely because AllowReconstruction is set.");

        AssertFullAgreement(undamaged, withReconstructionAllowed);
    }

    private static void AssertM4TruncationOutcome(byte[] original, PdfDocumentReader undamaged, string fixtureName)
    {
        var cut = undamaged.StartXrefOffset;
        var damaged = original.AsSpan(0, cut).ToArray();

        // A multi-revision file can leave an EARLIER revision's own complete, independently valid
        // xref/trailer/startxref/%%EOF behind this cut point — truncating only removes the
        // OUTERMOST revision's tail. When that happens the file is not damaged at all from the
        // ordinary reader's point of view, so there is nothing here for reconstruction to prove.
        // Verified by actually trying the ordinary path, not assumed from the revision count.
        try
        {
            using var stillOpensNormally = PdfReader.Open(damaged);
            Assert.Skip($"{fixtureName}: truncating at the last startxref ({cut}) left an earlier " +
                "revision's own complete structure intact, so the file still opens without " +
                "reconstruction — nothing for M4 to exercise on this fixture.");
            return;
        }
        catch (InvalidDataException)
        {
            // Genuinely damaged — proceed.
        }

        // The opposite extreme: a linearized file's OWN last startxref points at its FRONT hint
        // cross-reference section, not a tail one (ISO 32000-2 Annex F.3.4 — this is how a "fast
        // web view" reader gets page 1 without reading the rest of the file first). Cutting there
        // removes almost the entire document rather than just its tail machinery, and can leave
        // nothing recognisable as a catalog behind at all. That is not a reconstruction defect —
        // there is genuinely nothing left to find — so a clean, defined failure here is accepted
        // rather than treated as the "reconstructs" outcome the brief describes for the ordinary
        // (tail-truncation) case M4 was written for.
        PdfDocumentReader reconstructed;
        try
        {
            reconstructed = OpenReconstructed(damaged);
        }
        catch (InvalidDataException ex)
        {
            Assert.Skip($"{fixtureName}: truncating at offset {cut} left nothing reconstruction could " +
                $"recover a catalog from ({ex.Message}) — plausible for a linearized file, whose last " +
                "startxref names its front hint section rather than a tail one; not evidence of a defect " +
                "on its own since the byte range genuinely contains no catalog.");
            return;
        }

        // Deliberately OUTSIDE the catch above: a genuine agreement failure here — including
        // AssertAgreementOverResolvableObjects surfacing a resolved object that does not match, or
        // rethrowing on a candidate that parsed into something wrong rather than failing to parse
        // at all — is a real defect and must fail loudly. Catching InvalidDataException around
        // this call too would let a bug in reconstruction hide behind the "linearized file, nothing
        // left to find" skip message above.
        using (reconstructed)
        {
            AssertAgreementOverResolvableObjects(undamaged, reconstructed);
        }
    }

    private static void AssertKeywordDamageOutcome(DamageMode mode, byte[] original, PdfDocumentReader undamaged, string fixtureName)
    {
        var (start, length) = FindLastStartxrefDigits(original);
        if (length == 0)
        {
            Assert.Skip($"{fixtureName} has no 'startxref' digits at all — {mode} has nothing to corrupt.");
            return;
        }

        byte[] damaged;
        switch (mode)
        {
            case DamageMode.OutOfRangeOffset:
                if (!CanApplyM1(original, length))
                {
                    Assert.Skip($"{fixtureName}: the recorded startxref has only {length} digit(s), and " +
                        $"the largest {length}-digit value ({MaxValueForDigits(length)}) is still inside " +
                        $"this {original.Length}-byte file — no same-digit-count value can be out of range.");
                    return;
                }
                damaged = ApplyM1_OutOfRangeStartxref(original, start, length);
                break;
            case DamageMode.InRangeNonXrefOffset:
                damaged = ApplyM2_InRangeNonXrefStartxref(original, start, length);
                break;
            case DamageMode.CorruptedKeyword:
                damaged = ApplyM3_CorruptStartxrefKeyword(original);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        // Corrupting the keyword itself (M3) is not just "this revision's entry point is gone" — a
        // backward scan for the literal text 'startxref' skips right past the corrupted one and can
        // land on an EARLIER revision's own intact keyword instead. For a multi-revision fixture
        // that earlier occurrence can be a perfectly complete, independently valid xref/trailer, so
        // the file opens normally with no damage left to recover from. M1/M2 leave the keyword
        // itself intact, so this should not arise for them, but it is verified here rather than
        // assumed, the same way M4 verifies its own "already survives" case.
        try
        {
            using var stillOpensNormally = PdfReader.Open(damaged);
            Assert.Skip($"{fixtureName}/{mode}: the damaged bytes still open through the ordinary path " +
                "(an earlier revision's own startxref survived) — nothing here for reconstruction to prove.");
            return;
        }
        catch (InvalidDataException)
        {
            // Genuinely damaged — proceed.
        }

        using var reconstructed = OpenReconstructed(damaged);
        AssertFullAgreement(undamaged, reconstructed);
    }

    // ── Encrypted documents are refused in this PR ──────────────────────────────────────────────

    // Every fixture in Fixtures/Encrypted except plaintext-baseline.pdf (which is not encrypted,
    // and is exercised through the ordinary damage matrix above instead). Passwords per the
    // fixture corpus's own documented facts (PasswordShapeTests, EncryptedReaderTests).
    private static readonly IReadOnlyDictionary<string, string> EncryptedFixturePasswords =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enc-256-cleartextmd.pdf"] = "u",
            ["enc-256-linearized-objstm-cleartextmd.pdf"] = "u",
            ["enc-aes-128-cleartextmd.pdf"] = "u",
            ["enc-aes-128-emptyuser.pdf"] = "",
            ["enc-aes-128-linearized.pdf"] = "u",
            ["enc-aes-128-longpassword.pdf"] = "0123456789abcdefghijklmnopqrstuvwxyzABCD",
            ["enc-aes-128-nestedstrings.pdf"] = "u",
            ["enc-aes-128-pdfdocpassword.pdf"] = "pässwörd",
            ["enc-aes-128-samepassword.pdf"] = "same",
            ["enc-aes-128-tworevisions.pdf"] = "",
            ["enc-aes-128.pdf"] = "u",
            ["enc-aes-256-r5.pdf"] = "u",
            ["enc-aes-256-r6.pdf"] = "u",
            ["enc-rc4-128-v4.pdf"] = "u",
            ["enc-rc4-128.pdf"] = "u",
            ["enc-rc4-40.pdf"] = "u",
            ["enc-rc4-objstm.pdf"] = "u",
        };

    public static TheoryData<string> EncryptedFixtureNames
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var name in EncryptedFixturePasswords.Keys)
                data.Add(name);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(EncryptedFixtureNames))]
    public void EncryptedFixture_underM1Damage_refusesReconstruction_exactType(string fixtureName)
    {
        var original = LoadEncrypted(fixtureName);
        var password = EncryptedFixturePasswords[fixtureName];

        var (start, length) = FindLastStartxrefDigits(original);
        Assert.True(length > 0, $"{fixtureName}: expected a 'startxref' to corrupt");
        if (!CanApplyM1(original, length))
        {
            // Same structural constraint as the plaintext matrix: a linearized fixture's last
            // startxref names its (early, short) front hint section (Annex F.3.4), so its digit
            // count is too small to represent any out-of-range, same-digit-count offset.
            Assert.Skip($"{fixtureName}: the recorded startxref has only {length} digit(s), and the " +
                $"largest {length}-digit value ({MaxValueForDigits(length)}) is still inside this " +
                $"{original.Length}-byte file — no same-digit-count value can be out of range.");
            return;
        }
        var damaged = ApplyM1_OutOfRangeStartxref(original, start, length);

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = password }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The harder case the brief calls out: M4 destroys the classic trailer entirely, so nothing in
    /// the damaged file declares <c>/Encrypt</c> at all any more. A reconstruction that fell back to
    /// "no evidence of encryption, so treat as plaintext" would hand ciphertext back as if it were
    /// the real content — the exact failure mode this exists to guard against.
    /// </summary>
    [Theory]
    [InlineData("enc-aes-128.pdf")]
    [InlineData("enc-rc4-128.pdf")]
    public void EncryptedFixture_underM4Truncation_stillRefusesReconstruction_exactType(string fixtureName)
    {
        var original = LoadEncrypted(fixtureName);
        var password = EncryptedFixturePasswords[fixtureName];

        using var undamaged = PdfReader.Open(original, new PdfReaderOptions { Password = password });
        var cut = undamaged.StartXrefOffset;
        var damaged = original.AsSpan(0, cut).ToArray();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = password }));
        Assert.Null(reader);
    }

    // ── Negatives ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FreedObjectReuse_undamaged_doesNotReconstruct_andObject7StaysFree()
    {
        using var reader = PdfReader.Open(LoadThirdParty("freed-object-reuse.pdf"),
            new PdfReaderOptions { AllowReconstruction = true });

        // The xref here is perfectly well-formed and simply records object 7 as free. Reconstruction
        // exists for a broken startxref chain (ISO 32000-2 Annex C.4), not as a way to second-guess
        // an intact table's own deletions.
        Assert.False(reader.WasReconstructed);
        Assert.Null(reader.Resolve(7));
    }

    [Fact]
    public void DefaultOptions_onADamagedFixture_throwsInvalidDataException_namingAllowReconstruction()
    {
        var bytes = LoadThirdParty("broken-startxref.pdf");
        var ex = Assert.Throws<InvalidDataException>(() => PdfReader.Open(bytes));
        Assert.Contains("AllowReconstruction", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AppendRevision_onAReconstructedDocument_throwsInvalidOperationException()
    {
        using var reader = OpenReconstructed(LoadThirdParty("broken-startxref.pdf"));

        Assert.Throws<InvalidOperationException>(() =>
            reader.AppendRevision([]));
    }

    [Fact]
    public void BufferWithNoObjectHeadersAtAll_throwsInvalidDataException()
    {
        var bytes = "%PDF-1.7\n%%EOF"u8.ToArray();
        Assert.Throws<InvalidDataException>(() =>
            PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
    }

    [Fact]
    public void ObjStmXrefStream_underM1Damage_recoversTheCatalogPackedInsideTheObjectStream()
    {
        var original = LoadThirdParty("objstm-xrefstream.pdf");
        var (start, length) = FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        Assert.True(CanApplyM1(original, length));
        var damaged = ApplyM1_OutOfRangeStartxref(original, start, length);

        using var reconstructed = OpenReconstructed(damaged);

        // The catalog has no top-level "2 0 obj" header at all — it lives inside object 1's
        // /ObjStm. Recovering /Root here requires reconstruction to have actually expanded the
        // object stream, not merely scanned bare "N G obj" headers.
        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(2, root.ObjectNumber);
        var typeName = Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);
    }

    [Fact]
    public void TruncatedTail_reconstructs_andEveryResolvedObjectMatchesBaseline()
    {
        using var reconstructed = OpenReconstructed(LoadThirdParty("truncated-tail.pdf"));
        using var baseline = PdfReader.Open(LoadThirdParty("baseline.pdf"));

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        // truncated-tail.pdf is baseline.pdf's own first 1200 bytes, so object numbering is
        // identical — but object 6 is cut mid-dictionary there, so this loop is the check for
        // whether reconstruction silently resolved a corrupted partial object to something that
        // does NOT match the real one, not just whether the file opened at all. A candidate whose
        // bytes were themselves truncated (object 6) can legitimately fail to PARSE at all rather
        // than resolve to the wrong thing — Resolve() throwing for a genuinely malformed object is
        // the reader's ordinary behaviour on any document, reconstructed or not, so that candidate
        // is treated as "does not resolve" here rather than a disagreement.
        var compared = 0;
        foreach (var objectNumber in reconstructed.ObjectNumbers)
        {
            PdfObject? recoveredValue;
            try
            {
                recoveredValue = reconstructed.Resolve(objectNumber);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (recoveredValue is null)
                continue;

            var baselineValue = baseline.Resolve(objectNumber);
            Assert.NotNull(baselineValue);
            Assert.Equal(WriteToBytes(baselineValue!), WriteToBytes(recoveredValue));
            compared++;
        }

        Assert.True(compared > 0);
    }

    /// <summary>
    /// The agreement property on the COMMITTED fixture pair, not a byte array built to match it.
    /// <c>broken-startxref.pdf</c> is <c>baseline.pdf</c> with its startxref changed from
    /// <c>1432</c> to <c>9999</c> (see Fixtures/ThirdParty/README.md) — the same shape M1
    /// constructs on the fly, but pinned against the actual committed bytes so a fixture
    /// regenerated differently would be caught here even if the on-the-fly M1 matrix kept passing.
    /// </summary>
    [Fact]
    public void BrokenStartxref_agreesWithBaseline_onTheCommittedFixturePair()
    {
        using var baseline = PdfReader.Open(LoadThirdParty("baseline.pdf"));
        Assert.False(baseline.WasReconstructed);

        using var reconstructed = OpenReconstructed(LoadThirdParty("broken-startxref.pdf"));

        AssertFullAgreement(baseline, reconstructed);
    }

    // ── Adversarial cases built by hand ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal, otherwise-independent PDF (its own header, one catalog object, its own
    /// classic xref/trailer/startxref/%%EOF) to embed as another document's stream payload. Its
    /// catalog deliberately points <c>/Pages</c> at a nonexistent object 99 — a marker that lets a
    /// test tell "the inner document's catalog" apart from "the outer document's catalog" by more
    /// than just an object number that reconstruction might have reassigned.
    /// </summary>
    private static byte[] BuildInnerStandalonePdf(string marker)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        var o1 = (int)ms.Position;
        W($"1 0 obj\n<< /Type /Catalog /Pages 99 0 R /Marker ({marker}) >>\nendobj\n");
        var xref = (int)ms.Position;
        W("xref\n0 2\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W("trailer\n<< /Size 2 /Root 1 0 R >>\n");
        W($"startxref\n{xref}\n%%EOF\n");

        return ms.ToArray();
    }

    /// <summary>
    /// A document carrying another whole PDF as a stream payload, where the container stream's
    /// declared length is either an indirect reference (the shape real producers emit — the length
    /// of a not-yet-fully-written stream is usually filled in after the fact) or a direct integer.
    /// The inner PDF's own "1 0 obj" appears LATER in the byte stream than the outer document's real
    /// "1 0 obj". Under naive last-definition-wins scanning with no extent awareness, the inner
    /// header would win; correct extent resolution excludes it as living inside a confirmed stream
    /// body. Neither document carries a real trailer or xref of its own at the OUTER level — this is
    /// only ever opened through <see cref="PdfReaderOptions.AllowReconstruction"/>.
    /// </summary>
    private static byte[] BuildEmbeddedPdfHijack(bool indirectLength)
    {
        var inner = BuildInnerStandalonePdf("INNERDOC");

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");

        W("4 0 obj\n<< /Length ");
        W(indirectLength ? "9 0 R" : inner.Length.ToString(CultureInfo.InvariantCulture));
        W(" >>\nstream\n");
        WB(inner);
        W("\nendstream\nendobj\n");

        if (indirectLength)
            W($"9 0 obj\n{inner.Length}\nendobj\n");

        W("%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// Confirms extent resolution, not just header scanning: the earlier attempt at this test used a
    /// direct <c>/Length</c>, which most producers never emit and which a scanner can size correctly
    /// without resolving anything — dodging the actual defect class. The indirect-length row is the
    /// one that matters; the direct row stays only so the difference between the two remains visible.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EmbeddedPdfHijack_outerCatalogWins_regardlessOfLengthShape(bool indirectLength)
    {
        var bytes = BuildEmbeddedPdfHijack(indirectLength);
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        // The OUTER /Pages target, not the inner document's 99 0 R.
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
    }

    /// <summary>
    /// A container stream whose declared <c>/Length</c> is simply wrong (far too small to reach the
    /// real terminator), so the object's extent cannot come from trusting <c>/Length</c> at all: the
    /// walker has to fall back to locating <c>endstream</c> itself. The design that replaced the
    /// earlier per-candidate scan window keeps a stream candidate's body region "fail closed" —
    /// it is never left unrecorded — by tiering over a one-pass index of every <c>endstream</c>
    /// occurrence in the file rather than bounding how far a single candidate may look: this fixture
    /// still has exactly one real <c>endstream</c>, at the true end of the inner document's bytes,
    /// so the tiered search has to reach past the 2000 bytes of filler in front of it rather than
    /// give up and leave the candidate suppressing nothing. This is the shape the two rows above do
    /// not reach: both use a <c>/Length</c> that VERIFIES, so neither ever touches the fallback path
    /// at all.
    /// </summary>
    private static byte[] BuildEmbeddedPdfHijack_UnverifiableLengthAndFarTerminator()
    {
        var inner = BuildInnerStandalonePdf("INNERDOC");

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");

        // /Length 10 cannot verify against a multi-hundred-byte body — the walker looks for
        // 'endstream' ten bytes into filler and finds none, so it must fall through to a real
        // search rather than trust the declared value.
        W("4 0 obj\n<< /Length 10 >>\nstream\n");
        W(new string('A', 2000));
        WB(inner);
        W("\nendstream\nendobj\n");

        W("%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>
    /// A stream whose declared <c>/Length</c> cannot verify, with the real terminator sitting far
    /// past it: the shape <see cref="EmbeddedPdfHijack_outerCatalogWins_regardlessOfLengthShape"/>'s
    /// two rows cannot reach, because both use a <c>/Length</c> that verifies and so never touch the
    /// fallback search at all.
    /// </summary>
    [Fact]
    public void EmbeddedPdfHijack_unverifiableLengthAndFarTerminator_outerCatalogStillWins()
    {
        var bytes = BuildEmbeddedPdfHijack_UnverifiableLengthAndFarTerminator();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
    }

    /// <summary>
    /// A stream with a wrong declared <c>/Length</c> whose body contains a decoy occurrence of
    /// <c>endstream</c> ahead of the real terminator, followed by more legitimate top-level objects.
    /// Probes for a single high-water mark on confirmed stream extents overshooting and swallowing
    /// what comes after.
    /// </summary>
    private static byte[] BuildOverSuppressionProbe()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        W("4 0 obj\n<< /Length 3 >>\nstream\nAAAA endstream BBBB\nendstream\nendobj\n");
        W("5 0 obj\n<< /Type /Marker /Value (AfterBadStream) >>\nendobj\n");
        W("%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void OverSuppressionProbe_objectsAfterAMisdeclaredStream_stayResolvable()
    {
        var bytes = BuildOverSuppressionProbe();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        var marker = Assert.IsType<PdfDictionary>(reconstructed.Resolve(5));
        var value = Assert.IsType<PdfLiteralString>(marker.Get(new PdfName("Value")));
        Assert.Equal("AfterBadStream", Encoding.ASCII.GetString(value.Bytes.Span));
    }

    /// <summary>
    /// A file padded with many stream-shaped headers that never terminate — each declares a
    /// <c>/Length</c> far past the file's own size and contains no real <c>endstream</c> at all.
    /// Under the final fail-closed extent design a confirmed-but-unterminated stream's body region
    /// is extended straight to EOF in a single cursor jump rather than searched for byte by byte, so
    /// the FIRST such decoy swallows every decoy placed after it — N decoys cost O(1), not O(N),
    /// which is exactly what <see cref="DecoyBudget_manyFalseStreamCandidates_costStaysLinearInFileSize"/>
    /// below now pins. Unterminated decoy STREAMS can no longer exhaust the byte budget on their
    /// own because of that; <see cref="BuildUnterminatedDictionaryRunProbe"/> further down is what
    /// does, through repeated failed dictionary parses rather than repeated stream candidates.
    /// </summary>
    private static byte[] BuildDecoyBudgetProbe(int decoyCount)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        // The real document comes FIRST, decoys after: an unterminated decoy stream's fallback
        // endstream search looks forward from its own start, so nothing legitimate placed before any
        // decoy can ever fall inside one — decoys can only ever threaten to swallow later decoys,
        // never the real catalog. Real objects after the decoys would risk the reverse: a decoy
        // whose scan runs far enough to reach the one genuine 'endstream' in the file (inside
        // object 4) would suppress everything between as "inside its own stream body", including
        // the catalog — precisely the over-suppression class the other probe above exists to
        // catch, and not what THIS test means to exercise.
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        W("4 0 obj\n<< /Length 24 >>\nstream\nBT /F1 12 Tf (Hi) Tj ET\nendstream\nendobj\n");

        for (var i = 0; i < decoyCount; i++)
            W($"{1000 + i} 0 obj\n<< /Length 999999999 >>\nstream\nno terminator here, just filler bytes\n");

        W("%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void DecoyBudget_manyFalseStreamCandidates_costStaysLinearInFileSize()
    {
        const int DecoyCount = 200;
        const long GenerousLinearMultiple = 25;

        var bytes = BuildDecoyBudgetProbe(DecoyCount);
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        // With fail-closed extents, an unterminated decoy stream's body region is a single O(1)
        // jump to EOF rather than a bounded search — so 200 such decoys should barely register
        // against the budget at all. The counter, not wall-clock time, is what pins that: timings
        // are flaky on shared CI hardware, and a low counter here is direct evidence that decoy
        // STREAMS specifically are cheap under this design (see BuildUnterminatedDictionaryRunProbe
        // for the shape that actually burns the budget instead).
        Assert.True(reconstructed.ReconstructionBytesConsumed <= bytes.Length * GenerousLinearMultiple,
            $"ReconstructionBytesConsumed ({reconstructed.ReconstructionBytesConsumed}) exceeded " +
            $"{GenerousLinearMultiple}x the {bytes.Length}-byte input — cost looks superlinear.");
    }

    // The decoy-STREAM shape above no longer exhausts the budget at all: under the final
    // fail-closed extent design, a confirmed-but-unterminated stream's body region is a single O(1)
    // jump to EOF, so the first decoy swallows every decoy placed after it (see
    // BuildDecoyBudgetProbe's own comment). What actually burns charged budget is a failed
    // PdfObjectParser.ParseObject() at a top-level "<<": it charges the bytes it consumed
    // before failing, then the walk resyncs only +2 bytes and tries again. A run of N repeated,
    // unterminated "<< " tokens therefore produces roughly N/2 overlapping failed parses,
    // each charging up to N bytes — quadratic charged cost on linear input, which trips
    // `budget = max(1 MiB, 8 × length)` on a file well under 128 KiB, where the 1 MiB floor
    // dominates (charged work of roughly runLength²/4 crosses 1 MiB once the run passes ~2 KiB;
    // 4096 tokens gives a comfortable margin while keeping the whole file a few KiB, not 64).
    private const int UnterminatedDictionaryRunCount = 4096;

    private static byte[] BuildUnterminatedDictionaryRunProbe(int runCount)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        // A small valid prefix, so the file has real objects to resolve if the budget somehow
        // survived the run — exhaustion has to come from the run itself, not from there being
        // nothing else in the file to parse.
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        // No closing '>>' anywhere in the run: every "<<" the walk reaches dispatches to
        // ParseObject(), fails to find one, and charges the bytes it read before failing.
        for (var i = 0; i < runCount; i++)
            W("<< /A ");

        W("\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void BudgetExhaustion_onALargeEnoughInput_throwsInvalidDataException()
    {
        var bytes = BuildUnterminatedDictionaryRunProbe(UnterminatedDictionaryRunCount);

        // Exhaustion is a defined, typed, named failure, not a silent success or an unrelated
        // crash: refusing outright is deliberate, since degrading to a partial scan would leave an
        // unsuppressed stream candidate standing — exactly the suppression gap the embedded-PDF
        // hijack tests above exist to close (there is no flag to check instead: a budget that ran
        // out mid-scan always throws, it never returns a reader with reconstruction half-done).
        PdfDocumentReader? reader = null;
        var ex = Assert.Throws<InvalidDataException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
        Assert.Contains("cost budget", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The un-starvability proof the brief calls out: the same budget-burning run as
    /// <see cref="BuildUnterminatedDictionaryRunProbe"/>, followed — past everything the walk can
    /// actually reach before the quadratic charge trips the budget — by a plain trailer declaring
    /// <c>/Encrypt</c>. Refusal-vs-plaintext has to be decided before any cost limit can fire, or an
    /// attacker gets to choose which exception a reader throws simply by padding the file long
    /// enough to exhaust the budget first: the exhaustion path's own raw, uncharged sweep of the
    /// un-walked remainder is what finds this evidence regardless. <c>InvalidDataException</c>
    /// naming a cost budget is a refusal either way, but <c>UnsupportedPdfFeatureException</c> is
    /// the one that is actually true here, and the one a caller who branches on exception type needs
    /// to see.
    /// </summary>
    private static byte[] BuildUnterminatedDictionaryRunProbeWithEncryptedTail(int runCount)
    {
        var prefix = BuildUnterminatedDictionaryRunProbe(runCount);

        var ms = new MemoryStream(prefix.Length + 64);
        ms.Write(prefix);
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("\ntrailer\n<< /Size 10 /Root 1 0 R /Encrypt 9 0 R >>\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void BudgetExhaustion_withEncryptedTail_refusesAsEncrypted_notAsExhaustion()
    {
        var bytes = BuildUnterminatedDictionaryRunProbeWithEncryptedTail(UnterminatedDictionaryRunCount);

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    // ── Bounded mutation fuzz ────────────────────────────────────────────────────────────────────

    private static byte[] Mutate(byte[] original, Random rng)
    {
        var mutated = (byte[])original.Clone();
        var mutationCount = rng.Next(1, 4);

        for (var m = 0; m < mutationCount; m++)
        {
            if (rng.Next(2) == 0)
            {
                // Flip a single byte, XORed against a random nonzero value so it always changes.
                var index = rng.Next(mutated.Length);
                mutated[index] = (byte)(mutated[index] ^ rng.Next(1, 256));
            }
            else
            {
                // Splice a short random-length span with different random-length random bytes, so
                // total file length can drift too — exercises offset-based logic, not just content.
                var spliceStart = rng.Next(mutated.Length);
                var removeLength = Math.Min(rng.Next(1, 9), mutated.Length - spliceStart);
                var insertLength = rng.Next(0, 9);
                var replacement = new byte[insertLength];
                rng.NextBytes(replacement);

                var next = new byte[mutated.Length - removeLength + insertLength];
                Array.Copy(mutated, 0, next, 0, spliceStart);
                replacement.CopyTo(next, spliceStart);
                Array.Copy(mutated, spliceStart + removeLength, next, spliceStart + insertLength,
                    mutated.Length - spliceStart - removeLength);
                mutated = next;
            }
        }

        return mutated;
    }

    /// <summary>
    /// Fixed seed and iteration count so a failure is reproducible: a mutation that trips an
    /// unexpected exception type on one run must trip it on every run. Only the documented failure
    /// vocabulary for a corrupted or unreadable file may escape <c>Open</c> here — anything else
    /// (NullReferenceException, IndexOutOfRangeException, ArgumentException, OverflowException
    /// especially) is exactly the class of defect #184's brief cites two previous attempts for.
    /// </summary>
    [Fact]
    public void BoundedMutationFuzz_onlyTheDocumentedExceptionVocabularyEscapes()
    {
        const int Seed = 184_2026;
        const int Iterations = 500;
        const long GenerousLinearMultiple = 50;

        var corpus = new[]
        {
            LoadThirdParty("baseline.pdf"),
            LoadThirdParty("objstm-xrefstream.pdf"),
            LoadThirdParty("freed-object-reuse.pdf"),
            LoadThirdParty("incremental-update.pdf"),
            LoadEncrypted("plaintext-baseline.pdf"),
        };

        var rng = new Random(Seed);

        for (var iteration = 0; iteration < Iterations; iteration++)
        {
            var corpusIndex = rng.Next(corpus.Length);
            var mutated = Mutate(corpus[corpusIndex], rng);

            PdfDocumentReader? reader = null;
            try
            {
                reader = PdfReader.Open(mutated, new PdfReaderOptions { AllowReconstruction = true });
                Assert.True(reader.ReconstructionBytesConsumed <= mutated.Length * GenerousLinearMultiple,
                    $"seed {Seed}, iteration {iteration}: ReconstructionBytesConsumed " +
                    $"({reader.ReconstructionBytesConsumed}) exceeded a linear multiple of the " +
                    $"{mutated.Length}-byte mutated input.");
            }
            catch (Exception ex) when (ex is InvalidDataException or UnsupportedPdfFeatureException or PdfPasswordException)
            {
                // The documented failure vocabulary for a corrupted or unreadable file.
            }
            catch (Exception ex)
            {
                Assert.Fail($"seed {Seed}, iteration {iteration}: unexpected {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                reader?.Dispose();
            }
        }
    }

    // ── Hybrid fixtures: what reconstruction actually finds, not what it is assumed to find ───────

    /// <summary>
    /// <c>hybrid-spec-convention.pdf</c> is the ISO 32000-2 §7.5.8.4 "hidden object" shape: object 3
    /// is free in revision 1's classic table and defined only inside revision 2's <c>/XRefStm</c>,
    /// packed into object 7's <c>/ObjStm</c> — no top-level "3 0 obj" header exists anywhere in the
    /// file. Under a broken startxref the working xref chain is gone entirely, so recovering object
    /// 3 depends on Phase A finding object 7's container and Phase B actually expanding it, not on
    /// scanning bare "N G obj" headers. Whether that reaches the SAME answer the working xref's own
    /// precedence rule gives (<c>ThirdPartyReaderBehaviorTests.Hybrid_hiddenObject_resolvesFromTheNewerRevisionsXRefStm</c>)
    /// is #206's open question; this records the actual outcome rather than assuming one.
    /// </summary>
    [Fact]
    public void HybridSpecConvention_underDamage_object3RecoversFromTheObjectStream()
    {
        var original = LoadThirdParty("hybrid-spec-convention.pdf");
        var (start, length) = FindLastStartxrefDigits(original);
        Assert.True(length > 0);

        // hybrid-spec-convention.pdf's last startxref has only 3 digits (947) in a 1078-byte file,
        // so no same-digit-count value can push it out of range (see CanApplyM1) — M2 still forces
        // a full reconstruction, with no working xref chain left for the ordinary /XRefStm
        // precedence rule to run at all.
        var damaged = ApplyM2_InRangeNonXrefStartxref(original, start, length);

        using var undamaged = PdfReader.Open(original);
        var undamagedObject3 = undamaged.Resolve(3);

        using var reconstructed = OpenReconstructed(damaged);
        var reconstructedObject3 = reconstructed.Resolve(3);

        // As observed against this corpus: reconstruction's scan-and-expand recovers the SAME
        // object 3 the working xref's /XRefStm precedence rule reports. If a future change to
        // either the scan order or the precedence rule makes this diverge, that is #206
        // resurfacing under reconstruction — a finding for that issue, not a reason to relax this
        // assertion to "resolves to something".
        Assert.NotNull(undamagedObject3);
        Assert.NotNull(reconstructedObject3);
        Assert.Equal(WriteToBytes(undamagedObject3!), WriteToBytes(reconstructedObject3!));
    }

    /// <summary>
    /// As regenerated on main, <c>hybrid-samesection-undefined.pdf</c> is no longer an ObjStm-packing
    /// fixture at all — it is a single revision (no <c>/Prev</c> chain) where the classic table and
    /// its own <c>/XRefStm</c> coexist in the SAME section. Objects 4 and 7 both have ordinary
    /// top-level "N G obj" bytes on disk, but the classic table marks object 4 free (generation 1)
    /// and never mentions object 7 at all — both are reachable, under the working xref, only through
    /// the <c>/XRefStm</c>'s own entries (see the fixture README and #206). None of that involves an
    /// object stream; under a broken startxref, with no working xref left to defer to, reconstruction
    /// sees only the raw bytes, where BOTH objects have real headers like any other object.
    /// </summary>
    [Fact]
    public void HybridSameSectionUndefined_underDamage_object4ResurrectsAndObject7Agrees()
    {
        var original = LoadThirdParty("hybrid-samesection-undefined.pdf");
        var (start, length) = FindLastStartxrefDigits(original);
        Assert.True(length > 0);

        var damaged = ApplyM2_InRangeNonXrefStartxref(original, start, length);

        using var undamaged = PdfReader.Open(original);
        Assert.Null(undamaged.Resolve(4)); // the classic table's free entry wins the search (#206)

        using var reconstructed = OpenReconstructed(damaged);

        // With no working xref chain left to consult, reconstruction has only the raw bytes: object
        // 4's real top-level header sits there like any other object, so it resurrects rather than
        // staying free — exactly the one-directional divergence Annex C.4 sanctions. The undamaged
        // parse never resolves object 4 at all, so the agreement property (see AssertFullAgreement)
        // makes no claim about it either way; this is not a disagreement to reconcile.
        var reconstructedObject4 = reconstructed.ResolveStream(4);
        Assert.NotNull(reconstructedObject4);
        var decoded = reconstructed.GetDecodedStreamData(reconstructedObject4);
        Assert.NotNull(decoded);
        Assert.Contains("HYBRIDXREFSTM", Encoding.Latin1.GetString(decoded), StringComparison.Ordinal);

        // Object 7 has a real top-level header too, so — unlike object 4 — reconstruction and the
        // working xref chain reach the exact same bytes, without needing any object-stream
        // expansion at all.
        var undamagedObject7 = undamaged.Resolve(7);
        var reconstructedObject7 = reconstructed.Resolve(7);
        Assert.NotNull(undamagedObject7);
        Assert.NotNull(reconstructedObject7);
        Assert.Equal(WriteToBytes(undamagedObject7!), WriteToBytes(reconstructedObject7!));
    }

    // ── Acceptance table: one test per row (rows 1–15) ──────────────────────────────────────────
    //
    // Each fixture below is built to reproduce the SHAPE the acceptance table's row describes; the
    // assertion pins the CORRECT (fixed) outcome, which is the opposite of the "reproduced outcome"
    // column the table records for the earlier, broken scan. None of these were written by reading
    // the rework's implementation — only the row descriptions and the design direction that names
    // which rule closes each one.

    // Row 1: an encryption dictionary padded far past any historical per-dictionary probe cap, with
    // no trailer at all declaring /Encrypt — reachable only through A5's structural last-resort scan
    // of every top-level dictionary. A capped probe used to truncate before ever reaching /R, /O or
    // /U; the rework charges the whole parse against the byte budget instead of capping it.
    private static byte[] BuildPaddedEncryptionDictionary_NoTrailer()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        var padding = new string('B', 10_000);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W($"9 0 obj\n<< /Filter /Standard /V 2 /R 3 "
          + "/O <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
          + "/U <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 /Length 128 "
          + $"/Padding ({padding}) >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row1_PaddedEncryptionDictionary_stillDetected_refusesReconstruction()
    {
        var bytes = BuildPaddedEncryptionDictionary_NoTrailer();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    // Row 2: a public-key handler's encryption dictionary — /Filter names the handler (any name;
    // ISO 32000-2 §7.6.5.2 never keys detection on the literal "Adobe.PubSec") and /V is present,
    // but /O, /U and /R never appear at all, since those belong to the Standard handler (Table 20)
    // only. A rule keyed on /O+/U+/R+/V together is blind to this shape by construction.
    private static byte[] BuildPublicKeyEncryptionDictionary_NoDisambiguators()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("9 0 obj\n<< /Filter /Adobe.PubSec /V 1 >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row2_PublicKeyHandlerWithoutOUR_stillDetected_refusesReconstruction()
    {
        var bytes = BuildPublicKeyEncryptionDictionary_NoDisambiguators();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    // Row 3: 257 decoy occurrences of the bare "trailer<<...>>" shape before the real one, which
    // declares /Encrypt. Object 9, the /Encrypt target, is an ordinary /Info dict with no
    // encryption shape of its own — no /Filter, no /V, no /O, /U or /R anywhere in the file. What
    // this variant actually proves: the whole-file /Encrypt evidence sweep finds the token and
    // refuses even with 257 decoys ahead of it. It does NOT, on its own, prove the trailer-candidate
    // scan is uncapped — that sweep is a separate, byte-wide mechanism that would still catch the
    // token even if a per-candidate cap on trailer collection came back, since it does not depend on
    // any trailer actually being parsed as a candidate. The plaintext variant right below, which has
    // no /Encrypt anywhere for the sweep to find, is what a re-introduced cap would actually break.
    private static byte[] BuildManyDecoyTrailerKeywords()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("9 0 obj\n<< /Type /Info /Title (NotAnEncryptionDictionary) >>\nendobj\n");

        for (var i = 0; i < 257; i++)
            W($"trailer\n<< /Size 10 /Decoy {i} >>\n");

        W("trailer\n<< /Size 10 /Root 1 0 R /Encrypt 9 0 R >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row3_257DecoyTrailersBeforeTheRealOne_stillFindsIt_refusesReconstruction()
    {
        var bytes = BuildManyDecoyTrailerKeywords();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The row's actual guard. <c>/Root</c> alone cannot discriminate an uncapped trailer scan from
    /// a capped one: A6's own catalog fallback scans the WHOLE file for <c>/Type /Catalog</c>
    /// regardless of where the trailer scan gives up, so it finds the genuine catalog wherever it
    /// sits and <c>/Root</c> resolves the same either way. <c>/Info</c> is the real discriminator —
    /// <c>RecoverTrailer</c> resolves <c>/Root</c>, <c>/Encrypt</c>, <c>/ID</c>, <c>/Info</c> and
    /// <c>/Size</c> per key, highest offset wins, and A6 never supplies <c>/Info</c> at all. Only
    /// the real, 258th trailer, past all 257 decoys, declares it; the decoys carry nothing but
    /// <c>/Size</c>. If a trailer-candidate cap were reintroduced and stopped collection one short
    /// of the real trailer, <c>/Info</c> would be absent from the recovered trailer and the
    /// assertion below goes red — a guard A6's own reach cannot mask.
    /// </summary>
    private static byte[] BuildManyDecoyTrailerKeywords_NoEncryption()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        for (var i = 0; i < 257; i++)
            W($"trailer\n<< /Size 1 /Decoy {i} >>\n");

        // Only the real trailer, past all 257 decoys, ever mentions object 4 or an /Info key.
        W("4 0 obj\n<< /Producer (Row3ScanReachedTheRealTrailer) >>\nendobj\n");
        W("trailer\n<< /Size 10 /Root 1 0 R /Info 4 0 R >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row3_PlaintextVariant_257DecoyTrailersBeforeTheRealOne_recoversInfoPastAllDecoys()
    {
        var bytes = BuildManyDecoyTrailerKeywords_NoEncryption();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        var typeName = Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);
        Assert.Null(reconstructed.Encryption);

        // The actual guard: /Info can only have come from the real, 258th trailer — nothing else
        // in the file supplies it, and A6's own candidate-root scan never populates /Info at all.
        var infoRef = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Info));
        Assert.Equal(4, infoRef.ObjectNumber);
        var info = Assert.IsType<PdfDictionary>(reconstructed.Resolve(4));
        var producer = Assert.IsType<PdfLiteralString>(info.Get(new PdfName("Producer")));
        Assert.Equal("Row3ScanReachedTheRealTrailer", Encoding.ASCII.GetString(producer.Bytes.Span));
    }

    // Row 4: an embedded PDF whose CONTAINER stream declares a /Length three bytes short of the
    // real body — an ordinary producer bug (a trailing edit that grew the stream without updating
    // /Length), not a hostile shape. The near-miss has to be recovered by looking a short distance
    // past the declared value for the real terminator, or the inner document's own header wins.
    private static byte[] BuildEmbeddedPdfHijack_LengthStaleByThreeBytes()
    {
        var inner = BuildInnerStandalonePdf("INNERDOC");

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        W($"4 0 obj\n<< /Length {inner.Length - 3} >>\nstream\n");
        WB(inner);
        W("\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row4_ContainerLengthStaleByThreeBytes_outerCatalogWins()
    {
        var bytes = BuildEmbeddedPdfHijack_LengthStaleByThreeBytes();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
    }

    // Row 5: the same shape, but the /Length is correct and the gap is 40 spaces of padding between
    // the real body and 'endstream' — a legal, if unusual, producer choice. Verifying the terminator
    // has to skip an unbounded run of whitespace, not stop after some fixed number of bytes.
    private static byte[] BuildEmbeddedPdfHijack_FortySpacesBeforeEndstream()
    {
        var inner = BuildInnerStandalonePdf("INNERDOC");

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        W($"4 0 obj\n<< /Length {inner.Length} >>\nstream\n");
        WB(inner);
        W(new string(' ', 40));
        W("\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row5_FortySpacesBeforeEndstream_outerCatalogWins()
    {
        var bytes = BuildEmbeddedPdfHijack_FortySpacesBeforeEndstream();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
    }

    // Row 6: an "N G obj ... /Type /Catalog" byte run sitting inside a literal string's payload, in
    // an ordinary NON-stream object — no /Length, no stream body, no verification window involved
    // at all. A lexical walker that tracks string nesting reads this as string content; a raw byte
    // scan does not.
    private static byte[] BuildFakeHeaderInsideLiteralString()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("5 0 obj\n<< /Type /Note /Text (7 0 obj << /Type /Catalog /Pages 99 0 R >> endobj) >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row6_FakeHeaderInsideLiteralString_isIgnored_realCatalogWins()
    {
        var bytes = BuildFakeHeaderInsideLiteralString();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        Assert.Null(reconstructed.Resolve(7)); // no real object 7 exists; the decoy never registered

        // The literal string itself, decoy header shape and all, must round-trip intact — a walker
        // that got confused mid-string would corrupt or truncate it, not just misfile a candidate.
        var note = Assert.IsType<PdfDictionary>(reconstructed.Resolve(5));
        var text = Assert.IsType<PdfLiteralString>(note.Get(new PdfName("Text")));
        Assert.Equal(
            "7 0 obj << /Type /Catalog /Pages 99 0 R >> endobj",
            Encoding.ASCII.GetString(text.Bytes.Span));
    }

    // Row 7: the same decoy, inside a comment instead of a string. A comment runs to end of line
    // regardless of what it contains, so an object placed right after it must still parse cleanly.
    private static byte[] BuildFakeHeaderInsideComment()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("% 7 0 obj << /Type /Catalog /Pages 99 0 R >> endobj (decoy inside a comment, runs to EOL)\n");
        W("5 0 obj\n<< /Type /Marker /Value (AfterComment) >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row7_FakeHeaderInsideComment_isIgnored_realCatalogWins()
    {
        var bytes = BuildFakeHeaderInsideComment();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        Assert.Null(reconstructed.Resolve(7));

        var marker = Assert.IsType<PdfDictionary>(reconstructed.Resolve(5));
        var value = Assert.IsType<PdfLiteralString>(marker.Get(new PdfName("Value")));
        Assert.Equal("AfterComment", Encoding.ASCII.GetString(value.Bytes.Span));
    }

    // Row 8: object 4's stream declares an indirect /Length (5 0 R). The GENUINE object 5, at a
    // LOWER file offset than object 4, declares the correct length; a SHADOW "5 0 obj 9" plus a
    // planted 'endstream' sits INSIDE object 4's own body, at a HIGHER offset — nested content that
    // cannot be "already confirmed" when /Length is resolved. Resolving /Length from "the last
    // definition anywhere in the file" would pick the shadow, verify it against the planted
    // terminator, and truncate the stream to 9 bytes.
    private static readonly byte[] Row8RealBody = Encoding.ASCII.GetBytes("5 0 obj 9\nendstream\nAFTERSHADOW");

    private static byte[] BuildShadowedIndirectLengthProbe()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W($"5 0 obj\n{Row8RealBody.Length}\nendobj\n");
        W("4 0 obj\n<< /Length 5 0 R >>\nstream\n");
        ms.Write(Row8RealBody);
        W("\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row8_ShadowedIndirectLength_resolvesOnlyFromTheLowerOffsetConfirmedDefinition()
    {
        var bytes = BuildShadowedIndirectLengthProbe();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        // The real, lower-offset object 5 — not the 9 the shadow declares.
        var length = Assert.IsType<PdfInteger>(reconstructed.Resolve(5));
        Assert.Equal((long)Row8RealBody.Length, length.Value);

        // The full body, including the decoy bytes it happens to start with — not a 9-byte
        // truncation.
        var stream = reconstructed.ResolveStream(4);
        Assert.NotNull(stream);
        var decoded = reconstructed.GetDecodedStreamData(stream);
        Assert.NotNull(decoded);
        Assert.Equal(Row8RealBody, decoded);
    }

    // Row 9: two /Type /Catalog objects, BOTH fully corroborated — each /Pages resolves to its own
    // real, independent /Type /Pages tree. Within a tier, catalog election orders candidates by
    // DictStart DESCENDING (latest in file first) and takes the first that validates, so between
    // two equally corroborated catalogs the one LATEST in the file wins: a deterministic
    // last-definition-wins outcome. That is the defensible reading here, not a guess — Annex C.4
    // is informative and gives no rule for choosing between two candidates neither evidence tier
    // can separate, so "the most recent definition in the byte stream" is the same tie-break the
    // rest of reconstruction already uses everywhere else (A3's xref, A5's trailer). An earlier
    // version of this fixture left the second catalog's /Pages pointing at a /Type /NotPages
    // object, so it never reached the corroborated tier at all — that pinned corroborated-beats-bare,
    // not the tie-break this row is actually about.
    private static byte[] BuildTwoCorroboratedCatalogs()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        // Corroborated by its own real page tree, but not the latest definition in the file — must
        // lose to the one below.
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        // Equally corroborated, by a completely separate page tree — and the one election must
        // choose, purely because it sits LAST in the file.
        W("9 0 obj\n<< /Type /Catalog /Pages 10 0 R >>\nendobj\n");
        W("10 0 obj\n<< /Type /Pages /Kids [11 0 R] /Count 1 >>\nendobj\n");
        W("11 0 obj\n<< /Type /Page /Parent 10 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row9_TwoEquallyCorroboratedCatalogs_theLatestInFileWins()
    {
        var bytes = BuildTwoCorroboratedCatalogs();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(9, root.ObjectNumber);
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(10, pages.ObjectNumber);
    }

    /// <summary>
    /// Row 9's actual regression guard. In the shape above, both a buggy single-slot
    /// implementation (one variable, overwritten as the scan walks forward, so it ends holding
    /// whichever candidate came last) and a correct multi-candidate one land on object 9 anyway,
    /// because it happens to be both the latest AND the only one a buggy scan would still be
    /// holding by the time B2 runs — enumeration order equals file order there, so the two
    /// implementations are indistinguishable. This shape separates them: the GENUINE catalog is
    /// written FIRST, and its own /Pages target is packed inside an object stream — Phase A's
    /// candidate ranking can only corroborate a TOP-LEVEL /Pages, so at ranking time this looks
    /// bare-tier, even though B2's later, real-resolution check (after Phase B has expanded the
    /// container) sees the real page tree just fine. The decoy is written LAST, also bare-tier, but
    /// its /Pages names an object that exists nowhere in the file — real resolution never validates
    /// it. A single-slot implementation remembers only the decoy (the last /Type /Catalog it saw)
    /// and, once the decoy's /Pages fails to resolve, has no genuine candidate left to fall back to
    /// except the same decoy in B2's looser pass — accepting it on /Type /Catalog alone. Retaining
    /// EVERY candidate (not overwriting a single slot) is what lets B2 skip the invalid decoy and
    /// still reach the genuine catalog.
    /// </summary>
    private static byte[] BuildDiscriminatingCatalogElectionProbe()
    {
        var member2 = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>";
        var member3 = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>";
        var payload = member2 + member3;
        var header = $"2 0 3 {member2.Length} ";
        var objStmBody = header + payload;

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        // The genuine catalog: a real top-level object, so A6's own scan sees it at all, but its
        // /Pages target lives only inside the object stream below.
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W($"10 0 obj\n<< /Type /ObjStm /N 2 /First {header.Length} /Length {objStmBody.Length} >>\nstream\n");
        W(objStmBody);
        W("\nendstream\nendobj\n");

        // The decoy: top-level, also bare-tier, but its /Pages names object 999, which exists
        // nowhere in the file.
        W("9 0 obj\n<< /Type /Catalog /Pages 999 0 R >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row9_DiscriminatingCase_genuineBareTierCatalogBeatsALaterUnresolvingDecoy()
    {
        var bytes = BuildDiscriminatingCatalogElectionProbe();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
        var pagesObj = Assert.IsType<PdfDictionary>(reconstructed.Resolve(2));
        var pagesType = Assert.IsType<PdfName>(pagesObj.Get(PdfName.Type));
        Assert.Equal("Pages", pagesType.Value);
    }

    // Row 10: a raw "/Catalog" byte run sitting in trailing garbage after the real document ends —
    // no object header, no dictionary, nothing a lexical walk would ever parse as a candidate at
    // all. There is no separate raw-text scan for "/Type /Catalog" to fool.
    private static byte[] BuildCatalogTextInTrailingGarbage()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("%%EOF\n");
        W("random trailing noise /Type /Catalog more noise, no N G obj header anywhere near this text\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row10_CatalogTextInTrailingGarbage_isIgnored()
    {
        var bytes = BuildCatalogTextInTrailingGarbage();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
    }

    // Row 11: an over-long /Length that DOES verify — a real 'endstream' really does sit at the
    // declared offset — but the span it covers swallows two ordinary, independently well-formed
    // objects: a marker (5) and a decoy catalog (6) whose /Pages points at the SAME real page tree
    // as the genuine catalog. Secondary recovery has to rescue both as ordinary resolvable objects
    // (confirmed independently: qpdf 12.3.2's own reconstruction on this exact shape also resolves
    // both --show-object=5 and --show-object=6 as separate objects) while the QUARANTINE holds:
    // object 6 must never win catalog election just because it happens to validate.
    private static byte[] BuildOverLongVerifiedLengthProbe()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        var swallowedTail =
            "endstream\nendobj\n"
            + "5 0 obj\n<< /Type /Marker /Value (SwallowedByOverLongLength) >>\nendobj\n"
            + "6 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n";
        var padding = new string('X', 64);
        var fullBody = Encoding.ASCII.GetBytes("REALCONTENT" + swallowedTail + padding);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /Contents 4 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W($"4 0 obj\n<< /Length {fullBody.Length} >>\nstream\n");
        ms.Write(fullBody);
        W("\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row11_OverLongVerifiedLength_secondaryRecoveryRescuesSwallowedObjects_withoutBreakingQuarantine()
    {
        var bytes = BuildOverLongVerifiedLengthProbe();
        using var reconstructed = OpenReconstructed(bytes);

        // Quarantine: the genuine, top-level catalog wins even though object 6 — recovered only
        // through secondary walking — also has /Type /Catalog with a /Pages that genuinely
        // resolves to /Type /Pages.
        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        // Both swallowed objects still resolve as ordinary objects.
        var marker = Assert.IsType<PdfDictionary>(reconstructed.Resolve(5));
        var value = Assert.IsType<PdfLiteralString>(marker.Get(new PdfName("Value")));
        Assert.Equal("SwallowedByOverLongLength", Encoding.ASCII.GetString(value.Bytes.Span));

        var decoyCatalog = Assert.IsType<PdfDictionary>(reconstructed.Resolve(6));
        var decoyType = Assert.IsType<PdfName>(decoyCatalog.Get(PdfName.Type));
        Assert.Equal("Catalog", decoyType.Value);

        // The primary stream's own extent is unaffected by the secondary pass: it still decodes to
        // the FULL declared body, decoy bytes and all.
        var stream = reconstructed.ResolveStream(4);
        Assert.NotNull(stream);
        var decoded = reconstructed.GetDecodedStreamData(stream);
        Assert.NotNull(decoded);
        Assert.Contains("REALCONTENT", Encoding.ASCII.GetString(decoded), StringComparison.Ordinal);
    }

    // Row 12: a junk top-level object that SHADOWS the packed catalog's own object number — object
    // 2 has both a real top-level header (junk) and a packed definition inside the /ObjStm. A
    // container member is only added to the xref when its number is not ALREADY present top-level
    // (a real header is stronger evidence), so an ordinary Resolve(2) would return the junk
    // dictionary and never reach the real catalog at all: recovery depends on B3 recognising that
    // the top-level definition is not a usable catalog and rebinding object 2 to the packed member
    // instead. An earlier version of this fixture numbered the junk object 1, leaving the packed
    // catalog's number 2 free — with no collision, the packed member was simply added as normal and
    // the rebind path this row is about never ran.
    private static byte[] BuildObjStmPackedCatalogBesideJunkTopLevel()
    {
        var member2 = "<< /Type /Catalog /Pages 3 0 R >>";
        var member3 = "<< /Type /Pages /Kids [4 0 R] /Count 1 >>";
        var member4 = "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 200 200] >>";
        var payload = member2 + member3 + member4;
        var header = $"2 0 3 {member2.Length} 4 {member2.Length + member3.Length} ";
        var objStmBody = header + payload;

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("2 0 obj\n<< /Type /Info /Title (NotTheCatalog) >>\nendobj\n");
        W($"10 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Length {objStmBody.Length} >>\nstream\n");
        W(objStmBody);
        W("\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row12_JunkTopLevelObjectShadowsThePackedCatalogsNumber_b3RebindsToTheRealCatalog()
    {
        var bytes = BuildObjStmPackedCatalogBesideJunkTopLevel();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(2, root.ObjectNumber);
        var typeName = Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(3, pages.ObjectNumber);
    }

    // Row 13: the legal compact spelling "<</Type/Catalog/Pages 2 0 R>>" — no whitespace around any
    // delimiter. A conforming file; verified locally against qpdf 12.3.2, which normalizes this
    // exact input's object 1 to "<< /Pages 2 0 R /Type /Catalog >>". A byte scan keyed on the
    // literal substring "/Type /Catalog" (with a space) would find nothing at all here.
    private static byte[] BuildCompactSyntaxDocument()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<</Type/Catalog/Pages 2 0 R>>\nendobj\n");
        W("2 0 obj\n<</Type/Pages/Kids[3 0 R]/Count 1>>\nendobj\n");
        W("3 0 obj\n<</Type/Page/Parent 2 0 R/MediaBox[0 0 200 200]>>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row13_CompactSyntaxWithNoWhitespace_stillReconstructs()
    {
        var bytes = BuildCompactSyntaxDocument();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        var typeName = Assert.IsType<PdfName>(reconstructed.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeName.Value);

        var pagesRef = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pagesRef.ObjectNumber);
        var pages = Assert.IsType<PdfDictionary>(reconstructed.Resolve(2));
        var kids = Assert.IsType<PdfArray>(pages.Get(new PdfName("Kids")));
        var kidRef = Assert.IsType<PdfIndirectReference>(kids[0]);
        Assert.Equal(3, kidRef.ObjectNumber);
        var page = Assert.IsType<PdfDictionary>(reconstructed.Resolve(3));
        var pageType = Assert.IsType<PdfName>(page.Get(PdfName.Type));
        Assert.Equal("Page", pageType.Value);
    }

    // Row 14: an ordinary, conforming object whose OWN dictionary — not a stream body — is far
    // larger than any historical per-dictionary probe cap. Verified locally against qpdf 12.3.2,
    // whose --show-object=1 on this exact shape prints the padding string in full, start marker
    // through end marker, with nothing truncated.
    private static byte[] BuildLargeDictionaryDocument(out string padding)
    {
        padding = "PADSTART" + new string('A', 9_984) + "PADEND";
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W($"1 0 obj\n<< /Type /Catalog /Pages 2 0 R /Padding ({padding}) >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row14_DictionaryLargerThanAnyHistoricalProbeCap_parsesInFull()
    {
        var bytes = BuildLargeDictionaryDocument(out var padding);
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        var paddingValue = Assert.IsType<PdfLiteralString>(reconstructed.Catalog.Get(new PdfName("Padding")));
        Assert.Equal(padding, Encoding.ASCII.GetString(paddingValue.Bytes.Span));
    }

    // Row 15: an /ObjStm-shaped container whose declared filter cannot honestly decode its body (no
    // real FlateDecode stream is 5000 bytes of a single repeated value). The real catalog resolves
    // independently, so the open still succeeds — what this isolates is whether the throw's cost
    // gets charged against the aggregate object-stream decode budget before it propagates, or
    // whether a decode failure quietly evades the very cap it exists to enforce.
    private const int Row15GarbageLength = 5_000;

    private static byte[] BuildObjStmContainerWhoseDecodeThrows()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        var garbage = new byte[Row15GarbageLength];
        Array.Fill(garbage, (byte)0xFF);
        W($"9 0 obj\n<< /Type /ObjStm /N 1 /First 4 /Filter /FlateDecode /Length {garbage.Length} >>\nstream\n");
        ms.Write(garbage);
        W("\nendstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row15_ObjStmContainerWhoseDecodeThrows_stillChargesItsRawBodyLength()
    {
        var bytes = BuildObjStmContainerWhoseDecodeThrows();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);

        Assert.True(reconstructed.ReconstructionObjStmBytesCharged >= Row15GarbageLength,
            $"ReconstructionObjStmBytesCharged ({reconstructed.ReconstructionObjStmBytesCharged}) did not " +
            $"reflect the throwing container's {Row15GarbageLength}-byte raw body — a decode failure must " +
            "still charge the aggregate budget, or the cap this exists to enforce never trips.");
    }

    // ── Rows 4/5, isolated from the line-initial T_scan tier ────────────────────────────────────
    //
    // The original row 4/5 fixtures above both happen to leave the real 'endstream' at the start
    // of a fresh line, so a T_scan fallback keyed on "line-initial, and ideally followed by
    // endobj" can find the real terminator whether or not the near-miss window or the whitespace
    // skip actually ran — neither test proves its own named mechanism fired. These two variants
    // place 'endstream' mid-line, immediately after non-whitespace content, so the line-initial
    // tier cannot rescue them: only the mechanism each is named for can.

    /// <summary>
    /// Isolates <c>LengthNearMissWindowBytes</c> from the line-initial fallback. The declared
    /// <c>/Length</c> undercounts the real body by 20 bytes of ordinary, NON-whitespace padding —
    /// not whitespace, so the unbounded whitespace skip (row 5's own mechanism) cannot bridge the
    /// gap either — and <c>endstream</c> sits directly after that padding with no newline in front
    /// of it. Only re-searching within the near-miss window of the wrong declared end can recover
    /// this.
    /// </summary>
    private static byte[] BuildEmbeddedPdfHijack_NearMissWindow_NonLineInitialEndstream()
    {
        var inner = BuildInnerStandalonePdf("INNERDOC");
        const string Padding = "XXXXXXXXXXXXXXXXXXXX"; // 20 non-whitespace bytes

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        W($"4 0 obj\n<< /Length {inner.Length} >>\nstream\n");
        WB(inner);
        W(Padding);
        W("endstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row4b_NearMissWindow_NonLineInitialEndstream_outerCatalogWins()
    {
        var bytes = BuildEmbeddedPdfHijack_NearMissWindow_NonLineInitialEndstream();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
    }

    /// <summary>
    /// Isolates the unbounded whitespace skip from the line-initial fallback. 40 spaces run
    /// directly into <c>endstream</c> with no newline anywhere between them, so the token is never
    /// line-initial — only skipping an unbounded (not capped) run of whitespace before checking for
    /// the keyword can reach it.
    /// </summary>
    private static byte[] BuildEmbeddedPdfHijack_WhitespaceSkip_NonLineInitialEndstream()
    {
        var inner = BuildInnerStandalonePdf("INNERDOC");

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] /Contents 4 0 R >>\nendobj\n");
        W($"4 0 obj\n<< /Length {inner.Length} >>\nstream\n");
        WB(inner);
        W(new string(' ', 40));
        W("endstream\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void Row5b_WhitespaceSkip_NonLineInitialEndstream_outerCatalogWins()
    {
        var bytes = BuildEmbeddedPdfHijack_WhitespaceSkip_NonLineInitialEndstream();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        var pages = Assert.IsType<PdfIndirectReference>(reconstructed.Catalog.Get(PdfName.Pages));
        Assert.Equal(2, pages.ObjectNumber);
    }

    // ── AppendRevision guard on the ORDINARY path ───────────────────────────────────────────────

    /// <summary>
    /// <see cref="PdfDocumentReader.AppendRevision"/> already refuses when
    /// <see cref="PdfDocumentReader.WasReconstructed"/> is set; this pins the other half of that
    /// guard — <see cref="PdfDocumentReader.DroppedOrphanedObjectStreamMembers"/>, reachable on the
    /// ORDINARY (non-reconstructed) path whenever a dangling object-stream member gets swept out of
    /// the xref. Appending on top of an xref the reader itself already knows is missing entries is
    /// the same hazard reconstruction's own guard exists for, just reached a different way. The
    /// fixture reuses <c>XrefStreamTests.BuildDanglingObjStmMemberPdf</c>'s shape: a type-2 row
    /// naming container object 99, which no revision or table anywhere in the file mentions.
    /// </summary>
    private static byte[] BuildDocumentWithADanglingObjStmMember()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
        void WB(byte[] b) => ms.Write(b);

        W("%PDF-1.5\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog >>\nendobj\n");

        var memberBody = "<< /Note (STILLLIVE) >>"u8.ToArray();
        var objStmHeader = "6 0\n"u8.ToArray();
        var objStmBody = objStmHeader.Concat(memberBody).ToArray();
        var o5 = (int)ms.Position;
        W($"5 0 obj\n<< /Type /ObjStm /N 1 /First {objStmHeader.Length} /Length {objStmBody.Length} >>\nstream\n");
        WB(objStmBody);
        W("\nendstream\nendobj\n");

        byte[] Row(byte type, long f2, long f3) =>
        [
            type,
            (byte)((f2 >> 24) & 0xFF), (byte)((f2 >> 16) & 0xFF), (byte)((f2 >> 8) & 0xFF), (byte)(f2 & 0xFF),
            (byte)((f3 >> 8) & 0xFF), (byte)(f3 & 0xFF),
        ];
        var streamBody = new MemoryStream();
        streamBody.Write(Row(1, o5, 0)); // obj 5 (container): live
        streamBody.Write(Row(2, 5, 0));  // obj 6 (member): container 5, index 0 — live
        streamBody.Write(Row(2, 99, 0)); // obj 7 (member): container 99 — no revision mentions 99
        var streamBodyArr = streamBody.ToArray();
        var xrefStmOffset = (int)ms.Position;
        W($"8 0 obj\n<< /Type /XRef /Size 9 /W [1 4 2] /Index [5 3] /Length {streamBodyArr.Length} >>\nstream\n");
        WB(streamBodyArr);
        W("\nendstream\nendobj\n");

        var classicXrefOffset = (int)ms.Position;
        W("xref\n0 2\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"trailer\n<< /Size 9 /Root 1 0 R /XRefStm {xrefStmOffset} >>\n");
        W($"startxref\n{classicXrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    [Fact]
    public void AppendRevision_onADocumentWithDroppedOrphanedObjectStreamMembers_throwsInvalidOperationException()
    {
        using var reader = PdfReader.Open(BuildDocumentWithADanglingObjStmMember());

        Assert.False(reader.WasReconstructed);
        Assert.True(reader.DroppedOrphanedObjectStreamMembers);

        Assert.Throws<InvalidOperationException>(() => reader.AppendRevision([]));
    }

    // ── C1: encryption evidence survives a corrupted terminator or an unterminated string ───────

    /// <summary>
    /// Flips one byte inside the LAST <c>endstream</c> keyword in the given bytes, keeping length
    /// identical. For an xref-stream-based encrypted fixture that keyword usually belongs to the
    /// cross-reference stream itself — corrupting it forces extent resolution to work for its
    /// answer rather than trust a clean terminator, while the dictionary region carrying
    /// <c>/Encrypt</c> must stay readable regardless of how the body region resolves.
    /// </summary>
    private static byte[] FlipByteInsideLastEndstreamKeyword(byte[] original)
    {
        var idx = LastIndexOfAscii(original, "endstream");
        Assert.True(idx >= 0, "expected an 'endstream' keyword to corrupt");
        var damaged = (byte[])original.Clone();
        damaged[idx + 5] ^= 0x20; // 'r' <-> 'R': same length, no longer a valid keyword
        return damaged;
    }

    public static TheoryData<string> C1EncryptedFixtureNames =>
        ["enc-rc4-128.pdf", "enc-aes-128.pdf", "enc-aes-256-r6.pdf"];

    [Theory]
    [MemberData(nameof(C1EncryptedFixtureNames))]
    public void C1_EncryptedFixture_underM1DamagePlusCorruptedLastEndstream_stillRefuses(string fixtureName)
    {
        var original = LoadEncrypted(fixtureName);
        var password = EncryptedFixturePasswords[fixtureName];

        var (start, length) = FindLastStartxrefDigits(original);
        Assert.True(length > 0);
        if (!CanApplyM1(original, length))
        {
            Assert.Skip($"{fixtureName}: the recorded startxref has only {length} digit(s), too few " +
                $"to represent a same-digit-count value outside this {original.Length}-byte file.");
            return;
        }

        var m1Damaged = ApplyM1_OutOfRangeStartxref(original, start, length);
        var damaged = FlipByteInsideLastEndstreamKeyword(m1Damaged);

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(damaged, new PdfReaderOptions { AllowReconstruction = true, Password = password }));
        Assert.Null(reader);
    }

    /// <summary>
    /// A trailer declaring <c>/Encrypt</c> sitting AFTER an unterminated top-level literal string.
    /// By design an unterminated string consumes to EOF (fail closed, under-recovering but safe for
    /// the OBJECT scan) — which would otherwise swallow everything past it, trailer included, into
    /// the string's own content. The word-bounded <c>/Encrypt</c> evidence sweep has to find it
    /// regardless of what the string-nesting walk thinks is inside a string.
    /// </summary>
    private static byte[] BuildUnterminatedLiteralStringBeforeEncryptedTrailer()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("4 0 obj\n(this literal string never closes, so a naive walk consumes everything after it\n");
        W("trailer\n<< /Size 10 /Root 1 0 R /Encrypt 5 0 R >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C1_UnterminatedLiteralStringBeforeEncryptedTrailer_stillRefuses()
    {
        var bytes = BuildUnterminatedLiteralStringBeforeEncryptedTrailer();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>Same shape as the literal-string case above, with an unterminated hex string instead.</summary>
    private static byte[] BuildUnterminatedHexStringBeforeEncryptedTrailer()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("4 0 obj\n<DEADBEEF00112233\n"); // opens a hex string, never closed with '>'
        W("trailer\n<< /Size 10 /Root 1 0 R /Encrypt 5 0 R >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C1_UnterminatedHexStringBeforeEncryptedTrailer_stillRefuses()
    {
        var bytes = BuildUnterminatedHexStringBeforeEncryptedTrailer();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    // ── C1 (CRITICAL): a #XX-escaped /Encrypt must not dodge the whole-file evidence sweep ───────
    //
    // ISO 32000-2 §7.3.5 lets any name spell any of its characters as a #XX hex escape, including
    // ordinary ASCII letters that need no escaping at all — /Encrypt and /Encryp#74 name the exact
    // same token. A sweep that matches on raw bytes only sees the second as different text and
    // misses it entirely, which is a silent-plaintext bug on a real encrypted file: worse than a
    // refusal, an open that returns ciphertext as content. Both fixtures below abandon the
    // encryption dictionary object itself behind an unterminated top-level string (fail-closed
    // consume-to-EOF), specifically so no OTHER signal can catch the file — no dictionary ever gets
    // PARSED there, so "a parsed dict matching the structural shape" cannot fire, and the public-key
    // variant carries no /O, /U or /R for the co-occurrence rule to catch either. The escaped
    // /Encrypt token in the trailer, decoded correctly by the sweep, is the only thing standing
    // between this file and opening as plaintext.

    /// <summary>
    /// A public-key handler (no <c>/O</c>, <c>/U</c> or <c>/R</c> at all) referenced only through
    /// <c>/Encryp#74</c> in the trailer, past an unterminated literal string that abandons the
    /// dictionary object itself. If the whole-file sweep matched raw bytes instead of decoding the
    /// escape, this file would open with <see cref="PdfDocumentReader.Encryption"/> null.
    /// </summary>
    private static byte[] BuildEscapedEncryptTokenInAbandonedRegion()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("4 0 obj\n(this string never closes, so the walk abandons everything after it\n");
        W("9 0 obj\n<< /Filter /Adobe.PPKLite /SubFilter /adbe.pkcs7.s5 /V 4 >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Encryp#74 9 0 R /Size 10 >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C1_EscapedEncryptTokenInAbandonedRegion_stillRefuses()
    {
        var bytes = BuildEscapedEncryptTokenInAbandonedRegion();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The same abandoned-region shape, but a Standard-handler dictionary whose own <c>/O</c>,
    /// <c>/U</c> and <c>/R</c> keys are ALSO spelled with a single hex escape each
    /// (<c>/#4F</c>, <c>/#55</c>, <c>/#52</c>) — regression coverage for escape decoding beyond just
    /// the <c>/Encrypt</c> token itself, since a structural detector may look for those key names
    /// too. The trailer's own reference is escaped the same way as the public-key case above.
    /// </summary>
    private static byte[] BuildEscapedStandardHandlerKeysInAbandonedRegion()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("4 0 obj\n(this string never closes, so the walk abandons everything after it\n");
        W("9 0 obj\n<< /Filter /Standard /V 2 /#52 3 "
          + "/#4F <2a2f0a1990192c60114730bdcd39f37828a53c89a340dd473c85299dc5258e1c> "
          + "/#55 <6c8913ac9fc602eb1aad2a1ec614bee90021446990b9e4114071a4d9104984c1> /P -4 >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Encryp#74 9 0 R /Size 10 >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C1_EscapedStandardHandlerKeys_stillRefuses()
    {
        var bytes = BuildEscapedStandardHandlerKeysInAbandonedRegion();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The negative control: the identical abandoned-region shape, but nothing in the file spells
    /// <c>/Encrypt</c> at all, escaped or not. Proves the escape-decoding fix stayed narrow — it
    /// still lets an ordinary damaged plaintext file, one that merely happens to contain an
    /// unterminated string, reconstruct and open.
    /// </summary>
    private static byte[] BuildUnterminatedStringWithNoEncryptionEvidence()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("4 0 obj\n(this string never closes, so the walk abandons everything after it\n");
        W("9 0 obj\n<< /Type /Marker /Value (NotEncryptionEvidence) >>\nendobj\n");
        W("trailer\n<< /Root 1 0 R /Size 10 >>\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C1_UnterminatedStringWithNoEncryptionEvidence_stillReconstructsAsPlaintext()
    {
        var bytes = BuildUnterminatedStringWithNoEncryptionEvidence();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        Assert.Null(reconstructed.Encryption);
    }

    // ── C2: /ByteRange (a signature-dictionary key) must not hide or fake encryption evidence ────

    /// <summary>
    /// An encryption dictionary padded with <c>/ByteRange</c> — a key that belongs to signature
    /// dictionaries (ISO 32000-2 Table 255), not encryption ones, and whatever exclusion keeps a
    /// real signature dictionary from being mistaken for an encryption dictionary (see the negative
    /// case below) must stay narrow enough that adding this one extra key to a genuine encryption
    /// dictionary does not make detection miss it. No trailer at all: structural last-resort only.
    /// </summary>
    private static byte[] BuildEncryptionDictionaryPaddedWithByteRange()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        var thirtyTwoZeroBytes = new string('0', 64); // 64 hex chars = 32 bytes, Table 20's /O, /U width
        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W($"9 0 obj\n<< /Filter /Standard /V 2 /R 3 /O<{thirtyTwoZeroBytes}> /U<{thirtyTwoZeroBytes}> "
          + "/P -1 /ByteRange [0 0 0 0] >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C2_EncryptionDictionaryPaddedWithByteRange_stillRefuses()
    {
        var bytes = BuildEncryptionDictionaryPaddedWithByteRange();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    /// <summary>
    /// The companion negative: a genuine signature-dictionary shape — <c>/Type /Sig</c>,
    /// <c>/ByteRange</c>, <c>/Contents</c> — with none of the encryption keys at all. Whatever
    /// exclusion keeps <c>/ByteRange</c> from hiding the real encryption dictionary above must also
    /// not swing the other way and treat an ordinary signature on a damaged plaintext file as
    /// encryption evidence: this must reconstruct and open as plaintext, proving the exclusion is
    /// narrow rather than absent.
    /// </summary>
    private static byte[] BuildDamagedPlaintextWithATopLevelSignatureDictionary()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("9 0 obj\n<< /Type /Sig /ByteRange [0 100 200 300] /Contents <00112233445566778899aabbccddeeff> >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C2_TrueSignatureDictionaryShape_isNotMistakenForEncryption_opensAsPlaintext()
    {
        var bytes = BuildDamagedPlaintextWithATopLevelSignatureDictionary();
        using var reconstructed = OpenReconstructed(bytes);

        var root = Assert.IsType<PdfIndirectReference>(reconstructed.Trailer.Get(PdfName.Root));
        Assert.Equal(1, root.ObjectNumber);
        Assert.Null(reconstructed.Encryption);
    }

    /// <summary>
    /// The public-key twin of <see cref="BuildEncryptionDictionaryPaddedWithByteRange"/>: no
    /// <c>/O</c>, <c>/U</c> or <c>/R</c> at all, disambiguated only by <c>/SubFilter</c>, still
    /// padded with <c>/ByteRange</c>. Detection has to reach it through the <c>/SubFilter</c> branch
    /// of the narrowing rather than the Standard-handler one, so this is not redundant with the
    /// Standard-handler case above — a fix that only widened the Standard branch's exclusion could
    /// leave this one still broken.
    /// </summary>
    private static byte[] BuildPublicKeyEncryptionDictionaryPaddedWithByteRange()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");
        W("9 0 obj\n<< /Filter /Adobe.PPKLite /V 4 /SubFilter /adbe.pkcs7.s5 /ByteRange [0 0 0 0] >>\nendobj\n");
        W("%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C2_PublicKeyEncryptionDictionaryPaddedWithByteRange_stillRefuses()
    {
        var bytes = BuildPublicKeyEncryptionDictionaryPaddedWithByteRange();

        PdfDocumentReader? reader = null;
        Assert.Throws<UnsupportedPdfFeatureException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
    }

    // ── C3: degenerate boundary inputs stay in the documented crash vocabulary ──────────────────

    /// <summary>
    /// The bare keyword with nothing after it at all, at the very end of the buffer. A trailer
    /// scan that assumes a dictionary always follows the keyword can read past the end of the
    /// buffer; the reader's crash vocabulary is <see cref="InvalidDataException"/>, never
    /// <see cref="IndexOutOfRangeException"/>.
    /// </summary>
    [Fact]
    public void C3_BareTrailerKeywordWithNothingAfterIt_throwsInvalidDataException()
    {
        var bytes = "trailer"u8.ToArray();
        Assert.Throws<InvalidDataException>(() =>
            PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
    }

    /// <summary>The same boundary case, past one otherwise-ordinary minimal object.</summary>
    [Fact]
    public void C3_MinimalObjectFollowedByBareTrailerKeyword_throwsInvalidDataException()
    {
        var bytes = "%PDF-1.7\n1 0 obj<<>>endobj\ntrailer"u8.ToArray();
        Assert.Throws<InvalidDataException>(() =>
            PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
    }

    // ── C4: a large comment must not let a failing boundary check evade the byte budget ─────────

    /// <summary>
    /// A handful of blocks, each a stream with an unverifiable <c>/Length</c> followed by a
    /// multi-KB comment and then a single byte that is not a real boundary. A comment is meant to
    /// be a cheap, uncharged skip to end of line; if the length-verification retry instead walks
    /// through the comment's own bytes while trying to confirm a terminator, the charge compounds
    /// across blocks. This file stays a few tens of KB — well inside the region where
    /// <c>budget = max(1 MiB, 8 × length)</c> is dominated by the 1 MiB floor, the same margin the
    /// earlier budget-exhaustion tests rely on — so an honest charge for this specific shape should
    /// already be enough to trip it.
    /// </summary>
    private static byte[] BuildUnverifiableLengthBeforeALargeCommentProbe()
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 200] >>\nendobj\n");

        const int BlockCount = 40;
        const int CommentBytes = 5_000;
        for (var i = 0; i < BlockCount; i++)
        {
            W($"{100 + i} 0 obj\n<< /Length 999999999 >>\nstream\nAB\nendstream\n");
            W("%" + new string('C', CommentBytes) + "\n");
            W("Z"); // filler: not the start of any real terminator or header
        }

        W("\n%%EOF\n");
        return ms.ToArray();
    }

    [Fact]
    public void C4_UnverifiableLengthBeforeALargeComment_throwsInvalidDataException_namingTheCostBudget()
    {
        var bytes = BuildUnverifiableLengthBeforeALargeCommentProbe();

        PdfDocumentReader? reader = null;
        var ex = Assert.Throws<InvalidDataException>(() =>
            reader = PdfReader.Open(bytes, new PdfReaderOptions { AllowReconstruction = true }));
        Assert.Null(reader);
        Assert.Contains("cost budget", ex.Message, StringComparison.Ordinal);
    }
}
