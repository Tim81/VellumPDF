// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The list indent clamp (#476) draws text a list used to discard, so a list can need more page
/// continuations than before. A list whose clamp could take it past the limit is laid out without
/// drawing first, and is placed unclamped when that run reaches the limit, so a document that saved
/// still saves and a document that was refused is still refused.
/// </summary>
public sealed class ListIndentContinuationTests
{
    private static TextStyle Helv10() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    // A page 40pt tall with 10pt margins leaves an area 20pt tall: one 12pt line, never two.
    private static readonly PdfRectangle OneLinePage = new(0, 0, 200, 40);

    // 50pt leaves 30pt: two lines, never three.
    private static readonly PdfRectangle TwoLinePage = new(0, 0, 200, 50);

    private static readonly DateTimeOffset Stamp = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private static PdfDocument NewPdf() => new() { Timestamp = Stamp, DocumentId = new byte[16] };

    // The text "W" repeated, one word per line at the width the clamp gives it.
    private static string Words(int count) => string.Join(" ", Enumerable.Repeat("W", count));

    private static ListElement FlatList(int entries, int words)
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        for (var i = 0; i < entries; i++) list.Add(new ListItem(Words(words), Helv10()));
        return list;
    }

    private sealed record Run(
        string Content,
        int Pages,
        IReadOnlyList<LayoutDiagnostic> Diagnostics,
        int Probes,
        Exception? Error);

    private static Run Place(
        IRenderer renderer, PdfRectangle page, int limit = 50_000)
    {
        var pdf = NewPdf();
        var documentRenderer = new DocumentRenderer(pdf, page, new EdgeInsets(10)) { ContinuationLimit = limit };
        documentRenderer.Add(renderer);
        using var ms = new MemoryStream();
        Exception? error = null;
        try
        {
            documentRenderer.Render(ms);
        }
        catch (InvalidOperationException ex)
        {
            error = ex;
        }

        var content = error is null ? PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()) : "";
        return new Run(content, pdf.Pages.Count, documentRenderer.Diagnostics, documentRenderer.ContinuationProbes, error);
    }

    private static int Shows(Run run, string text) =>
        Regex.Matches(run.Content, @"\(" + Regex.Escape(text) + @"\)\s*Tj").Count;

    private static bool Skipped(Run run) =>
        run.Diagnostics.Any(d => d.Code == LayoutDiagnosticCode.ListIndentClampSkipped);

    // ── The decision, at a limit small enough to reach in a blink ───────────────

    /// <summary>
    /// One entry of N words, a page that holds one line: the clamped list takes N - 1
    /// continuations. At the limit it keeps the clamp, one more word and it is placed unclamped.
    /// </summary>
    [Theory]
    [InlineData(100, false)]
    [InlineData(101, false)]
    [InlineData(102, true)]
    [InlineData(500, true)]
    public void AListThatNeedsMoreContinuationsThanTheLimit_isPlacedUnclamped(int words, bool unclamped)
    {
        var run = Place(new ListRenderer(FlatList(1, words)), OneLinePage, limit: 100);

        Assert.Null(run.Error);
        Assert.Equal(unclamped, Skipped(run));
        Assert.Equal(unclamped ? 0 : words, Shows(run, "W"));
        Assert.Equal(1, Shows(run, "1."));
        Assert.Equal(unclamped ? 1 : words, run.Pages);
    }

    /// <summary>
    /// The bound is the number of wrapped lines. At the limit it cannot be exceeded, so no probe
    /// runs; one line above it a probe runs and, when the page holds two lines, finds room.
    /// </summary>
    [Theory]
    [InlineData(100, 0)]
    [InlineData(101, 1)]
    [InlineData(200, 1)]
    public void TheBound_keepsTheProbeForListsThatCouldReachTheLimit(int words, int probes)
    {
        var run = Place(new ListRenderer(FlatList(1, words)), TwoLinePage, limit: 100);

        Assert.Null(run.Error);
        Assert.Equal(probes, run.Probes);
        Assert.False(Skipped(run));
        Assert.Equal(words, Shows(run, "W"));
    }

    [Fact]
    public void AListWithNoClampedEntry_isNeverProbed()
    {
        var list = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < 90; i++) list.Add(new ListItem(Words(3), Helv10()));

        var run = Place(new ListRenderer(list), OneLinePage, limit: 100);

        Assert.Null(run.Error);
        Assert.Equal(0, run.Probes);
        Assert.Empty(run.Diagnostics);
    }

    /// <summary>
    /// The unclamped placement is the placement a list gets with no clamp in the library: a list
    /// that main refuses at the limit is refused here with the same message.
    /// </summary>
    [Fact]
    public void AListTheUnclampedPlacementRefuses_isRefusedWithItsMessage()
    {
        // 150 entries, one marker line each on a one-line page: 149 continuations unclamped.
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        for (var i = 0; i < 150; i++) list.Add(new ListItem("W", Helv10()));
        var unclamped = Place(new ListRenderer(list).Unclamped(), OneLinePage, limit: 100);

        var clamped = Place(new ListRenderer(list), OneLinePage, limit: 100);

        Assert.NotNull(unclamped.Error);
        Assert.NotNull(clamped.Error);
        Assert.Equal(unclamped.Error.Message, clamped.Error.Message);
        Assert.StartsWith("A single element needed more than 50000 page continuations", clamped.Error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Seeded lists at a small limit, each placed through the decision and as the unclamped twin:
    /// the twin saves, so the decision saves; the twin is refused, so the decision is refused with
    /// the same message; and where the decision falls back, its output is the twin's.
    /// </summary>
    [Fact]
    public void Decision_agreesWithTheUnclampedPlacement_forSeededLists()
    {
        const int Seed = 476_207;
        const int Lists = 3_000;
        var rng = new Random(Seed);
        int fellBack = 0, keptClamp = 0, bothRefused = 0, probed = 0;
        for (var n = 0; n < Lists; n++)
        {
            var limit = rng.Next(4, 60);
            var page = rng.Next(3) switch { 0 => OneLinePage, 1 => TwoLinePage, _ => new PdfRectangle(0, 0, 200, 90) };
            var style = (ListStyle)rng.Next(4);
            var list = new ListElement(style) { Indent = rng.Next(3) == 0 ? rng.Next(0, 400) : 1000 };
            for (var i = rng.Next(1, 6); i > 0; i--)
            {
                var item = new ListItem(Words(rng.Next(1, 45)), Helv10());
                if (rng.Next(3) == 0)
                    for (var c = rng.Next(1, 3); c > 0; c--) item.AddChild(Words(rng.Next(1, 25)));
                list.Add(item);
            }

            var main = Place(new ListRenderer(list).Unclamped(), page, limit);
            var run = Place(new ListRenderer(list), page, limit);

            if (main.Error is not null)
            {
                bothRefused++;
                Assert.True(run.Error is not null, $"list {n}: refused unclamped, saved clamped");
                Assert.Equal(main.Error.Message, run.Error.Message);
                continue;
            }

            Assert.True(run.Error is null, $"list {n}: saved unclamped, refused clamped: {run.Error?.Message}");
            if (run.Probes > 0) probed++;
            if (Skipped(run))
            {
                fellBack++;
                Assert.Equal(main.Content, run.Content);
                Assert.Equal(main.Pages, run.Pages);
            }
            else if (run.Diagnostics.Any(d => d.Code == LayoutDiagnosticCode.ListIndentClamped))
            {
                keptClamp++;
            }
        }

        // Measured for this seed. Each branch is reached, or the loop above proves nothing.
        Assert.Equal(375, fellBack);
        Assert.Equal(631, keptClamp);
        Assert.Equal(1727, bothRefused);
        Assert.Equal(671, probed);
    }

    // ── Full size, at the limit the library ships with ──────────────────────────

    /// <summary>
    /// A thousand entries of 51 words on a page that holds one line: 51,000 continuations clamped,
    /// 1,000 unclamped. It saved as 1,000 marker lines before the clamp, and still does.
    /// </summary>
    [Fact]
    public void ADocumentThatSaved_stillSaves_whenTheClampWouldPassFiftyThousandContinuations()
    {
        using var doc = new Document { PageSize = OneLinePage, Margins = new EdgeInsets(10), Timestamp = Stamp, DocumentId = new byte[16] };
        doc.Add(FlatList(1000, 51));
        using var ms = new MemoryStream();

        doc.Save(ms);

        var content = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        Assert.Equal(1000, Regex.Matches(content, @"\((?:\d+)\.\)\s*Tj").Count);
        Assert.DoesNotContain("(W)", content, StringComparison.Ordinal);
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.ListIndentClampSkipped, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(1000, item.Count);
        Assert.Equal(1, item.PageNumber);
    }

    // A thousand entries of fifty words, and ahead of them one entry of `first` words when that is
    // not zero: 50,000 + first lines, so 49,999 + first continuations on a page that holds one line.
    private static ListElement AroundTheLimit(int first)
    {
        var list = new ListElement(ListStyle.OrderedDecimal) { Indent = 1000 };
        if (first > 0) list.Add(new ListItem(Words(first), Helv10()));
        for (var i = 0; i < 1000; i++) list.Add(new ListItem(Words(50), Helv10()));
        return list;
    }

    /// <summary>
    /// 50,001 lines on a page that holds one line is 50,000 continuations, the most one element may
    /// take: the bound cannot rule it out, so the list is probed once, and it keeps its clamp.
    /// </summary>
    [Fact]
    public void AListOneLineAboveTheBound_isProbedAndKeepsItsClampAtTheLimitExactly()
    {
        var run = Place(new ListRenderer(AroundTheLimit(1)), OneLinePage);

        Assert.Null(run.Error);
        Assert.Equal(1, run.Probes);
        Assert.Equal(50_001, Shows(run, "W"));
        Assert.Equal(50_001, run.Pages);
        Assert.False(Skipped(run));
    }

    /// <summary>
    /// One line more is 50,001 continuations, which the limit refuses, so the list is placed
    /// unclamped.
    /// </summary>
    [Fact]
    public void AListOneContinuationPastTheLimit_isPlacedUnclamped()
    {
        var run = Place(new ListRenderer(AroundTheLimit(2)), OneLinePage);

        Assert.Null(run.Error);
        Assert.Equal(1, run.Probes);
        Assert.Equal(0, Shows(run, "W"));
        Assert.Equal(1001, run.Pages);
        Assert.True(Skipped(run));
    }

    /// <summary>50,000 lines is at the bound, so no probe runs and the clamp is kept.</summary>
    [Fact]
    public void AListAtTheBound_isNotProbed()
    {
        var run = Place(new ListRenderer(AroundTheLimit(0)), OneLinePage);

        Assert.Null(run.Error);
        Assert.Equal(0, run.Probes);
        Assert.Equal(50_000, Shows(run, "W"));
        Assert.False(Skipped(run));
    }

    /// <summary>
    /// 60,000 lines is above the bound, two to a page is 30,000 pages: the probe runs once, finds
    /// the room, and the text is kept.
    /// </summary>
    [Fact]
    public void AListAboveTheBoundThatFits_isProbedOnceAndKeepsItsClamp()
    {
        var run = Place(new ListRenderer(FlatList(1000, 60)), TwoLinePage);

        Assert.Null(run.Error);
        Assert.Equal(1, run.Probes);
        Assert.Equal(60_000, Shows(run, "W"));
        Assert.Equal(30_000, run.Pages);
        Assert.Equal(1000, Assert.Single(run.Diagnostics).Count);
        Assert.Equal(LayoutDiagnosticCode.ListIndentClamped, Assert.Single(run.Diagnostics).Code);
    }
}
