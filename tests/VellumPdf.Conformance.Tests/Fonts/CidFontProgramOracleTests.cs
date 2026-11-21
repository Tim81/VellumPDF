// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Fonts.Sfnt;
using VellumPdf.TestSupport;

namespace VellumPdf.Conformance.Tests.Fonts;

// The enclosing VellumPdf.Conformance namespace has its own PdfConformance; this file means the
// document one.
using PdfConformance = VellumPdf.Document.PdfConformance;

/// <summary>
/// The external readers accept a glyf program that carries no <c>cmap</c> table (#632) and a
/// descriptor with the font's own <c>/FontBBox</c> (#596). Each document is a PDF/A-2u file whose
/// glyf program is a CIDFontType2 descendant's; the fixture check comes first, so a verdict is
/// never taken on a file that still has the table.
///
/// The text, its extraction and its rendering are checked by value: pdftotext returns the string
/// that was drawn, and the 150 dpi render of page 1 has ink where the glyphs are and nowhere
/// else. A blank page, or one drawn with the wrong glyphs, fails there.
/// </summary>
[Collection("veraPDF")]
public sealed class CidFontProgramOracleTests : IDisposable
{
    private const string Text = "Hello world";

    // Not CreateTempSubdirectory: CI runs veraPDF in a container as another user, which needs a
    // directory the runner created with ordinary permissions.
    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"vellum-cid-program-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static TheoryData<string> Fonts => ["DejaVu Sans", "Liberation Sans (Helvetica)"];

    private static byte[] Save(string font)
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2u, Language = "en-US" };
        doc.Info.Title = "cid font program";
        var page = doc.AddPage();
        var handle = font == "DejaVu Sans"
            ? doc.UseTrueTypeFont(CmapFormat12Tests.LoadAsset("DejaVuSans.ttf"))
            : doc.EmbedStandard14Font(Standard14.Helvetica);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[Text.Length];
        var count = handle.GetGlyphIds(Text, gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private string Write(string name, byte[] pdf)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    /// <summary>The embedded glyf program, found as the one inflated stream that is a TrueType
    /// font.</summary>
    private static byte[] GlyfProgram(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var pos = 0;
        while (true)
        {
            var s = text.IndexOf("stream\n", pos, StringComparison.Ordinal);
            Assert.True(s >= 0, "no embedded TrueType program in the file");
            s += "stream\n".Length;
            var e = text.IndexOf("\nendstream", s, StringComparison.Ordinal);
            pos = e + "\nendstream".Length;
            byte[] body;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, s, e - s), CompressionMode.Decompress);
                using var inflated = new MemoryStream();
                z.CopyTo(inflated);
                body = inflated.ToArray();
            }
            catch (InvalidDataException)
            {
                continue;
            }
            if (body.Length > 12 && body[0] == 0 && body[1] == 1 && body[2] == 0 && body[3] == 0) return body;
        }
    }

    private static string[] Tags(byte[] sfnt) =>
        [.. SfntFont.Parse(sfnt).Tables.Keys.Select(t => t.ToString()).OrderBy(t => t, StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Program_hasNoCmapTable_andVeraPdfAccepts2u(string font)
    {
        VeraPdf.EnsureAvailable();
        var pdf = Save(font);
        Assert.DoesNotContain("cmap", Tags(GlyfProgram(pdf)));      // fixture check

        var report = VeraPdf.Report(Write("a2u.pdf", pdf), "2u");

        AssertCompliant(report);
    }

    /// <summary>The tagged build, validated against PDF/UA-1.</summary>
    [Fact]
    public void TaggedLayoutDocument_program_hasNoCmapTable_andVeraPdfAcceptsUa1()
    {
        VeraPdf.EnsureAvailable();
        using var doc = new VellumPdf.Layout.Document { Conformance = PdfConformance.PdfUA1, Language = "en-US" };
        doc.Info.Title = "cid font program";
        var font = doc.UseTrueTypeFont(CmapFormat12Tests.LoadAsset("DejaVuSans.ttf"));
        doc.Add(new VellumPdf.Layout.Elements.Paragraph(
            Text, new VellumPdf.Layout.Core.TextStyle { FontRef = font, FontSize = 12 }));
        using var ms = new MemoryStream();
        doc.Save(ms);
        var pdf = ms.ToArray();
        Assert.DoesNotContain("cmap", Tags(GlyfProgram(pdf)));      // fixture check

        var report = VeraPdf.Report(Write("ua1.pdf", pdf), "ua1");

        AssertCompliant(report);
    }

    /// <summary> The report says the job ran, the file is compliant, no rule failed, and a real
    /// number of rules passed, so an empty or truncated report cannot pass.
    /// </summary>
    private static void AssertCompliant(string report)
    {
        Assert.Contains("jobEndStatus=\"normal\"", report, StringComparison.Ordinal);
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        Assert.Empty(Regex.Matches(report, "<rule [^>]*status=\"failed\""));
        var details = Regex.Match(report, @"<details passedRules=""(?<p>\d+)"" failedRules=""(?<f>\d+)""");
        Assert.True(details.Success, "no <details> element in the veraPDF report");
        Assert.Equal("0", details.Groups["f"].Value);
        Assert.True(int.Parse(details.Groups["p"].Value) >= 100, "veraPDF checked fewer rules than any profile holds");
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Qpdf_checksTheFileClean(string font)
    {
        var pdf = Save(font);
        Assert.DoesNotContain("cmap", Tags(GlyfProgram(pdf)));      // fixture check

        ExternalTool.TryRun("qpdf", ["--check", Write("q.pdf", pdf)], out var exit, out var stdout, out var stderr, out var timedOut);

        Assert.False(timedOut, "qpdf --check timed out, or its output could not be fully captured.");
        Assert.Equal(0, exit);
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", stdout + stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("ERROR", stdout + stderr, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Fonts))]
    public void Pdftotext_returnsTheDrawnText(string font)
    {
        var pdf = Save(font);
        Assert.DoesNotContain("cmap", Tags(GlyfProgram(pdf)));      // fixture check

        ExternalTool.TryRun("pdftotext", ["-enc", "UTF-8", Write("t.pdf", pdf), "-"], out var exit, out var text, out var stderr,
            out var timedOut, outputEncoding: Encoding.UTF8);

        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        Assert.Equal(Text, text.Trim());
    }

    /// <summary> The render goes through the program's glyph data and the CIDToGIDMap, not through
    /// a cmap the program no longer has: the page has ink, and it is in the rows and columns the
    /// text occupies. The text starts at x = 72 pt and its baseline is 720 pt up an A4 page, which
    /// at 150 dpi is column 150 and row 254 (841.89 pt tall).
    /// </summary>
    [Theory]
    [MemberData(nameof(Fonts))]
    public void Pdftoppm_drawsInkWhereTheTextIs(string font)
    {
        var pdf = Save(font);
        Assert.DoesNotContain("cmap", Tags(GlyfProgram(pdf)));      // fixture check
        var outBase = Path.Combine(_dir, "render");

        ExternalTool.TryRun("pdftoppm", ["-r", "150", "-f", "1", "-l", "1", "-singlefile", Write("p.pdf", pdf), outBase],
            out var exit, out _, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);

        var ppm = File.ReadAllBytes(outBase + ".ppm");
        var (width, height, pixels) = ParsePpm(ppm);
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1, ink = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var o = pixels + (y * width + x) * 3;
                if ((ppm[o] + ppm[o + 1] + ppm[o + 2]) / 3 >= 128) continue;
                ink++;
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }

        Assert.Equal((1241, 1754), (width, height));
        // Measured at poppler 26.08.0 and allowed a few pixels either way for another version's
        // anti-aliasing: the first letter starts at column 152 and the string ends at column 287
        // (DejaVu Sans) or 271 (Liberation Sans); ink spans rows 234 to 252.
        var (inkRange, lastColumn) = font == "DejaVu Sans" ? ((540, 1000), 287) : ((460, 860), 271);
        Assert.InRange(ink, inkRange.Item1, inkRange.Item2);
        Assert.InRange(minX, 147, 157);
        Assert.InRange(maxX, lastColumn - 6, lastColumn + 6);
        Assert.InRange(minY, 229, 240);
        Assert.InRange(maxY, 247, 257);
    }

    private static (int Width, int Height, int PixelOffset) ParsePpm(byte[] ppm)
    {
        // "P6\n<width> <height>\n255\n" then width * height * 3 bytes.
        var header = Encoding.ASCII.GetString(ppm, 0, Math.Min(32, ppm.Length));
        var m = Regex.Match(header, @"^P6\s+(\d+)\s+(\d+)\s+255\s");
        Assert.True(m.Success, "not a binary PPM");
        return (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Length);
    }
}
