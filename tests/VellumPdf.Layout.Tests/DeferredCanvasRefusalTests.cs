// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The canvas refuses a non-finite operand while an element is drawn, which 2.3.3 did not: it wrote
/// the token and laid out the elements after it. With a <c>NaN</c> document top margin, a table is
/// drawn at a <c>NaN</c> position, and a paragraph after it is then too tall, which 2.3.3 refused
/// with <see cref="InvalidOperationException"/>. The draw now stops at the canvas refusal and the
/// layout goes on without drawing, so that refusal, and not the canvas's
/// <see cref="ArgumentException"/>, is what the caller sees, and the canvas's is raised when nothing
/// later throws. Measured on 2.3.3 (1c61fa6) with a top margin of <c>NaN</c> and the other three
/// edges 0 or 72: a table alone saves with the token <c>NaN</c> in it, and a table followed by a
/// paragraph throws "too tall".
/// </summary>
public sealed class DeferredCanvasRefusalTests
{
    private static Document NanTopMargin(double others)
    {
        var doc = new Document { Margins = new EdgeInsets { Top = double.NaN, Right = others, Bottom = others, Left = others } };
        var table = new TableElement();
        table.AddRow().AddCell("cell");
        doc.Add(table);
        return doc;
    }

    [Theory]
    [InlineData(0)]
    [InlineData(72)]
    public void TableAtANaNTopMargin_followedByAParagraph_isRefusedAsTooTall_asOn1c61fa6(double others)
    {
        using var doc = NanTopMargin(others);
        doc.Add(new Paragraph("after"));

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Contains("too tall", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TableAtANaNTopMargin_alone_isRefusedByTheCanvas()
    {
        using var doc = NanTopMargin(72);

        var ex = Assert.Throws<ArgumentException>(() => doc.Save(new MemoryStream()));

        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.ParamName);
    }

    /// <summary>
    /// What follows the refused element is laid out as on 2.3.3, not skipped: a later refusal that
    /// 2.3.3 made is the one raised, here an invalid column count on a table that comes after.
    /// </summary>
    [Fact]
    public void ARefusalLaterInTheDocument_isRaisedInsteadOfTheCanvasRefusal()
    {
        using var doc = NanTopMargin(72);
        var later = new TableElement();
        later.AddRow().AddCell(new Cell("x") { ColSpan = 0 });
        doc.Add(later);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Contains("ColSpan", ex.Message, StringComparison.Ordinal);
    }
}
