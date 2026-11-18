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
/// or, with <c>PdfCanvas</c> now refusing the token, as an unnamed canvas error. A line separator is
/// refused by name before the layout; see <see cref="Document"/> for the rule that covers the rest.
/// </summary>
public sealed class ElementMarginNonFiniteTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    // Only the line separator is refused before the layout: it threw for every edge and every
    // non-finite value on 1c61fa6, with the same message. Every other element runs the layout as it
    // did there and is covered by KeepRenderingTests.
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
