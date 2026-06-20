// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Unit coverage for the VellumPdf.Fonts.Standard14 substitution package. The end-to-end
/// PDF/A conformance of its output is cross-validated against veraPDF by the
/// <c>pdfa2b-standard14-substitute</c> oracle fixture.
/// </summary>
public sealed class Standard14SubstituteTests
{
    [Theory]
    [InlineData(Standard14.Helvetica)]
    [InlineData(Standard14.TimesRoman)]
    [InlineData(Standard14.CourierBold)]
    public void EmbedStandard14Font_embedsAsType0WithFontFile2(Standard14 font)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var handle = doc.EmbedStandard14Font(font);
        doc.RegisterEmbeddedFontUsage(page, handle);

        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[3];
        var n = handle.GetGlyphIds("Abc", gids);
        canvas.ShowGlyphs(gids.AsSpan(0, n));
        canvas.EndText();
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var pdf = Encoding.Latin1.GetString(ms.ToArray());

        Assert.Contains("/Type0", pdf);
        Assert.Contains("/FontFile2", pdf);   // the substitute is embedded
        Assert.Contains("/Identity-H", pdf);
    }

    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    public void EmbedStandard14Font_throwsForUncoveredFonts(Standard14 font)
    {
        using var doc = new PdfDocument();
        Assert.Throws<NotSupportedException>(() => doc.EmbedStandard14Font(font));
    }

    [Fact]
    public void EmbedStandard14Font_nullDocument_throws()
    {
        PdfDocument doc = null!;
        Assert.Throws<ArgumentNullException>(() => doc.EmbedStandard14Font(Standard14.Helvetica));
    }
}
