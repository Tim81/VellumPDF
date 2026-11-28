// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #365: a rule of a zoned band never touches the text. The page is rendered by pdftoppm and the
/// pixels in the rule's rows are counted, so the test sees the glyphs that were drawn and not the
/// arithmetic that placed them. The text holds descenders (g, j, p, q, y) and tall accented capitals,
/// in each of three zones.
/// </summary>
public sealed class BandZonesRuleRenderTests : IDisposable
{
    private readonly string _dir;

    public BandZonesRuleRenderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"vellumrule_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best-effort: the directory may be locked or gone */ }
        catch (UnauthorizedAccessException) { /* best-effort: a locked file on Windows */ }
    }

    private const int PixelsPerPoint = 5;

    // The bounding box of each face in Adobe's published metrics, from the lowest y to the highest.
    private static (double Low, double High) Box(Standard14 font) => font switch
    {
        Standard14.Helvetica => (-0.225, 0.931),
        Standard14.TimesRoman => (-0.218, 0.898),
        _ => throw new ArgumentOutOfRangeException(nameof(font)),
    };

    private static (int Width, int Height, byte[] Rgb) ReadPpm(byte[] data)
    {
        // "P6\n<width> <height>\n255\n" and then three bytes a pixel.
        var pos = 0;
        string Token()
        {
            while (char.IsWhiteSpace((char)data[pos])) pos++;
            var start = pos;
            while (!char.IsWhiteSpace((char)data[pos])) pos++;
            return Encoding.ASCII.GetString(data, start, pos - start);
        }

        Assert.Equal("P6", Token());
        var width = int.Parse(Token(), CultureInfo.InvariantCulture);
        var height = int.Parse(Token(), CultureInfo.InvariantCulture);
        Assert.Equal("255", Token());
        pos++;
        return (width, height, data[pos..]);
    }

    [Theory]
    [InlineData(Standard14.Helvetica, BandRulePosition.Below, 0.5, true)]
    [InlineData(Standard14.Helvetica, BandRulePosition.Below, 1, true)]
    [InlineData(Standard14.Helvetica, BandRulePosition.Below, 1, false)]
    [InlineData(Standard14.Helvetica, BandRulePosition.Above, 1, true)]
    [InlineData(Standard14.Helvetica, BandRulePosition.Above, 0.5, false)]
    [InlineData(Standard14.TimesRoman, BandRulePosition.Below, 1, true)]
    [InlineData(Standard14.TimesRoman, BandRulePosition.Above, 1, false)]
    public void TheRule_touchesNoGlyphPixel_atTheMinimumHeightAndAtTheDefault(
        Standard14 face, BandRulePosition position, double ruleWidth, bool minimumHeight)
    {
        var style = new TextStyle { Font = face, FontSize = 10 };
        var (low, high) = Box(face);

        // The text box plus the rule and its clearance of 1 pt: the least the save accepts. The extra
        // 0.001 keeps a rounding of the sum from landing under it.
        double? height = minimumHeight ? (high - low) * 10 + ruleWidth + 1 + 0.001 : null;
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 300), Margins = new EdgeInsets(20) };
        doc.Add(new Paragraph("body", style) { Margins = new EdgeInsets(200, 0, 0, 0) });
        const string text = "gjpqy ÅÄÖ gjpqy";
        doc.Header = new RunningBand(new BandZone(text, style), new BandZone(text, style), new BandZone(text, style))
        {
            Height = height,
            Rule = new BandRule(position, ruleWidth, new ColorRgb(1, 0, 0)),
        };

        using var ms = new MemoryStream();
        doc.Save(ms);
        var pdf = Path.Combine(_dir, "rule.pdf");
        File.WriteAllBytes(pdf, ms.ToArray());
        var prefix = Path.Combine(_dir, "page");

        // The top 60 pt of the page, 1500 x 300 pixels.
        ExternalTool.TryRun(
            "pdftoppm",
            ["-r", (72 * PixelsPerPoint).ToString(CultureInfo.InvariantCulture), "-x", "0", "-y", "0", "-W", "1500", "-H", "300", "-singlefile", pdf, prefix],
            out var exit, out _, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, stderr);
        var (width, rows, rgb) = ReadPpm(File.ReadAllBytes(prefix + ".ppm"));
        Assert.Equal((1500, 300), (width, rows));

        var bandHeight = doc.Header.EffectiveHeight;
        var (top, bottom) = position == BandRulePosition.Below
            ? (20 + bandHeight - ruleWidth, 20 + bandHeight)
            : (20.0, 20 + ruleWidth);
        var firstRow = (int)Math.Round(top * PixelsPerPoint);
        var lastRow = (int)Math.Round(bottom * PixelsPerPoint);

        int Dark(int from, int to)
        {
            var count = 0;
            for (var y = from; y < to; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 3;
                    if (rgb[i] < 100 && rgb[i + 1] < 100 && rgb[i + 2] < 100) count++;
                }

            return count;
        }

        int Red(int from, int to)
        {
            var count = 0;
            for (var y = from; y < to; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = (y * width + x) * 3;
                    if (rgb[i] - rgb[i + 1] > 60 && rgb[i] - rgb[i + 2] > 60) count++;
                }

            return count;
        }

        // The file holds the feature: a red rule across the content width, and glyphs in the band.
        Assert.True(Red(firstRow, lastRow) > 1000, "the rule must be drawn in the rows computed for it");
        Assert.True(Dark(20 * PixelsPerPoint, (int)((20 + bandHeight) * PixelsPerPoint)) > 200, "the band must show its glyphs");

        // The verdict: no glyph pixel in the rule's rows.
        Assert.Equal(0, Dark(firstRow, lastRow));
    }
}
