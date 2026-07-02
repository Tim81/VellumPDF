// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Canvas;

/// <summary>
/// Specifies how text is aligned relative to the x coordinate passed to
/// <see cref="PdfCanvas.ShowTextAligned"/> and <see cref="PdfCanvas.ShowGlyphsAligned"/>.
/// </summary>
public enum TextAlignment
{
    /// <summary>The x coordinate is the left edge of the text. Text grows rightward.</summary>
    Left = 0,

    /// <summary>The x coordinate is the horizontal midpoint of the text.</summary>
    Center = 1,

    /// <summary>The x coordinate is the right edge of the text. Text grows leftward.</summary>
    Right = 2,
}
