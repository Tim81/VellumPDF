// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.Concurrent;
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
/// <see cref="LayoutContext.BreakLongWordsAtPlaces"/> (#594). The value travels on the context, a
/// built-in renderer keeps the one it first wrapped text with, and a later layout with the other
/// value throws. Nothing else carries it: no thread, task or save leaves a value behind for a
/// later layout to read.
/// </summary>
public sealed class LongWordReuseTests
{
    private const string Path = "aa/bb/cccccccccc";

    private static readonly string[] AtTheGlyph = ["aa/bb/cccc", "cccccc"];
    private static readonly string[] AtThePlace = ["aa/bb/", "cccccccccc"];

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

    private static ParagraphRenderer Paragraph() => new(new Paragraph(Path, Courier()));

    // A table of one cell holding one 60-character word: 6 lines cut at the glyph, 10 after a solidus.
    private static TableRenderer Table()
    {
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        table.AddRow().AddCell(string.Concat(Enumerable.Repeat("aaaaa/", 10)));
        return new TableRenderer(table);
    }

    private static byte[] SaveWith(IRenderer renderer, bool on, bool table = false)
    {
        using var doc = table ? NewDocument(on, 92, 180, 10) : NewDocument(on);
        doc.Add(renderer);
        return Save(doc);
    }

    // ── The value reaches a renderer of yours' child through a derived context ──

    public enum Flow
    {
        Inline,
        Task,
        Thread,
        ParallelFor,
        SuppressFlow,
    }

    // How the child's context is made: derived from the one the caller was given, as a renderer that
    // passes the value on does, or built fresh, as one that does not.
    public enum Derive
    {
        WithArea,
        Fresh,
    }

    private sealed class Forward(IRenderer inner, Flow flow, Derive derive = Derive.WithArea) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context)
        {
            var child = derive == Derive.WithArea ? context.WithArea(context.Area) : new LayoutContext(context.Area);
            switch (flow)
            {
                case Flow.Task:
                    return Task.Run(() => inner.Layout(child)).GetAwaiter().GetResult();
                case Flow.Thread:
                    LayoutResult? result = null;
                    Exception? failure = null;
                    var thread = new Thread(() =>
                    {
                        try
                        {
                            result = inner.Layout(child);
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
                case Flow.ParallelFor:
                    LayoutResult? parallel = null;
                    Parallel.For(0, 1, _ => parallel = inner.Layout(child));
                    return parallel!;
                case Flow.SuppressFlow:
                    LayoutResult? suppressed = null;
                    Exception? suppressedFailure = null;
                    using (ExecutionContext.SuppressFlow())
                    {
                        var worker = new Thread(() =>
                        {
                            try
                            {
                                suppressed = inner.Layout(child);
                            }
                            catch (Exception ex)
                            {
                                suppressedFailure = ex;
                            }
                        });
                        worker.Start();
                        worker.Join();
                    }

                    if (suppressedFailure is not null) throw suppressedFailure;
                    return suppressed!;
                default:
                    return inner.Layout(child);
            }
        }

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    [Theory]
    [InlineData(Flow.Inline)]
    [InlineData(Flow.Task)]
    [InlineData(Flow.Thread)]
    [InlineData(Flow.ParallelFor)]
    [InlineData(Flow.SuppressFlow)]
    public void AParagraphLaidOutThroughADerivedContext_readsTheDocumentsValueOnAnyFlow(Flow flow)
    {
        var on = SaveWith(new Forward(Paragraph(), flow), on: true);
        Assert.Equal(AtThePlace, Placements(on));

        var off = SaveWith(new Forward(Paragraph(), flow), on: false);
        Assert.Equal(AtTheGlyph, Placements(off));
    }

    [Theory]
    [InlineData(Flow.Inline)]
    [InlineData(Flow.Task)]
    [InlineData(Flow.Thread)]
    [InlineData(Flow.ParallelFor)]
    [InlineData(Flow.SuppressFlow)]
    public void ATableLaidOutThroughADerivedContext_isMeasuredAndDrawnWithTheDocumentsValueOnAnyFlow(Flow flow)
    {
        var onInline = SaveWith(new Forward(Table(), Flow.Inline), on: true, table: true);

        Assert.Equal(onInline, SaveWith(new Forward(Table(), flow), on: true, table: true));
        Assert.Equal(10, Placements(onInline).Length);
        Assert.Equal(6, Placements(SaveWith(new Forward(Table(), flow), on: false, table: true)).Length);
    }

    [Theory]
    [InlineData(Flow.Inline)]
    [InlineData(Flow.Task)]
    [InlineData(Flow.Thread)]
    public void AParagraphLaidOutThroughAFreshContext_readsFalse_whateverTheDocumentSays(Flow flow)
    {
        var fresh = SaveWith(new Forward(Paragraph(), flow, Derive.Fresh), on: true);

        Assert.Equal(AtTheGlyph, Placements(fresh));
        Assert.Equal(SaveWith(new Forward(Paragraph(), flow, Derive.Fresh), on: false), fresh);
    }

    [Fact]
    public void ATableLaidOutThroughAFreshContext_readsFalse_whateverTheDocumentSays()
    {
        var fresh = SaveWith(new Forward(Table(), Flow.Inline, Derive.Fresh), on: true, table: true);

        Assert.Equal(6, Placements(fresh).Length);
        Assert.Equal(SaveWith(new Forward(Table(), Flow.Inline, Derive.Fresh), on: false, table: true), fresh);
    }

    // ── A renderer keeps the value it first wrapped with ─────────────────────

    private const string ParagraphRefusal =
        "A paragraph renderer, or a piece it was split into, was first laid out with LayoutContext.BreakLongWordsAtPlaces set to {0} "
        + "and is now laid out with it set to {1}. Its lines and the start lines of its pieces belong to the first value. "
        + "Lay a new renderer out for the other value.";

    private const string TableRefusal =
        "A table renderer, or a piece it was split into, was first laid out with LayoutContext.BreakLongWordsAtPlaces set to {0} "
        + "and is now laid out with it set to {1}. Its measured heights and the lines of a row it cut belong to the first value. "
        + "Lay a new renderer out for the other value.";

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AParagraphRendererLaidOutInADocumentWithTheOtherValue_isRefused(bool first, bool second)
    {
        var shared = Paragraph();
        SaveWith(shared, first);

        var ex = Assert.Throws<InvalidOperationException>(() => SaveWith(shared, second));

        Assert.Equal(string.Format(ParagraphRefusal, first ? "true" : "false", second ? "true" : "false"), ex.Message);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void ATableRendererLaidOutInADocumentWithTheOtherValue_isRefused(bool first, bool second)
    {
        var shared = Table();
        SaveWith(shared, first, table: true);

        var ex = Assert.Throws<InvalidOperationException>(() => SaveWith(shared, second, table: true));

        Assert.Equal(string.Format(TableRefusal, first ? "true" : "false", second ? "true" : "false"), ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARendererLaidOutAgainWithTheSameValue_savesAsAFreshOneDoes(bool value)
    {
        var shared = Paragraph();
        SaveWith(shared, value);

        Assert.Equal(SaveWith(Paragraph(), value), SaveWith(shared, value));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APieceOfAParagraphIsBoundToTheValueItWasCutUnder(bool cutUnder)
    {
        // The caller lays the paragraph out in a box of one line, outside any save, and keeps the
        // overflow, which starts at line 1 of the wrap it was cut from. Laid out in a document with
        // the other value it would start at line 1 of another wrap and draw some characters twice or
        // not at all, so it is refused. In a document with the same value it draws the rest.
        var first = Paragraph().Layout(new LayoutContext(new LayoutBox(0, 0, 60, 12)) { BreakLongWordsAtPlaces = cutUnder });
        var piece = first.OverflowRenderer!;

        var ex = Assert.Throws<InvalidOperationException>(() => SaveWith(piece, !cutUnder));
        Assert.Equal(string.Format(ParagraphRefusal, cutUnder ? "true" : "false", cutUnder ? "false" : "true"), ex.Message);

        Assert.Equal(cutUnder ? ["cccccccccc"] : ["cccccc"], Placements(SaveWith(piece, cutUnder)));
    }

    // A renderer of the caller's that forwards to a built-in one and keeps the first overflow piece it
    // returns, as one that places pieces itself does.
    private sealed class KeepOverflow(IRenderer inner) : IRenderer
    {
        internal IRenderer? Piece { get; private set; }

        public LayoutResult Layout(LayoutContext context)
        {
            var result = inner.Layout(context);
            if (Piece is null && result.Status == LayoutResult.Outcome.Partial) Piece = result.OverflowRenderer;
            return result;
        }

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APieceOfATableThatResumesACutRowIsBoundToTheValueItWasCutUnder(bool cutUnder)
    {
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        var word = string.Concat(Enumerable.Repeat("aaaaa/", 10));
        table.AddRow().AddCell(string.Join(' ', Enumerable.Repeat(word, 4)));
        var keep = new KeepOverflow(new TableRenderer(table));
        SaveWith(keep, cutUnder, table: true);
        var piece = keep.Piece;
        Assert.NotNull(piece);

        var ex = Assert.Throws<InvalidOperationException>(() => SaveWith(piece, !cutUnder, table: true));
        Assert.Equal(string.Format(TableRefusal, cutUnder ? "true" : "false", cutUnder ? "false" : "true"), ex.Message);
    }

    // ── Nothing outlives a save ──────────────────────────────────────────────

    // A renderer of a caller's that keeps one worker thread for the program and sends every layout to
    // it, with the context it was given.
    private sealed class Worker : IRenderer
    {
        private static readonly BlockingCollection<(IRenderer Inner, LayoutContext Context, TaskCompletionSource<LayoutResult> Done)> s_jobs = [];
        private static readonly Thread s_thread = Start();

        private readonly IRenderer _inner;

        internal Worker(IRenderer inner) => _inner = inner;

        private static Thread Start()
        {
            var thread = new Thread(() =>
            {
                foreach (var (inner, context, done) in s_jobs.GetConsumingEnumerable())
                {
                    try
                    {
                        done.SetResult(inner.Layout(context));
                    }
                    catch (Exception ex)
                    {
                        done.SetException(ex);
                    }
                }
            })
            { IsBackground = true };
            thread.Start();
            return thread;
        }

        public LayoutResult Layout(LayoutContext context)
        {
            _ = s_thread;
            var done = new TaskCompletionSource<LayoutResult>();
            s_jobs.Add((_inner, context.WithArea(context.Area), done));
            return done.Task.GetAwaiter().GetResult();
        }

        public void Draw(DrawContext context) => _inner.Draw(context);
    }

    [Fact]
    public void AWorkerThatAnOnSaveStarted_readsTheLaterOffDocumentsValue()
    {
        Assert.Equal(AtThePlace, Placements(SaveWith(new Worker(Paragraph()), on: true)));

        var later = SaveWith(new Worker(Paragraph()), on: false);

        Assert.Equal(AtTheGlyph, Placements(later));
        Assert.Equal(SaveWith(new Forward(Paragraph(), Flow.Inline), on: false), later);
    }

    [Fact]
    public async Task WorkThatOutlivesTheSave_readsTheContextItWasGiven_andNothingTheSaveLeft()
    {
        // A renderer of the caller's starts two tasks during an on save. They lay a url paragraph out
        // after the save has returned: under a context the task builds itself it reads false (6
        // lines), and under the one it captured it reads that context's value (10 lines).
        var gate = new ManualResetEventSlim();
        Task<LayoutResult>? late = null;
        Task<LayoutResult>? captured = null;
        var area = new LayoutBox(0, 0, 60, 300);
        static ParagraphRenderer Url() => new(new Paragraph(string.Concat(Enumerable.Repeat("aaaaa/", 10)), Courier()));

        var starter = new Starter(context =>
        {
            late = Task.Run(() =>
            {
                gate.Wait();
                return Url().Layout(new LayoutContext(area));
            });
            captured = Task.Run(() =>
            {
                gate.Wait();
                return Url().Layout(context.WithArea(area));
            });
        });

        SaveWith(starter, on: true);
        gate.Set();

        Assert.Equal(6, (await late!).OccupiedArea!.Value.Height / 12);
        Assert.Equal(10, (await captured!).OccupiedArea!.Value.Height / 12);
    }

    private sealed class Starter(Action<LayoutContext> start) : IRenderer
    {
        private bool _started;

        public LayoutResult Layout(LayoutContext context)
        {
            if (!_started)
            {
                _started = true;
                start(context);
            }

            return LayoutResult.Full(new LayoutBox(context.Area.X, context.Area.Y, 0, 0));
        }

        public void Draw(DrawContext context)
        {
        }
    }

    // ── The multi-page table and the limit ───────────────────────────────────

    [Fact]
    public void ATableThatSplitsOverPages_drawsEveryLineOfEveryPieceAfterThePlace()
    {
        using var doc = NewDocument(on: true, 92, 180, 10);
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        for (var i = 0; i < 3; i++)
            table.AddRow().AddCell(string.Concat(Enumerable.Repeat("aaaaa/", 10)));
        doc.Add(table);

        var lines = Placements(Save(doc));

        Assert.Equal(30, lines.Length);
        Assert.All(lines, l => Assert.Equal("aaaaa/", l));
    }

    // A url of 60 characters is 6 lines cut at the glyph and 10 after a solidus, and a page holds one
    // line, so 6,000 of them are 36,000 pages and 60,000 pages, over the limit of 50,000.
    private static ParagraphRenderer ManyAddresses()
    {
        var word = string.Concat(Enumerable.Repeat("aaaaa/", 10));
        return new ParagraphRenderer(new Paragraph(string.Join(' ', Enumerable.Repeat(word, 6000)), Courier()));
    }

    [Fact]
    public void ADocumentThatLeavesTheSettingOff_savesWhatAFreshOneDoes_afterAnOnDocumentWasRefused()
    {
        using (var on = NewDocument(true, 80, 37, 10))
        {
            on.Add(ManyAddresses());
            Assert.Throws<InvalidOperationException>(() => Save(on));
        }

        using var off = NewDocument(false, 80, 37, 10);
        off.Add(ManyAddresses());
        var afterwards = Save(off);

        using var fresh = NewDocument(false, 80, 37, 10);
        fresh.Add(ManyAddresses());
        Assert.Equal(Save(fresh), afterwards);
    }
}
