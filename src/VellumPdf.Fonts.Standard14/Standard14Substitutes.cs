// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// The bundled Liberation font programs, for code that registers a font itself. A Layout
/// <c>Document</c> embeds one with <c>doc.UseTrueTypeFont(Standard14Substitutes.GetFontProgram(font))</c>.
/// </summary>
public static class Standard14Substitutes
{
    /// <summary>
    /// Returns the TrueType program of the Liberation face that stands in for
    /// <paramref name="font"/>: Liberation Sans for the Helvetica family, Liberation Serif for the
    /// Times family and Liberation Mono for the Courier family, in the matching weight and slant.
    /// </summary>
    /// <remarks>
    /// <para><b>A value this package has no face for is refused at this call.</b> A
    /// <see cref="Standard14"/> value that is not one of the twelve Helvetica, Times and Courier
    /// faces throws <see cref="System.NotSupportedException"/>. That covers
    /// <see cref="Standard14.Symbol"/>, <see cref="Standard14.ZapfDingbats"/> and a number the
    /// enumeration does not name. The Liberation set has no symbol face.</para>
    /// <para>Every call returns a new array, copied from the embedded resource, so a caller that
    /// changes the bytes cannot alter what the next call returns. Two calls for the same face
    /// return equal bytes, and a document that registers equal bytes twice gets the same handle
    /// back, so calling this more than once costs a copy, not a second embedded subset.</para>
    /// <para><b>Do not</b> expect a <see cref="Standard14"/> face in a text style to use this
    /// font. A style whose font is a <see cref="Standard14"/> value stays unembedded. Register
    /// the bytes and set the style's <c>FontRef</c> to the handle.</para>
    /// </remarks>
    /// <param name="font">The standard-14 font to substitute.</param>
    /// <returns>A new array holding the font program.</returns>
    /// <exception cref="System.NotSupportedException">
    /// <paramref name="font"/> is Symbol, ZapfDingbats or a value the enumeration does not name.
    /// </exception>
    public static byte[] GetFontProgram(Standard14 font) =>
        Standard14FontExtensions.LoadFont(Standard14FontExtensions.ResourceNameFor(font));
}
