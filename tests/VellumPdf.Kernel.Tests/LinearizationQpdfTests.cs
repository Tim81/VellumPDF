// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// qpdf structural check for linearized output.
/// Tries the qpdf binary at the well-known local path and on PATH.
/// Self-skips when qpdf is unavailable on local dev machines; fails on CI.
/// </summary>
public sealed class LinearizationQpdfTests : IDisposable
{
    private static readonly DateTimeOffset PinnedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    // Full path to local dev qpdf install (not on PATH).
    private const string LocalQpdfPath = @"C:\Users\Timothy\tools\qpdf\qpdf-12.3.2-msvc64\bin\qpdf.exe";

    private readonly string _tempDir;

    public LinearizationQpdfTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellum_lin_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void Linearized_MultiPage_QpdfCheck_Passes()
    {
        var path = Path.Combine(_tempDir, "linearized_3page.pdf");

        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
            Linearize = true,
        };
        doc.Info.Title = "QpdfOracleTest";
        for (var i = 0; i < 3; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText().SetFont(font, 12)
                .SetTextMatrix(1, 0, 0, 1, 72, 720)
                .ShowText($"Page {i + 1}")
                .EndText();
            canvas.Finish();
        }

        using (var fs = File.OpenWrite(path))
            doc.Save(fs);

        if (!TryRunQpdf($"--check \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on linearized 3-page doc.\n" +
            $"stdout: {stdout}\nstderr: {stderr}");
        Assert.Contains("File is linearized", stdout);
    }

    [Fact]
    public void Linearized_ShowLinearization_RecognizedAndClean()
    {
        var path = Path.Combine(_tempDir, "linearized_show.pdf");

        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
            Linearize = true,
        };
        for (var i = 0; i < 4; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText().SetFont(font, 12)
                .SetTextMatrix(1, 0, 0, 1, 72, 720)
                .ShowText($"Page {i + 1}")
                .EndText();
            canvas.Finish();
        }
        using (var fs = File.OpenWrite(path))
            doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        // qpdf recognizes the file as linearized, reports the right page count, and emits no
        // WARNING lines (which is where hint-table inconsistencies surface).
        Assert.True(exit == 0, $"qpdf --show-linearization exit {exit}.\nstdout: {stdout}\nstderr: {stderr}");
        Assert.Contains("npages: 4", stdout);
        Assert.DoesNotContain("WARNING", stdout);
        Assert.DoesNotContain("WARNING", stderr);
    }

    // Tries the local qpdf path, then falls back to finding "qpdf" on PATH.
    private static bool TryRunQpdf(string args, out int exitCode, out string stdout, out string stderr)
    {
        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;

        // Try the local dev install first; fall back to PATH.
        string? exe = File.Exists(LocalQpdfPath) ? LocalQpdfPath : "qpdf";

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try { process = Process.Start(psi); }
        catch (Win32Exception) { return false; }

        if (process is null) return false;

        using (process)
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            var completed = process.WaitForExit(milliseconds: 30_000);
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();
            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                exitCode = -1;
                return true;
            }
            exitCode = process.ExitCode;
        }
        return true;
    }

    private static void GateOnCi(string toolName)
    {
        var isCI = string.Equals(
            Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase);
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
            StringComparison.OrdinalIgnoreCase);
        if (isCI || isGitHubActions)
            Assert.Fail($"Required external tool '{toolName}' is unavailable on CI.");
    }
}
