// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>Flow-layout integration for adding a barcode to a <see cref="VellumPdf.Layout.Document"/>.</summary>
public static class DocumentBarcodeExtensions
{
    /// <summary>
    /// Adds a barcode to the document content, wrapped in a <see cref="BarcodeRenderer"/>.
    /// Returns <paramref name="document"/> for chaining.
    /// </summary>
    /// <param name="document">The document to add the barcode to.</param>
    /// <param name="barcode">The barcode to add.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> or <paramref name="barcode"/> is null.</exception>
    public static VellumPdf.Layout.Document Add(this VellumPdf.Layout.Document document, Barcode barcode)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(barcode);
        return document.Add(new BarcodeRenderer(barcode));
    }
}
