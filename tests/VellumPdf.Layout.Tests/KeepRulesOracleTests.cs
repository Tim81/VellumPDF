// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #306 against the external tools: the pages an independent text extractor reports, qpdf's
/// structural check, and veraPDF on a PDF/A-2u document whose every heading keeps with its text.
/// </summary>
public sealed class KeepRulesOracleTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vellumkeep_{Guid.NewGuid():N}");

    public KeepRulesOracleTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    // A handbook: twelve chapters, each a heading and two paragraphs, on a page small enough that
    // a heading meets a page foot several times. Embedded font, PDF/A-2u.
    private static Document Handbook(bool keep)
    {
        var fontPath = Path.Combine(FontReportSupport.FontsDir, "DejaVuSans.ttf");
        if (!File.Exists(fontPath))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");

        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 220),
            Margins = new EdgeInsets(24),
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Conformance = PdfConformance.PdfA2u,
        };
        doc.Info.Title = "Keep rules";
        doc.Language = "en-US";
        doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath)), FontSize = 11 };
        for (var chapter = 1; chapter <= 12; chapter++)
        {
            var number = chapter.ToString("00", CultureInfo.InvariantCulture);
            doc.Add(new Heading("Chapter" + number, style) { Level = 0, KeepWithNext = keep });
            doc.Add(new Paragraph(
                string.Join(' ', Enumerable.Range(1, 22).Select(i => $"c{number}w{i:00}")), style)
            {
                MinLinesBeforeBreak = keep ? 2 : 1,
                MinLinesAfterBreak = keep ? 2 : 1,
            });
            doc.Add(new Paragraph(string.Join(' ', Enumerable.Range(1, 9).Select(i => $"c{number}v{i:00}")), style));
        }

        return doc;
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private string Write(byte[] pdf, string name)
    {
        var path = Path.Combine(_tempDir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    private static string PageText(string path, int page)
    {
        var number = page.ToString(CultureInfo.InvariantCulture);
        if (!ExternalTool.TryRun("pdftotext", ["-layout", "-f", number, "-l", number, path, "-"],
                out var exit, out var text, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdftotext");
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        return text;
    }

    // The non-empty lines of a page, in the order the extractor reports them.
    private static string[] LinesOf(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)];

    private static int PageCountOf(string path)
    {
        ExternalTool.TryRun("qpdf", ["--show-npages", path], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        return int.Parse(stdout.Trim(), CultureInfo.InvariantCulture);
    }

    private static bool IsHeading(string line) => line.StartsWith("Chapter", StringComparison.Ordinal);

    [Fact]
    public void ADocumentOfHeadingsAndText_hasNoHeadingLastOnAPage_onlyWithTheFlags()
    {
        var control = Write(Save(Handbook(keep: false)), "control.pdf");
        var strandedWithout = 0;
        for (var page = 1; page <= PageCountOf(control); page++)
            if (LinesOf(PageText(control, page)) is { Length: > 0 } lines && IsHeading(lines[^1]))
                strandedWithout++;

        // The control has to strand a heading, or the assertion below proves nothing.
        Assert.True(strandedWithout > 0, "the document without the flags strands no heading");

        using var doc = Handbook(keep: true);
        var path = Write(Save(doc), "keep.pdf");
        var pages = PageCountOf(path);
        var headingsSeen = 0;
        for (var page = 1; page <= pages; page++)
        {
            var lines = LinesOf(PageText(path, page));
            Assert.NotEmpty(lines);
            Assert.False(IsHeading(lines[^1]), $"page {page} ends with {lines[^1]}");
            headingsSeen += lines.Count(IsHeading);
        }

        Assert.Equal(12, headingsSeen);
        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void TheKeptDocument_passesQpdf_andIsPdfA2uCompliant()
    {
        using var doc = Handbook(keep: true);
        var bytes = Save(doc);
        var path = Write(bytes, "kept-2u.pdf");

        ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

        var report = FontReportSupport.VeraPdf("2u", bytes);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
    }
}
