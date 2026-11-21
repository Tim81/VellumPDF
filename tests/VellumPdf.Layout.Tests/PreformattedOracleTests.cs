// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// External oracles for the preformatted element (#591): <c>pdftotext -fixed</c> reproduces the
/// columns, veraPDF checks the claims, and qpdf checks the syntax. Each test first asserts that the
/// saved file holds the change, so a verdict is never about a file that dodges the feature. Files go
/// in a directory made under the temp path, because veraPDF can run as another user.
/// </summary>
public sealed class PreformattedOracleTests : IDisposable
{
    private readonly string _dir;

    public PreformattedOracleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vellumpre_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort: the directory may be locked or gone */ }
        catch (UnauthorizedAccessException) { /* best-effort: a locked file on Windows */ }
    }

    private const string Source = "x234567890x234567890x23456789!\n\tingesprongen\tmet tabs\n    vier  spaties  dubbel\n\na\tb\tc";

    private static readonly string[] Expanded =
    [
        "x234567890x234567890x23456789!",
        "        ingesprongen    met tabs",
        "    vier  spaties  dubbel",
        "",
        "a       b       c",
    ];

    private string Write(Document doc, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Save(doc));
        return path;
    }

    // ── pdftotext -fixed ─────────────────────────────────────────────────────

    /// <summary>
    /// A 10 pt Courier or Liberation Mono column is 6 pt wide, so <c>-fixed 6</c> puts each glyph in
    /// its own column, after the 72 pt margin's 12. <c>-layout</c> is not an exact oracle: it
    /// collapsed a double space on the poppler this was measured on. Blank lines are not compared,
    /// because how many an extractor writes between text lines is its own choice.
    /// </summary>
    private void AssertColumns(string path)
    {
        ExternalTool.TryRun("pdftotext", ["-fixed", "6", "-enc", "UTF-8", path, "-"], out var exit, out var text, out var stderr, out var timedOut);
        Assert.False(timedOut, "pdftotext timed out, or its output could not be fully captured.");
        Assert.True(exit == 0, stderr);

        var lines = Regex.Split(text.Replace("\f", string.Empty, StringComparison.Ordinal), "\r?\n")
            .Where(l => l.Length > 0)
            .Select(l =>
            {
                Assert.StartsWith(Spaces(12), l, StringComparison.Ordinal);
                return l[12..].TrimEnd();
            })
            .ToArray();

        Assert.Equal(Expanded.Where(l => l.Length > 0), lines);
    }

    [Fact]
    public void Courier_pdftotextFixed_returnsTheExpandedColumns()
    {
        using var doc = new Document();
        doc.Add(new Preformatted(Source));
        var path = Write(doc, "courier.pdf");

        // The file holds the expanded line and no tab.
        var content = Assert.Single(PageContents(File.ReadAllBytes(path)));
        Assert.Contains("(        ingesprongen    met tabs) Tj", content, StringComparison.Ordinal);
        Assert.DoesNotContain('\t', content);

        AssertColumns(path);
    }

    [Fact]
    public void LiberationMono_pdftotextFixed_returnsTheExpandedColumns()
    {
        using var doc = new Document();
        doc.Add(new Preformatted(Source, MonoStyle(doc)));
        var path = Write(doc, "mono.pdf");

        var content = Assert.Single(PageContents(File.ReadAllBytes(path)));
        Assert.Contains(HexOf(MonoPath, Expanded[1]) + " Tj", content, StringComparison.Ordinal);

        AssertColumns(path);
    }

    // ── veraPDF and qpdf ─────────────────────────────────────────────────────

    private static string Digits(int n) => string.Concat(Enumerable.Range(0, n).Select(i => (char)('0' + i % 10)));

    private static string Body() =>
        Source + "\n" + Digits(160) + "\n" + string.Join('\n', Enumerable.Range(1, 80).Select(i => $"  line {i}\tend"));

    private static void AssertVeraPdfPasses(string path, string flavour)
    {
        ExternalTool.TryRun("verapdf", ["--flavour", flavour, path], out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut, "veraPDF timed out");
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");

        // The failed rules, by clause and test number, so a failure names them.
        var failed = Regex.Matches(report, @"<ruleSummary\b[^>]*\bstatus=""failed""[^>]*>")
            .Select(m => Regex.Match(m.Value, @"clause=""([^""]*)""").Groups[1].Value + "#" + Regex.Match(m.Value, @"testNumber=""([^""]*)""").Groups[1].Value)
            .ToArray();
        Assert.Empty(failed);
        Assert.True(
            report.Contains("isCompliant=\"true\"", StringComparison.Ordinal),
            $"veraPDF reported the document non-compliant (exit {exit}).\n{report}");
    }

    private static void AssertQpdfClean(string path)
    {
        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut, "qpdf --check timed out, or its output could not be fully captured.");
        Assert.True(
            stdout.Contains("No syntax or stream encoding errors found", StringComparison.Ordinal),
            $"qpdf --check did not report a clean verdict (exit {exit}).\nstdout: {stdout}\nstderr: {stderr}");
    }

    private static Document Claimed(PdfConformance claim, bool tagged)
    {
        var doc = new Document { Conformance = claim, Language = "nl-NL" };
        doc.Info.Title = "Preformatted";
        if (tagged)
            doc.Tagged = true;
        return doc;
    }

    [Fact]
    public void PdfA2u_withTabsAndWrappedLines_passesVeraPdf_andQpdf()
    {
        using var doc = Claimed(PdfConformance.PdfA2u, tagged: false);
        doc.Add(new Preformatted(Body(), MonoStyle(doc)));
        var path = Write(doc, "a2u.pdf");

        var pages = PageContents(File.ReadAllBytes(path));
        Assert.True(pages.Count >= 2, "the fixture must span pages");
        Assert.Contains("0.6 w", string.Concat(pages), StringComparison.Ordinal);
        Assert.Contains(HexOf(MonoPath, Expanded[1]) + " Tj", pages[0], StringComparison.Ordinal);

        AssertVeraPdfPasses(path, "2u");
        AssertQpdfClean(path);
    }

    [Fact]
    public void PdfA2a_withTabsAndWrappedLines_passesVeraPdf()
    {
        using var doc = Claimed(PdfConformance.PdfA2a, tagged: true);
        doc.Add(new Preformatted(Body(), MonoStyle(doc)));
        var path = Write(doc, "a2a.pdf");

        Assert.StartsWith("/P <</MCID 0>> BDC\n", PageContents(File.ReadAllBytes(path))[0], StringComparison.Ordinal);

        AssertVeraPdfPasses(path, "2a");
    }

    [Fact]
    public void PdfUA1_withTabsAndWrappedLines_passesVeraPdf_andQpdf()
    {
        using var doc = Claimed(PdfConformance.PdfUA1, tagged: true);
        doc.Add(new Preformatted(Body(), MonoStyle(doc)));
        var path = Write(doc, "ua1.pdf");

        var pages = PageContents(File.ReadAllBytes(path));
        Assert.Contains("/Artifact BMC\nq\n", pages[0], StringComparison.Ordinal);

        AssertVeraPdfPasses(path, "ua1");
        AssertQpdfClean(path);
    }

    // The ceiling is 1e30 (Preformatted, LargestFontSize). The marker's x is about 1.08 times the
    // size for a segment of one cluster, so this puts a number near 1e30 into the Tf operand, the
    // baseline and the marker path, and veraPDF checks each against 6.1.13-2.
    [Fact]
    public void PdfA2u_atTheLargestFontSize_passesVeraPdf()
    {
        using var doc = Claimed(PdfConformance.PdfA2u, tagged: false);
        var style = new TextStyle { FontRef = doc.LoadTrueTypeFont(MonoPath), FontSize = 1e30, Leading = 12 };
        doc.Add(new Preformatted("ab", style));
        var path = Write(doc, "a2u-max.pdf");

        var content = Assert.Single(PageContents(File.ReadAllBytes(path)));
        Assert.Matches(@"/\w+ 1(0{30})(\.0+)? Tf", content);
        Assert.Contains(" w\n", content, StringComparison.Ordinal);
        Assert.Contains(" l\n", content, StringComparison.Ordinal);

        AssertVeraPdfPasses(path, "2u");
    }
}
