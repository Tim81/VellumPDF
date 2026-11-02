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
            rows.Add(edge, double.NaN, "NaN");
            rows.Add(edge, double.PositiveInfinity, "positive infinity");
            rows.Add(edge, double.NegativeInfinity, "negative infinity");
        }
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
