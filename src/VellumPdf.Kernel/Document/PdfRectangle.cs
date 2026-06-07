// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Document;

/// <summary>PDF rectangle [llx lly urx ury] in user-space units (points).</summary>
public sealed class PdfRectangle
{
    public double LlX { get; }
    public double LlY { get; }
    public double UrX { get; }
    public double UrY { get; }

    public double Width  => UrX - LlX;
    public double Height => UrY - LlY;

    public PdfRectangle(double llx, double lly, double urx, double ury)
    {
        LlX = llx; LlY = lly; UrX = urx; UrY = ury;
    }

    /// <summary>A4 portrait: 210 × 297 mm at 72 pt/in (595.28 × 841.89 pt).</summary>
    public static readonly PdfRectangle A4 = new(0, 0, 595.28, 841.89);

    public PdfArray ToArray() => new([
        new PdfReal(LlX), new PdfReal(LlY), new PdfReal(UrX), new PdfReal(UrY)
    ]);
}
