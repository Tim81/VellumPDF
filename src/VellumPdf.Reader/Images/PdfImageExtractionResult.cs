// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary> The result of one <c>ExtractImages</c> call (#98): every image found, in draw order
/// (document level: first-page-first, deduped by object identity; page level: every occurrence,
/// undeduped; see <see cref="PdfDocumentReader.ExtractImages()"/> and <see
/// cref="PdfReadPage.ExtractImages()"/> for which), and every diagnostic raised while walking for
/// them.
/// </summary>
public sealed class PdfImageExtractionResult
{
    /// <summary>The images found.</summary>
    public IReadOnlyList<PdfExtractedImage> Images { get; }

    /// <summary>
    /// Every diagnostic this call raised: the interpreter's own (ISO 32000-2 §7.8.2 content-stream
    /// conditions) alongside <see cref="PdfReaderDiagnosticCode"/>'s <c>5xx</c> image codes. Also
    /// forwarded into <see cref="PdfDocumentReader.Diagnostics"/> (see
    /// <c>DiagnosticSink.CreateScope</c> for the identity contract this shares with the parent, and
    /// its one limit once the parent already holds a given diagnostic's key).
    /// </summary>
    public IReadOnlyList<PdfReaderDiagnostic> Diagnostics { get; }

    internal PdfImageExtractionResult(
        IReadOnlyList<PdfExtractedImage> images, IReadOnlyList<PdfReaderDiagnostic> diagnostics)
    {
        Images = images;
        Diagnostics = diagnostics;
    }
}
