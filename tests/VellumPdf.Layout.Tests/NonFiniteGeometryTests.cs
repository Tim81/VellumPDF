// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #502 and #520: a document margin or a running band height that is not a finite number. Both
/// were checked by a comparison, and a comparison against <c>NaN</c> is false, so the three
/// non-finite values took three different routes and surfaced as messages about something else.
/// Every value is listed on its own, for every edge and for both bands, because a test
/// parameterised on "a non-finite value" reproduces neither the defect nor its absence.
/// </summary>
public sealed class NonFiniteGeometryTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private static Document Doc(EdgeInsets margins) => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = margins,
    };

    public static TheoryData<int, double, string> Insets()
    {
        var rows = new TheoryData<int, double, string>();
        foreach (var edge in new[] { 0, 1, 2, 3 })
        {
            foreach (var (value, rendered) in new[]
            {
                (double.NaN, "NaN"),
                (double.PositiveInfinity, "positive infinity"),
                (double.NegativeInfinity, "negative infinity"),
            })
            {
                // Three combinations saved a valid file on 1c61fa6 and keep saving; see
                // Margin_thatSavedCleanBefore_stillSaves.
                if (SavesClean(edge, value)) continue;
                rows.Add(edge, value, rendered);
            }
        }
        return rows;
    }

    /// <summary>Edge 1 is top, 2 is right and 3 is bottom, in the order <see cref="Margins"/>
    /// uses: left, top, right, bottom.</summary>
    private static bool SavesClean(int edge, double value) =>
        (edge == 1 && double.IsNegativeInfinity(value)) ||
        (edge == 2 && (double.IsNaN(value) || double.IsNegativeInfinity(value))) ||
        (edge == 3 && (double.IsNaN(value) || double.IsNegativeInfinity(value)));

    public static TheoryData<int, double> SavedCleanBefore()
    {
        var rows = new TheoryData<int, double>();
        foreach (var edge in new[] { 1, 2, 3 })
            foreach (var value in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
                if (SavesClean(edge, value)) rows.Add(edge, value);
        return rows;
    }

    private static readonly string[] _edgeNames = ["left", "top", "right", "bottom"];

    private static EdgeInsets Margins(int edge, double value)
    {
        double l = 10, t = 10, r = 10, b = 10;
        switch (edge)
        {
            case 0: l = value; break;
            case 1: t = value; break;
            case 2: r = value; break;
            default: b = value; break;
        }
        // EdgeInsets takes (Top, Right, Bottom, Left).
        return new EdgeInsets(t, r, b, l);
    }

    private static void Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
    }

    // ── #502: Document.Margins ───────────────────────────────────────────────

    /// <summary>
    /// Measured on 1c61fa6: a negative-infinity top margin, a NaN or negative-infinity right margin
    /// and a NaN or negative-infinity bottom margin saved a file with no NaN or Infinity token (with
    /// a one-row table for the top and the NaN bottom, a paragraph for the rest), so they keep saving
    /// (#484). The other combinations are refused by the theory
    /// below.
    /// </summary>
    [Theory]
    [MemberData(nameof(SavedCleanBefore))]
    public void Margin_thatSavedCleanBefore_stillSaves(int edge, double value)
    {
        using var doc = Doc(Margins(edge, value));

        // A paragraph under a NaN bottom or negative-infinity top margin was refused as too tall on
        // 1c61fa6 and still is; a table saved there, so it is the body for those two.
        if ((edge == 1 && double.IsNegativeInfinity(value)) || (edge == 3 && double.IsNaN(value)))
        {
            var table = new TableElement { DefaultCellStyle = Style() };
            table.AddRow().AddCell("x");
            doc.Add(table);
        }
        else
        {
            doc.Add(new Paragraph("x", Style()));
        }

        using var ms = new MemoryStream();

        doc.Save(ms);

        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
        // A negative-infinity top margin puts the table off the page: it saved a page with no text
        // on 1c61fa6, so there is nothing to find for that row.
        if (!(edge == 1 && double.IsNegativeInfinity(value)))
            Assert.Contains("(x)", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Insets))]
    public void Margin_nonFinite_isRefusedAndNamesTheEdge(int edge, double value, string rendered)
    {
        using var doc = Doc(Margins(edge, value));
        doc.Add(new Paragraph("x", Style()));

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));

        Assert.Equal(
            $"The {_edgeNames[edge]} margin is {rendered}, which cannot be laid out. " +
            "A margin must be a finite number. (Parameter 'margins')",
            ex.Message);
        Assert.Equal("margins", ex.ParamName);
    }

    /// <summary>
    /// The element no longer decides the outcome. The table in #502 showed a paragraph, a table, an
    /// image and a separator each reaching a different message, or saving with invalid tokens.
    /// </summary>
    [Theory]
    [InlineData("paragraph")]
    [InlineData("table")]
    [InlineData("separator")]
    [InlineData("pie")]
    public void Margin_nonFinite_isRefusedWhateverTheElement(string element)
    {
        using var doc = Doc(new EdgeInsets(double.NaN, 10, 10, 10));
        switch (element)
        {
            case "paragraph": doc.Add(new Paragraph("x", Style())); break;
            case "table":
                var table = new TableElement { DefaultCellStyle = Style() };
                table.AddRow().AddCell("x");
                doc.Add(table);
                break;
            case "separator": doc.Add(new LineSeparator()); break;
            default: doc.Add(new PieChart { Slices = [new PieSlice(1, ColorRgb.Black)] }); break;
        }

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.Equal("margins", ex.ParamName);
        Assert.StartsWith("The top margin is NaN", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Left alone on purpose: a negative finite margin saves today and a reader clips what lands
    /// outside the page, so refusing it would turn a rendering document into an exception.
    /// </summary>
    [Fact]
    public void Margin_negativeFinite_stillSaves()
    {
        using var doc = Doc(new EdgeInsets(-72));
        doc.Add(new Paragraph("x", Style()));

        var ex = Record.Exception(() => Save(doc));

        Assert.Null(ex);
    }

    // ── #520: RunningBand.Height ─────────────────────────────────────────────

    public static TheoryData<bool, double, string> Heights()
    {
        var rows = new TheoryData<bool, double, string>();
        foreach (var header in new[] { true, false })
        {
            rows.Add(header, double.NaN, "NaN");
            rows.Add(header, double.PositiveInfinity, "positive infinity");
            rows.Add(header, double.NegativeInfinity, "negative infinity");
        }
        return rows;
    }

    /// <summary>
    /// Six cases, not three. The header and the footer took different routes, and a footer at
    /// negative infinity was the one that saved a file, writing <c>NaN</c> into a text matrix.
    /// </summary>
    [Theory]
    [MemberData(nameof(Heights))]
    public void BandHeight_nonFinite_isRefusedForBothBandsAndWritesNothing(bool header, double value, string rendered)
    {
        using var doc = Doc(new EdgeInsets(10));
        var band = new RunningBand("page {page}", Style()) { Height = value };
        if (header) doc.Header = band; else doc.Footer = band;
        doc.Add(new Paragraph("body", Style()));

        using var ms = new MemoryStream();
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal(
            $"The {(header ? "header" : "footer")} band has a height of {rendered}, which cannot be laid out. " +
            "A band height must be a finite number.",
            ex.Message);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void BandHeight_finite_isDrawnAsBefore()
    {
        using var doc = Doc(new EdgeInsets(10));
        doc.Footer = new RunningBand("page {page}", Style()) { Height = 20 };
        doc.Add(new Paragraph("body", Style()));

        using var ms = new MemoryStream();
        doc.Save(ms);
        var stream = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.Contains("(page 1) Tj", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
    }
}
