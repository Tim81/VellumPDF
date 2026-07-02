// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Fonts.Cff;
using Xunit;

namespace VellumPdf.Conformance.Tests;

/// <summary>
/// Unit tests for CffWidths FontMatrix scaling. Verifies that the advance widths returned by
/// TryGetWidth are in PDF 1/1000 text-space units, not raw charstring-space units, and that a
/// non-default FontMatrix (e.g. a 2000-unit em with matrix[0] = 0.0005) is applied correctly.
/// </summary>
public sealed class CffWidthsTests
{
    // A synthetic CFF with two glyphs:
    //   GID 0 (.notdef): endchar only — uses defaultWidthX = 0.
    //   GID 1 (test):    raw advance 1000 charstring units (width operand 1000 + endchar).
    // The Top DICT carries FontMatrix [0.0005 0 0 0.0005 0 0] (2000-unit em).
    // Expected PDF width for GID 1: 1000 * 0.0005 * 1000 = 500.
    //
    // A standard 1000-unit em CFF (no FontMatrix in Top DICT) would return width = 1000 for GID 1.
    //
    // CFF layout (50 bytes total):
    //   [0]  CFF header: 01 00 04 01
    //   [4]  Name INDEX: 1 entry "Test"
    //   [13] Top DICT INDEX: 1 entry with FontMatrix op + CharStrings op
    //   [36] String INDEX: empty
    //   [38] Global Subr INDEX: empty
    //   [40] CharStrings INDEX: 2 glyphs
    private static readonly byte[] s_cff2000Em = BuildSynthCff(withFontMatrix: true);
    private static readonly byte[] s_cff1000Em = BuildSynthCff(withFontMatrix: false);

    [Fact]
    public void TryGetWidth_StandardEm_RawWidthPassedThrough()
    {
        // Without a FontMatrix, scale defaults to 1.0; raw width 1000 → PDF width 1000.
        var font = CffFont.Parse(s_cff1000Em);
        Assert.True(CffWidths.TryCreate(font, out var cw) && cw is not null);

        Assert.True(cw!.TryGetWidth(1, out var w));
        Assert.True(Math.Abs(w - 1000.0) <= 0.5,
            $"Standard-em width: expected ≈1000, got {w}");
    }

    [Fact]
    public void TryGetWidth_NonDefaultFontMatrix_ScalesWidthToPdfUnits()
    {
        // FontMatrix [0.0005 …] → scale 0.5; raw width 1000 → PDF width 500.
        var font = CffFont.Parse(s_cff2000Em);
        Assert.True(CffWidths.TryCreate(font, out var cw) && cw is not null);

        Assert.True(cw!.TryGetWidth(1, out var w));
        Assert.True(Math.Abs(w - 500.0) <= 0.5,
            $"2000-em scaled width: expected ≈500, got {w}");
    }

    [Fact]
    public void TryGetWidth_Notdef_ReturnsDefaultWidth()
    {
        // GID 0 (.notdef) has an empty-stack endchar; both nominalWidth and defaultWidth are 0,
        // so the result must be 0 regardless of the FontMatrix scale.
        var font = CffFont.Parse(s_cff2000Em);
        Assert.True(CffWidths.TryCreate(font, out var cw) && cw is not null);

        Assert.True(cw!.TryGetWidth(0, out var w));
        Assert.Equal(0.0, w);
    }

    // Builds a minimal but valid CFF binary with 2 glyphs and either a 2000-unit-em FontMatrix
    // (matrix[0]=0.0005, scale=0.5) or no FontMatrix (defaults to standard 1000-em, scale=1.0).
    private static byte[] BuildSynthCff(bool withFontMatrix)
    {
        // CharStrings:
        //   GID 0: [14] (endchar — defaultWidthX = 0)
        //   GID 1: [FA 7C 14] (integer 1000 + endchar — raw advance = 1000)
        // Integer 1000 in CFF two-byte encoding: (b0-247)*256 + b1 + 108 = 1000
        //   → b0=250 (FA), b1=(1000-108-768)=124 (7C). Check: (250-247)*256+124+108=768+124+108=1000 ✓
        byte[] csGid0 = [0x14];
        byte[] csGid1 = [0xFA, 0x7C, 0x14];

        // CFF real 0.0005 in nibble encoding:
        //   Lead byte: 0x1E (= 30 decimal, the CFF real-number marker per TN 5176 §4).
        //   Nibbles: 0, '.', 0, 0, 0, 5, end, end → bytes: 0x1E 0x0A 0x00 0x05 0xFF
        byte[] real0005 = [0x1E, 0x0A, 0x00, 0x05, 0xFF];
        byte intZero = 0x8B; // CFF integer 0 (139 → 139-139=0)

        // Build FontMatrix operands + op when requested.
        // [0.0005 0 0 0.0005 0 0] op 12 7 = 5+1+1+5+1+1+2 = 16 bytes
        byte[] fontMatrixBytes = withFontMatrix
            ? [.. real0005, intZero, intZero, .. real0005, intZero, intZero, 0x0C, 0x07]
            : [];

        // CharStrings INDEX is placed at the end of the header sections (computed below).
        // Layout: header(4) + NameIdx(9) + TopDictIdx + StringIdx(2) + GlobalSubrIdx(2) + CharStrIdx(10)
        // TopDictIdx = 5 (fixed header) + topDictBody bytes
        // topDictBody = fontMatrixBytes.Length + 1 (CharStrings int) + 1 (op 17)
        // But the CharStrings int size depends on the offset value — see below.
        // We know all section sizes except TopDictIdx, which depends on the CharStrings int size.
        // The CharStrings offset = 4+9 + topDictIdxSize + 2 + 2.
        // topDictIdxSize = 5 + topDictBody.Length.
        // topDictBody.Length = fontMatrixBytes.Length + intSize + 1.
        // intSize = 1 if value in [32,246] (i.e. decoded value in [-107,107]), else 2.
        // We know the value will be in the range [40,60], which is in [32,246], so intSize=1.
        // Therefore: topDictBody.Length = fontMatrixBytes.Length + 2.
        // topDictIdxSize = 5 + fontMatrixBytes.Length + 2 = 7 + fontMatrixBytes.Length.
        // CharStrings offset = 4+9 + 7+fontMatrixBytes.Length + 2 + 2
        //                    = 24 + fontMatrixBytes.Length.
        var charStrOffset = 24 + fontMatrixBytes.Length;

        // Verify the assumption that charStrOffset fits in a 1-byte CFF integer ([32,246] range
        // encodes values [-107, 107]; for values [108,363] range 247-250 is used — but offsets up
        // to ~100 are in the 1-byte range by value+139).
        // Actually: byte b encodes b-139. So b=b0 encodes b0-139. For value V, b0=V+139.
        // The 1-byte range is b0 in [32,246], i.e. V in [-107, 107].
        // charStrOffset = 24 + fontMatrixBytes.Length ≤ 24+16=40, which is in [-107,107] ✓.
        if (charStrOffset < -107 || charStrOffset > 107)
            throw new InvalidOperationException($"CharStrings offset {charStrOffset} out of 1-byte CFF int range.");

        byte charStrOffsetByte = (byte)(charStrOffset + 139);

        // Top DICT body: FontMatrix (optional) + CharStrings offset + op
        byte[] topDictBody = [.. fontMatrixBytes, charStrOffsetByte, 0x11]; // 0x11 = op 17 (CharStrings)

        // Name INDEX (4-byte name "Test"):
        //   00 01       count = 1
        //   01          offSize = 1
        //   01 05       offsets [1, 5] (name is 4 bytes, so end-offset = 1+4 = 5)
        //   54 65 73 74 "Test"
        byte[] nameIdx = [0x00, 0x01, 0x01, 0x01, 0x05, 0x54, 0x65, 0x73, 0x74];

        // Top DICT INDEX:
        //   00 01            count = 1
        //   01               offSize = 1
        //   01 xx            offsets [1, 1+topDictBody.Length]
        //   <topDictBody>
        var topDictEnd = (byte)(1 + topDictBody.Length);
        byte[] topDictIdx = [0x00, 0x01, 0x01, 0x01, topDictEnd, .. topDictBody];

        // String INDEX (empty): 00 00
        byte[] stringIdx = [0x00, 0x00];

        // Global Subr INDEX (empty): 00 00
        byte[] globalSubrIdx = [0x00, 0x00];

        // CharStrings INDEX with 2 glyphs:
        //   00 02       count = 2
        //   01          offSize = 1
        //   01 02 05    offsets [1, 2, 5] (glyph 0 = 1 byte, glyph 1 = 3 bytes)
        //   14          GID 0 charstring
        //   FA 7C 14    GID 1 charstring
        byte[] charStrIdx =
        [
            0x00, 0x02,
            0x01,
            0x01, 0x02, 0x05,
            .. csGid0,
            .. csGid1,
        ];

        // CFF header: major=1, minor=0, hdrSize=4, offSize=1
        byte[] header = [0x01, 0x00, 0x04, 0x01];

        return [.. header, .. nameIdx, .. topDictIdx, .. stringIdx, .. globalSubrIdx, .. charStrIdx];
    }
}
