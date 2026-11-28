// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.NoteBlockSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// External oracles for a running band of three zones (#365): veraPDF checks the PDF/A-2u and PDF/UA-1
/// claims, qpdf the syntax and pdftotext what a reader extracts. Each test first asserts that the
/// saved file holds the header's three zones, its logo and its rule, so a verdict is never about a
/// file that dodges the feature. Files go in a directory made under the temp path, because veraPDF
/// can run as another user.
/// </summary>
public sealed class BandZonesOracleTests : IDisposable
{
    private readonly string _dir;

    public BandZonesOracleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vellumband_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort: the directory may be locked or gone */ }
        catch (UnauthorizedAccessException) { /* best-effort: a locked file on Windows */ }
    }

    private static readonly string BoldPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Bold.ttf");

    // Three pages of 300 x 200 pt, one chapter each. The header holds a logo on the left, a chapter
    // title and a bold label in the middle, and the page number on the right, under a rule; the
    // footer holds a count on the right and a rule above it. The right zones swap on even pages.
    private static Document Build(PdfConformance claim, bool tagged)
    {
        var doc = NewDoc(300, 200);
        doc.Conformance = claim;
        doc.Language = "en-US";
        doc.Info.Title = "Band zones";
        doc.MirrorMargins = true;
        if (tagged)
            doc.Tagged = true;

        var body = new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 };
        var bold = new TextStyle { FontRef = doc.LoadTrueTypeFont(BoldPath), FontSize = 10 };
        var logo = LayoutImage.FromBytes(ImageFixtures.Png());

        doc.Header = new RunningBand(
            BandZone.Logo(logo),
            new BandZone([new TextRun("Chapter: ", bold), new TextRun("{chapter}", body)]),
            new BandZone("Page {page}", body))
        {
            Height = 20,
            ChapterToken = true,
            MirrorZones = true,
            Rule = new BandRule(BandRulePosition.Below, 0.75, new ColorRgb(0.2, 0.2, 0.6)),
        };
        doc.Footer = new RunningBand(BandZone.Empty, BandZone.Empty, new BandZone("of {pages}", body))
        {
            Rule = new BandRule(BandRulePosition.Above, 0.5),
        };

        for (var i = 1; i <= 3; i++)
        {
            doc.Add(new Heading($"Chapter {i}", body) { Level = 0, BreakBefore = PageBreakMode.Page });
            doc.Add(new Paragraph("The body of the chapter, which sits between the bands.", body));
        }

        return doc;
    }

    private string Write(Document doc, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Save(doc));
        return path;
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void AssertTheFileHoldsTheBand(string path, bool tagged)
    {
        var pages = PageContents(File.ReadAllBytes(path));
        Assert.Equal(3, pages.Count);
        foreach (var page in pages)
        {
            // The logo, 2 x 2 pixels, is the band's 20 pt less the rule's 0.75 pt and 1 pt of clearance:
            // 18.25 pt each way, ending 1.75 pt above the band's bottom edge at 160.
            Assert.Matches(@"(?m)^18\.25 0 0 18\.25 \S+ 161\.75 cm$", page);
            Assert.Contains("0.2 0.2 0.6 RG\n0.75 w\n", page, StringComparison.Ordinal);
            Assert.Contains("0 0 0 RG\n0.5 w\n", page, StringComparison.Ordinal);

            // The label run, the title run and the page number stand on one baseline above the body,
            // and the count on the footer's below it. An embedded font shows its text as hex, so the
            // placements are what is counted here.
            var baselines = TmPositions(page)
                .Select(p => double.Parse(p.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture))
                .ToList();
            var header = baselines.Where(y => y > 160).ToList();
            Assert.Equal(3, header.Count);
            Assert.Single(header.Distinct());
            Assert.Single(baselines, y => y < 30);
            if (tagged)
                Assert.Contains("/Artifact BMC", page, StringComparison.Ordinal);
        }

        if (tagged)
            Assert.DoesNotContain("/Figure", Encoding.Latin1.GetString(File.ReadAllBytes(path)), StringComparison.Ordinal);
    }

    private static void AssertVeraPdfPasses(string path, string flavour)
    {
        ExternalTool.TryRun("verapdf", ["--flavour", flavour, path], out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut, "veraPDF timed out");
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");

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

    [Fact]
    public void PdfA2u_withAThreeZoneHeader_aLogoAndARule_passesVeraPdf_andQpdf()
    {
        using var doc = Build(PdfConformance.PdfA2u, tagged: false);
        var path = Write(doc, "a2u.pdf");

        AssertTheFileHoldsTheBand(path, tagged: false);
        AssertVeraPdfPasses(path, "2u");
        AssertQpdfClean(path);
    }

    [Fact]
    public void PdfUA1_withAThreeZoneHeader_aLogoAndARule_passesVeraPdf_andQpdf()
    {
        using var doc = Build(PdfConformance.PdfUA1, tagged: true);
        var path = Write(doc, "ua1.pdf");

        AssertTheFileHoldsTheBand(path, tagged: true);
        AssertVeraPdfPasses(path, "ua1");
        AssertQpdfClean(path);
    }

    [Fact]
    public void Pdftotext_readsTheZonesOnEveryPage_withTheTokensResolved()
    {
        using var doc = Build(PdfConformance.None, tagged: true);
        var path = Write(doc, "plain.pdf");

        AssertTheFileHoldsTheBand(path, tagged: true);
        AssertQpdfClean(path);
        for (var page = 1; page <= 3; page++)
        {
            var n = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
            ExternalTool.TryRun("pdftotext", ["-enc", "UTF-8", "-layout", "-f", n, "-l", n, path, "-"], out var exit, out var text, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, stderr);
            var flat = Regex.Replace(text, @"\s+", " ");

            // Chapter k stands on page k; the page number and the count are the page's and the document's.
            Assert.Contains($"Chapter: Chapter {n}", flat, StringComparison.Ordinal);
            Assert.Contains($"Page {n}", flat, StringComparison.Ordinal);
            Assert.Contains("of 3", flat, StringComparison.Ordinal);
        }
    }
}
