// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// What the library says when it refuses an input, and what it still draws when it does not.
///
/// Three sections. (a) is input a renderer cannot lay out. (b) is input that used to save a document
/// whose content stream a reader cannot use. (c) is the inputs immediately next to those, which
/// render today and must keep rendering, because refusing one would turn a working document into an
/// exception and this is a patch release.
///
/// Two lessons from review are built into the shape of this file. Section (c) asserts the operator
/// each input emits rather than that a document came back: a PDF is never empty, and an earlier
/// version of this file passed in full with the image paint operator deleted and with paragraph
/// drawing returning immediately. And every non-finite input is listed value by value, because NaN,
/// positive infinity and negative infinity take three different branches, and an earlier version
/// measured one of them and claimed all three.
/// </summary>
public sealed class RefusalMessageTests
{
    private static FontReference Helvetica => new(Standard14.Helvetica);

    private static TextStyle Style(double size = 10) => new() { FontRef = Helvetica, FontSize = size };

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

    /// <summary>The page's own operators, so a test can assert what was drawn rather than that something was.</summary>
    private static string Stream(Document doc) => PdfTestUtil.DecompressAllFlatStreams(Render(doc));

    private static InvalidOperationException Refused(Document doc) =>
        Assert.Throws<InvalidOperationException>(() => Render(doc));

    // ── (a) Input a renderer cannot lay out ───────────────────────────────────

    /// <summary>
    /// All three non-finite sizes, because they reach the old too-tall message by three routes. NaN
    /// makes every height comparison false, so nothing is measured. Positive infinity reports a
    /// height larger than any page. Negative infinity is clamped to zero by the line-height maximum
    /// and used to save successfully, writing <c>/F1 -Infinity Tf</c>, so for that value this is an
    /// invalid-output fix rather than a wording one.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "positive infinity")]
    [InlineData(double.NegativeInfinity, "negative infinity")]
    public void Paragraph_nonFiniteFontSize_namesTheFontSize(double size, string rendered)
    {
        using var doc = Page();
        doc.Add(new Paragraph("x", Style(size)));

        Assert.Equal(
            $"A paragraph run has a font size of {rendered}, which cannot be laid out. " +
            "A font size must be a finite number.",
            Refused(doc).Message);
    }

    /// <summary>
    /// The heading and list paths reach the same check, because both build a paragraph renderer.
    /// Pinned so a future change that gives either its own text path does not lose the guard.
    /// </summary>
    [Fact]
    public void Heading_nonFiniteFontSize_namesTheFontSize()
    {
        using var doc = Page();
        doc.Add(new Heading("x", Style(double.NaN)));

        Assert.StartsWith("A paragraph run has a font size of NaN", Refused(doc).Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The running band is drawn straight onto the canvas and reaches no renderer, so it reached no
    /// validation: this saved a document containing <c>/F1 NaN Tf</c> and <c>1 0 0 1 NaN NaN Tm</c>.
    /// It had no test at all, in any suite.
    /// </summary>
    [Theory]
    [InlineData(true, "header")]
    [InlineData(false, "footer")]
    public void Band_nonFiniteFontSize_namesTheBand(bool header, string named)
    {
        using var doc = Page();
        var band = new RunningBand("page {page}", Style(double.NaN)) { Height = 20 };
        if (header) doc.Header = band; else doc.Footer = band;
        doc.Add(new Paragraph("body", Style()));

        Assert.Equal(
            $"The {named} band has a font size of NaN, which cannot be laid out. " +
            "A font size must be a finite number.",
            Refused(doc).Message);
    }

    /// <summary>
    /// A column span below one. In the row that sets the column count this already threw, with the
    /// wrong message; in any other row it rendered while silently dropping the cell, which is why
    /// the second case here puts the bad cell in a narrower row.
    /// </summary>
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(-3, 0, 0)]
    [InlineData(0, 1, 2)]
    public void Table_colSpanBelowOne_namesTheCellTheCallerWrote(int colSpan, int badRow, int badCell)
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() };
        if (badRow == 0)
        {
            table.AddRow().AddCell(new Cell("x") { ColSpan = colSpan });
        }
        else
        {
            table.AddRow().AddCell("a").AddCell("b").AddCell("c");
            var narrow = table.AddRow();
            narrow.AddCell("d").AddCell("e").AddCell(new Cell("f") { ColSpan = colSpan });
        }
        doc.Add(table);

        Assert.Equal(
            $"Table row {badRow}, cell {badCell} has a ColSpan of {colSpan}. " +
            "A span must cover at least one column.",
            Refused(doc).Message);
    }

    /// <summary>
    /// The cell-style path, which had no test in any of the repository's 6935 cases, and the reason
    /// that mattered: it was handed the column index while the span check was handed the authoring
    /// index, so one physical cell was reported two different ways. A preceding span of two makes
    /// the two indices diverge, which a single-cell row cannot do.
    /// </summary>
    [Fact]
    public void Table_nonFiniteCellStyle_namesTheCellTheCallerWrote()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() };
        var row = table.AddRow();
        row.AddCell(new Cell("wide") { ColSpan = 2 });
        row.AddCell(new Cell("bad") { Style = Style(double.NaN) });
        doc.Add(table);

        // The bad cell is the caller's index 1, and lands in column 2.
        Assert.Equal(
            "Table row 0, cell 1 has a font size of NaN, which cannot be laid out. " +
            "A font size must be a finite number.",
            Refused(doc).Message);
    }

    /// <summary>
    /// A table resolving to no columns is the third refusal #481 names, and the one route to it no
    /// span check covers, so it kept reporting the page size on both sides of the first attempt.
    /// </summary>
    [Fact]
    public void Table_withNoCells_saysSoInsteadOfBlamingThePage()
    {
        using var doc = Page();
        doc.Add(new TableElement { DefaultCellStyle = Style() });

        Assert.Equal(
            "A table resolved to no columns, so it has nothing to draw. " +
            "Add a row holding at least one cell.",
            Refused(doc).Message);
    }

    // ── (b) Input that saved a document a reader cannot use ───────────────────

    /// <summary>
    /// The guard is on the token the canvas writes, not on the value held, because that is what
    /// makes the matrix singular. <c>PdfCanvas</c> formats coordinates to five decimals, so 4.9e-6
    /// is written as <c>0</c> while 5e-6 is written as <c>0.00001</c>. An earlier version refused
    /// only an exact zero, and 4e-6 went on emitting <c>0 0 0 0 10 390 cm</c>, the very matrix the
    /// check quotes.
    /// </summary>
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(4e-6, "0.000004")]
    [InlineData(-4e-6, "-0.000004")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.NegativeInfinity, "negative infinity")]
    public void Image_widthWrittenAsZeroOrNonFinite_namesTheWidth(double width, string rendered)
    {
        using var doc = Page();
        doc.Add(new LayoutImage(TwoByTwoImage()) { Width = width });

        Assert.Equal(
            $"An image would be drawn with a width of {rendered}, which cannot be painted. " +
            "A drawn width must be finite and at least 0.000005 points in magnitude; anything " +
            "smaller is written as zero, giving a transformation matrix with no inverse.",
            Refused(doc).Message);
    }

    /// <summary>
    /// The height, at every value the width is tested at. An earlier version tested the height at
    /// zero alone, so dropping the non-finite half of its guard failed nothing in the solution.
    /// </summary>
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(4e-6, "0.000004")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "positive infinity")]
    [InlineData(double.NegativeInfinity, "negative infinity")]
    public void Image_heightWrittenAsZeroOrNonFinite_namesTheHeight(double height, string rendered)
    {
        using var doc = Page();
        doc.Add(new LayoutImage(TwoByTwoImage()) { Height = height });

        Assert.Equal(
            $"An image would be drawn with a height of {rendered}, which cannot be painted. " +
            "A drawn height must be finite and at least 0.000005 points in magnitude; anything " +
            "smaller is written as zero, giving a transformation matrix with no inverse.",
            Refused(doc).Message);
    }

    /// <summary>
    /// Margins that consume the content box drive the drawn width to zero while the property itself
    /// was never set, which is why the message carries no advice to unset it.
    /// </summary>
    [Fact]
    public void Image_marginsConsumingTheBox_doesNotAdviseUnsettingAnUnsetWidth()
    {
        using var doc = Page();
        doc.Add(new LayoutImage(TwoByTwoImage()) { Margins = new EdgeInsets(140) });

        var message = Refused(doc).Message;
        Assert.StartsWith("An image would be drawn with a width of 0", message, StringComparison.Ordinal);
        Assert.DoesNotContain("leave it unset", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A non-finite line width reaches the stream as <c>NaN w</c> and carries into the line's own
    /// coordinates. Zero is not refused: <c>0 w</c> asks for the thinnest line the device renders.
    /// </summary>
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "positive infinity")]
    public void Separator_nonFiniteLineWidth_namesIt(double width, string rendered)
    {
        using var doc = Page();
        doc.Add(new LineSeparator { LineWidth = width });

        Assert.Equal(
            $"A line separator has a line width of {rendered}, which cannot be drawn. " +
            "A line width must be a finite number.",
            Refused(doc).Message);
    }

    /// <summary>A non-finite cell padding writes <c>10 NaN 280 NaN re</c> and a NaN text matrix.</summary>
    [Fact]
    public void Table_nonFiniteCellPadding_namesTheCell()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() };
        table.AddRow().AddCell(new Cell("x") { Padding = new EdgeInsets(double.NaN) });
        doc.Add(table);

        Assert.Equal(
            "Table row 0, cell 0 has a non-finite inset. Every inset must be a finite number.",
            Refused(doc).Message);
    }

    /// <summary>A non-finite table border width writes <c>NaN w</c> around every cell.</summary>
    [Fact]
    public void Table_nonFiniteBorderWidth_namesTheTable()
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style(), BorderWidth = double.NaN };
        table.AddRow().AddCell("x");
        doc.Add(table);

        Assert.Equal(
            "A table has a line width of NaN, which cannot be drawn. " +
            "A line width must be a finite number.",
            Refused(doc).Message);
    }

    // ── (c) The neighbours, asserted by what they draw ────────────────────────

    /// <summary>
    /// Each of these renders valid content today, so refusing it would turn a working document into
    /// an exception. They are contract tightenings for the next major.
    ///
    /// The assertion is the operator, not that bytes came back. An earlier version used
    /// <c>Assert.NotEmpty</c>, which a PDF always satisfies: with the image paint operator deleted,
    /// or with paragraph drawing returning immediately, every case still passed.
    /// </summary>
    [Theory]
    [InlineData("font size zero", "/F1 0 Tf")]
    [InlineData("font size negative", "/F1 -12 Tf")]
    [InlineData("leading NaN", "1 0 0 1 10 380 Tm")]
    [InlineData("leading positive infinity", "1 0 0 1 10 380 Tm")]
    [InlineData("image width negative", "-40 0 0 -40 10 430 cm")]
    [InlineData("image width positive infinity", "280 0 0 280 10 110 cm")]
    public void Neighbour_rendersAndEmitsTheOperator(string which, string expectedOperator)
    {
        using var doc = Page();
        switch (which)
        {
            case "font size zero":
                doc.Add(new Paragraph("Hi", Style(0)));
                break;
            case "font size negative":
                doc.Add(new Paragraph("Hi", Style(-12)));
                break;
            case "leading NaN":
                doc.Add(new Paragraph("Hi", new TextStyle { FontRef = Helvetica, FontSize = 10, Leading = double.NaN }));
                break;
            case "leading positive infinity":
                doc.Add(new Paragraph("Hi", new TextStyle { FontRef = Helvetica, FontSize = 10, Leading = double.PositiveInfinity }));
                break;
            case "image width negative":
                doc.Add(new LayoutImage(TwoByTwoImage()) { Width = -40 });
                break;
            case "image width positive infinity":
                doc.Add(new LayoutImage(TwoByTwoImage()) { Width = double.PositiveInfinity });
                break;
            default:
                throw new InvalidOperationException($"unknown case {which}");
        }

        Assert.Contains(expectedOperator, Stream(doc), StringComparison.Ordinal);
    }

    /// <summary>
    /// A row span below one behaves as one rather than being refused, so every cell of a two-row
    /// table still draws. Asserted on the drawn text, not on a byte count.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-2)]
    public void Neighbour_rowSpanBelowOne_stillDrawsEveryCell(int rowSpan)
    {
        using var doc = Page();
        var table = new TableElement { DefaultCellStyle = Style() };
        var first = table.AddRow();
        first.AddCell(new Cell("A") { RowSpan = rowSpan });
        first.AddCell("B");
        table.AddRow().AddCell("C").AddCell("D");
        doc.Add(table);

        var stream = Stream(doc);
        foreach (var cell in new[] { "(A) Tj", "(B) Tj", "(C) Tj", "(D) Tj" })
            Assert.Contains(cell, stream, StringComparison.Ordinal);
    }

    /// <summary>
    /// A zero separator line width is not refused, and emits the operator asking for the device's
    /// thinnest line.
    /// </summary>
    [Fact]
    public void Neighbour_zeroSeparatorLineWidth_stillDraws()
    {
        using var doc = Page();
        doc.Add(new LineSeparator { LineWidth = 0 });

        Assert.Contains("0 w", Stream(doc), StringComparison.Ordinal);
    }

    /// <summary>
    /// An empty or whitespace-only paragraph produces no text fragments, so the style is never
    /// reached and a non-finite size in it is not refused. Nothing invalid is emitted, so the
    /// behaviour is fine, but it is a boundary of the check and belongs written down.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Neighbour_emptyTextSkipsTheStyleCheck(string text)
    {
        using var doc = Page();
        doc.Add(new Paragraph(text, Style(double.NaN)));

        var stream = Stream(doc);
        Assert.DoesNotContain("NaN", stream, StringComparison.Ordinal);
        Assert.DoesNotContain(") Tj", stream, StringComparison.Ordinal);
    }

    /// <summary>A 2x2 opaque RGB PNG, decoded so these tests need no fixture file.</summary>
    private static VellumPdf.Images.PdfImageXObject TwoByTwoImage()
    {
        static uint Crc(byte[] d)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in d)
            {
                c ^= b;
                for (var k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            return ~c;
        }

        static void Chunk(Stream m, string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length);
            Array.Reverse(len);
            m.Write(len);
            var body = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            m.Write(body);
            var crc = BitConverter.GetBytes(Crc(body));
            Array.Reverse(crc);
            m.Write(crc);
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);

        var raw = new byte[2 * (1 + (2 * 3))];
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 6; x++)
                raw[(y * 7) + 1 + x] = (byte)(x * 40);

        using var deflated = new MemoryStream();
        using (var z = new System.IO.Compression.ZLibStream(
            deflated, System.IO.Compression.CompressionLevel.Optimal, true))
        {
            z.Write(raw);
        }

        Chunk(ms, "IDAT", deflated.ToArray());
        Chunk(ms, "IEND", []);
        return VellumPdf.Images.PngImageLoader.Load(ms.ToArray());
    }
}
