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
    private static readonly string[] _elements = ["paragraph first", "paragraph last", "table", "list", "pie", "image"];

    private static string Kind(double v) => double.IsNaN(v) ? "NaN" : v > 0 ? "+inf" : "-inf";

    private static EdgeInsets Edge(string edge, double v) => edge switch
    {
        "top" => new EdgeInsets(v, 0, 0, 0),
        "right" => new EdgeInsets(0, v, 0, 0),
        "bottom" => new EdgeInsets(0, 0, v, 0),
        _ => new EdgeInsets(0, 0, 0, v),
    };

    /// <summary>
    /// The 20 of 72 combinations that saved clean on 1c61fa6, as (element, edge, value). Paragraph
    /// counts twice because it was measured first and last in the document. Image margins were
    /// measured separately with the same probe shape: a 2 by 2 image after one paragraph.
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
            Assert.ThrowsAny<Exception>(() => Save(doc));
        }
    }

    [Fact]
    public void ElementMarginTable_holdsTwentyOfSeventyTwoCombinations()
    {
        var saves = 0;
        var total = 0;
        foreach (var element in _elements)
            foreach (var edge in _edges)
                foreach (var v in _nonFinite)
                {
                    total++;
                    if (SavedClean(element, edge, v)) saves++;
                }

        Assert.Equal(20, saves);
        Assert.Equal(72, total);
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
}
