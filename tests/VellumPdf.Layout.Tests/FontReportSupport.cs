// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Shared by the tests of the glyph-0 and unembedded-font reports (#592, #593): documents with
/// pinned identity, the in-tree fonts, and readers for the two things those tests assert against,
/// the content stream's text operators and the ToUnicode map's entries.
/// </summary>
internal static class FontReportSupport
{
    internal static readonly string FontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

    internal static byte[] DejaVuBytes => File.ReadAllBytes(Path.Combine(FontsDir, "DejaVuSans.ttf"));
    internal static byte[] LiberationMonoBytes => File.ReadAllBytes(Path.Combine(FontsDir, "LiberationMono-Regular.ttf"));

    internal static Document NewDocument(PdfConformance conformance)
    {
        var doc = new Document
        {
            Conformance = conformance,
            Timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        if (conformance is PdfConformance.PdfA2u or PdfConformance.PdfA2b or PdfConformance.PdfA2a)
            doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");
        if (conformance is PdfConformance.PdfUA1)
        {
            doc.Info.Title = "Font report";
            doc.Language = "en-US";
        }
        return doc;
    }

    internal static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>The text-showing lines of the page content, one per operator.</summary>
    internal static List<string> ShowLines(byte[] pdf) =>
        [.. Lines(pdf).Where(l => l.EndsWith(" Tj", StringComparison.Ordinal))];

    /// <summary>The font-selection lines of the page content, one per operator.</summary>
    internal static List<string> TfLines(byte[] pdf) =>
        [.. Lines(pdf).Where(l => l.EndsWith(" Tf", StringComparison.Ordinal))];

    /// <summary>The <c>beginbfchar</c> headers and the entries of every ToUnicode map, in file
    /// order.</summary>
    internal static List<string> BfcharLines(byte[] pdf) =>
        [.. Lines(pdf).Where(l => Regex.IsMatch(l, @"^(\d+ beginbfchar|<[0-9A-F]{4}> <[0-9A-F]{4,8}>)$") && l != "<0000> <FFFF>")];

    private static IEnumerable<string> Lines(byte[] pdf) =>
        PdfTestUtil.DecompressAllFlatStreams(pdf).Split('\n').Select(l => l.TrimEnd('\r'));

    /// <summary>The failed rules of a veraPDF report as <c>clause-testNumber</c>, in report
    /// order.</summary>
    internal static List<string> FailedRules(string report) =>
        [.. Regex.Matches(report, "<rule specification=\"[^\"]+\" clause=\"([^\"]+)\" testNumber=\"(\\d+)\" status=\"failed\"")
            .Select(m => $"{m.Groups[1].Value}-{m.Groups[2].Value}")];

    /// <summary>Runs veraPDF on <paramref name="pdf"/> and returns its report.</summary>
    internal static string VeraPdf(string flavour, byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("verapdf", ["--flavour", flavour, path],
            out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);
        Assert.False(timedOut, $"veraPDF timed out.\nstderr:\n{stderr}");
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
        return report;
    }

    internal sealed class TempDir : IDisposable
    {
        // Not CreateTempSubdirectory (mode 0700): the Docker veraPDF on CI runs as another user.
        private readonly string _root = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vellum-fontreport-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>Draws glyph ids it is given with <c>ShowGlyphs</c>, as a renderer that does not go
    /// through a style does.</summary>
    internal sealed class GlyphIdRenderer(Fonts.EmbeddedFontHandle font, ushort[] ids) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(20));

        public void Draw(DrawContext ctx)
        {
            var name = ctx.UseEmbeddedFont(font);
            ctx.Canvas.BeginText().SetFontByName(name, 12).SetTextMatrix(1, 0, 0, 1, 72, 600);
            ctx.Canvas.ShowGlyphs(ids);
            ctx.Canvas.EndText();
        }
    }
}
