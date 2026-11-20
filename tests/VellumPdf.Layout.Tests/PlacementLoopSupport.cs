// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Shared by the placement-loop tests (#288): documents built only from public renderers over a
/// caller-owned <see cref="PdfDocument"/>, a wrapper that logs every <c>Layout</c> call, and the
/// normalisation that lets one hash serve net10.0 and net11.0.
/// </summary>
internal static class PlacementLoopSupport
{
    internal static readonly PdfRectangle Page = new(0, 0, 300, 300);
    internal static readonly EdgeInsets Margins = new(20);

    internal static readonly TextStyle Body = new() { Font = Standard14.Helvetica, FontSize = 12 };

    internal static PdfDocument NewPdf() => new()
    {
        Timestamp = new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero),
        DocumentId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F"),
    };

    /// <summary>One line per <c>Layout</c> call: the wrapper's number, then the area offered.</summary>
    internal sealed class CallLog
    {
        private readonly List<string> _lines = [];
        private int _next;

        internal int Calls => _lines.Count;
        internal int NextId() => _next++;
        internal void Add(int id, LayoutBox area) =>
            _lines.Add(string.Create(CultureInfo.InvariantCulture, $"{id} {area.X:F3} {area.Y:F3} {area.Width:F3} {area.Height:F3}"));
        internal string Text => string.Join('\n', _lines);
    }

    /// <summary>Forwards to <paramref name="inner"/> and logs each <c>Layout</c> call. A split or overflow renderer it returns is wrapped under the same number.</summary>
    internal sealed class Logging(IRenderer inner, CallLog log, int id) : IRenderer
    {
        internal static Logging Wrap(IRenderer inner, CallLog log) => new(inner, log, log.NextId());

        public LayoutResult Layout(LayoutContext context)
        {
            log.Add(id, context.Area);
            var result = inner.Layout(context);
            if (result.Status == LayoutResult.Outcome.Partial)
                return LayoutResult.Partial(
                    result.OccupiedArea!.Value,
                    new Logging(result.SplitRenderer!, log, id),
                    new Logging(result.OverflowRenderer!, log, id));
            return result;
        }

        public void Draw(DrawContext context) => inner.Draw(context);
    }

    internal static string ParagraphText(int i) =>
        $"Paragraph {i}: the quick brown fox jumps over the lazy dog, again and again, over a line that wraps.";

    /// <summary>Paragraphs, a table and a list: several pages at 300 x 300 pt.</summary>
    internal static List<IRenderer> StandardContent(CallLog? log = null)
    {
        var list = new List<IRenderer>();
        for (var i = 0; i < 12; i++)
            list.Add(new ParagraphRenderer(new Paragraph(ParagraphText(i), Body)));
        var table = new TableElement();
        for (var r = 0; r < 14; r++)
        {
            var row = table.AddRow();
            row.AddCell($"Row {r} cell A");
            row.AddCell($"Row {r} cell B with a little more text to wrap in the column");
        }
        list.Add(new TableRenderer(table));
        var items = new ListElement(ListStyle.OrderedDecimal);
        for (var i = 0; i < 10; i++)
            items.Add($"Item {i} of the list, with enough words to take two lines on this narrow page.", Body);
        list.Add(new ListRenderer(items));
        for (var i = 12; i < 24; i++)
            list.Add(new ParagraphRenderer(new Paragraph(ParagraphText(i), Body)));
        return log is null ? list : [.. list.Select(r => (IRenderer)Logging.Wrap(r, log))];
    }

    internal static byte[] Render(DocumentRenderer renderer)
    {
        using var ms = new MemoryStream();
        renderer.Render(ms);
        return ms.ToArray();
    }

    internal static int PageCount(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page(?![s\w])").Count;

    internal static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    internal static string NormalisedSha256(byte[] pdf, bool foldToUnicodeLineEnds = false) =>
        Convert.ToHexString(SHA256.HashData(Encoding.Latin1.GetBytes(Normalise(pdf, foldToUnicodeLineEnds))));

    // The same rule as CatalogGoldenTests.Normalise in VellumPdf.Kernel.Tests (#626): inflate every
    // Flate stream and blank the xref offsets, so the compressor of the runtime drops out.
    private static readonly Regex StreamObject = new(
        @"(?<head>\d+ 0 obj\n)(?<dict><<(?:(?!\nstream\n|endobj).)*?>>)(?<mid>\nstream\n)(?<data>.*?)(?<tail>\nendstream)",
        RegexOptions.Singleline | RegexOptions.CultureInvariant);

    // foldToUnicodeLineEnds: an embedded font's ToUnicode CMap is written with CR LF line ends on
    // Windows and LF on Linux (#630), so a hash that must hold on both folds CR LF to LF inside
    // ToUnicode streams, and nowhere else. It does nothing once #630 is fixed (#647), because
    // every line end is then LF already.
    internal static string Normalise(byte[] pdf, bool foldToUnicodeLineEnds = false)
    {
        var text = Encoding.Latin1.GetString(pdf);
        text = StreamObject.Replace(text, m =>
        {
            var dictionary = m.Groups["dict"].Value;
            if (!dictionary.Contains("/FlateDecode", StringComparison.Ordinal))
                return m.Value;
            var inflated = Inflate(Encoding.Latin1.GetBytes(m.Groups["data"].Value));
            if (foldToUnicodeLineEnds && inflated.Contains("begincmap", StringComparison.Ordinal))
                inflated = inflated.Replace("\r\n", "\n", StringComparison.Ordinal);
            dictionary = Regex.Replace(dictionary, @"/Length \d+", "/Length " + inflated.Length);
            dictionary = dictionary.Replace("/Filter /FlateDecode", "/Filter /FlateDecode (inflated)", StringComparison.Ordinal);
            return m.Groups["head"].Value + dictionary + m.Groups["mid"].Value + inflated + m.Groups["tail"].Value;
        });
        text = Regex.Replace(text, @"\d{10} (\d{5}) ([nf])", "# $1 $2");
        text = Regex.Replace(text, @"startxref\n\d+", "startxref\n#");
        return text;
    }

    // An empty stream has no zlib header on net10.0, so there is nothing to inflate.
    private static string Inflate(byte[] data)
    {
        if (data.Length == 0)
            return string.Empty;
        using var input = new MemoryStream(data);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return Encoding.Latin1.GetString(output.ToArray());
    }
}
