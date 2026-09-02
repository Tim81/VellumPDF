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
/// sentinel, never more.
/// </param>
internal sealed class DiagnosticSink(int cap)
{
    // (code, object number, page index) — the key #385 defines dedupe over. Generation is
    // deliberately excluded: two reports about the same object number and page but different
    // generations are still "the same condition" for a reader that already resolves by object
    // number first (see PdfDocumentReader.Resolve's own cache key).
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
    /// Creates a child sink sharing this sink's cap: a report against the child is recorded in the
    /// child's own list AND forwarded to this sink, as the SAME <see cref="PdfReaderDiagnostic"/>
    /// instance — not a re-report that would construct a second, reference-distinct copy — so a
    /// per-operation result built from the child later carries diagnostics that are also, by
    /// reference, exactly what <see cref="PdfDocumentReader.Diagnostics"/> exposes.
    /// </summary>
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
        if (!_seen.Add((code, objectNumber, pageIndex)))
            return;

        var diagnostic = new PdfReaderDiagnostic(code, message, objectNumber, generation, pageIndex);
        AddOrSuppress(diagnostic);
        _parent?.Forward(diagnostic);
    }

    /// <summary>
    /// Records <paramref name="diagnostic"/> — an instance a child sink already built via
    /// <see cref="Report"/> — into this sink under this sink's own dedupe key and cap, preserving
    /// its identity rather than constructing a new instance.
    /// </summary>
    private void Forward(PdfReaderDiagnostic diagnostic)
    {
        if (!_seen.Add((diagnostic.Code, diagnostic.ObjectNumber, diagnostic.PageIndex)))
            return;

        AddOrSuppress(diagnostic);
        _parent?.Forward(diagnostic);
    }

    private void AddOrSuppress(PdfReaderDiagnostic diagnostic)
    {
        if (_diagnostics.Count < _cap)
            _diagnostics.Add(diagnostic);
        else
            RecordSuppression();
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
