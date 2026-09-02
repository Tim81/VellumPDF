// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader;
using static VellumPdf.Conformance.Tests.EncryptDictionaryAssertions;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// veraPDF cross-validation for ISO 14289-1 §7.16-1. Gated like every other veraPDF test through
/// <see cref="VeraPdf.EnsureAvailable"/> and the shared <c>veraPDF</c> collection (at most one
/// veraPDF JVM alive at a time). Not part of <c>OracleCorpus</c>: every corpus fixture is fed to
/// <c>InProcessVerdict_EqualsVeraPdf</c> with no password, and an encrypted fixture there would
/// hit the refusal guard on every run rather than the one this class exists to exercise.
/// </summary>
/// <remarks>
/// The three R6 fixtures the tests below read through <see cref="ReadEmbeddedFixture"/> are
/// committed rather than built by each test, because a document built fresh at
/// test time is an unreliable input to veraPDF here. ISO 32000-2 §7.6.4.3.4 Algorithm 2.B runs its
/// hash loop for 64 rounds, then keeps going while the last byte of E is greater than the round
/// number minus 32, so the correct exit test is <c>E[last] &lt;= completedRounds - 32</c>. That is
/// what this library's <c>StandardSecurityHandler.Hash2B</c> does, and qpdf and pdf.js agree.
/// veraPDF's own <c>EncryptionToolsRevision5_6.computeHash</c> (veraPDF-parser,
/// <c>org.verapdf.tools</c>) instead exits on <c>E[last] &lt;= rounds - 32</c> with a zero-based
/// round counter — in the spec's completed-rounds frame (<c>completedRounds = rounds + 1</c>) that
/// is <c>E[last] &lt;= completedRounds - 33</c>, one round later than the spec text. The two
/// readings only disagree when
/// <c>E[last]</c> lands exactly on <c>completedRounds - 32</c>, in which case veraPDF runs one
/// extra round, derives a different hash from the same password and salt, and fails its own
/// <c>/U</c> check on the file it just opened, refusing it outright (exit 8) even though qpdf and
/// poppler read the same bytes without complaint. Because the writer draws its salts from
/// <see cref="System.Security.Cryptography.RandomNumberGenerator"/>, whether a freshly written file
/// lands on that boundary is chance, measured at 6 refusals out of 60 freshly built files, so a
/// fixture built inline here would make these tests flaky against veraPDF for a reason unrelated to
/// §7.16-1. See <c>Assets/README.md</c> for the exact provenance of each fixture. If a fixture is
/// ever regenerated, it has to be re-checked with veraPDF before its SHA-256 is updated, the same
/// way the check was done before it was first committed.
/// </remarks>
[Collection("veraPDF")]
public sealed class UaEncryptionPermissionsVeraPdfTests
{
    private const string RuleId = "ISO14289-1:7.16-1";

    // Measured directly from a local veraPDF 1.30.2 --format xml run against the ua1 flavour:
    // <validationReport jobEndStatus="normal" profileName="PDF/UA-1 validation profile" ...>. A
    // report that names some OTHER profile, or a truncated one that dropped this element entirely,
    // must not let a "no clause 7.16" assertion below pass for the wrong reason.
    private const string Ua1ProfileNameAttribute = "profileName=\"PDF/UA-1 validation profile\"";

    /// <summary>
    /// The committed violating fixture: veraPDF's own report names the exact rule this library's
    /// rule claims to implement, in the attribute order veraPDF 1.30.2 prints it.
    /// </summary>
    [Fact]
    public void ViolatingFixture_veraPdfReportsClause716Failed()
    {
        VeraPdf.EnsureAvailable();

        var bytes = ReadEmbeddedFixture("enc-aes-256-p-bit10-clear.pdf");
        AssertEncryptDictionaryIsR6WithP(bytes, -516);
        var path = WriteTempFile(bytes, "violating");
        try
        {
            var report = VeraPdf.Report(path, "ua1");

            Assert.Contains(Ua1ProfileNameAttribute, report, StringComparison.Ordinal);
            Assert.Contains(
                "clause=\"7.16\" testNumber=\"1\" status=\"failed\"", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A writer-built, bit-10-set fixture draws no 7.16 element at all — veraPDF's XML report
    /// lists failed rules only, so a passing rule is absent rather than marked
    /// <c>status="passed"</c> (measured on
    /// <c>tests/VellumPdf.Reader.Tests/Fixtures/Encrypted/enc-aes-128-emptyuser.pdf</c>, which fails
    /// nine other rules and has no 7.16 element either). "Absent" only means something once the
    /// report is confirmed real: an empty or truncated string is also missing every clause, so this
    /// asserts the report names the PDF/UA-1 profile before trusting that 7.16 is not in it. The
    /// overall verdict from <see cref="VeraPdf.Validate"/> is asserted too, so this fixture is proven
    /// non-compliant for reasons OTHER than 7.16-1 rather than merely unchecked.
    /// </summary>
    [Fact]
    public void CompliantFixture_veraPdfReportsNoClause716Element()
    {
        VeraPdf.EnsureAvailable();

        var bytes = ReadEmbeddedFixture("enc-aes-256-emptyuser-p-all.pdf");
        AssertEncryptDictionaryIsR6WithP(bytes, -4);
        var path = WriteTempFile(bytes, "compliant");
        try
        {
            Assert.False(VeraPdf.Validate(path, "ua1"), "untagged fixture; non-compliant for unrelated UA-1 reasons");

            var report = VeraPdf.Report(path, "ua1");
            Assert.Contains(Ua1ProfileNameAttribute, report, StringComparison.Ordinal);
            Assert.DoesNotContain("clause=\"7.16\"", report, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// A document with a real (non-empty) user password. Before #138 this was the case
    /// <c>PdfUaOutOfScope</c> named directly: "a preflight run has no way to be given a password, so
    /// a document that needs one cannot be checked at all." Both veraPDF and this library now refuse
    /// it with none supplied, for the same reason: <see cref="VeraPdf.Validate"/>'s refusal guard
    /// was written against veraPDF exiting 8 on exactly this shape of file.
    /// </summary>
    [Fact]
    public void UserPasswordDocument_noPassword_refusalMatchesInProcessThrow()
    {
        VeraPdf.EnsureAvailable();

        var bytes = ReadEmbeddedFixture("enc-aes-256-userpw-u-p-all.pdf");
        AssertEncryptDictionaryIsR6WithP(bytes, -4);
        var path = WriteTempFile(bytes, "userpw-nopw");
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => VeraPdf.Validate(path, "ua1"));
            Assert.Contains("cannot be used as an oracle input", ex.Message, StringComparison.Ordinal);

            Assert.Throws<PdfPasswordException>(
                () => PdfPreflight.Validate(bytes, PdfConformance.PdfUA1, password: null));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// The same document, this time with the password both sides need: veraPDF's <c>--password</c>
    /// and <see cref="PdfPreflight"/>'s new <c>password</c> parameter. Both open the file and agree
    /// there is no 7.16-1 finding — the fixture's <c>Permissions = All</c> sets bit 10 — even though
    /// the overall verdict stays non-compliant, for the unrelated reason this fixture is untagged.
    /// The profile-name check on the report rules out an empty or truncated response agreeing by
    /// accident.
    /// </summary>
    [Fact]
    public void UserPasswordDocument_withPassword_verdictMatchesVeraPdf()
    {
        VeraPdf.EnsureAvailable();

        var bytes = ReadEmbeddedFixture("enc-aes-256-userpw-u-p-all.pdf");
        AssertEncryptDictionaryIsR6WithP(bytes, -4);
        var path = WriteTempFile(bytes, "userpw-withpw");
        try
        {
            // The overall verdict is non-compliant — this minimal fixture is untagged, so plenty of
            // OTHER UA-1 rules fail on it — the report content below is what isolates 7.16-1's own
            // verdict from the rest.
            Assert.False(VeraPdf.Validate(path, "ua1", password: "u"));

            var report = VeraPdf.Report(path, "ua1", password: "u");
            Assert.Contains(Ua1ProfileNameAttribute, report, StringComparison.Ordinal);
            var veraPdfHasFinding = report.Contains("clause=\"7.16\"", StringComparison.Ordinal);

            var result = PdfPreflight.Validate(bytes, PdfConformance.PdfUA1, password: "u");
            var inProcessHasFinding = result.Assertions.Any(a => a.RuleId == RuleId);

            Assert.False(inProcessHasFinding);
            Assert.Equal(veraPdfHasFinding, inProcessHasFinding);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static byte[] ReadEmbeddedFixture(string logicalName)
    {
        using var s = typeof(UaEncryptionPermissionsVeraPdfTests).Assembly.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"{logicalName} embedded resource not found.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    // Same reasoning as VeraPdfOracleTests: veraPDF's CLI shim mounts /tmp into the container on CI,
    // so the fixture must live there. Windows is excluded so a stray C:\tmp does not collect them.
    private static string WriteTempFile(byte[] bytes, string label)
    {
        var baseDir = !OperatingSystem.IsWindows() && Directory.Exists("/tmp")
            ? "/tmp"
            : Path.GetTempPath();
        var path = Path.Combine(baseDir, $"vellum-oracle-716-{label}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, bytes);
        return path;
    }
}
