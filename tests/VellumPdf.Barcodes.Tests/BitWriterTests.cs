// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Tests;

/// <summary>Tests for the MSB-first <see cref="BitWriter"/> used by the future QR/Micro QR bit-stream builders.</summary>
public sealed class BitWriterTests
{
    [Fact]
    public void WriteBits_msbFirst_withinByte()
    {
        var w = new BitWriter();
        w.WriteBits(1, 1);
        w.WriteBits(0, 7);
        Assert.Equal(new byte[] { 0x80 }, w.ToArray());
    }

    [Fact]
    public void WriteBits_32bitValue_isBigEndian()
    {
        var w = new BitWriter();
        w.WriteBits(1, 32);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, w.ToArray());
    }

    [Fact]
    public void ToArray_padsPartialFinalByte_withZeros()
    {
        var w = new BitWriter();
        w.WriteBits(1, 1);
        Assert.Equal(new byte[] { 0x80 }, w.ToArray());
    }

    [Fact]
    public void ToArray_whenAligned_addsNoExtraByte()
    {
        var w = new BitWriter();
        w.WriteBits(0xAB, 8);
        Assert.Equal(new byte[] { 0xAB }, w.ToArray());
    }

    [Fact]
    public void BitCount_tracksTotalBitsWritten()
    {
        var w = new BitWriter();
        w.WriteBits(0b101, 3);
        w.WriteBit(1);
        Assert.Equal(4, w.BitCount);
    }

    [Fact]
    public void ToArray_doesNotMutateState_soMoreBitsCanFollow()
    {
        var w = new BitWriter();
        w.WriteBits(1, 1);
        _ = w.ToArray();
        w.WriteBits(1, 1);
        w.WriteBits(0, 6);
        Assert.Equal(new byte[] { 0xC0 }, w.ToArray());
    }
}
