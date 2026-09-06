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
/// <param name="Trm">
/// The painted text rendering matrix (§9.4.4), rise included: where this glyph is actually drawn.
/// </param>
/// <param name="LineY">
/// <see cref="GlyphPositioner.ComputeLineKey"/> applied to the SAME Trm computation with rise
/// forced to zero instead — the line-grouping key <see cref="TextAssembler"/> compares between
/// consecutive glyphs, not <see cref="Trm"/>'s own <c>F</c> (see that method's remarks for why
/// plain <c>F</c> only works when the page is not rotated). §9.3.7 scopes rise to superscripts and
/// subscripts, which read as the same line as their surrounding text, so folding rise into the key
/// would put every superscript on its own line.
/// </param>
/// <param name="FontSize">Tfs in effect when this glyph was shown.</param>
internal readonly record struct PositionedGlyph(string Characters, Matrix Trm, double LineY, double FontSize);
