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
    /// <c>"\f"</c> (form feed), the same character <c>pdftotext</c> emits between pages.
    /// </summary>
    /// <exception cref="ArgumentNullException">Set to <see langword="null"/>. Checked in this
    /// property's own <c>init</c> accessor, unlike <see cref="Pages"/> above (whose validity
    /// depends on which overload consumes it, so it cannot be judged from this type alone): a
    /// <see langword="null"/> separator is invalid regardless of context, so this options object
    /// is either fully valid or never constructed at all, and both <see
    /// cref="PdfDocumentReader.ExtractText(PdfTextExtractionOptions)"/> and <see
    /// cref="PdfReadPage.ExtractText(PdfTextExtractionOptions)"/> can rely on that without
    /// re-checking it themselves.</exception>
    public string PageSeparator
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    } = "\f";
}
