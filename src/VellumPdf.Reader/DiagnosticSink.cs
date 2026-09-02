// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;

namespace VellumPdf.Reader;

/// <summary>
/// Collects <see cref="PdfReaderDiagnostic"/> instances for one <see cref="PdfDocumentReader"/> (or
/// one scoped operation on it — see <see cref="CreateScope"/>), bounded to <paramref name="cap"/>
/// entries and deduplicated so a condition that recurs across many objects does not turn the
/// document-level list into noise.
/// </summary>
/// <param name="cap">
/// The maximum number of ordinary diagnostics this sink holds before it starts recording
/// <see cref="PdfReaderDiagnosticCode.DiagnosticsSuppressed"/> instead — see
/// <see cref="PdfReaderOptions.MaxDiagnostics"/>. The cap does not count that one sentinel entry
/// itself, so a sink at capacity holds at most <paramref name="cap"/> ordinary diagnostics plus one
/// sentinel, never more — and, per <see cref="TryAccept"/>, bounds the internal dedupe bookkeeping
/// the same way, not just the list a caller sees.
/// </param>
internal sealed class DiagnosticSink(int cap)
{
    // (code, object number, page index) — the key #385 defines dedupe over. Generation is
    // deliberately excluded: two reports about the same object number and page but different
    // generations are still "the same condition" for a reader that already resolves by object
    // number first (see PdfDocumentReader.Resolve's own cache key).
    //
    // Bounded by TryAccept to at most _cap entries: nothing is ever added to this set once
    // _diagnostics.Count reaches _cap (see that method), so a document engineered to report a
    // huge number of DISTINCT triples cannot grow this set without limit just because every one
    // of them past the cap is suppressed rather than recorded.
    private readonly HashSet<(PdfReaderDiagnosticCode Code, int? ObjectNumber, int? PageIndex)> _seen = [];
    private readonly List<PdfReaderDiagnostic> _diagnostics = [];

    // Built lazily (a field initializer cannot reference another instance field, and this class
    // uses a primary constructor with no body to compute it in) and cached from then on. A
    // ReadOnlyCollection wraps _diagnostics by reference rather than copying it, so it still
    // reflects every later Report/Forward call — the live-view contract Diagnostics documents —
    // but, unlike handing out _diagnostics itself as an IReadOnlyList<T>, it cannot be downcast
    // back to List<T> to mutate it. RecordSuppression's _suppressionIndex depends on nobody but
    // this sink ever inserting, removing, or reordering entries.
    private ReadOnlyCollection<PdfReaderDiagnostic>? _diagnosticsView;
    private readonly DiagnosticSink? _parent;
    private readonly int _cap = cap >= 1
        ? cap
        : throw new ArgumentOutOfRangeException(nameof(cap), cap, "A diagnostic sink's cap must be at least 1.");

    // Index into _diagnostics of the DiagnosticsSuppressed sentinel, once one exists — tracked
    // separately from _seen (RecordSuppression never goes through the dedupe check Report/Forward
    // apply to ordinary diagnostics) so the sentinel's message can be updated in place instead of
    // appending a new entry per suppressed report, which would defeat the point of capping at all.
    private int? _suppressionIndex;
    private int _suppressedCount;

    private DiagnosticSink(DiagnosticSink parent) : this(parent._cap)
    {
        _parent = parent;
    }

    /// <summary>Every diagnostic this sink has recorded, in the order <see cref="Report"/> or
    /// forwarding from a <see cref="CreateScope"/> child added them.</summary>
    internal IReadOnlyList<PdfReaderDiagnostic> Diagnostics => _diagnosticsView ??= _diagnostics.AsReadOnly();

    /// <summary>
    /// The dedupe set's own size — test-only visibility for pinning that <see cref="TryAccept"/>
    /// actually stops growing it once <see cref="Diagnostics"/> reaches its cap, rather than the
    /// bounded-memory claim resting on <see cref="Diagnostics"/>.Count staying small alone.
    /// </summary>
    internal int SeenCount => _seen.Count;

    /// <summary>
    /// Creates a child sink sharing this sink's cap: a report against the child is recorded in the
    /// child's own list AND forwarded to this sink, as the SAME <see cref="PdfReaderDiagnostic"/>
    /// instance — not a re-report that would construct a second, reference-distinct copy — so a
    /// per-operation result built from the child later carries diagnostics that are also, by
    /// reference, exactly what <see cref="PdfDocumentReader.Diagnostics"/> exposes.
    /// </summary>
    /// <remarks>
    /// Unused in this PR (#385 lands only the document-level sink; a per-operation result — the
    /// first candidate is #98's text extraction — is what will actually call this). Present now,
    /// and exercised directly by <c>DiagnosticSinkTests</c>, so the forwarding contract is pinned
    /// before anything depends on it rather than designed against its first real caller.
    /// </remarks>
    internal DiagnosticSink CreateScope() => new(this);

    /// <summary>
    /// Records one observation, unless (code, <paramref name="objectNumber"/>,
    /// <paramref name="pageIndex"/>) was already reported on this sink, or the cap has been reached
    /// (in which case a <see cref="PdfReaderDiagnosticCode.DiagnosticsSuppressed"/> sentinel is
    /// recorded or updated instead). Also forwarded to the parent sink, when this sink is a
    /// <see cref="CreateScope"/> child, regardless of whether THIS sink's own cap accepted or
    /// suppressed it — the two sinks' caps are tracked independently.
    /// </summary>
    internal void Report(
        PdfReaderDiagnosticCode code, string message,
        int? objectNumber = null, int? generation = null, int? pageIndex = null)
    {
        if (!TryAccept((code, objectNumber, pageIndex)))
            return;

        var diagnostic = new PdfReaderDiagnostic(code, message, objectNumber, generation, pageIndex);
        _diagnostics.Add(diagnostic);
        _parent?.Forward(diagnostic);
    }

    /// <summary>
    /// Records <paramref name="diagnostic"/> — an instance a child sink already built via
    /// <see cref="Report"/> — into this sink under this sink's own dedupe key and cap, preserving
    /// its identity rather than constructing a new instance.
    /// </summary>
    private void Forward(PdfReaderDiagnostic diagnostic)
    {
        if (!TryAccept((diagnostic.Code, diagnostic.ObjectNumber, diagnostic.PageIndex)))
            return;

        _diagnostics.Add(diagnostic);
        _parent?.Forward(diagnostic);
    }

    /// <summary>
    /// Decides whether <paramref name="key"/> should be recorded as a new diagnostic — shared by
    /// <see cref="Report"/> and <see cref="Forward"/> so the two can never diverge on where the
    /// cap is actually checked relative to <c>_seen</c>.
    /// </summary>
    /// <remarks>
    /// The cap is checked FIRST, before <c>_seen</c> is touched at all. Checking it after (the
    /// original shape: add to <c>_seen</c> unconditionally, then decide whether the diagnostic
    /// itself fits under the cap) still bounds the visible <see cref="Diagnostics"/> list to
    /// <c>cap + 1</c> entries, but <c>_seen</c> itself grows by one for every DISTINCT
    /// (code, object, page) triple ever reported, capped or not — a document engineered to trigger
    /// a huge number of distinct conditions after the cap is already full would retain one
    /// <c>HashSet</c> entry per condition forever, defeating the memory bound
    /// <see cref="PdfReaderOptions.MaxDiagnostics"/> promises (measured: a million distinct object
    /// numbers against <c>cap: 1</c> retained tens of megabytes in <c>_seen</c> alone, while
    /// <see cref="Diagnostics"/> itself stayed at two entries).
    /// <para>
    /// Once at capacity, a key already in <c>_seen</c> from before the cap was reached is still a
    /// genuine duplicate and must stay silent — deduping a repeat never counts as that repeat
    /// being dropped, capped or not — so this still consults <c>_seen</c> there, just with
    /// <c>Contains</c> rather than <c>Add</c>: nothing new is ever inserted into it past the cap.
    /// </para>
    /// </remarks>
    private bool TryAccept((PdfReaderDiagnosticCode Code, int? ObjectNumber, int? PageIndex) key)
    {
        if (_diagnostics.Count < _cap)
            return _seen.Add(key);

        if (_seen.Contains(key))
            return false;

        RecordSuppression();
        return false;
    }

    private void RecordSuppression()
    {
        _suppressedCount++;
        var message = _suppressedCount == 1
            ? $"1 diagnostic suppressed after reaching the {_cap}-entry cap (PdfReaderOptions.MaxDiagnostics)."
            : $"{_suppressedCount} diagnostics suppressed after reaching the {_cap}-entry cap (PdfReaderOptions.MaxDiagnostics).";
        var sentinel = new PdfReaderDiagnostic(PdfReaderDiagnosticCode.DiagnosticsSuppressed, message, null, null, null);

        if (_suppressionIndex is int index)
            _diagnostics[index] = sentinel;
        else
        {
            _suppressionIndex = _diagnostics.Count;
            _diagnostics.Add(sentinel);
        }
    }
}
