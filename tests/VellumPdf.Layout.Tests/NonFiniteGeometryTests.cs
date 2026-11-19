// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>A document margin or running band height that is not a finite number is not checked before the layout; the layout runs and <c>PdfCanvas</c> refuses what it would write as <c>NaN</c> or <c>Infinity</c> (see <see cref="Document"/>), and each row is 2.3.3's outcome for the shape named, including the blank page a negative-infinity top margin gives a table.</summary>
public sealed class NonFiniteGeometryTests
{
    private static TextStyle Style() => new() { FontRef = new FontReference(Standard14.Helvetica), FontSize = 10 };

    private enum Outcome
    {
        /// <summary>Saved, and the cell text was drawn.</summary>
        Draws,

        /// <summary>Saved, and nothing was drawn.</summary>
        Blank,

        /// <summary>2.3.3 wrote an invalid token; the canvas throws <see cref="ArgumentException"/>.</summary>
        Canvas,

        /// <summary>The margin sums against the page are refused, as in 2.3.3.</summary>
        MarginSum,
    }

    private static EdgeInsets Edge(string edge, double v) => edge switch
    {
        "top" => new EdgeInsets(v, 10, 10, 10),
        "right" => new EdgeInsets(10, v, 10, 10),
        "bottom" => new EdgeInsets(10, 10, v, 10),
        _ => new EdgeInsets(10, 10, 10, v),
    };

    private static readonly double NaN = double.NaN;
    private static readonly double PosInf = double.PositiveInfinity;
    private static readonly double NegInf = double.NegativeInfinity;

    // Document margin, one-row table "a" on an A4 page; outcomes are 2.3.3's.
    private static readonly (string Edge, double Value, Outcome Expected)[] _documentMargins =
    [
        ("top", NaN, Outcome.Canvas), ("top", PosInf, Outcome.MarginSum), ("top", NegInf, Outcome.Blank),
        ("right", NaN, Outcome.Canvas), ("right", PosInf, Outcome.MarginSum), ("right", NegInf, Outcome.Canvas),
        ("bottom", NaN, Outcome.Draws), ("bottom", PosInf, Outcome.MarginSum), ("bottom", NegInf, Outcome.Draws),
        ("left", NaN, Outcome.Canvas), ("left", PosInf, Outcome.MarginSum), ("left", NegInf, Outcome.Canvas),
    ];

    public static TheoryData<string, double, string> DocumentMarginRows()
    {
        var rows = new TheoryData<string, double, string>();
        foreach (var (edge, value, expected) in _documentMargins) rows.Add(edge, value, expected.ToString());
        return rows;
    }

    private static string Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    [Theory]
    [MemberData(nameof(DocumentMarginRows))]
    public void DocumentMargin_nonFinite_givesWhat1c61fa6Gave(string edge, double value, string expectedName)
    {
        var expected = Enum.Parse<Outcome>(expectedName);
        using var doc = new Document { Margins = Edge(edge, value) };
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell("a");
        doc.Add(table);

        switch (expected)
        {
            case Outcome.Draws:
            case Outcome.Blank:
                var stream = Save(doc);
                Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
                Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
                Assert.Equal(expected == Outcome.Draws, stream.Contains("(a) Tj", StringComparison.Ordinal));
                break;
            case Outcome.Canvas:
                var canvas = Assert.Throws<ArgumentException>(() => Save(doc));
                Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", canvas.Message, StringComparison.Ordinal);
                break;
            default:
                var sum = Assert.Throws<ArgumentException>(() => Save(doc));
                Assert.Equal("margins", sum.ParamName);
                Assert.Contains("meet or exceed", sum.Message, StringComparison.Ordinal);
                break;
        }
    }

    /// <summary>A negative finite margin saves and a reader clips what lands outside the page, so it is not refused.</summary>
    [Fact]
    public void Margin_negativeFinite_stillSaves()
    {
        using var doc = new Document { Margins = new EdgeInsets(-72) };
        doc.Add(new Paragraph("x", Style()));

        var ex = Record.Exception(() => Save(doc));

        Assert.Null(ex);
    }

    // ── RunningBand.Height ───────────────────────────────────────────────────

    private enum BandOutcome
    {
        /// <summary>Saved with the body drawn.</summary>
        Saves,

        /// <summary>Refused as too tall to fit, as in 2.3.3.</summary>
        TooTall,

        /// <summary>Gives up after 50,000 page continuations, as in 2.3.3.</summary>
        Continuations,

        /// <summary>2.3.3 wrote a NaN text matrix; the canvas throws.</summary>
        Canvas,

        /// <summary>Refused with the content area check, as in 2.3.3.</summary>
        ContentArea,
    }

    // A one-paragraph body "x" on an A4 page; outcomes are 2.3.3's.
    private static readonly (string Band, string Template, double Value, BandOutcome Expected)[] _bands =
    [
        ("header", "H", double.NaN, BandOutcome.TooTall),
        ("footer", "F", double.NaN, BandOutcome.TooTall),
        ("footer", "", double.NaN, BandOutcome.TooTall),
        ("header", "H", double.PositiveInfinity, BandOutcome.ContentArea),
        ("footer", "F", double.PositiveInfinity, BandOutcome.ContentArea),
        ("footer", "", double.PositiveInfinity, BandOutcome.ContentArea),
        ("header", "H", double.NegativeInfinity, BandOutcome.Continuations),
        ("footer", "F", double.NegativeInfinity, BandOutcome.Canvas),
        ("footer", "", double.NegativeInfinity, BandOutcome.Saves),
    ];

    public static TheoryData<string, string, double, string> BandRows()
    {
        var rows = new TheoryData<string, string, double, string>();
        foreach (var (band, template, value, expected) in _bands) rows.Add(band, template, value, expected.ToString());
        return rows;
    }

    [Theory]
    [MemberData(nameof(BandRows))]
    public void BandHeight_nonFinite_givesWhat1c61fa6Gave(string band, string template, double value, string expectedName)
    {
        var expected = Enum.Parse<BandOutcome>(expectedName);
        using var doc = new Document();
        var running = new RunningBand(template, Style()) { Height = value };
        if (band == "header") doc.Header = running; else doc.Footer = running;
        doc.Add(new Paragraph("x", Style()));

        switch (expected)
        {
            case BandOutcome.Saves:
                var stream = Save(doc);
                Assert.Contains("(x) Tj", stream, StringComparison.Ordinal);
                Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
                Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
                break;
            case BandOutcome.Canvas:
                var canvas = Assert.Throws<ArgumentException>(() => Save(doc));
                Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", canvas.Message, StringComparison.Ordinal);
                break;
            case BandOutcome.ContentArea:
                var area = Assert.Throws<ArgumentException>(() => Save(doc));
                Assert.Equal("margins", area.ParamName);
                Assert.StartsWith("The computed content area has no positive size", area.Message, StringComparison.Ordinal);
                break;
            case BandOutcome.TooTall:
                var tall = Assert.Throws<InvalidOperationException>(() => Save(doc));
                Assert.StartsWith("An element is too tall to fit on a single page", tall.Message, StringComparison.Ordinal);
                break;
            default:
                var loop = Assert.Throws<InvalidOperationException>(() => Save(doc));
                Assert.StartsWith("A single element needed more than 50000 page continuations", loop.Message, StringComparison.Ordinal);
                break;
        }
    }

    [Fact]
    public void BandHeight_finite_isDrawnAsBefore()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 300, 400), Margins = new EdgeInsets(10) };
        doc.Footer = new RunningBand("page {page}", Style()) { Height = 20 };
        doc.Add(new Paragraph("body", Style()));

        var stream = Save(doc);

        Assert.Contains("(page 1) Tj", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
    }
}
