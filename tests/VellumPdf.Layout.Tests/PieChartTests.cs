// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Tests;

public sealed class PieChartTests
{
    private static byte[] Save(PieChart chart, bool tagged = false)
    {
        using var doc = new Document { Tagged = tagged, Language = tagged ? "en-US" : null };
        doc.Add(chart);
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void PieChart_twoSlices_producesValidPdf()
    {
        var bytes = Save(new PieChart
        {
            Slices =
            [
                new PieSlice(60, ColorRgb.FromHex(0x3366CC)),
                new PieSlice(40, ColorRgb.FromHex(0xDC3912)),
            ],
        });

        Assert.True(bytes.Length > 100);
        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void PieChart_singleFullSlice_producesValidPdf()
    {
        var bytes = Save(new PieChart
        {
            Slices = [new PieSlice(100, ColorRgb.Black)],
        });

        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void PieChart_emptySlices_throws()
    {
        Assert.Throws<ArgumentException>(() => Save(new PieChart { Slices = [] }));
    }

    [Fact]
    public void PieChart_allZeroValues_throws()
    {
        Assert.Throws<ArgumentException>(() => Save(new PieChart
        {
            Slices = [new PieSlice(0, ColorRgb.Black), new PieSlice(0, ColorRgb.White)],
        }));
    }

    [Fact]
    public void PieChart_negativeValue_throws()
    {
        Assert.Throws<ArgumentException>(() => Save(new PieChart
        {
            Slices = [new PieSlice(-5, ColorRgb.Black)],
        }));
    }

    [Fact]
    public void PieChart_counterClockwiseWithCustomStartAngle_producesValidPdf()
    {
        var bytes = Save(new PieChart
        {
            Slices =
            [
                new PieSlice(1, ColorRgb.FromHex(0x3366CC)),
                new PieSlice(2, ColorRgb.FromHex(0xDC3912)),
                new PieSlice(3, ColorRgb.FromHex(0xFF9900)),
            ],
            Clockwise = false,
            StartAngle = 0,
        });

        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void PieChart_zeroValueSliceAmongNonZero_producesValidPdf()
    {
        // One drawable slice plus a zero-value slice collapses to a seamless full circle;
        // the zero slice must not paint a stray radial line when stroking is on.
        var bytes = Save(new PieChart
        {
            Slices = [new PieSlice(100, ColorRgb.Black), new PieSlice(0, ColorRgb.White)],
            StrokeColor = ColorRgb.White,
        });

        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    [Fact]
    public void PieChart_sliceWithLabelAndStroke_producesValidPdf()
    {
        var bytes = Save(new PieChart
        {
            Slices =
            [
                new PieSlice(70, ColorRgb.FromHex(0x109618), "Yes"),
                new PieSlice(30, ColorRgb.FromHex(0x990099), "No"),
            ],
            StrokeColor = ColorRgb.White,
            StrokeWidth = 1,
        });

        Assert.Equal("%PDF-2.0"u8.ToArray(), bytes[..8]);
    }

    // ── Behavioural assertions ────────────────────────────────────────────────

    [Fact]
    public void PieChart_threeSlices_emitsOneFillPerSlice()
    {
        var bytes = Save(new PieChart
        {
            Slices =
            [
                new PieSlice(1, ColorRgb.FromHex(0x3366CC)),
                new PieSlice(2, ColorRgb.FromHex(0xDC3912)),
                new PieSlice(3, ColorRgb.FromHex(0xFF9900)),
            ],
        });

        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        // No stroke configured, so each wedge paints with a plain fill (f) on its own line —
        // one per slice. The fill operator is newline-delimited, not space-prefixed.
        Assert.Equal(3, PdfTestUtil.CountOccurrences(ops, "\nf\n"));
    }

    [Fact]
    public void PieChart_wrapsDrawingInSaveRestoreState()
    {
        var bytes = Save(new PieChart { Slices = [new PieSlice(1, ColorRgb.Black), new PieSlice(1, ColorRgb.White)] });

        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        Assert.Contains("q\n", ops, StringComparison.Ordinal);
        Assert.Contains("Q\n", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void PieChart_tagged_emitsFigureWithAltText()
    {
        var bytes = Save(
            new PieChart
            {
                Slices =
                [
                    new PieSlice(70, ColorRgb.Black, "Yes"),
                    new PieSlice(30, ColorRgb.White, "No"),
                ],
            },
            tagged: true);

        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        // Data-bearing chart is tagged as a Figure, not skipped as a decorative artifact.
        Assert.Contains("/Figure", ops, StringComparison.Ordinal);
        Assert.DoesNotContain("/Artifact", ops, StringComparison.Ordinal);

        // The Figure struct elem and its alternate text are written to the structure tree.
        var raw = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("/Figure", raw, StringComparison.Ordinal);
        Assert.Contains("/Alt", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void PieChart_decorativeTagged_emitsArtifactNotFigure()
    {
        var bytes = Save(
            new PieChart
            {
                Slices = [new PieSlice(1, ColorRgb.Black), new PieSlice(1, ColorRgb.White)],
                Decorative = true,
            },
            tagged: true);

        var ops = PdfTestUtil.DecompressAllFlatStreams(bytes);
        // Decorative charts are skipped by assistive technology: artifact, not Figure.
        Assert.Contains("/Artifact", ops, StringComparison.Ordinal);
        Assert.DoesNotContain("/Figure", ops, StringComparison.Ordinal);
    }

    [Fact]
    public void PieChart_clockwiseAndCounterClockwise_produceDifferentPaths()
    {
        PieSlice[] slices =
        [
            new PieSlice(1, ColorRgb.Black),
            new PieSlice(3, ColorRgb.White),
        ];
        var cw = PdfTestUtil.DecompressAllFlatStreams(Save(new PieChart { Slices = slices, Clockwise = true }));
        var ccw = PdfTestUtil.DecompressAllFlatStreams(Save(new PieChart { Slices = slices, Clockwise = false }));

        // Direction must actually affect the emitted geometry.
        Assert.NotEqual(cw, ccw);
    }

    [Fact]
    public void PieChart_nonFiniteDiameter_throws()
    {
        Assert.Throws<ArgumentException>(() => Save(new PieChart
        {
            Slices = [new PieSlice(1, ColorRgb.Black)],
            Diameter = double.NaN,
        }));
        Assert.Throws<ArgumentException>(() => Save(new PieChart
        {
            Slices = [new PieSlice(1, ColorRgb.Black)],
            Diameter = double.PositiveInfinity,
        }));
    }

    [Fact]
    public void PieChart_nonPositiveDiameter_throws()
    {
        Assert.Throws<ArgumentException>(() => Save(new PieChart
        {
            Slices = [new PieSlice(1, ColorRgb.Black)],
            Diameter = 0,
        }));
    }

    [Fact]
    public void PieChart_nonFiniteStartAngle_throws()
    {
        Assert.Throws<ArgumentException>(() => Save(new PieChart
        {
            Slices = [new PieSlice(1, ColorRgb.Black)],
            StartAngle = double.NaN,
        }));
    }
}
