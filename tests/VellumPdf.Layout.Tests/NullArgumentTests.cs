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
/// <c>Document.Save</c>. The rule is that a null is refused where 2.3.3 dereferenced it, and not
/// before. Each call in the case table throws <see cref="ArgumentNullException"/> from the call that
/// took the null, naming the parameter. Every row of the three issues' tables is one case, and the
/// controls show the same calls with a real value still work. A null that 2.3.3 stored, and
/// dereferenced only in some shapes or only when it was drawn, is stored here too and refused where
/// 2.3.3 failed (#484); those tests are at the end of the class, and <c>NullDeferralTests</c> holds
/// the table of shapes.
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
        new("new TextRun(null, style)", "Text", () => _ = new TextRun(null!, Style())),
        // #547 and #523: lists
        new("ListElement.Add((string)null)", "text", () => new ListElement().Add((string)null!)),
        new("ListElement.Add((ListItem)null)", "item", () => new ListElement().Add((ListItem)null!)),
        new("new ListElement(style, [null item])", "items", () => _ = new ListElement(ListStyle.Unordered, new ListItem[] { null! })),
        // #547 and #523: tables
        new("Row.AddCell((Cell)null)", "cell", () => new TableElement().AddRow().AddCell((Cell)null!)),
        // #547: image, chart, band
        new("new LayoutImage(null)", "image", () => _ = new LayoutImage(null!)),
        new("new PieChart { Slices = null }", "value", () => _ = new PieChart { Slices = null! }),
        // #531
        // #523
        new("new DocumentRenderer(null)", "pdf", () => _ = new DocumentRenderer(null!)),
        new("new DocumentRenderer(null, pageSize)", "pdf", () => _ = new DocumentRenderer(null!, new PdfRectangle(0, 0, 400, 400))),
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

    // ── A null cell text: stored, and refused where a column is sized automatically ──

    [Fact]
    public void NullCellText_isStoredAsGiven_asOn1c61fa6()
    {
        Assert.Null(new Cell(null!).Content);
        Assert.Null(Assert.Single(new TableElement().AddRow().AddCell((string)null!).Cells).Content);
    }

    private static Document ThreeCellRow(bool explicitWidths)
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 200), Margins = new EdgeInsets(10) };
        var table = new TableElement { DefaultCellStyle = Style() };
        if (explicitWidths) table.SetColumnWidths(60, 60, 60);
        table.AddRow().AddCell("A").AddCell((string)null!).AddCell("B");
        doc.Add(table);
        return doc;
    }

    [Fact]
    public void TableWithANullCell_andEveryColumnWidthSet_draws_theCellEmpty()
    {
        using var doc = ThreeCellRow(explicitWidths: true);
        using var ms = new MemoryStream();
        doc.Save(ms);

        var placements = ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()));
        Assert.Equal(["A", "", "B"], placements.Select(p => p.Text));
    }

    [Fact]
    public void TableWithANullCell_andAnAutoSizedColumn_isRefusedNamingTheContent()
    {
        using var doc = ThreeCellRow(explicitWidths: false);
        using var ms = new MemoryStream();

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(ms));

        Assert.Equal("content", ex.ParamName);
    }

    // ── Nulls that keep rendering ────────────────────────────────────────

    private static IReadOnlyList<ContentStreamReadback.TextPlacement> Placements(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()));
    }

    [Fact]
    public void TextRun_withANullStyle_isStored_asOn1c61fa6()
    {
        Assert.Null(new TextRun("b", null!).Style);
    }

    [Theory]
    [InlineData("b", true)]
    [InlineData("\u00A0", true)]
    [InlineData("a\nb", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("\t", false)]
    [InlineData("\n", false)]
    public void TextRun_withANullStyle_isRefusedOnlyWhenItYieldsAWord(string text, bool refused)
    {
        using var doc = new Document();
        doc.Add(new Paragraph(new[] { new TextRun("a", Style()), new TextRun(text, null!) }));
        using var ms = new MemoryStream();

        if (refused)
        {
            var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(ms));
            Assert.Equal("Style", ex.ParamName);
        }
        else
        {
            doc.Save(ms);
            Assert.True(ms.Length > 0);
        }
    }

    [Fact]
    public void TextRun_withANullText_stillThrowsFromTheConstructor()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => new TextRun(null!, null!));

        Assert.Equal("Text", ex.ParamName);
    }

    private static Document ListWithGrandchild(Action<ListItem> addToChild)
    {
        var doc = new Document();
        var child = new ListItem("child", Style());
        addToChild(child);
        doc.Add(new ListElement(ListStyle.Unordered, [new ListItem("first", Style()).AddChild(child), new ListItem("second", Style())]));
        return doc;
    }

    [Fact]
    public void NullGrandchild_isSkipped_andTheListRendersAsIfItWereNeverAdded()
    {
        using var withNull = ListWithGrandchild(child => child.AddChild((ListItem)null!));
        using var without = ListWithGrandchild(_ => { });

        var placements = Placements(withNull);

        Assert.Contains("first", placements.Select(p => p.Text));
        Assert.Contains("child", placements.Select(p => p.Text));
        Assert.Contains("second", placements.Select(p => p.Text));
        Assert.Equal(Placements(without), placements);
    }

    [Fact]
    public void NullGrandchild_isNotStored_soChildrenNeverHoldsANull()
    {
        var child = new ListItem("child").AddChild((ListItem)null!);

        Assert.Null(child.Children);
    }

    [Fact]
    public void NullChildOfATopLevelItem_isRefusedBySaveNamingChild()
    {
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.Unordered).Add(new ListItem("first", Style()).AddChild((ListItem)null!)));
        using var ms = new MemoryStream();

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(ms));

        Assert.Equal("child", ex.ParamName);
    }

    [Fact]
    public void NullTextGrandchild_isSkipped_andTheListRendersAsIfItWereNeverAdded()
    {
        foreach (var make in new Func<ListItem, ListItem>[]
        {
            c => c.AddChild((string)null!),
            c => c.AddChild(new ListItem(null!)),
        })
        {
            using var withNull = ListWithGrandchild(c => make(c));
            using var without = ListWithGrandchild(_ => { });

            Assert.Equal(Placements(without), Placements(withNull));
        }
    }

    [Fact]
    public void NullText_isStoredAsAnEmptyString()
    {
        Assert.Equal(string.Empty, new ListItem(null!).Text);
    }

    [Fact]
    public void NullTextOnATopLevelItem_isRefusedBySaveNamingText()
    {
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.Unordered).Add(new ListItem(null!, Style())));
        using var ms = new MemoryStream();

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(ms));

        Assert.Equal("text", ex.ParamName);
    }

    [Fact]
    public void NullTextOnAChildOfATopLevelItem_isRefusedBySaveNamingText()
    {
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.Unordered).Add(new ListItem("first", Style()).AddChild((string)null!)));
        using var ms = new MemoryStream();

        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(ms));

        Assert.Equal("text", ex.ParamName);
    }
}
