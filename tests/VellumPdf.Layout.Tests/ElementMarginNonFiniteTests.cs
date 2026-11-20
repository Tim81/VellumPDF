// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>A non-finite element inset is not checked by the element itself: a comparison against <c>NaN</c> is false, so the element is reported as too tall or as an unnamed canvas error from <c>PdfCanvas</c>; a line separator is refused by name before the layout (see <see cref="Document"/> for the rule covering the rest).</summary>
public sealed class ElementMarginNonFiniteTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    // Only the line separator is refused before the layout, as in 2.3.3: it throws for every edge and every non-finite value,
    // with the same message. Every other element runs the layout and is covered by KeepRenderingTests.
    public static TheoryData<string, double> EdgesAndValues()
    {
        var data = new TheoryData<string, double>();
        foreach (var edge in new[] { "top", "right", "bottom", "left" })
        {
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                data.Add(edge, value);
            }
        }

        return data;
    }

    // EdgeInsets takes (top, right, bottom, left); the non-finite value goes on one edge and the other three stay 0, so
    // a check that skips that edge is the only thing that can let the document through.
    private static EdgeInsets OneEdge(string edge, double value) => new(
        edge == "top" ? value : 0,
        edge == "right" ? value : 0,
        edge == "bottom" ? value : 0,
        edge == "left" ? value : 0);

    [Theory]
    [MemberData(nameof(EdgesAndValues))]
    public void LineSeparatorMargins_nonFinite_areRefusedByName(string edge, double value)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        doc.Add(new LineSeparator { Margins = OneEdge(edge, value) });

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal("A line separator has a non-finite inset. Every inset must be a finite number.", ex.Message);
    }

    [Theory]
    [MemberData(nameof(EdgesAndValues))]
    public void CellPadding_nonFinite_isRefusedByName(string edge, double value)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell(new Cell("x") { Padding = OneEdge(edge, value) });
        doc.Add(table);

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal("Table row 0, cell 0 has a non-finite inset. Every inset must be a finite number.", ex.Message);
    }

    /// <summary>
    /// A finite negative inset saves on each of the two members that <c>LayoutValidation.ValidateInsets</c> checks, a
    /// line separator's margins and a cell's padding, and on a paragraph, which it never checks; a check that refused a
    /// negative inset would fail the first two.
    /// </summary>
    [Fact]
    public void ElementMargins_finiteNegative_stillSaves()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        doc.Add(new Paragraph("x", Style()) { Margins = new EdgeInsets(0, 0, 0, -5) });
        doc.Add(new LineSeparator { Margins = new EdgeInsets(-5, -5, -5, -5) });
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell(new Cell("x") { Padding = new EdgeInsets(-1) });
        doc.Add(table);

        using var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        // The separator still draws its line.
        Assert.Matches(@"(?m)^[-\d.]+ [-\d.]+ l\s*$", stream);
    }
}
