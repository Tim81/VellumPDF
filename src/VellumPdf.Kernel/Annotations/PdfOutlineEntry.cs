// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Annotations;

/// <summary>
/// A bookmark (outline item) to be written into the document's /Outlines tree.
/// Entries are flat-listed on the document; the <see cref="Level"/> property
/// controls the tree nesting (0 = top-level, 1 = child, …).
/// </summary>
public sealed class PdfOutlineEntry
{
    /// <summary>Bookmark title displayed in the reader's outline panel.</summary>
    public required string Title { get; init; }

    /// <summary>
    /// The target page. Resolved to a page indirect-reference during Save.
    /// Must not be null when actually writing the PDF.
    /// </summary>
    public required PdfPage DestPage { get; init; }

    /// <summary>X coordinate of the destination viewport origin (PDF user-space).</summary>
    public double DestLeft { get; init; }

    /// <summary>Y coordinate of the destination viewport origin (PDF user-space).</summary>
    public double DestTop { get; init; }

    /// <summary>
    /// Nesting level. 0 = top-level; each increment adds one level of indentation.
    /// </summary>
    public int Level { get; init; }

    /// <summary>
    /// When <see langword="true"/> (the default), the outline item is shown expanded in the
    /// reader's bookmark panel and its /Count is positive (ISO 32000-2 §12.3.3).
    /// When <see langword="false"/>, the item is shown collapsed and /Count is negative
    /// (the magnitude still reflects the number of visible descendants of the open subtree).
    /// Existing callers that do not set this property retain the previous behaviour
    /// (all items open, positive counts).
    /// </summary>
    public bool IsExpanded { get; init; } = true;
}
