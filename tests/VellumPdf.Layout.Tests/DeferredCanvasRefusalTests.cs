// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>The canvas refuses a non-finite operand while an element is drawn, and the layout goes on without drawing: a later refusal that 2.3.3 made (here "too tall", <see cref="InvalidOperationException"/>, for a paragraph after a table drawn at a <c>NaN</c> position) is what the caller sees, and the canvas's <see cref="ArgumentException"/> is raised when nothing later throws.</summary>
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

    /// <summary>What follows the refused element is laid out, not skipped: a later refusal that 2.3.3 made is the one raised, here an invalid column count on a table that comes after.</summary>
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
