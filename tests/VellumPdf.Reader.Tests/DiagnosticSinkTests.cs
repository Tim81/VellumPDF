// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="DiagnosticSink"/> is #385's internal plumbing behind
/// <see cref="PdfDocumentReader.Diagnostics"/>: recording order, the (code, object, page) dedupe
/// key, the <see cref="PdfReaderDiagnosticCode.DiagnosticsSuppressed"/> cap, and
/// <see cref="DiagnosticSink.CreateScope"/> forwarding. Exercised directly rather than only through
/// a full <see cref="PdfDocumentReader"/> read, since this PR has no per-page or per-operation
/// caller of <see cref="DiagnosticSink.CreateScope"/> yet — the plumbing has to be pinned on its
/// own so a later PR that starts calling it can trust it.
/// </summary>
public sealed class DiagnosticSinkTests
{
    [Fact]
    public void Report_recordsInCallOrder()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.XrefReconstructed, "first");
        sink.Report(PdfReaderDiagnosticCode.FilterNull, "second", objectNumber: 1);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "third", objectNumber: 2);

        Assert.Equal(3, sink.Diagnostics.Count);
        Assert.Equal("first", sink.Diagnostics[0].Message);
        Assert.Equal("second", sink.Diagnostics[1].Message);
        Assert.Equal("third", sink.Diagnostics[2].Message);
    }

    [Fact]
    public void Report_populatesEveryField()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.ObjectGenerationMismatch, "mismatch", objectNumber: 7, generation: 2, pageIndex: 3);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal(PdfReaderDiagnosticCode.ObjectGenerationMismatch, d.Code);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
        Assert.Equal("mismatch", d.Message);
        Assert.Equal(7, d.ObjectNumber);
        Assert.Equal(2, d.Generation);
        Assert.Equal(3, d.PageIndex);
    }

    // ── Dedupe: at most once per (code, objectNumber, pageIndex) ────────────────────────────────

    [Fact]
    public void Report_sameCodeObjectAndPage_deduped()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "first", objectNumber: 5, pageIndex: 0);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "second", objectNumber: 5, pageIndex: 0);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal("first", d.Message); // the SECOND report is the one silently dropped.
    }

    [Fact]
    public void Report_sameCodeAndPage_differentObject_notDeduped()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 5, pageIndex: 0);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 6, pageIndex: 0);

        Assert.Equal(2, sink.Diagnostics.Count);
    }

    [Fact]
    public void Report_sameCodeAndObject_differentPage_notDeduped()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 5, pageIndex: 0);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 5, pageIndex: 1);

        Assert.Equal(2, sink.Diagnostics.Count);
    }

    [Fact]
    public void Report_differentCode_sameObjectAndPage_notDeduped()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 5, pageIndex: 0);
        sink.Report(PdfReaderDiagnosticCode.FilterNull, "b", objectNumber: 5, pageIndex: 0);

        Assert.Equal(2, sink.Diagnostics.Count);
    }

    [Fact]
    public void Report_generationDoesNotParticipateInDedupeKey()
    {
        var sink = new DiagnosticSink(cap: 10);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 5, generation: 0, pageIndex: 0);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 5, generation: 1, pageIndex: 0);

        // Same (code, object, page) key regardless of generation — the second report is dropped.
        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal("a", d.Message);
    }

    // ── Cap and the DiagnosticsSuppressed sentinel ───────────────────────────────────────────────

    [Fact]
    public void Report_underCap_neverAddsASentinel()
    {
        var sink = new DiagnosticSink(cap: 3);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 1);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 2);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "c", objectNumber: 3);

        Assert.Equal(3, sink.Diagnostics.Count);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DiagnosticsSuppressed);
    }

    [Fact]
    public void Report_atCap_recordsOneSuppressedSentinel_andDropsTheRest()
    {
        var sink = new DiagnosticSink(cap: 2);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 1);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 2);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "c", objectNumber: 3); // suppressed #1
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "d", objectNumber: 4); // suppressed #2

        // cap ordinary entries + exactly one sentinel, never an ever-growing tail.
        Assert.Equal(3, sink.Diagnostics.Count);
        var sentinel = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DiagnosticsSuppressed);
        // The full count, not a substring match: "2" alone is also satisfied by "the 2-entry cap"
        // regardless of how many were actually dropped.
        Assert.StartsWith("2 diagnostics suppressed", sentinel.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_atCap_suppressedEntry_updatesInPlace_ratherThanAppending()
    {
        var sink = new DiagnosticSink(cap: 1);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 1);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 2);
        Assert.Equal(2, sink.Diagnostics.Count); // 1 ordinary + 1 sentinel

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "c", objectNumber: 3);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "d", objectNumber: 4);

        // Still 1 + 1: the sentinel's own slot was reused, not grown.
        Assert.Equal(2, sink.Diagnostics.Count);
        var sentinel = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DiagnosticsSuppressed);
        Assert.StartsWith("3 diagnostics suppressed", sentinel.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Regression for the bug the cap-then-dedupe reordering in <c>TryAccept</c> fixes: checking
    /// the cap AFTER adding to <c>_seen</c> means a document that reports a huge number of
    /// DISTINCT (code, object, page) triples keeps growing the dedupe set forever, even though
    /// every one of those triples past the first is suppressed rather than recorded — a document
    /// engineered to do exactly that (a condition reported once per object, across a huge number
    /// of objects) retained tens of megabytes in <c>_seen</c> alone while <c>Diagnostics</c>
    /// itself stayed at two entries, before this fix. Asserted on <c>SeenCount</c> rather than
    /// <c>GC.GetTotalMemory</c>, which is not a reliable per-test signal.
    /// </summary>
    [Fact]
    public void Report_manyDistinctReports_pastCap_doesNotGrowTheDedupeSetUnbounded()
    {
        var sink = new DiagnosticSink(cap: 1);

        for (var i = 0; i < 100_000; i++)
            sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "m", objectNumber: i);

        // At most `cap` entries are ever added to _seen — the ones accepted before the cap was
        // first reached. Nothing past that point is ever inserted, however many distinct triples
        // arrive afterward.
        Assert.True(
            sink.SeenCount <= 1,
            $"the dedupe set held {sink.SeenCount} entries after 100,000 distinct reports against a cap of 1.");
        Assert.Equal(2, sink.Diagnostics.Count); // 1 ordinary + 1 sentinel, regardless of object count.
    }

    [Fact]
    public void Report_dedupedReport_doesNotCountAgainstTheCap()
    {
        var sink = new DiagnosticSink(cap: 1);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 1);
        // Same (code, object, page) key as above — deduped before the cap is ever consulted, so
        // this must not trigger suppression.
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a-again", objectNumber: 1);

        var d = Assert.Single(sink.Diagnostics);
        Assert.Equal("a", d.Message);
    }

    /// <summary>
    /// The suppressed count is reports dropped, not distinct conditions dropped: a key first
    /// encountered PAST the cap is never remembered (see <c>TryAccept</c>), so every one of its
    /// later recurrences is counted again — unlike the same-key-below-the-cap case above, which
    /// stays silent forever once recorded once.
    /// </summary>
    [Fact]
    public void Report_sameKey_repeatedPastTheCap_countsEachOccurrenceAsDropped()
    {
        var sink = new DiagnosticSink(cap: 1);

        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 1);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 2);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b-again", objectNumber: 2);

        var sentinel = Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DiagnosticsSuppressed);
        Assert.StartsWith("2 diagnostics suppressed", sentinel.Message, StringComparison.Ordinal);
    }

    // ── CreateScope ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateScope_report_appearsInBothChildAndParent_asTheSameInstance()
    {
        var parent = new DiagnosticSink(cap: 10);
        var child = parent.CreateScope();

        child.Report(PdfReaderDiagnosticCode.UnknownFilter, "from child", objectNumber: 1);

        var childEntry = Assert.Single(child.Diagnostics);
        var parentEntry = Assert.Single(parent.Diagnostics);
        Assert.Same(childEntry, parentEntry);
    }

    [Fact]
    public void CreateScope_parentDirectReport_doesNotAppearInChild()
    {
        var parent = new DiagnosticSink(cap: 10);
        var child = parent.CreateScope();

        parent.Report(PdfReaderDiagnosticCode.UnknownFilter, "from parent", objectNumber: 1);

        Assert.Empty(child.Diagnostics);
        Assert.Single(parent.Diagnostics);
    }

    [Fact]
    public void CreateScope_dedupeIsIndependentPerSink()
    {
        var parent = new DiagnosticSink(cap: 10);
        var child = parent.CreateScope();

        // Same (code, object, page) key reported first directly on the parent, then through the
        // child: the child's OWN dedupe set has not seen this key, so it still records locally —
        // but the forward to the parent is deduped there, so the parent still ends with one entry.
        parent.Report(PdfReaderDiagnosticCode.UnknownFilter, "direct", objectNumber: 1);
        child.Report(PdfReaderDiagnosticCode.UnknownFilter, "via child", objectNumber: 1);

        var childEntry = Assert.Single(child.Diagnostics);
        Assert.Equal("via child", childEntry.Message);

        var parentEntry = Assert.Single(parent.Diagnostics);
        Assert.Equal("direct", parentEntry.Message);
    }

    /// <summary>
    /// A <see cref="DiagnosticSink.CreateScope"/> child shares its parent's cap (10 here), so this
    /// deliberately stays well under it — five reports, none suppressed. It exercises forwarding
    /// at a size larger than the single-report tests above, not suppression itself: the child
    /// cannot reach its own cap before the parent does (their caps are equal and every accepted
    /// child report also reaches the parent), so a genuine "child suppresses, parent does not"
    /// case does not exist under today's CreateScope, where cap is always inherited unchanged.
    /// </summary>
    [Fact]
    public void CreateScope_reportsUnderCap_reachBothLists_withNoSentinel()
    {
        var parent = new DiagnosticSink(cap: 10);
        var child = parent.CreateScope();

        for (var i = 0; i < 5; i++)
            child.Report(PdfReaderDiagnosticCode.UnknownFilter, $"m{i}", objectNumber: i);

        Assert.Equal(5, child.Diagnostics.Count);
        Assert.Equal(5, parent.Diagnostics.Count);
        Assert.DoesNotContain(parent.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.DiagnosticsSuppressed);
    }

    // ── Live-view enumeration ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="DiagnosticSink.Diagnostics"/> wraps the sink's own live <c>List&lt;T&gt;</c>
    /// (see <see cref="DiagnosticSink"/>'s own remarks), so — unlike a defensively copied snapshot
    /// — mutating the sink while enumerating it invalidates the enumerator. Pinned here because
    /// docs/reader-guide.md promises the caller this behaviour by name.
    /// </summary>
    [Fact]
    public void Diagnostics_reportedToWhileEnumerating_throwsInvalidOperationException()
    {
        var sink = new DiagnosticSink(cap: 10);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "a", objectNumber: 1);
        sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "b", objectNumber: 2);

        Assert.Throws<InvalidOperationException>(() =>
        {
            foreach (var _ in sink.Diagnostics)
                sink.Report(PdfReaderDiagnosticCode.UnknownFilter, "c", objectNumber: 3);
        });
    }

    // ── Construction ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_capBelowOne_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticSink(cap: 0));
    }
}
