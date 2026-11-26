// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <c>EmbedStandard14Font</c> on a Layout <see cref="Document"/>. Each face is checked against the
/// Liberation file it must stand for: the document hands back one handle for equal bytes, so
/// registering the file by hand and calling the extension must return the same instance.
/// </summary>
public sealed class Standard14EmbedTests
{
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

        Assert.Same(byFile, doc.EmbedStandard14Font(font));
    }

    [Fact]
    public void CalledTwice_returnsTheSameHandle_andOneSubsetIsEmbedded()
    {
        using var doc = new Document();
        var first = doc.EmbedStandard14Font(Standard14.TimesRoman);
        var second = doc.EmbedStandard14Font(Standard14.TimesRoman);
        Assert.Same(first, second);

        doc.Add(new Paragraph("Hello", new TextStyle { FontRef = first, FontSize = 12 }));
        var pdf = System.Text.Encoding.Latin1.GetString(FontReportSupport.Save(doc));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(pdf, "/FontFile2"));
    }

    [Fact]
    public void TwoFaces_areTwoHandles()
    {
        using var doc = new Document();
        Assert.NotSame(doc.EmbedStandard14Font(Standard14.TimesRoman), doc.EmbedStandard14Font(Standard14.TimesBold));
    }

    [Fact]
    public void ADocumentThatDoesNotCallIt_embedsNothing()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello"));
        var pdf = System.Text.Encoding.Latin1.GetString(FontReportSupport.Save(doc));
        Assert.DoesNotContain("/FontFile2", pdf, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Standard14.Symbol)]
    [InlineData(Standard14.ZapfDingbats)]
    [InlineData((Standard14)99)]
    [InlineData((Standard14)(-1))]
    public void AFaceWithNoSubstitute_isRefusedAtTheCall(Standard14 font)
    {
        using var doc = new Document();
        Assert.Throws<NotSupportedException>(() => doc.EmbedStandard14Font(font));
    }

    [Fact]
    public void ANullDocument_isRefused()
    {
        Document doc = null!;
        Assert.Throws<ArgumentNullException>(() => doc.EmbedStandard14Font(Standard14.Helvetica));
    }

    [Fact]
    public void LiberationSerifRegular_inPdfA2u_passesVeraPdf_andTheTextReadsBack()
    {
        using var doc = FontReportSupport.NewDocument(PdfConformance.PdfA2u);
        var serif = doc.EmbedStandard14Font(Standard14.TimesRoman);
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
