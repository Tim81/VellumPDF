// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;

namespace VellumPdf.Conformance.Tests.Fonts;

/// <summary>
/// The ToUnicode map of an embedded font is the same bytes on every operating system (#630): each
/// line ends with one line feed. These cases run on the two font programs the assembly carries, a
/// glyf one (DejaVu Sans) and a CFF one (Source Sans 3 ExtraLight), whose ToUnicode maps come from
/// the same method but travel in different font dictionaries.
/// </summary>
public sealed class ToUnicodeLineEndTests
{
    /// <summary>
    /// SHA-256 of the inflated ToUnicode stream for the text "Hello" in DejaVu Sans. It is the
    /// digest of the map Linux wrote before this change, where the platform line end is a line
    /// feed, so a Windows build that matches it writes the Linux bytes.
    /// </summary>
    private const string DejaVuHelloToUnicodeSha256 = "D0418C7FAA9C128732D2A641BB83107FF7104A194A1A2ED4C5A60532BC5A29C1";

    [Theory]
    [InlineData("DejaVuSans.ttf", 18, 380)]
    [InlineData("SourceSans3-ExtraLight.otf", 18, 380)]
    public void ToUnicodeMap_hasOneLineFeedPerLineAndNoCarriageReturn(string asset, int lineCount, int length)
    {
        var map = ToUnicodeOf(CmapFormat12Tests.LoadAsset(asset));

        Assert.Equal(0, map.Count(b => b == 0x0D));
        Assert.Equal(lineCount, map.Count(b => b == 0x0A));
        Assert.Equal(length, map.Length);
        Assert.EndsWith("endcmap\nCMapName currentdict /CMap defineresource pop\nend end\n", Encoding.ASCII.GetString(map));
    }

    [Fact]
    public void DejaVuToUnicodeMap_hasTheBytesLinuxWrote()
    {
        var map = ToUnicodeOf(CmapFormat12Tests.LoadAsset("DejaVuSans.ttf"));

        Assert.Equal(DejaVuHelloToUnicodeSha256, Convert.ToHexString(SHA256.HashData(map)));
    }

    /// <summary>The inflated ToUnicode map of a Kernel document that draws "Hello" in the font.</summary>
    private static byte[] ToUnicodeOf(byte[] font)
    {
        using var doc = new PdfDocument
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(font);
        doc.RegisterEmbeddedFontUsage(page, handle);
        var canvas = new PdfCanvas(page);
        canvas.BeginText().SetFontByName(handle.ResourceName, 12).SetTextMatrix(1, 0, 0, 1, 72, 720);
        var gids = new ushort[5];
        var count = handle.GetGlyphIds("Hello", gids);
        canvas.ShowGlyphs(gids.AsSpan(0, count));
        canvas.EndText();
        canvas.Finish();
        using var ms = new MemoryStream();
        doc.Save(ms);
        var pdf = ms.ToArray();

        var text = Encoding.Latin1.GetString(pdf);
        var pos = 0;
        var maps = new List<byte[]>();
        while (true)
        {
            var s = text.IndexOf("stream\n", pos, StringComparison.Ordinal);
            if (s < 0) break;
            s += "stream\n".Length;
            var e = text.IndexOf("\nendstream", s, StringComparison.Ordinal);
            if (e < 0) break;
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
                body = pdf[s..e];
            }
            if (body.AsSpan().StartsWith("/CIDInit"u8)) maps.Add(body);
            pos = e + "\nendstream".Length;
        }
        return Assert.Single(maps);
    }
}
