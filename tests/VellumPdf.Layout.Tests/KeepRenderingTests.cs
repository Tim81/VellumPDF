// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A release before 3.0 may not turn a document that saved a valid file on 1c61fa6 into an exception
/// (#484). Each case here saved with no NaN or Infinity token on that version and keeps saving; the
/// other combinations of the same inputs wrote an invalid token or threw there, and stay refused.
/// The expected outcomes were measured against 1c61fa6, not derived from the code.
/// </summary>
public sealed class KeepRenderingTests
{
    private static readonly double[] _nonFinite = [double.NaN, double.PositiveInfinity, double.NegativeInfinity];
    private static readonly string[] _edges = ["top", "right", "bottom", "left"];
    private static readonly string[] _elements = ["paragraph first", "paragraph last", "table",
    "list", "pie", "image"];

    private static string Kind(double v) => double.IsNaN(v) ? "NaN" : v > 0 ? "+inf" : "-inf";

    private static EdgeInsets Edge(string edge, double v) => edge switch
    {
        "top" => new EdgeInsets(v, 0, 0, 0),
        "right" => new EdgeInsets(0, v, 0, 0),
        "bottom" => new EdgeInsets(0, 0, v, 0),
        _ => new EdgeInsets(0, 0, 0, v),
    };

    /// <summary>
    /// The combinations that saved clean on 1c61fa6, as (element, edge, value). A paragraph was
    /// measured first and last in the document. Image margins were measured separately with the same
    /// probe shape: a 2 by 2 image after one paragraph.
    /// </summary>
    private static bool SavedClean(string element, string edge, double v)
    {
        var nan = double.IsNaN(v);
        var neg = double.IsNegativeInfinity(v);
        var pos = double.IsPositiveInfinity(v);
        return element switch
        {
            "paragraph first" or "paragraph last" or "list" =>
                (edge == "right" && (nan || neg)) || (edge == "bottom" && neg),
            "table" => (edge == "top" && (pos || neg)) || (edge == "bottom" && (nan || neg)),
            "pie" => edge == "right" && (pos || neg),
            "image" => (edge == "right") || (edge == "left" && (pos || neg)),
            _ => false,
        };
    }

    public static TheoryData<string, string, double> MarginCases()
    {
        var rows = new TheoryData<string, string, double>();
        foreach (var element in _elements)
            foreach (var edge in _edges)
                foreach (var v in _nonFinite)
                    rows.Add(element, edge, v);
        return rows;
    }

    private static Document Build(string element, EdgeInsets margins)
    {
        var doc = new Document();
        switch (element)
        {
            case "paragraph first":
                doc.Add(new Paragraph("First one") { Margins = margins });
                doc.Add(new Paragraph("Hello world"));
                break;
            case "paragraph last":
                doc.Add(new Paragraph("Hello world again"));
                doc.Add(new Paragraph("Last one") { Margins = margins });
                break;
            case "table":
                doc.Add(new Paragraph("Hello"));
                var t = new TableElement { Margins = margins };
                t.AddRow().AddCell("a");
                doc.Add(t);
                break;
            case "list":
                doc.Add(new Paragraph("Hello"));
                doc.Add(new ListElement { Margins = margins }.Add("item"));
                break;
            case "image":
                doc.Add(new Paragraph("Hello"));
                doc.Add(new LayoutImage(RefusalMessageTests.TwoByTwoImage()) { Margins = margins, Width = 20 });
                break;
            default:
                doc.Add(new Paragraph("Hello"));
                doc.Add(new PieChart { Margins = margins, Slices = [new PieSlice(1, ColorRgb.Black)] });
                break;
        }
        return doc;
    }

    private static string Save(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());
    }

    [Theory]
    [MemberData(nameof(MarginCases))]
    public void ElementMargin_nonFinite_savesCleanWhereItDidAndIsRefusedWhereItDidNot(string element, string edge, double v)
    {
        using var doc = Build(element, Edge(edge, v));

        if (SavedClean(element, edge, v))
        {
            var stream = Save(doc);
            Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
            Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
        }
        else
        {
            // Refused from the element's own check, from the too-tall path or from PdfCanvas; each
            // message says which.
            var ex = Assert.ThrowsAny<Exception>(() => Save(doc));
            Assert.True(
                ex is InvalidOperationException or ArgumentException,
                $"{element} {edge} {v}: unexpected {ex.GetType().Name}: {ex.Message}");
            Assert.Matches(
                "non-finite inset|Pie chart margins|too tall|PDF does not support NaN or Infinity|did not converge|single element",
                ex.Message);
        }
    }

    // ── Colours that are not written ─────────────────────────────────────────

    private static readonly ColorRgb _nan = new(double.NaN, 0, 0);

    [Fact]
    public void PieChart_zeroValueSliceWithANanColour_saves_andNoWedgeIsDrawnForIt()
    {
        using var doc = new Document();
        doc.Add(new PieChart { Slices = [new PieSlice(1, new ColorRgb(0, 0, 1)), new PieSlice(1, new ColorRgb(0, 1, 0)), new PieSlice(0, _nan)] });

        var stream = Save(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);

        // The zero-value slice draws no wedge: the chart is the one without it.
        using var without = new Document();
        without.Add(new PieChart { Slices = [new PieSlice(1, new ColorRgb(0, 0, 1)), new PieSlice(1, new ColorRgb(0, 1, 0))] });
        Assert.Equal(Save(without), stream);
    }

    [Fact]
    public void PieChart_soleDrawableSlice_withAZeroValueNanSliceBesideIt_saves()
    {
        using var doc = new Document();
        doc.Add(new PieChart { Slices = [new PieSlice(1, new ColorRgb(0, 0, 1)), new PieSlice(0, _nan)] });

        Assert.DoesNotContain("NaN", Save(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void PieChart_aDrawnSliceWithANanColour_isStillRefused()
    {
        using var doc = new Document();
        doc.Add(new PieChart { Slices = [new PieSlice(1, _nan)] });

        Assert.Throws<ArgumentException>(() => Save(doc));
    }

    [Fact]
    public void Header_withAnEmptyTemplateAndANanColour_saves()
    {
        using var doc = new Document();
        doc.Header = new RunningBand(string.Empty, new TextStyle { Color = _nan });
        doc.Add(new Paragraph("Hello"));

        Assert.DoesNotContain("NaN", Save(doc), StringComparison.Ordinal);
    }

    [Fact]
    public void Header_withTextAndANanColour_isStillRefused()
    {
        using var doc = new Document();
        doc.Header = new RunningBand("Title", new TextStyle { Color = _nan });
        doc.Add(new Paragraph("Hello"));

        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    // ── Elements that write nothing ──────────────────────────────────────────

    public static TheoryData<string, string, double> EmptyElementCases()
    {
        var rows = new TheoryData<string, string, double>();
        foreach (var element in new[] { "list margins", "list indent", "paragraph empty", "paragraph white space", "heading empty" })
        {
            foreach (var (edge, v) in new[] { ("left", double.NaN), ("left", double.NegativeInfinity), ("top", double.NegativeInfinity) })
            {
                // An empty heading still writes its bookmark position, so a negative-infinity top
                // margin reaches PdfCanvas and throws on 1c61fa6 and here.
                if (element == "heading empty" && edge == "top") continue;
                rows.Add(element, edge, v);
            }
        }
        rows.Add("list margins", "top", double.NaN);
        rows.Add("list margins", "bottom", double.NaN);
        rows.Add("list indent", "none", double.NaN);
        return rows;
    }

    /// <summary>
    /// An element that draws nothing writes none of its insets, so a non-finite one saved a valid file
    /// on 1c61fa6 and keeps saving. Each row was measured there.
    /// </summary>
    [Theory]
    [MemberData(nameof(EmptyElementCases))]
    public void ElementThatDrawsNothing_savesWithANonFiniteInset(string element, string edge, double v)
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello"));
        var margins = edge == "none" ? EdgeInsets.Zero : Edge(edge, v);
        switch (element)
        {
            case "list margins": doc.Add(new ListElement { Margins = margins }); break;
            case "list indent": doc.Add(new ListElement { Indent = v }); break;
            case "paragraph empty": doc.Add(new Paragraph("") { Margins = margins }); break;
            case "paragraph white space": doc.Add(new Paragraph("   ") { Margins = margins }); break;
            default: doc.Add(new Heading("") { Margins = margins }); break;
        }

        var stream = Save(doc);

        Assert.Contains("(Hello)", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void Paragraph_withDrawableTextAndANanLeftMargin_isStillRefused()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello") { Margins = Edge("left", double.NaN) });

        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    [Fact]
    public void Footer_withAnEmptyTemplateAndANegativeInfinityHeight_saves()
    {
        using var doc = new Document();
        doc.Footer = new RunningBand(string.Empty) { Height = double.NegativeInfinity };
        doc.Add(new Paragraph("Hello"));

        var stream = Save(doc);

        Assert.Contains("(Hello)", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void Footer_withTextAndANegativeInfinityHeight_isStillRefused()
    {
        using var doc = new Document();
        doc.Footer = new RunningBand("Page {page}") { Height = double.NegativeInfinity };
        doc.Add(new Paragraph("Hello"));

        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    // ── A table cell that is never placed ────────────────────────────────────

    private static TableElement CellPastTheLastColumn(Cell last)
    {
        var table = new TableElement();
        var first = table.AddRow();
        first.AddCell(new Cell("a") { RowSpan = 2 });
        first.AddCell("b");
        var second = table.AddRow();
        second.AddCell("c");
        second.AddCell(last);
        return table;
    }

    [Fact]
    public void TableCell_pastTheLastColumn_withANanBackground_saves_becauseItIsNeverDrawn()
    {
        using var doc = new Document();
        doc.Add(CellPastTheLastColumn(new Cell("d") { Background = _nan }));

        var stream = Save(doc);

        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("(d)", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void TableCell_thatIsDrawn_withANanBackground_isStillRefused()
    {
        using var doc = new Document();
        var table = new TableElement();
        table.AddRow().AddCell(new Cell("a") { Background = _nan });
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith("Table row 0, cell 0 has a background colour whose red channel is NaN", ex.Message, StringComparison.Ordinal);
    }
}
