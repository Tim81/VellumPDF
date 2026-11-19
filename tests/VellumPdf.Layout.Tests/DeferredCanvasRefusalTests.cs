// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;

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
    public void TableAtANaNTopMargin_followedByAParagraph_isRefusedAsTooTall(double others)
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

    // ── Which refusal is kept, and which exception is not deferred at all ────

    /// <summary>A renderer that places itself with no height and runs <paramref name="draw"/> when it is drawn.</summary>
    private sealed class DrawingRenderer(Action<DrawContext> draw) : IRenderer
    {
        public int Draws { get; private set; }

        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context)
        {
            Draws++;
            draw(context);
        }
    }

    private static (DocumentRenderer Renderer, MemoryStream Stream) RendererOf(params IRenderer[] renderers)
    {
        var renderer = new DocumentRenderer(new PdfDocument(), new PdfRectangle(0, 0, 300, 200), new EdgeInsets(10));
        foreach (var r in renderers) renderer.Add(r);
        return (renderer, new MemoryStream());
    }

    /// <summary>
    /// Two elements are each refused by the canvas, with different operands in the message (NaN, then positive
    /// infinity). The layout goes on past the first, and the one raised is the first, as on 2.3.3, which threw at it.
    /// </summary>
    [Fact]
    public void TwoCanvasRefusals_theFirstIsTheOneRaised_andBothElementsWereDrawn()
    {
        var first = new DrawingRenderer(c => c.Canvas.MoveTo(double.NaN, 0));
        var second = new DrawingRenderer(c => c.Canvas.MoveTo(double.PositiveInfinity, 0));
        var (renderer, stream) = RendererOf(first, second);

        var ex = Assert.Throws<ArgumentException>(() => renderer.Render(stream));

        Assert.Equal("PDF does not support NaN or Infinity as a real number value. Got: NaN", ex.Message);
        Assert.Equal(1, first.Draws);
        Assert.Equal(1, second.Draws);
    }

    /// <summary>
    /// The refusal that is held back is the canvas's, which names no parameter. The same message from
    /// <c>PdfReal</c> names <c>value</c>, as a link rectangle or a bookmark does; that one is not held back, so
    /// it propagates at once and no later element is drawn.
    /// </summary>
    [Fact]
    public void ARealNumberRefusalThatNamesAParameter_isNotDeferred()
    {
        var refusing = new DrawingRenderer(c => { _ = new PdfReal(double.NaN); });
        var after = new DrawingRenderer(_ => { });
        var (renderer, stream) = RendererOf(refusing, after);

        var ex = Assert.Throws<ArgumentException>(() => renderer.Render(stream));

        Assert.Equal("value", ex.ParamName);
        Assert.Equal(0, after.Draws);
    }

    /// <summary>An <see cref="ArgumentException"/> from a renderer that is not the canvas's refusal propagates at once.</summary>
    [Fact]
    public void AnUnrelatedArgumentException_fromADraw_isNotDeferred()
    {
        var refusing = new DrawingRenderer(_ => throw new ArgumentException("not a canvas refusal"));
        var after = new DrawingRenderer(_ => { });
        var (renderer, stream) = RendererOf(refusing, after);

        var ex = Assert.Throws<ArgumentException>(() => renderer.Render(stream));

        Assert.Equal("not a canvas refusal", ex.Message);
        Assert.Equal(0, after.Draws);
    }
}
