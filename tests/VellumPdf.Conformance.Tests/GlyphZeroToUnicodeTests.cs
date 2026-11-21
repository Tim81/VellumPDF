// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Conformance.Tests.Fonts;
using VellumPdf.Document;
using Xunit;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// The ToUnicode map maps glyph 0 to U+FFFD, which the Unicode value rule accepts (ISO 14289-1,
/// 7.21.7), so a file that draws a missing character fails the .notdef rule and only that one (#592).
/// </summary>
public sealed class GlyphZeroToUnicodeTests
{
    private static byte[] DocumentDrawingAMissingCharacter()
    {
        using var doc = new PdfDocument
        {
            Conformance = VellumPdf.Document.PdfConformance.PdfA2u,
            Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
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

    [Fact]
    public void AMissingCharacter_failsTheNotdefRule_andNotTheToUnicodeRule()
    {
        var pdf = DocumentDrawingAMissingCharacter();
        var text = System.Text.Encoding.Latin1.GetString(CmapFormat12Tests.Inflated(pdf));
        Assert.Contains("<0000> <FFFD>", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<0000> <26B9>", text, StringComparison.Ordinal);

        var ids = PdfPreflight.Validate(pdf, PdfConformance.PdfA2U).Assertions.Select(a => a.RuleId).ToList();

        Assert.Contains("ISO19005-2:6.2.11.8-notdef", ids);
        Assert.DoesNotContain("ISO19005-2:6.2.11.7.2-tounicode", ids);
    }
}
