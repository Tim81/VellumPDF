// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>Common page sizes as <see cref="PdfRectangle"/> constants (portrait).</summary>
public static class PageSize
{
    // 1 mm = 72/25.4 pt ≈ 2.8346 pt
    private const double MmToPt = 72.0 / 25.4;

    /// <summary>
    /// Builds a portrait <see cref="PdfRectangle"/> of <paramref name="widthMm"/> ×
    /// <paramref name="heightMm"/> millimetres, converted to PDF user-space points
    /// (the unit used throughout the API).
    /// </summary>
    public static PdfRectangle Mm(double widthMm, double heightMm) =>
        new(0, 0, Math.Round(widthMm * MmToPt, 2), Math.Round(heightMm * MmToPt, 2));

    /// <summary>ISO A0 (841 × 1189 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A0 = Mm(841, 1189);

    /// <summary>ISO A1 (594 × 841 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A1 = Mm(594, 841);

    /// <summary>ISO A2 (420 × 594 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A2 = Mm(420, 594);

    /// <summary>ISO A3 (297 × 420 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A3 = Mm(297, 420);

    /// <summary>ISO A4 (210 × 297 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A4 = Mm(210, 297);

    /// <summary>ISO A5 (148 × 210 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A5 = Mm(148, 210);

    /// <summary>ISO A6 (105 × 148 mm) in points, portrait.</summary>
    public static readonly PdfRectangle A6 = Mm(105, 148);

    /// <summary>US Letter (8.5 × 11 in = 612 × 792 pt), portrait.</summary>
    public static readonly PdfRectangle Letter = new(0, 0, 612, 792);

    /// <summary>US Legal (8.5 × 14 in = 612 × 1008 pt), portrait.</summary>
    public static readonly PdfRectangle Legal = new(0, 0, 612, 1008);

    /// <summary>US Ledger (17 × 11 in = 1224 × 792 pt).</summary>
    public static readonly PdfRectangle Ledger = new(0, 0, 1224, 792);
}
