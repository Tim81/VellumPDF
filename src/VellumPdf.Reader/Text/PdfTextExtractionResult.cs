// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary> The result of one <c>ExtractText</c> call (#98): the extracted text, and every
/// diagnostic raised while walking for it. See <see cref="PdfDocumentReader.ExtractText()"/> and
/// <see cref="PdfReadPage.ExtractText()"/> for how <see cref="Text"/> is assembled.
/// </summary>
public sealed class PdfTextExtractionResult
{
    /// <summary>The extracted text. Never <see langword="null"/>: a page (or a call) that draws no
    /// text at all yields the empty string, not a null one.</summary>
    public string Text { get; }

    /// <summary>
    /// Every diagnostic this call raised: the interpreter's own (ISO 32000-2 §7.8.2 content-stream
    /// conditions), the font subsystem's own <c>4xx</c> codes, and <see
    /// cref="PdfReaderDiagnosticCode"/>'s <c>6xx</c> text codes. Also forwarded into <see
    /// cref="PdfDocumentReader.Diagnostics"/> (see <c>DiagnosticSink.CreateScope</c> for the
    /// identity contract this shares with the parent).
    /// </summary>
    public IReadOnlyList<PdfReaderDiagnostic> Diagnostics { get; }

    internal PdfTextExtractionResult(string text, IReadOnlyList<PdfReaderDiagnostic> diagnostics)
    {
        Text = text;
        Diagnostics = diagnostics;
    }
}
