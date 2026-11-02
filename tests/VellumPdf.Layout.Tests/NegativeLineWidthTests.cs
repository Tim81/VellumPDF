// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #557: ISO 32000-2, 8.4.3.2 says a line width "shall be a non-negative number", and a negative
/// width was written as <c>-3 w</c>. Zero is legal and stays accepted. The tests assert the operator
/// each valid width writes, because a PDF is never empty and "a file came back" proves nothing.
/// </summary>
public sealed class NegativeLineWidthTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(10),
    };

    private static string Stream(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    [Theory]
    [InlineData(-3.0, "-3")]
    [InlineData(-0.5, "-0.5")]
    [InlineData(-1e-3, "-0.001")]
    public void LineSeparator_negativeWidth_isRefusedNamingTheWidthAndTheClause(double width, string rendered)
    {
        using var doc = Page();
        doc.Add(new LineSeparator { LineWidth = width });

        var ex = Assert.Throws<InvalidOperationException>(() => Stream(doc));

        Assert.Equal(
            $"A line separator has a line width of {rendered}, which cannot be drawn. " +
            "A line width must not be negative (ISO 32000-2, 8.4.3.2).",
            ex.Message);
    }

    [Fact]
    public void Table_negativeBorderWidth_isRefused()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style(), BorderWidth = -2 };
        table.AddRow().AddCell("x");
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Stream(doc));

        Assert.Equal(
            "A table has a line width of -2, which cannot be drawn. " +
            "A line width must not be negative (ISO 32000-2, 8.4.3.2).",
            ex.Message);
    }

    /// <summary>Negative infinity is still the non-finite refusal, not the negative one.</summary>
    [Fact]
    public void LineSeparator_negativeInfinity_keepsTheNonFiniteMessage()
    {
        using var doc = Page();
        doc.Add(new LineSeparator { LineWidth = double.NegativeInfinity });

        var ex = Assert.Throws<InvalidOperationException>(() => Stream(doc));

        Assert.Equal(
            "A line separator has a line width of negative infinity, which cannot be drawn. " +
            "A line width must be a finite number.",
            ex.Message);
    }

    [Theory]
    [InlineData(0.0, "0 w")]
    [InlineData(3.0, "3 w")]
    public void LineSeparator_zeroAndPositiveWidth_stillWriteTheirOperator(double width, string operatorText)
    {
        using var doc = Page();
        doc.Add(new LineSeparator { LineWidth = width });

        Assert.Contains(operatorText, Stream(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void Table_zeroBorderWidth_stillWritesTheOperator()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style(), BorderWidth = 0 };
        table.AddRow().AddCell("x");
        doc.Add(table);

        Assert.Contains("0 w", Stream(doc), StringComparison.Ordinal);
    }

    /// <summary>The pie chart already refused a negative stroke width, so #557 needed no change there.</summary>
    [Fact]
    public void PieChart_negativeStrokeWidth_wasAlreadyRefused()
    {
        using var doc = Page();
        doc.Add(new PieChart
        {
            Slices = [new PieSlice(1, ColorRgb.Black)],
            StrokeColor = ColorRgb.Black,
            StrokeWidth = -1,
        });

        var ex = Assert.Throws<ArgumentException>(() => Stream(doc));

        Assert.Equal("StrokeWidth", ex.ParamName);
    }
}
