// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Canvas;

/// <summary>
/// One character, in one font, that a canvas wrote as glyph 0 (the <c>.notdef</c> glyph) because
/// the font has no glyph for it, with the number of times it was written. Read the entries from
/// <see cref="PdfCanvas.NotdefGlyphs"/> and
/// <see cref="VellumPdf.Document.PdfDocument.NotdefGlyphs"/>.
/// </summary>
/// <remarks>
/// <b>Nothing here is refused and nothing throws.</b> The type has no public constructor and only
/// getters, so it takes no input that could be wrong. ISO 19005-2, 6.2.11.8, and ISO 14289-1,
/// 7.21.8, forbid a reference to the <c>.notdef</c> glyph in a PDF/A-2 or PDF/UA-1 file; this
/// type reports that a canvas wrote one and leaves the decision to the caller.
/// <para>An entry exists for every glyph id 0 the canvas wrote through
/// <see cref="PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/> and through the library's own
/// text drawing. It does not exist for content bytes set directly on a
/// <see cref="VellumPdf.Document.PdfPage"/>, for a canvas whose <see cref="PdfCanvas.Finish"/> has
/// not run, or for entries past the 1,000 the list holds;
/// <see cref="PdfCanvas.NotdefGlyphsDropped"/> counts those. <b>Do not</b> read an empty list as
/// proof that the file holds no glyph 0.</para>
/// <para>Reading an entry while another thread draws on the canvas that holds it is not
/// supported.</para>
/// </remarks>
public sealed class NotdefGlyphWarning
{
    internal NotdefGlyphWarning(Rune? character, string? fontResourceName, string? fontName, int count)
    {
        Character = character;
        FontResourceName = fontResourceName;
        FontName = fontName;
        Count = count;
    }

    /// <summary>
    /// The character that had no glyph, or null when the canvas never saw the text.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is refused and nothing throws.</b> The value is null for glyph ids passed to
    /// <see cref="PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/>, because the canvas receives
    /// ids and not text. The caller of that method holds the text and can find the characters with
    /// <c>EmbeddedFontHandle.GetGlyphId</c>, which returns 0 for exactly these. A character outside
    /// the Basic Multilingual Plane is one <see cref="Rune"/>, so it is one entry.
    /// </remarks>
    public Rune? Character { get; }

    /// <summary>
    /// The resource name the content stream selects the font by, such as <c>TT1</c>, or null when
    /// no font had been selected on the canvas.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is refused and nothing throws.</b> The name is the one given to the last
    /// <see cref="PdfCanvas.SetFont(VellumPdf.Fonts.PdfFontResource, double)"/> or
    /// <see cref="PdfCanvas.SetFontByName(string, double)"/> call on the canvas. It does not follow
    /// a font that <see cref="PdfCanvas.RestoreState"/> brings back. Resource names are counters
    /// local to one document, so within a document the pair of character and name identifies one
    /// character in one font.
    /// </remarks>
    public string? FontResourceName { get; }

    /// <summary>
    /// The font's PostScript name without the subset tag, such as <c>DejaVuSans</c>, or null when
    /// the canvas does not know it.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is refused and nothing throws.</b> Only the library's own text drawing knows
    /// the name. Glyph ids passed to
    /// <see cref="PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/> leave it null; use
    /// <see cref="FontResourceName"/> to find the font.
    /// </remarks>
    public string? FontName { get; }

    /// <summary>
    /// The number of times glyph 0 was written for this character in this font, saturating at
    /// <see cref="int.MaxValue"/>.
    /// </summary>
    /// <remarks>
    /// <b>Nothing here is refused and nothing throws.</b> The list a canvas returns is live, so the
    /// count of an entry grows while the canvas draws. <b>Do not</b> read it from another thread
    /// while the canvas draws.
    /// </remarks>
    public int Count { get; internal set; }
}
