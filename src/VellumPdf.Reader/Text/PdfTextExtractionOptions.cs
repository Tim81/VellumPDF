// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Options for <see cref="PdfDocumentReader.ExtractText(PdfTextExtractionOptions)"/> and
/// <see cref="PdfReadPage.ExtractText(PdfTextExtractionOptions)"/> (#98).
/// </summary>
public sealed class PdfTextExtractionOptions
{
    /// <summary>Creates an options instance with every property at its default.</summary>
    public PdfTextExtractionOptions()
    {
    }

    /// <summary>
    /// Which pages to extract, by zero-based index. Default <see cref="Range.All"/>. Meaningless on
    /// <see cref="PdfReadPage.ExtractText(PdfTextExtractionOptions)"/>, which already names one
    /// page: a value other than the default there throws <see cref="ArgumentException"/> rather
    /// than being silently ignored.
    /// </summary>
    public Range Pages { get; init; } = Range.All;

    /// <summary>
    /// The text inserted between two pages' own extracted text, on
    /// <see cref="PdfDocumentReader.ExtractText(PdfTextExtractionOptions)"/>. Default
    /// <c>"\f"</c> (form feed), the same page separator <c>pdftotext</c> emits, so a caller
    /// diffing this reader's own output against that oracle needs no separator translation.
    /// </summary>
    public string PageSeparator { get; init; } = "\f";
}
