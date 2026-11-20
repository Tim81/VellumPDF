// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Forms;
using VellumPdf.IO;
using VellumPdf.TestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// A whole number outside the 32-bit range is written as a real token (<c>2147483648.0</c>), not
/// as an integer token. ISO 32000-1, Table C.1 gives 2,147,483,647 as the largest integer (ISO 32000-2
/// says only that integers "can often be expressed within 32 bits"), and ISO 19005-2, 6.1.13 holds
/// a PDF/A file to that range; a whole value of 2^63 or more as an integer token is one a reader
/// cannot convert at all. The rule reads the written text, so a value that rounds to a whole number
/// is judged after rounding. Every expectation is the exact token as it sits in the file.
/// </summary>
public sealed class RealTokenTests
{
    public static TheoryData<double, string> Cases => new()
    {
        { 2147483647d, "2147483647" },
        { 2147483648d, "2147483648.0" },
        { -2147483648d, "-2147483648" },
        { -2147483649d, "-2147483649.0" },
        { 2147483647.000001, "2147483647" },
        { 2147483648.000001, "2147483648.0" },
        { 3e9, "3000000000.0" },
        { 1e15, "1000000000000000.0" },
        { 9.223372036854776E+18, "9223372036854780000.0" },
        { 3.403e38, "340300000000000000000000000000000000000.0" },
        { 0.5, "0.5" },
        { 3d, "3" },
        { -0.000001, "-0" },
    };

    private static byte[] Save(PdfDocument doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static string WriteReal(double value)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        new PdfReal(value).WriteTo(writer);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    /// <summary>Inflates every Flate stream in the file and joins the results.</summary>
    private static string InflatedStreams(byte[] pdf)
    {
        var sb = new StringBuilder();
        foreach (Match m in Regex.Matches(Text(pdf), @"stream\r?\n(?<d>.*?)\r?\nendstream", RegexOptions.Singleline))
        {
            var data = Encoding.Latin1.GetBytes(m.Groups["d"].Value);
            if (data.Length == 0)
                continue;
            try
            {
                using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
                using var o = new MemoryStream();
                z.CopyTo(o);
                sb.Append(Encoding.Latin1.GetString(o.ToArray())).Append('\n');
            }
            catch (InvalidDataException)
            {
                // Not a Flate stream.
            }
        }
        return sb.ToString();
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void PdfReal_writes_the_token(double value, string expected) =>
        Assert.Equal(expected, WriteReal(value));

    [Theory]
    [MemberData(nameof(Cases))]
    public void CanvasOperand_writes_the_token(double value, string expected)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        new PdfCanvas(page).MoveTo(value, 1).Finish();
        Assert.Contains($"{expected} 1 m", InflatedStreams(Save(doc)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void LinkDest_writes_the_token(double value, string expected)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(10, 10, 50, 30),
            DestPage = page,
            DestLeft = value,
            DestTop = 100,
        });
        Assert.Contains($"/XYZ {expected} 100 null]", Text(Save(doc)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void OutlineDest_writes_the_token(double value, string expected)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "T", DestPage = page, DestLeft = value, DestTop = 100 });
        Assert.Contains($"/XYZ {expected} 100 null]", Text(Save(doc)));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MediaBox_writes_the_token(double value, string expected)
    {
        using var doc = new PdfDocument();
        doc.AddPage(new PdfRectangle(0, 0, value, 100));
        Assert.Contains($"/MediaBox [0 0 {expected} 100]", Text(Save(doc)));
    }

    [Fact]
    public void FormField_wide_rect_writes_real_tokens_in_rect_bbox_and_appearance()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddTextField(page, "F", new PdfRectangle(0, 0, 3e9, 20), "v");
        var pdf = Save(doc);
        var text = Text(pdf);
        Assert.Contains("/Rect [0 0 3000000000.0 20]", text);
        Assert.Contains("/BBox [0 0 3000000000.0 20]", text);
        Assert.Contains("0 0 3000000000.0 20 re f", InflatedStreams(pdf));
        Assert.Contains("0 0 3000000000.0 20 re S", InflatedStreams(pdf));
    }

    [Fact]
    public void FormField_tall_rect_writes_a_real_token_in_the_baseline()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddTextField(page, "F", new PdfRectangle(0, 0, 100, 1e10), "v");
        Assert.Contains("2 4999999994.0 Td", InflatedStreams(Save(doc)));
    }

    [Fact]
    public void FormField_huge_font_size_writes_real_tokens_in_DA_and_appearance()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddTextField(page, "F", new PdfRectangle(0, 0, 100, 20), "v", new FormFieldOptions { FontSize = 3e9 });
        var pdf = Save(doc);
        Assert.Contains("(/Helv 3000000000.0 Tf 0 g)", Text(pdf));
        Assert.Contains("/Helv 3000000000.0 Tf", InflatedStreams(pdf));
    }

    // Each appearance-stream number site is covered on its own: a site left on the plain format
    // changes only the one line asserted here.

    [Fact]
    public void PushButton_wide_rect_writes_real_tokens_in_both_rectangles_and_the_baseline()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "B", new PdfRectangle(0, 0, 3e9, 6e9), "Go", new FormFieldOptions { FontSize = 10 });
        var streams = InflatedStreams(Save(doc));
        Assert.Contains("0 0 3000000000.0 6000000000.0 re f", streams);
        Assert.Contains("0.5 0.5 2999999999.0 5999999999.0 re S", streams);
        Assert.Contains("/Helv 10 Tf", streams);
        Assert.Contains("4 2999999995.0 Td", streams);
    }

    [Fact]
    public void PushButton_huge_font_size_writes_a_real_token_in_Tf()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "B", new PdfRectangle(0, 0, 100, 20), "Go", new FormFieldOptions { FontSize = 3e9 });
        Assert.Contains("/Helv 3000000000.0 Tf", InflatedStreams(Save(doc)));
    }

    [Fact]
    public void CheckBox_large_rect_writes_real_tokens_in_Td()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddCheckBox(page, "C", new PdfRectangle(0, 0, 1e10, 1e10), true, new FormFieldOptions { FontSize = 10 });
        var streams = InflatedStreams(Save(doc));
        Assert.Contains("/ZaDb 10 Tf", streams);
        Assert.Contains("4999999997.0 4999999995.0 Td", streams);
    }

    [Fact]
    public void CheckBox_huge_font_size_writes_a_real_token_in_Tf()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddCheckBox(page, "C", new PdfRectangle(0, 0, 20, 20), true, new FormFieldOptions { FontSize = 3e9 });
        Assert.Contains("/ZaDb 3000000000.0 Tf", InflatedStreams(Save(doc)));
    }

    [Fact]
    public void RadioGroup_large_rect_writes_real_tokens_in_Td()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddRadioButtonGroup("R", [new RadioOption(page, new PdfRectangle(0, 0, 1e10, 1e10), "A")], "A",
            new FormFieldOptions { FontSize = 10 });
        var streams = InflatedStreams(Save(doc));
        Assert.Contains("/ZaDb 10 Tf", streams);
        Assert.Contains("4999999997.0 4999999995.0 Td", streams);
    }

    [Fact]
    public void RadioGroup_huge_font_size_writes_a_real_token_in_Tf()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddRadioButtonGroup("R", [new RadioOption(page, new PdfRectangle(0, 0, 20, 20), "A")], "A",
            new FormFieldOptions { FontSize = 3e9 });
        Assert.Contains("/ZaDb 3000000000.0 Tf", InflatedStreams(Save(doc)));
    }

    [Fact]
    public void FormField_ordinary_appearance_is_unchanged()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.AddPushButton(page, "B", new PdfRectangle(0, 0, 100, 20), "Go");
        var streams = InflatedStreams(Save(doc));
        Assert.Contains("0.5 0.5 99 19 re S", streams);
        Assert.Contains("0 0 100 20 re f", streams);
        Assert.Contains("4 4 Td", streams);
    }

    // ── Oracle: the real token is valid for a reader ──────────────────────────

    [Theory]
    [InlineData(2147483648d)]
    [InlineData(3.403e38)]
    public void Qpdf_check_is_clean_and_reads_the_link_destination(double value)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(10, 10, 50, 30),
            DestPage = page,
            DestLeft = value,
            DestTop = 100,
        });
        new PdfCanvas(page).MoveTo(value, 1).LineTo(2, 2).Finish();
        var pdf = Save(doc);

        var dir = Path.Combine(Path.GetTempPath(), "vellum_realtoken_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "real.pdf");
            File.WriteAllBytes(path, pdf);
            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout);

            ExternalTool.TryRun("qpdf", ["--json=2", "--json-key=qpdf", path], out var jexit, out var json, out _, out _);
            Assert.Equal(0, jexit);
            var expected = value == 2147483648d ? "2147483648.0" : "340300000000000000000000000000000000000.0";
            Assert.Contains($"\"/XYZ\",{expected},100,null", Regex.Replace(json, @"\s+", ""));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
