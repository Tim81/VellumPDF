// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #262, the control: a document that uses neither a page-label range nor an anchor writes neither
/// a <c>/PageLabels</c> tree nor a <c>/Names</c> dictionary, and the oracles agree. It uses no
/// member this change adds, so it also runs against a build that predates them.
/// </summary>
public sealed class NavigationControlTests
{
    private static byte[] Build()
    {
        using var doc = new Document { PageSize = Page, Margins = Margins };
        doc.Footer = new RunningBand("{page}/{pages}");
        doc.Add(new Paragraph("one", Body) { Margins = new EdgeInsets(200, 0, 0, 0) });
        doc.Add(new Paragraph("two", Body) { Margins = new EdgeInsets(200, 0, 0, 0) });
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void DocumentWithoutLabelsOrAnchors_writesNoPageLabelsAndNoNames()
    {
        var text = Encoding.Latin1.GetString(Build());

        Assert.DoesNotContain("/PageLabels", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Names", text, StringComparison.Ordinal);
        Assert.DoesNotContain("/Dests", text, StringComparison.Ordinal);
    }

    [Fact]
    public void DocumentWithoutLabelsOrAnchors_hasEmptyPageLabelsAndNoDestinationsForTheOracles()
    {
        var path = Path.Combine(Path.GetTempPath(), "vellum_navctl_" + Guid.NewGuid().ToString("N") + ".pdf");
        File.WriteAllBytes(path, Build());
        try
        {
            ExternalTool.TryRun("qpdf", ["--json", "--json-key=pagelabels", path], out var exit, out var json, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf exit {exit}\n{json}\n{stderr}");
            using var parsed = System.Text.Json.JsonDocument.Parse(json);
            Assert.Equal(0, parsed.RootElement.GetProperty("pagelabels").GetArrayLength());

            if (!ExternalTool.TryRun("pdfinfo", ["-dests", path], out exit, out var dests, out stderr, out timedOut))
                OracleGate.Unavailable("pdfinfo");
            Assert.False(timedOut);
            Assert.True(exit == 0, $"pdfinfo exit {exit}\n{dests}\n{stderr}");
            var lines = dests.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            Assert.Single(lines);
            Assert.StartsWith("Page", lines[0], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
