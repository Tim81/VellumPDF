// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Canvas;

/// <summary>
/// Records a single Unicode scalar that <see cref="PdfCanvas.ShowText"/> could not represent in
/// WinAnsiEncoding (the encoding used for Standard-14 fonts) and therefore substituted with '?'.
/// Characters that WinAnsi covers, including the 0x80–0x9F punctuation block, never produce a
/// warning. Use an embedded font (via <c>SetFontByName</c> + <c>ShowGlyphs</c>) to render glyphs
/// outside WinAnsi.
/// </summary>
/// <param name="Character">
/// The unrepresentable scalar. A <see cref="Rune"/> rather than a <see cref="char"/> so that a
/// character outside the Basic Multilingual Plane is reported once, as itself, instead of twice as
/// its two UTF-16 surrogate halves — neither of which is a Unicode code point.
/// </param>
public readonly record struct TextEncodingWarning(Rune Character)
{
    /// <summary>
    /// The scalar value of the unmapped <see cref="Character"/>, in the range U+0000–U+10FFFF.
    /// Never a surrogate code unit.
    /// </summary>
    public int CodePoint => Character.Value;
}
