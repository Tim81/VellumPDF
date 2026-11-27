// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A PDF/A-2u document holding one image of each format the factories read, loaded through them,
/// validated by veraPDF against the same document built with the explicit loaders.
/// </summary>
public sealed class LayoutImageFactoryOracleTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"vellum_imgfactory_oracle_{Guid.NewGuid():N}");

    public LayoutImageFactoryOracleTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort cleanup */ }
        catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
    }

    private static readonly byte[][] Files =
    [
        ImageFixtures.Png(),
        ImageFixtures.Jpeg(),
        ImageFixtures.TiffGrey(true, 8),
        ImageFixtures.Bmp(),
        ImageFixtures.Gif(),
        ImageFixtures.Jbig2(),
        ImageFixtures.Jp2(),
        ImageFixtures.JpxCodestream(),
    ];

    private static byte[] Build(Func<int, LayoutImage> image)
    {
        using var doc = new Document
        {
            Conformance = PdfConformance.PdfA2u,
            Timestamp = new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero),
            DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
        };
        doc.Info.Title = "Images loaded by signature";
        doc.Info.Producer = "VellumPdf";
        for (var i = 0; i < Files.Length; i++)
            doc.Add(image(i));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static LayoutImage Explicit(int i) => i switch
    {
        0 => Sized(PngImageLoader.Load(Files[i])),
        1 => Sized(JpegImageLoader.Load(Files[i])),
        2 => Sized(TiffImageLoader.Load(Files[i])),
        3 => Sized(BmpImageLoader.Load(Files[i])),
        4 => Sized(GifImageLoader.Load(Files[i])),
        5 => Sized(Jbig2ImageLoader.Load(Files[i])),
        _ => Sized(JpxImageLoader.Load(Files[i])),
    };

    private static LayoutImage Sized(PdfImageXObject x) => new(x);

    private static LayoutImage Factory(int i) => LayoutImage.FromBytes(Files[i]);

    [Fact]
    public void TheDocument_HoldsEveryFormatsFilter_AndTheTwoPathsSaveTheSameBytes()
    {
        var viaFactory = Build(Factory);
        var viaLoaders = Build(Explicit);

        var text = Encoding.Latin1.GetString(viaFactory);
        // The fixture check: without these the veraPDF verdict below would say nothing about the
        // factories.
        Assert.Equal(Files.Length, CountOf(text, "/Subtype /Image"));
        Assert.Contains("/DCTDecode", text, StringComparison.Ordinal);
        Assert.Contains("/JBIG2Decode", text, StringComparison.Ordinal);
        Assert.Contains("/JPXDecode", text, StringComparison.Ordinal);
        Assert.Equal(viaLoaders, viaFactory);
    }

    private static int CountOf(string text, string needle)
    {
        var count = 0;
        for (var at = text.IndexOf(needle, StringComparison.Ordinal); at >= 0;
             at = text.IndexOf(needle, at + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void PdfA2u_veraPdf_AcceptsTheDocumentBuiltThroughTheFactories()
    {
        var path = Path.Combine(_dir, "factories.pdf");
        File.WriteAllBytes(path, Build(Factory));

        AssertVeraPdfCompliant(path);
    }

    [Fact]
    public void PdfA2u_veraPdf_AcceptsTheDocumentBuiltThroughTheExplicitLoaders()
    {
        var path = Path.Combine(_dir, "loaders.pdf");
        File.WriteAllBytes(path, Build(Explicit));

        AssertVeraPdfCompliant(path);
    }

    private static void AssertVeraPdfCompliant(string pdfPath)
    {
        ExternalTool.TryRun("verapdf", ["--flavour", "2u", pdfPath],
            out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);

        Assert.False(timedOut, $"veraPDF timed out on {pdfPath}.\n{report}\n{stderr}");
        Assert.True(exit is 0 or 1,
            $"veraPDF exited {exit} for {pdfPath}; {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
        Assert.True(report.Contains("isCompliant=\"true\"", StringComparison.Ordinal),
            $"veraPDF PDF/A-2u validation failed (exit {exit}).\n{report}\n{stderr}");
    }
}
