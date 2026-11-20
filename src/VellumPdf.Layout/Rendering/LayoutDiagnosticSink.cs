// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Collects what a layout noticed. A report is keyed on its code and subject, and on the element
/// too when the caller asks for that; a repeat bumps the item's count, and the 1,001st distinct
/// key is not stored.
/// </summary>
/// <remarks>
/// The cap bounds memory against a document that produces a distinct subject per character. It is
/// the Reader's default, not a figure any standard gives. The dedupe dictionary holds at most
/// <see cref="MaxItems"/> keys for the same reason. A repeat allocates nothing when the caller
/// passes a static lambda for the message.
/// </remarks>
internal sealed class LayoutDiagnosticSink
{
    internal const int MaxItems = 1000;

    private readonly record struct Key(LayoutDiagnosticCode Code, int Kind, int Index, long Subject);

    // Created on the first report, so a document with nothing to report allocates neither.
    private Dictionary<Key, LayoutDiagnostic>? _byKey;
    private List<LayoutDiagnostic>? _items;
    private int _dropped;

    // The canvas whose encoding warnings are already accounted for, and how many of them.
    private PdfCanvas? _syncedCanvas;
    private int _syncedCount;

    /// <summary>The location the next report is attributed to, set by the renderer that drives the
    /// layout.</summary>
    internal LayoutElementKind CurrentKind { get; set; } = LayoutElementKind.Document;

    /// <summary>Zero-based element position, or null for a band or a document-wide item.</summary>
    internal int? CurrentIndex { get; set; }

    /// <summary>One-based page being drawn, or null.</summary>
    internal int? CurrentPage { get; set; }

    /// <summary>The number of keys the dedupe dictionary holds, for the bound test.</summary>
    internal int SeenCount => _byKey?.Count ?? 0;

    /// <summary>The error occurrences reported, stored or dropped.</summary>
    internal int ErrorReports { get; private set; }

    /// <summary>True when an error was reported, stored or dropped.</summary>
    internal bool HasErrors => ErrorReports > 0;

    /// <summary>The number of occurrences dropped past the cap.</summary>
    internal int Dropped => _dropped;

    internal void Report(
        LayoutDiagnosticCode code,
        long subject,
        string message,
        int count = 1,
        bool perElement = false) =>
        ReportCore(code, subject, message, null, count, perElement);

    /// <summary>
    /// <paramref name="message"/> runs only when the subject is new, so a repeat allocates nothing
    /// when the caller passes a static lambda.
    /// </summary>
    internal void Report(
        LayoutDiagnosticCode code,
        long subject,
        Func<long, string> message,
        int count = 1,
        bool perElement = false) =>
        ReportCore(code, subject, null, message, count, perElement);

    private void ReportCore(
        LayoutDiagnosticCode code,
        long subject,
        string? text,
        Func<long, string>? textFor,
        int count,
        bool perElement)
    {
        var isError = LayoutDiagnostic.SeverityOf(code) == LayoutDiagnosticSeverity.Error;
        if (isError) ErrorReports = Saturate(ErrorReports, count);

        var key = perElement
            ? new Key(code, (int)CurrentKind, CurrentIndex ?? -1, subject)
            : new Key(code, 0, 0, subject);

        if (_byKey is not null && _byKey.TryGetValue(key, out var existing))
        {
            existing.Count = Saturate(existing.Count, count);
            return;
        }

        if (_byKey is { Count: >= MaxItems })
        {
            _dropped = Saturate(_dropped, count);
            return;
        }

        var item = new LayoutDiagnostic(
            code, text ?? textFor!(subject), null, CurrentKind, CurrentIndex, CurrentPage, count);
        (_byKey ??= []).Add(key, item);
        (_items ??= []).Add(item);
    }

    /// <summary>
    /// Accounts for the characters the canvas has substituted since the last call. With
    /// <paramref name="report"/> false they are skipped: text the library wrote itself, such as a
    /// list marker, is not the caller's and is not listed.
    /// </summary>
    internal void SyncEncodingWarnings(PdfCanvas canvas, bool report)
    {
        if (!ReferenceEquals(_syncedCanvas, canvas))
        {
            _syncedCanvas = canvas;
            _syncedCount = 0;
        }

        var warnings = canvas.TextEncodingWarnings;
        for (; _syncedCount < warnings.Count; _syncedCount++)
        {
            if (!report) continue;
            Report(
                LayoutDiagnosticCode.UnencodableCharacter,
                warnings[_syncedCount].CodePoint,
                static codePoint => string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"U+{codePoint:X4} is not in WinAnsiEncoding and is written as '?'."));
        }
    }

    /// <summary>The stored items in first-occurrence order, then the sentinel when anything was
    /// dropped.</summary>
    internal List<LayoutDiagnostic> Snapshot()
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
