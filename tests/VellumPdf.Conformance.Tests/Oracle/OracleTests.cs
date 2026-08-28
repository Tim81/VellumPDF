// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Conformance.Tests.Oracle;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.TestSupport;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// The in-process half of the oracle gate: every corpus fixture's in-process preflight verdict must
/// match its declared expectation. Runs everywhere, with or without veraPDF.
/// </summary>
public sealed class InProcessOracleTests
{
    public static IEnumerable<object[]> Fixtures => OracleCorpus.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void InProcessVerdict_MatchesExpectation(string name)
    {
        var fixture = OracleCorpus.ByName(name);

        var result = PdfPreflight.Validate(fixture.Bytes, fixture.Level);

        Assert.Equal(fixture.ExpectedCompliant, result.IsCompliant);
    }
}

/// <summary>
/// Serialises the veraPDF cross-validation tests. Each one spawns a veraPDF JVM (hundreds of MB), so
/// running the whole corpus in parallel exhausts memory on small CI runners. A disabled-parallelisation
/// collection keeps at most one veraPDF process alive at a time.
/// </summary>
[CollectionDefinition("veraPDF", DisableParallelization = true)]
public sealed class VeraPdfCollection;

/// <summary>
/// The cross-validation half of the oracle gate: for each corpus fixture, the in-process verdict
/// must equal the verdict produced by veraPDF. When veraPDF is not on the PATH (the typical local
/// setup) the test is skipped — unless <c>CI</c>, <c>GITHUB_ACTIONS</c>, <c>REQUIRE_ORACLES</c> or
/// <c>REQUIRE_VERAPDF</c> demands the oracle run (<see cref="OracleGate"/>), which turns a
/// confirmed-absent or confirmed-wrong veraPDF into a failure so a misconfigured CI image cannot
/// silently skip the entire gate, the largest one in the tree, at 273 cases. A probe that merely
/// times out is gentler: it skips regardless of what the environment demands, unless it times out
/// three consecutive times, at which point <see cref="ExternalTool.CheckIdentity"/> itself treats
/// it the same as a confirmed-absent tool (#198 review, round 5).
/// </summary>
[Collection("veraPDF")]
public sealed class VeraPdfOracleTests
{
    public static IEnumerable<object[]> Fixtures => OracleCorpus.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void InProcessVerdict_EqualsVeraPdf(string name)
    {
        VeraPdf.EnsureAvailable();

        var fixture = OracleCorpus.ByName(name);

        // veraPDF's CLI shim mounts /tmp into the container, so on CI the fixture must live there.
        // Windows is excluded deliberately: Directory.Exists("/tmp") is true whenever a
        // C:\tmp directory happens to exist, and Path.Combine then yields the drive-relative
        // "/tmp\name.pdf", scattering fixtures through a shared root instead of the per-user
        // temp directory. veraPDF reads that path without trouble; this is hygiene, not a fix.
        // A GUID keeps concurrent runs from colliding on the same path.
        var baseDir = !OperatingSystem.IsWindows() && Directory.Exists("/tmp")
            ? "/tmp"
            : Path.GetTempPath();
        var path = Path.Combine(baseDir, $"vellum-oracle-{fixture.Name}-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, fixture.Bytes);
        try
        {
            var veraCompliant = VeraPdf.Validate(path, fixture.VeraFlavour);
            var inProcess = PdfPreflight.Validate(fixture.Bytes, fixture.Level).IsCompliant;

            Assert.Equal(fixture.ExpectedCompliant, veraCompliant);
            Assert.Equal(veraCompliant, inProcess);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>
/// Regression for an encrypted file reaching <see cref="VeraPdf.Validate"/>: veraPDF 1.30.2
/// refuses to open one at all, exiting 8 (measured directly against a user-password PDF/UA-1
/// file, invoked exactly as <see cref="VeraPdf.Validate"/> does) — which already falls into the
/// generic error arm of the exit-code switch and throws. The dedicated refusal check ahead of
/// that switch doesn't change whether this throws; it turns a generic "veraPDF returned error
/// exit code 8" into a message that names the actual cause, and is defence-in-depth against a
/// future veraPDF version or CI's Docker shim reporting this refusal on a different exit code.
/// </summary>
[Collection("veraPDF")]
public sealed class VeraPdfEncryptedFileRefusalTests
{
    [Fact]
    public void Validate_onEncryptedFile_reportsRefusalNotAGenericErrorCode()
    {
        VeraPdf.EnsureAvailable();

        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfUA1, Tagged = true, Language = "en-US" };
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("veraPDF encrypted-refusal oracle regression").EndText();
        canvas.Finish();
        doc.Encrypt(new PdfEncryptionSettings { UserPassword = "openme" });

        using var ms = new MemoryStream();
        doc.Save(ms);

        // Same reasoning as the site above: /tmp is what the CI shim mounts, and Windows is
        // excluded so a stray C:\tmp does not collect fixtures.
        var baseDir = !OperatingSystem.IsWindows() && Directory.Exists("/tmp")
            ? "/tmp"
            : Path.GetTempPath();
        var path = Path.Combine(baseDir, $"vellum-oracle-encrypted-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, ms.ToArray());
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => VeraPdf.Validate(path, "ua1"));

            // Assert on text only the dedicated guard emits, not "encrypted" alone: the generic
            // error-exit-code arm's message also embeds the raw stdout/stderr, which contains
            // that word too, so a weaker assertion here would pass even with the guard deleted.
            Assert.Contains("cannot be used as an oracle input", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Thin wrapper around the veraPDF command-line validator.</summary>
internal static class VeraPdf
{
    /// <summary>
    /// Gates the caller on veraPDF's availability through <see
    /// cref="ExternalTool.EnsureUsable(string)"/>, the same single routing point <see
    /// cref="ExternalTool.TryRun"/> itself uses for its own five known tools, so this wrapper
    /// shares that probe's budget, its cache, its consecutive-timeout retry-then-escalate policy,
    /// and its policy of skipping (not failing the build) on a single timeout. Before #198 round 4
    /// this ran its own independent probe (a hardcoded 10-second
    /// <c>verapdf --version</c>, its result cached forever in a <c>static readonly</c> field
    /// initializer) that read a slow-but-fine JVM cold start the same way the pre-round-3 shared
    /// 10-second budget did, except here the false verdict was permanent for the process and, once
    /// the caller below started routing it through <see cref="OracleGate.Unavailable(string,
    /// string)"/>, escalated under CI and failed every one of the 273
    /// <c>InProcessVerdict_EqualsVeraPdf</c> cases off a single unlucky sample. Before round 5 this
    /// method still re-derived the IsTimeout branch itself, alongside two other call sites doing
    /// the same thing, exactly the duplication that let one of the three get it wrong.
    /// </summary>
    public static void EnsureAvailable() => ExternalTool.EnsureUsable("verapdf");

    /// <summary>Returns true when veraPDF reports <paramref name="path"/> compliant with <paramref name="flavour"/>.</summary>
    public static bool Validate(string path, string flavour)
    {
        ExternalTool.TryRun(
            "verapdf", ["--flavour", flavour, "--format", "text", path],
            out var exit, out var stdout, out var stderr, out var timedOut, timeoutMs: 120_000);

        if (timedOut)
        {
            throw new InvalidOperationException(
                $"veraPDF timed out validating {path} ({flavour}).\nstdout:\n{stdout}\nstderr:\n{stderr}");
        }

        // veraPDF 1.30.2 refuses to open an encrypted PDF outright rather than validating it —
        // measured directly (--flavour ua1 --format text, matching the invocation below) as
        // exit 8, stdout "...appears to be an encrypted PDF file and could not be processed.",
        // stderr "WARNING: ...appears to be an encrypted PDF." Exit 8 already falls into the
        // generic error arm below and throws, so this check isn't closing a "refusal reads as
        // compliant" hole on the pinned version; it turns that generic error into a message that
        // names the actual cause, and is defence-in-depth against a future version or CI's Docker
        // shim reporting the refusal on a different exit code. Matches the shorter "appears to be
        // an encrypted PDF" (common to both streams' wording) against stdout and stderr, since
        // --format text never puts document content on either stream — only "PASS|FAIL <path>
        // <flavour>" on success and diagnostics on failure — so there's no false-positive risk
        // from a title or other string that happens to contain it.
        if (stdout.Contains("appears to be an encrypted PDF", StringComparison.Ordinal)
            || stderr.Contains("appears to be an encrypted PDF", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"veraPDF refused {path} as an encrypted PDF and did not validate it (exit {exit}); "
                + "it cannot be used as an oracle input.\n"
                + $"stdout:\n{stdout}\nstderr:\n{stderr}");

        // veraPDF exit codes: 0 = the file is compliant; 1 = ran, file non-compliant; >1 = error.
        return exit switch
        {
            0 => true,
            1 => false,
            _ => throw new InvalidOperationException(
                $"veraPDF returned error exit code {exit} for {path} ({flavour}).\n"
                + $"stdout:\n{stdout}\nstderr:\n{stderr}"),
        };
    }
}
