// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// What the library says when it refuses an input, and what it still accepts.
///
/// Two halves, and the second matters as much as the first. Sections (a) and (b) pin the messages
/// for input that cannot be laid out or cannot be drawn. Section (c) pins the inputs immediately
/// next to them that render today and must keep rendering, because rejecting those would turn a
/// working document into an exception and this is a patch.
///
/// Every boundary here was measured on the emitted content stream rather than reasoned about. The
/// first version of this change rejected a whole class at once and broke
/// <see cref="OffPagePlacementTests"/>'s mirrored-image case, which exists precisely to record that
/// a negative extent renders on purpose.
/// </summary>
public sealed class RefusalMessageTests
{
    private static FontReference Helvetica => new(Standard14.Helvetica);

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

    // ── (a) Input that cannot be laid out ─────────────────────────────────────

    /// <summary>
    /// A non-finite font size makes <c>EffectiveLeading</c> non-finite, so every height comparison
    /// is false and the paragraph reports placing nothing. <c>DocumentRenderer</c> reads that as
    /// "this element can never fit" and used to raise the too-tall message, whose suggested remedy
    /// — reduce the content or enlarge the page — cannot help an input that is not a number (#481).
    /// </summary>
    [Theory]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.PositiveInfinity, "positive infinity")]
    [InlineData(double.NegativeInfinity, "negative infinity")]
    public void Paragraph_nonFiniteFontSize_namesTheFontSize(double size, string rendered)
    {
        using var doc = Page();
        doc.Add(new Paragraph("x", new TextStyle { FontRef = Helvetica, FontSize = size }));

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));

        Assert.Equal(
            $"A paragraph run has a font size of {rendered}, which cannot be laid out. " +
            "A font size must be a finite number.",
            ex.Message);
    }

    /// <summary>
    /// A <c>ColSpan</c> below one can leave the grid resolver with no columns at all, since the
    /// column count is the widest row's span sum, so no width is allocated and the table reports
    /// placing nothing. Same wrong message, same reason (#481).
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void Table_colSpanBelowOne_namesTheCell(int colSpan)
    {
        using var doc = Page();
        var table = new TableElement
        {
            DefaultCellStyle = new TextStyle { FontRef = Helvetica, FontSize = 10 },
        };
        table.AddRow().AddCell(new Cell("x") { ColSpan = colSpan });
        doc.Add(table);

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));

        Assert.Equal(
            $"Table row 0, cell 0 has a ColSpan of {colSpan}. A span must cover at least one column.",
            ex.Message);
    }

    // ── (b) Input that saved a document a reader cannot render ────────────────

    /// <summary>
    /// A zero image extent reached the content stream as <c>0 0 0 0 10 390 cm</c>, a singular
    /// matrix that cannot be inverted, which ISO 32000-2 leaves undefined for a painted XObject.
    /// A non-finite one reached it as <c>NaN 0 0 NaN 10 NaN cm</c>, where the token is not a PDF
    /// number at all. Both saved without complaint, so unlike section (a) there was no message to
    /// correct: there was no message (#478).
    ///
    /// A zero width also collapses the height, because the height is derived from the width through
    /// the aspect ratio, and the height check passes trivially at zero.
    ///
    /// Positive infinity is absent from this list on purpose and sits in section (c) instead. The
    /// over-wide clamp added by #472 fires on it, because infinity is greater than the content box,
    /// so it becomes the box width before anything here sees it and the image renders. NaN does not
    /// reach that clamp, since every comparison against NaN is false, and negative infinity is not
    /// greater than the box so it is not clamped either.
    /// </summary>
    [Theory]
    [InlineData(0.0, "0")]
    [InlineData(double.NaN, "NaN")]
    [InlineData(double.NegativeInfinity, "negative infinity")]
    public void Image_zeroOrNonFiniteWidth_namesTheWidth(double width, string rendered)
    {
        using var doc = Page();
        doc.Add(new LayoutImage(TwoByTwoImage()) { Width = width });

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));

        Assert.Equal(
            $"An image has a width of {rendered}, which cannot be drawn. " +
            "A width must be a finite, non-zero number; leave it unset to fill the content box.",
            ex.Message);
    }

    /// <summary>An explicit zero height, with the width left to fill the box.</summary>
    [Fact]
    public void Image_zeroHeight_namesTheHeight()
    {
        using var doc = Page();
        doc.Add(new LayoutImage(TwoByTwoImage()) { Height = 0 });

        var ex = Assert.Throws<InvalidOperationException>(() => Render(doc));

        Assert.Equal(
            "An image has a height of 0, which cannot be drawn. " +
            "A height must be a finite, non-zero number; leave it unset to derive it from the width.",
            ex.Message);
    }

    // ── (c) The neighbours that must keep rendering ───────────────────────────

    /// <summary>
    /// Each of these renders today and emits valid content, measured on the stream, so refusing any
    /// of them would turn a working document into an exception. They are deferred to the next major
    /// with the rest of the contract tightening, and this pins that the patch left them alone.
    ///
    /// A font size of zero emits <c>/F1 0 Tf</c>, a valid operator. A non-finite leading emits a
    /// valid text matrix, because the leading never reaches the stream directly. A negative image
    /// extent emits <c>-40 0 0 -40 10 430 cm</c>, a valid matrix that mirrors the image, which
    /// <see cref="OffPagePlacementTests"/> records as a deliberate decision.
    /// </summary>
    [Fact]
    public void Neighbouring_inputs_stillRender()
    {
        using (var zeroFont = Page())
        {
            zeroFont.Add(new Paragraph("x", new TextStyle { FontRef = Helvetica, FontSize = 0 }));
            Assert.NotEmpty(Render(zeroFont));
        }

        using (var negativeFont = Page())
        {
            negativeFont.Add(new Paragraph("x", new TextStyle { FontRef = Helvetica, FontSize = -12 }));
            Assert.NotEmpty(Render(negativeFont));
        }

        using (var nonFiniteLeading = Page())
        {
            nonFiniteLeading.Add(new Paragraph("x", new TextStyle
            {
                FontRef = Helvetica,
                FontSize = 10,
                Leading = double.NaN,
            }));
            Assert.NotEmpty(Render(nonFiniteLeading));
        }

        using (var zeroRowSpan = Page())
        {
            var table = new TableElement
            {
                DefaultCellStyle = new TextStyle { FontRef = Helvetica, FontSize = 10 },
            };
            table.AddRow().AddCell(new Cell("x") { RowSpan = 0 });
            zeroRowSpan.Add(table);
            Assert.NotEmpty(Render(zeroRowSpan));
        }

        using (var negativeWidth = Page())
        {
            negativeWidth.Add(new LayoutImage(TwoByTwoImage()) { Width = -40 });
            Assert.NotEmpty(Render(negativeWidth));
        }

        using (var infiniteWidth = Page())
        {
            // Clamped to the content box by #472's over-wide check before any validation sees it.
            infiniteWidth.Add(new LayoutImage(TwoByTwoImage()) { Width = double.PositiveInfinity });
            Assert.NotEmpty(Render(infiniteWidth));
        }
    }

    /// <summary>A 2x2 opaque RGB PNG, decoded so the tests have an image without a fixture file.</summary>
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
