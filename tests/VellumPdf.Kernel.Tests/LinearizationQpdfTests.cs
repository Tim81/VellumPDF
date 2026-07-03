// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Forms;

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

    [Fact]
    public void Linearized_EqualLengthPages_QpdfClean()
    {
        // Byte-identical pages give equal page lengths and object counts, so several hint-table
        // delta columns collapse to zero width. qpdf must still accept the result.
        var path = Path.Combine(_tempDir, "linearized_equal.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };
        for (var i = 0; i < 3; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Same").EndText();
            canvas.Finish();
        }
        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
    }

    [Fact]
    public void Linearized_EmbeddedFontSharedAcrossPages_QpdfClean()
    {
        // Exercises the deepest remap chain (FontFile2 → FontDescriptor → CIDFont → Type0 → ToUnicode)
        // and a genuinely shared object (one embedded font used on every page).
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform TrueType font"); return; }

        var path = Path.Combine(_tempDir, "linearized_embfont.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };
        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));
        for (var i = 0; i < 4; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            doc.RegisterEmbeddedFontUsage(page, handle);
            var canvas = new PdfCanvas(page);
            canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
            var text = $"Embedded font page {i + 1}";
            var gids = new ushort[text.Length];
            var count = handle.GetGlyphIds(text, gids);
            canvas.ShowGlyphs(gids.AsSpan(0, count));
            canvas.EndText();
            canvas.Finish();
        }
        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
    }

    [Fact]
    public void Linearized_ObjectSharedAmongLaterPagesOnly_QpdfClean()
    {
        // A font used only on pages 2+ (not the first page) is shared among later pages -> part 8,
        // so nshared_total > nshared_first_page and the shared-object table's first_shared_obj must
        // be the real object number, not 0. Regression for a hint-table mismatch qpdf flagged.
        var fontPath = FindPlatformFont();
        if (fontPath is null) { GateOnCi("platform TrueType font"); return; }

        var path = Path.Combine(_tempDir, "linearized_part8.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };

        var first = doc.AddPage(PageSize.A4);
        var c0 = new PdfCanvas(first);
        var helv = doc.UseFont(Standard14.Helvetica);
        c0.BeginText().SetFont(helv, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("First page, Helvetica only").EndText();
        c0.Finish();

        var handle = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath));
        for (var i = 1; i < 3; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            doc.RegisterEmbeddedFontUsage(page, handle);
            var canvas = new PdfCanvas(page);
            canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
            var text = $"Shared embedded font, page {i + 1}";
            var gids = new ushort[text.Length];
            var count = handle.GetGlyphIds(text, gids);
            canvas.ShowGlyphs(gids.AsSpan(0, count));
            canvas.EndText();
            canvas.Finish();
        }
        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
    }

    [Fact]
    public void Linearized_WithOutlines_QpdfClean()
    {
        var path = Path.Combine(_tempDir, "linearized_outlines.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };

        var p0 = doc.AddPage(PageSize.A4);
        var c0 = new PdfCanvas(p0);
        var f0 = doc.UseFont(Standard14.Helvetica);
        c0.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 1").EndText();
        c0.Finish();

        var p1 = doc.AddPage(PageSize.A4);
        var c1 = new PdfCanvas(p1);
        c1.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 2").EndText();
        c1.Finish();

        var p2 = doc.AddPage(PageSize.A4);
        var c2 = new PdfCanvas(p2);
        c2.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 3").EndText();
        c2.Finish();

        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Chapter 1", DestPage = p0 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Section 1.1", DestPage = p1, Level = 1 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Chapter 2", DestPage = p2 });

        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
        Assert.DoesNotContain("WARNING", stderr);

        if (!TryRunQpdf($"--check \"{path}\"", out var checkExit, out var checkOut, out var checkErr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(checkExit == 0,
            $"qpdf --check failed (exit {checkExit}).\nstdout: {checkOut}\nstderr: {checkErr}");
        Assert.DoesNotContain("WARNING", checkOut);
        Assert.DoesNotContain("WARNING", checkErr);
    }

    [Fact]
    public void Linearized_WithTextAndCheckBoxFields_QpdfClean()
    {
        var path = Path.Combine(_tempDir, "linearized_fields.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };

        var p0 = doc.AddPage(PageSize.A4);
        var c0 = new PdfCanvas(p0);
        var f0 = doc.UseFont(Standard14.Helvetica);
        c0.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 1").EndText();
        c0.Finish();
        doc.AddTextField(p0, "Name", new PdfRectangle(72, 650, 300, 670));
        doc.AddCheckBox(p0, "Accept", new PdfRectangle(72, 620, 90, 638));

        var p1 = doc.AddPage(PageSize.A4);
        var c1 = new PdfCanvas(p1);
        c1.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 2").EndText();
        c1.Finish();
        doc.AddTextField(p1, "Email", new PdfRectangle(72, 650, 300, 670));

        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
        Assert.DoesNotContain("WARNING", stderr);

        if (!TryRunQpdf($"--check \"{path}\"", out var checkExit, out var checkOut, out var checkErr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(checkExit == 0,
            $"qpdf --check failed (exit {checkExit}).\nstdout: {checkOut}\nstderr: {checkErr}");
        Assert.DoesNotContain("WARNING", checkOut);
        Assert.DoesNotContain("WARNING", checkErr);
    }

    [Fact]
    public void Linearized_WithRadioGroupAcrossPages_QpdfClean()
    {
        var path = Path.Combine(_tempDir, "linearized_radio.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };

        var p0 = doc.AddPage(PageSize.A4);
        var c0 = new PdfCanvas(p0);
        var f0 = doc.UseFont(Standard14.Helvetica);
        c0.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 1").EndText();
        c0.Finish();

        var p1 = doc.AddPage(PageSize.A4);
        var c1 = new PdfCanvas(p1);
        c1.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 2").EndText();
        c1.Finish();

        doc.AddRadioButtonGroup("Choice", new List<RadioOption>
        {
            new(p0, new PdfRectangle(72, 650, 90, 668), "A"),
            new(p1, new PdfRectangle(72, 650, 90, 668), "B"),
        });

        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
        Assert.DoesNotContain("WARNING", stderr);

        if (!TryRunQpdf($"--check \"{path}\"", out var checkExit, out var checkOut, out var checkErr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(checkExit == 0,
            $"qpdf --check failed (exit {checkExit}).\nstdout: {checkOut}\nstderr: {checkErr}");
        Assert.DoesNotContain("WARNING", checkOut);
        Assert.DoesNotContain("WARNING", checkErr);
    }

    [Fact]
    public void Linearized_WithOutlinesAndForms_QpdfClean()
    {
        var path = Path.Combine(_tempDir, "linearized_outlines_and_forms.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };

        var p0 = doc.AddPage(PageSize.A4);
        var c0 = new PdfCanvas(p0);
        var f0 = doc.UseFont(Standard14.Helvetica);
        c0.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 1").EndText();
        c0.Finish();
        doc.AddTextField(p0, "Field1", new PdfRectangle(72, 650, 300, 670));

        var p1 = doc.AddPage(PageSize.A4);
        var c1 = new PdfCanvas(p1);
        c1.BeginText().SetFont(f0, 12).SetTextMatrix(1, 0, 0, 1, 72, 720).ShowText("Page 2").EndText();
        c1.Finish();

        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Start", DestPage = p0 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Page 2", DestPage = p1 });

        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }

        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
        Assert.DoesNotContain("WARNING", stderr);

        if (!TryRunQpdf($"--check \"{path}\"", out var checkExit, out var checkOut, out var checkErr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(checkExit == 0,
            $"qpdf --check failed (exit {checkExit}).\nstdout: {checkOut}\nstderr: {checkErr}");
        Assert.DoesNotContain("WARNING", checkOut);
        Assert.DoesNotContain("WARNING", checkErr);
    }

    [Fact]
    public void Linearized_Tagged_QpdfClean()
    {
        var path = Path.Combine(_tempDir, "linearized_tagged.pdf");
        using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = true };
        doc.Tagged = true;

        var font = doc.UseFont(Standard14.Helvetica);
        for (var i = 0; i < 3; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var mcid = canvas.BeginMarkedContent("P");
            canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
                .ShowText($"Page {i + 1}").EndText();
            canvas.EndMarkedContent();
            canvas.Finish();
            doc.RegisterStructElem(new PdfStructElem("P") { Page = page, Mcid = mcid });
        }

        using (var fs = File.OpenWrite(path)) doc.Save(fs);

        if (!TryRunQpdf($"--show-linearization \"{path}\"", out var exit, out var stdout, out var stderr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(exit == 0, $"exit {exit}.\n{stdout}\n{stderr}");
        Assert.DoesNotContain("WARNING", stdout);
        Assert.DoesNotContain("WARNING", stderr);

        if (!TryRunQpdf($"--check \"{path}\"", out var checkExit, out var checkOut, out var checkErr))
        {
            GateOnCi("qpdf");
            return;
        }
        Assert.True(checkExit == 0,
            $"qpdf --check failed (exit {checkExit}).\nstdout: {checkOut}\nstderr: {checkErr}");
        Assert.DoesNotContain("WARNING", checkOut);
        Assert.DoesNotContain("WARNING", checkErr);
    }

    private static string? FindPlatformFont()
    {
        string[] candidates =
        [
            @"C:\Windows\Fonts\arial.ttf",
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf",
        ];
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        return null;
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
