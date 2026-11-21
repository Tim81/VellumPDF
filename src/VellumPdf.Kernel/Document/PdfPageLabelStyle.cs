// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>
/// How the numeric part of a page label is written (ISO 32000-2, Table 161, entry <c>S</c>). Pass
/// one to <see cref="PdfDocument.AddPageLabelRange"/>.
/// </summary>
/// <remarks>
/// <b>A value that is not one of the members below is refused.</b>
/// <see cref="PdfDocument.AddPageLabelRange"/> throws <see cref="ArgumentOutOfRangeException"/>
/// from the call, naming <c>style</c>, because there is no <c>/S</c> value to write for it.
/// </remarks>
public enum PdfPageLabelStyle
{
    /// <summary>No numeric part: the label is the prefix alone, and a range without a prefix labels
    /// its pages with the empty string. <c>/S</c> is left out of the range's dictionary.</summary>
    None = 0,

    /// <summary>Decimal Arabic numerals: 1, 2, 3. Written as <c>/S /D</c>.</summary>
    Decimal = 1,

    /// <summary>Upper-case Roman numerals: I, II, III. Written as <c>/S /R</c>.</summary>
    UpperRoman = 2,

    /// <summary>Lower-case Roman numerals: i, ii, iii. Written as <c>/S /r</c>.</summary>
    LowerRoman = 3,

    /// <summary>Upper-case letters: the numbers 1 to 26 are A to Z, 27 to 52 are AA to ZZ and 53
    /// starts AAA. The letter repeats; it is not base 26. Written as <c>/S /A</c>.</summary>
    UpperLetters = 4,

    /// <summary>Lower-case letters: the numbers 1 to 26 are a to z, 27 to 52 are aa to zz and 53
    /// starts aaa. The letter repeats; it is not base 26. Written as <c>/S /a</c>.</summary>
    LowerLetters = 5,
}
