// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Reader.Content;

namespace VellumPdf.Reader;

/// <summary>
/// One glyph positioned by <see cref="GlyphPositioner"/>, ready for <see cref="TextAssembler"/> to
/// fold into the page's text (#98).
/// </summary>
/// <param name="Characters">
/// The glyph's Unicode mapping (§9.10.2's glyph-name route; <c>/ToUnicode</c> is not parsed yet),
/// or an empty string when the code has no mapping. Never <see langword="null"/>: the empty string
/// is what lets <see cref="TextAssembler"/> still register the glyph's position (§9.4.4's advance
/// applies regardless of Unicode mapping) without inventing a placeholder character.
/// </param>
/// <param name="Code">The character code, as <see cref="Fonts.DecodedGlyph.Code"/> reported it.</param>
/// <param name="Trm">
/// The painted text rendering matrix (§9.4.4), rise included: where this glyph is actually drawn.
/// </param>
/// <param name="LineY">
/// <see cref="Trm"/>'s own translation row computed with rise forced to zero instead — the line-
/// grouping key <see cref="TextAssembler"/> compares between consecutive glyphs, not <see
/// cref="Trm"/>'s own <c>F</c>. §9.3.7 scopes rise to superscripts and subscripts, which read as
/// the same line as their surrounding text, so folding rise into the key would put every
/// superscript on its own line.
/// </param>
/// <param name="Advance">
/// The text-space displacement (§9.4.4's <c>tx</c>, from <see
/// cref="GlyphPositioner.ComputeGlyphDisplacement"/>) this glyph advanced the text matrix by.
/// </param>
/// <param name="FontSize">Tfs in effect when this glyph was shown.</param>
/// <param name="RenderMode">Tr (§9.3.6) in effect when this glyph was shown. Not filtered on:
/// extraction reports what the file contains, so an invisible-text glyph (<c>3 Tr</c>, an OCR
/// layer over a scanned page being the common case) is included the same as any other.</param>
internal readonly record struct PositionedGlyph(
    string Characters, int Code, Matrix Trm, double LineY, double Advance, double FontSize,
    int RenderMode);
