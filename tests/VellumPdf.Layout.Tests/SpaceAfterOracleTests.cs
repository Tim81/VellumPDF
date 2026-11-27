// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <c>SpaceAfter</c> against the external tools: <c>pdftotext -bbox</c> places the word after each
/// gap, and veraPDF checks a PDF/A-2u document that sets one on each of the eight kinds. Each
/// test first reads the gap from the saved file, so a verdict is never about a file that dodges
/// the property.
/// </summary>
public sealed class SpaceAfterOracleTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"vellumgap_{Guid.NewGuid():N}");

    public SpaceAfterOracleTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private static Document Build(double space)
    {
        var fontPath = Path.Combine(FontReportSupport.FontsDir, "DejaVuSans.ttf");
        var monoPath = Path.Combine(FontReportSupport.FontsDir, "LiberationMono-Regular.ttf");
        if (!File.Exists(fontPath)) OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");
        if (!File.Exists(monoPath)) OracleGate.Unavailable("LiberationMono-Regular.ttf", "Liberation Mono is needed for an embedded font.");

        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 400),
            Margins = new EdgeInsets(24),
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
            Conformance = PdfConformance.PdfA2u,
        };
        doc.Info.Title = "Space after";
        doc.Language = "en-US";
        doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(File.ReadAllBytes(fontPath)), FontSize = 11 };
        var mono = new TextStyle { FontRef = doc.UseTrueTypeFont(File.ReadAllBytes(monoPath)), FontSize = 10 };

        doc.Add(new Heading("Alpha", style) { Level = 0, SpaceAfter = space });
        doc.Add(new Paragraph("Bravo", style) { SpaceAfter = space });
        doc.Add(new ListElement(ListStyle.Unordered, [new ListItem("Charlie", style)]) { SpaceAfter = space });
        var table = new TableElement { SpaceAfter = space };
        table.AddRow().AddCell(new Cell("Delta") { Style = style });
        doc.Add(table);
        doc.Add(new Preformatted("Echo", mono) { SpaceAfter = space });
        doc.Add(new LineSeparator { SpaceAfter = space });
        doc.Add(new LayoutImage(PngImageLoader.Load(PdfTestUtil.CreateMinimalRgbPng())) { Width = 20, Height = 20, SpaceAfter = space });
        doc.Add(new PieChart { Slices = [new PieSlice(1, ColorRgb.Black)], Diameter = 20, SpaceAfter = space });
        doc.Add(new Paragraph("Foxtrot", style));
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

    // The top edge of each word, by word, as pdftotext reports it.
    private static Dictionary<string, double> WordTops(string path)
    {
        if (!ExternalTool.TryRun("pdftotext", ["-bbox", path, "-"], out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdftotext");
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        var tops = new Dictionary<string, double>();
        foreach (Match m in Regex.Matches(stdout, @"yMin=""(?<y>[0-9.]+)""[^>]*>(?<w>[A-Za-z]+)</word>"))
            tops[m.Groups["w"].Value] = double.Parse(m.Groups["y"].Value, CultureInfo.InvariantCulture);
        return tops;
    }

    [Fact]
    public void EachGap_moves_theWordAfterIt_byItsSize()
    {
        using var plain = Build(0);
        using var gapped = Build(10);
        var before = WordTops(Write(Save(plain), "plain.pdf"));
        var after = WordTops(Write(Save(gapped), "gapped.pdf"));

        // The separator, image and chart hold no word, so Foxtrot sits after eight gaps and Echo
        // after four.
        var gaps = new Dictionary<string, int> { ["Bravo"] = 1, ["Charlie"] = 2, ["Delta"] = 3, ["Echo"] = 4, ["Foxtrot"] = 8 };
        foreach (var (word, count) in gaps)
            Assert.Equal(10 * count, after[word] - before[word], 1);
    }

    [Fact]
    public void TheDocumentWithGaps_passesQpdf_andIsPdfA2uCompliant()
    {
        using var doc = Build(10);
        var bytes = Save(doc);
        var path = Write(bytes, "gapped-2u.pdf");

        // The file holds the gaps: the same document without them differs.
        using var plain = Build(0);
        Assert.NotEqual(Save(plain), bytes);

        if (!ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("qpdf");
        Assert.False(timedOut);
        Assert.True(exit == 0, stdout + stderr);
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);

        var report = FontReportSupport.VeraPdf("2u", bytes);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        Assert.Empty(doc.Diagnostics);
    }
}
