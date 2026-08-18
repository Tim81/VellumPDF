// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using VellumPdf.Canvas;
using VellumPdf.Conformance.Tests.Oracle;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;

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
/// setup) the test is skipped — unless <c>REQUIRE_VERAPDF=1</c>, which turns the absence into a
/// failure so a misconfigured CI image cannot silently skip the entire gate.
/// </summary>
[Collection("veraPDF")]
public sealed class VeraPdfOracleTests
{
    public static IEnumerable<object[]> Fixtures => OracleCorpus.All.Select(f => new object[] { f.Name });

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void InProcessVerdict_EqualsVeraPdf(string name)
    {
        if (!VeraPdf.IsAvailable)
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_VERAPDF") == "1")
                Assert.Fail("REQUIRE_VERAPDF=1 but the veraPDF CLI is not available on PATH.");
            Assert.Skip("veraPDF is not available on PATH (set up by CI; skipped locally).");
        }

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
        if (!VeraPdf.IsAvailable)
        {
            if (Environment.GetEnvironmentVariable("REQUIRE_VERAPDF") == "1")
                Assert.Fail("REQUIRE_VERAPDF=1 but the veraPDF CLI is not available on PATH.");
            Assert.Skip("veraPDF is not available on PATH (set up by CI; skipped locally).");
        }

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
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            // Short timeout so a hung `verapdf --version` cannot stall the test class's static init.
            return Run(10_000, "--version").Exit == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns true when veraPDF reports <paramref name="path"/> compliant with <paramref name="flavour"/>.</summary>
    public static bool Validate(string path, string flavour)
    {
        var (exit, stdout, stderr) = Run(120_000, "--flavour", flavour, "--format", "text", path);

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

    private static (int Exit, string Stdout, string Stderr) Run(int timeoutMs, params string[] args)
    {
        var (file, prefix) = ResolveLauncher();
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in prefix)
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start veraPDF.");

        // Drain both pipes concurrently BEFORE waiting, or a report larger than the OS pipe
        // buffer would block the child on write while we block in WaitForExit (deadlock).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            // Observe the drain tasks so a later fault doesn't surface as an unobserved exception.
            ObserveAndForget(stdoutTask);
            ObserveAndForget(stderrTask);
            throw new InvalidOperationException(
                $"veraPDF timed out after {timeoutMs}ms (args: {string.Join(' ', args)}).");
        }

        // The process has exited; bound the stream drain too, so a grandchild that inherited the
        // pipes and outlives the parent cannot make these reads hang indefinitely.
        var stdout = stdoutTask.Wait(5_000) ? stdoutTask.Result : string.Empty;
        var stderr = stderrTask.Wait(5_000) ? stderrTask.Result : string.Empty;
        return (process.ExitCode, stdout, stderr);
    }

    // Resolves the veraPDF launcher. CI puts an extensionless `verapdf` shim on PATH (Linux), which
    // CreateProcess runs directly. The Windows installer instead ships `verapdf.bat`, and
    // ProcessStartInfo with UseShellExecute=false only auto-resolves `.exe` — so when VERAPDF_HOME
    // points at an install carrying that launcher, invoke it through `cmd.exe /c`.
    private static (string File, string[] Prefix) ResolveLauncher()
    {
        if (OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable("VERAPDF_HOME") is { Length: > 0 } home)
        {
            var bat = Path.Combine(home, "verapdf.bat");
            if (File.Exists(bat))
                return ("cmd.exe", ["/c", bat]);
        }
        return ("verapdf", []);
    }

    private static void ObserveAndForget(Task task)
        => _ = task.ContinueWith(t => _ = t.Exception, TaskScheduler.Default);
}
