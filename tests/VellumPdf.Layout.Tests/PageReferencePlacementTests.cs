// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.LinkSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Where the text of a page reference is placed once its label is known. Each document is built
/// twice: with the reference, and with the label it settles on written as literal text in a style
/// of its own. The two lay out the same text where the same text is wrapped, measured and drawn,
/// and the placeholder label gives a document that does not, so a test cannot pass by never
/// reading the label.
/// </summary>
public sealed class PageReferencePlacementTests
{
    private const string Prefix = "Section-";

    private static TextStyle Fresh(double size = 12) => new() { FontRef = Helv, FontSize = size };

    private static List<ContentStreamReadback.TextPlacement> Texts(Document doc) =>
        [.. ContentStreamReadback.TextPlacements(Content(Save(doc)))];

    // The target is on page 2, whose label is "Section-2".
    private static Document Labelled(double width, double height = 800)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, width, height),
            Margins = new EdgeInsets(10),
            PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, Prefix),
        };
        return doc;
    }

    private static void AddTarget(Document doc)
    {
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("target", Fresh()) { Anchor = "t" });
    }

    // ── A list entry: its gutter, its wrap and where its text starts ─────────

    // A marker 30pt wide in an indent of 10 sets a gutter of 30, and a label too wide to fit
    // beside it narrows the gutter to the indent. The placeholder "1" fits, the settled label does
    // not, so the entry's text starts at x = 40 with the placeholder and at x = 20 once it is
    // settled.
    private static Document ListDoc(string? literal)
    {
        var doc = Labelled(110);
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 10, DefaultStyle = Fresh(36) };
        var small = Fresh(15);
        list.Add(new ListItem([new TextRun("a ", small), literal is null ? TextRun.PageReference("t", small) : new TextRun(literal, Fresh(15))]));
        doc.Add(list);
        AddTarget(doc);
        return doc;
    }

    [Fact]
    public void AListEntry_holdsTheGutterWrapAndPositionOfTheLiteralLabel_afterTheLabelChanges()
    {
        var placeholder = Texts(ListDoc("1"));
        var settled = Texts(ListDoc(Prefix + "2"));

        // The precondition: the two literal documents put the entry's text at different x, so a
        // list that kept the entries built for the placeholder would be told apart.
        var itemAt = (List<ContentStreamReadback.TextPlacement> t) => t.First(p => p.Text.StartsWith('a')).X;
        Assert.NotEqual(itemAt(placeholder), itemAt(settled));

        ListRenderer.BuildCalls = 0;
        var reference = Texts(ListDoc(null));
        var builds = ListRenderer.BuildCalls;

        Assert.Equal(settled, reference);

        // The entries are built for the placeholder, once more for the settled label, and not again.
        ListRenderer.BuildCalls = 0;
        _ = Texts(ListDoc(Prefix + "2"));
        Assert.Equal(ListRenderer.BuildCalls + 1, builds);
    }

    // ── A run cell: its grid, its row height and its baseline ───────────────

    private static Document TableDoc(string? literal, double[] widths, TextStyle labelStyle)
    {
        var doc = Labelled(300);
        var table = new TableElement { DefaultCellStyle = Fresh(10) };
        if (widths.Length > 0) table.SetColumnWidths(widths);
        var label = literal is null ? TextRun.PageReference("t", labelStyle) : new TextRun(literal, Fresh(labelStyle.FontSize));
        table.AddRow()
            .AddCell(new Cell([new TextRun("ab ", Fresh(10)), label]))
            .AddCell("beside");
        doc.Add(table);
        doc.Add(new Paragraph("after the table", Fresh(10)));
        AddTarget(doc);
        return doc;
    }

    [Fact]
    public void AFixedWidthColumn_whoseLabelWrapsOneMoreLine_takesTheRowHeightOfTheLiteralLabel()
    {
        double[] widths = [60, 100];
        var placeholder = Texts(TableDoc("1", widths, Fresh(12)));
        var settled = Texts(TableDoc(Prefix + "2", widths, Fresh(12)));
        var reference = Texts(TableDoc(null, widths, Fresh(12)));

        // The precondition: the table is taller with the settled label, so the paragraph after it is
        // lower, and a grid kept from the placeholder would show.
        var after = (List<ContentStreamReadback.TextPlacement> t) => t.First(p => p.Text.StartsWith("after", StringComparison.Ordinal)).Y;
        Assert.True(after(settled) < after(placeholder) - 1);

        Assert.Equal(settled, reference);
    }

    [Fact]
    public void AnAutoWidthColumn_whoseWidestContentIsTheReference_takesTheColumnWidthOfTheLiteralLabel()
    {
        double[] widths = [];
        var placeholder = Texts(TableDoc("1", widths, Fresh(12)));
        var settled = Texts(TableDoc(Prefix + "2", widths, Fresh(12)));
        var reference = Texts(TableDoc(null, widths, Fresh(12)));

        // The second cell starts where the first column ends, which is wider with the settled label.
        var beside = (List<ContentStreamReadback.TextPlacement> t) => t.First(p => p.Text == "beside").X;
        Assert.True(beside(settled) > beside(placeholder) + 1);

        Assert.Equal(settled, reference);
    }

    [Fact]
    public void ARunCellWhoseReferenceIsTheLargestType_setsTheLineHeightAndTheBaselineOfTheLiteralLabel()
    {
        double[] widths = [200, 50];
        var big = Fresh(24);
        var settled = Texts(TableDoc(Prefix + "2", widths, big));
        var reference = Texts(TableDoc(null, widths, big));

        Assert.Equal(settled, reference);

        // The 24pt label sets the first baseline: 4pt of padding and 24pt of size below the top.
        var first = reference.First(p => p.Text.StartsWith("ab", StringComparison.Ordinal));
        Assert.Equal(800 - 10 - 4 - 24, first.Y, 3);
    }

    [Fact]
    public void ACellWhoseOnlyRunIsAReference_isSizedAndDrawnFromItsLabel()
    {
        Document Build(string? literal)
        {
            var doc = Labelled(300);
            var table = new TableElement { DefaultCellStyle = Fresh(10) }.SetColumnWidths(200);
            var label = literal is null ? TextRun.PageReference("t", Fresh(20)) : new TextRun(literal, Fresh(20));
            table.AddRow().AddCell(new Cell([label]));
            doc.Add(table);
            doc.Add(new Paragraph("after", Fresh(10)));
            AddTarget(doc);
            return doc;
        }

        Assert.Equal(Texts(Build(Prefix + "2")), Texts(Build(null)));
    }

    [Fact]
    public void ARowCutAcrossAPage_drawsTheLabelOnceAndLinksItOnceOnThePageThatHoldsIt()
    {
        var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 90), Margins = new EdgeInsets(10) };
        var table = new TableElement { DefaultCellStyle = Fresh(10) }.SetColumnWidths(250);
        var words = string.Join(" ", Enumerable.Range(0, 60).Select(i => "w" + i));
        table.AddRow().AddCell(new Cell([new TextRun(words + " see ", Fresh(10)), TextRun.PageReference("t", Fresh(10))]));
        doc.Add(table);
        doc.Add(new Paragraph("target") { Anchor = "t" });

        var pdf = Save(doc);

        Assert.Equal(1, Shown(pdf).Count(t => t.Trim().Length > 0 && char.IsDigit(t.Trim()[0]) && t.Trim().Length <= 2 && !t.Trim().StartsWith('w')));
        Assert.Single(Links(pdf), l => l.Dest == "t");
    }
}
