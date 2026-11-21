// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;
using static VellumPdf.Layout.Tests.PreformattedSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>A tagged document draws each piece of a preformatted block as one P structure element, with the continuation markers outside it as artifacts (#591).</summary>
public sealed class PreformattedTaggedTests
{
    private static string Digits(int n) => string.Concat(Enumerable.Range(0, n).Select(i => (char)('0' + i % 10)));

    private static string Everything(byte[] pdf) =>
        Encoding.Latin1.GetString(pdf) + "\n" + PdfTestUtil.DecompressAllFlatStreams(pdf);

    private static List<string> StructureTypes(byte[] pdf) =>
        [.. Regex.Matches(Everything(pdf), @"/S /(\w+)").Select(m => m.Groups[1].Value)];

    [Fact]
    public void EachPiece_isOneParagraphElementUnderDocument_withItsOwnMcid()
    {
        using var doc = new Document { Tagged = true, Language = "en-US" };
        doc.Info.Title = "t";
        doc.Add(new Preformatted(string.Join('\n', Enumerable.Range(1, 150).Select(i => $"line {i}")), Courier()));

        var pdf = Save(doc);
        var pages = PageContents(pdf);

        Assert.Equal(3, pages.Count);
        Assert.All(pages, p => Assert.StartsWith("/P <</MCID 0>> BDC\nBT\n", p, StringComparison.Ordinal));
        Assert.All(pages, p => Assert.EndsWith("ET\nEMC\n", p, StringComparison.Ordinal));
        Assert.Equal(["Document", "P", "P", "P"], StructureTypes(pdf).Where(t => t is "Document" or "P" or "Code"));
    }

    [Fact]
    public void ABlockOfWrappedLines_keepsItsMarkersOutsideTheParagraphElement()
    {
        using var doc = new Document { Tagged = true, Language = "en-US" };
        doc.Info.Title = "t";
        doc.Add(new Preformatted(Digits(76) + "\n" + Digits(150), Courier()));

        var content = Assert.Single(PageContents(Save(doc)));

        var emc = content.IndexOf("ET\nEMC\n", StringComparison.Ordinal);
        Assert.True(emc > 0);
        Assert.Single(Regex.Matches(content, @"^/P <</MCID 0>> BDC$", RegexOptions.Multiline));
        // Three markers (one for the first line, two for the second), each wrapped in an artifact
        // sequence that follows the paragraph's EMC.
        Assert.Equal(3, Regex.Matches(content, @"^/Artifact BMC\nq$", RegexOptions.Multiline).Count);
        Assert.Equal(3, Regex.Matches(content[emc..], @"^Q\nEMC$", RegexOptions.Multiline).Count);
        Assert.DoesNotContain("/Artifact", content[..emc], StringComparison.Ordinal);
    }

    /// <summary>Setting the structure type to Code changes the type of each element and nothing else in the file.</summary>
    [Fact]
    public void TheStructureType_isTheOnlyThingACodeTagChanges()
    {
        byte[] Build(string structType)
        {
            var pdf = NewPdf();
            pdf.Tagged = true;
            pdf.Language = "en-US";
            pdf.Info.Title = "t";
            var renderer = new DocumentRenderer(pdf, new PdfRectangle(0, 0, 595.28, 841.89), new EdgeInsets(72));
            renderer.Add(new PreformattedRenderer(new Preformatted(Digits(76) + "\n" + Digits(60), Courier())) { StructType = structType });
            return Render(renderer);
        }

        var paragraph = Build("P");
        var code = Build("Code");

        Assert.Contains("/Code <</MCID 0>> BDC", PdfTestUtil.DecompressAllFlatStreams(code), StringComparison.Ordinal);
        Assert.DoesNotContain("/Code", Everything(paragraph), StringComparison.Ordinal);
        // The stream's own length moves with the longer name, so lengths are blanked in both.
        static string Blanked(string normalised) => Regex.Replace(normalised, @"/Length \d+", "/Length #");
        Assert.Equal(Blanked(Normalise(code)), Blanked(Regex.Replace(Normalise(paragraph), @"/S /P\n|^/P <</MCID", m => m.Value.Replace("/P", "/Code", StringComparison.Ordinal), RegexOptions.Multiline)));
    }
}
