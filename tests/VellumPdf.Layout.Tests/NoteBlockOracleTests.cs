// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.NoteBlockSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// External oracles for the note block (#703): veraPDF checks the PDF/A and PDF/UA claims, qpdf
/// the syntax and pdftotext what a reader extracts. Each test first asserts that the saved file
/// holds the block, so a verdict is never about a file that dodges the feature. Files go in a
/// directory made under the temp path, because veraPDF can run as another user.
/// </summary>
public sealed class NoteBlockOracleTests : IDisposable
{
    private readonly string _dir;

    public NoteBlockOracleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vellumnote_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort: the directory may be locked or gone */ }
        catch (UnauthorizedAccessException) { /* best-effort: a locked file on Windows */ }
    }

    private static readonly string BoldPath = Path.Combine(AppContext.BaseDirectory, "Fonts", "LiberationSans-Bold.ttf");

    // A heading, a paragraph, a block of three paragraphs with a bold lead-in, and a table, long enough
    // to span three pages of 300 x 200 pt. With links, the first paragraph of the block holds a link to
    // a URI and a page reference, and a paragraph after the block holds the anchor; PDF/UA-1 refuses
    // both before layout, so its fixture leaves them out.
    private static Document Build(PdfConformance claim, bool tagged, bool links = true)
    {
        var doc = NewDoc(300, 200);
        doc.Conformance = claim;
        doc.Language = "en-US";
        doc.Info.Title = "Note block";
        if (tagged)
            doc.Tagged = true;

        var body = new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10 };
        var bold = new TextStyle { FontRef = doc.LoadTrueTypeFont(BoldPath), FontSize = 10 };
        var linked = new TextStyle { FontRef = doc.LoadTrueTypeFont(DejaVuPath), FontSize = 10, LinkUri = "https://example.com/note" };
        var words = string.Join(' ', Enumerable.Repeat("a note that wraps inside its padding", 18));
        var lead = new List<TextRun> { new("Warning: ", bold), new(words, body) };
        if (links)
        {
            lead.Add(new TextRun(" See this link", linked));
            lead.Add(new TextRun(" and page ", body));
            lead.Add(TextRun.PageReference("after", body));
        }

        doc.Add(new Heading("Notes", body));
        doc.Add(new Paragraph("Before the block.", body));
        doc.Add(new NoteBlock
        {
            Background = new ColorRgb(0.92, 0.95, 1),
            BorderWidth = 1.5,
            BorderColor = new ColorRgb(0.1, 0.2, 0.6),
            Padding = new EdgeInsets(8, 10, 8, 10),
            SpaceAfter = 6,
        }
            .Add(new Paragraph(lead) { SpaceAfter = 4 })
            .Add(new Paragraph(words, body) { SpaceAfter = 4 })
            .Add(new Paragraph(words, body)));
        doc.Add(new Paragraph("After the block.", body) { Anchor = links ? "after" : null });
        var table = new VellumPdf.Layout.Elements.Table.TableElement();
        table.AddRow(isHeader: true).AddCell(new VellumPdf.Layout.Elements.Table.Cell("Key") { Style = bold }).AddCell(new VellumPdf.Layout.Elements.Table.Cell("Value") { Style = bold });
        table.AddRow().AddCell(new VellumPdf.Layout.Elements.Table.Cell("a") { Style = body }).AddCell(new VellumPdf.Layout.Elements.Table.Cell("b") { Style = body });
        doc.Add(table);
        return doc;
    }

    private string Write(Document doc, string name)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Save(doc));
        return path;
    }

    private static void AssertTheFileHoldsTheBlock(string path, bool tagged)
    {
        var pdf = File.ReadAllBytes(path);
        var pages = PageContents(pdf);
        Assert.True(pages.Count >= 3, "the fixture must span pages");

        // The fill is on every page the block reaches, and so are its two sides.
        var withFill = pages.Where(p => p.Contains("0.92 0.95 1 rg", StringComparison.Ordinal)).ToList();
        Assert.True(withFill.Count >= 3, "the block must reach three pages");
        Assert.All(withFill, p => Assert.True(Rects(p).Count(r => r.Split(' ')[2] == "1.5") >= 2));
        if (tagged)
            Assert.StartsWith("/H1 <</MCID 0>> BDC", pages[0], StringComparison.Ordinal);
    }

    // The tree holds the heading, the paragraph, one Div of paragraph pieces, and the table, and
    // names no Aside.
    private static void AssertTheBlockIsADiv(byte[] pdf)
    {
        var tree = StructTree(pdf);
        Assert.Matches(@"^Document\(H1,P,Div\((P,)+P\),P,Table\(.*\)\)$", tree);
        Assert.DoesNotContain("Aside", tree, StringComparison.Ordinal);
    }

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

    [Fact]
    public void PdfA2u_withABlockOverThreePages_passesVeraPdf_andQpdf()
    {
        using var doc = Build(PdfConformance.PdfA2u, tagged: false);
        var path = Write(doc, "a2u.pdf");

        AssertTheFileHoldsTheBlock(path, tagged: false);
        AssertVeraPdfPasses(path, "2u");
        AssertQpdfClean(path);
    }

    [Fact]
    public void PdfA2a_withATaggedBlock_isADivAndPassesVeraPdf()
    {
        using var doc = Build(PdfConformance.PdfA2a, tagged: true);
        var path = Write(doc, "a2a.pdf");

        AssertTheFileHoldsTheBlock(path, tagged: true);
        AssertTheBlockIsADiv(File.ReadAllBytes(path));
        AssertVeraPdfPasses(path, "2a");
    }

    [Fact]
    public void PdfUA1_withATaggedBlock_isADivAndPassesVeraPdf_andQpdf()
    {
        using var doc = Build(PdfConformance.PdfUA1, tagged: true, links: false);
        var path = Write(doc, "ua1.pdf");

        AssertTheFileHoldsTheBlock(path, tagged: true);
        AssertTheBlockIsADiv(File.ReadAllBytes(path));
        AssertVeraPdfPasses(path, "ua1");
        AssertQpdfClean(path);
    }

    /// <summary>A PDF 2.0 file with a tagged block is clean for qpdf, and pdftotext reads the text in order.</summary>
    [Fact]
    public void APdf2File_withATaggedBlock_isADiv_isCleanForQpdf_andPdftotextReadsTheParagraphs()
    {
        using var doc = Build(PdfConformance.None, tagged: true);
        var path = Write(doc, "pdf20.pdf");

        AssertTheFileHoldsTheBlock(path, tagged: true);
        AssertTheBlockIsADiv(File.ReadAllBytes(path));
        AssertQpdfClean(path);

        ExternalTool.TryRun("pdftotext", ["-enc", "UTF-8", path, "-"], out var exit, out var text, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);

        // pdftotext reads no artifact marking, so this proves the reading order of the paragraphs
        // around and inside the block, not how the border and the fill are treated.
        var flat = Regex.Replace(text, @"\s+", " ");
        var before = flat.IndexOf("Before the block.", StringComparison.Ordinal);
        var warning = flat.IndexOf("Warning: a note that wraps", StringComparison.Ordinal);
        var after = flat.IndexOf("After the block.", StringComparison.Ordinal);
        Assert.True(before >= 0 && warning > before && after > warning, flat);
    }
}
