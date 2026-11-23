// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// What every list of one document layout shares: the document, and whether the document keeps
/// every list as its twin.
/// </summary>
/// <remarks>
/// A 2.x compatibility device, removed in 3.0 (#688), which refuses such a font where the style is
/// built and lays a list out one way.
/// <para>Clamping or moving an entry changes where everything after it falls, and two things on
/// 2.3.3 depend on where that is. A list entry whose font the document cannot draw saved when its
/// text fell where it does not fit and was dropped there, and threw when the text was drawn. An
/// element that needs about as many continuations as the limit allows (#459) saved or threw
/// according to where it started. One document could flip from one outcome to the other without
/// any list having touched the element itself. The document keeps its 2.3.3 outcome only if no
/// list moves anything, so a document that holds either lays every list out as its twin. The run
/// is how a list that the document did not address, one a renderer of a caller's holds, reaches
/// that decision: <see cref="Current"/> is the run of the layout in progress, whatever context the
/// list was handed and whatever thread it is laid out on.</para>
/// <para>A layout that finds such an entry after another list has been laid out the new way must
/// lay out again from the start; <see cref="DocumentRenderer"/> does that for the measuring
/// passes, and a pass that draws cannot be redone.</para>
/// <para>The run flows with the execution context, so a renderer that lays its list out on a task
/// or a thread pool thread is seen. Work queued with the context suppressed
/// (<c>ThreadPool.UnsafeQueueUserWorkItem</c>, <c>ExecutionContext.SuppressFlow</c>) is not.</para>
/// </remarks>
internal sealed class ListLayoutRun
{
    // Not ThreadStatic: a renderer of a caller's that lays its list out through Task.Run, Parallel
    // or a new thread would otherwise lay it out under no run, and the document would not learn
    // what that list holds. A layout started inside another one restores the outer run in End.
    private static readonly AsyncLocal<ListLayoutRun?> s_current = new();

    // The embedded fonts the document was found able to draw. A list asks about the same few fonts
    // for every run of every entry, so a font is asked about once, however the runs alternate. Only
    // a yes is kept: a font cannot be taken away from a document, and a renderer of a caller's may
    // embed one while the layout runs, so a no is asked again. Read from every thread a renderer
    // lays its list out on.
    private System.Collections.Concurrent.ConcurrentDictionary<EmbeddedFontHandle, bool>? _drawable;

    // The Standard 14 value whose answer was asked for last, which costs one comparison. The value
    // and its answer are packed in one long, written and read whole, because the run is read from
    // every thread a renderer lays its list out on and three separate fields can pair one value
    // with another's answer. Bit 0 says an answer is held, bit 1 is the answer, the rest the
    // value. Zero is no answer yet.
    private long _lastStandard;

    // Read from every thread a renderer lays its list out on, and only ever set to true. The two
    // causes are kept apart because the report of a list names the one that applied.
    private volatile bool _allAsTwin;
    private volatile bool _undrawable;
    private volatile bool _nearTheLimit;

    // The lists that laid out as a twin of their own during the probing pass, which let go of it
    // when the pass ends.
    private List<ListRenderer>? _probed;

    private ListLayoutRun(PdfDocument pdf) => Pdf = pdf;

    /// <summary>The run of the layout in progress, or null outside one.</summary>
    internal static ListLayoutRun? Current => s_current.Value;

    /// <summary>The document the layout is for.</summary>
    internal PdfDocument Pdf { get; }

    /// <summary>
    /// True once any list of the document was found to hold an entry the document cannot draw, or
    /// an element after a list was found that can need about as many continuations as the limit
    /// allows. It never goes back: every list of the document is then laid out as its twin.
    /// </summary>
    internal bool AllAsTwin => _allAsTwin;

    /// <summary>Whether a list was found to hold an entry the document cannot draw.</summary>
    internal bool FoundUndrawable => _undrawable;

    /// <summary>
    /// Whether an element after a list was found that can need about as many continuations as the
    /// limit allows.
    /// </summary>
    internal bool FoundNearTheLimit => _nearTheLimit;

    /// <summary>
    /// True while the document lays out only to find <see cref="AllAsTwin"/>. Every list lays out
    /// as its twin then, on a copy of its own that <see cref="EndProbe"/> lets go of, whether or
    /// not the document turns out to hold such an entry.
    /// </summary>
    internal bool Probing { get; set; }

    /// <summary>
    /// Starts the run of a layout. <paramref name="previous"/> is what <see cref="End"/> restores,
    /// so a layout started inside another one, from a renderer of a caller's, leaves the outer run
    /// as it was.
    /// </summary>
    internal static ListLayoutRun Begin(PdfDocument pdf, out ListLayoutRun? previous)
    {
        previous = s_current.Value;
        var run = new ListLayoutRun(pdf);
        s_current.Value = run;
        return run;
    }

    internal static void End(ListLayoutRun? previous) => s_current.Value = previous;

    /// <summary>Records that a list holds an entry the document cannot draw.</summary>
    internal void NoteUndrawable()
    {
        _undrawable = true;
        _allAsTwin = true;
    }

    /// <summary>
    /// Records that an element after a list can need about as many continuations as the limit
    /// allows, so where it starts decides whether the document saves.
    /// </summary>
    internal void NoteNearTheLimit()
    {
        _nearTheLimit = true;
        _allAsTwin = true;
    }

    /// <summary>
    /// Remembers that <paramref name="list"/> laid out as a twin of its own during the probing
    /// pass. The pass leaves the list itself as it was, which is what a list that a caller laid
    /// out before the save needs: 2.3.3 keeps the entries that call built.
    /// </summary>
    internal void NoteProbed(ListRenderer list) => (_probed ??= []).Add(list);

    /// <summary>
    /// Lets go of what the probing pass built, so a document with a long list does not hold a
    /// second copy of its entries through the pass that draws.
    /// </summary>
    internal void EndProbe()
    {
        if (_probed is null) return;
        foreach (var list in _probed) list.ReleaseProbe();
        _probed = null;
    }

    /// <summary>
    /// Whether text in <paramref name="style"/> can be drawn in the document, which is the question
    /// before text that was discarded is drawn (#476) or an entry that was dropped is moved and
    /// drawn (#685). The text is drawn through TextEmitter.SelectFont, and two things there refuse
    /// a style that layout accepted. An embedded handle is registered for the page, and the save
    /// refuses one the document defines no font for (PdfDocument.RefuseUndefinedForeignFonts). A
    /// Standard 14 value the enumeration does not name makes the save throw
    /// IndexOutOfRangeException when the font dictionary reads its name
    /// (PdfFontResource.BaseFontName). A non-finite size or colour is the other refusal the draw
    /// adds, and the clamp checks it separately.
    /// </summary>
    /// <remarks>
    /// A layout that has no run cannot say whether a handle is defined in the document, so an
    /// embedded font is then not drawable.
    /// </remarks>
    internal static bool CanDraw(ListLayoutRun? run, TextStyle style)
    {
        var font = style.FontRef;
        if (font.IsEmbedded) return run is not null && run.CanDrawEmbedded(font.Embedded);
        return run is null ? Enum.IsDefined(font.Standard14) : run.IsStandardDefined(font.Standard14);
    }

    // How many times the document was asked whether an embedded font can be drawn, for a test to
    // pin that a font it answered yes for is not asked about again.
    private int _documentQuestions;

    internal int DocumentQuestions => Volatile.Read(ref _documentQuestions);

    private bool CanDrawEmbedded(EmbeddedFontHandle handle)
    {
        if (_drawable is { } known && known.ContainsKey(handle)) return true;
        Interlocked.Increment(ref _documentQuestions);
        if (!Pdf.CanDrawFont(handle)) return false;

        var created = _drawable ?? Interlocked.CompareExchange(ref _drawable, new(), null) ?? _drawable;
        created[handle] = true;
        return true;
    }

    private bool IsStandardDefined(Standard14 font)
    {
        var last = Volatile.Read(ref _lastStandard);
        if (last != 0 && (last >> 2) == (long)font) return (last & 2) != 0;
        var defined = Enum.IsDefined(font);
        Volatile.Write(ref _lastStandard, ((long)font << 2) | (defined ? 2L : 0L) | 1L);
        return defined;
    }
}
