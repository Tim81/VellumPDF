// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.TestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// veraPDF's verdict on the real tokens of <see cref="RealTokenTests"/> and on a form field whose
/// name and value hold U+00A0. ISO 19005-2, 6.1.13 holds a PDF/A file to the 32-bit integer range
/// and to ±3.403 × 10^38 for a real. Each fixture is first checked for the token the change
/// writes, so the verdict is about that token and not about a document that happens to avoid it.
/// The assertions read the list of failed rules from veraPDF's report, not its exit code.
/// </summary>
public sealed class RealTokenVeraPdfTests
{
    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    private static string Inflated(byte[] pdf)
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

    /// <summary>
    /// Validates with veraPDF 2u and returns the failed rules as "clause-testNumber", sorted. Gated
    /// through <see cref="ExternalTool"/>, which skips locally and fails under REQUIRE_VERAPDF or CI.
    /// </summary>
    private static List<string> FailedRules(byte[] pdf)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vellum_realtoken_vera_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "doc.pdf");
            File.WriteAllBytes(path, pdf);
            ExternalTool.TryRun("verapdf", ["--flavour", "2u", path],
                out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);
            Assert.False(timedOut, "veraPDF timed out.");
            Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
            Assert.Contains("<validationReport", report);
            var failed = new List<string>();
            foreach (Match m in Regex.Matches(report, @"<rule\b[^>]*>"))
            {
                if (!m.Value.Contains("status=\"failed\"", StringComparison.Ordinal))
                    continue;
                var clause = Regex.Match(m.Value, "clause=\"([^\"]*)\"").Groups[1].Value;
                var test = Regex.Match(m.Value, "testNumber=\"([^\"]*)\"").Groups[1].Value;
                failed.Add($"{clause}-{test}");
            }
            failed.Sort(StringComparer.Ordinal);
            return failed;
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    public static TheoryData<double, string> Values => new()
    {
        { 2147483648d, "2147483648.0" },
        { -2147483649d, "-2147483649.0" },
        { 3.403e38, "340300000000000000000000000000000000000.0" },
        { 3e9, "3000000000.0" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void PdfA2u_link_destination_and_canvas_operands_pass_veraPdf(double value, string token)
    {
        using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2u };
        doc.Info.Title = "Real token";
        var page = doc.AddPage();
        doc.RegisterLinkAnnotation(page, new PdfLinkAnnotation
        {
            Rect = new PdfRectangle(10, 10, 50, 30),
            DestPage = page,
            DestLeft = value,
            DestTop = 100,
        });
        new PdfCanvas(page).MoveTo(value, 5).LineTo(10, 10).Stroke().Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        var pdf = ms.ToArray();

        // The fixture holds the token the change writes, in the link and in the content stream.
        Assert.Contains($"/XYZ {token} 100 null]", Text(pdf));
        Assert.Contains($"{token} 5 m", Inflated(pdf));

        Assert.Empty(FailedRules(pdf));
    }

    [Fact]
    public void PdfA2u_form_with_U_00A0_name_and_value_fails_the_same_rules_as_an_ASCII_one()
    {
        byte[] Build(string name, string value)
        {
            using var doc = new PdfDocument { Conformance = PdfConformance.PdfA2u };
            doc.Info.Title = "Form";
            var page = doc.AddPage();
            doc.AddTextField(page, name, new PdfRectangle(0, 0, 100, 20), value);
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        var nbsp = Build("a b", "v w");
        // The change under test: both strings are UTF-16BE with a mark, where 2.3.3 wrote 0xA0.
        byte[] name = [0xFE, 0xFF, 0x00, 0x61, 0x00, 0xA0, 0x00, 0x62];
        Assert.True(IndexOf(nbsp, [.. "/T ("u8.ToArray(), .. name, (byte)')']) >= 0, "/T is not the UTF-16BE string.");
        byte[] value = [0xFE, 0xFF, 0x00, 0x76, 0x00, 0xA0, 0x00, 0x77];
        Assert.True(IndexOf(nbsp, [.. "/V ("u8.ToArray(), .. value, (byte)')']) >= 0, "/V is not the UTF-16BE string.");

        var ascii = Build("a b", "v w");
        Assert.Equal(FailedRules(ascii), FailedRules(nbsp));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        return -1;
    }
}
