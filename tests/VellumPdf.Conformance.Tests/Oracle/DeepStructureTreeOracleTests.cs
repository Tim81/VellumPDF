// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;

namespace VellumPdf.Conformance.Tests.Oracle;

/// <summary>A 4,000-level structure tree, the deepest veraPDF reads, is accepted by it as a structure tree.</summary>
[Collection("veraPDF")]
public sealed class DeepStructureTreeOracleTests
{
    // veraPDF 1.30.4 walks the structure tree recursively on the JVM's default stack: it ended with a
    // StackOverflowError between 4,000 and 6,000 levels, so this is a depth it validates.
    private const int Depth = 4_000;

    [Fact]
    public void PdfUA1_chainOf4000Divs_endsNormally_andFailsNoStructureRule()
    {
        VeraPdf.EnsureAvailable();

        using var doc = new PdfDocument { Conformance = VellumPdf.Document.PdfConformance.PdfUA1, Tagged = true, Language = "en-US" };
        doc.Info.Title = "Deep structure tree";
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        var mcid = canvas.BeginMarkedContent("P");
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("deep").EndText();
        canvas.EndMarkedContent();
        canvas.Finish();

        var root = new PdfStructElem("Div");
        var current = root;
        for (var i = 1; i < Depth; i++)
            current = current.AddChild(new PdfStructElem("Div"));
        current.AddChild(new PdfStructElem("P") { Page = page, Mcid = mcid });
        doc.RegisterStructElem(root);

        using var ms = new MemoryStream();
        doc.Save(ms);

        var baseDir = !OperatingSystem.IsWindows() && Directory.Exists("/tmp") ? "/tmp" : Path.GetTempPath();
        var path = Path.Combine(baseDir, $"vellum-oracle-deep-tree-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, ms.ToArray());
        try
        {
            var report = VeraPdf.Report(path, "ua1");

            Assert.Contains("jobEndStatus=\"normal\"", report, StringComparison.Ordinal);
            var failedClauses = Regex.Matches(report, "<rule [^>]*clause=\"(?<c>[^\"]+)\"[^>]*status=\"failed\"")
                .Select(m => m.Groups["c"].Value)
                .Concat(Regex.Matches(report, "<rule [^>]*status=\"failed\"[^>]*clause=\"(?<c>[^\"]+)\"").Select(m => m.Groups["c"].Value))
                .ToList();
            // The chain has no embedded font, so clause 7.21 fails for that reason; clauses 7.1 and 7.2
            // are the structure-tree and logical-structure rules.
            Assert.NotEmpty(Regex.Matches(report, "<rule ", RegexOptions.CultureInvariant));
            Assert.DoesNotContain(failedClauses, c => c is "7.1" or "7.2");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
