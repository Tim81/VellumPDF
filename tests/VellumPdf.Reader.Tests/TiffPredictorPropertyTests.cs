// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using CsCheck;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Differential property test for TIFF predictor 2 (ISO 32000-2 §7.4.4.4): for a random shape and a
/// random row body, the implementation must agree byte for byte with an independently written
/// reference decoder. A known-answer test only ever proves the cases someone thought to write
/// down; this generates the shape space the cumulative-sum rule has to hold over.
/// <para>
/// The shape space covers every bit depth <c>Filters.cs</c> accepts and the whole colour count it
/// accepts (1 to 32). Column counts run to 200 rather than to the 1 048 576 the guard admits,
/// because the byte loops the implementation uses process a row in chunks and a defect that starts
/// partway along a row is only reachable once a generated row is longer than one such chunk; 200
/// columns clears that at every depth, while generating megabyte rows would cost far more than it
/// finds. Row counts run to 4, enough for the per-row restart to be exercised repeatedly.
/// </para>
/// </summary>
public sealed class TiffPredictorPropertyTests
{
    private static readonly Gen<(int Bpc, int Colors, int Columns, int Rows)> ShapeGen =
        Gen.Select(
            Gen.OneOfConst(1, 2, 4, 8, 16), Gen.Int[1, 32], Gen.Int[1, 200], Gen.Int[1, 4],
            (bpc, colors, columns, rows) => (bpc, colors, columns, rows));

    // Each shape needs a body of exactly rowBytes * rows: too short and Filters.cs's own
    // `rows = data.Length / rowBytes` would silently drop a partial row before the predictor ever
    // ran, which is a different behaviour than the one under test here.
    private static readonly Gen<(int Bpc, int Colors, int Columns, int Rows, byte[] Body)> CaseGen =
        ShapeGen.SelectMany(shape =>
        {
            var rowBytes = ((shape.Columns * shape.Colors * shape.Bpc) + 7) / 8;
            return Gen.Byte.Array[rowBytes * shape.Rows]
                .Select(body => (shape.Bpc, shape.Colors, shape.Columns, shape.Rows, body));
        });

    [Fact]
    public void ApplyTiffPredictor2_matchesIndependentReferenceDecoder()
    {
        CaseGen.Sample(c =>
        {
            var expected = ReferenceDecode(c.Body, c.Columns, c.Colors, c.Bpc);
            var actual = DecodeThroughImplementation(c.Body, c.Columns, c.Colors, c.Bpc);
            Assert.True(
                expected.AsSpan().SequenceEqual(actual),
                $"bpc={c.Bpc} colors={c.Colors} columns={c.Columns} rows={c.Rows} "
                + $"body={Convert.ToHexString(c.Body)}: expected {Convert.ToHexString(expected)}, "
                + $"got {Convert.ToHexString(actual)}");
        }, iter: FuzzBudget.Iterations);
    }

    // Unpacks every sample to a ushort[] in row-major order, adds each to the sample `colors`
    // positions earlier in the same row with an explicit modulus, and repacks. One bit-position
    // reader/writer covers all five bit depths uniformly, high-order bit first, which is a
    // different shape of code from Filters.cs's three separate arms (byte loop at 8 bits, ushort
    // loop at 16, shift-and-mask loop below that): written for clarity against §7.4.4.4's text, not
    // to share a code path with what it is checking.
    private static byte[] ReferenceDecode(byte[] data, int columns, int colors, int bpc)
    {
        var rowBytes = ((columns * colors * bpc) + 7) / 8;
        var rows = data.Length / rowBytes;
        var modulus = 1L << bpc;
        var result = (byte[])data.Clone();

        for (var row = 0; row < rows; row++)
        {
            var rowStart = row * rowBytes;
            var samplesPerRow = columns * colors;
            var samples = new ushort[samplesPerRow];

            for (var i = 0; i < samplesPerRow; i++)
                samples[i] = (ushort)ReadBits(result, rowStart, i * bpc, bpc);

            // Ascending order matters: sample i - colors already holds its own decoded value by
            // the time sample i reads it, whether that value was a row-leading base sample or was
            // itself just accumulated.
            for (var i = colors; i < samplesPerRow; i++)
                samples[i] = (ushort)((samples[i] + samples[i - colors]) % modulus);

            for (var i = 0; i < samplesPerRow; i++)
                WriteBits(result, rowStart, i * bpc, bpc, samples[i]);
        }
        return result;
    }

    private static long ReadBits(byte[] data, int rowStart, int bitOffset, int bitCount)
    {
        long value = 0;
        for (var b = 0; b < bitCount; b++)
        {
            var pos = bitOffset + b;
            var bit = (data[rowStart + (pos / 8)] >> (7 - (pos % 8))) & 1;
            value = (value << 1) | (uint)bit;
        }
        return value;
    }

    private static void WriteBits(byte[] data, int rowStart, int bitOffset, int bitCount, long value)
    {
        for (var b = 0; b < bitCount; b++)
        {
            var pos = bitOffset + b;
            var bit = (int)((value >> (bitCount - 1 - b)) & 1);
            var byteIndex = rowStart + (pos / 8);
            var shift = 7 - (pos % 8);
            data[byteIndex] = (byte)((data[byteIndex] & ~(1 << shift)) | (bit << shift));
        }
    }

    private static byte[] CompressZlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] DecodeThroughImplementation(byte[] raw, int columns, int colors, int bpc)
    {
        var compressed = CompressZlib(raw);
        var parms = new PdfDictionary()
            .Set(new PdfName("Predictor"), new PdfInteger(2))
            .Set(new PdfName("Columns"), new PdfInteger(columns))
            .Set(new PdfName("Colors"), new PdfInteger(colors))
            .Set(new PdfName("BitsPerComponent"), new PdfInteger(bpc));
        var dict = new PdfDictionary()
            .Set(PdfName.Filter, PdfName.FlateDecode)
            .Set(new PdfName("DecodeParms"), parms);
        var stream = new ParsedStream(
            dict, new ReadOnlyMemory<byte>(compressed), bodyOffset: 0, objectNumber: 1, generation: 0);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);
        Assert.NotNull(decoded);
        Assert.Empty(sink.Diagnostics);
        return decoded!;
    }

    private static class FuzzBudget
    {
        private const long DefaultIterations = 3_000;

        /// <summary>
        /// Iterations per property run, overridable via <c>VELLUMPDF_FUZZ_ITER</c>. A copy of
        /// <c>ImageExtractionFuzzTests.FuzzBudget</c>'s own copy of
        /// <c>ParserFuzzTests.FuzzBudget</c>, for the reason that one already gives: lifting it
        /// into a shared file would touch <c>VellumPdf.TestSupport</c>, which the image and text
        /// lanes both build against in parallel this milestone.
        /// </summary>
        internal static long Iterations
        {
            get
            {
                var raw = Environment.GetEnvironmentVariable("VELLUMPDF_FUZZ_ITER");
                return long.TryParse(raw, out var parsed) && parsed > 0 ? parsed : DefaultIterations;
            }
        }
    }
}
