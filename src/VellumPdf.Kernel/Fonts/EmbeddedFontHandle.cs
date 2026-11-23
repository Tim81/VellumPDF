// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// An opaque handle to a TrueType font registered with a <see cref="VellumPdf.Document.PdfDocument"/>.
/// Returned by <c>PdfDocument.UseTrueTypeFont</c>; pass to canvas and layout APIs to use the font.
/// </summary>
/// <remarks>
/// A handle belongs to the document that returned it. Its <see cref="ResourceName"/> is a counter
/// local to that document, so another document cannot use it as it stands. A saving document that
/// defines no font under the handle's resource name refuses it; see
/// <see cref="VellumPdf.Document.PdfDocument.RegisterEmbeddedFontUsage"/>. Embed the font through
/// the document that saves the page.
/// </remarks>
public sealed class EmbeddedFontHandle
{
    internal TrueTypeFontEmbedder Embedder { get; }

    /// <summary>
    /// The document that embedded this font. A handle is only meaningful to that document: its
    /// <see cref="ResourceName"/> is a counter local to it, so two documents can each hold a
    /// different font called <c>TT1</c>, and a handle used on the wrong one would resolve to
    /// whichever font the saving document happened to register under that name.
    /// </summary>
    internal object Owner { get; }

    /// <summary>PDF resource name assigned to this font (e.g. "TT1").</summary>
    public string ResourceName => Embedder.ResourceName;

    /// <summary>The font's PostScript name without a subset tag, for reports that name the
    /// font.</summary>
    internal string PostScriptName => Embedder.PostScriptName;

    /// <summary>
    /// SHA-256 of the font program this handle embeds, as the owning document keyed it. Another
    /// document that embedded the same program recognises it by this value.
    /// </summary>
    internal string FontHash { get; }

    internal EmbeddedFontHandle(TrueTypeFontEmbedder embedder, object owner, string fontHash)
    {
        Embedder = embedder;
        Owner = owner;
        FontHash = fontHash;
    }

    /// <summary>Measures the width of <paramref name="text"/> in points at <paramref name="pointSize"/>.</summary>
    public double MeasureString(string text, double pointSize) =>
        Embedder.MeasureString(text, pointSize);

    /// <summary>
    /// The width <see cref="MeasureString"/> returns, without registering any character for the
    /// subset.
    /// </summary>
    internal double MeasureStringWithoutRegistering(string text, double pointSize) =>
        Embedder.MeasureStringWithoutRegistering(text, pointSize);

    /// <summary>
    /// Maps a Unicode code point to a glyph id, registering it for font subsetting.
    /// Safe to call from the layout/draw phase; all registered code points are included
    /// in the subset written during <c>Save</c>.
    /// </summary>
    /// <remarks>
    /// A code point the font has no glyph for gives glyph 0 (.notdef), with one exception:
    /// U+202F NARROW NO-BREAK SPACE and U+2007 FIGURE SPACE, when the font lacks them, give the
    /// glyph of U+00A0 NO-BREAK SPACE, or of U+0020 SPACE when it lacks that too. Layout keeps these
    /// characters in a paragraph rather than turning them into a plain space, so a font that cannot
    /// draw them must still be able to show a space. A font that has the glyph keeps it. The same
    /// lookup measures the text, so the width of the space that is drawn is the width that was
    /// measured.
    /// <para><b>Nothing here is refused and nothing throws.</b> A 0 is not an error at this call,
    /// but <see cref="VellumPdf.Canvas.PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/> writes
    /// it as CID 0, which ISO 19005-2, 6.2.11.8, and ISO 14289-1, 7.21.8, forbid in a PDF/A-2 or
    /// PDF/UA-1 file. Asking for a character that gives 0 is enough for the font's ToUnicode CMap
    /// to list glyph 0 once, as U+FFFD, whether or not it is drawn; the entry is there because
    /// glyph 0 is itself refused by those clauses. A Kernel caller sees a write in
    /// <see cref="VellumPdf.Canvas.PdfCanvas.NotdefGlyphs"/>, without the character. <b>Do
    /// not pass</b> a code point this method answers 0 for to <c>ShowGlyphs</c> under either
    /// claim.</para>
    /// </remarks>
    public ushort GetGlyphId(int codePoint) => Embedder.GetGlyphId(codePoint);

    /// <summary>
    /// The glyph id <see cref="GetGlyphId"/> would return, without registering it for the subset.
    /// </summary>
    internal ushort PeekGlyphId(int codePoint) => Embedder.PeekGlyphId(codePoint);

    /// <summary>
    /// Converts every Unicode scalar in <paramref name="text"/> to glyph ids and writes
    /// them into <paramref name="buffer"/>. Returns the number of glyph ids written.
    /// The buffer must be at least as long as the number of Unicode scalars in the string.
    /// </summary>
    /// <remarks>
    /// Each id is the one <see cref="GetGlyphId(int)"/> returns, so a 0 means the font has no glyph
    /// for the character, except the stand-ins described there. A scalar past the end of the buffer
    /// is left out and the returned count says how many were written. An unpaired surrogate is
    /// looked up as U+FFFD. <b>Do not pass</b> the ids to
    /// <see cref="VellumPdf.Canvas.PdfCanvas.ShowGlyphs(System.ReadOnlySpan{ushort})"/> under a
    /// PDF/A-2 or PDF/UA-1 claim without checking them for 0; see <see cref="GetGlyphId(int)"/>.
    /// </remarks>
    public int GetGlyphIds(string text, Span<ushort> buffer)
    {
        var idx = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (idx >= buffer.Length) break;
            buffer[idx++] = Embedder.GetGlyphId(rune.Value);
        }
        return idx;
    }
}
