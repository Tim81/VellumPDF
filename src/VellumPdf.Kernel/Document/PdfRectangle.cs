// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Document;

/// <summary>PDF rectangle [llx lly urx ury] in user-space units (points).</summary>
public sealed class PdfRectangle
{
    /// <summary>Lower-left X coordinate (points).</summary>
    public double LlX { get; }

    /// <summary>Lower-left Y coordinate (points).</summary>
    public double LlY { get; }

    /// <summary>Upper-right X coordinate (points).</summary>
    public double UrX { get; }

    /// <summary>Upper-right Y coordinate (points).</summary>
    public double UrY { get; }

    /// <summary>Rectangle width (<see cref="UrX"/> − <see cref="LlX"/>), in points.</summary>
    public double Width => UrX - LlX;

    /// <summary>Rectangle height (<see cref="UrY"/> − <see cref="LlY"/>), in points.</summary>
    public double Height => UrY - LlY;

    /// <summary>Creates a rectangle from its lower-left and upper-right corners (points).</summary>
    /// <param name="llx">Lower-left X coordinate.</param>
    /// <param name="lly">Lower-left Y coordinate.</param>
    /// <param name="urx">Upper-right X coordinate.</param>
    /// <param name="ury">Upper-right Y coordinate.</param>
    public PdfRectangle(double llx, double lly, double urx, double ury)
    {
        LlX = llx; LlY = lly; UrX = urx; UrY = ury;
    }

    /// <summary>A4 portrait. Delegates to <see cref="PageSize.A4"/> to keep a single canonical value.</summary>
    public static PdfRectangle A4 => PageSize.A4;

    /// <summary>Returns the rectangle as a PDF array <c>[llx lly urx ury]</c>.</summary>
    public PdfArray ToArray() => new([
        new PdfReal(LlX), new PdfReal(LlY), new PdfReal(UrX), new PdfReal(UrY)
    ]);
}
