// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <c>Standard14Substitutes.GetFontProgram</c> feeding a Layout <see cref="Document"/>. Each face
/// is checked against the Liberation file it must stand for, by bytes and through the document: it
/// hands back one handle for equal bytes, so a wrong file for a style returns another handle.
/// </summary>
public sealed class Standard14EmbedTests
{
    private static EmbeddedFontHandle Embed(Document doc, Standard14 font) =>
        doc.UseTrueTypeFont(Standard14Substitutes.GetFontProgram(font));

    public static TheoryData<Standard14, string> Faces => new()
    {
        { Standard14.Helvetica, "LiberationSans-Regular.ttf" },
        { Standard14.HelveticaBold, "LiberationSans-Bold.ttf" },
        { Standard14.HelveticaOblique, "LiberationSans-Italic.ttf" },
        { Standard14.HelveticaBoldOblique, "LiberationSans-BoldItalic.ttf" },
        { Standard14.TimesRoman, "LiberationSerif-Regular.ttf" },
        { Standard14.TimesBold, "LiberationSerif-Bold.ttf" },
        { Standard14.TimesItalic, "LiberationSerif-Italic.ttf" },
        { Standard14.TimesBoldItalic, "LiberationSerif-BoldItalic.ttf" },
        { Standard14.Courier, "LiberationMono-Regular.ttf" },
        { Standard14.CourierBold, "LiberationMono-Bold.ttf" },
        { Standard14.CourierOblique, "LiberationMono-Italic.ttf" },
        { Standard14.CourierBoldOblique, "LiberationMono-BoldItalic.ttf" },
    };

    [Theory]
    [MemberData(nameof(Faces))]
    public void EachFace_isTheLiberationFileForThatStyle(Standard14 font, string file)
    {
        using var doc = new Document();
        var byFile = doc.UseTrueTypeFont(File.ReadAllBytes(Path.Combine(FontReportSupport.FontsDir, file)));

        Assert.Equal(File.ReadAllBytes(Path.Combine(FontReportSupport.FontsDir, file)), Standard14Substitutes.GetFontProgram(font));
        Assert.Same(byFile, Embed(doc, font));
    }

    [Fact]
    public void CalledTwice_returnsTheSameHandle_andOneSubsetIsEmbedded()
    {
        using var doc = new Document();
        var first = Embed(doc, Standard14.TimesRoman);
        var second = Embed(doc, Standard14.TimesRoman);
        Assert.Same(first, second);

        doc.Add(new Paragraph("Hello", new TextStyle { FontRef = first, FontSize = 12 }));
        var pdf = System.Text.Encoding.Latin1.GetString(FontReportSupport.Save(doc));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(pdf, "/FontFile2"));
    }

    [Fact]
    public void TwoFaces_areTwoHandles()
    {
        using var doc = new Document();
        Assert.NotSame(Embed(doc, Standard14.TimesRoman), Embed(doc, Standard14.TimesBold));
    }

    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    [InlineData((Standard14)99)]
    [InlineData((Standard14)(-1))]
    public void AFaceWithNoSubstitute_isRefusedAtTheCall(Standard14 font)
    {
        var ex = Assert.Throws<NotSupportedException>(() => Standard14Substitutes.GetFontProgram(font));

        // A Layout caller has no PdfDocument, and a number is not a font.
        Assert.DoesNotContain("PdfDocument", ex.Message, StringComparison.Ordinal);
        if (!Enum.IsDefined(font))
            Assert.Contains($"{(int)font}, which is not a Standard14 value", ex.Message, StringComparison.Ordinal);
    }

    public static TheoryData<Standard14> AllFaces => [.. Faces.Select(f => f.Data.Item1)];

    [Theory]
    [MemberData(nameof(AllFaces))]
    public void EveryCallReturnsANewArray_soAChangeCannotReachTheNextCall(Standard14 font)
    {
        var first = Standard14Substitutes.GetFontProgram(font);
        var expected = (byte[])first.Clone();
        Assert.NotSame(first, Standard14Substitutes.GetFontProgram(font));

        Array.Fill(first, (byte)0);

        Assert.Equal(expected, Standard14Substitutes.GetFontProgram(font));
    }

    [Fact]
    public void LiberationSerifRegular_inPdfA2u_passesVeraPdf_andTheTextReadsBack()
    {
        using var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        var serif = Embed(doc, Standard14.TimesRoman);
        doc.Add(new Paragraph("Embedded serif text", new TextStyle { FontRef = serif, FontSize = 12 }));
        var pdf = FontReportSupport.Save(doc);

        // The fixture holds the change: an embedded Liberation Serif program, not a base-14 name.
        var text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert.Contains("/FontFile2", text, StringComparison.Ordinal);
        Assert.Matches(@"/BaseFont\s*/[A-Z]{6}\+LiberationSerif", text);

        using var dir = new FontReportSupport.TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("pdftotext", [path, "-"], out var exit, out var extracted, out var stderr, out var timedOut);
        Assert.False(timedOut, "pdftotext timed out.");
        Assert.True(exit == 0, stderr);
        Assert.Contains("Embedded serif text", extracted, StringComparison.Ordinal);

        Assert.Empty(FontReportSupport.FailedRules(FontReportSupport.VeraPdf("2u", pdf)));
    }
}
