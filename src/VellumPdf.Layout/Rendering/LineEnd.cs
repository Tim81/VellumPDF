// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Internal: how a wrapped line ended. Nothing reads it when a paragraph is drawn; it records where
/// each line's end falls.
/// </summary>
internal enum LineEnd : byte
{
    /// <summary>The line ended at a space, which the wrap dropped.</summary>
    Soft,

    /// <summary>The line ended at a line break in the text.</summary>
    Hard,

    /// <summary>
    /// The line ended inside a word that is wider than the line, cut between two characters: at the
    /// glyph, or, when the document set <see cref="VellumPdf.Layout.Document.BreakLongWordsAtPlaces"/>,
    /// after a solidus, a reverse solidus or a hyphen-minus where one fits.
    /// </summary>
    Forced,

    /// <summary>The line is the last one, and ends where the text does.</summary>
    Last,
}
