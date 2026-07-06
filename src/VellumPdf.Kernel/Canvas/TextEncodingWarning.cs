// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Canvas;

/// <summary>
/// Records a single character that <see cref="PdfCanvas.ShowText"/> could not represent in
/// WinAnsiEncoding (the encoding used for Standard-14 fonts) and therefore substituted with '?'.
/// Characters that WinAnsi covers, including the 0x80–0x9F punctuation block, never produce a
/// warning. Use an embedded font (via <c>SetFontByName</c> + <c>ShowGlyphs</c>) to render glyphs
/// outside WinAnsi.
/// </summary>
public readonly record struct TextEncodingWarning(char Character)
{
    /// <summary>
    /// The value of the unmapped <see cref="Character"/>. For an astral character outside the
    /// Basic Multilingual Plane, each UTF-16 surrogate half is reported as its own warning, so
    /// this is a surrogate code unit (0xD800–0xDFFF) rather than a full Unicode scalar value.
    /// </summary>
    public int CodePoint => Character;
}
