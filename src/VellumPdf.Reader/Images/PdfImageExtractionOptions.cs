// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// Options for <see cref="PdfDocumentReader.ExtractImages(PdfImageExtractionOptions)"/> and
/// <see cref="PdfReadPage.ExtractImages(PdfImageExtractionOptions)"/> (#98).
/// </summary>
public sealed class PdfImageExtractionOptions
{
    /// <summary>Creates an options instance with every property at its default.</summary>
    public PdfImageExtractionOptions()
    {
    }

    /// <summary>
    /// Whether inline images (ISO 32000-2 §8.9.7, the <c>BI</c>…<c>ID</c>…<c>EI</c> form) are
    /// included. Default <see langword="true"/>.
    /// </summary>
    public bool IncludeInlineImages { get; init; } = true;

    /// <summary> Whether annotation appearance streams (ISO 32000-2 §12.5.5, an annotation's <c>/AP
    /// /N</c>) are walked for the images they draw, in addition to the page's own content. Every
    /// appearance state a dictionary-valued <c>/N</c> carries is walked, not only the one
    /// <c>/AS</c> selects (§12.5.5: <c>/AS</c> is a rendering-time choice, and extraction asks what
    /// the file contains). The same reasoning extends to a Hidden annotation (§12.5.3, the
    /// <c>/F</c> flag) and to an image inside an optional-content group an <c>/OC</c> entry
    /// (Table 87) or the catalog's own <c>OCProperties</c> marks OFF: neither is evaluated, and an
    /// image behind either is still returned. Default <see langword="true"/>.
    /// </summary>
    public bool IncludeAnnotationAppearances { get; init; } = true;
}
