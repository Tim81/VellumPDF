// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Signing;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// External-validator oracle: generates representative PDFs to temp files and
/// validates them with qpdf (structural check) and pdftotext (content extraction).
///
/// Tool gating:
///   - If a required tool is unavailable AND we are on CI (CI=true or GITHUB_ACTIONS=true),
///     the test fails with a clear message so CI never silently skips the oracle.
///   - If the tool is unavailable on a local dev machine, the test returns early (skip).
/// </summary>
public sealed class PdfValidatorOracleTests : IDisposable
{
    private readonly string _tempDir;

    public PdfValidatorOracleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumoracle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup — temp dir may already be gone */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup — locked file on Windows */ }
    }

    // ── qpdf structural check ────────────────────────────────────────────────

    [Fact]
    public void MultiPage_StandardFont_QpdfCheck_Passes()
    {
        var pdfPath = Path.Combine(_tempDir, "multipage.pdf");
        GenerateMultiPageDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on multi-page doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void HeadingTableList_QpdfCheck_Passes()
    {
        var pdfPath = Path.Combine(_tempDir, "heading_table_list.pdf");
        GenerateHeadingTableListDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on heading/table/list doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void EmbeddedFont_QpdfCheck_Passes()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) return; // no platform font — skip embedded-font case only

        var pdfPath = Path.Combine(_tempDir, "embedded_font.pdf");
        GenerateEmbeddedFontDoc(pdfPath, fontPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on embedded-font doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    // ── pdftotext content extraction ─────────────────────────────────────────

    [Fact]
    public void MultiPage_StandardFont_PdftotextFindsMarkers()
    {
        var pdfPath = Path.Combine(_tempDir, "multipage_text.pdf");
        GenerateMultiPageDoc(pdfPath);

        if (!TryRunTool("pdftotext", $"\"{pdfPath}\" -", out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        Assert.True(
            text.Contains("VELLUMORACLE123", StringComparison.Ordinal),
            $"Marker 'VELLUMORACLE123' not found in pdftotext output.\nstderr: {stderr}");
        Assert.True(
            text.Contains("FinalMarkerXYZ", StringComparison.Ordinal),
            $"Marker 'FinalMarkerXYZ' not found in pdftotext output.\nstderr: {stderr}");
    }

    [Fact]
    public void HeadingTableList_PdftotextFindsMarkers()
    {
        var pdfPath = Path.Combine(_tempDir, "heading_table_list_text.pdf");
        GenerateHeadingTableListDoc(pdfPath);

        if (!TryRunTool("pdftotext", $"\"{pdfPath}\" -", out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        Assert.True(
            text.Contains("CellMarkerA", StringComparison.Ordinal),
            $"Table marker 'CellMarkerA' not found.\nstderr: {stderr}");
        Assert.True(
            text.Contains("ListMarkerOne", StringComparison.Ordinal),
            $"List marker 'ListMarkerOne' not found.\nstderr: {stderr}");
        Assert.True(
            text.Contains("ListMarkerTwo", StringComparison.Ordinal),
            $"List marker 'ListMarkerTwo' not found.\nstderr: {stderr}");
    }

    [Fact]
    public void EmbeddedFont_PdftotextFindsMarker()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) return; // no platform font — skip embedded-font case only

        var pdfPath = Path.Combine(_tempDir, "embedded_font_text.pdf");
        GenerateEmbeddedFontDoc(pdfPath, fontPath);

        if (!TryRunTool("pdftotext", $"\"{pdfPath}\" -", out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        Assert.True(
            text.Contains("EMBEDORACLE456", StringComparison.Ordinal),
            $"Embedded-font marker 'EMBEDORACLE456' not found in pdftotext output.\nstderr: {stderr}");
    }

    // ── AES-256 encryption oracle tests ─────────────────────────────────────

    private const string EncryptionUserPassword = "openme";
    private const string EncryptionMarker = "VELLUMORACLE_ENCRYPTTEST_7F3A";

    [Fact]
    public void AesEncrypted_QpdfCheck_Passes()
    {
        var pdfPath = Path.Combine(_tempDir, "encrypted_aes256.pdf");
        GenerateEncryptedDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--password={EncryptionUserPassword} --check \"{pdfPath}\"",
            out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on AES-256 encrypted doc.\n" +
            $"stdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void AesEncrypted_QpdfShowEncryption_ReportsAESV3()
    {
        var pdfPath = Path.Combine(_tempDir, "encrypted_showenc.pdf");
        GenerateEncryptedDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--password={EncryptionUserPassword} --show-encryption \"{pdfPath}\"",
            out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        // qpdf --show-encryption reports the algorithm as "AESv3" for V5/R6
        // (it does not print the literal key length "256").
        Assert.True(
            stdout.Contains("AES", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("AES", StringComparison.OrdinalIgnoreCase),
            $"Expected 'AES' in qpdf --show-encryption output.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void AesEncrypted_PdftotextWithPassword_FindsMarker()
    {
        var pdfPath = Path.Combine(_tempDir, "encrypted_pdftotext.pdf");
        GenerateEncryptedDoc(pdfPath);

        if (!TryRunTool("pdftotext", $"-upw {EncryptionUserPassword} \"{pdfPath}\" -",
            out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        Assert.True(
            text.Contains(EncryptionMarker, StringComparison.Ordinal),
            $"Encryption marker '{EncryptionMarker}' not found in pdftotext output.\nstderr: {stderr}");
    }

    // ── PAdES signature oracle test ─────────────────────────────────────────

    [Fact]
    public void Signed_doc_pdfsig_reports_valid_signature()
    {
        var pdfPath = Path.Combine(_tempDir, "signed.pdf");
        GenerateSignedDoc(pdfPath);

        if (!TryRunTool("pdfsig", $"\"{pdfPath}\"", out _, out var stdout, out var stderr))
        {
            GateOnCi("pdfsig");
            return;
        }

        // pdfsig (poppler-utils) outputs "Signature is Valid" for a valid digest.
        // An untrusted self-signed certificate still produces a valid digest, so
        // "Signature is Valid" confirms the /ByteRange and /Contents are correct.
        Assert.True(
            stdout.Contains("Signature is Valid", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Signature is Valid", StringComparison.OrdinalIgnoreCase),
            $"pdfsig did not report 'Signature is Valid'.\nstdout: {stdout}\nstderr: {stderr}");
    }

    // ── Document generators ──────────────────────────────────────────────────

    private static void GenerateSignedDoc(string path)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Oracle Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        using var doc = new Document();
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        doc.Add(new Paragraph("VELLUM_PDFSIG_ORACLE_TEST", style));

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            SignerName = "VellumPdf Oracle",
            Reason = "Oracle test",
        };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        doc.Sign(fs, settings);
    }

    private static void GenerateMultiPageDoc(string path)
    {
        using var doc = new Document();
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };

        doc.Add(new Paragraph("VELLUMORACLE123 — page one marker.", style));

        // Fill enough paragraphs to guarantee a second page
        for (var i = 0; i < 55; i++)
            doc.Add(new Paragraph($"Body paragraph {i + 1}: The quick brown fox jumps over the lazy dog.", style));

        doc.Add(new Paragraph("FinalMarkerXYZ — last-page marker.", style));
        doc.Save(path);
    }

    private static void GenerateHeadingTableListDoc(string path)
    {
        using var doc = new Document();

        doc.Add(new Heading("OracleHeading", new TextStyle { FontSize = 18 }));

        var table = new TableElement();
        table.SetColumnWidths(200, 200);
        var header = table.AddHeaderRow();
        header.AddCell("ColumnOne").AddCell("ColumnTwo");
        var row = table.AddRow();
        row.AddCell("CellMarkerA").AddCell("CellMarkerB");
        doc.Add(table);

        var list = new ListElement(ListStyle.OrderedDecimal);
        list.Add("ListMarkerOne");
        list.Add("ListMarkerTwo");
        doc.Add(list);

        doc.Save(path);
    }

    private static void GenerateEncryptedDoc(string path)
    {
        using var doc = new Document();
        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };
        doc.Add(new Paragraph(EncryptionMarker, style));
        doc.Add(new Paragraph("This document is AES-256 encrypted.", style));
        doc.Encrypt(new PdfEncryptionSettings
        {
            UserPassword = EncryptionUserPassword,
            OwnerPassword = "ownerpassword",
            Permissions = PdfPermissions.All,
        });
        doc.Save(path);
    }

    private static void GenerateEmbeddedFontDoc(string path, string fontPath)
    {
        using var doc = new Document();
        var handle = doc.LoadTrueTypeFont(fontPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("EMBEDORACLE456 — embedded font marker.", style));
        doc.Save(path);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first platform font path that exists, or null if neither is present.
    /// </summary>
    private static string? FindPlatformFont()
    {
        string[] candidates =
        [
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// Attempts to run an external CLI tool and captures its output.
    /// Returns false if the process cannot be started (tool not installed).
    /// Gives the process a 30-second timeout and disposes properly.
    /// stdout and stderr are captured; stdout is returned via the out parameter.
    /// </summary>
    private static bool TryRunTool(
        string exe,
        string args,
        out int exitCode,
        out string stdout,
        out string stderr)
    {
        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        Process? process = null;
        try
        {
            process = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            // Tool not installed on this machine.
            return false;
        }

        if (process is null) return false;

        using (process)
        {
            // Read both streams concurrently to avoid deadlock on large output.
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            var completed = process.WaitForExit(milliseconds: 30_000);
            stdout = stdoutTask.GetAwaiter().GetResult();
            stderr = stderrTask.GetAwaiter().GetResult();

            if (!completed)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* process already exited — best-effort */ }
                exitCode = -1;
                return true; // tool exists but timed out — let the assertion handle it
            }

            exitCode = process.ExitCode;
        }

        return true;
    }

    /// <summary>
    /// Asserts failure when running on CI and the required tool is absent.
    /// On a local dev machine (non-CI), this method does nothing (skip silently).
    /// </summary>
    private static void GateOnCi(string toolName)
    {
        var isCI = string.Equals(
            Environment.GetEnvironmentVariable("CI"), "true",
            StringComparison.OrdinalIgnoreCase);
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
            StringComparison.OrdinalIgnoreCase);

        if (isCI || isGitHubActions)
        {
            Assert.Fail(
                $"Required external tool '{toolName}' is not available on CI. " +
                "Ensure the CI workflow installs it (e.g. sudo apt-get install -y qpdf poppler-utils).");
        }

        // Local dev: tool not installed — silently skip.
    }
}
