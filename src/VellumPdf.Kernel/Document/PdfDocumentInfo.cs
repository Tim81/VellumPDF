// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Document;

/// <summary>Document information dictionary (ISO 32000-2 §14.3.3).</summary>
public sealed class PdfDocumentInfo
{
    /// <summary>The document's title (<c>/Title</c>).</summary>
    public string? Title { get; set; }
    /// <summary>The name of the person who created the document (<c>/Author</c>).</summary>
    public string? Author { get; set; }
    /// <summary>The subject of the document (<c>/Subject</c>).</summary>
    public string? Subject { get; set; }
    /// <summary>Keywords associated with the document (<c>/Keywords</c>).</summary>
    public string? Keywords { get; set; }
    /// <summary>The application that created the original document (<c>/Creator</c>).</summary>
    public string? Creator { get; set; }
    /// <summary>The application that produced the PDF (<c>/Producer</c>).</summary>
    public string? Producer { get; set; }

    internal PdfDictionary BuildDictionary()
    {
        var d = new PdfDictionary();
        SetIfPresent(d, "Title", Title);
        SetIfPresent(d, "Author", Author);
        SetIfPresent(d, "Subject", Subject);
        SetIfPresent(d, "Keywords", Keywords);
        SetIfPresent(d, "Creator", Creator);
        SetIfPresent(d, "Producer", Producer);
        return d;
    }

    private static void SetIfPresent(PdfDictionary d, string key, string? value)
    {
        if (value is not null)
            d.Set(new PdfName(key), PdfLiteralString.FromUnicode(value));
    }
}
