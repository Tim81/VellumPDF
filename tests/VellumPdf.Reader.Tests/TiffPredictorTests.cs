// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary> Pins that TIFF predictor 2 is undone at 1, 2, 4, 8 and 16 bits per component per
/// ISO 32000-2 §7.4.4.4, with the sub-byte arms keeping the row's padding bits and the 16-bit arm
/// treating each sample as big-endian per §8.9.3. Every case here is a known-answer test computed
/// by hand from the cumulative-sum rule (<c>sample[i] += sample[i - colors]</c>, modulo
/// <c>2^BitsPerComponent</c>, restarting at the start of each row) and cross-checked
/// independently.
/// </summary>
public sealed class TiffPredictorTests
{
    private static ParsedStream MakeParsedStream(PdfDictionary dict, byte[] rawBody) =>
        new(dict, new ReadOnlyMemory<byte>(rawBody), bodyOffset: 0, objectNumber: 1, generation: 0);

    private static byte[] CompressZlib(byte[] data)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(data);
        return ms.ToArray();
    }

    private static byte[] Decode(byte[] raw, int columns, int colors, int bpc)
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
        var stream = MakeParsedStream(dict, compressed);
        var sink = new DiagnosticSink(cap: 10);

        var decoded = PdfFilters.Decode(stream, ReaderLimits.Defaults, diagnostics: sink);
        Assert.NotNull(decoded);
        Assert.Empty(sink.Diagnostics);
        return decoded!;
    }

    [Fact]
    public void Bpc1_Colors1_Columns8_oneRow()
    {
        // Row: 1 0 0 0 0 0 0 0 (from 0x80) accumulates to 1 1 1 1 1 1 1 1 (0xFF): each later bit
        // adds the running carry of the all-zero tail once the leading 1 has been added in.
        var decoded = Decode([0x80], columns: 8, colors: 1, bpc: 1);
        Assert.Equal(new byte[] { 0xFF }, decoded);
    }

    [Fact]
    public void Bpc1_Colors1_Columns5_twoRows()
    {
        // rowBytes = 1 (5 bits used, 3 padding bits preserved, never accumulated into).
        // Row 1 (0x87 = 10000111): samples 1 0 0 0 0, padding 111 -> predicted 1 1 1 1 1, padding
        // kept as 111 -> 11111111 = 0xFF.
        // Row 2 (0x80 = 10000000): samples 1 0 0 0 0, padding 000 -> predicted 1 1 1 1 1, padding
        // kept as 000 -> 11111000 = 0xF8.
        var decoded = Decode([0x87, 0x80], columns: 5, colors: 1, bpc: 1);
        Assert.Equal(new byte[] { 0xFF, 0xF8 }, decoded);
    }

    [Fact]
    public void Bpc2_Colors1_Columns4_wrapsModulo4()
    {
        // 0x55 = 01 01 01 01: samples 1 1 1 1 accumulate to 1 2 3 0 (mod 4) -> 01 10 11 00 = 0x6C.
        var decoded = Decode([0x55], columns: 4, colors: 1, bpc: 2);
        Assert.Equal(new byte[] { 0x6C }, decoded);
    }

    [Fact]
    public void Bpc4_Colors1_Columns4()
    {
        // 0x11 0x11: samples 1 1 1 1 accumulate to 1 2 3 4 -> 0x12 0x34.
        var decoded = Decode([0x11, 0x11], columns: 4, colors: 1, bpc: 4);
        Assert.Equal(new byte[] { 0x12, 0x34 }, decoded);
    }

    [Fact]
    public void Bpc4_Colors1_Columns3_paddingNibblePreserved()
    {
        // rowBytes = 2 (3 samples, one padding nibble). 0x11 0x1F: samples 1 1 1, padding F ->
        // predicted 1 2 3, padding kept as F -> 0x12 0x3F.
        var decoded = Decode([0x11, 0x1F], columns: 3, colors: 1, bpc: 4);
        Assert.Equal(new byte[] { 0x12, 0x3F }, decoded);
    }

    [Fact]
    public void Bpc4_Colors3_Columns3_componentWiseNotSampleWise()
    {
        // The vector that separates "predict from the same component, colors positions back"
        // (correct) from "predict from the immediately preceding nibble" (wrong). Raw nibbles: 1 2
        // 3 1 1 1 1 1 1 (9 samples, colors = 3: three RGB-like pixels). i0..i2 (no prior
        // component): 1, 2, 3 unchanged. i3 = raw[3] + predicted[0] = 1 + 1 = 2. i4 = raw[4] +
        // predicted[1] = 1 + 2 = 3. i5 = raw[5] + predicted[2] = 1 + 3 = 4. i6 = raw[6] +
        // predicted[3] = 1 + 2 = 3. i7 = raw[7] + predicted[4] = 1 + 3 = 4. i8 = raw[8] +
        // predicted[5] = 1 + 4 = 5. Predicted nibbles: 1 2 3 2 3 4 3 4 5 -> bytes 0x12 0x32 0x34
        // 0x34 0x50 (last nibble padding, 0).
        var decoded = Decode([0x12, 0x31, 0x11, 0x11, 0x10], columns: 3, colors: 3, bpc: 4);
        Assert.Equal(new byte[] { 0x12, 0x32, 0x34, 0x34, 0x50 }, decoded);
    }

    [Fact]
    public void Bpc8_existingKnownAnswer_unchanged()
    {
        var raw = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var decoded = Decode(raw, columns: 8, colors: 1, bpc: 8);
        // Cumulative sum modulo 256 of 1..8: 1 3 6 10 15 21 28 36.
        Assert.Equal(new byte[] { 1, 3, 6, 10, 15, 21, 28, 36 }, decoded);
    }

    [Fact]
    public void Bpc16_Colors1_Columns2_bigEndian()
    {
        // Two big-endian 16-bit samples, 0x0001 and 0x0001, accumulate to 0x0001 and 0x0002.
        var decoded = Decode([0x00, 0x01, 0x00, 0x01], columns: 2, colors: 1, bpc: 16);
        Assert.Equal(new byte[] { 0x00, 0x01, 0x00, 0x02 }, decoded);
    }

    /// <summary> The pre-existing <c>rows = data.Length / rowBytes</c> behaviour (Filters.cs) is
    /// unaffected by #98: a body ending mid-row still returns only whole rows, silently dropping
    /// the partial one.
    /// </summary>
    [Fact]
    public void BodyEndingMidRow_returnsOnlyWholeRows()
    {
        // Columns 8, Colors 1, BitsPerComponent 4 -> rowBytes = 4. One full row (4 bytes) plus a
        // 2-byte partial second row that must be dropped entirely, not decoded.
        var raw = new byte[] { 0x11, 0x11, 0x22, 0x22, 0x33, 0x33 };
        var decoded = Decode(raw, columns: 8, colors: 1, bpc: 4);
        Assert.Equal(4, decoded.Length);
    }
}
