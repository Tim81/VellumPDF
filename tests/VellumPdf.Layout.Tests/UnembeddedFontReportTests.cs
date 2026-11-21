// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using static VellumPdf.Layout.Tests.FontReportSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// An element that selects a Standard 14 font while the document carries a conformance claim is
/// reported as <see cref="LayoutDiagnosticCode.UnembeddedFont"/>, once per element and font,
/// counting the font operators written (#593). Nothing about the file changes; the byte pins are in
/// <see cref="FontReportByteIdentityTests"/>.
/// </summary>
public sealed class UnembeddedFontReportTests
{
    private const string Clause2u = "ISO 19005-2:2011, 6.2.11.4.1";
    private const string ClauseUa = "ISO 14289-1:2014, 7.21.4.1";

    private static string Message(string font) =>
        $"{font} is a Standard 14 font and is not embedded. Give this element a style with an embedded font; "
        + "SetDefaultFont applies only to Document.Add(string).";

    private static void AssertItem(
        LayoutDiagnostic item, string font, string clause, LayoutElementKind kind, int? index, int count)
    {
        Assert.Equal(LayoutDiagnosticCode.UnembeddedFont, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(Message(font), item.Message);
        Assert.Equal(clause, item.Clause);
        Assert.Equal(kind, item.ElementKind);
        Assert.Equal(index, item.ElementIndex);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(count, item.Count);
    }

    // A claim and a default style that only Document.Add(string) reads.
    private static Document DefaultFontDocument(PdfConformance conformance, bool strict = false)
    {
        var doc = NewDocument(conformance);
        doc.StrictDiagnostics = strict;
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes), FontSize = 11 };
        doc.SetDefaultFont(style);
        doc.Add("Hello");
        var list = new ListElement();
        list.Add("item");
        doc.Add(list);
        var table = new TableElement();
        table.AddRow().AddCell("cell");
        doc.Add(table);
        return doc;
    }

    [Fact]
    public void AListAndATableThatIgnoreTheDefaultFont_areReported_withTheirFontSelectionCounts()
    {
        using var doc = DefaultFontDocument(PdfConformance.PdfA2u);

        var pdf = Save(doc);

        // The list draws its marker and its item text, two paragraphs, so two selections.
        Assert.Equal(2, doc.Diagnostics.Count);
        AssertItem(doc.Diagnostics[0], "Helvetica", Clause2u, LayoutElementKind.List, 1, 2);
        AssertItem(doc.Diagnostics[1], "Helvetica", Clause2u, LayoutElementKind.Table, 2, 1);
        Assert.Equal(["/TT1 11 Tf", "/F1 12 Tf", "/F1 12 Tf", "/F1 12 Tf"], TfLines(pdf));
    }

    [Fact]
    public void StrictMode_refusesTheSave_namingTheListAndTheFurtherItem_andWritesNothing()
    {
        using var doc = DefaultFontDocument(PdfConformance.PdfA2u, strict: true);
        using var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal(
            "StrictDiagnostics refused the save: UnembeddedFont (ISO 19005-2:2011, 6.2.11.4.1) in List 1, page 1: "
            + Message("Helvetica") + " 1 further error item(s).",
            ex.Message);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void EmbeddedStylesOnTheListAndTheTable_reportNothing_andSaveUnderStrictMode()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        doc.StrictDiagnostics = true;
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes) };
        doc.Add("Hello", style);
        var list = new ListElement { DefaultStyle = style };
        list.Add("item");
        doc.Add(list);
        var table = new TableElement { DefaultCellStyle = style };
        table.AddRow().AddCell("cell");
        doc.Add(table);

        var pdf = Save(doc);

        Assert.Empty(doc.Diagnostics);
        Assert.DoesNotContain(TfLines(pdf), l => l.StartsWith("/F", StringComparison.Ordinal));
    }

    [Fact]
    public void WithNoClaim_nothingIsReported()
    {
        using var doc = DefaultFontDocument(PdfConformance.None, strict: true);

        Save(doc);

        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void EveryKindOfElementAndTheFooter_isReportedInOrder()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        doc.SetDefaultFont(new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes), FontSize = 11 });
        doc.Add("Hello");
        doc.Add(new Paragraph("para"));
        doc.Add(new Heading("head"));
        var list = new ListElement();
        list.Add("item");
        doc.Add(list);
        var table = new TableElement();
        table.AddRow().AddCell("cell");
        doc.Add(table);
        doc.SetFooter("foot");

        Save(doc);

        Assert.Equal(5, doc.Diagnostics.Count);
        AssertItem(doc.Diagnostics[0], "Helvetica", Clause2u, LayoutElementKind.Paragraph, 1, 1);
        AssertItem(doc.Diagnostics[1], "Helvetica", Clause2u, LayoutElementKind.Heading, 2, 1);
        AssertItem(doc.Diagnostics[2], "Helvetica", Clause2u, LayoutElementKind.List, 3, 2);
        AssertItem(doc.Diagnostics[3], "Helvetica", Clause2u, LayoutElementKind.Table, 4, 1);
        AssertItem(doc.Diagnostics[4], "Helvetica", Clause2u, LayoutElementKind.Footer, null, 1);
    }

    [Fact]
    public void AnEmptyCell_isReported_becauseTheFontOperatorIsWritten()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes) };
        doc.Add(new Paragraph("x", style));
        var table = new TableElement();
        table.AddRow().AddCell("");
        doc.Add(table);

        var pdf = Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        AssertItem(item, "Helvetica", Clause2u, LayoutElementKind.Table, 1, 1);
        Assert.Equal(["/TT1 12 Tf", "/F1 12 Tf"], TfLines(pdf));
    }

    [Fact]
    public void AnExplicitStandard14Style_underPdfUa_isReportedUnderThePdfUaClause()
    {
        using var doc = NewDocument(PdfConformance.PdfUA1);
        doc.Add(new Paragraph("text", new TextStyle { Font = Standard14.TimesRoman }));

        Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        AssertItem(item, "Times-Roman", ClauseUa, LayoutElementKind.Paragraph, 0, 1);
    }

    [Fact]
    public void TwoRunsInOneFontAtTwoSizes_countOneFontOperator()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        var paragraph = new Paragraph("ten ", new TextStyle { Font = Standard14.Helvetica, FontSize = 10 });
        paragraph.Add("twelve", new TextStyle { Font = Standard14.Helvetica, FontSize = 12 });
        doc.Add(paragraph);

        var pdf = Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        AssertItem(item, "Helvetica", Clause2u, LayoutElementKind.Paragraph, 0, 1);
        Assert.Equal(["/F1 10 Tf"], TfLines(pdf));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStandard14ValueTheEnumerationDoesNotName_isNotReported_andTheSaveThrowsAsBefore(bool strict)
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        doc.StrictDiagnostics = strict;
        doc.Add(new Paragraph("x", new TextStyle { Font = (Standard14)99 }));
        using var ms = new MemoryStream();

        var ex = Assert.Throws<IndexOutOfRangeException>(() => doc.Save(ms));

        // The write raises it, after the layout and after the strict check. A report would read the
        // font's name during the layout and throw from there, or turn it into the strict refusal.
        Assert.Contains("RegisterStandard14Fonts", ex.StackTrace, StringComparison.Ordinal);
        Assert.DoesNotContain("TextEmitter", ex.StackTrace, StringComparison.Ordinal);
        Assert.Empty(doc.Diagnostics);
    }


    private static LayoutDiagnostic OnlyItem(Document doc)
    {
        using (doc) Save(doc);
        return Assert.Single(doc.Diagnostics);
    }

    private static Document Helvetica(Action<Document, TextStyle> add)
    {
        var doc = NewDocument(PdfConformance.PdfA2u);
        var style = new TextStyle { Font = Standard14.Helvetica };
        add(doc, style);
        return doc;
    }

    [Fact]
    public void AParagraph_isReported() =>
        AssertItem(OnlyItem(Helvetica((d, s) => d.Add(new Paragraph("text", s)))),
            "Helvetica", Clause2u, LayoutElementKind.Paragraph, 0, 1);

    [Fact]
    public void ATableCell_isReported() =>
        AssertItem(OnlyItem(Helvetica((d, s) =>
        {
            var table = new TableElement { DefaultCellStyle = s };
            table.AddRow().AddCell("cell");
            d.Add(table);
        })), "Helvetica", Clause2u, LayoutElementKind.Table, 0, 1);

    [Fact]
    public void AHeaderBand_isReported() =>
        AssertItem(OnlyItem(Helvetica((d, s) =>
        {
            d.Add(new Paragraph("body", new TextStyle { FontRef = d.UseTrueTypeFont(DejaVuBytes) }));
            d.SetHeader("head", s);
        })), "Helvetica", Clause2u, LayoutElementKind.Header, null, 1);

    [Fact]
    public void AFooterBand_isReported() =>
        AssertItem(OnlyItem(Helvetica((d, s) =>
        {
            d.Add(new Paragraph("body", new TextStyle { FontRef = d.UseTrueTypeFont(DejaVuBytes) }));
            d.SetFooter("foot", s);
        })), "Helvetica", Clause2u, LayoutElementKind.Footer, null, 1);


    [Fact]
    public void VeraPdf_failsOnlyTheEmbeddingRule_forTheDefaultFontDocument_andForTheEmptyCell()
    {
        var g3 = Save(DefaultFontDocument(PdfConformance.PdfA2u));
        Assert.Contains(PdfFontsNames(g3), n => n.StartsWith("Helvetica", StringComparison.Ordinal));

        Assert.Equal(["6.2.11.4.1-1"], FailedRules(VeraPdf("2u", g3)));

        using var empty = NewDocument(PdfConformance.PdfA2u);
        empty.Add(new Paragraph("x", new TextStyle { FontRef = empty.UseTrueTypeFont(DejaVuBytes) }));
        var table = new TableElement();
        table.AddRow().AddCell("");
        empty.Add(table);
        var emptyPdf = Save(empty);
        Assert.Equal(["/TT1 12 Tf", "/F1 12 Tf"], TfLines(emptyPdf));
        Assert.Contains(PdfFontsNames(emptyPdf), n => n.StartsWith("Helvetica", StringComparison.Ordinal));
        Assert.Equal(["6.2.11.4.1-1"], FailedRules(VeraPdf("2u", emptyPdf)));
    }

    [Fact]
    public void VeraPdf_passesTheDocumentWhoseStylesAreEmbedded()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(DejaVuBytes) };
        doc.Add("Hello", style);
        var list = new ListElement { DefaultStyle = style };
        list.Add("item");
        doc.Add(list);
        var table = new TableElement { DefaultCellStyle = style };
        table.AddRow().AddCell("cell");
        doc.Add(table);
        var pdf = Save(doc);

        var rows = PdfFontsRows(pdf);
        Assert.Single(rows, r => r.Name.EndsWith("DejaVuSans", StringComparison.Ordinal) && r.Embedded);
        Assert.DoesNotContain(rows, r => !r.Embedded);
        var report = VeraPdf("2u", pdf);
        Assert.Contains("isCompliant=" + '"' + "true" + '"', report, StringComparison.Ordinal);
        Assert.Empty(FailedRules(report));
    }

    [Fact]
    public void Qpdf_checksTheDefaultFontDocument_clean()
    {
        var pdf = Save(DefaultFontDocument(PdfConformance.PdfA2u));
        Assert.Contains("/F1 12 Tf", TfLines(pdf));
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);

        VellumPdf.TestSupport.ExternalTool.TryRun("qpdf", ["--check", path],
            out var exit, out var stdout, out var stderr, out var timedOut);

        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
    }

    // pdffonts prints fixed-width columns under a header that names them. The embedded flag is read
    // at the header's "emb" column, so the parse does not depend on how many words a type has.
    private static List<(string Name, bool Embedded)> PdfFontsRows(byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        VellumPdf.TestSupport.ExternalTool.TryRun("pdffonts", [path],
            out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, $"pdffonts exit {exit}\n{stderr}");
        var lines = stdout.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var emb = lines[0].IndexOf("emb", StringComparison.Ordinal);
        Assert.True(emb > 0, $"No emb column in the pdffonts header: {lines[0]}");
        return [.. lines.Skip(2).Where(l => l.Length > emb + 3)
            .Select(l => (l.Split(' ')[0], l.Substring(emb, 3).Trim() == "yes"))];
    }

    private static List<string> PdfFontsNames(byte[] pdf) =>
        [.. PdfFontsRows(pdf).Where(r => !r.Embedded).Select(r => r.Name)];


    [Fact]
    public void TwoStandard14FontsInOneParagraph_areTwoItems()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        var paragraph = new Paragraph("helv ", new TextStyle { Font = Standard14.Helvetica });
        paragraph.Add("times", new TextStyle { Font = Standard14.TimesRoman });
        doc.Add(paragraph);

        Save(doc);

        Assert.Equal(2, doc.Diagnostics.Count);
        AssertItem(doc.Diagnostics[0], "Helvetica", Clause2u, LayoutElementKind.Paragraph, 0, 1);
        AssertItem(doc.Diagnostics[1], "Times-Roman", Clause2u, LayoutElementKind.Paragraph, 0, 1);
    }

    [Fact]
    public void AListOfThreeItems_isOneItemWhoseCountIsSix()
    {
        using var doc = NewDocument(PdfConformance.PdfA2u);
        var list = new ListElement();
        list.Add("one");
        list.Add("two");
        list.Add("three");
        doc.Add(list);

        Save(doc);

        var item = Assert.Single(doc.Diagnostics);
        AssertItem(item, "Helvetica", Clause2u, LayoutElementKind.List, 0, 6);
    }
}
