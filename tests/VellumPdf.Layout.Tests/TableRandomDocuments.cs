// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// Seeded documents of long tables whose cells hold control white space, for the tests that compare
/// one measurement of a table with another over many tables. The same seed gives the same document.
/// </summary>
internal static class TableRandomDocuments
{
    internal static readonly string DejaVu = Path.Combine(AppContext.BaseDirectory, "Fonts", "DejaVuSans.ttf");

    private static readonly DateTimeOffset s_pin = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly byte[] s_pinId = [.. Enumerable.Range(1, 16).Select(i => (byte)i)];

    private static string Text(Random rng, int maxTokens, bool controls, int tallLines)
    {
        string[] words =
        [
            "ab", "x", "iiii", "WWW", "lorem", "ipsum", "caf\u00e9", "x\u00a0y", "\u2003em", "q", "mm",
            rng.Next(6) == 0 ? "Superlongwordwithoutanybreak" : "ok", "1234567", "", "f",
        ];
        string[] seps = controls
            ? [" ", " ", " ", "  ", "\t", "\n", "\n", "\r\n", "\r", "\f", "\v", "\u0085", "\u2028", "\u2029", "\t\t", "\n\n"]
            : [" ", " ", " ", "  "];
        var sb = new StringBuilder();
        var k = rng.Next(7) == 0 && tallLines > 0 ? rng.Next(tallLines / 2, tallLines + 6) : rng.Next(0, maxTokens + 1);

        // A tall cell is mostly line feeds, so its height is near the page's.
        var tall = k > maxTokens;
        for (var i = 0; i < k; i++)
        {
            if (i > 0) sb.Append(tall && controls && rng.Next(5) != 0 ? (rng.Next(6) == 0 ? "\r\n" : "\n") : seps[rng.Next(seps.Length)]);
            sb.Append(words[rng.Next(words.Length)]);
        }
        if (controls && rng.Next(10) == 0) sb.Append(seps[rng.Next(seps.Length)]);
        if (controls && rng.Next(15) == 0) sb.Insert(0, "\t");
        return sb.ToString();
    }

    private static TextStyle Style(Random rng, EmbeddedFontHandle dv)
    {
        double[] sizes = [7, 9, 10, 12, 14];
        FontReference f = rng.Next(5) switch
        {
            0 => Standard14.TimesRoman,
            1 => Standard14.Courier,
            2 or 3 => dv,
            _ => Standard14.Helvetica,
        };
        var size = sizes[rng.Next(sizes.Length)];

        // The generator once drew a leading here; the draw stays so that a seed keeps its tables.
        _ = rng.Next(6);
        return new TextStyle { FontRef = f, FontSize = size };
    }

    private static void Widths(Random rng, TableElement t, int cols, double pw)
    {
        switch (rng.Next(5))
        {
            case 0: break;
            case 1:
                t.SetColumnWidths([.. Enumerable.Range(0, cols).Select(_ => (double)rng.Next(20, Math.Max(21, (int)(pw / cols))))]);
                break;
            case 2:
                t.SetColumnWidths([.. Enumerable.Range(0, cols).Select(_ => rng.Next(4) switch
                {
                    0 => 0.0,
                    1 => -5.0,
                    2 => double.NaN,
                    _ => (double)rng.Next(20, Math.Max(21, (int)(pw / cols))),
                })]);
                break;
            case 3:
                t.SetColumnWidths([.. Enumerable.Range(0, rng.Next(1, cols + 1)).Select(_ => (double)rng.Next(20, Math.Max(21, (int)(pw / cols))))]);
                break;
            default:
                t.SetColumnWidths([.. Enumerable.Range(0, cols).Select(i => i == 0 ? (double)rng.Next(30, Math.Max(31, (int)(pw * 0.7))) : 0.0)]);
                break;
        }
    }

    private static void Fill(Random rng, TableElement t, int rows, int cols, int headers, bool controls, int tallLines, EmbeddedFontHandle dv, double rowSpanP, double colSpanP)
    {
        for (var r = 0; r < rows; r++)
        {
            var isHeader = r < headers || rng.Next(25) == 0;
            var row = t.AddRow(isHeader);
            var c = 0;
            var n = rng.Next(8) == 0 ? cols + 1 : cols;
            while (c < n)
            {
                var colSpan = rng.NextDouble() < colSpanP ? rng.Next(2, 4) : 1;
                var rowSpan = rng.NextDouble() < rowSpanP ? rng.Next(2, 5) : 1;
                var cell = new Cell(Text(rng, 14, controls, isHeader ? tallLines / 5 : rowSpan > 1 ? tallLines / 3 : tallLines))
                {
                    ColSpan = colSpan,
                    RowSpan = rowSpan,
                    Style = rng.Next(4) == 0 ? Style(rng, dv) : null,
                    Padding = rng.Next(5) == 0 ? new EdgeInsets(rng.Next(0, 9), rng.Next(0, 9), rng.Next(0, 9), rng.Next(0, 9)) : new EdgeInsets(4, 6, 4, 6),
                };
                row.AddCell(cell);
                c += colSpan;
            }
        }
    }

    /// <summary>
    /// Long tables over many pages: repeated header rows, rows cut between their lines, mirrored
    /// margins of unequal sides, running bands, and several tables with paragraphs between them.
    /// Explicit, zero, negative and NaN column widths all occur. The caller disposes the document.
    /// </summary>
    internal static Document Long(int seed)
    {
        var rng = new Random((seed * 31337) + 5);
        var pw = rng.Next(350, 900);
        var ph = rng.Next(300, 700);
        var d = new Document
        {
            Timestamp = s_pin,
            DocumentId = s_pinId,
            PageSize = new PdfRectangle(0, 0, pw, ph),
            Margins = new EdgeInsets(rng.Next(5, 50), rng.Next(5, 80), rng.Next(5, 50), rng.Next(5, 80)),
            MirrorMargins = rng.Next(2) == 0,
            Tagged = rng.Next(6) == 0,
            Language = "en",
        };
        var dv = d.UseTrueTypeFont(File.ReadAllBytes(DejaVu));
        if (rng.Next(3) == 0)
            d.SetHeader(rng.Next(2) == 0 ? "Page {page}\tof {total}" : "Head {page}", new TextStyle { FontRef = rng.Next(2) == 0 ? dv : new FontReference(Standard14.Helvetica), FontSize = 8 });
        if (rng.Next(4) == 0) d.SetFooter("{page}", new TextStyle { FontSize = 8 });
        d.MirrorMargins = true;
        var tables = rng.Next(1, 4);
        for (var k = 0; k < tables; k++)
        {
            if (rng.Next(2) == 0) d.Add(new Paragraph(string.Join(" ", Enumerable.Repeat("before", rng.Next(1, 200)))));
            var cols = rng.Next(1, 5);
            var t = new TableElement { DefaultCellStyle = rng.Next(2) == 0 ? new TextStyle { FontRef = dv, FontSize = 9 } : null };
            Widths(rng, t, cols, pw);
            var size = t.DefaultCellStyle?.FontSize ?? 12;
            var tall = (int)(ph / (size * 1.2));
            Fill(rng, t, rng.Next(20, 120), cols, rng.Next(0, 3), true, tall, dv, 0.04, 0.05);
            d.Add(t);
        }
        return d;
    }
}
