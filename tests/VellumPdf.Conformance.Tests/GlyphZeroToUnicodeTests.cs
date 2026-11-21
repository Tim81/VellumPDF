// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Conformance.Tests.Fonts;
using VellumPdf.Document;
using Xunit;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// The ToUnicode map maps glyph 0 to U+FFFD, which the Unicode value rule accepts (ISO 14289-1,
/// 7.21.7), so a file that draws a missing character fails the .notdef rule and not the
/// ToUnicode rules (#592).
/// </summary>
public sealed class GlyphZeroToUnicodeTests
{
    private static byte[] DocumentDrawingAMissingCharacter(VellumPdf.Document.PdfConformance claim)
    {
        using var doc = new PdfDocument
        {
            Conformance = claim,
            Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        doc.Info.Title = "Glyph zero";
        doc.Language = "en-US";
        var page = doc.AddPage();
        var font = doc.UseTrueTypeFont(CmapFormat12Tests.LoadAsset("DejaVuSans.ttf"));
        doc.RegisterEmbeddedFontUsage(page, font);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(font.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 700);
        var ids = new ushort[2];
        font.GetGlyphIds("A\u26B9", ids);
        canvas.ShowGlyphs(ids).EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static void AssertFixtureHoldsTheChange(byte[] pdf)
    {
        var text = System.Text.Encoding.Latin1.GetString(CmapFormat12Tests.Inflated(pdf));
        Assert.Contains("<0000> <FFFD>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<0000> <26B9>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void PdfA_aMissingCharacter_failsTheNotdefRule_andNotTheToUnicodeRule()
    {
        var pdf = DocumentDrawingAMissingCharacter(VellumPdf.Document.PdfConformance.PdfA2u);
        AssertFixtureHoldsTheChange(pdf);

        var ids = PdfPreflight.Validate(pdf, PdfConformance.PdfA2U).Assertions.Select(a => a.RuleId).ToList();

        Assert.Contains("ISO19005-2:6.2.11.8-notdef", ids);
        Assert.DoesNotContain("ISO19005-2:6.2.11.7.2-tounicode", ids);
    }

    [Fact]
    public void PdfUa_aMissingCharacter_failsTheNotdefRule_andNotTheToUnicodeValueRule()
    {
        var pdf = DocumentDrawingAMissingCharacter(VellumPdf.Document.PdfConformance.PdfUA1);
        AssertFixtureHoldsTheChange(pdf);

        var ids = PdfPreflight.Validate(pdf, PdfConformance.PdfUA1).Assertions.Select(a => a.RuleId).ToList();

        Assert.Contains("ISO14289-1:7.21.8-1", ids);
        Assert.DoesNotContain("ISO14289-1:7.21.7-2", ids);
    }
}
