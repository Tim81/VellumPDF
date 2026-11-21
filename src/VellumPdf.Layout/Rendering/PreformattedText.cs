// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// One drawn line of a <see cref="Preformatted"/>: the text of a whole source line or of one
/// segment of a wrapped one, and whether a continuation marker follows it.
/// </summary>
internal readonly record struct PreformattedLine(string Text, bool Continued);

/// <summary>
/// The line, tab and wrap rules of <see cref="Preformatted"/>, kept apart from the renderer so the
/// layout pass and the pre-save checks read the text the same way.
/// </summary>
internal static class PreformattedText
{
    /// <summary>
    /// The continuation marker is as wide as one column of Courier or of a monospace font, 600
    /// units of 1,000 to the em.
    /// </summary>
    internal const int MarkerUnits = 600;

    /// <summary>
    /// Finds the next line of <paramref name="text"/> from <paramref name="position"/>, ending at a
    /// carriage return followed by a line feed, at a lone carriage return or at a lone line feed. A
    /// line end at the very end of the text ends the last line and starts none, so the caller stops
    /// when this returns false.
    /// </summary>
    /// <param name="text">The whole text.</param>
    /// <param name="position">Where the line starts; moved past the line end.</param>
    /// <param name="first">True for the first call, which yields one empty line for an empty
    /// text.</param>
    /// <param name="line">The line, without its end.</param>
    internal static bool NextLine(string text, ref int position, bool first, out ReadOnlySpan<char> line)
    {
        if (position >= text.Length && !first)
        {
            line = default;
            return false;
        }

        var rest = text.AsSpan(position);
        var end = rest.IndexOfAny('\r', '\n');
        if (end < 0)
        {
            line = rest;
            position = text.Length;
            return true;
        }

        line = rest[..end];
        position += end + (rest[end] == '\r' && end + 1 < rest.Length && rest[end + 1] == '\n' ? 2 : 1);
        return true;
    }

    /// <summary>
    /// Replaces each tab in <paramref name="line"/> by the spaces that reach the next tab stop, a
    /// stop being a multiple of <paramref name="tabWidth"/> columns, where a column is one extended
    /// grapheme cluster. Returns the line itself, with no copy, when it holds no tab.
    /// </summary>
    internal static ReadOnlySpan<char> Expand(ReadOnlySpan<char> line, int tabWidth, out string? expanded)
    {
        expanded = null;
        if (line.IndexOf('\t') < 0)
            return line;

        var sb = new StringBuilder(line.Length + tabWidth);
        var column = 0;
        var i = 0;
        while (i < line.Length)
        {
            if (line[i] == '\t')
            {
                var fill = tabWidth - (column % tabWidth);
                sb.Append(' ', fill);
                column += fill;
                i++;
                continue;
            }

            var length = StringInfo.GetNextTextElementLength(line[i..]);
            sb.Append(line.Slice(i, length));
            column++;
            i += length;
        }

        expanded = sb.ToString();
        return expanded;
    }

    /// <summary>
    /// The advance of one cluster in 1/1000 em, summed per scalar for an embedded font and per
    /// UTF-16 unit for a Standard-14 font, the lookups <see cref="FontReference.MeasureString"/>
    /// uses. An embedded font registers the glyphs it looks up, as measuring does.
    /// </summary>
    internal static long ClusterUnits(FontReference font, ReadOnlySpan<char> cluster)
    {
        long units = 0;
        if (font.IsEmbedded)
        {
            var embedder = font.Embedded.Embedder;
            foreach (var rune in cluster.EnumerateRunes())
                units += embedder.GetAdvanceWidth(embedder.GetGlyphId(rune.Value));
            return units;
        }

        var standard = font.Standard14;
        foreach (var c in cluster)
            units += Standard14Metrics.GetWidth(standard, c);
        return units;
    }

    internal static long LineUnits(FontReference font, ReadOnlySpan<char> line)
    {
        long units = 0;
        var i = 0;
        while (i < line.Length)
        {
            var length = StringInfo.GetNextTextElementLength(line[i..]);
            units += ClusterUnits(font, line.Slice(i, length));
            i += length;
        }

        return units;
    }

    /// <summary>The width in points of a run of <paramref name="units"/>, as <c>MeasureString</c>
    /// computes it.</summary>
    internal static double Points(long units, double fontSize) => units / 1000.0 * fontSize;

    /// <summary>
    /// Splits <paramref name="element"/> into the lines it draws at <paramref name="width"/>:
    /// lines, tabs, then the wrap. <paramref name="wrappedSourceLines"/> is the number of source
    /// lines that needed a marker.
    /// </summary>
    internal static List<PreformattedLine> Layout(Preformatted element, double width, out int wrappedSourceLines)
    {
        var lines = new List<PreformattedLine>();
        var font = element.Style.FontRef;
        var size = element.Style.FontSize;
        var marker = Points(MarkerUnits, size);
        var wrapped = 0;

        var position = 0;
        var first = true;
        while (NextLine(element.Text, ref position, first, out var source))
        {
            first = false;
            var line = Expand(source, element.TabWidth, out _);
            var total = LineUnits(font, line);
            if (Points(total, size) <= width)
            {
                lines.Add(new PreformattedLine(line.ToString(), false));
                continue;
            }

            wrapped++;
            long consumed = 0;
            var at = 0;
            while (true)
            {
                if (Points(total - consumed, size) <= width)
                {
                    lines.Add(new PreformattedLine(line[at..].ToString(), false));
                    break;
                }

                // At least one cluster, so the wrap advances even when one cluster and the marker
                // are wider than the box; that segment is the one text drawn outside it.
                var start = at;
                long units = 0;
                while (at < line.Length)
                {
                    var length = StringInfo.GetNextTextElementLength(line[at..]);
                    var next = ClusterUnits(font, line.Slice(at, length));
                    if (at > start && Points(units + next, size) + marker > width)
                        break;
                    units += next;
                    at += length;
                }

                lines.Add(new PreformattedLine(line[start..at].ToString(), at < line.Length));
                consumed += units;
                if (at >= line.Length)
                    break;
            }
        }

        wrappedSourceLines = wrapped;
        return lines;
    }
}
