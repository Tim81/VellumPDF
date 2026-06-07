// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Document;

/// <summary>Common page sizes as <see cref="PdfRectangle"/> constants (portrait).</summary>
public static class PageSize
{
    // 1 mm = 72/25.4 pt ≈ 2.8346 pt
    private const double MmToPt = 72.0 / 25.4;

    private static PdfRectangle Mm(double w, double h) =>
        new(0, 0, Math.Round(w * MmToPt, 2), Math.Round(h * MmToPt, 2));

    public static readonly PdfRectangle A0 = Mm(841, 1189);
    public static readonly PdfRectangle A1 = Mm(594, 841);
    public static readonly PdfRectangle A2 = Mm(420, 594);
    public static readonly PdfRectangle A3 = Mm(297, 420);
    public static readonly PdfRectangle A4 = Mm(210, 297);
    public static readonly PdfRectangle A5 = Mm(148, 210);
    public static readonly PdfRectangle A6 = Mm(105, 148);

    public static readonly PdfRectangle Letter = new(0, 0, 612, 792);
    public static readonly PdfRectangle Legal = new(0, 0, 612, 1008);
    public static readonly PdfRectangle Ledger = new(0, 0, 1224, 792);
}
