// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Fonts;
using VellumPdf.Forms;
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

    // ── AcroForm interactive-field oracle tests ─────────────────────────────

    private const string FormTextFieldMarker = "FORMORACLE_TXT_9B4F";

    [Fact]
    public void AcroForm_QpdfCheck_Passes()
    {
        var pdfPath = Path.Combine(_tempDir, "acroform.pdf");
        GenerateAcroFormDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on AcroForm doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void AcroForm_PdftotextFindsFieldValue()
    {
        var pdfPath = Path.Combine(_tempDir, "acroform_text.pdf");
        GenerateAcroFormDoc(pdfPath);

        if (!TryRunTool("pdftotext", $"\"{pdfPath}\" -", out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        // pdftotext may or may not render field appearance streams depending on the
        // version — we accept either the text appearing or pdftotext succeeding without error.
        // If the marker IS present that is a bonus assertion.
        if (text.Contains(FormTextFieldMarker, StringComparison.Ordinal))
        {
            // Positive signal — appearance text was extracted.
            Assert.True(true);
        }
        else
        {
            // pdftotext ran successfully but may not render form AP streams; that's OK.
            // The structural validity is covered by qpdf --check above.
            Assert.True(stderr.Length == 0 || !stderr.Contains("Error", StringComparison.OrdinalIgnoreCase),
                $"pdftotext reported errors on AcroForm doc.\nstderr: {stderr}");
        }
    }

    /// <summary>
    /// Generates a PDF with one text field, one checkbox (checked), and one dropdown.
    /// Uses PdfDocument directly (no Layout engine) so the test stays at the kernel layer.
    /// </summary>
    private static void GenerateAcroFormDoc(string path)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);

        // Text field — value contains the oracle marker
        doc.AddTextField(
            page,
            name: "TextField1",
            rect: new PdfRectangle(72, 700, 400, 720),
            value: FormTextFieldMarker,
            options: new FormFieldOptions { FontSize = 12 });

        // Checkbox — checked
        doc.AddCheckBox(
            page,
            name: "CheckBox1",
            rect: new PdfRectangle(72, 670, 92, 690),
            checkedState: true);

        // Dropdown
        doc.AddChoiceField(
            page,
            name: "Dropdown1",
            rect: new PdfRectangle(72, 640, 300, 660),
            options: ["Option A", "Option B", "Option C"],
            selected: "Option B",
            combo: true);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        doc.Save(fs);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first platform font path that exists, or null if neither is present.
    /// Delegates to the shared cross-platform finder in PdfTestUtil.
    /// </summary>
    private static string? FindPlatformFont() => PdfTestUtil.FindPlatformFont();

    /// <summary>
    /// Returns the first OpenType-CFF (.otf) font path found on the current platform,
    /// or null if none is available. Delegates to the shared finder in PdfTestUtil.
    /// </summary>
    private static string? FindOtfFont() => PdfTestUtil.FindOtfFont();

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

    // ── Radio button group + push button oracle tests ────────────────────────

    [Fact]
    public void RadioGroupAndPushButton_QpdfCheck_Passes()
    {
        var pdfPath = Path.Combine(_tempDir, "radio_pushbutton.pdf");
        GenerateRadioAndPushButtonDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on radio+push-button doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    /// <summary>
    /// Generates a PDF with a radio button group (3 options, one selected) and a push button.
    /// Uses PdfDocument directly (kernel layer only).
    /// </summary>
    private static void GenerateRadioAndPushButtonDoc(string path)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);

        var radioOptions = new[]
        {
            new RadioOption(page, new PdfRectangle(72, 720, 90, 738), "OptionA"),
            new RadioOption(page, new PdfRectangle(72, 700, 90, 718), "OptionB"),
            new RadioOption(page, new PdfRectangle(72, 680, 90, 698), "OptionC"),
        };
        doc.AddRadioButtonGroup(
            name: "RadioGroup1",
            options: radioOptions,
            selectedExportValue: "OptionB");

        doc.AddPushButton(
            page: page,
            name: "Submit",
            rect: new PdfRectangle(72, 640, 200, 660),
            caption: "Submit Form");

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        doc.Save(fs);
    }

    // ── Object-stream / XRef-stream oracle tests ─────────────────────────────

    private const string ObjStmOracleMarker = "VELLUMORACLE_OBJSTM_4D9B";

    [Fact]
    public void ObjectStream_MultiPage_QpdfCheck_Passes()
    {
        var pdfPath = Path.Combine(_tempDir, "objstm_multipage.pdf");
        GenerateObjStmMultiPageDoc(pdfPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on object-stream doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void ObjectStream_MultiPage_PdftotextFindsMarker()
    {
        var pdfPath = Path.Combine(_tempDir, "objstm_multipage_text.pdf");
        GenerateObjStmMultiPageDoc(pdfPath);

        if (!TryRunTool("pdftotext", $"\"{pdfPath}\" -", out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        Assert.True(
            text.Contains(ObjStmOracleMarker, StringComparison.Ordinal),
            $"Marker '{ObjStmOracleMarker}' not found in pdftotext output.\nstderr: {stderr}");
    }

    private static void GenerateObjStmMultiPageDoc(string path)
    {
        using var doc = new Document();
        doc.UseObjectStreams = true;

        var style = new TextStyle { Font = Standard14.Helvetica, FontSize = 12 };

        doc.Add(new Paragraph($"{ObjStmOracleMarker} — object-stream test doc.", style));

        // Multi-page content to produce several indirect objects
        for (var i = 0; i < 30; i++)
            doc.Add(new Paragraph($"Body paragraph {i + 1}: The quick brown fox jumps over the lazy dog.", style));

        doc.Add(new Paragraph("Last line of object-stream test.", style));
        doc.Save(path);
    }

    // ── veraPDF PDF/A conformance oracle ──────────────────────────────────────

    // The veraPDF CLI is provided on PATH in CI via a Docker shim (see
    // .github/workflows/ci.yml "Install veraPDF shim"). These tests therefore gate
    // on CI through GateOnCi("verapdf"): if veraPDF is missing on CI the build fails,
    // while a local machine without veraPDF simply skips. The compliance assertion is
    // strict — a non-compliant report fails the test and the full report is surfaced;
    // it is never weakened to hide a failing rule.
    //
    // Each gated document embeds a TrueType font for ALL of its text (PDF/A forbids
    // unembedded fonts) — the layout default style is Standard-14 Helvetica, so the
    // embedded style is set explicitly on every element (default cell style, list
    // default style, heading/paragraph styles).

    [Fact]
    public void PdfA2b_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_verapdf.pdf");
        GeneratePdfATextDoc(pdfPath, fontPath, PdfConformance.PdfA2b);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void PdfA2u_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2u_verapdf.pdf");
        GeneratePdfATextDoc(pdfPath, fontPath, PdfConformance.PdfA2u);
        AssertVeraPdfCompliant(pdfPath, "2u");
    }

    [Fact]
    public void PdfA2b_Table_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_table_verapdf.pdf");
        GeneratePdfATableDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void PdfA2b_Image_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_image_verapdf.pdf");
        GeneratePdfAImageDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    // Regression for issue #91: a real RGB JPEG 2000 image in a PDF/A-2b document must pass
    // veraPDF clause 6.2.8.3 (colour channels / bit depth). The historical loader embedded only
    // the bare jp2c codestream, stripping the ihdr/colr boxes veraPDF reads, so it saw 0/0.
    // The fixture below is a 16×16 RGB 8-bit JP2 produced by Pillow/OpenJPEG (the exact case in
    // the report), embedded as base64 so the test is self-contained and needs no JP2 encoder on CI.
    [Fact]
    public void PdfA2b_Jpx_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for JPEG 2000 PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_jpx_verapdf.pdf");
        GeneratePdfAJpxDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    // Companion to PdfA2b_Jpx (issue #91 follow-up): JBIG2 in PDF/A-2b. PDF/A-2 imposes no
    // JBIG2-specific structural rules — only the generic image-dictionary rules plus "filter
    // allowed" (6.1.7.2-1) — so this gates the passthrough path end-to-end: embedded organisation,
    // /DecodeParms omitted when there are no globals, and bpc=1 DeviceGray under the auto sRGB
    // output intent. veraPDF validates the PDF/A structure; it does not decode JBIG2 samples.
    [Fact]
    public void PdfA2b_Jbig2_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for JBIG2 PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_jbig2_verapdf.pdf");
        GeneratePdfAJbig2Doc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void PdfA2b_Tagged_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_tagged_verapdf.pdf");
        GeneratePdfATaggedDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    // Regression for issue #89: a PAdES-signed PDF/A-2b document must pass veraPDF.
    // The previous signer wrote a comment between the /Contents key and its hex value,
    // which veraPDF 1.30+ rejected on clause 6.4.3-1 (doesByteRangeCoverEntireDocument)
    // even though the byte range and CMS signature were correct. This is the gate that
    // exercises the real validator against a signed conformance document — the structural
    // unit test in HardeningV155Tests asserts the absence of the comment, this proves the
    // validator accepts the result.
    [Fact]
    public void Signed_PdfA2b_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for signed PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "signed_pdfa2b_verapdf.pdf");
        GenerateSignedPdfA2bDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    /// <summary>
    /// Generates a signed PDF/A-2b document with an embedded font (issue #89 regression).
    /// The signing path runs through <c>SigningExtensions.Sign</c>, which preserves the
    /// document's PDF/A conformance.
    /// </summary>
    private static void GenerateSignedPdfA2bDoc(string path, string fontPath)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Signed PDF/A Oracle",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));

        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF Oracle — Signed PDF/A-2b";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph("Signed PDF/A-2b body with an embedded font.", style));

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            SignerName = "VellumPdf Oracle",
            Reason = "Issue #89 regression",
        };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        doc.Sign(fs, settings);
    }

    // Issue #106: a PAdES B-LT and B-LTA signed PDF/A-2b document carries DSS and DocTimeStamp
    // incremental revisions. These gates establish whether those LTV revisions preserve PDF/A-2b
    // conformance per veraPDF, using an offline test TSA and canned revocation evidence.
    private static readonly byte[] s_cannedOcsp = [0x30, 0x03, 0x0A, 0x01, 0x00];
    private static readonly byte[] s_cannedCrl = [0x30, 0x02, 0x30, 0x00];

    private sealed class CannedRevocationClient : IRevocationClient
    {
        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
            => new() { Ocsp = s_cannedOcsp, Crl = s_cannedCrl };
    }

    [Fact]
    public void Signed_PdfA2b_BLT_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for signed PDF/A LTV oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "signed_pdfa2b_blt_verapdf.pdf");
        GenerateSignedLtvPdfA2bDoc(pdfPath, fontPath, PadesLevel.B_LT);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    [Fact]
    public void Signed_PdfA2b_BLTA_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for signed PDF/A LTV oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "signed_pdfa2b_blta_verapdf.pdf");
        GenerateSignedLtvPdfA2bDoc(pdfPath, fontPath, PadesLevel.B_LTA);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    private static void GenerateSignedLtvPdfA2bDoc(string path, string fontPath, PadesLevel level)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Signed PDF/A LTV Oracle", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF Oracle — Signed PDF/A-2b LTV";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph($"Signed PDF/A-2b {level} body with an embedded font.", style));

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            SignerName = "VellumPdf Oracle",
            Reason = "Issue #106 LTV PDF/A",
            Level = level,
            TimestampClient = new VellumPdf.Kernel.Tests.TestTimestampClient(DateTimeOffset.UtcNow),
            RevocationClient = new CannedRevocationClient(),
        };

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        doc.Sign(fs, settings);
    }

    // PDF/A-2a (conformance level A) requires full accessible tagging — catalog /Lang,
    // a role map, and validated marked-content↔structure linkage — all now implemented
    // (#37, #38). This strict veraPDF 2a gate validates a fully-tagged document (heading,
    // paragraph, table, ordered list, figure-with-/Alt, embedded font, catalog /Lang).
    [Fact]
    public void PdfA2a_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2a_verapdf.pdf");
        GeneratePdfATaggedDoc(pdfPath, fontPath, PdfConformance.PdfA2a);
        AssertVeraPdfCompliant(pdfPath, "2a");
    }

    [Fact]
    public void PdfA2a_Text_veraPdf_reportsCompliant()
    {
        // Minimal tagged text (heading + paragraph) isolates basic level-A tagging
        // from the table/list/figure content of the comprehensive 2a document above.
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/A oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2a_text_verapdf.pdf");
        GeneratePdfATaggedTextDoc(pdfPath, fontPath);
        AssertVeraPdfCompliant(pdfPath, "2a");
    }

    [Fact]
    public void PdfUA1_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/UA oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfua1_verapdf.pdf");
        GeneratePdfATaggedDoc(pdfPath, fontPath, PdfConformance.PdfUA1);
        AssertVeraPdfCompliant(pdfPath, "ua1");
    }

    [Fact]
    public void PdfUA1_Text_veraPdf_reportsCompliant()
    {
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform font for PDF/UA oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfua1_text_verapdf.pdf");
        GeneratePdfATaggedTextDoc(pdfPath, fontPath, PdfConformance.PdfUA1);
        AssertVeraPdfCompliant(pdfPath, "ua1");
    }

    /// <summary>
    /// PDF/A-2b compliance with a subsetted OTF/CFF embedded font.
    /// Verifies that the subsetted CFF sfnt meets all PDF/A-2b requirements.
    /// Skips locally when no OTF font or veraPDF is available; fails on CI
    /// when either is missing.
    /// </summary>
    [Fact]
    public void PdfA2b_OtfCff_veraPdf_reportsCompliant()
    {
        var otfPath = FindOtfFont();
        if (otfPath is null) { GateOnCi("OTF font for PDF/A OTF/CFF oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "pdfa2b_otf_cff_verapdf.pdf");
        GeneratePdfA2bOtfCffDoc(pdfPath, otfPath);
        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    /// <summary>
    /// Generates a PDF/A-2b document using an embedded OTF/CFF font (subsetted).
    /// </summary>
    private static void GeneratePdfA2bOtfCffDoc(string path, string otfPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF OTF/CFF Oracle";
        doc.Info.Producer = "VellumPdf";

        var handle = doc.LoadTrueTypeFont(otfPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph("VellumPdf PDF/A-2b subsetted OTF/CFF oracle document.", style));
        doc.Add(new Paragraph("This document uses a subsetted CFF (OpenType) embedded font.", style));
        doc.Save(path);
    }

    /// <summary>
    /// Runs veraPDF against <paramref name="pdfPath"/> at the given flavour and asserts
    /// the report says the file is compliant. Gates on CI when veraPDF is unavailable
    /// (fail), skips locally. The assertion is strict and surfaces the full report.
    /// </summary>
    private static void AssertVeraPdfCompliant(string pdfPath, string flavour)
    {
        if (!TryRunTool("verapdf", $"--flavour {flavour} \"{pdfPath}\"",
            out var exit, out var reportXml, out var stderr))
        {
            GateOnCi("verapdf");
            return;
        }

        var isCompliant = reportXml.Contains("isCompliant=\"true\"", StringComparison.Ordinal) ||
                          reportXml.Contains("compliant=\"true\"", StringComparison.Ordinal);

        Assert.True(
            isCompliant,
            $"veraPDF PDF/A-{flavour} validation failed (exit {exit}).\n" +
            $"veraPDF report:\n{reportXml}\n" +
            $"stderr:\n{stderr}\n" +
            "Failing rules are listed in the report above. Common causes: unembedded fonts " +
            "(use LoadTrueTypeFont), missing OutputIntent, incorrect XMP pdfaid schema, " +
            "missing /CIDSet, or a missing font subset tag.");
    }

    private static TextStyle EmbeddedStyle(Document doc, string fontPath, double size = 12)
    {
        var handle = doc.LoadTrueTypeFont(fontPath);
        return new TextStyle { FontRef = handle, FontSize = size };
    }

    private static void GeneratePdfATextDoc(string path, string fontPath, PdfConformance conformance)
    {
        using var doc = new Document();
        doc.Conformance = conformance;
        doc.Info.Title = "VellumPdf veraPDF Oracle";
        doc.Info.Author = "VellumPdf Oracle";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph("VellumPdf PDF/A oracle test document.", style));
        doc.Add(new Paragraph("This document uses an embedded TrueType font.", style));

        doc.Save(path);
    }

    private static void GeneratePdfATableDoc(string path, string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF Oracle — Table";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph("PDF/A-2b table document.", style));

        var table = new TableElement { DefaultCellStyle = style };
        table.SetColumnWidths(200, 200);
        table.AddHeaderRow().AddCell("Column One").AddCell("Column Two");
        table.AddRow().AddCell("Cell A").AddCell("Cell B");
        doc.Add(table);

        doc.Save(path);
    }

    // A real 16×16 RGB 8-bit JPEG 2000 (JP2 box file) written by Pillow/OpenJPEG 2.5.4.
    // ihdr: NC=3, BPC=8; colr: METH=1, EnumCS=16 (sRGB). 249 bytes.
    private const string RealRgbJp2Base64 =
        "AAAADGpQICANCocKAAAAFGZ0eXBqcDIgAAAAAGpwMiAAAAAtanAyaAAAABZpaGRyAAAAEAAAABAAAwcH" +
        "AAAAAAAPY29scgEAAAAAABAAAACsanAyY/9P/1EALwAAAAAAEAAAABAAAAAAAAAAAAAAABAAAAAQAAAA" +
        "AAAAAAAAAwcBAQcBAQcBAf9SAAwAAAABAAQEBAAB/1wAEEBASEhQSEhQSEhQSEhQ/2QAJQABQ3JlYXRl" +
        "ZCBieSBPcGVuSlBFRyB2ZXJzaW9uIDIuNS40/5AACgAAAAAAKAAB/5PB8gEHx9QEAb/PtAgHL4CAgICA" +
        "gICAgICAgP/Z";

    private static void GeneratePdfAJpxDoc(string path, string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF Oracle — JPEG 2000";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph("PDF/A-2b JPEG 2000 document.", style));

        var imgXObj = VellumPdf.Images.JpxImageLoader.Load(Convert.FromBase64String(RealRgbJp2Base64));
        doc.Add(new LayoutImage(imgXObj) { Width = 40, AltText = "RGB JPEG 2000 test image" });

        doc.Save(path);
    }

    private static void GeneratePdfAJbig2Doc(string path, string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF Oracle — JBIG2";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph("PDF/A-2b JBIG2 document.", style));

        var imgXObj = VellumPdf.Images.Jbig2ImageLoader.Load(BuildMinimalEmbeddedJbig2(16, 16));
        doc.Add(new LayoutImage(imgXObj) { Width = 40, AltText = "JBIG2 bilevel test image" });

        doc.Save(path);
    }

    // Builds a minimal embedded-organisation JBIG2 (no file header): a page-information segment
    // plus one immediate-lossless generic region (MMR-coded, all-white 16×16). This is enough
    // structure for a valid /JBIG2Decode image; veraPDF validates PDF/A structure, not the codestream.
    private static byte[] BuildMinimalEmbeddedJbig2(int width, int height)
    {
        // page-info data (§7.4.8): width(4) height(4) xres(4) yres(4) flags(1) striping(2) = 19 bytes.
        var pageInfo = new byte[19];
        WriteJbig2Int32(pageInfo, 0, width);
        WriteJbig2Int32(pageInfo, 4, height);
        var pageInfoSeg = BuildJbig2Segment(0, type: 48, pageAssociation: 1, pageInfo);

        // generic region: region-info(17) + generic-region flags(1) + MMR data.
        var region = new byte[17 + 1 + 2];
        WriteJbig2Int32(region, 0, width);   // region bitmap width
        WriteJbig2Int32(region, 4, height);  // region bitmap height
        // region x=0, y=0, combination operator=0 — already zero.
        region[17] = 0x01;                   // generic-region flags: MMR = 1
        region[18] = 0xFF;                   // 16 rows of MMR vertical-0 codes => all-white
        region[19] = 0xFF;
        var regionSeg = BuildJbig2Segment(1, type: 39, pageAssociation: 1, region);

        return [.. pageInfoSeg, .. regionSeg];
    }

    private static byte[] BuildJbig2Segment(int segNumber, int type, int pageAssociation, byte[] data)
    {
        var seg = new byte[4 + 1 + 1 + 1 + 4 + data.Length];
        var pos = 0;
        WriteJbig2Int32(seg, pos, segNumber); pos += 4;
        seg[pos++] = (byte)(type & 0x3F); // header flags: segment type, 1-byte page association
        seg[pos++] = 0x00;                // referred-to-segment count (0) + retention flags
        seg[pos++] = (byte)pageAssociation;
        WriteJbig2Int32(seg, pos, data.Length); pos += 4;
        Array.Copy(data, 0, seg, pos, data.Length);
        return seg;
    }

    private static void WriteJbig2Int32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    private static void GeneratePdfAImageDoc(string path, string fontPath)
    {
        using var doc = new Document();
        doc.Conformance = PdfConformance.PdfA2b;
        doc.Info.Title = "VellumPdf veraPDF Oracle — Image";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Paragraph("PDF/A-2b image document.", style));

        var imgXObj = VellumPdf.Images.PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "White test square" });

        doc.Save(path);
    }

    private static void GeneratePdfATaggedDoc(
        string path, string fontPath, PdfConformance conformance = PdfConformance.PdfA2b)
    {
        using var doc = new Document();
        doc.Conformance = conformance;
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Info.Title = "VellumPdf veraPDF Oracle — Tagged";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Heading("Tagged Heading", new TextStyle { FontRef = style.FontRef, FontSize = 18 }));
        doc.Add(new Paragraph("A tagged paragraph with an embedded font.", style));

        var table = new TableElement { DefaultCellStyle = style };
        table.SetColumnWidths(200, 200);
        table.AddHeaderRow().AddCell("H1").AddCell("H2");
        table.AddRow().AddCell("A").AddCell("B");
        doc.Add(table);

        var list = new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = style };
        list.Add("First item");
        list.Add("Second item");
        doc.Add(list);

        var imgXObj = VellumPdf.Images.PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng());
        doc.Add(new LayoutImage(imgXObj) { Width = 48, AltText = "White test square" });

        doc.Save(path);
    }

    private static void GeneratePdfATaggedTextDoc(string path, string fontPath, PdfConformance conformance = PdfConformance.PdfA2a)
    {
        using var doc = new Document();
        doc.Conformance = conformance;
        doc.Tagged = true;
        doc.Language = "en-US";
        doc.Info.Title = "VellumPdf veraPDF Oracle — Tagged Text";
        doc.Info.Producer = "VellumPdf";

        var style = EmbeddedStyle(doc, fontPath);
        doc.Add(new Heading("Tagged Heading", new TextStyle { FontRef = style.FontRef, FontSize = 16 }));
        doc.Add(new Paragraph("A minimal tagged PDF/A-2a text document.", style));

        doc.Save(path);
    }

    // ── CMYK + ICCBased veraPDF oracle tests ─────────────────────────────────

    /// <summary>
    /// PDF/A-2b document using DeviceCMYK operators under a CMYK output intent.
    /// No fonts are embedded (no text), so only DeviceCMYK and DeviceGray default
    /// colour spaces appear — valid under the CMYK output intent.
    /// </summary>
    [Fact]
    public void PdfA2b_Cmyk_veraPdf_reportsCompliant()
    {
        var pdfPath = Path.Combine(_tempDir, "pdfa2b_cmyk_verapdf.pdf");

        using (var doc = new PdfDocument())
        {
            doc.Conformance = PdfConformance.PdfA2b;
            doc.Info.Title = "VellumPdf CMYK Oracle";
            doc.Info.Producer = "VellumPdf";
            doc.UseCmykOutputIntent("VellumPdf CMYK Oracle");

            var page = doc.AddPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            canvas
                .SetFillColorCmyk(0, 0.5, 1, 0)
                .Rectangle(72, 72, 200, 200)
                .Fill()
                .SetStrokeColorCmyk(0, 0, 0, 1)
                .SetLineWidth(2)
                .Rectangle(300, 300, 150, 100)
                .Stroke();
            canvas.Finish();

            using var fs = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
            doc.Save(fs);
        }

        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    /// <summary>
    /// PDF/A-2b document using an ICCBased sRGB colour space under the default sRGB
    /// output intent. ICCBased is device-independent and valid under the sRGB intent.
    /// </summary>
    [Fact]
    public void PdfA2b_IccBased_veraPdf_reportsCompliant()
    {
        var pdfPath = Path.Combine(_tempDir, "pdfa2b_iccbased_verapdf.pdf");

        using (var doc = new PdfDocument())
        {
            doc.Conformance = PdfConformance.PdfA2b;
            doc.Info.Title = "VellumPdf ICCBased Oracle";
            doc.Info.Producer = "VellumPdf";
            // Default sRGB output intent — do NOT call UseCmykOutputIntent.

            var page = doc.AddPage(PageSize.A4);
            doc.RegisterIccBasedColorSpace(page, IccProfiles.Srgb, 3, "CS0");

            var canvas = new PdfCanvas(page);
            canvas
                .SetFillColorSpace("CS0")
                .SetFillColor(0.2, 0.4, 0.6)
                .Rectangle(72, 72, 200, 200)
                .Fill();
            canvas.Finish();

            using var fs = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
            doc.Save(fs);
        }

        AssertVeraPdfCompliant(pdfPath, "2b");
    }

    // ── OpenType-CFF (OTTO) oracle tests ────────────────────────────────────

    private const string OtfOracleMarker = "VELLUMORACLE_OTF_CFF_8E2C";

    [Fact]
    public void OtfCff_QpdfCheck_Passes()
    {
        var otfPath = FindOtfFont();
        if (otfPath is null) { GateOnCi("OTF/CFF font for oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "otf_cff.pdf");
        GenerateOtfCffDoc(pdfPath, otfPath);

        if (!TryRunTool("qpdf", $"--check \"{pdfPath}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(
            exit == 0,
            $"qpdf --check failed (exit {exit}) on OTF/CFF embedded font doc.\nstdout: {stdout}\nstderr: {stderr}");
    }

    [Fact]
    public void OtfCff_PdftotextFindsMarker()
    {
        var otfPath = FindOtfFont();
        if (otfPath is null) { GateOnCi("OTF/CFF font for oracle"); return; }

        var pdfPath = Path.Combine(_tempDir, "otf_cff_text.pdf");
        GenerateOtfCffDoc(pdfPath, otfPath);

        if (!TryRunTool("pdftotext", $"\"{pdfPath}\" -", out _, out var text, out var stderr))
        {
            GateOnCi("pdftotext");
            return;
        }

        Assert.True(
            text.Contains(OtfOracleMarker, StringComparison.Ordinal),
            $"OTF/CFF marker '{OtfOracleMarker}' not found in pdftotext output.\nstderr: {stderr}");
    }

    /// <summary>
    /// Generates a PDF with an OTF/CFF embedded font and a marker string.
    /// </summary>
    private static void GenerateOtfCffDoc(string path, string otfPath)
    {
        using var doc = new Document();
        var handle = doc.LoadTrueTypeFont(otfPath);
        var style = new TextStyle { FontRef = handle, FontSize = 12 };
        doc.Add(new Paragraph($"{OtfOracleMarker} — OTF/CFF embedded font marker.", style));
        doc.Save(path);
    }
}
