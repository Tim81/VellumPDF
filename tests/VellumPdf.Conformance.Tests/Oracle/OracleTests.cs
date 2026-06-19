// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using VellumPdf.Conformance.Tests.Oracle;

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
/// The cross-validation half of the oracle gate: for each corpus fixture, the in-process verdict
/// must equal the verdict produced by veraPDF. When veraPDF is not on the PATH (the typical local
/// setup) the test is skipped — unless <c>REQUIRE_VERAPDF=1</c>, which turns the absence into a
/// failure so a misconfigured CI image cannot silently skip the entire gate.
/// </summary>
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

        // veraPDF's CLI shim mounts /tmp into the container, so the fixture must live there.
        // A GUID keeps concurrent runs from colliding on the same path.
        var baseDir = Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath();
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

/// <summary>Thin wrapper around the veraPDF command-line validator.</summary>
internal static class VeraPdf
{
    public static bool IsAvailable { get; } = Probe();

    private static bool Probe()
    {
        try
        {
            return Run("--version").Exit == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Returns true when veraPDF reports <paramref name="path"/> compliant with <paramref name="flavour"/>.</summary>
    public static bool Validate(string path, string flavour)
    {
        var (exit, stdout, stderr) = Run("--flavour", flavour, "--format", "text", path);

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

    private static (int Exit, string Stdout, string Stderr) Run(params string[] args)
    {
        var psi = new ProcessStartInfo("verapdf")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start veraPDF.");

        // Drain both pipes concurrently BEFORE waiting, or a report larger than the OS pipe
        // buffer would block the child on write while we block in WaitForExit (deadlock).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(120_000))
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* best effort */ }
            throw new InvalidOperationException(
                $"veraPDF timed out after 120s (args: {string.Join(' ', args)}).");
        }

        // Ensure the async stream reads have completed now that the process has exited.
        process.WaitForExit();
        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }
}
