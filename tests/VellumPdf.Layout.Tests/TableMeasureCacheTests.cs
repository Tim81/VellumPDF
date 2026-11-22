// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The caches of the decision that reads some table cells unbroken. A cache answers what measuring
/// again would, so these tests count what it answered and compare the outcome with the one the
/// documentation states; the differential against the build without the caches is run apart from
/// the suite.
/// </summary>
public sealed class TableMeasureCacheTests
{
    private static TextStyle Helvetica(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static Document WidePage() => new()
    {
        PageSize = new PdfRectangle(0, 0, 2000, 600),
        Margins = new EdgeInsets(10),
    };

    private static string Words(int n, string separator = "\n") =>
        string.Join(separator, Enumerable.Repeat("ab", n));

    private static byte[] Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static string[] Dropped(Document doc) =>
        [.. doc.Diagnostics
            .Where(d => d.Code == LayoutDiagnosticCode.CellWhiteSpaceDropped)
            .Select(d => Regex.Match(d.Message, @"row \d+, cell \d+").Value)
            .OrderBy(m => int.Parse(Regex.Match(m, @"\d+").Value, CultureInfo.InvariantCulture))
            .ThenBy(m => m, StringComparer.Ordinal)];

    /// <summary>Runs <paramref name="save"/> with a probe on this thread, and returns the probe.</summary>
    private static TableMeasureProbe Probed(Action save)
    {
        var probe = new TableMeasureProbe();
        TableMeasureProbe.Current = probe;
        try { save(); }
        finally { TableMeasureProbe.Current = null; }
        return probe;
    }

    /// <summary>
    /// Columns of 36pt, 36pt and one sized by its content, then <paramref name="groups"/> groups
    /// that each give up the breaks of a cell of 60 lines in the automatic column.
    /// </summary>
    private static TableElement FillerTable(int groups)
    {
        var t = new TableElement { DefaultCellStyle = Helvetica() }.SetColumnWidths(36, 36, 0);
        for (var i = 0; i < groups; i++)
        {
            t.AddRow().AddCell("x").AddCell("x").AddCell(new Cell(Words(60)) { RowSpan = 2 });
            t.AddRow().AddCell("y");
        }
        return t;
    }

    /// <summary>
    /// A cell's longest word and widest line are measured once per mode. Each of the 6 groups ends
    /// a pass, so the table is resolved more than 6 times, and a cell is measured once as it is
    /// and, for the cell that gives up its breaks, once as it is read unbroken.
    /// </summary>
    [Fact]
    public void ACellIsMeasuredForItsColumnWidthOncePerMode()
    {
        const int groups = 6;
        using var doc = WidePage();
        doc.Add(FillerTable(groups));

        var probe = Probed(() => Save(doc));

        Assert.Equal(Enumerable.Range(0, groups).Select(i => $"row {2 * i}, cell 2").ToArray(), Dropped(doc));
        Assert.True(probe.Passes > groups, $"{probe.Passes} resolutions");
        const int cells = 4 * groups;
        Assert.Equal(cells + groups, probe.WidthMisses);
        Assert.True(probe.WidthHits > probe.WidthMisses, $"{probe.WidthHits} hits, {probe.WidthMisses} misses");
    }
}
