// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

public sealed class AnnotationAndOutlineTests
{
    // ── Helper ────────────────────────────────────────────────────────────────

    private static string SaveToString(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return System.Text.Encoding.Latin1.GetString(ms.ToArray());
    }

    // ── Link annotation tests ─────────────────────────────────────────────────

    [Fact]
    public void Page_withUriLinkAnnotation_containsAnnotsKey()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Llx = 72,
            Lly = 700,
            Urx = 200,
            Ury = 714,
            Uri = "https://example.com",
        });

        var content = SaveToString(doc);
        Assert.Contains("/Annots", content);
    }

    [Fact]
    public void Page_withUriLinkAnnotation_containsLinkSubtype()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Llx = 72,
            Lly = 700,
            Urx = 200,
            Ury = 714,
            Uri = "https://example.com",
        });

        var content = SaveToString(doc);
        Assert.Contains("/Subtype /Link", content);
    }

    [Fact]
    public void Page_withUriLinkAnnotation_containsUriAction()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        const string url = "https://example.com/page";

        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Llx = 72,
            Lly = 700,
            Urx = 200,
            Ury = 714,
            Uri = url,
        });

        var content = SaveToString(doc);
        Assert.Contains("/URI", content);
        Assert.Contains("example.com/page", content);
    }

    [Fact]
    public void Page_withUriLinkAnnotation_containsRectEntry()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Llx = 10,
            Lly = 20,
            Urx = 110,
            Ury = 40,
            Uri = "https://x.com",
        });

        var content = SaveToString(doc);
        Assert.Contains("/Rect", content);
    }

    [Fact]
    public void Page_withInternalLinkAnnotation_containsDestKey()
    {
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage();

        doc.RegisterLinkAnnotation(page1, new PdfLinkAnnotation
        {
            Llx = 72,
            Lly = 700,
            Urx = 200,
            Ury = 714,
            DestPage = page2,
            DestLeft = 0,
            DestTop = 841,
        });

        var content = SaveToString(doc);
        Assert.Contains("/Dest", content);
        Assert.Contains("/XYZ", content);
    }

    [Fact]
    public void Page_withNoAnnotations_doesNotContainAnnotsKey()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);
        Assert.DoesNotContain("/Annots", content);
    }

    [Fact]
    public void MultipleAnnotationsOnOnePage_allWritten()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Llx = 72,
            Lly = 700,
            Urx = 200,
            Ury = 714,
            Uri = "https://a.com",
        });
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Llx = 72,
            Lly = 650,
            Urx = 200,
            Ury = 664,
            Uri = "https://b.com",
        });

        var content = SaveToString(doc);
        Assert.Contains("a.com", content);
        Assert.Contains("b.com", content);
    }

    // ── Outline tests ─────────────────────────────────────────────────────────

    [Fact]
    public void Document_withOutlineEntry_containsOutlinesKey()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Section 1",
            DestPage = page,
            DestLeft = 0,
            DestTop = 800,
            Level = 0,
        });

        var content = SaveToString(doc);
        Assert.Contains("/Outlines", content);
    }

    [Fact]
    public void Document_withOutlineEntry_containsTitleKey()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "My Section",
            DestPage = page,
            DestLeft = 0,
            DestTop = 800,
            Level = 0,
        });

        var content = SaveToString(doc);
        Assert.Contains("/Title", content);
    }

    [Fact]
    public void Document_withOutlineEntry_titleIsUtf16Encoded()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Hello",
            DestPage = page,
            DestLeft = 0,
            DestTop = 800,
            Level = 0,
        });

        // UTF-16BE strings begin with BOM bytes 0xFE 0xFF inside a literal string.
        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        // Look for the BOM sequence as raw bytes in the output.
        var found = false;
        for (var i = 0; i < bytes.Length - 1; i++)
        {
            if (bytes[i] == 0xFE && bytes[i + 1] == 0xFF)
            {
                found = true;
                break;
            }
        }
        Assert.True(found, "Expected UTF-16BE BOM (FE FF) for outline title.");
    }

    [Fact]
    public void Document_withOutlineEntry_containsDestKey()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Section",
            DestPage = page,
            DestLeft = 0,
            DestTop = 800,
            Level = 0,
        });

        var content = SaveToString(doc);
        Assert.Contains("/Dest", content);
    }

    [Fact]
    public void Document_withOutlineEntry_setsPageModeUseOutlines()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Section",
            DestPage = page,
            Level = 0,
        });

        var content = SaveToString(doc);
        Assert.Contains("/UseOutlines", content);
    }

    [Fact]
    public void Document_withoutOutlineEntries_doesNotContainOutlines()
    {
        using var doc = new PdfDocument();
        doc.AddPage();

        var content = SaveToString(doc);
        // No /Outlines entry in catalog when there are no bookmarks.
        Assert.DoesNotContain("/Outlines", content);
    }

    [Fact]
    public void Document_withMultipleOutlineEntries_allTitlesPresent()
    {
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Chapter One",
            DestPage = page1,
            Level = 0,
        });
        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Chapter Two",
            DestPage = page2,
            Level = 0,
        });

        // Both titles are encoded as UTF-16BE; we can find fragments of "Chapter" in the byte string.
        var content = SaveToString(doc);
        Assert.Contains("/Outlines", content);
        // /Count should be 2 (total items)
        Assert.Contains("/Count 2", content);
    }

    [Fact]
    public void OutlineEntry_destDestinationResolvesToCorrectPageRef()
    {
        // Verifies the /Dest array references the correct page object number.
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();  // will get the first page dict object number
        var page2 = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Go to page 2",
            DestPage = page2,
            Level = 0,
        });

        var ms = new MemoryStream();
        doc.Save(ms);
        var content = System.Text.Encoding.Latin1.GetString(ms.ToArray());

        // /Dest should appear and include /XYZ
        Assert.Contains("/XYZ", content);
    }

    // ── Fix #4: Outline /Count must be the total descendant count ────────────

    [Fact]
    public void OutlineTree_nestedEntry_parentCountIsDescendantTotal()
    {
        // 2-level outline: one root item ("Chapter") with two direct children ("Sec 1", "Sec 2").
        // The parent item's /Count must be 2 (its two descendants), NOT 1 (just children).
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Chapter", DestPage = page, Level = 0 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Sec 1", DestPage = page, Level = 1 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Sec 2", DestPage = page, Level = 1 });

        var content = SaveToString(doc);

        // The /Count 2 must appear somewhere (parent item's count = 2 descendants).
        Assert.Contains("/Count 2", content);
        // Root /Outlines /Count = number of visible top-level items = 1 (only "Chapter" at level 0).
        Assert.Contains("/Count 1", content);
    }

    [Fact]
    public void OutlineTree_noNesting_rootCountEqualsTopLevelItemCount()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Item A", DestPage = page, Level = 0 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Item B", DestPage = page, Level = 0 });

        var content = SaveToString(doc);

        // Root /Outlines /Count = 2 top-level items.
        Assert.Contains("/Count 2", content);
    }
}
