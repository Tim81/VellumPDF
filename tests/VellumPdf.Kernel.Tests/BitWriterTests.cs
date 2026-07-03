// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.IO.Linearization;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the MSB-first <see cref="BitWriter"/> used by the linearization hint tables.
/// Expected byte patterns are cross-checked against qpdf's hint-stream encoding.
/// </summary>
public sealed class BitWriterTests
{
    [Fact]
    public void WriteBits_32bitValue_isBigEndian()
    {
        var w = new BitWriter();
        w.WriteBits(1, 32);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01 }, w.ToArray());
    }

    [Fact]
    public void WriteBits_msbFirst_withinByte()
    {
        // Single 1 bit followed by seven 0 bits -> 0x80 (high bit set).
        var w = new BitWriter();
        w.WriteBits(1, 1);
        w.WriteBits(0, 7);
        Assert.Equal(new byte[] { 0x80 }, w.ToArray());
    }

    [Fact]
    public void SkipToNextByte_padsCurrentByteWithZeros()
    {
        // One 1 bit then align -> the partial byte is flushed as 0x80.
        var w = new BitWriter();
        w.WriteBits(1, 1);
        w.SkipToNextByte();
        Assert.Equal(new byte[] { 0x80 }, w.ToArray());
    }

    [Fact]
    public void SkipToNextByte_whenAligned_isNoop()
    {
        var w = new BitWriter();
        w.WriteBits(0xAB, 8);
        w.SkipToNextByte();
        w.SkipToNextByte();
        Assert.Equal(new byte[] { 0xAB }, w.ToArray());
    }

    [Fact]
    public void Reproduces_qpdf_pageOffsetHeader_prefix()
    {
        // First two header fields of qpdf's page-offset hint table for the 3-page reference:
        // min_nobjects = 1 (32 bits), item2 (first-page offset, H-relative) = 529 = 0x211 (32 bits).
        var w = new BitWriter();
        w.WriteBits(1, 32);
        w.WriteBits(529, 32);
        Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x02, 0x11 }, w.ToArray());
    }

    [Fact]
    public void Reproduces_qpdf_deltaNobjects_column_withAlignment()
    {
        // delta_nobjects column for 3 pages at 1 bit each (values 1,0,0) then align -> 0x80.
        var w = new BitWriter();
        w.WriteBits(1, 1);
        w.WriteBits(0, 1);
        w.WriteBits(0, 1);
        w.SkipToNextByte();
        Assert.Equal(new byte[] { 0x80 }, w.ToArray());
    }

    [Fact]
    public void Reproduces_qpdf_deltaPageLength_column()
    {
        // delta_page_length column for 3 pages at 9 bits each (324,0,0) then align.
        // 324 = 0b101000100. Column bits: 101000100 000000000 000000000, padded to 4 bytes.
        var w = new BitWriter();
        w.WriteBits(324, 9);
        w.WriteBits(0, 9);
        w.WriteBits(0, 9);
        w.SkipToNextByte();
        // 27 bits -> 4 bytes: 10100010 00000000 00000000 000(00000)
        Assert.Equal(new byte[] { 0xA2, 0x00, 0x00, 0x00 }, w.ToArray());
    }
}
