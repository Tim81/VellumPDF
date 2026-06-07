// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Document;

/// <summary>Document information dictionary (ISO 32000-2 §14.3.3).</summary>
public sealed class PdfDocumentInfo
{
    public string? Title    { get; set; }
    public string? Author   { get; set; }
    public string? Subject  { get; set; }
    public string? Keywords { get; set; }
    public string? Creator  { get; set; }
    public string? Producer { get; set; }

    internal PdfDictionary BuildDictionary()
    {
        var d = new PdfDictionary();
        SetIfPresent(d, "Title",    Title);
        SetIfPresent(d, "Author",   Author);
        SetIfPresent(d, "Subject",  Subject);
        SetIfPresent(d, "Keywords", Keywords);
        SetIfPresent(d, "Creator",  Creator);
        SetIfPresent(d, "Producer", Producer);
        return d;
    }

    private static void SetIfPresent(PdfDictionary d, string key, string? value)
    {
        if (value is not null)
            d.Set(new PdfName(key), PdfLiteralString.FromUnicode(value));
    }
}
