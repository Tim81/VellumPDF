// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// What every list of one document layout shares: the document, and whether any list of it holds
/// an entry the document cannot draw.
/// </summary>
/// <remarks>
/// A 2.x compatibility device, removed in 3.0 (#688), which refuses such a font where the style is
/// built.
/// <para>On 2.3.3 a list entry whose font the document cannot draw saved when its text fell where
/// it does not fit and was dropped there, and threw when the text was drawn. Clamping or moving an
/// entry changes where everything after it falls, so one document could flip from one outcome to
/// the other without any list having touched the entry itself. The document keeps its 2.3.3
/// outcome only if no list moves anything, so a document that holds such an entry lays every list
/// out as its twin. The run is how a list that the document did not address, one a renderer of a
/// caller's holds, reaches that decision: <see cref="Current"/> is the run of the layout in
/// progress on this thread, whatever context the list was handed.</para>
/// <para>A layout that finds such an entry after another list has been laid out the new way must
/// lay out again from the start; <see cref="DocumentRenderer"/> does that for the measuring
/// passes, and a pass that draws cannot be redone.</para>
/// </remarks>
internal sealed class ListLayoutRun
{
    [ThreadStatic]
    private static ListLayoutRun? s_current;

    // The font whose answer was asked for last. A list asks about the same few fonts for every run
    // of every entry, so the answer is read once per font and not once per run.
    private EmbeddedFontHandle? _lastHandle;
    private bool _lastHandleDrawable;
    private Standard14 _lastStandard;
    private bool _lastStandardDefined;
    private bool _hasLastStandard;

    private ListLayoutRun(PdfDocument pdf) => Pdf = pdf;

    /// <summary>The run of the layout in progress on this thread, or null outside one.</summary>
    internal static ListLayoutRun? Current => s_current;

    /// <summary>The document the layout is for.</summary>
    internal PdfDocument Pdf { get; }

    /// <summary>
    /// True once any list of the document was found to hold an entry the document cannot draw. It
    /// never goes back: every list of the document is then laid out as its twin.
    /// </summary>
    internal bool AllAsTwin { get; private set; }

    /// <summary>
    /// True while the document lays out only to find <see cref="AllAsTwin"/>. Every list lays out
    /// as its twin then, whether or not the document turns out to hold such an entry.
    /// </summary>
    internal bool Probing { get; set; }

    /// <summary>
    /// Counts the times the layout's lists were told to start over, by <see cref="StartOver"/>. A list that was laid out before keeps the paragraphs it laid out,
    /// and a paragraph that is later laid out where its text does not fit is drawn from where it
    /// was placed last. Lists that an earlier pass placed differently would draw text where 2.3.3
    /// dropped it, so a list that sees a new generation forgets what it built, and from then on
    /// keeps what it builds, as a list does across the passes of a document with a header.
    /// </summary>
    internal int Generation { get; private set; }

    /// <summary>
    /// Starts the run of a layout. <paramref name="previous"/> is what <see cref="End"/> restores,
    /// so a layout started inside another one, from a renderer of a caller's, leaves the outer run
    /// as it was.
    /// </summary>
    internal static ListLayoutRun Begin(PdfDocument pdf, out ListLayoutRun? previous)
    {
        previous = s_current;
        var run = new ListLayoutRun(pdf);
        s_current = run;
        return run;
    }

    internal static void End(ListLayoutRun? previous) => s_current = previous;

    /// <summary>Records that a list holds an entry the document cannot draw.</summary>
    internal void NoteUndrawable() => AllAsTwin = true;

    /// <summary>
    /// Tells every list to forget the layout it holds, after a pass whose result is not kept. What
    /// it left behind is not what a first layout would have.
    /// </summary>
    internal void StartOver() => Generation++;

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

    private bool CanDrawEmbedded(EmbeddedFontHandle handle)
    {
        if (ReferenceEquals(handle, _lastHandle)) return _lastHandleDrawable;
        _lastHandleDrawable = Pdf.CanDrawFont(handle);
        _lastHandle = handle;
        return _lastHandleDrawable;
    }

    private bool IsStandardDefined(Standard14 font)
    {
        if (_hasLastStandard && _lastStandard == font) return _lastStandardDefined;
        _lastStandardDefined = Enum.IsDefined(font);
        _lastStandard = font;
        _hasLastStandard = true;
        return _lastStandardDefined;
    }
}
