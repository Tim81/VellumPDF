// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Collects what a layout noticed. One item per (code, element, subject); a repeat bumps the item's
/// count and allocates nothing, and the 1,001st distinct item is not stored.
/// </summary>
/// <remarks>
/// The cap bounds memory against a document that produces a distinct subject per character. It is
/// the Reader's default, not a figure any standard gives. The dedupe dictionary holds at most
/// <see cref="MaxItems"/> keys for the same reason.
/// </remarks>
internal sealed class LayoutDiagnosticSink
{
    internal const int MaxItems = 1000;

    private readonly record struct Key(LayoutDiagnosticCode Code, int Kind, int Index, long Subject);

    // Created on the first report, so a document with nothing to report allocates neither.
    private Dictionary<Key, LayoutDiagnostic>? _byKey;
    private List<LayoutDiagnostic>? _items;
    private int _dropped;

    /// <summary>The location the next report is attributed to, set by the renderer that drives the layout.</summary>
    internal LayoutElementKind CurrentKind { get; set; } = LayoutElementKind.Document;

    /// <summary>Zero-based element position, or null for a band or a document-wide item.</summary>
    internal int? CurrentIndex { get; set; }

    /// <summary>One-based page being drawn, or null.</summary>
    internal int? CurrentPage { get; set; }

    /// <summary>The number of keys the dedupe dictionary holds, for the bound test.</summary>
    internal int SeenCount => _byKey?.Count ?? 0;

    /// <summary>The number of stored items that are errors plus the dropped reports that were.</summary>
    internal int ErrorReports { get; private set; }

    internal void Report(
        LayoutDiagnosticCode code,
        long subject,
        string message,
        int count = 1,
        bool perElement = false) =>
        Report(code, subject, _ => message, count, perElement);

    /// <summary>
    /// <paramref name="message"/> runs only when the subject is new, so a repeat allocates nothing
    /// when the caller passes a static lambda.
    /// </summary>
    internal void Report(
        LayoutDiagnosticCode code,
        long subject,
        Func<long, string> message,
        int count = 1,
        bool perElement = false)
    {
        var severity = LayoutDiagnostic.SeverityOf(code);
        var key = perElement
            ? new Key(code, (int)CurrentKind, CurrentIndex ?? -1, subject)
            : new Key(code, 0, 0, subject);

        if (_byKey is not null && _byKey.TryGetValue(key, out var existing))
        {
            existing.Count = Saturate(existing.Count, count);
            if (severity == LayoutDiagnosticSeverity.Error) ErrorReports = Saturate(ErrorReports, count);
            return;
        }

        if (severity == LayoutDiagnosticSeverity.Error) ErrorReports = Saturate(ErrorReports, count);

        if (_byKey is { Count: >= MaxItems })
        {
            _dropped = Saturate(_dropped, 1);
            return;
        }

        var item = new LayoutDiagnostic(code, message(subject), null, CurrentKind, CurrentIndex, CurrentPage, count);
        (_byKey ??= []).Add(key, item);
        (_items ??= []).Add(item);
    }

    /// <summary>True when an error was reported, stored or dropped.</summary>
    internal bool HasErrors => ErrorReports > 0;

    /// <summary>The number of reports dropped past the cap.</summary>
    internal int Dropped => _dropped;

    /// <summary>The stored items in first-occurrence order, then the sentinel when anything was dropped.</summary>
    internal IReadOnlyList<LayoutDiagnostic> Snapshot()
    {
        var result = new List<LayoutDiagnostic>((_items?.Count ?? 0) + 1);
        foreach (var item in _items ?? [])
            result.Add(new LayoutDiagnostic(
                item.Code, item.Message, item.Clause, item.ElementKind, item.ElementIndex, item.PageNumber, item.Count));
        if (_dropped > 0)
            result.Add(new LayoutDiagnostic(
                LayoutDiagnosticCode.DiagnosticsDropped,
                $"The channel holds at most {MaxItems} distinct items; later reports were dropped.",
                null, LayoutElementKind.Document, null, null, _dropped));
        return result;
    }

    private static int Saturate(int current, int add) =>
        add > int.MaxValue - current ? int.MaxValue : current + add;
}
