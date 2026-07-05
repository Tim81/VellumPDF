// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// External-decoder oracle: renders each symbology to a PDF, rasterizes it with
/// <c>pdftoppm</c> (poppler-utils) at 300 dpi, and decodes the image with zxing-cpp
/// (<c>eng/barcode-decode.py</c>), asserting the round-tripped format and content.
///
/// <para>
/// Mirrors the <c>TryRunTool</c>/<c>GateOnCi</c> pattern in
/// <c>VellumPdf.Layout.Tests.PdfValidatorOracleTests</c>: a missing tool skips silently on a
/// local dev machine, but fails the build on CI (<c>CI</c>/<c>GITHUB_ACTIONS</c>) or when
/// <c>REQUIRE_BARCODE_ORACLE=1</c> is set, so the decode oracle can never silently pass
/// vacuously. <c>python</c> is tried first, then <c>python3</c> (Windows has no
/// <c>python3</c> alias); a distinct exit code (3) from the script means zxing-cpp/Pillow is
/// not installed, which gates the same way as a missing executable.
/// </para>
/// </summary>
public sealed class ZxingDecodeOracleTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _scriptPath;

    public ZxingDecodeOracleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumbarcodeoracle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "barcode-decode.py");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup — temp dir may already be gone */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup — locked file on Windows */ }
    }

    private readonly record struct DecodeResult(string Format, string ContentType, string Text);

    // ── QR ────────────────────────────────────────────────────────────────

    [Fact]
    public void QrCode_AsciiContent_RoundTrips()
    {
        const string content = "VellumPdf QR oracle test";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new QrCode(content) { ModuleSize = 4 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("QRCode", result.Format);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public void QrCode_Utf8AutoEncoding_RoundTripsExactly()
    {
        const string content = "Grüße 😀";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new QrCode(content) { ModuleSize = 4, TextEncoding = QrTextEncoding.Auto }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("QRCode", result.Format);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public void QrCode_ForcedVersion10ErrorCorrectionH_RoundTrips()
    {
        const string content = "VellumPdf forced version 10, EC level H";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(
                new QrCode(content) { Version = 10, ErrorCorrection = QrErrorCorrection.H, ModuleSize = 3 }, 50, 400));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("QRCode", result.Format);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public void QrCode_TargetWidthScaled_StillDecodes()
    {
        const string content = "VellumPdf scaled QR";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new QrCode(content) { TargetWidth = 120 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("QRCode", result.Format);
        Assert.Equal(content, result.Text);
    }

    // ── Micro QR ──────────────────────────────────────────────────────────

    [Fact]
    public void MicroQrCode_M4_RoundTrips()
    {
        const string content = "VellumPdf M4";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new MicroQrCode(content) { Version = 4, ModuleSize = 6 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("MicroQRCode", result.Format);
        Assert.Equal(content, result.Text);
    }

    // ── PDF417 ────────────────────────────────────────────────────────────

    [Fact]
    public void Pdf417Barcode_Text_RoundTrips()
    {
        const string content = "VellumPdf PDF417 oracle round-trip test";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new Pdf417Barcode(content) { ModuleSize = 2 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("PDF417", result.Format);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public void Pdf417Barcode_BinaryBytes_RoundTrips()
    {
        byte[] content = [0x00, 0x01, 0x02, 0xFF, 0xFE, 0x7F, 0x80, 0x10, 0x20, 0x30];
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new Pdf417Barcode(content) { ModuleSize = 2 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("PDF417", result.Format);
        Assert.Equal("Binary", result.ContentType);
        Assert.Equal(Convert.ToHexStringLower(content), result.Text);
    }

    // ── Code 128 ──────────────────────────────────────────────────────────

    [Fact]
    public void Code128Barcode_Plain_RoundTrips()
    {
        const string content = "VELLUM-CODE128";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new Code128Barcode(content) { ShowText = false, ModuleSize = 2 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("Code128", result.Format);
        Assert.Equal(content, result.Text);
    }

    [Fact]
    public void Code128Barcode_Gs1_DecodesAsGs1ContentType()
    {
        // AI(01) + a 14-digit GTIN-like payload. The Code128 encoder does not validate GS1
        // application-identifier structure, so only the FNC1-after-start-code marker matters
        // for the decoder to recognise this as a GS1-128 symbol.
        const string content = "0100012345678905";
        var pdfPath = BuildSinglePdf((_, canvas) =>
            canvas.DrawBarcode(new Code128Barcode(content) { Gs1 = true, ShowText = false, ModuleSize = 2 }, 50, 500));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("Code128", result.Format);
        Assert.Equal("GS1", result.ContentType);
    }

    // ── EAN / UPC / ITF ───────────────────────────────────────────────────

    [Fact]
    public void EanBarcode_Ean13_RoundTrips()
    {
        var barcode = new EanBarcode(EanSymbology.Ean13, "400638133393");
        var pdfPath = BuildSinglePdf((doc, canvas) =>
            canvas.DrawBarcode(barcode, 50, 500, doc.UseFont(Standard14.Helvetica)));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("EAN13", result.Format);
        Assert.Equal(barcode.Digits, result.Text);
    }

    [Fact]
    public void EanBarcode_Ean8_RoundTrips()
    {
        var barcode = new EanBarcode(EanSymbology.Ean8, "1234567");
        var pdfPath = BuildSinglePdf((doc, canvas) =>
            canvas.DrawBarcode(barcode, 50, 500, doc.UseFont(Standard14.Helvetica)));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.Equal("EAN8", result.Format);
        Assert.Equal(barcode.Digits, result.Text);
    }

    [Fact]
    public void EanBarcode_UpcA_RoundTrips()
    {
        var barcode = new EanBarcode(EanSymbology.UpcA, "03600029145");
        var pdfPath = BuildSinglePdf((doc, canvas) =>
            canvas.DrawBarcode(barcode, 50, 500, doc.UseFont(Standard14.Helvetica)));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        // A UPC-A symbol is physically an EAN-13 symbol with an implicit leading '0' (that is how
        // this encoder draws it), and zxing-cpp's default (unrestricted) format list reports it as
        // EAN13 with that leading zero folded into the text. Restricting the decode to UPCA only
        // changes the reported format name, not the 13-digit text; both are the same symbol.
        Assert.True(result.Format is "EAN13" or "UPCA", $"Unexpected format '{result.Format}'.");
        Assert.Equal("0" + barcode.Digits, result.Text);
    }

    [Fact]
    public void EanBarcode_Ean13WithAddOn_MainDigitsExact_AddOnTolerant()
    {
        var barcode = new EanBarcode(EanSymbology.Ean13, "400638133393") { AddOn = "12345" };
        var pdfPath = BuildSinglePdf((doc, canvas) =>
            canvas.DrawBarcode(barcode, 50, 500, doc.UseFont(Standard14.Helvetica)));

        if (!TryDecodeAll(pdfPath, out var results)) return;

        var main = results.Find(r => r.Format is "EAN13" or "EANUPC");
        Assert.True(main.Format is not null,
            $"No EAN-13 result found among: {string.Join(", ", results.Select(r => r.Format))}");

        // The add-on's presentation differs across zxing-cpp versions: appended to the main
        // text (with or without a separating space) in some, a distinct EAN-5 result in
        // others. Only the main 13 digits are asserted strictly; CI pins the exact version.
        var normalized = main.Text.Replace(" ", "");
        Assert.StartsWith(barcode.Digits, normalized, StringComparison.Ordinal);
    }

    [Fact]
    public void Itf14Barcode_RoundTrips()
    {
        var barcode = new Itf14Barcode("1234567890123");
        var pdfPath = BuildSinglePdf((doc, canvas) =>
            canvas.DrawBarcode(barcode, 50, 500, doc.UseFont(Standard14.Helvetica)));

        if (!TryDecodeSingle(pdfPath, out var result)) return;

        Assert.True(result.Format is "ITF14" or "ITF", $"Unexpected format '{result.Format}'.");
        Assert.Equal(barcode.Digits, result.Text);
    }

    // ── Multi-symbol page ─────────────────────────────────────────────────

    [Fact]
    public void MultiSymbolPage_DecodesAllSymbols()
    {
        var pdfPath = BuildSinglePdf((doc, canvas) =>
        {
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.DrawBarcode(new QrCode("MULTI-QR") { ModuleSize = 3 }, 50, 700);
            canvas.DrawBarcode(new Code128Barcode("MULTI128") { ShowText = false, ModuleSize = 2 }, 320, 700);
            canvas.DrawBarcode(new EanBarcode(EanSymbology.Ean13, "400638133393"), 50, 550, font);
            canvas.DrawBarcode(new Itf14Barcode("1234567890123") { ShowText = false }, 320, 550);
        });

        if (!TryDecodeAll(pdfPath, out var results)) return;

        Assert.Equal(4, results.Count);
        Assert.Contains(results, r => r.Format == "QRCode" && r.Text == "MULTI-QR");
        Assert.Contains(results, r => r.Format == "Code128" && r.Text == "MULTI128");
        Assert.Contains(results, r => r.Format == "EAN13" && r.Text == "4006381333931");
        Assert.Contains(results, r => r.Format is "ITF14" or "ITF");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private string BuildSinglePdf(Action<PdfDocument, PdfCanvas> draw)
    {
        var pdfPath = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.pdf");
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);
        var canvas = new PdfCanvas(page);
        draw(doc, canvas);
        canvas.Finish();

        using var fs = new FileStream(pdfPath, FileMode.Create, FileAccess.Write, FileShare.None);
        doc.Save(fs);
        return pdfPath;
    }

    /// <summary>Runs the full pipeline for a PDF expected to contain exactly one barcode.</summary>
    private bool TryDecodeSingle(string pdfPath, out DecodeResult result)
    {
        if (!TryDecodeAll(pdfPath, out var results))
        {
            result = default;
            return false;
        }

        Assert.Single(results);
        result = results[0];
        return true;
    }

    /// <summary>
    /// Rasterizes <paramref name="pdfPath"/> with pdftoppm, then decodes it with the zxing-cpp
    /// oracle script. Returns <c>false</c> (after gating on CI) when either tool is unavailable.
    /// </summary>
    private bool TryDecodeAll(string pdfPath, out List<DecodeResult> results)
    {
        results = [];

        var pngBase = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(pdfPath));
        if (!TryRunTool("pdftoppm", $"-r 300 -png -singlefile \"{pdfPath}\" \"{pngBase}\"",
                out var ppmExit, out _, out var ppmStderr)
            || ppmExit != 0)
        {
            GateOnCi("pdftoppm");
            return false;
        }

        var pngPath = pngBase + ".png";
        Assert.True(File.Exists(pngPath), $"pdftoppm did not produce '{pngPath}'.\nstderr: {ppmStderr}");

        if (!TryRunPythonScript(pngPath, out var exit, out var stdout, out var stderr, out var missingTool))
        {
            GateOnCi(missingTool);
            return false;
        }

        Assert.True(exit == 0, $"barcode-decode.py failed (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            Assert.True(parts.Length == 3, $"Unexpected decode-oracle output line: '{line}'");
            results.Add(new DecodeResult(parts[0], parts[1], parts[2]));
        }

        return true;
    }

    /// <summary>
    /// Runs <c>eng/barcode-decode.py</c> against <paramref name="imagePath"/>, trying
    /// <c>python</c> then <c>python3</c>. An exit code of 3 (or neither interpreter being
    /// launchable) counts as the "zxing-cpp"/"python" tool being missing respectively.
    /// </summary>
    private bool TryRunPythonScript(
        string imagePath, out int exitCode, out string stdout, out string stderr, out string missingTool)
    {
        foreach (var python in new[] { "python", "python3" })
        {
            if (TryRunTool(python, $"\"{_scriptPath}\" \"{imagePath}\"", out exitCode, out stdout, out stderr))
            {
                if (exitCode == 3)
                {
                    missingTool = "zxing-cpp";
                    return false;
                }

                missingTool = string.Empty;
                return true;
            }
        }

        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;
        missingTool = "python";
        return false;
    }

    /// <summary>Locates the repository root by walking up from the test assembly's directory to find <c>VellumPdf.slnx</c>.</summary>
    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VellumPdf.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate VellumPdf.slnx by walking up from AppContext.BaseDirectory.");
    }

    /// <summary>
    /// Attempts to run an external CLI tool and captures its output. Returns <c>false</c> if
    /// the process cannot be started (tool not installed). Mirrors
    /// <c>PdfValidatorOracleTests.TryRunTool</c>, except output is decoded as UTF-8: decoded
    /// barcode text can carry arbitrary Unicode, and the decode script writes UTF-8 explicitly
    /// (see <c>eng/barcode-decode.py</c>), which does not match the console's default codepage
    /// on Windows.
    /// </summary>
    private static bool TryRunTool(string exe, string args, out int exitCode, out string stdout, out string stderr)
    {
        exitCode = -1;
        stdout = string.Empty;
        stderr = string.Empty;

        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
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
    /// Asserts failure when a required external tool is absent and either CI is detected
    /// (<c>CI</c>/<c>GITHUB_ACTIONS</c>) or <c>REQUIRE_BARCODE_ORACLE=1</c> is set. On a local
    /// dev machine without that override, this method does nothing (skip silently).
    /// </summary>
    private static void GateOnCi(string toolName)
    {
        var isCI = string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase);
        var isGitHubActions = string.Equals(
            Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.OrdinalIgnoreCase);
        var requireOracle = Environment.GetEnvironmentVariable("REQUIRE_BARCODE_ORACLE") == "1";

        if (isCI || isGitHubActions || requireOracle)
        {
            Assert.Fail(
                $"Required external tool '{toolName}' is not available. Ensure it is installed " +
                "(pdftoppm from poppler-utils; zxing-cpp via `pip install zxing-cpp==3.0.0 pillow`).");
        }

        // Local dev without the override: tool not installed, silently skip.
    }
}
