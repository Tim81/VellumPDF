// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="Document.BreakLongWordsAtPlaces"/> and the renderers a caller holds (#594). A built-in
/// renderer that two documents with different values lay out follows the document that lays it out,
/// so the document that leaves the setting off gets main's bytes and main's verdict whichever
/// renderers it shares with one that turns it on. A built-in renderer that a renderer of yours
/// lays out on a task or a thread during the save reads the document's setting there.
/// </summary>
public sealed class LongWordReuseTests
{
    private const string Path = "aa/bb/cccccccccc";

    private static TextStyle Courier() => new() { Font = Standard14.Courier, FontSize = 10 };

    private static Document NewDocument(bool on, double width = 100, double height = 300, double margin = 20) => new()
    {
        BreakLongWordsAtPlaces = on,
        PageSize = new PdfRectangle(0, 0, width, height),
        Margins = new EdgeInsets(margin),
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[] Placements(byte[] pdf) =>
        [.. ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(pdf))
            .Where(p => p.Text.Length > 0).Select(p => p.Text)];

    // A caller's renderer that lays a built-in one out inline, on a task or on a new thread.
    public enum Flow
    {
        Inline,
        Task,
        Thread,
    }

    private sealed class Forward(IRenderer inner, Flow flow) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            switch (flow)
            {
                case Flow.Task:
                    return Task.Run(() => inner.Layout(context)).GetAwaiter().GetResult();
                case Flow.Thread:
                    LayoutResult? result = null;
                    Exception? failure = null;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            result = inner.Layout(context);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }
                    });
                    thread.Start();
                    thread.Join();
                    if (failure is not null) throw failure;
                    return result!;
                default:
                    return inner.Layout(context);
            }
        }

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    private static ParagraphRenderer Paragraph() => new(new Paragraph(Path, Courier()));

    private static TableRenderer Table(double column = 72)
    {
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(column);
        table.AddRow().AddCell(string.Concat(Enumerable.Repeat("aaaaa/", 10)));
        return new TableRenderer(table);
    }

    private static byte[] SaveWith(IRenderer renderer, bool on, double width = 100, double height = 300, double margin = 20)
    {
        using var doc = NewDocument(on, width, height, margin);
        doc.Add(renderer);
        return Save(doc);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AParagraphRendererReusedAcrossDocuments_followsTheDocumentThatLaysItOut(bool first, bool second)
    {
        var shared = Paragraph();
        var one = SaveWith(shared, first);
        var two = SaveWith(shared, second);

        // Each document is what a renderer made for it alone gives, byte for byte.
        Assert.Equal(SaveWith(Paragraph(), first), one);
        Assert.Equal(SaveWith(Paragraph(), second), two);
        Assert.NotEqual(one, two);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ATableRendererReusedAcrossDocuments_followsTheDocumentThatLaysItOut(bool first, bool second)
    {
        var shared = Table();
        var one = SaveWith(shared, first, 92, 180, 10);
        var two = SaveWith(shared, second, 92, 180, 10);

        Assert.Equal(SaveWith(Table(), first, 92, 180, 10), one);
        Assert.Equal(SaveWith(Table(), second, 92, 180, 10), two);
        Assert.NotEqual(one, two);
    }

    [Fact]
    public void ATableRendererReusedInAnOnDocument_drawsEveryLineInsideItsCell()
    {
        // The cell of the off document is 6 lines tall and the on document wraps into 10, so a draw
        // under the other document's setting puts four lines below the cell's bottom edge. The
        // rectangle's height is the figure a drawn line has to sit within.
        var shared = Table();
        SaveWith(shared, on: false, 92, 180, 10);
        var pdf = SaveWith(shared, on: true, 92, 180, 10);

        Assert.Equal(SaveWith(Table(), on: true, 92, 180, 10), pdf);
        Assert.Equal(10, Placements(pdf).Length);
    }

    // A url of 60 characters is 6 lines cut at the glyph and 10 after a solidus, and a page holds one
    // line, so 6,000 of them are 36,000 pages and 60,000 pages, over the limit of 50,000.
    private static ParagraphRenderer ManyAddresses()
    {
        var word = string.Concat(Enumerable.Repeat("aaaaa/", 10));
        return new ParagraphRenderer(new Paragraph(string.Join(' ', Enumerable.Repeat(word, 6000)), Courier()));
    }

    [Fact]
    public void ARendererThatAnOnDocumentRefusedAtTheLimit_savesInAnOffDocumentAsAFreshOneDoes()
    {
        var shared = ManyAddresses();
        using (var on = NewDocument(true, 80, 37))
        {
            on.Margins = new EdgeInsets(10);
            on.Add(shared);
            Assert.Throws<InvalidOperationException>(() => Save(on));
        }

        using var off = NewDocument(false, 80, 37);
        off.Margins = new EdgeInsets(10);
        off.Add(shared);
        var reused = Save(off);

        using var fresh = NewDocument(false, 80, 37);
        fresh.Margins = new EdgeInsets(10);
        fresh.Add(ManyAddresses());
        Assert.Equal(Save(fresh), reused);
    }

    [Theory]
    [InlineData(Flow.Task)]
    [InlineData(Flow.Thread)]
    public void AParagraphLaidOutOnAnotherFlowDuringTheSave_readsTheDocumentsSetting(Flow flow)
    {
        var inline = SaveWith(new Forward(Paragraph(), Flow.Inline), on: true);

        Assert.Equal(inline, SaveWith(new Forward(Paragraph(), flow), on: true));
        Assert.Equal(["aa/bb/", "cccccccccc"], Placements(inline));

        var offInline = SaveWith(new Forward(Paragraph(), Flow.Inline), on: false);
        Assert.Equal(offInline, SaveWith(new Forward(Paragraph(), flow), on: false));
        Assert.Equal(["aa/bb/cccc", "cccccc"], Placements(offInline));
    }

    [Theory]
    [InlineData(Flow.Task)]
    [InlineData(Flow.Thread)]
    public void ATableLaidOutOnAnotherFlowDuringTheSave_isMeasuredAndDrawnAsTheSameFlowDoes(Flow flow)
    {
        var inline = SaveWith(new Forward(Table(), Flow.Inline), on: true, 92, 180, 10);

        Assert.Equal(inline, SaveWith(new Forward(Table(), flow), on: true, 92, 180, 10));
        Assert.Equal(SaveWith(new Forward(Table(), Flow.Inline), on: false, 92, 180, 10),
            SaveWith(new Forward(Table(), flow), on: false, 92, 180, 10));
    }
}
