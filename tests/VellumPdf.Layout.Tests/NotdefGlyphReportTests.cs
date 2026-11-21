// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.FontReportSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A character the embedded font has no glyph for is written as glyph 0, and the save reports it as
/// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> (#592). DejaVu Sans lacks U+26B9 and U+1F31F;
/// Liberation Mono lacks U+2609 and U+FFFD but has the glyph for U+00A0, which stands in for
/// U+202F.
/// </summary>
public sealed class NotdefGlyphReportTests
{
    private const string Clause2u = "ISO 19005-2:2011, 6.2.11.8";
    private const string ClauseUa = "ISO 14289-1:2014, 7.21.8";
    private const string Text = "A \u26B9 B \U0001F31F C";

    private static Document Build(PdfConformance conformance, string text = Text, bool strict = false)
    {
        var doc = NewDocument(conformance);
        doc.StrictDiagnostics = strict;
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Paragraph(text, new TextStyle { FontRef = font }));
        return doc;
    }

    private static void AssertItem(
        LayoutDiagnostic item, string message, string? clause, LayoutElementKind kind, int? index, int count)
    {
        Assert.Equal(LayoutDiagnosticCode.NotdefGlyph, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(message, item.Message);
        Assert.Equal(clause, item.Clause);
        Assert.Equal(kind, item.ElementKind);
        Assert.Equal(index, item.ElementIndex);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(count, item.Count);
    }

    private const string MessageStar = "U+26B9 has no glyph in DejaVuSans (TT1) and is drawn as glyph 0 (.notdef).";
    private const string MessageSun = "U+1F31F has no glyph in DejaVuSans (TT1) and is drawn as glyph 0 (.notdef).";

    // \u2500\u2500 The report, the content stream and the ToUnicode map \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [Fact]
    public void MissingCharacters_areReportedOnceEach_andTheContentIsUnchanged()
    {
        using var doc = Build(PdfConformance.PdfA2u);

        var pdf = Save(doc);

        // The glyph ids are written as they were: glyph 0 for each missing character.
        Assert.Equal(["<002400030000000300250003000000030026> Tj"], ShowLines(pdf));
        // Glyph 0 has the one entry U+FFFD, and no entry names a missing character.
        Assert.Equal(
            ["5 beginbfchar", "<0000> <FFFD>", "<0003> <0020>", "<0024> <0041>", "<0025> <0042>", "<0026> <0043>"],
            BfcharLines(pdf));
        Assert.Equal(2, doc.Diagnostics.Count);
        AssertItem(doc.Diagnostics[0], MessageStar, Clause2u, LayoutElementKind.Paragraph, 0, 1);
        AssertItem(doc.Diagnostics[1], MessageSun, Clause2u, LayoutElementKind.Paragraph, 0, 1);
    }

    [Theory]
    [InlineData(PdfConformance.None, null)]
    [InlineData(PdfConformance.PdfA2b, Clause2u)]
    [InlineData(PdfConformance.PdfA2a, Clause2u)]
    [InlineData(PdfConformance.PdfUA1, ClauseUa)]
    public void TheClause_followsTheClaim(PdfConformance conformance, string? clause)
    {
        using var doc = Build(conformance);

        Save(doc);

        Assert.Equal(2, doc.Diagnostics.Count);
        AssertItem(doc.Diagnostics[0], MessageStar, clause, LayoutElementKind.Paragraph, 0, 1);
        AssertItem(doc.Diagnostics[1], MessageSun, clause, LayoutElementKind.Paragraph, 0, 1);
    }

    [Fact]
    public void AValueTheEnumerationDoesNotName_isReportedUnderThePdfAClause()
    {
        using var doc = Build((PdfConformance)99);

        Save(doc);

        Assert.Equal(2, doc.Diagnostics.Count);
        Assert.Equal(Clause2u, doc.Diagnostics[0].Clause);
    }

    [Fact]
    public void StrictMode_refusesTheSave_naming_theFirstItem_andWritesNothing()
    {
        using var doc = Build(PdfConformance.PdfA2u, strict: true);
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal(
            "StrictDiagnostics refused the save: NotdefGlyph (ISO 19005-2:2011, 6.2.11.8) in Paragraph 0, page 1: "
            + MessageStar + " 1 further error item(s).",
            ex.Message);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void ManyOccurrences_areOneItemEach_withTheirCounts()
    {
        using var doc = Build(PdfConformance.None, string.Join(' ', Enumerable.Repeat("\u26B9", 100_000)));
        Save(doc);
        var one = Assert.Single(doc.Diagnostics);
        AssertItem(one, MessageStar, null, LayoutElementKind.Paragraph, 0, 100_000);

        using var mono = NewDocument(PdfConformance.None);
        var font = mono.UseTrueTypeFont(LiberationMonoBytes);
        mono.Add(new Paragraph(string.Join(' ', Enumerable.Repeat("\u26B9\u2609", 50_000)), new TextStyle { FontRef = font }));
        Save(mono);

        Assert.Equal(2, mono.Diagnostics.Count);
        AssertItem(mono.Diagnostics[0], "U+26B9 has no glyph in LiberationMono (TT1) and is drawn as glyph 0 (.notdef).",
            null, LayoutElementKind.Paragraph, 0, 50_000);
        AssertItem(mono.Diagnostics[1], "U+2609 has no glyph in LiberationMono (TT1) and is drawn as glyph 0 (.notdef).",
            null, LayoutElementKind.Paragraph, 0, 50_000);
    }

    [Fact]
    public void ACharacterTheFontLacks_isReportedWithTheFontItWasDrawnIn()
    {
        using var doc = NewDocument(PdfConformance.None);
        var font = doc.UseTrueTypeFont(LiberationMonoBytes);
        doc.Add(new Paragraph("a\u2609b", new TextStyle { FontRef = font }));

        var pdf = Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal("U+2609 has no glyph in LiberationMono (TT1) and is drawn as glyph 0 (.notdef).", item.Message);
        Assert.Equal(["<004400000045> Tj"], ShowLines(pdf));
    }

    [Fact]
    public void AStandInGlyph_isNotGlyphZero_andIsNotReported()
    {
        using var doc = NewDocument(PdfConformance.None);
        var font = doc.UseTrueTypeFont(LiberationMonoBytes);
        doc.Add(new Paragraph("1\u202F000", new TextStyle { FontRef = font }));

        var pdf = Save(doc);

        Assert.Empty(doc.Diagnostics);
        Assert.Equal(["<00140062001300130013> Tj"], ShowLines(pdf));
        Assert.DoesNotContain(BfcharLines(pdf), l => l.StartsWith("<0000>", StringComparison.Ordinal));
    }

    // \u2500\u2500 One document per drawing site, so a site that stops reporting changes the item list \u2500\u2500\u2500\u2500

    private static Document SiteDocument(Action<Document, TextStyle> add)
    {
        var doc = NewDocument(PdfConformance.None);
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes) };
        add(doc, style);
        return doc;
    }

    private static void AssertSite(Document doc, LayoutElementKind kind, int? index)
    {
        Save(doc);
        var item = Assert.Single(doc.Diagnostics);
        AssertItem(item, MessageStar, null, kind, index, 1);
    }

    [Fact]
    public void ATableCell_isReported()
    {
        using var doc = SiteDocument((d, style) =>
        {
            var table = new TableElement { DefaultCellStyle = style };
            table.AddRow().AddCell("x\u26B9y");
            d.Add(table);
        });

        AssertSite(doc, LayoutElementKind.Table, 0);
    }

    [Fact]
    public void AHeaderBand_isReported() =>
        AssertSiteDispose(SiteDocument((d, style) => d.Add("body", style).SetHeader("h\u26B9", style)), LayoutElementKind.Header, null);

    [Fact]
    public void AFooterBand_isReported() =>
        AssertSiteDispose(SiteDocument((d, style) => d.Add("body", style).SetFooter("f\u26B9", style)), LayoutElementKind.Footer, null);

    [Fact]
    public void AListItem_isReported()
    {
        using var doc = SiteDocument((d, style) =>
        {
            var list = new ListElement { DefaultStyle = style };
            list.Add("\u26B9");
            d.Add(list);
        });

        AssertSite(doc, LayoutElementKind.List, 0);
    }

    private static void AssertSiteDispose(Document doc, LayoutElementKind kind, int? index)
    {
        using (doc) AssertSite(doc, kind, index);
    }

    // \u2500\u2500 A custom renderer, and the sink on its own \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [Fact]
    public void AnIdPassedToShowGlyphs_isReportedWithTheCharacterUnknown_andStrictRefusesIt()
    {
        foreach (var strict in new[] { false, true })
        {
            using var doc = NewDocument(PdfConformance.PdfA2u);
            doc.StrictDiagnostics = strict;
            var font = doc.UseTrueTypeFont(DejaVuBytes);
            doc.Add(new Paragraph("A", new TextStyle { FontRef = font }));
            doc.Add(new GlyphIdRenderer(font, [0, 36]));
            using var ms = new MemoryStream();

            if (strict)
            {
                var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));
                Assert.Equal(
                    "StrictDiagnostics refused the save: NotdefGlyph (ISO 19005-2:2011, 6.2.11.8) in Custom 1, page 1: "
                    + "Glyph 0 (.notdef) was drawn in font TT1 from glyph ids passed to PdfCanvas.ShowGlyphs, so the character is not known.",
                    ex.Message);
                continue;
            }

            doc.Save(ms);
            var item = Assert.Single(doc.Diagnostics);
            AssertItem(
                item,
                "Glyph 0 (.notdef) was drawn in font TT1 from glyph ids passed to PdfCanvas.ShowGlyphs, so the character is not known.",
                Clause2u, LayoutElementKind.Custom, 1, 1);
        }
    }

    [Fact]
    public void TheSink_reportsGlyphZeroWhateverReportSays_asAListMarkerNeeds()
    {
        using var pdf = new PdfDocument();
        var handle = pdf.UseTrueTypeFont(DejaVuBytes);
        var canvas = new PdfCanvas(pdf.AddPage());
        var sink = new LayoutDiagnosticSink { CurrentKind = LayoutElementKind.List, CurrentIndex = 0, CurrentPage = 1 };

        canvas.BeginText().SetFontByName(handle.ResourceName, 12);
        TextEmitter.Show(canvas, new TextStyle { FontRef = handle }, "\u26B9");
        sink.SyncEncodingWarnings(canvas, report: false);

        var item = Assert.Single(sink.Snapshot());
        Assert.Equal(LayoutDiagnosticCode.NotdefGlyph, item.Code);
        Assert.Equal(MessageStar, item.Message);
        Assert.Equal(1, item.Count);
    }

    [Fact]
    public void TheSink_countsWhatTheCanvasDropped_andReadsItOnce()
    {
        var canvas = new PdfCanvas(new PdfPage(new PdfRectangle(0, 0, 100, 100)));
        canvas.BeginText();
        for (var i = 1; i <= 1003; i++) canvas.SetFontByName($"X{i}", 12).ShowGlyphs([0]);
        var sink = new LayoutDiagnosticSink();

        sink.SyncEncodingWarnings(canvas, report: true);
        var first = sink.Snapshot();
        sink.SyncEncodingWarnings(canvas, report: true);
        var second = sink.Snapshot();

        var last = first[^1];
        Assert.Equal(LayoutDiagnosticCode.DiagnosticsDropped, last.Code);
        Assert.Equal(3, last.Count);
        Assert.True(sink.HasErrors);
        Assert.Equal(1001, first.Count);
        Assert.Equal(first.Select(i => i.Count), second.Select(i => i.Count));
        Assert.Equal(1003, sink.ErrorReports);
    }

    // \u2500\u2500 The same data through the Kernel list \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [Fact]
    public void ThroughADocumentRenderer_theKernelListHoldsTheSameEntries()
    {
        var pdf = PlacementLoopSupport.NewPdf();
        var font = pdf.UseTrueTypeFont(DejaVuBytes);
        var renderer = new DocumentRenderer(pdf, PlacementLoopSupport.Page, PlacementLoopSupport.Margins);
        renderer.Add(new ParagraphRenderer(new Paragraph(Text, new TextStyle { FontRef = font })));

        PlacementLoopSupport.Render(renderer);

        Assert.Equal(
            [("\u26B9", "TT1", "DejaVuSans", 1), ("\U0001F31F", "TT1", "DejaVuSans", 1)],
            pdf.NotdefGlyphs.Select(e => ((string?)e.Character?.ToString(), e.FontResourceName, e.FontName, e.Count)));
        Assert.Equal(0, pdf.NotdefGlyphsDropped);
        Assert.Equal(2, renderer.Diagnostics.Count);
        Assert.Equal(pdf.NotdefGlyphs.Select(e => e.Count), renderer.Diagnostics.Select(i => i.Count));
    }

    [Fact]
    public void AHelveticaParagraph_reportsTheUnencodableCharacters_andNoGlyphZero()
    {
        using var doc = NewDocument(PdfConformance.None);
        doc.Add(new Paragraph("A \u26B9 B \u2609 C"));

        Save(doc);

        Assert.Equal(
            [LayoutDiagnosticCode.UnencodableCharacter, LayoutDiagnosticCode.UnencodableCharacter],
            doc.Diagnostics.Select(i => i.Code));
        Assert.Equal(
            ["U+26B9 is not in WinAnsiEncoding and is written as '?' for each UTF-16 code unit.",
             "U+2609 is not in WinAnsiEncoding and is written as '?' for each UTF-16 code unit."],
            doc.Diagnostics.Select(i => i.Message));
    }

    // \u2500\u2500 What byte-identity means here: only the ToUnicode map moves \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [Fact]
    public void TheMissingCharacterDocument_differsFromOneWithoutThemOnlyInItsToUnicodeMap()
    {
        using var doc = Build(PdfConformance.PdfA2u);
        var pdf = Save(doc);

        Assert.Contains("<0000> <FFFD>", BfcharLines(pdf));
        Assert.DoesNotContain("<0000> <26B9>", BfcharLines(pdf));
        Assert.DoesNotContain("<0000> <D83CDF1F>", BfcharLines(pdf));
    }

    // \u2500\u2500 Oracles \u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500\u2500

    [Fact]
    public void VeraPdf_failsOnlyTheNotdefRule_andNotTheToUnicodeRule()
    {
        var pdf = Save(Build(PdfConformance.PdfA2u));
        Assert.Contains("<0000> <FFFD>", BfcharLines(pdf));
        Assert.DoesNotContain("<0000> <26B9>", BfcharLines(pdf));

        var failed = FailedRules(VeraPdf("2u", pdf));

        Assert.Equal(["6.2.11.8-1"], failed);
    }

    [Fact]
    public void VeraPdf_ua1_failsTheNotdefRule_andNotTheUnicodeValueRule()
    {
        var pdf = Save(Build(PdfConformance.PdfUA1));
        Assert.Contains("<0000> <FFFD>", BfcharLines(pdf));

        var failed = FailedRules(VeraPdf("ua1", pdf));

        Assert.Contains("7.21.8-1", failed);
        Assert.DoesNotContain("7.21.7-1", failed);
    }

    [Fact]
    public void Poppler_extractsTheReplacementCharacterForEachMissingOne()
    {
        var pdf = Save(Build(PdfConformance.PdfA2u));
        Assert.Contains("<0000> <FFFD>", BfcharLines(pdf));
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);

        VellumPdf.TestSupport.ExternalTool.TryRun("pdftotext", ["-enc", "UTF-8", path, "-"],
            out var exit, out var text, out var stderr, out var timedOut);

        Assert.False(timedOut);
        Assert.True(exit == 0, $"pdftotext exit {exit}\n{stderr}");
        Assert.Equal(2, text.Count(c => c == '\uFFFD'));
        Assert.DoesNotContain("\u26B9", text, StringComparison.Ordinal);
        Assert.DoesNotContain("\U0001F31F", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Qpdf_checksTheFile_clean()
    {
        var pdf = Save(Build(PdfConformance.PdfA2u));
        Assert.Contains("<0000> <FFFD>", BfcharLines(pdf));
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);

        VellumPdf.TestSupport.ExternalTool.TryRun("qpdf", ["--check", path],
            out var exit, out var stdout, out var stderr, out var timedOut);

        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
    }
}
