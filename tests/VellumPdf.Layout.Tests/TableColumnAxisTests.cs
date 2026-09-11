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
    /// A row-axis fixture that must not move here: the same two-row, uneven-count shape as
    /// <see cref="ColumnCount_widerLaterRow_drawsEveryCellInIt"/> — one cell against three — except
    /// the narrower row is a header rather than an earlier data row. That is the row-axis defect
    /// the next pull request owns (its own header handling, not the column count), so this table's
    /// own header-repeat logic — not touched here — still decides what draws; this only pins that
    /// resolving the column count from two unevenly sized rows does not throw or drop a whole
    /// row's cells at the grid level.
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

    /// <summary>
    /// T3: the widest-row rule (#480 section 1) also widens the count for a row's own
    /// <c>ColSpan</c>, not only for a row with more cells. Page 400x300, margin 10 (available
    /// 380), no explicit widths, row 0 "a0 a1" (two ordinary cells, setting an apparent count of
    /// 2), row 1 "b0" plus a "b1" with <c>ColSpan = 2</c> (a true count of 3). Before this fix the
    /// grid resolved to two auto columns at 190pt each and the ColSpan-2 cell drew at 190pt — one
    /// column's width, the span silently discarded. It now resolves to three columns at 152, 152
    /// and 76, and the span cell draws at 152 + 76 = 228pt. Both shapes keep every literal inside
    /// the content box, so this is not recovering lost content the way the "more cells" case is —
    /// it is honouring a span width the caller asked for and the old count ignored, and any
    /// existing document with this shape resolves to different column widths after this fix.
    /// </summary>
    [Fact]
    public void ColumnCount_columnSpanOnALaterRow_resolvesTheSpanWidth()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var st = Style();
        var t = new TableElement { DefaultCellStyle = st };
        var r0 = t.AddRow(); r0.AddCell("a0"); r0.AddCell("a1");
        var r1 = t.AddRow(); r1.AddCell("b0"); r1.AddCell(new Cell("b1") { ColSpan = 2 });
        doc.Add(t);

        var rects = CellRectangles(RenderAndDecompress(doc));

        Assert.Equal(4, rects.Count);
        // Row 0: two ordinary columns. Row 1: the plain cell, then the span cell.
        Assert.Equal(152.0, rects[0].W, 0.001);
        Assert.Equal(152.0, rects[1].W, 0.001);
        Assert.Equal(152.0, rects[2].W, 0.001);
        Assert.Equal(228.0, rects[3].W, 0.001);
        // The span cell's own right edge is the containment claim, not the first row's sum,
        // which is two of the three columns and cannot exceed the box while the widths above
        // hold. Row 1's span starts where its first column ends.
        Assert.Equal(380.0, rects[2].W + rects[3].W, 0.001);
    }

    /// <summary>
    /// The column count accumulates spans, so a caller can overflow the sum. It used to be a
    /// checked LINQ <c>Sum</c> and became a bare accumulation, which in this project's build is
    /// unchecked: the sum wrapped and a row silently lost a cell instead of the render refusing.
    /// The accumulation is checked again, and this pins that the refusal is loud.
    ///
    /// Two cells of <c>int.MaxValue</c> are used rather than one, because one does not overflow the
    /// sum and instead reaches the array allocation, which raises its own out-of-memory refusal for
    /// the array's dimension. Both are loud; only the wrap is this guard's business.
    /// </summary>
    [Fact]
    public void ColumnCount_spanSumOverflowing_refusesInsteadOfDroppingACell()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        var row = t.AddRow();
        row.AddCell(new Cell("a") { ColSpan = int.MaxValue });
        row.AddCell(new Cell("b") { ColSpan = int.MaxValue });
        doc.Add(t);

        Assert.Throws<OverflowException>(() => doc.Save(new MemoryStream()));
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
    ///
    /// The third entry is 500, not 100 (T4): with all three entries equal, a mutation that folds
    /// the ignored surplus entry into <c>explicitSum</c> scales both sides of the comparison by
    /// the same factor and the assertion still passes. Measured with a genuinely surplus 500: that
    /// mutation makes the three-entry side scale to 54.28571 per column against the expected 100,
    /// which is what this test is supposed to catch.
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

        Assert.Equal(RectWidths([100, 100]), RectWidths([100, 100, 500]));
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
    /// A non-finite explicit width is the fourth input that cannot be honoured, alongside a missing
    /// entry, an explicit zero and a negative width, and it is the only one whose old behaviour
    /// produced something that is not a PDF at all. Measured on eedaa3c, a 400x300pt page at 10pt
    /// margins with widths of NaN, 100 and 100: the first cell emitted <c>w=NaN</c>, positive
    /// infinity emitted <c>w=Infinity</c> and negative infinity <c>w=-Infinity</c>, and each also
    /// left the x operand of every following column non-numeric. The resolver now classifies all
    /// three as auto, so the row resolves to the same widths a zero array would give.
    ///
    /// This asserts the absence of the token rather than only the widths, because a width
    /// assertion alone would pass on a stream that still carried <c>NaN</c> somewhere else in it.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ExplicitWidths_nonFinite_resolveAsAutoAndEmitNoSuchToken(double first)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(first, 100, 100);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1"); row.AddCell("c2");
        doc.Add(t);

        var stream = RenderAndDecompress(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("\u221E", stream, StringComparison.Ordinal);

        var widths = CellRectangles(stream).Select(r => r.W).ToArray();
        Assert.Equal(3, widths.Length);
        Assert.All(widths, w => Assert.True(w > 0, $"a column resolved to {w}"));
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
    /// width, so the auto column's residual is zero and it falls back to its own content floor —
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

    /// <summary>
    /// T1: an explicit array that already fits pins absolute widths, not a comparison against a
    /// second array. <see cref="ExplicitWidths_surplusArray_thirdEntryIgnored"/> and
    /// <see cref="ExplicitWidths_negative_clampedToTheSameAutoWidthsAsZero"/> both compare two
    /// renders against each other, so a mutation that scales every fitting array up equally —
    /// removing the <c>total &lt;= available</c> guard from <c>ScaleToFit</c> — leaves both sides
    /// scaled the same amount and neither test fails. Measured with that guard removed: (100, 120,
    /// 90) on a 380pt available width becomes (122.58065, 147.09677, 110.32258) instead of staying
    /// at (100, 120, 90), which is what this test is supposed to catch.
    /// </summary>
    [Fact]
    public void ExplicitWidths_arrayThatFits_resolvesToExactWidths()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 200),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(100, 120, 90);
        var row = t.AddRow();
        row.AddCell("c0"); row.AddCell("c1"); row.AddCell("c2");
        doc.Add(t);

        var widths = CellRectangles(RenderAndDecompress(doc)).Select(r => r.W).ToArray();

        Assert.Equal(new[] { 100.0, 120.0, 90.0 }, widths);
    }

    /// <summary>
    /// C1: when the auto columns' own content floors alone already exceed the available width,
    /// scaling the explicit columns down to what is left over — <c>Math.Max(0.0, available -
    /// autoFloorSum)</c> — reaches a budget of zero and used to zero every explicit column while
    /// the auto column kept its unreduced floor. Page 400x300, margin 10 (available 380),
    /// <c>SetColumnWidths(200, 0)</c> — an explicit 200pt column and an auto one — with cells
    /// "keepme" and a 100-character run of "W" with no spaces, so the auto column's own floor (one
    /// unbreakable word) is far wider than the whole available width on its own. Both columns must
    /// stay above zero: the explicit column absorbs its share of the shortfall rather than
    /// vanishing, and the auto column's floor is scaled down rather than left untouched while the
    /// explicit column is crushed to nothing. This is also what catches a mutation that removes
    /// the final <c>ScaleToFit</c> call from <c>ReconcileExplicitWidths</c> entirely: without it,
    /// the row's total width is left unbounded above <paramref name="available"/> whenever the
    /// auto floors alone overrun it.
    /// </summary>
    [Fact]
    public void ExplicitWidths_bothSetsCannotBeSatisfied_neitherReachesZero()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 300),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(200, 0);
        var row = t.AddRow();
        row.AddCell("keepme"); row.AddCell(new string('W', 100));
        doc.Add(t);

        var rects = CellRectangles(RenderAndDecompress(doc));

        Assert.Equal(2, rects.Count);
        Assert.All(rects, r => Assert.True(r.W > 0, $"a column resolved to {r.W}"));
        Assert.True(rects.Sum(r => r.W) <= 380.0 + 0.001);
    }

    /// <summary>
    /// T1: the residual left for the auto columns — <c>Math.Max(0.0, available - explicitSum)</c>
    /// — is clamped before it reaches the one branch with no floor of its own: when every auto
    /// column's own max-content width is zero (an empty cell with zero padding, so it never
    /// contributes to <c>autoMaxTotal</c>), <c>result[i]</c> is assigned <c>residual / autoCount</c>
    /// directly, with no <c>Math.Max(minW[i], …)</c> to catch a negative value the way the other
    /// branch has. Page 200x200, margin 10 (available 180). Row 0 sets the column count to 2 with
    /// two ordinary cells; row 1 widens it to 3 with a third cell of empty content and zero
    /// padding, so column 2's own content floor is exactly 0 and never touched by any other row.
    /// <c>SetColumnWidths(500, 500)</c> leaves column 2 auto with an explicit sum (1000) far past
    /// the 180pt available width, so the residual without the clamp would be negative. The auto
    /// column's resolved width must not be negative.
    /// </summary>
    [Fact]
    public void ExplicitWidths_noAutoContentWithNegativeResidual_clampedNotNegative()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 200, 200),
            Margins = new EdgeInsets(10),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(500, 500);
        var r0 = t.AddRow();
        r0.AddCell("x"); r0.AddCell("y");
        var r1 = t.AddRow();
        r1.AddCell("a"); r1.AddCell("b");
        r1.AddCell(new Cell(string.Empty) { Padding = EdgeInsets.Zero });
        doc.Add(t);

        var rects = CellRectangles(RenderAndDecompress(doc));

        // Row 0 draws two cells (columns 0 and 1); row 1 draws three (columns 0, 1 and the auto
        // column 2), so the auto column's own rectangle is the fifth and last one in stream order.
        Assert.Equal(5, rects.Count);
        Assert.True(rects[4].W >= 0.0, $"the auto column resolved to {rects[4].W}");
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

    /// <summary>
    /// T5: the origin floor keeps a single over-wide rune's line inside the cell, but the comment
    /// ahead of it is careful to say only that — an accepted limit, not a claim that the glyph
    /// itself stays on the page. <see cref="CellText_oneRuneWiderThanTheColumn_startsAtTheCellLeftEdge"/>
    /// uses a 200pt page, wide enough that the glyph's own right edge never reaches the page edge,
    /// so it cannot see this. Page 120pt wide, margin 50 (a 20pt column at x 50), zero padding, one
    /// "W" at Helvetica 106pt. The origin still floors to the cell's own left edge at 50, and the
    /// glyph's own right edge — 50 plus its measured width — lands at 150.064, well past the 120pt
    /// page.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void CellText_oneRuneWiderThanTheColumn_glyphStillLeavesANarrowPage(HorizontalAlignment alignment)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 120, 600),
            Margins = new EdgeInsets(50),
        };
        var st = Style(106);
        var t = new TableElement { DefaultCellStyle = st };
        t.SetColumnWidths(20);
        t.AddRow().AddCell(new Cell("W") { Alignment = alignment, Padding = EdgeInsets.Zero });
        doc.Add(t);

        var placement = Assert.Single(ContentStreamReadback.TextPlacements(RenderAndDecompress(doc)));
        var glyphWidth = st.FontRef.MeasureString("W", 106);
        var rightEdge = placement.X + glyphWidth;

        Assert.Equal(50.0, placement.X, 0.001);
        Assert.Equal(150.064, rightEdge, 0.001);
        Assert.True(rightEdge > 120.0, "the glyph leaves this narrow a page even with the origin floored");
    }

    // ── (e) Cell text draw order and empty-cell edges ─────────────────────────

    /// <summary>
    /// T2: the comment ahead of the offset floor in <c>TableRenderer.DrawCell</c> says a single
    /// over-wide rune is the only way <c>innerBox.Width - lineW</c> goes negative. An empty cell
    /// whose explicit column is narrower than its own padding reaches the same floor with no rune
    /// at all. Page 400x400, margin 50 (content box [50, 350]), <c>SetColumnWidths(1, 200)</c> — a
    /// 1pt explicit column, well under its own 12pt horizontal padding — with an empty first cell.
    /// The inner width is 1 - 12 = -11, and the line is the empty string, so <c>lineW</c> is 0:
    /// without the floor, Centre would land at x 50.5 and Right at 45, both left of the cell's own
    /// inner edge at 56; with it, both floor to 56.
    /// </summary>
    [Theory]
    [InlineData(HorizontalAlignment.Center)]
    [InlineData(HorizontalAlignment.Right)]
    public void CellText_emptyCellNarrowerThanItsPadding_offsetFloorsAtTheInnerEdge(HorizontalAlignment alignment)
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 400),
            Margins = new EdgeInsets(50),
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(1, 200);
        var row = t.AddRow();
        row.AddCell(new Cell(string.Empty) { Alignment = alignment });
        row.AddCell("c1");
        doc.Add(t);

        var placement = Assert.Single(ContentStreamReadback.TextPlacements(RenderAndDecompress(doc)),
            p => p.Text.Length == 0);

        Assert.Equal(56.0, placement.X, 0.001);
    }

    /// <summary>
    /// C3: <c>HardBreakWord</c> used to be invoked from a <c>lineW == 0</c> test — a measured
    /// advance, not an emptiness flag — so a zero-advance character (Helvetica's DEL, U+007F) left
    /// the line buffer holding a pending, un-flushed line while <c>lineW</c> still read 0. The next
    /// over-wide word then believed it was starting a fresh line too, pushed its own hard-break
    /// fragments ahead of the pending one, and the pending line was flushed only at the very end —
    /// after content that followed it in the source. A 60pt box at Helvetica 20, cell content
    /// U+007F followed by a space and eight "A"s: before the fix this drew "AAAA", "AAAA", then
    /// U+007F last; guarding on <c>lineBuilder.Length == 0</c> instead — the same emptiness test
    /// <c>ParagraphRenderer</c> already uses — restores source order: U+007F, "AAAA", "AAAA".
    /// </summary>
    [Fact]
    public void CellText_zeroAdvanceCharacterBeforeAHardBreak_keepsSourceOrder()
    {
        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 400),
            Margins = new EdgeInsets(50),
        };
        var st = Style(20);
        var t = new TableElement { DefaultCellStyle = st };
        t.SetColumnWidths(60);
        // U+007F (DEL) built from its character code, not an inline escape: it has a zero
        // advance in Helvetica (measured: MeasureString(DEL, 20) == 0), which is what reaches
        // the buggy lineW == 0 branch. A real space separates it from the word that hard-breaks.
        var del = ((char)0x7F).ToString();
        t.AddRow().AddCell(new Cell(del + " " + new string('A', 8)) { Padding = EdgeInsets.Zero });
        doc.Add(t);

        var placements = ContentStreamReadback.TextPlacements(RenderAndDecompress(doc));

        Assert.Equal(3, placements.Count);
        Assert.Equal(del, placements[0].Text);
        Assert.Equal("AAAA", placements[1].Text);
        Assert.Equal("AAAA", placements[2].Text);
    }

    /// <summary>
    /// D2: hard-breaking a cell word that used to draw as a single over-wide line (#473) can make
    /// the row taller than the page has room for, so a document that rendered before this pull
    /// request now throws instead. This is a deliberate, disclosed consequence of measuring the
    /// cell correctly rather than a new validation rule — see the CHANGELOG entry this pins. Page
    /// 400x200, zero margins, one 100pt column with zero padding, one cell holding 300 "W"s at the
    /// default 10pt size: the hard-broken lines do not fit a 200pt page height.
    /// </summary>
    [Fact]
    public void CellText_hardBrokenRowTallerThanThePage_throwsElementTooTall()
    {
        const string elementTooTallMessage =
            "An element is too tall to fit on a single page and cannot be rendered. " +
            "Reduce the element's content or increase the page size.";

        using var doc = new Document
        {
            PageSize = new PdfRectangle(0, 0, 400, 200),
            Margins = EdgeInsets.Zero,
        };
        var t = new TableElement { DefaultCellStyle = Style() };
        t.SetColumnWidths(100);
        t.AddRow().AddCell(new Cell(new string('W', 300)) { Padding = EdgeInsets.Zero });
        doc.Add(t);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Equal(elementTooTallMessage, ex.Message);
    }
}
