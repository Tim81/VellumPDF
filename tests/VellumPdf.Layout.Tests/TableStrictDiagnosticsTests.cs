// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.ContinuationSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A tab, a line break or another control white-space character in a table cell reaches no font,
/// so the save lists no <c>NotdefGlyph</c> or <c>UnencodableCharacter</c> for it and
/// <see cref="Document.StrictDiagnostics"/> does not refuse the cell (#611). That holds for a
/// table the document lays out and for one that a caller's renderer holds, which draws the
/// character as nothing. A running band reads the same characters as spaces, in its template and
/// in a page label, so <see cref="Document.StrictDiagnostics"/> does not refuse a band for one
/// either. A control character that is not white space is still reported and refused.
/// </summary>
public sealed class TableStrictDiagnosticsTests : IDisposable
{
    private static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private readonly string _tempDir;

    public TableStrictDiagnosticsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"vellumstrictcell_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private static void RequireDejaVu()
    {
        if (!File.Exists(DejaVu))
            OracleGate.Unavailable("DejaVuSans.ttf", "DejaVu Sans is needed for an embedded font.");
    }

    private static Document StrictDocument(PdfConformance conformance = PdfConformance.None) => new()
    {
        PageSize = new PdfRectangle(0, 0, 400, 400),
        Margins = new EdgeInsets(10),
        Conformance = conformance,
        StrictDiagnostics = true,
    };

    private static void AddTable(Document doc, TableElement table, bool heldByAWrapper)
    {
        if (heldByAWrapper)
            doc.Add(new Wrapper(new TableRenderer(table), WrapKind.PassThrough));
        else
            doc.Add(table);
    }

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[] Reported(Document doc) =>
        [.. doc.Diagnostics.Where(d => d.Code is LayoutDiagnosticCode.NotdefGlyph or LayoutDiagnosticCode.UnencodableCharacter)
            .Select(d => d.Message)];

    /// <summary>
    /// An embedded font has no glyph for a tab, a line feed, a carriage return, a form feed or the
    /// line separators, so each reaching the font would be glyph 0 and a refusal under the switch.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnEmbeddedCellWithControlWhiteSpace_saves_underStrictDiagnostics(bool heldByAWrapper)
    {
        RequireDejaVu();
        using var doc = StrictDocument();
        var font = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var table = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 10 } }
            .SetColumnWidths(90, 90, 90, 90);
        table.AddRow().AddCell("A\tB").AddCell("line1\nline2").AddCell("cr1\r\ncr2").AddCell("ff\fx\u0085y\u2028z\u2029w");
        AddTable(doc, table, heldByAWrapper);

        var bytes = Save(doc);

        Assert.NotEmpty(bytes);
        Assert.Empty(Reported(doc));
    }

    /// <summary>
    /// A Standard 14 font cannot encode U+2028, which the table would otherwise write as a question
    /// mark and report as <c>UnencodableCharacter</c>; a tab it would write as a raw 0x09 byte.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStandard14CellWithControlWhiteSpace_saves_underStrictDiagnostics(bool heldByAWrapper)
    {
        using var doc = StrictDocument();
        var table = new TableElement
        {
            DefaultCellStyle = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 },
        }.SetColumnWidths(90, 90, 90);
        table.AddRow().AddCell("A\u2028B").AddCell("a\tb").AddCell("one\ntwo\u2029three");
        AddTable(doc, table, heldByAWrapper);

        var bytes = Save(doc);

        Assert.NotEmpty(bytes);
        Assert.Empty(Reported(doc));
    }

    /// <summary>
    /// A control character that is not white space still reaches the font, so the report and the
    /// refusal stay: the switch refuses the cell for U+0001, whoever holds the table.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ACellWithAControlCharacterThatIsNotWhiteSpace_isStillRefused_underStrictDiagnostics(bool heldByAWrapper)
    {
        RequireDejaVu();
        using var doc = StrictDocument();
        var font = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var table = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 10 } }
            .SetColumnWidths(90);
        table.AddRow().AddCell("a\u0001b");
        AddTable(doc, table, heldByAWrapper);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("NotdefGlyph", ex.Message, StringComparison.Ordinal);
        Assert.Contains("U+0001", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The output the switch lets through is correct, which the oracle says: a PDF/A-2u document
    /// with a table holding a tab and a line break, behind a renderer of the caller's, passes
    /// veraPDF, where glyph 0 in the file fails clause 6.2.11.8.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStrictPdfA2uDocumentWithControlWhiteSpaceInACell_passesVeraPdf(bool heldByAWrapper)
    {
        RequireDejaVu();
        using var doc = StrictDocument(PdfConformance.PdfA2u);
        doc.Info.Title = "Strict cells";
        var font = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var table = new TableElement { DefaultCellStyle = new TextStyle { FontRef = font, FontSize = 10 } }
            .SetColumnWidths(100, 100);
        table.AddRow().AddCell("a\tb").AddCell("plain");
        table.AddRow().AddCell("line1\nline2").AddCell("c\td");
        AddTable(doc, table, heldByAWrapper);
        var path = Path.Combine(_tempDir, heldByAWrapper ? "held.pdf" : "direct.pdf");
        File.WriteAllBytes(path, Save(doc));

        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var exit, out var report, out var err, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut);
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}\n{report}\n{err}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);
    }

    /// <summary>
    /// A band draws a tab, a line feed, a form feed or a line separator as a space, so a
    /// Standard 14 band that holds them saves under the switch with nothing reported. The page
    /// label's prefix is read the same way.
    /// </summary>
    [Fact]
    public void AStandard14BandWithControlWhiteSpace_saves_underStrictDiagnostics()
    {
        using var doc = StrictDocument();
        var style = new TextStyle { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, "A\tB\u2028", 1);
        doc.SetHeader("head\tline\nbreak\u2028", style);
        doc.SetFooter("p\f{page}", style);
        doc.Add(new Paragraph("body", style));

        var bytes = Save(doc);

        Assert.NotEmpty(bytes);
        Assert.Empty(Reported(doc));
    }

    /// <summary>
    /// The band's output the switch lets through is correct, which the oracle says: a PDF/A-2u
    /// document whose header, footer and page label hold a tab, a line break and a form feed passes
    /// veraPDF. Glyph 0 in the file would fail clause 6.2.11.8.
    /// </summary>
    [Fact]
    public void AStrictPdfA2uDocumentWithControlWhiteSpaceInABand_passesVeraPdf()
    {
        RequireDejaVu();
        using var doc = StrictDocument(PdfConformance.PdfA2u);
        doc.Info.Title = "Strict bands";
        var font = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        var style = new TextStyle { FontRef = font, FontSize = 10 };
        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, "A\tB\n", 1);
        doc.SetHeader("head\tline\nbreak", style);
        doc.SetFooter("p\f{page}\tof {pages}", style);
        doc.Add(new Paragraph("body", style));
        var path = Path.Combine(_tempDir, "band.pdf");
        File.WriteAllBytes(path, Save(doc));

        Assert.Empty(Reported(doc));
        ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
            out var exit, out var report, out var err, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut);
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}\n{report}\n{err}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal), report);
    }

    /// <summary>
    /// A control character that is not white space still reaches the font from a band, so the
    /// switch refuses it: the band rule reads white space only.
    /// </summary>
    [Fact]
    public void ABandWithAControlCharacterThatIsNotWhiteSpace_isStillRefused_underStrictDiagnostics()
    {
        RequireDejaVu();
        using var doc = StrictDocument();
        var font = doc.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        doc.SetHeader("a\u0001b", new TextStyle { FontRef = font, FontSize = 10 });
        doc.Add(new Paragraph("body", new TextStyle { FontRef = font, FontSize = 10 }));

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.Contains("NotdefGlyph", ex.Message, StringComparison.Ordinal);
        Assert.Contains("U+0001", ex.Message, StringComparison.Ordinal);
    }
}
