// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Every line of an embedded font's ToUnicode CMap ends with one line feed, on every operating
/// system (#630). The map used to be written with <c>StringBuilder.AppendLine</c>, so it ended its
/// lines with a carriage return and a line feed on Windows. CI runs these tests on Linux, where
/// that call writes a bare line feed, so the build-time ban on the platform line end in
/// <c>src/BannedSymbols.txt</c> is what keeps the change from regressing on a platform CI does not
/// run; these tests pin the bytes.
/// </summary>
public sealed class ToUnicodeLineEndTests
{
    private static readonly string FontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

    // The header and trailer are fixed text, so the expected map is a literal with \n, not a string
    // built by the code under test. 0x48 -> glyph 43 and U+1F600 -> glyph 5857 (a surrogate pair,
    // D83D DE00) are one BMP and one supplementary entry.
    private const string ExpectedMap =
        "/CIDInit /ProcSet findresource begin\n" +
        "12 dict begin\n" +
        "begincmap\n" +
        "/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n" +
        "/CMapName /Adobe-Identity-UCS def\n" +
        "/CMapType 2 def\n" +
        "1 begincodespacerange\n" +
        "<0000> <FFFF>\n" +
        "endcodespacerange\n" +
        "2 beginbfchar\n" +
        "<002B> <0048>\n" +
        "<16E1> <D83DDE00>\n" +
        "endbfchar\n" +
        "endcmap\n" +
        "CMapName currentdict /CMap defineresource pop\n" +
        "end end\n";

    [Fact]
    public void BuildToUnicodeCMapContent_endsEveryLineWithOneLineFeed()
    {
        var map = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(
            new Dictionary<int, ushort> { [0x48] = 43, [0x1F600] = 5857 }, new HashSet<int>());

        Assert.Equal(ExpectedMap, map);
        Assert.DoesNotContain('\r', map);
    }

    /// <summary>
    /// The 101st entry opens a second <c>beginbfchar</c> set (Adobe TN5014 caps a set at 100 lines),
    /// and that boundary writes its own line ends.
    /// </summary>
    [Fact]
    public void BuildToUnicodeCMapContent_chunkBoundary_endsEveryLineWithOneLineFeed()
    {
        var assignments = new Dictionary<int, ushort>();
        for (var i = 0; i < 101; i++) assignments[0x100 + i] = (ushort)(i + 1);

        var map = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(assignments, new HashSet<int>());

        var lines = map.Split('\n');
        Assert.Equal(string.Empty, lines[^1]);                       // ends with a line feed
        Assert.Equal(9 + 2 + 2 + 101 + 3, lines.Length - 1);
        Assert.Equal("100 beginbfchar", lines[9]);
        Assert.Equal("1 beginbfchar", lines[9 + 1 + 100 + 1]);
        Assert.DoesNotContain('\r', map);
    }

    [Fact]
    public void KernelDocument_dejaVu_hasOneLineFeedPerLineAndNoCarriageReturn()
    {
        var maps = ToUnicodeMaps(SaveKernelDocument(File.ReadAllBytes(Path.Combine(FontsDir, "DejaVuSans.ttf"))));

        var map = Assert.Single(maps);
        Assert.Equal(0, Count(map, 0x0D));
        Assert.Equal(18, Count(map, 0x0A));   // 9 header + beginbfchar + 4 entries + endbfchar + 3 trailer
        Assert.StartsWith("/CIDInit /ProcSet findresource begin\n12 dict begin\n", Encoding.ASCII.GetString(map));
        Assert.EndsWith("endcmap\nCMapName currentdict /CMap defineresource pop\nend end\n", Encoding.ASCII.GetString(map));
    }

    [Fact]
    public void LayoutDocument_twoFontsAndTwoChunks_hasNoCarriageReturnInAnyMap()
    {
        using var doc = new VellumPdf.Layout.Document
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        var dejaVu = doc.UseTrueTypeFont(File.ReadAllBytes(Path.Combine(FontsDir, "DejaVuSans.ttf")));
        var liberation = doc.UseTrueTypeFont(File.ReadAllBytes(Path.Combine(FontsDir, "LiberationSans-Regular.ttf")));

        // U+00C0 to U+0137 are 120 distinct code points and the spaces add U+0020, so the
        // DejaVu map needs two beginbfchar sets. A space every ten keeps the paragraph breakable.
        var text = new StringBuilder();
        for (var cp = 0xC0; cp <= 0x137; cp++)
        {
            text.Append((char)cp);
            if ((cp - 0xC0) % 10 == 9) text.Append(' ');
        }
        doc.Add(new Paragraph(text.ToString(), new TextStyle { FontRef = dejaVu, FontSize = 12 }));
        doc.Add(new Paragraph("Hello", new TextStyle { FontRef = liberation, FontSize = 12 }));
        using var ms = new MemoryStream();
        doc.Save(ms);

        var maps = ToUnicodeMaps(ms.ToArray());

        Assert.Equal(2, maps.Count);
        foreach (var map in maps)
            Assert.Equal(0, Count(map, 0x0D));
        var text0 = Encoding.ASCII.GetString(
            maps.Single(m => Encoding.ASCII.GetString(m).Contains("100 beginbfchar\n", StringComparison.Ordinal)));
        Assert.Equal(
            ["100 beginbfchar", "21 beginbfchar"],
            text0.Split('\n').Where(l => l.EndsWith(" beginbfchar", StringComparison.Ordinal)).ToArray());
        // 9 header, 2 sets of (beginbfchar + endbfchar), 121 entries (the 120 and the space), 3 trailer.
        Assert.Equal(9 + 4 + 121 + 3, Count(Encoding.ASCII.GetBytes(text0), 0x0A));
    }

    private static byte[] SaveKernelDocument(byte[] font)
    {
        using var doc = new PdfDocument
        {
            Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
            DocumentId = new byte[16],
        };
        var page = doc.AddPage();
        var handle = doc.UseTrueTypeFont(font);
        var gids = new ushort[5];
        handle.GetGlyphIds("Hello", gids);
        doc.RegisterEmbeddedFontUsage(page, handle);
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static int Count(byte[] data, byte value) => data.Count(b => b == value);

    /// <summary>The inflated body of every stream that starts with <c>/CIDInit</c>, which is only the ToUnicode maps.</summary>
    private static List<byte[]> ToUnicodeMaps(byte[] pdf)
    {
        var text = Encoding.Latin1.GetString(pdf);
        var maps = new List<byte[]>();
        var pos = 0;
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
        return maps;
    }
}
