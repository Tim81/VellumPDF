// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.IO.Linearization;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="HintStreamBuilder"/>. The golden case reproduces, byte-for-byte,
/// the hint stream qpdf 12.3.2 emits for a 3-page reference document (decoded from its
/// FlateDecode stream), so our encoder is pinned to the exact format the qpdf oracle validates.
/// </summary>
public sealed class HintStreamBuilderTests
{
    [Fact]
    public void Build_reproduces_qpdf_threePage_hintStream_byteForByte()
    {
        // Inputs measured from qpdf's own linearized output (see --show-linearization):
        //   page 0: 2 objects, 587 bytes, no shared refs
        //   pages 1,2: 1 object, 263 bytes, each references shared object #1
        //   shared table: two entries of length 263 and 324; both belong to the first page
        //   first page's first object sits 529 bytes into the hint coordinate system
        var pages = new List<HintStreamBuilder.PageHint>
        {
            new(ObjectCount: 2, Length: 587, SharedIds: []),
            new(ObjectCount: 1, Length: 263, SharedIds: [1]),
            new(ObjectCount: 1, Length: 263, SharedIds: [1]),
        };
        var shared = new List<HintStreamBuilder.SharedHint>
        {
            new(GroupLength: 263),
            new(GroupLength: 324),
        };

        var (body, sharedOffset) = HintStreamBuilder.Build(
            pages,
            firstPageOffset: 529,
            shared,
            nsharedFirstPage: 2,
            firstSharedObj: 0,
            firstSharedOffset: 0);

        var expected = new byte[]
        {
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x02, 0x11, 0x00, 0x01, 0x00, 0x00, 0x01, 0x07, 0x00, 0x09,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x07, 0x00, 0x09, 0x00, 0x01, 0x00, 0x02,
            0x00, 0x00, 0x00, 0x04, 0x80, 0xA2, 0x00, 0x00, 0x00, 0x60, 0x50, 0xA2, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x02, 0x00,
            0x00, 0x00, 0x00, 0x01, 0x07, 0x00, 0x06, 0x03, 0xD0, 0x00,
        };

        Assert.Equal(47, sharedOffset);
        Assert.Equal(expected, body);
    }
}
