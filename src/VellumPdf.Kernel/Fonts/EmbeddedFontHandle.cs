// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// An opaque handle to a TrueType font registered with a <see cref="VellumPdf.Document.PdfDocument"/>.
/// Returned by <c>PdfDocument.UseTrueTypeFont</c>; pass to canvas and layout APIs to use the font.
/// </summary>
/// <remarks>
/// A handle belongs to the document that returned it. Its <see cref="ResourceName"/> is a counter
/// local to that document, so another document refuses it where it records page usage, with
/// <see cref="ArgumentException"/> (#544). Embed the font again through the document that saves
/// the page.
/// </remarks>
public sealed class EmbeddedFontHandle
{
    internal TrueTypeFontEmbedder Embedder { get; }

    /// <summary>
    /// The document that embedded this font. A handle is only meaningful to that document: its
    /// <see cref="ResourceName"/> is a counter local to it, so two documents can each hold a
    /// different font called <c>TT1</c>, and a handle used on the wrong one would resolve to
    /// whichever font the saving document happened to register under that name (#544).
    /// </summary>
    internal object Owner { get; }

    /// <summary>PDF resource name assigned to this font (e.g. "TT1").</summary>
    public string ResourceName => Embedder.ResourceName;

    internal EmbeddedFontHandle(TrueTypeFontEmbedder embedder, object owner)
    {
        Embedder = embedder;
        Owner = owner;
    }

    /// <summary>Measures the width of <paramref name="text"/> in points at <paramref name="pointSize"/>.</summary>
    public double MeasureString(string text, double pointSize) =>
        Embedder.MeasureString(text, pointSize);

    /// <summary>
    /// Maps a Unicode code point to a glyph id, registering it for font subsetting.
    /// Safe to call from the layout/draw phase; all registered code points are included
    /// in the subset written during <c>Save</c>.
    /// </summary>
    public ushort GetGlyphId(int codePoint) => Embedder.GetGlyphId(codePoint);

    /// <summary>
    /// Converts every Unicode scalar in <paramref name="text"/> to glyph ids and writes
    /// them into <paramref name="buffer"/>. Returns the number of glyph ids written.
    /// The buffer must be at least as long as the number of Unicode scalars in the string.
    /// </summary>
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
