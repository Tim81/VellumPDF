// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

public sealed class DocumentTests
{
    [Fact]
    public void Save_singleParagraph_producesValidPdf()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello, VellumPdf layout engine!"));

        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void Save_withMetadata_includesInfoDict()
    {
        using var doc = new Document();
        doc.Info.Title = "Layout Test";
        doc.Info.Author = "Test";
        doc.Add("Some content");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/Info", content);
    }

    [Fact]
    public void Save_longText_createsTwoPages()
    {
        using var doc = new Document();
        // ~40 paragraphs of text should overflow one A4 page at 12pt
        for (var i = 0; i < 50; i++)
            doc.Add($"Paragraph number {i + 1}: The quick brown fox jumps over the lazy dog.");

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        // At least 2 pages → /Count must be >= 2
        Assert.DoesNotContain("/Count 1\n", content);
    }

    [Fact]
    public void Save_lineSeparator_succeeds()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Before"));
        doc.Add(new LineSeparator());
        doc.Add(new Paragraph("After"));

        var ms = new MemoryStream();
        doc.Save(ms); // just verifies no exception
        Assert.True(ms.Length > 0);
    }
}
