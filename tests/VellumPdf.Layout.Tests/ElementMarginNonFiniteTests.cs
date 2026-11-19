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
    public static TheoryData<double> Values() => new()
    {
        double.NaN,
        double.PositiveInfinity,
        double.NegativeInfinity,
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void LineSeparatorMargins_nonFinite_areRefusedByName(double value)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 300, 300),
            Margins = new EdgeInsets(10),
        };
        doc.Add(new LineSeparator { Margins = new EdgeInsets(0, 0, 0, value) });

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal("A line separator has a non-finite inset. Every inset must be a finite number.", ex.Message);
    }

    /// <summary>A finite negative element margin saves.</summary>
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
