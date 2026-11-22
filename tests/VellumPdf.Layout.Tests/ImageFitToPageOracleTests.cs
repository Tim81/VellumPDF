// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.ImageFitToPageTests;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #471 against veraPDF and qpdf: a fitted image in a mirrored, tagged document that claims PDF/A
/// or PDF/UA. The image is as tall as the content box, so the page it is on holds nothing else.
/// </summary>
public sealed class ImageFitToPageOracleTests
{
    // A tagged document: a heading, then a fitted image with alternate text and an anchor, on a
    // page whose margins differ between recto and verso, then a paragraph on the next page.
    private static Document Build(PdfConformance conformance, bool tagged)
    {
        var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(30, 20, 30, 50),
            MirrorMargins = true,
            Conformance = conformance,
            Tagged = tagged,
            Language = "en-US",
            Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        doc.Info.Title = "Fitted image";
        if (conformance is PdfConformance.PdfA2u or PdfConformance.PdfA2a)
            doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");

        var style = new TextStyle { FontRef = doc.UseTrueTypeFont(FontReportSupport.DejaVuBytes), FontSize = 12 };
        doc.Add(new Heading("Figure one", style));
        doc.Add(new PageBreak());
        doc.Add(new LayoutImage(VellumPdf.Images.BmpImageLoader.Load(Bmp(3, 5)))
        {
            FitToPage = true,
            AltText = "A fitted figure",
            Anchor = "fig",
        });
        doc.Add(new PageBreak());
        doc.Add(new Paragraph("After the figure.", style));
        return doc;
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2u, false, "2u")]
    [InlineData(PdfConformance.PdfA2a, true, "2a")]
    [InlineData(PdfConformance.PdfUA1, true, "ua1")]
    public void FittedImage_inAMirroredDocument_isCompliant(PdfConformance conformance, bool tagged, string flavour)
    {
        using var doc = Build(conformance, tagged);
        var pdf = Save(doc);

        // The change is in the fixture: the image is drawn at the fitted size on the middle page.
        // The content box is 330 wide and 240 tall, and a 3 by 5 source is 550 tall at 330 wide,
        // so it is fitted to 240 and is 144 wide. Page two is verso, so its left edge is the
        // 20 pt outside margin.
        var pages = PageStreams(pdf);
        Assert.Equal(3, pages.Count);
        Assert.Contains("144 0 0 240 20 30 cm", pages[1], StringComparison.Ordinal);

        var path = Path.Combine(Path.GetTempPath(), "vellum-fit-" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, pdf);
        try
        {
            if (!ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut))
                OracleGate.Unavailable("qpdf");
            Assert.False(timedOut);
            Assert.True(exit == 0, stdout + stderr);
            Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }

        var report = FontReportSupport.VeraPdf(flavour, pdf);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);

        var text = Encoding.Latin1.GetString(pdf);
        if (tagged)
        {
            // A Figure with its alternate text and no /BBox, which this library writes for no
            // figure.
            var figure = FigureObjects(text);
            Assert.Contains("/S /Figure", figure, StringComparison.Ordinal);
            Assert.Contains("/Alt", figure, StringComparison.Ordinal);
            Assert.DoesNotContain("/BBox", figure, StringComparison.Ordinal);
        }
    }

    private static string FigureObjects(string text) =>
        string.Concat(Regex.Matches(text, @"(?m)^\d+ 0 obj\n<<[^>]*?/S /Figure[^>]*?>>").Select(m => m.Value));
}
