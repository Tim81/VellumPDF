// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #519: an element's own <c>Margins</c> went through no check. A non-finite inset reached a
/// comparison, and a comparison against <c>NaN</c> is false, so the element was reported as too tall
/// or, with <c>PdfCanvas</c> now refusing the token, as an unnamed canvas error. Each element that
/// carries margins is refused by name, with each of the three non-finite values on its own.
/// </summary>
public sealed class ElementMarginNonFiniteTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    public static TheoryData<string, double, string> Rows()
    {
        var rows = new TheoryData<string, double, string>();
        foreach (var element in new[] { "paragraph", "heading", "list", "table", "image", "separator" })
        {
            rows.Add(element, double.NaN, "NaN");
            rows.Add(element, double.PositiveInfinity, "positive infinity");
            rows.Add(element, double.NegativeInfinity, "negative infinity");
        }
        return rows;
    }

    private static string OwnerFor(string element) => element switch
    {
        "paragraph" or "heading" => "A paragraph or heading",
        "list" => "A list",
        "table" => "A table",
        "image" => "An image",
        _ => "A line separator",
    };

    [Theory]
    [MemberData(nameof(Rows))]
    public void ElementMargins_nonFinite_areRefusedByName(string element, double value, string rendered)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        var margins = new EdgeInsets(0, 0, 0, value);
        switch (element)
        {
            case "paragraph": doc.Add(new Paragraph("x", Style()) { Margins = margins }); break;
            case "heading": doc.Add(new Heading("x", Style()) { Margins = margins }); break;
            case "list": doc.Add(new ListElement { Margins = margins, DefaultStyle = Style() }.Add("x")); break;
            case "table":
                var table = new TableElement { Margins = margins, DefaultCellStyle = Style() };
                table.AddRow().AddCell("x");
                doc.Add(table);
                break;
            case "image": doc.Add(new LayoutImage(RefusalMessageTests.TwoByTwoImage()) { Margins = margins, Width = 20 }); break;
            default: doc.Add(new LineSeparator { Margins = margins }); break;
        }

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal($"{OwnerFor(element)} has a non-finite inset. Every inset must be a finite number.", ex.Message);
        Assert.NotNull(rendered);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void PieChartMargins_nonFinite_areRefusedByName(double value)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        doc.Add(new PieChart
        {
            Margins = new EdgeInsets(0, 0, 0, value),
            Slices = [new PieSlice(1, ColorRgb.Black)],
        });

        using var ms = new MemoryStream();
        var ex = Assert.Throws<ArgumentException>(() => doc.Save(ms));

        Assert.Equal("Margins", ex.ParamName);
        Assert.StartsWith("Pie chart margins must have finite insets.", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>A finite negative element margin saves today, and keeps saving.</summary>
    [Fact]
    public void ElementMargins_finiteNegative_stillSaves()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        doc.Add(new Paragraph("x", Style()) { Margins = new EdgeInsets(0, 0, 0, -5) });

        using var ms = new MemoryStream();
        var ex = Record.Exception(() => doc.Save(ms));

        Assert.Null(ex);
    }
}
