// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using VellumPdf.Cli;
using VellumPdf.Conformance;

namespace VellumPdf.Cli.Tests;

// ── PDF helpers ───────────────────────────────────────────────────────────────

file sealed record PdfObj(string Dict, byte[]? Stream = null);

file static class PdfBuilder
{
    // Assembles a minimal classic-xref PDF. The catalog is object 1, pages object 2.
    // An optional XMP /Metadata stream is injected into the catalog when metadataBytes != null.
    internal static byte[] AssemblePdf(byte[]? metadataBytes = null, string version = "1.7")
    {
        var objs = new List<PdfObj>
        {
            new("<< /Type /Catalog /Pages 2 0 R >>"),
            new("<< /Type /Pages /Kids [] /Count 0 >>"),
        };

        if (metadataBytes is not null)
        {
            var metaN = objs.Count + 1;
            objs.Add(new PdfObj("/Type /Metadata /Subtype /XML", metadataBytes));
            var dict0 = objs[0].Dict;
            var insertAt = dict0.LastIndexOf(">>", StringComparison.Ordinal);
            if (insertAt >= 0)
                dict0 = string.Concat(dict0[..insertAt], $"/Metadata {metaN} 0 R ", dict0[insertAt..]);
            objs[0] = objs[0] with { Dict = dict0 };
        }

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W($"%PDF-{version}\n");
        ms.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);

        var offsets = new int[objs.Count + 1];
        for (var i = 0; i < objs.Count; i++)
        {
            offsets[i + 1] = (int)ms.Position;
            var n = i + 1;
            if (objs[i].Stream is { } body)
            {
                W($"{n} 0 obj\n<< {objs[i].Dict} /Length {body.Length} >>\nstream\n");
                ms.Write(body);
                W("\nendstream\nendobj\n");
            }
            else
            {
                W($"{n} 0 obj\n{objs[i].Dict}\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        var size = objs.Count + 1;
        W($"xref\n0 {size}\n");
        W($"{0:D10} 65535 f \n");
        for (var i = 1; i <= objs.Count; i++)
            W($"{offsets[i]:D10} 00000 n \n");
        W($"trailer\n<< /Size {size} /Root 1 0 R " +
          "/ID [<00112233445566778899AABBCCDDEEFF> <00112233445566778899AABBCCDDEEFF>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    internal static byte[] PdfAXmp(string part, string conformance)
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfaid=\"http://www.aiim.org/pdfa/ns/id/\">"
            + $"<pdfaid:part>{part}</pdfaid:part>"
            + $"<pdfaid:conformance>{conformance}</pdfaid:conformance>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }

    internal static byte[] PdfUaXmp()
    {
        var xmp =
            "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF "
            + "xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" "
            + "xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">"
            + "<pdfuaid:part>1</pdfuaid:part>"
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
        return Encoding.UTF8.GetBytes(xmp);
    }
}

// ── Arg parsing tests ─────────────────────────────────────────────────────────

public sealed class ArgParserTests
{
    [Fact]
    public void Help_Flag_Sets_Help()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--help"], p);
        Assert.Null(err);
        Assert.True(p.Help);
    }

    [Fact]
    public void Short_Help_Flag_Sets_Help()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["-h"], p);
        Assert.Null(err);
        Assert.True(p.Help);
    }

    [Fact]
    public void Version_Flag_Sets_Version()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--version"], p);
        Assert.Null(err);
        Assert.True(p.Version);
    }

    [Fact]
    public void List_Profiles_Flag()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--list-profiles"], p);
        Assert.Null(err);
        Assert.True(p.ListProfiles);
    }

    [Fact]
    public void Coverage_Flag_No_Profile()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--coverage"], p);
        Assert.Null(err);
        Assert.True(p.Coverage);
        Assert.Null(p.CoverageProfile);
    }

    [Fact]
    public void Coverage_Flag_With_Profile()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--coverage", "2b"], p);
        Assert.Null(err);
        Assert.True(p.Coverage);
        Assert.Equal(PdfConformance.PdfA2B, p.CoverageProfile);
    }

    [Fact]
    public void Profile_2b_Parsed()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-p", "2b"], p);
        Assert.Null(err);
        Assert.Equal([PdfConformance.PdfA2B], p.Profiles);
        Assert.False(p.ProfileAuto);
    }

    [Fact]
    public void Profile_All_Sets_All()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-p", "all"], p);
        Assert.Null(err);
        Assert.True(p.ProfileAll);
    }

    [Fact]
    public void Profile_Auto_Sets_Auto()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-p", "auto"], p);
        Assert.Null(err);
        Assert.True(p.ProfileAuto);
    }

    [Fact]
    public void Profile_Comma_List()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-p", "2b,2u"], p);
        Assert.Null(err);
        Assert.Equal([PdfConformance.PdfA2B, PdfConformance.PdfA2U], p.Profiles);
    }

    [Fact]
    public void Default_Profile_Is_Auto()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf"], p);
        Assert.Null(err);
        Assert.True(p.ProfileAuto);
        Assert.False(p.ProfileAll);
        Assert.Empty(p.Profiles);
    }

    [Fact]
    public void Format_Json()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-f", "json"], p);
        Assert.Null(err);
        Assert.Equal(OutputFormat.Json, p.Format);
    }

    [Fact]
    public void Format_Sarif()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "--format", "sarif"], p);
        Assert.Null(err);
        Assert.Equal(OutputFormat.Sarif, p.Format);
    }

    [Fact]
    public void Unknown_Option_Returns_Error()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--bogus"], p);
        Assert.NotNull(err);
        Assert.Contains("bogus", err);
    }

    [Fact]
    public void Unknown_Profile_Returns_Error()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-p", "bogus"], p);
        Assert.NotNull(err);
        Assert.Contains("bogus", err);
    }

    [Fact]
    public void Recurse_Flag()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["-r", "dir"], p);
        Assert.Null(err);
        Assert.True(p.Recurse);
    }

    [Fact]
    public void NoColor_Flag()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "--no-color"], p);
        Assert.Null(err);
        Assert.True(p.NoColor);
    }

    [Fact]
    public void Severity_Warning()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "--severity", "warning"], p);
        Assert.Null(err);
        Assert.Equal(SeverityLevel.Warning, p.Severity);
    }

    [Fact]
    public void FailOn_None()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "--fail-on", "none"], p);
        Assert.Null(err);
        // "none" sentinel = (SeverityLevel)(-1)
        Assert.Equal((SeverityLevel)(-1), p.FailOn);
    }

    [Fact]
    public void Quiet_Flag()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["-q", "file.pdf"], p);
        Assert.Null(err);
        Assert.True(p.Quiet);
    }

    [Fact]
    public void Verbose_Flag()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["-v", "file.pdf"], p);
        Assert.Null(err);
        Assert.True(p.Verbose);
    }

    [Fact]
    public void Output_Option()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["file.pdf", "-o", "out.json"], p);
        Assert.Null(err);
        Assert.Equal("out.json", p.OutputPath);
    }

    [Fact]
    public void DoubleDash_Separator()
    {
        var p = new ParsedArgs();
        var err = ArgParser.TryParse(["--", "-weird-name.pdf"], p);
        Assert.Null(err);
        Assert.Equal(["-weird-name.pdf"], p.Inputs);
    }
}

// ── Exit code tests ───────────────────────────────────────────────────────────

public sealed class ExitCodeTests
{
    private static (int Code, string Out, string Err) Run(string[] args, Stream? stdin = null)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = PreflightRunner.Run(args, stdout, stderr, stdin);
        return (code, stdout.ToString(), stderr.ToString());
    }

    [Fact]
    public void Help_Returns_0()
    {
        var (code, _, _) = Run(["--help"]);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Version_Returns_0()
    {
        var (code, _, _) = Run(["--version"]);
        Assert.Equal(0, code);
    }

    [Fact]
    public void List_Profiles_Returns_0()
    {
        var (code, _, _) = Run(["--list-profiles"]);
        Assert.Equal(0, code);
    }

    [Fact]
    public void Coverage_Returns_0()
    {
        var (code, _, _) = Run(["--coverage", "2b"]);
        Assert.Equal(0, code);
    }

    [Fact]
    public void No_Inputs_Returns_2()
    {
        var (code, _, _) = Run([]);
        Assert.Equal(2, code);
    }

    [Fact]
    public void Missing_File_Returns_2()
    {
        var (code, _, err) = Run(["nonexistent-12345.pdf"]);
        Assert.Equal(2, code);
        Assert.Contains("nonexistent", err);
    }

    [Fact]
    public void Auto_No_Claim_Returns_2()
    {
        // PDF with no XMP metadata → auto profile → exit 2
        var bytes = PdfBuilder.AssemblePdf(metadataBytes: null);
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var (code, _, err) = Run([tmp, "-p", "auto"]);
            Assert.Equal(2, code);
            Assert.Contains("no PDF/A or PDF/UA conformance claim", err);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Conformant_Pdf_With_Explicit_Profile_Returns_0_Or_1()
    {
        // A PDF/A-2b claiming PDF is not necessarily conformant (rules are run),
        // but it must not return exit 2.
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var (code, _, _) = Run([tmp, "-p", "2b"]);
            Assert.True(code == 0 || code == 1);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Non_Pdf_Returns_2()
    {
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tmp, "not a pdf"u8.ToArray());
            var (code, _, _) = Run([tmp, "-p", "2b"]);
            Assert.Equal(2, code);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Unknown_Option_Returns_2()
    {
        var (code, _, _) = Run(["--totally-bogus"]);
        Assert.Equal(2, code);
    }

    [Fact]
    public void MultiFile_Any_Error_Still_Provides_Exit_1_Or_0_When_All_Readable()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            // Two copies of the same file
            var (code, _, _) = Run([tmp, tmp, "-p", "2b"]);
            Assert.True(code == 0 || code == 1);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

// ── Text output tests ─────────────────────────────────────────────────────────

public sealed class TextOutputTests
{
    private static string RunText(string[] extraArgs, byte[]? pdfBytes = null)
    {
        var bytes = pdfBytes ?? PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, .. extraArgs], stdout, stderr, null);
            return stdout.ToString();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Text_Contains_Pass_Or_Fail()
    {
        var output = RunText(["-p", "2b", "--no-color"]);
        Assert.True(output.Contains("PASS") || output.Contains("FAIL"));
    }

    [Fact]
    public void Text_Contains_Profile_Name()
    {
        var output = RunText(["-p", "2b", "--no-color"]);
        Assert.Contains("PDF/A-2b", output);
    }

    [Fact]
    public void Text_Contains_Passed_Section()
    {
        var output = RunText(["-p", "2b", "--no-color"]);
        Assert.Contains("PASSED", output);
    }

    [Fact]
    public void Text_Contains_Not_Evaluated_Footer()
    {
        var output = RunText(["-p", "2b", "--no-color"]);
        Assert.Contains("NOT FULLY EVALUATED", output);
    }

    [Fact]
    public void Quiet_Suppresses_Output()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", "2b", "-q"], stdout, stderr, null);
            Assert.Equal("", stdout.ToString());
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

// ── JSON output tests ─────────────────────────────────────────────────────────

public sealed class JsonOutputTests
{
    private static string RunJson(byte[]? pdfBytes = null, string profile = "2b")
    {
        var bytes = pdfBytes ?? PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", profile, "-f", "json"], stdout, stderr, null);
            return stdout.ToString();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Json_Parses_As_Valid_Json()
    {
        var json = RunJson();
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public void Json_Has_Required_Fields()
    {
        var json = RunJson();
        var doc = JsonDocument.Parse(json).RootElement;
        Assert.True(doc.TryGetProperty("tool", out _));
        Assert.True(doc.TryGetProperty("toolVersion", out _));
        Assert.True(doc.TryGetProperty("file", out _));
        Assert.True(doc.TryGetProperty("profile", out _));
        Assert.True(doc.TryGetProperty("profileSource", out _));
        Assert.True(doc.TryGetProperty("conformant", out _));
        Assert.True(doc.TryGetProperty("summary", out _));
        Assert.True(doc.TryGetProperty("failed", out _));
        Assert.True(doc.TryGetProperty("passed", out _));
        Assert.True(doc.TryGetProperty("notEvaluated", out _));
    }

    [Fact]
    public void Json_Summary_Has_Numeric_Fields()
    {
        var json = RunJson();
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        Assert.True(summary.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Number);
        Assert.True(summary.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.Number);
        Assert.True(summary.TryGetProperty("total", out var t) && t.ValueKind == JsonValueKind.Number);
    }

    [Fact]
    public void Json_Tool_Is_Correct()
    {
        var json = RunJson();
        var tool = JsonDocument.Parse(json).RootElement.GetProperty("tool").GetString();
        Assert.Equal("vellum-preflight", tool);
    }

    [Fact]
    public void Json_MultiFile_Has_Results_Wrapper()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp1 = Path.GetTempFileName() + ".pdf";
        var tmp2 = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp1, bytes);
            File.WriteAllBytes(tmp2, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp1, tmp2, "-p", "2b", "-f", "json"], stdout, stderr, null);
            var json = stdout.ToString();
            var doc = JsonDocument.Parse(json).RootElement;
            Assert.True(doc.TryGetProperty("results", out var results));
            Assert.Equal(2, results.GetArrayLength());
        }
        finally
        {
            File.Delete(tmp1);
            File.Delete(tmp2);
        }
    }
}

// ── SARIF output tests ────────────────────────────────────────────────────────

public sealed class SarifOutputTests
{
    private static string RunSarif(byte[]? pdfBytes = null, string profile = "2b")
    {
        var bytes = pdfBytes ?? PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", profile, "-f", "sarif"], stdout, stderr, null);
            return stdout.ToString();
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Sarif_Parses_As_Valid_Json()
    {
        var json = RunSarif();
        var doc = JsonDocument.Parse(json);
        Assert.NotNull(doc);
    }

    [Fact]
    public void Sarif_Has_Version_2_1_0()
    {
        var json = RunSarif();
        var version = JsonDocument.Parse(json).RootElement.GetProperty("version").GetString();
        Assert.Equal("2.1.0", version);
    }

    [Fact]
    public void Sarif_Has_Runs_Array()
    {
        var json = RunSarif();
        var runs = JsonDocument.Parse(json).RootElement.GetProperty("runs");
        Assert.Equal(JsonValueKind.Array, runs.ValueKind);
        Assert.True(runs.GetArrayLength() > 0);
    }

    [Fact]
    public void Sarif_Has_Tool_Driver()
    {
        var json = RunSarif();
        var driver = JsonDocument.Parse(json).RootElement
            .GetProperty("runs")[0]
            .GetProperty("tool")
            .GetProperty("driver");
        Assert.Equal("vellum-preflight", driver.GetProperty("name").GetString());
    }

    [Fact]
    public void Sarif_Has_Schema_Property()
    {
        var json = RunSarif();
        Assert.True(JsonDocument.Parse(json).RootElement.TryGetProperty("$schema", out _));
    }
}

// ── RuleId → TestId mapping tests ────────────────────────────────────────────

public sealed class RuleIdMappingTests
{
    [Theory]
    [InlineData("ISO19005-2:6.2.2-1", "6.2.2-1")]
    [InlineData("ISO14289-1:7.20-2", "7.20-2")]
    [InlineData("ISO32000-1:7.7.2-catalog-type", "7.7.2-catalog-type")]
    [InlineData("ISO19005-2:6.6.4-pdfaid", "6.6.4-pdfaid")]
    [InlineData("6.1.2-1", "6.1.2-1")]
    public void StripPrefix_Works(string ruleId, string expected)
    {
        var actual = PreflightRunner.StripPrefix(ruleId);
        Assert.Equal(expected, actual);
    }
}

// ── Coverage / list-profiles / help / version exit-0 tests ───────────────────

public sealed class InfoCommandTests
{
    [Fact]
    public void Coverage_All_Exits_0_And_Has_Output()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = PreflightRunner.Run(["--coverage"], stdout, stderr, null);
        Assert.Equal(0, code);
        Assert.Contains("PDF/A-2b", stdout.ToString());
    }

    [Fact]
    public void Coverage_Specific_Exits_0()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = PreflightRunner.Run(["--coverage", "ua1"], stdout, stderr, null);
        Assert.Equal(0, code);
        Assert.Contains("PDF/UA-1", stdout.ToString());
    }

    [Fact]
    public void List_Profiles_Has_All_Four()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        PreflightRunner.Run(["--list-profiles"], stdout, stderr, null);
        var text = stdout.ToString();
        Assert.Contains("2b", text);
        Assert.Contains("2u", text);
        Assert.Contains("2a", text);
        Assert.Contains("ua1", text);
    }

    [Fact]
    public void Help_Shows_Synopsis()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        PreflightRunner.Run(["--help"], stdout, stderr, null);
        Assert.Contains("vellum-preflight", stdout.ToString());
    }

    [Fact]
    public void Version_Shows_Version_Number()
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        PreflightRunner.Run(["--version"], stdout, stderr, null);
        // Should contain something that looks like a version.
        Assert.Contains("vellum-preflight", stdout.ToString());
    }
}

// ── Passed / notEvaluated diff tests ─────────────────────────────────────────

public sealed class PassedNotEvaluatedTests
{
    [Fact]
    public void Json_Passed_Contains_Implemented_Checks()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", "2b", "-f", "json"], stdout, stderr, null);
            var json = stdout.ToString();
            var passed = JsonDocument.Parse(json).RootElement.GetProperty("passed");
            // There should be some passed checks (the minimal PDF satisfies many structural rules).
            // We just verify the array exists; whether it has entries depends on the PDF's conformance.
            Assert.Equal(JsonValueKind.Array, passed.ValueKind);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Json_NotEvaluated_Contains_Partial_Or_Deferred()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", "2b", "-f", "json"], stdout, stderr, null);
            var json = stdout.ToString();
            var notEval = JsonDocument.Parse(json).RootElement.GetProperty("notEvaluated");
            Assert.Equal(JsonValueKind.Array, notEval.ValueKind);
            // PDF/A-2b has some partial/deferred/OOS checks.
            Assert.True(notEval.GetArrayLength() > 0);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}

// ── Clause-based passed / not-mislabeled tests ───────────────────────────────

public sealed class ClausePassedHonestyTests
{
    // Validates that a check belonging to a clause that has a failing assertion (even when the
    // assertion's RuleId is DESCRIPTIVE and does not match any catalog TestId) is NOT reported
    // as passed. Regression for the bug where StripPrefix("ISO19005-2:6.6.4-pdfaid") produced
    // "6.6.4-pdfaid", which does not match catalog TestIds "6.6.4-1" … "6.6.4-7", so those
    // checks were wrongly listed as passed despite a real failure in clause 6.6.4.
    [Fact]
    public void Descriptive_RuleId_Failure_Does_Not_Appear_In_Passed()
    {
        // A PDF with no XMP metadata triggers XmpConformanceRule with
        // RuleId="ISO19005-2:6.6.4-pdfaid" and Clause="ISO 19005-2:2011, 6.6.4".
        // Under the old StripPrefix logic "6.6.4-pdfaid" matched no catalog TestId, so
        // 6.6.4-1 … 6.6.4-7 were falsely listed as passed. The new clause-based logic
        // correctly excludes them.
        var bytes = PdfBuilder.AssemblePdf(metadataBytes: null);
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            PreflightRunner.Run([tmp, "-p", "2b", "-f", "json"], stdout, stderr, null);
            var root = JsonDocument.Parse(stdout.ToString()).RootElement;

            // There must be at least one failing assertion (the XMP / no-metadata failure).
            var failedArr = root.GetProperty("failed");
            Assert.True(failedArr.GetArrayLength() > 0, "Expected at least one failed assertion.");

            // Collect the clause numbers of all failing assertions from the JSON output.
            var failingClauses = new HashSet<string>(StringComparer.Ordinal);
            foreach (var f in failedArr.EnumerateArray())
            {
                if (f.TryGetProperty("clause", out var clauseProp))
                {
                    var raw = clauseProp.GetString() ?? string.Empty;
                    // Mirror ParseClauseNumber: take the part after the last ", ".
                    var sep = raw.LastIndexOf(", ", StringComparison.Ordinal);
                    failingClauses.Add(sep >= 0 ? raw[(sep + 2)..].Trim() : raw.Trim());
                }
            }

            Assert.NotEmpty(failingClauses);

            // No check whose clause appears in failingClauses may be in the passed list.
            var passedArr = root.GetProperty("passed");
            foreach (var p in passedArr.EnumerateArray())
            {
                if (!p.TryGetProperty("clause", out var passedClauseProp))
                    continue;
                var passedClause = passedClauseProp.GetString() ?? string.Empty;
                Assert.DoesNotContain(passedClause, failingClauses);
            }
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Theory]
    [InlineData("ISO 19005-2:2011, 6.2.8", "6.2.8")]
    [InlineData("ISO 19005-2:2011, 6.6.4", "6.6.4")]
    [InlineData("ISO 32000-1:2008, 7.7.2", "7.7.2")]
    [InlineData("6.2.8", "6.2.8")]
    [InlineData("  6.2.4.3  ", "6.2.4.3")]
    public void ParseClauseNumber_Works(string input, string expected)
    {
        Assert.Equal(expected, PreflightRunner.ParseClauseNumber(input));
    }
}

// ── Auto profile tests ────────────────────────────────────────────────────────

public sealed class AutoProfileTests
{
    [Fact]
    public void Auto_With_PdfA2B_Claim_Runs_2B_Profile()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfAXmp("2", "B"));
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = PreflightRunner.Run([tmp, "-p", "auto", "-f", "json"], stdout, stderr, null);
            // Should run (not return 2) and produce a report for PDF/A-2b
            Assert.True(code == 0 || code == 1);
            var json = stdout.ToString();
            Assert.NotEmpty(json);
            var profile = JsonDocument.Parse(json).RootElement.GetProperty("profile").GetString();
            Assert.Equal("PDF/A-2b", profile);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Auto_With_PdfUA1_Claim_Runs_UA1_Profile()
    {
        var bytes = PdfBuilder.AssemblePdf(PdfBuilder.PdfUaXmp());
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = PreflightRunner.Run([tmp, "-p", "auto", "-f", "json"], stdout, stderr, null);
            Assert.True(code == 0 || code == 1);
            var json = stdout.ToString();
            var profile = JsonDocument.Parse(json).RootElement.GetProperty("profile").GetString();
            Assert.Equal("PDF/UA-1", profile);
        }
        finally
        {
            File.Delete(tmp);
        }
    }

    [Fact]
    public void Auto_No_Claim_Returns_Exit_2()
    {
        var bytes = PdfBuilder.AssemblePdf(metadataBytes: null);
        var tmp = Path.GetTempFileName() + ".pdf";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var stdout = new StringWriter();
            var stderr = new StringWriter();
            var code = PreflightRunner.Run([tmp], stdout, stderr, null);
            Assert.Equal(2, code);
            Assert.Contains("no PDF/A or PDF/UA conformance claim", stderr.ToString());
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
