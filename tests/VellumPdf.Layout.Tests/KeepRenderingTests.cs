// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// A release before 3.0 may not turn a document that saved a valid file on 1c61fa6 into an exception
/// (#484). A non-finite inset, indent, colour or band height is not checked before the layout, with
/// two exceptions that threw for every value there; the layout runs as it did and <c>PdfCanvas</c>
/// refuses what would be written as <c>NaN</c> or <c>Infinity</c>. See <see cref="Document"/>.
/// Every expected outcome in this class was measured on 1c61fa6 and pins its output: the drawn text,
/// the rectangle, image and curve counts, or the exception type. Where that output lost content, such
/// as a table that is not drawn or text that wraps after every word, the test pins a known defect that
/// is kept until 3.0 and reported by the diagnostics channel (#607).
/// </summary>
public sealed class KeepRenderingTests
{
    private static string Normalise(string text) => Regex.Replace(text, @"[^\u0020-\u007E]", "?");

    private static string Summary(Document doc)
    {
        var stream = Save(doc);
        if (stream.Contains("NaN", StringComparison.Ordinal) || stream.Contains("Infinity", StringComparison.Ordinal))
            return "INVALID";

        var texts = Regex.Matches(stream, @"\(([^)]*)\) Tj").Select(m => Normalise(m.Groups[1].Value));
        return $"SAVED {string.Join("|", texts)} re={Regex.Matches(stream, @"\bre\b").Count} " +
            $"Do={Regex.Matches(stream, @"\bDo\b").Count} c={Regex.Matches(stream, @"(?m) c$").Count}";
    }

    private static EdgeInsets Edge(string edge, double v) => edge switch
    {
        "top" => new EdgeInsets(v, 0, 0, 0),
        "right" => new EdgeInsets(0, v, 0, 0),
        "bottom" => new EdgeInsets(0, 0, v, 0),
        _ => new EdgeInsets(0, 0, 0, v),
    };

    private static double Value(string v) => v switch
    {
        "NaN" => double.NaN,
        "+inf" => double.PositiveInfinity,
        _ => double.NegativeInfinity,
    };

    // Measured on 1c61fa6: element, edge, value, then what it did. SAVED rows list the drawn text
    // (one entry per Tj, a non-ASCII byte shown as ?) and the re, Do and curve counts. INVALID rows
    // wrote NaN or Infinity into the stream. THREW rows threw that type.
    private static readonly (string Element, string Edge, string Value, string Main)[] _margins =
    [
        ("paragraph first", "top", "NaN", "THREW InvalidOperationException"),
        ("paragraph first", "top", "+inf", "THREW InvalidOperationException"),
        ("paragraph first", "top", "-inf", "INVALID"),
        ("paragraph first", "right", "NaN", "SAVED First|one|Hello world re=0 Do=0 c=0"),
        ("paragraph first", "right", "+inf", "THREW InvalidOperationException"),
        ("paragraph first", "right", "-inf", "SAVED First one|Hello world re=0 Do=0 c=0"),
        ("paragraph first", "bottom", "NaN", "THREW InvalidOperationException"),
        ("paragraph first", "bottom", "+inf", "THREW InvalidOperationException"),
        ("paragraph first", "bottom", "-inf", "SAVED First one|Hello world re=0 Do=0 c=0"),
        ("paragraph first", "left", "NaN", "INVALID"),
        ("paragraph first", "left", "+inf", "THREW InvalidOperationException"),
        ("paragraph first", "left", "-inf", "INVALID"),
        ("paragraph last", "top", "NaN", "THREW InvalidOperationException"),
        ("paragraph last", "top", "+inf", "THREW InvalidOperationException"),
        ("paragraph last", "top", "-inf", "INVALID"),
        ("paragraph last", "right", "NaN", "SAVED Hello world again|Last|one re=0 Do=0 c=0"),
        ("paragraph last", "right", "+inf", "THREW InvalidOperationException"),
        ("paragraph last", "right", "-inf", "SAVED Hello world again|Last one re=0 Do=0 c=0"),
        ("paragraph last", "bottom", "NaN", "THREW InvalidOperationException"),
        ("paragraph last", "bottom", "+inf", "THREW InvalidOperationException"),
        ("paragraph last", "bottom", "-inf", "SAVED Hello world again|Last one re=0 Do=0 c=0"),
        ("paragraph last", "left", "NaN", "INVALID"),
        ("paragraph last", "left", "+inf", "THREW InvalidOperationException"),
        ("paragraph last", "left", "-inf", "INVALID"),
        ("table", "top", "NaN", "INVALID"),
        ("table", "top", "+inf", "SAVED Hello re=0 Do=0 c=0"),
        ("table", "top", "-inf", "SAVED Hello re=0 Do=0 c=0"),
        ("table", "right", "NaN", "INVALID"),
        ("table", "right", "+inf", "THREW InvalidOperationException"),
        ("table", "right", "-inf", "INVALID"),
        ("table", "bottom", "NaN", "SAVED Hello|a re=1 Do=0 c=0"),
        ("table", "bottom", "+inf", "THREW InvalidOperationException"),
        ("table", "bottom", "-inf", "SAVED Hello|a re=1 Do=0 c=0"),
        ("table", "left", "NaN", "INVALID"),
        ("table", "left", "+inf", "THREW InvalidOperationException"),
        ("table", "left", "-inf", "INVALID"),
        ("list", "top", "NaN", "THREW InvalidOperationException"),
        ("list", "top", "+inf", "THREW InvalidOperationException"),
        ("list", "top", "-inf", "INVALID"),
        ("list", "right", "NaN", "SAVED Hello|?|item re=0 Do=0 c=0"),
        ("list", "right", "+inf", "THREW InvalidOperationException"),
        ("list", "right", "-inf", "SAVED Hello|?|item re=0 Do=0 c=0"),
        ("list", "bottom", "NaN", "THREW InvalidOperationException"),
        ("list", "bottom", "+inf", "THREW InvalidOperationException"),
        ("list", "bottom", "-inf", "SAVED Hello|?|item re=0 Do=0 c=0"),
        ("list", "left", "NaN", "INVALID"),
        ("list", "left", "+inf", "THREW InvalidOperationException"),
        ("list", "left", "-inf", "INVALID"),
        ("pie", "top", "NaN", "INVALID"),
        ("pie", "top", "+inf", "THREW InvalidOperationException"),
        ("pie", "top", "-inf", "INVALID"),
        ("pie", "right", "NaN", "INVALID"),
        ("pie", "right", "+inf", "SAVED Hello re=0 Do=0 c=4"),
        ("pie", "right", "-inf", "SAVED Hello re=0 Do=0 c=4"),
        ("pie", "bottom", "NaN", "INVALID"),
        ("pie", "bottom", "+inf", "THREW InvalidOperationException"),
        ("pie", "bottom", "-inf", "INVALID"),
        ("pie", "left", "NaN", "INVALID"),
        ("pie", "left", "+inf", "INVALID"),
        ("pie", "left", "-inf", "INVALID"),
        ("image", "top", "NaN", "INVALID"),
        ("image", "top", "+inf", "THREW InvalidOperationException"),
        ("image", "top", "-inf", "INVALID"),
        ("image", "right", "NaN", "SAVED Hello re=0 Do=1 c=0"),
        ("image", "right", "+inf", "SAVED Hello re=0 Do=1 c=0"),
        ("image", "right", "-inf", "SAVED Hello re=0 Do=1 c=0"),
        ("image", "bottom", "NaN", "INVALID"),
        ("image", "bottom", "+inf", "THREW InvalidOperationException"),
        ("image", "bottom", "-inf", "INVALID"),
        ("image", "left", "NaN", "INVALID"),
        ("image", "left", "+inf", "SAVED Hello re=0 Do=1 c=0"),
        ("image", "left", "-inf", "SAVED Hello re=0 Do=1 c=0"),
    ];

    public static TheoryData<string, string, string, string> MarginCases()
    {
        var rows = new TheoryData<string, string, string, string>();
        foreach (var (element, edge, value, main) in _margins) rows.Add(element, edge, value, main);
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

    /// <summary>
    /// 72 shapes: six elements, four edges, three values. What 1c61fa6 saved, this saves with the same
    /// drawn output. What it wrote as an invalid token throws from <c>PdfCanvas</c> with
    /// <see cref="ArgumentException"/>. What it threw, it throws, with the same exception type.
    /// </summary>
    [Theory]
    [MemberData(nameof(MarginCases))]
    public void ElementMargin_nonFinite_gives1c61fa6Output(string element, string edge, string value, string main)
    {
        using var doc = Build(element, Edge(edge, Value(value)));

        if (main.StartsWith("SAVED", StringComparison.Ordinal))
        {
            Assert.Equal(main, Summary(doc));
        }
        else if (main == "INVALID")
        {
            var ex = Assert.Throws<ArgumentException>(() => Save(doc));
            Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            var type = main["THREW ".Length..];
            var ex = Assert.ThrowsAny<Exception>(() => Save(doc));
            Assert.Equal(type, ex.GetType().Name);
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

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Header_withAnEmptyTemplateAndANanColour_saves()
    {
        using var doc = new Document();
        doc.Header = new RunningBand(string.Empty, new TextStyle { Color = _nan });
        doc.Add(new Paragraph("Hello"));

        using var control = new Document();
        control.Header = new RunningBand(string.Empty);
        control.Add(new Paragraph("Hello"));
        Assert.Equal(Summary(control), Summary(doc));
    }

    [Fact]
    public void Header_withTextAndANanColour_isStillRefused()
    {
        using var doc = new Document();
        doc.Header = new RunningBand("Title", new TextStyle { Color = _nan });
        doc.Add(new Paragraph("Hello"));

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
    }

    // ── Elements that write nothing ──────────────────────────────────────────

    public static TheoryData<string, string, double> EmptyElementCases()
    {
        var rows = new TheoryData<string, string, double>();
        foreach (var element in new[] { "list margins", "list indent", "paragraph empty", "paragraph white space", "heading empty" })
        {
            foreach (var (edge, v) in new[] { ("left", double.NaN), ("left", double.NegativeInfinity), ("top", double.NegativeInfinity) })
            {
                // An empty heading still writes its bookmark position through PdfReal, so a
                // negative-infinity top margin throws on 1c61fa6 and here.
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

        using var control = new Document();
        control.Add(new Paragraph("Hello"));

        // The same output as the document without the element that draws nothing.
        Assert.Equal(Summary(control), Summary(doc));
    }

    [Fact]
    public void Paragraph_withDrawableTextAndANanLeftMargin_isStillRefused()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("Hello") { Margins = Edge("left", double.NaN) });

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// U+00A0 counts as drawable text: 1c61fa6 wrote <c>1 0 0 1 NaN 743.49 Tm</c> for it, so the
    /// canvas refuses it, where plain white space draws nothing and saves.
    /// </summary>
    [Theory]
    [InlineData(" ", true)]
    [InlineData("   ", false)]
    public void Paragraph_withANanLeftMargin_isRefusedOnlyWhenItDrawsSomething(string text, bool refused)
    {
        using var doc = new Document();
        doc.Add(new Paragraph(text) { Margins = Edge("left", double.NaN) });

        if (refused)
        {
            var ex = Assert.Throws<ArgumentException>(() => Save(doc));
            Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("NaN", Save(doc), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Footer_withAnEmptyTemplateAndANegativeInfinityHeight_saves()
    {
        using var doc = new Document();
        doc.Footer = new RunningBand(string.Empty) { Height = double.NegativeInfinity };
        doc.Add(new Paragraph("Hello"));

        var stream = Save(doc);
        Assert.Contains("(Hello) Tj", stream, StringComparison.Ordinal);
        Assert.DoesNotContain("Infinity", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void Footer_withTextAndANegativeInfinityHeight_isStillRefused()
    {
        using var doc = new Document();
        doc.Footer = new RunningBand("Page {page}") { Height = double.NegativeInfinity };
        doc.Add(new Paragraph("Hello"));

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));
        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
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

        using var control = new Document();
        control.Add(CellPastTheLastColumn(new Cell("d")));

        // Drawn as it is with no background colour at all; the pushed-past cell is not placed.
        var summary = Summary(doc);
        Assert.Equal(Summary(control), summary);
        Assert.DoesNotContain("|d|", "|" + summary + "|", StringComparison.Ordinal);
    }

    [Fact]
    public void TableCell_thatIsDrawn_withANanBackground_isStillRefused()
    {
        using var doc = new Document();
        var table = new TableElement();
        table.AddRow().AddCell(new Cell("a") { Background = _nan });
        doc.Add(table);

        var ex = Assert.Throws<ArgumentException>(() => Save(doc));

        Assert.StartsWith("PDF does not support NaN or Infinity as a real number value", ex.Message, StringComparison.Ordinal);
    }
}
