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

    /// <summary>A4 portrait. Delegates to <see cref="PageSize.A4"/> to keep a single canonical value.</summary>
    public static PdfRectangle A4 => PageSize.A4;

    public PdfArray ToArray() => new([
        new PdfReal(LlX), new PdfReal(LlY), new PdfReal(UrX), new PdfReal(UrY)
    ]);
}
