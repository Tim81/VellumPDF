// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Barcodes.Tests;

/// <summary>
/// Content-stream assertions for <see cref="BarcodeCanvasExtensions.DrawBarcode"/>: run-merged
/// rectangles, a single fill per symbol, balanced save/restore state, and no colour leakage.
/// </summary>
public sealed class BarcodeCanvasExtensionsTests
{
    private static byte[] Build(Action<PdfCanvas> draw)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);
        var canvas = new PdfCanvas(page);
        draw(canvas);
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void DrawBarcode_nullCanvas_throws()
    {
        var canvas = (PdfCanvas)null!;
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBarcode(new QrCode("A"), 0, 0));
    }

    [Fact]
    public void DrawBarcode_nullBarcode_throws()
    {
        using var doc = new PdfDocument();
        var canvas = new PdfCanvas(doc.AddPage(PageSize.A4));
        Assert.Throws<ArgumentNullException>(() => canvas.DrawBarcode(null!, 0, 0));
    }

    [Fact]
    public void DrawBarcode_1dWithShowTextAndNoFont_throwsInvalidOperationException()
    {
        Assert.Throws<InvalidOperationException>(() =>
            Build(canvas => canvas.DrawBarcode(new Code128Barcode("HELLO"), 50, 700)));
    }

    [Fact]
    public void DrawBarcode_1dWithShowTextFalseAndNoFont_doesNotThrow()
    {
        var bytes = Build(canvas => canvas.DrawBarcode(new Code128Barcode("HELLO") { ShowText = false }, 50, 700));
        Assert.True(bytes.Length > 100);
    }

    [Fact]
    public void DrawBarcode_qrMatrix_reCountMatchesIndependentlyCountedMergedRuns()
    {
        var qr = new QrCode("A") { Version = 1, ErrorCorrection = QrErrorCorrection.L, ModuleSize = 2 };
        var matrix = qr.GetMatrix();

        var (expectedRectangles, totalDarkModules) = CountMergedDarkRuns(matrix);

        var bytes = Build(canvas => canvas.DrawBarcode(qr, 50, 700));
        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        var reCount = PdfTestUtil.CountOccurrences(ops, " re\n");

        Assert.Equal(expectedRectangles, reCount);
        Assert.True(expectedRectangles < totalDarkModules,
            "a QR finder pattern must contain multi-module runs, otherwise this test can't prove merging happened");
    }

    /// <summary>Independently walks the matrix (same "maximal per-row dark run" definition the painter uses) so the test has ground truth to compare against.</summary>
    private static (int Rectangles, int DarkModules) CountMergedDarkRuns(BarcodeMatrix matrix)
    {
        var rectangles = 0;
        var darkModules = 0;
        for (var row = 0; row < matrix.Height; row++)
        {
            var col = 0;
            while (col < matrix.Width)
            {
                if (!matrix.IsDark(col, row)) { col++; continue; }
                rectangles++;
                while (col < matrix.Width && matrix.IsDark(col, row)) { darkModules++; col++; }
            }
        }

        return (rectangles, darkModules);
    }

    [Fact]
    public void DrawBarcode_noBackground_emitsExactlyOneFill_forA2dSymbol()
    {
        var bytes = Build(canvas => canvas.DrawBarcode(new QrCode("A"), 50, 700));
        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Equal(1, PdfTestUtil.CountOccurrences(ops, "\nf\n"));
    }

    [Fact]
    public void DrawBarcode_noBackground_emitsExactlyOneFill_forA1dSymbol()
    {
        var bytes = Build(canvas => canvas.DrawBarcode(new Code128Barcode("HELLO") { ShowText = false }, 50, 700));
        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Equal(1, PdfTestUtil.CountOccurrences(ops, "\nf\n"));
    }

    [Fact]
    public void DrawBarcode_withBackground_emitsTwoFills()
    {
        var bytes = Build(canvas => canvas.DrawBarcode(
            new QrCode("A") { Background = ColorRgb.White }, 50, 700));
        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Equal(2, PdfTestUtil.CountOccurrences(ops, "\nf\n"));
    }

    [Fact]
    public void DrawBarcode_itfWithBearer_stillEmitsExactlyOneFill()
    {
        // Bearer bars are extra subpaths added to the same fill, not a separate Fill() call.
        var bytes = Build(canvas => canvas.DrawBarcode(
            new Itf14Barcode("1234567890123") { ShowText = false }, 50, 700));
        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Equal(1, PdfTestUtil.CountOccurrences(ops, "\nf\n"));
    }

    [Fact]
    public void DrawBarcode_wrapsInBalancedSaveRestoreState()
    {
        var bytes = Build(canvas => canvas.DrawBarcode(new QrCode("A"), 50, 700));
        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);

        var opens = PdfTestUtil.CountOccurrences(ops, "q\n");
        var closes = PdfTestUtil.CountOccurrences(ops, "Q\n");
        Assert.True(opens >= 1);
        Assert.Equal(opens, closes);
    }

    [Fact]
    public void DrawBarcode_afterRestoreState_fillColourDoesNotLeak()
    {
        var magenta = ColorRgb.FromHex(0xFF00FF);
        var bytes = Build(canvas =>
        {
            canvas.DrawBarcode(new Code128Barcode("HELLO") { ShowText = false, Foreground = magenta }, 50, 700);
            canvas.Rectangle(50, 50, 10, 10).Fill(); // unrelated content, sets no colour of its own
        });

        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);

        // The magenta fill colour is set exactly once, inside the barcode's own q/Q bracket —
        // it does not get re-emitted (or implicitly reused) for the unrelated rectangle.
        Assert.Equal(1, PdfTestUtil.CountOccurrences(ops, "1 0 1 rg"));

        var opens = PdfTestUtil.CountOccurrences(ops, "q\n");
        var closes = PdfTestUtil.CountOccurrences(ops, "Q\n");
        Assert.Equal(opens, closes);
    }

    [Fact]
    public void DrawBarcode_withTextFont_drawsHumanReadableText()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage(PageSize.A4);
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);

        canvas.DrawBarcode(new Code128Barcode("HELLO"), 50, 700, font);
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Save(ms);
        var ops = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray());

        Assert.Contains("BT\n", ops, StringComparison.Ordinal);
        Assert.Contains("ET\n", ops, StringComparison.Ordinal);
        Assert.Contains("HELLO", ops, StringComparison.Ordinal);
    }
}
