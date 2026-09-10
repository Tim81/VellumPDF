// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The table pull request's horizontal axis: the column count, an explicit widths array, the
/// auto-width sum, and a cell wider than its own column. Four issues that meet in
/// <c>TableGridResolver.Resolve</c>, its own auto-width path and the table renderer's cell text
/// path — the plan's own analysis found that fixing any one alone reintroduces another, which is
/// why one class covers all four.
///
/// Every case here goes through the public <c>Document</c>/<c>TableElement</c> surface and reads
/// the emitted content stream back, the same way <see cref="OffPagePlacementTests"/> does — the
/// grid resolver itself is internal, and this project carries no <c>InternalsVisibleTo</c> grant
/// for it (only <c>VellumPdf.Signing</c>, a strong-named assembly, has one), so the resolved column
/// widths are only observable through what they cause the renderer to draw.
///
/// Content-loss cases assert an occurrence count rather than presence: the existing rowspan test
/// passes whether the spanning cell is drawn once or twice, which is exactly how a dropped or
/// duplicated cell survives review undetected.
///
/// Sectioned like <see cref="OffPagePlacementTests"/>: (a) column count, (b) explicit widths,
/// (c) auto-width sum, (d) cell text hard-break.
/// </summary>
public sealed partial class TableColumnAxisTests
{
    private static TextStyle Style(double size = 10) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = size,
    };

    private static string RenderAndDecompress(Document doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    /// <summary>
    /// Cell border rectangles in stream order: <c>DrawCell</c> emits exactly one <c>re</c> per
    /// cell when it has no background fill, via <c>Rectangle(x, y, w, h)</c>'s own
    /// <c>"{x} {y} {w} {h} re"</c> line — the format documented on <c>PdfCanvas.Rectangle</c>.
    /// </summary>
    [GeneratedRegex(@"(?m)^(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re$")]
    private static partial Regex RectangleLine();

    private static List<(double X, double Y, double W, double H)> CellRectangles(string decompressed)
    {
        var result = new List<(double, double, double, double)>();
        foreach (Match m in RectangleLine().Matches(decompressed))
        {
            double G(int i) => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture);
            result.Add((G(1), G(2), G(3), G(4)));
        }
        return result;
    }

    // ── (a) Column count ──────────────────────────────────────────────────────

    /// <summary>
    /// Page 400x300, margin 10; row 0 holds "a0 a1", row 1 holds "b0 b1 b2". Before the fix,
    /// <c>ColCount</c> came from <c>table.Rows.FirstOrDefault()</c> alone — 2 — so the renderer's
    /// own <c>while (col &lt; _colWidths.Length)</c> loop in <c>DrawRow</c> never reached "b2": it
    /// stopped at column 2 regardless of how many cells row 1 actually carried. The count is now
    /// the widest row, so all six cells draw.
    /// </summary>
    [Fact]
    public void ColumnCount_widerLaterRow_drawsEveryCellInIt()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var st = Style();
        var t = new TableElement { DefaultCellStyle = st };
        var r0 = t.AddRow(); r0.AddCell("a0"); r0.AddCell("a1");
        var r1 = t.AddRow(); r1.AddCell("b0"); r1.AddCell("b1"); r1.AddCell("b2");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        foreach (var text in new[] { "a0", "a1", "b0", "b1", "b2" })
            Assert.Equal(1, placements.Count(p => p.Text == text));
    }

    /// <summary>
    /// A row-axis fixture that must not move here: the same three-row, uneven-count shape as
    /// <see cref="ColumnCount_widerLaterRow_drawsEveryCellInIt"/>, except the widest row is a
    /// header rather than a later data row. That is the row-axis defect the next pull request
    /// owns (its own header handling, not the column count), so this table's own header-repeat
    /// logic — not touched here — still decides what draws; this only pins that resolving the
    /// column count from three unevenly sized rows does not throw or drop a whole row's cells at
    /// the grid level.
    /// </summary>
    [Fact]
    public void ColumnCount_unevenRowsIncludingAHeader_resolvesWithoutLoss()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var st = Style();
        var t = new TableElement { DefaultCellStyle = st };
        t.AddRow(isHeader: true).AddCell("H0");
        var r1 = t.AddRow(); r1.AddCell("d0"); r1.AddCell("d1"); r1.AddCell("d2");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(1, placements.Count(p => p.Text == "H0"));
        foreach (var text in new[] { "d0", "d1", "d2" })
            Assert.Equal(1, placements.Count(p => p.Text == text));
    }

    // ── (b) Explicit widths array ───────────────────────────────────────────────

    /// <summary>
    /// Page 400x200, margin 10 (available 380), an explicit two-entry array against a three-cell
    /// row. <c>TableRenderer.DrawRow</c> stops at <c>_colWidths.Length</c>, so before the fix
    /// <c>ColWidths</c> was the array copied verbatim — length 2 — and "c2" never drew regardless
    /// of the column count. The missing entry now reads as auto and gets a share of the width the
    /// two explicit columns leave over.
    /// </summary>
    [Fact]
    public void ExplicitWidths_shortArray_threeCells_allDraw()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 200),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(100, 100);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1"); row.AddCell("c2");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        foreach (var text in new[] { "c0", "c1", "c2" })
            Assert.Equal(1, placements.Count(p => p.Text == text));
    }

    /// <summary>Same shape, a fourth cell past the same two-entry array — two missing entries.</summary>
    [Fact]
    public void ExplicitWidths_shortArray_fourCells_allDraw()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 200),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(100, 100);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1"); row.AddCell("c2"); row.AddCell("c3");
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        foreach (var text in new[] { "c0", "c1", "c2", "c3" })
            Assert.Equal(1, placements.Count(p => p.Text == text));
    }

    /// <summary>
    /// A three-entry array against a two-cell row: the resolved column count is 2, so the third
    /// entry is never read. That was true before this fix too — this pins that reconciling the
    /// array against the count does not start reading past it, by comparing the drawn cell
    /// rectangles against the same two widths supplied alone.
    /// </summary>
    [Fact]
    public void ExplicitWidths_surplusArray_thirdEntryIgnored()
    {
        double[] RectWidths(double[] widths)
        {
            using var doc = new Document
            {
                PageSize = new PdfRectangle(0, 0, 400, 200),
                Margins = new EdgeInsets(10),
            };
            var t = new TableElement { DefaultCellStyle = Style() };
            t.SetColumnWidths(widths);
            var row = t.AddRow();
            row.AddCell("c0"); row.AddCell("c1");
            doc.Add(t);
            return CellRectangles(RenderAndDecompress(doc)).Select(r => r.W).ToArray();
        }

        Assert.Equal(RectWidths([100, 100]), RectWidths([100, 100, 100]));
    }

    /// <summary>
    /// Page 200x200, margin 10 (available 180), two zero-width columns. <c>TableElement.ColWidths</c>
    /// and <c>SetColumnWidths</c> both document zero as meaning auto; before the fix neither
    /// implemented it, and a zero-width column's border rectangle drew at zero width, abutting the
    /// next one. Both columns now get a share of the available width instead of collapsing to zero.
    /// </summary>
    [Fact]
    public void ExplicitWidths_zero_meansAuto()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 200),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(0, 0);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1");
        doc.Add(t);

        var rects = CellRectangles(RenderAndDecompress(doc));

        Assert.Equal(2, rects.Count);
        Assert.All(rects, r => Assert.True(r.W > 0));
    }

    /// <summary>
    /// Same fixture, negative widths. The maintainer's decision — not the plan's, which left it
    /// open — is to clamp rather than refuse: refusing would turn a document that renders today
    /// into a thrown exception, which a patch release must not introduce, so a negative entry gets
    /// the same auto treatment as zero and this pins that the two fixtures draw identically.
    /// </summary>
    [Fact]
    public void ExplicitWidths_negative_clampedToTheSameAutoWidthsAsZero()
    {
        double[] RectWidths(double[] widths)
        {
            using var doc = new Document
            {
                PageSize = new PdfRectangle(0, 0, 200, 200),
                Margins = new EdgeInsets(10),
            };
            var t = new TableElement { DefaultCellStyle = Style() };
            t.SetColumnWidths(widths);
            var row = t.AddRow();
            row.AddCell("c0"); row.AddCell("c1");
            doc.Add(t);
            return CellRectangles(RenderAndDecompress(doc)).Select(r => r.W).ToArray();
        }

        var negative = RectWidths([-100, -100]);
        Assert.All(negative, w => Assert.True(w > 0));
        Assert.Equal(RectWidths([0, 0]), negative);
    }

    /// <summary>
    /// Page 200x200, margin 10 (available 180), two 500pt columns — 1000pt against 180. Before the
    /// fix the array was copied verbatim, so the second column's rectangle started at x 510,
    /// entirely off a 200pt page. The resolved sum is now capped to the available width.
    /// </summary>
    [Fact]
    public void ExplicitWidths_oversized_sumCappedToAvailable()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 200),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(500, 500);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1");
        doc.Add(t);

        var rects = CellRectangles(RenderAndDecompress(doc));

        Assert.Equal(2, rects.Count);
        Assert.Equal(180.0, rects.Sum(r => r.W), 0.001);
        Assert.True(rects[1].X + rects[1].W <= 200.0 + 0.001);
    }

    /// <summary>
    /// Oversized and short at once: a two-entry, 500+500 array against a three-cell row, page
    /// 400x900, margin 50 (available 300). The explicit entries alone already exceed the available
    /// width, so the auto column's residual is zero and it falls back to its own content floor --
    /// exactly enough to draw "c2" on one line. A first version of this fix scaled every column,
    /// auto included, by the same factor once the row overran, which pushed that floor below what
    /// "c2" needs and forced <c>TableRenderer</c>'s own hard-break (#473) for a cell that was never
    /// the reason the row was too wide. The auto column keeps its floor here; the two oversized
    /// explicit columns absorb the correction instead.
    /// </summary>
    [Fact]
    public void ExplicitWidths_oversizedAndShortTogether_autoColumnKeepsItsFloor()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 900),
            Margins = new EdgeInsets(50),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(500, 500);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1"); row.AddCell("c2");
        doc.Add(t);

        var stream = RenderAndDecompress(doc);
        var rects = CellRectangles(stream);
        var placements = ContentStreamReadback.TextPlacements(stream);

        Assert.Equal(3, rects.Count);
        Assert.True(rects.Sum(r => r.W) <= 300.0 + 0.001);
        Assert.Equal(3, placements.Count);
        foreach (var text in new[] { "c0", "c1", "c2" })
            Assert.Equal(1, placements.Count(p => p.Text == text));
    }

    // ── (c) Auto-width sum (#468) ─────────────────────────────────────────────

    /// <summary>
    /// Page 400x400, margin 60 (content box [60, 340]), three auto-width columns at Helvetica
    /// 24pt, all three cells holding the same word from the "W" followed by g's family — the
    /// worst corner <see cref="LayoutGen.CellWord"/>'s own comment documents. Before the fix,
    /// <c>AutoWidth</c> floored each column at its longest word and never capped the sum: measured
    /// here, the rightmost cell's right edge sat at 340 through five characters, 364.128 at six —
    /// past the content box — and 404.16 at seven, past the 400pt page itself. After the fix every
    /// case caps to 340, the content box edge, regardless of word length.
    /// </summary>
    [Theory]
    [InlineData("Wg", 340.0)]
    [InlineData("Wggg", 340.0)]
    [InlineData("Wgggg", 340.0)]
    [InlineData("Wggggg", 340.0)]
    [InlineData("Wgggggg", 340.0)]
    public void AutoWidth_columnFloorsExceedingTheSum_capsToTheContentBox(string word, double expectedRightEdge)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 400),
            Margins = new EdgeInsets(60),
        };
        var st = Style(24);
        var t = new TableElement { DefaultCellStyle = st };
        var row = t.AddRow();
        row.AddCell(word); row.AddCell(word); row.AddCell(word);
        doc.Add(t);

        var rects = CellRectangles(RenderAndDecompress(doc));
        var rightEdge = rects.Max(r => r.X + r.W);

        Assert.Equal(expectedRightEdge, rightEdge, 0.001);
        Assert.True(rightEdge <= 400.0 + 0.001, "must stay on the page");
    }

    // ── (d) Cell text hard-break (#473) ───────────────────────────────────────

    /// <summary>
    /// Page 400x900, margin 50, one 300pt column (padding EdgeInsets(4, 6, 4, 6), inner width
    /// 288), one cell holding 60 "W"s. <c>WordWrapLines</c> used to emit a word wider than the
    /// column whole, so before the fix this was one 60-character literal at <c>Tm</c> x -83.2
    /// under Centre and -222.4 under Right — measured here, on the unmodified method, before
    /// pinning the fixed behaviour. <c>HardBreakWord</c> now splits it at character granularity
    /// (30 + 30, both 283.2pt, under the 288pt inner width) the way
    /// <c>ParagraphRenderer.HardBreakWord</c> already splits an over-wide paragraph word, so both
    /// lines fit and neither needs the floor below to stay on the page.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void CellText_widerThanItsColumn_hardBreaksInsteadOfOverrunning(HorizontalAlignment alignment)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 900),
            Margins = new EdgeInsets(50),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(300);
        t.AddRow().AddCell(new Cell(new string('W', 60)) { Alignment = alignment });
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(2, placements.Count);
        Assert.Equal(30, placements[0].Text.Length);
        Assert.Equal(30, placements[1].Text.Length);
        Assert.All(placements, p => Assert.True(p.X >= 56.0 - 0.001, "no origin left of the cell"));
    }

    /// <summary>
    /// The residue <see cref="CellText_widerThanItsColumn_hardBreaksInsteadOfOverrunning"/>'s own
    /// comment notes as the one case a character-granularity break cannot avoid: a single rune
    /// wider than the column. Page 200x600, margin 50, one 20pt column with zero padding, one cell
    /// holding "W" at Helvetica 106pt (measures ~100pt). Before the fix this measured Tm x -30.064
    /// under Right and 9.968 under Centre; floored at the cell's own left edge (#473, the same
    /// clamp #472 made for a paragraph), both now land on it.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void CellText_oneRuneWiderThanTheColumn_startsAtTheCellLeftEdge(HorizontalAlignment alignment)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 600),
            Margins = new EdgeInsets(50),
        };
        var st = Style(106);
        var t = new TableElement { DefaultCellStyle = st };
        t.SetColumnWidths(20);
        t.AddRow().AddCell(new Cell("W") { Alignment = alignment, Padding = EdgeInsets.Zero });
        doc.Add(t);

        var placement = Assert.Single(ContentStreamReadback.TextPlacements(RenderAndDecompress(doc)));

        Assert.Equal(50.0, placement.X, 0.001);
    }
}
