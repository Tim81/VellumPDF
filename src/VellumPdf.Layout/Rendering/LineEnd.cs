// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Internal: how a wrapped line ended. Nothing reads it when a paragraph is drawn; it records where
/// each line's end falls so that a later change can write a trailing space at some ends and not at
/// others without wrapping the text a second time.
/// </summary>
internal enum LineEnd : byte
{
    /// <summary>The line ended at a space, which the wrap dropped.</summary>
    Soft,

    /// <summary>The line ended at a line break in the text.</summary>
    Hard,

    /// <summary>The line ended inside a word that is wider than the line, cut between two characters.</summary>
    Forced,

    /// <summary>The line is the last one, and ends where the text does.</summary>
    Last,
}
