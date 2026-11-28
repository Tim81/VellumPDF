// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Collects what a layout noticed. A report is keyed on its code and subject, and on the element
/// too when the caller asks for that; a repeat bumps the item's count, and the 1,001st distinct key
/// is not stored.
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

    private ListWalkState? _listWalk;

    /// <summary>
    /// The items the list walk has visited in this save. A list takes part only when it reports, so
    /// the order of the walk is the order in which lists are drawn.
    /// </summary>
    internal ListWalkState ListWalk => _listWalk ??= new();

    // The canvas whose encoding warnings are already accounted for, and how many of them.
    private PdfCanvas? _syncedCanvas;
    private int _syncedCount;

    // The glyph-0 entries of that canvas already accounted for: the count read for each entry by
    // index, the dropped count, and the tally's total of occurrences, which is what tells a page
    // that wrote none from one that did.
    private readonly List<int> _notdefCounts = [];
    private int _notdefDropped;
    private int _notdefTotal;
    private Dictionary<string, int>? _fontOrdinals;

    /// <summary>
    /// The claim of the document being drawn. Assigned where the document's draw context is made,
    /// so a context a caller constructs has none and reports no conformance item.
    /// </summary>
    internal PdfConformance Conformance { get; set; }

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
        bool perElement = false,
        string? clause = null) =>
        ReportCore(code, subject, message, null, count, perElement, clause);

    /// <summary>
    /// <paramref name="message"/> runs only when the subject is new, so a repeat allocates nothing
    /// when the caller passes a static lambda.
    /// </summary>
    internal void Report(
        LayoutDiagnosticCode code,
        long subject,
        Func<long, string> message,
        int count = 1,
        bool perElement = false,
        string? clause = null) =>
        ReportCore(code, subject, null, message, count, perElement, clause);

    private void ReportCore(
        LayoutDiagnosticCode code,
        long subject,
        string? text,
        Func<long, string>? textFor,
        int count,
        bool perElement,
        string? clause)
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
            code, text ?? textFor!(subject), clause, CurrentKind, CurrentIndex, CurrentPage, count);
        (_byKey ??= []).Add(key, item);
        (_items ??= []).Add(item);
    }

    /// <summary>
    /// Accounts for what the canvas has written since the last call. The characters it substituted
    /// with '?' are skipped when <paramref name="report"/> is false: text the library wrote itself,
    /// such as a list marker, is not the caller's and is not listed. A glyph 0 is reported whatever
    /// <paramref name="report"/> says, because the file fails the same clause however the character
    /// got there and the caller chose the font.
    /// </summary>
    internal void SyncEncodingWarnings(PdfCanvas canvas, bool report)
    {
        if (!ReferenceEquals(_syncedCanvas, canvas))
        {
            _syncedCanvas = canvas;
            _syncedCount = 0;
            _notdefCounts.Clear();
            _notdefDropped = 0;
            _notdefTotal = 0;
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
                    $"U+{codePoint:X4} is not in WinAnsiEncoding and is written as '?' for each UTF-16 code unit."));
        }

        SyncNotdefGlyphs(canvas);
    }

    // One comparison for a page that wrote no glyph 0, or none since the last read.
    private void SyncNotdefGlyphs(PdfCanvas canvas)
    {
        if (canvas.NotdefTally is not { } tally || tally.Total == _notdefTotal) return;
        _notdefTotal = tally.Total;

        var entries = tally.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var seen = i < _notdefCounts.Count ? _notdefCounts[i] : 0;
            var increase = entry.Count - seen;
            if (i < _notdefCounts.Count) _notdefCounts[i] = entry.Count;
            else _notdefCounts.Add(entry.Count);
            if (increase <= 0) continue;

            var codePoint = NotdefGlyphTally.CodePointOf(entry.Character);
            var subject = ((long)FontOrdinal(entry.FontResourceName) << 32) | (uint)codePoint;
            Report(
                LayoutDiagnosticCode.NotdefGlyph,
                subject,
                _ => NotdefMessage(entry),
                increase,
                perElement: false,
                clause: ClauseFor(LayoutDiagnosticCode.NotdefGlyph, Conformance));
        }

        var dropped = tally.Dropped - _notdefDropped;
        if (dropped > 0)
        {
            _notdefDropped = tally.Dropped;
            AddDropped(dropped);
        }
    }

    private int FontOrdinal(string? resource)
    {
        _fontOrdinals ??= new(StringComparer.Ordinal);
        var key = resource ?? "";
        if (!_fontOrdinals.TryGetValue(key, out var ordinal))
            _fontOrdinals[key] = ordinal = _fontOrdinals.Count;
        return ordinal;
    }

    private static string NotdefMessage(NotdefGlyphWarning entry) =>
        entry.Character is { } rune
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"U+{rune.Value:X4} has no glyph in {entry.FontName} ({entry.FontResourceName}) and is drawn as glyph 0 (.notdef).")
            : string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Glyph 0 (.notdef) was drawn in font {entry.FontResourceName ?? "(none selected)"} from glyph ids passed to PdfCanvas.ShowGlyphs, so the character is not known.");

    /// <summary>
    /// Counts error occurrences the canvas could not store, so
    /// <see cref="LayoutDiagnosticCode.DiagnosticsDropped"/> appears and strict mode refuses the
    /// save.
    /// </summary>
    internal void AddDropped(int count)
    {
        ErrorReports = Saturate(ErrorReports, count);
        _dropped = Saturate(_dropped, count);
    }

    /// <summary>
    /// The clause a report breaks under the claim, in the format the conformance package cites, or
    /// null when no standard forbids it: plain PDF permits showing glyph 0, and a document with no
    /// claim has no embedding requirement.
    /// </summary>
    internal static string? ClauseFor(LayoutDiagnosticCode code, PdfConformance conformance) =>
        (code, conformance) switch
        {
            (LayoutDiagnosticCode.NotdefGlyph, PdfConformance.None) => null,
            (LayoutDiagnosticCode.NotdefGlyph, PdfConformance.PdfUA1) => "ISO 14289-1:2014, 7.21.8",
            (LayoutDiagnosticCode.NotdefGlyph, _) => "ISO 19005-2:2011, 6.2.11.8",
            (LayoutDiagnosticCode.UnembeddedFont, PdfConformance.None) => null,
            (LayoutDiagnosticCode.UnembeddedFont, PdfConformance.PdfUA1) => "ISO 14289-1:2014, 7.21.4.1",
            (LayoutDiagnosticCode.UnembeddedFont, _) => "ISO 19005-2:2011, 6.2.11.4.1",
            (LayoutDiagnosticCode.ToUnicodeValueSubstituted, PdfConformance.PdfUA1) => "ISO 14289-1:2014, 7.21.7",
            (LayoutDiagnosticCode.ToUnicodeValueSubstituted, PdfConformance.PdfA2u or PdfConformance.PdfA2a) =>
                "ISO 19005-2:2011, 6.2.11.7.2",
            _ => null,
        };

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
