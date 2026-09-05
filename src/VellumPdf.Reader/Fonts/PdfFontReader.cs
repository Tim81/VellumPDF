// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader.Fonts;

/// <summary>
/// One decoded glyph from a content-stream string operand (ISO 32000-2 §9.4.3).
/// </summary>
/// <param name="Code">The character code as read from the string.</param>
/// <param name="CodeLength">Bytes consumed from the string for this code; always 1 for a simple
/// font (§9.6.5), since every code in a simple font is a single byte.</param>
/// <param name="Width">The glyph's advance width, in the thousandths-of-text-space unit
/// <c>/Widths</c> itself uses (§9.6.2.1 Table 109). Always populated: <c>MissingWidth</c> (0
/// unless the font descriptor overrides it) when the font gives this code no width of its own,
/// never <see langword="null"/>.</param>
/// <param name="Unicode">The code's Unicode mapping, or <see langword="null"/> when no route maps
/// it (§9.10.2). This PR's <see cref="SimpleFontReader"/> populates this only from the glyph-name
/// route (the AGL, or the ZapfDingbats list); the higher-priority <c>/ToUnicode</c> route is parsed
/// starting in a later PR, tracked by <see cref="PdfFontReader.HasToUnicode"/> until then.</param>
/// <param name="IsSpaceCode">Whether this is the single-byte code 32, the word-spacing code
/// <c>Tw</c> applies to (§9.3.3) for a simple font.</param>
internal readonly record struct DecodedGlyph(
    int Code, int CodeLength, double Width, string? Unicode, bool IsSpaceCode);

/// <summary>
/// Decodes glyphs from a font's string operands. One instance is built per distinct font resource
/// (see <see cref="FontCache"/>) and reused across every string shown with it.
/// </summary>
internal abstract class PdfFontReader
{
    /// <summary>
    /// Decodes the next glyph starting at <paramref name="offset"/> into <paramref name="bytes"/>,
    /// advancing <paramref name="offset"/> past the bytes consumed. Returns <see langword="false"/>
    /// without advancing <paramref name="offset"/> when it is already at the end of
    /// <paramref name="bytes"/>.
    /// </summary>
    public abstract bool TryDecodeNext(ReadOnlySpan<byte> bytes, ref int offset, out DecodedGlyph glyph);

    /// <summary>
    /// Whether this font's dictionary names a <c>/ToUnicode</c> stream (§9.10.3). This PR only
    /// records the fact; a later PR parses the stream and gives it priority over the glyph-name
    /// route in <see cref="DecodedGlyph.Unicode"/>, per §9.10.2's own ordering.
    /// </summary>
    public abstract bool HasToUnicode { get; }
}
