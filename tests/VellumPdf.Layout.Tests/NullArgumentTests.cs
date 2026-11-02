// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #523, #531 and #547: a null argument was stored without a check and surfaced as
/// <see cref="NullReferenceException"/> from a later call the caller did not make, usually
/// <c>Document.Save</c>. Each call below now throws <see cref="ArgumentNullException"/> from the call
/// that took the null, naming the parameter. Every row of the three issues' tables is one case, and
/// the controls show the same calls with a real value still work.
/// </summary>
public sealed class NullArgumentTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private sealed record Case(string Name, string Param, Action Call);

    private static readonly Case[] _cases =
    [
        // #547: text and runs
        new("new Paragraph((string)null)", "text", () => _ = new Paragraph((string)null!)),
        new("Paragraph.Add(null)", "text", () => new Paragraph("x").Add(null!)),
        new("new Heading(null)", "text", () => _ = new Heading(null!)),
        new("new Paragraph(runs: null)", "runs", () => _ = new Paragraph((IEnumerable<TextRun>)null!)),
        new("new Paragraph([null run])", "runs", () => _ = new Paragraph(new TextRun[] { null! })),
        new("new TextRun(\"x\", null)", "Style", () => _ = new TextRun("x", null!)),
        new("new TextRun(null, style)", "Text", () => _ = new TextRun(null!, Style())),
        // #547 and #523: lists
        new("new ListItem(null)", "text", () => _ = new ListItem(null!)),
        new("ListElement.Add((string)null)", "text", () => new ListElement().Add((string)null!)),
        new("ListItem.AddChild((string)null)", "text", () => new ListItem("p").AddChild((string)null!)),
        new("ListElement.Add((ListItem)null)", "item", () => new ListElement().Add((ListItem)null!)),
        new("new ListElement(style, [null item])", "items", () => _ = new ListElement(ListStyle.Unordered, new ListItem[] { null! })),
        new("ListItem.AddChild((ListItem)null)", "child", () => new ListItem("p").AddChild((ListItem)null!)),
        // #547 and #523: tables
        new("Row.AddCell((Cell)null)", "cell", () => new TableElement().AddRow().AddCell((Cell)null!)),
        new("new Cell(null)", "content", () => _ = new Cell(null!)),
        new("Row.AddCell((string)null)", "content", () => new TableElement().AddRow().AddCell((string)null!)),
        // #547: image, chart, band
        new("new LayoutImage(null)", "image", () => _ = new LayoutImage(null!)),
        new("new PieChart { Slices = null }", "value", () => _ = new PieChart { Slices = null! }),
        // #531
        new("new RunningBand(null)", "template", () => _ = new RunningBand(null!)),
        // #523
        new("DocumentRenderer.Add(null)", "renderer", () => new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, 400, 400)).Add(null!)),
        // #547: every public renderer constructor
        new("new ParagraphRenderer(null)", "para", () => _ = new ParagraphRenderer(null!)),
        new("new ListRenderer(null)", "list", () => _ = new ListRenderer(null!)),
        new("new TableRenderer(null)", "table", () => _ = new TableRenderer(null!)),
        new("new LayoutImageRenderer(null)", "img", () => _ = new LayoutImageRenderer(null!)),
        new("new LineSeparatorRenderer(null)", "sep", () => _ = new LineSeparatorRenderer(null!)),
        new("new PieChartRenderer(null)", "chart", () => _ = new PieChartRenderer(null!)),
        // Document.Add overloads name their own parameter, not the renderer constructor's
        new("Document.Add((Paragraph)null)", "paragraph", () => new Document().Add((Paragraph)null!)),
        new("Document.Add((LineSeparator)null)", "separator", () => new Document().Add((LineSeparator)null!)),
        new("Document.Add((TableElement)null)", "table", () => new Document().Add((TableElement)null!)),
        new("Document.Add((LayoutImage)null)", "image", () => new Document().Add((LayoutImage)null!)),
        new("Document.Add((PieChart)null)", "chart", () => new Document().Add((PieChart)null!)),
        new("Document.Add((ListElement)null)", "list", () => new Document().Add((ListElement)null!)),
        new("Document.Add((Heading)null)", "heading", () => new Document().Add((Heading)null!)),
        new("Document.Add((string)null)", "text", () => new Document().Add((string)null!)),
        new("Document.SetHeader(null)", "template", () => new Document().SetHeader(null!)),
        new("Document.SetFooter(null)", "template", () => new Document().SetFooter(null!)),
    ];

    public static TheoryData<string> Names()
    {
        var rows = new TheoryData<string>();
        foreach (var c in _cases) rows.Add(c.Name);
        return rows;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void NullArgument_isRefusedByTheCallThatTookIt(string name)
    {
        var c = _cases.Single(x => x.Name == name);

        var ex = Assert.Throws<ArgumentNullException>(c.Call);

        Assert.Equal(c.Param, ex.ParamName);
    }

    /// <summary>The same calls with real values are unaffected, and the document still saves.</summary>
    [Fact]
    public void RealArguments_stillWork_andTheDocumentSaves()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 400), Margins = new EdgeInsets(10) };
        doc.Add(new Paragraph([new TextRun("runs ", Style()), new TextRun("here", Style())]));
        doc.Add(new Heading("title", Style()));
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [new ListItem("one", Style()).AddChild("child")]).Add("two", Style()));
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell(new Cell("cell")).AddCell("text");
        doc.Add(table);
        doc.Header = new RunningBand("h {page}", Style()) { Height = 20 };

        using var ms = new MemoryStream();
        doc.Save(ms);

        Assert.True(ms.Length > 0);
    }

    [Fact]
    public void EmptyStrings_areNotNull_andAreAccepted()
    {
        _ = new Paragraph(string.Empty);
        _ = new Heading(string.Empty);
        _ = new ListItem(string.Empty);
        _ = new Cell(string.Empty);
        _ = new RunningBand(string.Empty);
        _ = new TextRun(string.Empty, Style());
    }
}
