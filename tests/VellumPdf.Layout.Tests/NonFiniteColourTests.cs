// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #509: a colour channel that is not a finite number used to reach the content stream as
/// <c>NaN 0 0 rg</c> or <c>Infinity 0 0 rg</c>, neither of which is a PDF number. Each colour a
/// Layout element can carry is refused at the Layout seam, naming the element, and each of the three
/// non-finite values is listed on its own because they take separate branches elsewhere in this
/// package. A channel outside 0 to 1 is finite and still renders, and one test pins that.
/// </summary>
public sealed class NonFiniteColourTests
{
    private static TextStyle Style(ColorRgb? colour = null) => new()
    {
        FontRef = new FontReference(Standard14.Helvetica),
        FontSize = 10,
        Color = colour ?? ColorRgb.Black,
    };

    private static Document Page() => new()
    {
        PageSize = new PdfRectangle(0, 0, 300, 400),
        Margins = new EdgeInsets(10),
    };

    private static byte[] Render(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    public static TheoryData<int, double, string> Channels()
    {
        var rows = new TheoryData<int, double, string>();
        foreach (var channel in new[] { 0, 1, 2 })
        {
            rows.Add(channel, double.NaN, "NaN");
            rows.Add(channel, double.PositiveInfinity, "positive infinity");
            rows.Add(channel, double.NegativeInfinity, "negative infinity");
        }
        return rows;
    }

    private static ColorRgb With(int channel, double value) => channel switch
    {
        0 => new ColorRgb(value, 0.5, 0.5),
        1 => new ColorRgb(0.5, value, 0.5),
        _ => new ColorRgb(0.5, 0.5, value),
    };

    private static string Name(int channel) => channel switch { 0 => "red", 1 => "green", _ => "blue" };

    private static string Expect(string owner, string what, int channel, string rendered) =>
        $"{owner} has a {what} whose {Name(channel)} channel is {rendered}, " +
        "which cannot be written to a content stream. Every colour channel must be a finite number.";

    [Theory]
    [MemberData(nameof(Channels))]
    public void Paragraph_textColour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        doc.Add(new Paragraph("x", Style(With(channel, value))));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Equal(Expect("A paragraph run", "text colour", channel, rendered), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void RunningBand_textColour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        doc.Header = new RunningBand("h", Style(With(channel, value))) { Height = 20 };
        doc.Add(new Paragraph("body", Style()));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Equal(Expect("The header band", "text colour", channel, rendered), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void LineSeparator_colour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        doc.Add(new LineSeparator { Color = With(channel, value) });

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Equal(Expect("A line separator", "colour", channel, rendered), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void Table_borderColour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style(), BorderColor = With(channel, value) };
        table.AddRow().AddCell("x");
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Equal(Expect("A table", "border colour", channel, rendered), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void Table_cellBackground_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell(new Cell("x") { Background = With(channel, value) });
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Equal(Expect("Table row 0, cell 0", "background colour", channel, rendered), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void Table_cellTextColour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell(new Cell("x") { Style = Style(With(channel, value)) });
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));
        Assert.Equal(Expect("Table row 0, cell 0", "text colour", channel, rendered), ex.Message);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void PieChart_sliceColour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        doc.Add(new PieChart { Slices = [new PieSlice(1, With(channel, value))] });

        var ex = Assert.Throws<ArgumentException>(() => Render(doc));
        Assert.StartsWith("Pie slice colours must have finite channels", ex.Message, StringComparison.Ordinal);
        Assert.Equal("Slices", ex.ParamName);
        Assert.NotNull(rendered);
    }

    [Theory]
    [MemberData(nameof(Channels))]
    public void PieChart_strokeColour_isRefused(int channel, double value, string rendered)
    {
        using var doc = Page();
        doc.Add(new PieChart
        {
            Slices = [new PieSlice(1, ColorRgb.Black)],
            StrokeColor = With(channel, value),
        });

        var ex = Assert.Throws<ArgumentException>(() => Render(doc));
        Assert.StartsWith("Pie chart stroke colour must have finite channels", ex.Message, StringComparison.Ordinal);
        Assert.Equal("StrokeColor", ex.ParamName);
        Assert.NotNull(rendered);
    }

    /// <summary>
    /// A finite channel outside 0 to 1 still saves and still reaches the stream verbatim. ISO
    /// 32000-2 8.6.4.3 says a DeviceRGB component "shall" be in range, but the token parses and the
    /// file renders, and a patch release does not turn a rendering document into an exception.
    /// The range is documented instead and the refusal belongs to the next major (#484).
    /// </summary>
    [Fact]
    public void OutOfRangeFiniteChannel_stillRenders_andIsWrittenAsGiven()
    {
        using var doc = Page();
        doc.Add(new Paragraph("x", Style(new ColorRgb(5, 0, -1))));

        var stream = PdfTestUtil.DecompressAllFlatStreams(Render(doc));

        Assert.Contains("5 0 -1 rg", stream, StringComparison.Ordinal);
    }

    [Fact]
    public void FiniteColour_isWritten()
    {
        using var doc = Page();
        doc.Add(new Paragraph("x", Style(new ColorRgb(1, 0.5, 0.25))));

        var stream = PdfTestUtil.DecompressAllFlatStreams(Render(doc));

        Assert.Contains("1 0.5 0.25 rg", stream, StringComparison.Ordinal);
    }
}
