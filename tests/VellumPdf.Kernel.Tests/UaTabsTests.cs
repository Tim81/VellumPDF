// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// PDF/UA-1 (ISO 14289-1 §7.18.3): every page carrying an annotation must declare a tab order
/// of /S (structure order). Signed documents add a signature/DocTimeStamp widget annotation, so
/// without this a signed PDF/UA-1 document fails veraPDF (see the Signed_PdfUA1_BLTA oracle).
/// </summary>
public sealed class UaTabsTests
{
    private static byte[] SaveWithLink(PdfConformance conformance)
    {
        using var doc = new PdfDocument { Conformance = conformance };
        if (conformance is PdfConformance.PdfUA1 or PdfConformance.PdfA2a)
        {
            doc.Tagged = true;
            doc.Language = "en-US";
        }

        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(72, 72, 200, 100),
            Uri = "https://example.com",
        });

        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void PdfUA1_page_with_annotation_declares_Tabs_S()
    {
        var text = Encoding.Latin1.GetString(SaveWithLink(PdfConformance.PdfUA1));
        Assert.Contains("/Tabs /S", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NonUA_page_with_annotation_omits_Tabs()
    {
        var text = Encoding.Latin1.GetString(SaveWithLink(PdfConformance.PdfA2b));
        Assert.DoesNotContain("/Tabs", text, StringComparison.Ordinal);
    }
}
