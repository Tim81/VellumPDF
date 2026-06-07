// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Thread-local font resource registry used during a document's Draw pass.
/// Set by <see cref="DocumentRenderer"/> before calling Draw on each page.
/// </summary>
internal static class DocumentFontRegistry
{
    [ThreadStatic]
    private static PdfDocument? _document;

    internal static void SetDocument(PdfDocument doc) => _document = doc;

    internal static PdfFontResource GetOrCreate(Standard14 font)
    {
        if (_document is null)
            throw new InvalidOperationException("Draw called outside a DocumentRenderer.Draw pass.");
        return _document.UseFont(font);
    }
}
