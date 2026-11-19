// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>Non-finite insets, indents, colours and band heights saved on 2.3.3 unless <c>PdfCanvas</c> refused them, and every expected outcome here is 2.3.3's: the drawn text, the rectangle, image and curve counts, or the exception type.</summary>
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

    // 2.3.3's outcome per element, edge and value. SAVED rows list the drawn text (one entry per Tj, a non-ASCII byte shown as ?)
    // and the re, Do and curve counts; INVALID rows wrote NaN or Infinity into the stream; THREW rows threw that type.
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

    /// <summary>72 shapes: six elements (first and last paragraph, table, list, image, pie chart), four edges, three values; each saves with 2.3.3's drawn output, throws <see cref="ArgumentException"/> from <c>PdfCanvas</c> where 2.3.3 wrote an invalid token, or throws 2.3.3's exception type.</summary>
    [Theory]
    [MemberData(nameof(MarginCases))]
    public void ElementMargin_nonFinite_matchesTheRecordedOutcomePerShape(string element, string edge, string value, string main)
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
                // An empty heading still writes its bookmark position through PdfReal, so a negative-infinity top margin throws.
                if (element == "heading empty" && edge == "top") continue;
                rows.Add(element, edge, v);
            }
        }
        rows.Add("list margins", "top", double.NaN);
        rows.Add("list margins", "bottom", double.NaN);
        rows.Add("list indent", "none", double.NaN);
        return rows;
    }

    /// <summary>An element that draws nothing writes none of its insets, so a non-finite one saves, as in 2.3.3.</summary>
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

    /// <summary>U+00A0 counts as drawable text, so the canvas refuses the non-finite position, where plain white space draws nothing and saves.</summary>
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

    // ── A table cell's text colour is written only where the cell is drawn ───

    private static Document TableUnder(string setup, TextStyle? cellStyle, TextStyle? defaultStyle, double borderWidth = 0.5)
    {
        var doc = new Document();
        switch (setup)
        {
            case "document top -inf": doc.Margins = new EdgeInsets(double.NegativeInfinity, 72, 72, 72); break;
            case "empty header -inf": doc.Header = new RunningBand(string.Empty) { Height = double.NegativeInfinity }; break;
            default: doc.Add(new Paragraph(string.Empty) { Margins = new EdgeInsets(double.NegativeInfinity, 0, 0, 0) }); break;
        }

        var table = new TableElement { DefaultCellStyle = defaultStyle, BorderWidth = borderWidth };
        table.AddRow().AddCell(new Cell("a") { Style = cellStyle });
        doc.Add(table);
        return doc;
    }

    /// <summary>A table that never reaches the page writes no cell colour, so a non-finite one saves a blank page; the colour is checked where it is written.</summary>
    [Theory]
    [InlineData("document top -inf", true)]
    [InlineData("document top -inf", false)]
    [InlineData("empty header -inf", true)]
    [InlineData("empty header -inf", false)]
    [InlineData("empty paragraph top -inf", true)]
    [InlineData("empty paragraph top -inf", false)]
    public void TableCellTextColour_nan_savesWhenTheTableIsNotDrawn(string setup, bool onTheCell)
    {
        var nan = new TextStyle { Color = _nan };
        using var doc = TableUnder(setup, onTheCell ? nan : null, onTheCell ? null : nan);
        using var plain = TableUnder(setup, null, null);

        Assert.Equal(Summary(plain), Summary(doc));
    }

    /// <summary>A negative border width on a table that is laid out and not drawn saves; the width is refused where a cell border is written.</summary>
    [Theory]
    [InlineData("document top -inf")]
    [InlineData("empty header -inf")]
    [InlineData("empty paragraph top -inf")]
    public void TableBorderWidth_negative_savesWhenTheTableIsNotDrawn(string setup)
    {
        using var doc = TableUnder(setup, null, null, borderWidth: -2);
        using var plain = TableUnder(setup, null, null);

        Assert.Equal(Summary(plain), Summary(doc));
    }

    /// <summary>The non-finite width is a Layout refusal, as in 2.3.3, drawn or not.</summary>
    [Fact]
    public void TableBorderWidth_nan_isRefusedEvenWhenTheTableIsNotDrawn()
    {
        using var doc = TableUnder("document top -inf", null, null, borderWidth: double.NaN);

        var ex = Assert.Throws<InvalidOperationException>(() => Save(doc));

        Assert.StartsWith("A table has a line width of NaN", ex.Message, StringComparison.Ordinal);
    }
}
