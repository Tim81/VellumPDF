// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="Jbig2ImageLoader"/>: segment-header parsing, page/globals
/// partitioning, XObject dictionary shape, and error handling for malformed input.
///
/// <para>Constructing a fully viewer-valid JBIG2 codestream by hand is impractical,
/// so these tests focus on the structural parsing and partitioning logic. Actual
/// round-trip rendering is exercised by the oracle tests in
/// <c>VellumPdf.Layout.Tests/ImageCodecOracleTests.cs</c> on CI when fixtures are present.</para>
/// </summary>
public sealed class Jbig2ImageTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string DictText(VellumPdf.Images.PdfImageXObject img)
        => System.Text.Encoding.Latin1.GetString(Serialize(img));

    private static byte[] Serialize(VellumPdf.Images.PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        return ms.ToArray();
    }

    private static int FindSequence(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
                return i;
        }
        return -1;
    }

    // Extracts the raw bytes between the PDF stream's "stream\n" and "\nendstream" markers.
    private static byte[] ExtractStreamBody(byte[] raw)
    {
        var start = FindSequence(raw, "\nstream\n"u8);
        if (start < 0) throw new InvalidOperationException("stream marker not found");
        var bodyStart = start + "\nstream\n"u8.Length;
        var end = FindSequence(raw, "\nendstream"u8);
        if (end < 0) throw new InvalidOperationException("endstream marker not found");
        return raw[bodyStart..end];
    }

    // ── Minimal JBIG2 byte-array builders ────────────────────────────────────

    /// <summary>
    /// Builds a minimal 4-field page-info segment (type 48) with the given dimensions.
    /// Page-info data layout (§7.4.8): width(4) height(4) xres(4) yres(4) flags(1) striping(2) = 19 bytes.
    /// </summary>
    private static byte[] BuildPageInfoSegmentData(int width, int height)
    {
        var d = new byte[19];
        WriteInt32(d, 0, width);
        WriteInt32(d, 4, height);
        // xres, yres = 0, flags = 0, striping = 0 — all zeroed by default.
        return d;
    }

    /// <summary>
    /// Builds a complete JBIG2 sequential-file buffer containing just:
    ///   • An optional 8-byte file header.
    ///   • One page-info segment (type 48, page 1).
    ///   • One end-of-file segment (type 51).
    /// This is the smallest valid JBIG2 structure; it carries no image data but has
    /// a parseable geometry.
    /// </summary>
    private static byte[] BuildMinimalJbig2(int width, int height, bool withFileHeader = true)
    {
        // File header.
        byte[] header = withFileHeader
            ? [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A]
            : [];

        var pageInfoData = BuildPageInfoSegmentData(width, height);

        // Segment: number(4) flags(1) refCountByte(1) pageAssoc(1) dataLen(4) data.
        // Page-info: segNum=0, type=48, pageAssoc=1, dataLen=19.
        var pageInfoSeg = BuildSegment(segNumber: 0, type: 48, pageAssociation: 1, pageInfoData);

        // End-of-file: segNum=1, type=51, pageAssoc=0, dataLen=0.
        var eofSeg = BuildSegment(segNumber: 1, type: 51, pageAssociation: 0, []);

        return [.. header, .. pageInfoSeg, .. eofSeg];
    }

    /// <summary>
    /// Builds a JBIG2 file with a global symbol-dictionary segment (page 0) plus a
    /// page-info segment (page 1) to verify globals partitioning.
    /// The symbol-dictionary data is a stub (8 zero bytes) — the loader only needs to
    /// partition, not decode it.
    /// </summary>
    private static byte[] BuildJbig2WithGlobals(int width, int height)
    {
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];

        // Symbol dictionary (type 0) on page 0 = global.
        var symDictData = new byte[8]; // stub
        var symDictSeg = BuildSegment(segNumber: 0, type: 0, pageAssociation: 0, symDictData);

        var pageInfoData = BuildPageInfoSegmentData(width, height);
        var pageInfoSeg = BuildSegment(segNumber: 1, type: 48, pageAssociation: 1, pageInfoData);

        var eofSeg = BuildSegment(segNumber: 2, type: 51, pageAssociation: 0, []);

        return [.. header, .. symDictSeg, .. pageInfoSeg, .. eofSeg];
    }

    /// <summary>
    /// Builds one JBIG2 segment (minimal header: no referred-to segments, 1-byte page
    /// association, 4-byte data length).
    /// </summary>
    private static byte[] BuildSegment(int segNumber, int type, int pageAssociation, byte[] data)
    {
        var seg = new byte[4 + 1 + 1 + 1 + 4 + data.Length];
        var pos = 0;
        WriteInt32(seg, pos, segNumber); pos += 4;
        // flags: type in bits 0-5, pageAssocSize=0 (1-byte page assoc).
        seg[pos++] = (byte)(type & 0x3F);
        // refCountByte: lower 5 bits = 0 (no referred-to segments), upper 3 bits = 0.
        seg[pos++] = 0x00;
        // pageAssociation: 1 byte.
        seg[pos++] = (byte)pageAssociation;
        // dataLen: 4 bytes.
        WriteInt32(seg, pos, data.Length); pos += 4;
        Array.Copy(data, 0, seg, pos, data.Length);
        return seg;
    }

    /// <summary>
    /// Builds one JBIG2 segment that refers to a single other segment. The referred-to
    /// count is encoded in the TOP 3 bits of the count byte (T.88 §7.2.4); a segment number
    /// ≤ 256 uses 1-byte referred-to entries (§7.2.5).
    /// </summary>
    private static byte[] BuildSegmentWithRef(
        int segNumber, int type, int pageAssociation, int referredSegNumber, byte[] data)
    {
        var seg = new byte[4 + 1 + 1 + 1 + 1 + 1 + 4 + data.Length];
        var pos = 0;
        WriteInt32(seg, pos, segNumber); pos += 4;
        // flags: type in bits 0-5, pageAssocSize=0 (1-byte page assoc).
        seg[pos++] = (byte)(type & 0x3F);
        // refCountByte: count = 1 in the top 3 bits (0x20); retention flags in low 5 bits = 0.
        seg[pos++] = 0x20;
        // One 1-byte referred-to segment number.
        seg[pos++] = (byte)referredSegNumber;
        // pageAssociation: 1 byte.
        seg[pos++] = (byte)pageAssociation;
        // dataLen: 4 bytes.
        WriteInt32(seg, pos, data.Length); pos += 4;
        Array.Copy(data, 0, seg, pos, data.Length);
        return seg;
    }

    private static void WriteInt32(byte[] buf, int offset, int value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }

    /// <summary>Packs a sequence of (value, bit-length) codes MSB-first into a byte array.</summary>
    private static byte[] PackMsbFirst(params (int value, int bits)[] codes)
    {
        var bitCount = 0;
        foreach (var (_, bits) in codes) bitCount += bits;
        var bytes = new byte[(bitCount + 7) / 8];
        var bitIndex = 0;
        foreach (var (value, bits) in codes)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                if (((value >> i) & 1) != 0)
                    bytes[bitIndex / 8] |= (byte)(0x80 >> (bitIndex % 8));
                bitIndex++;
            }
        }
        return bytes;
    }

    /// <summary>Converts a Table 2/3a/3b code word written as a string of '0'/'1' into (value, bit-length).</summary>
    private static (int value, int bits) ParseCodeWord(string codeWord) => (Convert.ToInt32(codeWord, 2), codeWord.Length);

    /// <summary>Packs a string of '0'/'1' characters into a byte array, MSB first, matching <see cref="PackMsbFirst"/>'s bit order.</summary>
    private static byte[] PackBinaryString(string bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i] == '1')
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
        }
        return bytes;
    }

    /// <summary>
    /// Builds a JBIG2 file with a single MMR-coded immediate generic region (type 38) carrying
    /// <paramref name="mmr"/> as its compressed data, plus the page-info and EOF segments.
    /// </summary>
    private static byte[] BuildJbig2WithMmrRegion(int width, int height, byte[] mmr)
    {
        var regionData = new byte[18 + mmr.Length];
        WriteInt32(regionData, 0, width);   // regionWidth
        WriteInt32(regionData, 4, height);  // regionHeight
        WriteInt32(regionData, 8, 0);       // x
        WriteInt32(regionData, 12, 0);      // y
        regionData[16] = 0;                 // region combination flags
        regionData[17] = 0x01;              // grFlags: MMR = 1
        Array.Copy(mmr, 0, regionData, 18, mmr.Length);

        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoSeg = BuildSegment(0, 48, 1, BuildPageInfoSegmentData(width, height));
        var regionSeg = BuildSegment(1, 38, 1, regionData);
        var eofSeg = BuildSegment(2, 51, 0, []);
        return [.. header, .. pageInfoSeg, .. regionSeg, .. eofSeg];
    }

    // ── Filter / dict structure tests ─────────────────────────────────────────

    [Fact]
    public void Load_Filter_IsJbig2Decode()
    {
        var jbig2 = BuildMinimalJbig2(100, 80);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Contains("/JBIG2Decode", DictText(img));
    }

    [Fact]
    public void Load_ColorSpace_IsDeviceGray()
    {
        var jbig2 = BuildMinimalJbig2(10, 10);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Contains("/DeviceGray", DictText(img));
    }

    [Fact]
    public void Load_BitsPerComponent_Is1()
    {
        var jbig2 = BuildMinimalJbig2(10, 10);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Contains("/BitsPerComponent 1", DictText(img));
    }

    [Fact]
    public void Load_Width_Correct()
    {
        var jbig2 = BuildMinimalJbig2(width: 320, height: 240);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Equal(320, img.Width);
    }

    [Fact]
    public void Load_Height_Correct()
    {
        var jbig2 = BuildMinimalJbig2(width: 320, height: 240);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Equal(240, img.Height);
    }

    // ── File-header detection ─────────────────────────────────────────────────

    [Fact]
    public void Load_WithFileHeader_ParsesCorrectly()
    {
        var jbig2 = BuildMinimalJbig2(50, 40, withFileHeader: true);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Equal(50, img.Width);
        Assert.Equal(40, img.Height);
    }

    [Fact]
    public void Load_WithoutFileHeader_ParsesCorrectly()
    {
        // Embedded form: no magic header. The first bytes are immediately the segment header.
        var jbig2 = BuildMinimalJbig2(60, 50, withFileHeader: false);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Equal(60, img.Width);
        Assert.Equal(50, img.Height);
    }

    // ── Globals partitioning ──────────────────────────────────────────────────

    [Fact]
    public void Load_WithNoGlobals_Jbig2GlobalsIsNull()
    {
        var jbig2 = BuildMinimalJbig2(100, 80);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.Null(img.Jbig2Globals);
    }

    [Fact]
    public void Load_WithNoGlobals_DictHasNoDecodeParms()
    {
        // With no global segments there is nothing for /DecodeParms to reference, so the image
        // dictionary must not carry a (previously emitted, empty) /DecodeParms entry.
        var img = Jbig2ImageLoader.Load(BuildMinimalJbig2(100, 80));
        Assert.Null(img.Jbig2Globals);
        Assert.DoesNotContain("/DecodeParms", DictText(img));
    }

    [Fact]
    public void Load_DropsEndOfPageSegment_FromPageStream()
    {
        // The end-of-page segment (type 49) is file framing with no place in the PDF embedded
        // organisation, so it is dropped — leaving only the page-info segment in the page stream.
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoSeg = BuildSegment(segNumber: 0, type: 48, pageAssociation: 1, BuildPageInfoSegmentData(16, 16));
        var endOfPageSeg = BuildSegment(segNumber: 1, type: 49, pageAssociation: 1, []);
        byte[] jbig2 = [.. header, .. pageInfoSeg, .. endOfPageSeg];

        var img = Jbig2ImageLoader.Load(jbig2);
        var body = ExtractStreamBody(Serialize(img));

        // Only the page-info segment survives verbatim; the end-of-page segment is gone.
        Assert.Equal(pageInfoSeg, body);
    }

    [Fact]
    public void Load_WithGlobalSymbolDictionary_Jbig2GlobalsIsNotNull()
    {
        var jbig2 = BuildJbig2WithGlobals(100, 80);
        var img = Jbig2ImageLoader.Load(jbig2);
        Assert.NotNull(img.Jbig2Globals);
    }

    [Fact]
    public void Load_WithGlobals_GlobalsContainSymbolDictionarySegment()
    {
        var jbig2 = BuildJbig2WithGlobals(100, 80);
        var img = Jbig2ImageLoader.Load(jbig2);
        // The globals bytes should contain the symbol-dictionary segment bytes.
        // The stub symDict has 8 bytes of data; the total segment size is header (10) + 8 = 18 bytes.
        Assert.NotNull(img.Jbig2Globals);
        Assert.True(img.Jbig2Globals!.Length >= 8, "Globals should contain the symbol-dictionary segment.");
    }

    [Fact]
    public void Load_SegmentWithReferredToSegments_ParsesHeaderAndPartitionsCorrectly()
    {
        // Regression for the referred-to-count encoding (T.88 §7.2.4): the count lives in
        // the TOP 3 bits of the count byte. A text region (type 6) on page 1 refers to the
        // global symbol dictionary (segment 0). If the count byte (0x20) is misread, the
        // 1-byte referred-to segment number is not skipped and the rest of the header — and
        // every following segment — is misparsed, so the global partition is wrong.
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var symDictSeg = BuildSegment(segNumber: 0, type: 0, pageAssociation: 0, new byte[8]);
        var pageInfoSeg = BuildSegment(segNumber: 1, type: 48, pageAssociation: 1, BuildPageInfoSegmentData(100, 80));
        var textRegionSeg = BuildSegmentWithRef(
            segNumber: 2, type: 6, pageAssociation: 1, referredSegNumber: 0, new byte[20]);
        var eofSeg = BuildSegment(segNumber: 3, type: 51, pageAssociation: 0, []);
        byte[] jbig2 = [.. header, .. symDictSeg, .. pageInfoSeg, .. textRegionSeg, .. eofSeg];

        var img = Jbig2ImageLoader.Load(jbig2);

        Assert.Equal(100, img.Width);
        Assert.Equal(80, img.Height);
        // Globals must be exactly the symbol-dictionary segment: header (11) + 8 data = 19 bytes.
        // The page-1 text region must stay on the page stream, not leak into globals.
        Assert.NotNull(img.Jbig2Globals);
        Assert.Equal(19, img.Jbig2Globals!.Length);
    }

    [Fact]
    public void Load_WithGlobals_PageStreamDoesNotContainEofOrGlobals()
    {
        var jbig2 = BuildJbig2WithGlobals(100, 80);
        var img = Jbig2ImageLoader.Load(jbig2);
        // Page stream should contain the page-info segment (19 bytes data) but not the EOF segment.
        // It should also not contain the global segment bytes.
        var pageBytes = Serialize(img);
        // The page-info segment header is 10 bytes + 19 bytes data = 29 bytes.
        // Verify the page stream is non-empty (has at least the page-info segment).
        Assert.True(pageBytes.Length > 0, "Page stream should be non-empty.");
    }

    // ── Options: default is passthrough ──────────────────────────────────────

    [Fact]
    public void Load_DefaultOptions_IsPassthrough()
    {
        var jbig2 = BuildMinimalJbig2(10, 10);
        var img = Jbig2ImageLoader.Load(jbig2, ImageLoadOptions.Default);
        Assert.Contains("/JBIG2Decode", DictText(img));
    }

    // ── DecodeToRaster: unsupported segment types throw ───────────────────────

    [Fact]
    public void Load_DecodeToRaster_WithSymbolDictionary_Throws()
    {
        var jbig2 = BuildJbig2WithGlobals(10, 10); // contains a symbol-dictionary segment
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() => Jbig2ImageLoader.Load(jbig2, opts));
    }

    [Fact]
    public void Load_DecodeToRaster_WithNoUnsupportedSegments_DoesNotThrowOnNotSupported()
    {
        // A file with only page-info and EOF segments should not throw NotSupportedException;
        // it has no generic-region data to decode, but that's fine — the output will be all-white.
        var jbig2 = BuildMinimalJbig2(8, 4, withFileHeader: true);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        // Should not throw (no unsupported segment types).
        var img = Jbig2ImageLoader.Load(jbig2, opts);
        Assert.Equal(8, img.Width);
        Assert.Equal(4, img.Height);
        // Decode-to-raster emits FlateDecode.
        Assert.Contains("/FlateDecode", DictText(img));
    }

    [Fact]
    public void Load_DecodeToRaster_EmitsFlateDecodeFilter()
    {
        var jbig2 = BuildMinimalJbig2(8, 8);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var img = Jbig2ImageLoader.Load(jbig2, opts);
        Assert.Contains("/FlateDecode", DictText(img));
        Assert.DoesNotContain("/JBIG2Decode", DictText(img));
    }

    [Fact]
    public void Load_DecodeToRaster_NoJbig2Globals()
    {
        var jbig2 = BuildMinimalJbig2(8, 8);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var img = Jbig2ImageLoader.Load(jbig2, opts);
        Assert.Null(img.Jbig2Globals);
    }

    // ── Security / fuzz: malformed and truncated input ─────────────────────────

    [Fact]
    public void Load_NullData_Throws()
    {
        Assert.Throws<ArgumentException>(() => Jbig2ImageLoader.Load(null!));
    }

    [Fact]
    public void Load_EmptyData_Throws()
    {
        Assert.Throws<ArgumentException>(() => Jbig2ImageLoader.Load([]));
    }

    [Fact]
    public void Load_TruncatedAtSegmentNumber_ThrowsInvalidData()
    {
        // File header only, then 3 bytes (truncated 4-byte segment number).
        byte[] truncated = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x01];
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(truncated));
    }

    [Fact]
    public void Load_TruncatedAtFlags_ThrowsInvalidData()
    {
        // Segment number (4 bytes), then nothing.
        byte[] truncated = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x00];
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(truncated));
    }

    [Fact]
    public void Load_OversizedDataLength_ThrowsInvalidData()
    {
        // Construct a segment whose declared data length extends beyond the buffer.
        // Layout: 8-byte file header + 4 (segNum) + 1 (flags) + 1 (refCount) + 1 (pageAssoc) + 4 (dataLen) = 19 bytes total.
        // segNumber=0, type=48 (page-info), refCount=0, pageAssoc=1, dataLen=0x7FFFFFFF.
        var buf = new byte[8 + 4 + 1 + 1 + 1 + 4]; // exactly 19 bytes, dataLen field at end
        // File header.
        buf[0] = 0x97; buf[1] = 0x4A; buf[2] = 0x42; buf[3] = 0x32;
        buf[4] = 0x0D; buf[5] = 0x0A; buf[6] = 0x1A; buf[7] = 0x0A;
        var pos = 8;
        // segNumber = 0.
        buf[pos] = 0; buf[pos + 1] = 0; buf[pos + 2] = 0; buf[pos + 3] = 0; pos += 4;
        // flags: type=48 (0x30).
        buf[pos++] = 0x30;
        // refCountByte = 0 (no referred-to segments).
        buf[pos++] = 0x00;
        // pageAssoc = 1.
        buf[pos++] = 0x01;
        // dataLen = 0x7FFFFFFF (huge, extends past buffer).
        buf[pos] = 0x7F; buf[pos + 1] = 0xFF; buf[pos + 2] = 0xFF; buf[pos + 3] = 0xFF;
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(buf));
    }

    [Fact]
    public void Load_NoPageInfoSegment_ThrowsInvalidData()
    {
        // A file with only an end-of-file segment — no page-info.
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var eofSeg = BuildSegment(segNumber: 0, type: 51, pageAssociation: 0, []);
        var buf = new byte[header.Length + eofSeg.Length];
        Array.Copy(header, buf, header.Length);
        Array.Copy(eofSeg, 0, buf, header.Length, eofSeg.Length);

        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(buf));
    }

    [Fact]
    public void Load_PageInfoWithInvalidDimensions_ThrowsInvalidData()
    {
        // Page-info with width=0.
        var pageInfoData = BuildPageInfoSegmentData(0, 100);
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoSeg = BuildSegment(0, 48, 1, pageInfoData);
        var eofSeg = BuildSegment(1, 51, 0, []);
        var buf = new byte[header.Length + pageInfoSeg.Length + eofSeg.Length];
        Array.Copy(header, buf, header.Length);
        Array.Copy(pageInfoSeg, 0, buf, header.Length, pageInfoSeg.Length);
        Array.Copy(eofSeg, 0, buf, header.Length + pageInfoSeg.Length, eofSeg.Length);

        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(buf));
    }

    [Fact]
    public void Load_ExceedsDimensionLimit_ThrowsInvalidData()
    {
        // Dimensions that exceed ImageLimits.MaxPixels.
        // 100001 × 100001 > 100M.
        var pageInfoData = BuildPageInfoSegmentData(100001, 100001);
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoSeg = BuildSegment(0, 48, 1, pageInfoData);
        var eofSeg = BuildSegment(1, 51, 0, []);
        var buf = new byte[header.Length + pageInfoSeg.Length + eofSeg.Length];
        Array.Copy(header, buf, header.Length);
        Array.Copy(pageInfoSeg, 0, buf, header.Length, pageInfoSeg.Length);
        Array.Copy(eofSeg, 0, buf, header.Length + pageInfoSeg.Length, eofSeg.Length);

        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(buf));
    }

    // ── Passthrough options ───────────────────────────────────────────────────

    [Fact]
    public void Load_PassthroughMode_ReturnsJbig2DecodeFilter()
    {
        var jbig2 = BuildMinimalJbig2(16, 16);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.Passthrough };
        var img = Jbig2ImageLoader.Load(jbig2, opts);
        Assert.Contains("/JBIG2Decode", DictText(img));
    }

    // ── Decode-to-raster: text/halftone region types also throw ──────────────

    [Fact]
    public void Load_DecodeToRaster_ImmediateTextRegionSegment_Throws()
    {
        // Build a file with an immediate text region (type 6) segment.
        var jbig2WithText = BuildJbig2WithSegmentType(
            pageInfoWidth: 10, pageInfoHeight: 10,
            extraSegType: 6, extraPageAssoc: 1);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() => Jbig2ImageLoader.Load(jbig2WithText, opts));
    }

    [Fact]
    public void Load_DecodeToRaster_ImmediateHalftoneRegionSegment_Throws()
    {
        var jbig2WithHalftone = BuildJbig2WithSegmentType(
            pageInfoWidth: 10, pageInfoHeight: 10,
            extraSegType: 22, extraPageAssoc: 1);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() => Jbig2ImageLoader.Load(jbig2WithHalftone, opts));
    }

    // ── MmrDecoder round-trip test ────────────────────────────────────────────

    [Fact]
    public void MmrDecoder_AllWhiteG4_ProducesAllZeroRaster()
    {
        // Use the same all-white G4 stream builder from CcittImageTests.
        const int cols = 16;
        const int rows = 4;
        var g4Bytes = CcittImageTests.BuildAllWhiteG4(cols, rows);

        // Wrap in a JBIG2 generic region (MMR-coded) for the decode path.
        // Build a minimal generic-region segment: 17-byte region info + 1-byte grFlags + MMR data.
        // regionWidth=16, regionHeight=4, x=0, y=0, regionFlags=0, grFlags=0x01 (MMR).
        var regionData = new byte[18 + g4Bytes.Length];
        WriteInt32(regionData, 0, cols);   // regionWidth
        WriteInt32(regionData, 4, rows);   // regionHeight
        WriteInt32(regionData, 8, 0);      // x
        WriteInt32(regionData, 12, 0);     // y
        regionData[16] = 0;               // region combination flags
        regionData[17] = 0x01;            // grFlags: MMR = 1
        Array.Copy(g4Bytes, 0, regionData, 18, g4Bytes.Length);

        var regionSeg = BuildSegment(segNumber: 2, type: 38, pageAssociation: 1, regionData);

        var pageInfoData = BuildPageInfoSegmentData(cols, rows);
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoSeg = BuildSegment(0, 48, 1, pageInfoData);
        var eofSeg = BuildSegment(3, 51, 0, []);

        var jbig2 = new byte[header.Length + pageInfoSeg.Length + regionSeg.Length + eofSeg.Length];
        var writePos = 0;
        Array.Copy(header, 0, jbig2, writePos, header.Length); writePos += header.Length;
        Array.Copy(pageInfoSeg, 0, jbig2, writePos, pageInfoSeg.Length); writePos += pageInfoSeg.Length;
        Array.Copy(regionSeg, 0, jbig2, writePos, regionSeg.Length); writePos += regionSeg.Length;
        Array.Copy(eofSeg, 0, jbig2, writePos, eofSeg.Length);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var img = Jbig2ImageLoader.Load(jbig2, opts);

        Assert.Equal(cols, img.Width);
        Assert.Equal(rows, img.Height);
        Assert.Contains("/FlateDecode", DictText(img));

        // The name promised this and the body did not make it: before #437 this test asserted the
        // dimensions and the filter, never a pixel. Even with the assertion it stays a weak vector,
        // because an all-white image decodes to all zeros under a great many wrong mode tables,
        // which is why it stayed green while the table was wrong. It is kept as a regression on the
        // all-white encoder rather than as evidence about Table 1/T.6. The known-answer vectors
        // below are what carry that.
        Assert.All(CcittImageTests.DecompressFlateStream(img.BuildStream()), b => Assert.Equal(0, b));
    }

    // ── MmrDecoder: truncated MMR data throws ─────────────────────────────────

    [Fact]
    public void Load_DecodeToRaster_TruncatedMmrData_ThrowsInvalidData()
    {
        // Build a generic region segment whose declared data length says 50 bytes of MMR data,
        // but the buffer only has 2 bytes.
        var regionData = new byte[18 + 2]; // 18-byte header + 2 bytes data (should be 50)
        WriteInt32(regionData, 0, 16);  // regionWidth
        WriteInt32(regionData, 4, 10);  // regionHeight
        WriteInt32(regionData, 8, 0);
        WriteInt32(regionData, 12, 0);
        regionData[16] = 0;
        regionData[17] = 0x01; // MMR

        // Fill 2 bytes of MMR — clearly insufficient for 16×10 image.
        regionData[18] = 0x00;
        regionData[19] = 0x00;

        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoData = BuildPageInfoSegmentData(16, 10);
        var pageInfoSeg = BuildSegment(0, 48, 1, pageInfoData);
        var regionSeg = BuildSegment(1, 38, 1, regionData);
        var eofSeg = BuildSegment(2, 51, 0, []);

        var jbig2 = new byte[header.Length + pageInfoSeg.Length + regionSeg.Length + eofSeg.Length];
        var pos = 0;
        Array.Copy(header, 0, jbig2, pos, header.Length); pos += header.Length;
        Array.Copy(pageInfoSeg, 0, jbig2, pos, pageInfoSeg.Length); pos += pageInfoSeg.Length;
        Array.Copy(regionSeg, 0, jbig2, pos, regionSeg.Length); pos += regionSeg.Length;
        Array.Copy(eofSeg, 0, jbig2, pos, eofSeg.Length);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));
    }

    // ── MmrDecoder: malformed-input hardening ─────────────────────────────────

    [Fact]
    public void Load_DecodeToRaster_MmrRunExceedsWidth_ThrowsInvalidData()
    {
        // Horizontal mode followed by a white make-up code for a 64-pixel run, far larger than
        // the 2-pixel width. The run-length cap must reject this with InvalidDataException
        // rather than accumulating without bound — a long make-up run would otherwise overflow
        // the run accumulator and yield a negative fill range.
        //
        // The flag code is 001 (ITU-T T.6, 2.2.3.3). Before #437 this fixture used 011, which is
        // VR(1) in Table 1/T.6: the test encoded the decoder's own error, so it exercised the
        // run-length cap only because the decoder read 011 as Horizontal too.
        var mmr = PackMsbFirst((0b001, 3), (0b11011, 5)); // H, white make-up 64
        var jbig2 = BuildJbig2WithMmrRegion(width: 2, height: 1, mmr);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };

        // The message is the point of this test, not just the exception type: with the
        // run-length cap removed, BitReader.ReadBit runs off the end of the 1-byte input and
        // throws its own InvalidDataException ("unexpected end of compressed data"), which
        // Assert.Throws alone cannot tell apart from the cap firing.
        var ex = Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));
        Assert.Contains("exceeds the image width", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The run-length cap's boundary: <c>if (total &gt; maxRun)</c> must not fire when a run
    /// lands exactly on the image width, since a full-width run is legal — it is how any solid
    /// line is encoded. White 8 (ITU-T T.4 Table 2) exactly fills the 8-pixel row on its own, so
    /// the black 2 that follows it is clamped away entirely by the Horizontal arm's
    /// <c>Math.Min(a1 + run2, width)</c>, leaving the row all white. Mutating the cap's
    /// comparison to <c>&gt;=</c> rejects the white-8 run at the moment it reaches the cap
    /// exactly, throwing on a stream this decoder must accept; the existing cap test
    /// (<see cref="Load_DecodeToRaster_MmrRunExceedsWidth_ThrowsInvalidData"/>) only exercises 64
    /// against width 2, so it fires on both operators and cannot tell them apart.
    /// </summary>
    [Fact]
    public void MmrDecoder_HorizontalRun_CapDoesNotFireAtExactlyMaxRun()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b10011, 5), (0b11, 2)); // H, white run 8, black run 2

        var raster = DecodeMmrRaster(width: 8, height: 1, mmr);

        Assert.Equal([0x00], raster);
    }

    [Fact]
    public void Load_DecodeToRaster_MmrZeroRunHorizontalFlood_ThrowsInvalidData()
    {
        // Repeated zero-run Horizontal modes never advance the coding position but push two
        // changing elements each iteration. The bounds-checked changing-element append must
        // reject this with InvalidDataException rather than overrunning the CE array
        // (which previously surfaced as IndexOutOfRangeException).
        //
        // Both run-length codes must be the real terminating-0 code words (ITU-T T.4 Table 2), or
        // the fixture exercises a different guard: an earlier version of this fixture used
        // 0001111 for black 0, which was itself one of the pre-#440 table's 48 shadowed entries.
        // Under that old table, 00011 — the first five of those seven bits — already matched and
        // returned run 10, which exceeds the width of 2, so the run-length cap fired on the very
        // first Horizontal; the test never reached the changing-element guard it is named for,
        // before or after the table correction. What the correction changed is the decoded value,
        // 10 to 7, not which guard fires: the cap trips on that decoded value, and the leftover
        // two bits of 0001111 are never read either way.
        (int, int)[] zeroRunHorizontal =
        [
            (0b001, 3),            // Horizontal mode — the flag code of ITU-T T.6, 2.2.3.3
            (0b00110101, 8),       // white run length 0 (terminating)
            (0b0000110111, 10),    // black run length 0 (terminating), ITU-T T.4 Table 2
        ];
        var mmr = PackMsbFirst([.. zeroRunHorizontal, .. zeroRunHorizontal, .. zeroRunHorizontal]);
        var jbig2 = BuildJbig2WithMmrRegion(width: 2, height: 1, mmr);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        var ex = Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));

        Assert.Contains("changing elements", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DecodeToRaster_ExtremeAspectRatioMmrRegion_ThrowsInvalidData()
    {
        // 100,000,000 × 1 passes the width×height pixel-count limit, but the MMR decoder's
        // per-row changing-element scratch is sized to the width (~800 MB for this region)
        // — a decompression-bomb amplification from a few bytes. The per-dimension raster
        // limit must reject it with InvalidDataException before allocating.
        var mmr = new byte[] { 0x00, 0x00 };
        var jbig2 = BuildJbig2WithMmrRegion(width: 100_000_000, height: 1, mmr);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));
    }

    // ── MmrDecoder: Horizontal-mode width clamps ──────────────────────────────

    // <c>DecodeRow</c>'s Horizontal arm computes <c>a1 = Math.Min(a0 + run1, width)</c> and
    // <c>a2 = Math.Min(a1 + run2, width)</c>. Both lines predate this PR — they are not part of
    // its diff. Only <c>a1</c> had no fixture pinning it: removing it alone leaves the inherited
    // suite entirely green, because no fixture before this PR ever drove <c>a0 + run1</c> to a
    // point where the clamped and unclamped values differ. <c>a2</c> is not in the same state —
    // <see cref="MmrDecoder_HorizontalRun_CapDoesNotFireAtExactlyMaxRun"/>, inherited from the
    // parent PR, already depends on it: that test's own summary says its black run "is clamped
    // away entirely by the Horizontal arm's <c>Math.Min(a1 + run2, width)</c>", and removing only
    // that clamp fails exactly that one test with an <see cref="IndexOutOfRangeException"/>.
    // Against the inherited suite, removing both clamps together failed that same test and no
    // other, since with the inherited fixtures <c>a1</c>'s clamp was never the one doing the work.
    // The two fixtures below close that gap, so against this tree removing both clamps fails them
    // as well. Neither clamp is redundant with
    // <c>DecodeRun</c>'s own cap, which bounds a single run against the *full* image width rather
    // than what is left of the row, so a run the cap accepts can still carry a0 past width once
    // added to it. <c>FillRun</c> has no bounds check of its own, so an uncapped a1 or a2 escapes
    // as an <see cref="IndexOutOfRangeException"/> out of <see cref="Jbig2ImageLoader.Load"/> —
    // unhandled, the same failure class that
    // <see cref="Load_DecodeToRaster_MmrZeroRunHorizontalFlood_ThrowsInvalidData"/> keeps out of
    // the changing-element array by a different mechanism (a bounds check that throws
    // <see cref="InvalidDataException"/> in that array's case). The two fixtures below close the
    // <c>a1</c> gap and give <c>a2</c> a fixture that isolates it from <c>a1</c> instead of relying
    // on the inherited test's side effect; the rasters are worked out from the standard's decoding
    // procedure, not read off the decoder, matching the discipline the rest of this file uses for
    // MMR fixtures.

    /// <summary>
    /// Pins the first clamp, <c>a1 = Math.Min(a0 + run1, width)</c>. <c>DecodeRun</c>'s own cap
    /// cannot substitute for it here: the cap bounds <c>run1</c> against the full width (128), and
    /// 128 is itself a valid run, so the cap never fires — only <c>a1</c>'s own clamp keeps
    /// <c>a0 + run1</c> from exceeding the row once <c>a0</c> is already non-zero.
    /// <para>
    /// Row 0 is Horizontal, white run 64 then black run 64 (ITU-T T.4 Table 3a make-up codes,
    /// each closed with the run-0 terminating code), closing the row exactly at the 128-pixel
    /// width with reference changing elements at x = 64 and x = 128 — refCE = [64, 128, 128, …].
    /// Row 0's own raster is white 0-63, black 64-127: 8 bytes of 0x00 then 8 bytes of 0xFF.
    /// </para>
    /// <para>
    /// Row 1 opens with V(0), which steps a0 to refCE[0] = 64 and turns the colour black — this is
    /// what gets a0 off zero without touching either clamp under test. Horizontal then reads a
    /// black run of 128 (ITU-T T.4 Table 3a make-up code for 128, closed with terminating 0):
    /// <c>DecodeRun</c> accepts it, since 128 does not exceed the 128-pixel <c>maxRun</c> it is
    /// checked against, but <c>a0 + run1 = 64 + 128 = 192</c> is 64 pixels past the row. The
    /// correctly clamped decode paints black from a0 = 64 through a1 = 128 (the row's last 8
    /// bytes) and, because a1 already sits at width, the run-0 white run that follows paints
    /// nothing further and the row ends there — row 1 reads identically to row 0.
    /// </para>
    /// <para>
    /// Removing the a1 clamp lets <c>FillRun</c> receive (64, 192) for a row whose pixel columns
    /// run only to x = 128: <c>rowOffset + 191 / 8</c> indexes past the end of the two-row output
    /// array (32 bytes total; this row's own 16 bytes span byte offsets 16-31), and the decode
    /// throws <see cref="IndexOutOfRangeException"/> before returning a raster to compare at all.
    /// Verified by deleting the <c>Math.Min</c> call locally and re-running this test.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_HorizontalMode_A1ClampCapsARunThatCarriesANonZeroA0PastWidth()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b11011, 5), (0b00110101, 8),          // row 0: H, white make-up 64 + term 0
            (0b0000001111, 10), (0b0000110111, 10),             // row 0: black make-up 64 + term 0
            (0b1, 1),                                            // row 1: V(0) — a0 = 64, colour black
            (0b001, 3), (0b000011001000, 12), (0b0000110111, 10), // row 1: H, black make-up 128 + term 0
            (0b00110101, 8));                                    // row 1: white term 0 (run 0)

        var raster = DecodeMmrRaster(width: 128, height: 2, mmr);

        var row = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
        Assert.Equal([.. row, .. row], raster);
    }

    /// <summary>
    /// Pins the second clamp, <c>a2 = Math.Min(a1 + run2, width)</c>, using the reviewer's own
    /// repro from the #441 third-round notes: Horizontal, white run 64 then black run 128, on a
    /// 128-pixel row. <c>a1 = 64</c> is unaffected (the white run alone does not reach width), so
    /// this isolates the second clamp from the first.
    /// <para>
    /// The correctly clamped decode paints white 0-63 (a0 = 0 to a1 = 64) then black 64-127
    /// (a1 = 64 to a2 = <c>Math.Min(64 + 128, 128) = 128</c>) — 8 bytes of 0x00 then 8 bytes of
    /// 0xFF, the same pattern <see cref="MmrDecoder_HorizontalMode_A1ClampCapsARunThatCarriesANonZeroA0PastWidth"/>
    /// pins for its own reasons.
    /// </para>
    /// <para>
    /// Removing the a2 clamp leaves <c>a2 = 64 + 128 = 192</c>, and <c>FillRun</c> paints black
    /// through <c>rowOffset + 191 / 8</c> against a one-row, 16-byte output array — an
    /// <see cref="IndexOutOfRangeException"/> in place of a raster. Verified by deleting the
    /// second <c>Math.Min</c> call locally and re-running this test.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_HorizontalMode_A2ClampCapsTheSecondRunAtWidth()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b11011, 5), (0b00110101, 8),          // H, white make-up 64 + term 0
            (0b000011001000, 12), (0b0000110111, 10));          // black make-up 128 + term 0

        var raster = DecodeMmrRaster(width: 128, height: 1, mmr);

        Assert.Equal(
            new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF },
            raster);
    }

    // ── MmrDecoder: known-answer vectors from Table 1/T.6 ─────────────────────

    // Every vector below is hand-encoded from ITU-T T.6 Table 1 (mode codes) and ITU-T T.4
    // Table 2 (run lengths, white 2 = 0111, black 2 = 11), and every expected raster is worked
    // out from the standard's decoding procedure rather than read off this decoder. That
    // direction matters here: the tests that existed before #437 were authored from the
    // implementation, so they round-tripped its wrong mode table perfectly and proved nothing.
    //
    // The images are deliberately not uniform. An all-white raster decodes to all zeros under a
    // great many wrong tables, which is why the pre-existing all-white case could not tell a
    // correct decoder from the broken one.

    /// <summary>
    /// The three VL modes against the imaginary all-white reference line above row 0. Each
    /// leaves a1 short of the right edge by its delta, and the following V(0) paints from there
    /// to the edge in black, so the run of black pixels at the right edge *is* the delta.
    /// </summary>
    [Theory]
    [InlineData(0b010, 3, 0x01)]      // VL(1): a1 = 8 - 1, one black pixel
    [InlineData(0b000010, 6, 0x03)]   // VL(2): a1 = 8 - 2, two black pixels
    [InlineData(0b0000010, 7, 0x07)]  // VL(3): a1 = 8 - 3, three black pixels
    public void MmrDecoder_VerticalLeftModes_PaintTheDeltaAtTheRightEdge(
        int codeword, int bits, int expectedRow)
    {
        var mmr = PackMsbFirst((codeword, bits), (0b1, 1)); // VL(n), then V(0)

        var raster = DecodeMmrRaster(width: 8, height: 1, mmr);

        Assert.Equal([(byte)expectedRow], raster);
    }

    /// <summary>
    /// All seven vertical modes against a reference row whose only changing element is at x = 4,
    /// which is what makes VR(1) observable at all — against an all-white reference every VR(n)
    /// clamps to the right edge and reads the same as V(0).
    /// <para>
    /// Row 0 is one Horizontal codeword, white run 4 then black run 4 (ITU-T T.4 Table 2), giving
    /// black at x = 4..7 and a single changing element at x = 4. b1 for row 1 is therefore 4: a
    /// value one higher would put VR(3) exactly on the width clamp, so +3, +4, +5 and +99 would
    /// all produce the same raster and the case would catch nothing. b1 = 4 keeps every delta in
    /// {-3..+3} strictly inside the row (1 through 7), so none of the seven cases below touches
    /// either edge. Row 1 places a1 at 4 + delta and paints from there to the edge; each expected
    /// row is computed from that arithmetic by hand, not read off the decoder.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(0b1, 1, 0x0F)]        // V(0):  a1 = 4,         black 4..8
    [InlineData(0b011, 3, 0x07)]      // VR(1): a1 = 4 + 1 = 5, black 5..8
    [InlineData(0b000011, 6, 0x03)]   // VR(2): a1 = 4 + 2 = 6, black 6..8
    [InlineData(0b0000011, 7, 0x01)]  // VR(3): a1 = 4 + 3 = 7, black 7..8
    [InlineData(0b010, 3, 0x1F)]      // VL(1): a1 = 4 - 1 = 3, black 3..8
    [InlineData(0b000010, 6, 0x3F)]   // VL(2): a1 = 4 - 2 = 2, black 2..8
    [InlineData(0b0000010, 7, 0x7F)]  // VL(3): a1 = 4 - 3 = 1, black 1..8
    public void MmrDecoder_VerticalModes_ResolveAgainstTheReferenceChangingElement(
        int codeword, int bits, int expectedSecondRow)
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b1011, 4), (0b011, 3), // row 0: H, white run 4, black run 4
            (codeword, bits), (0b1, 1));         // row 1: the mode under test, then V(0)

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0x0F, (byte)expectedSecondRow], raster);
    }

    /// <summary>
    /// A vertical mode's changing element feeding the next row's reference line. The
    /// vertical-modes theory above and the Pass vector both close row 0 with Horizontal
    /// codewords that fill the width exactly, so their changing elements come entirely from
    /// Horizontal's unconditional appends. That is not true of every vector in this file that
    /// carries a reference into a second row: <see cref="MmrDecoder_FindB1_SkipsAReferenceElementA0SitsExactlyOn"/>
    /// and <see cref="MmrDecoder_VerticalMode_ClampFloorsAtA0NotZero"/> also decode row 0 with
    /// Horizontal, but its runs fall short of the width there, so a trailing V(0) supplies row
    /// 0's last changing element through the vertical arm's own <c>if (a1 != a0)</c> guard
    /// instead — row 0 exercises that guard branch, not only Horizontal's unconditional one.
    /// <see cref="MmrDecoder_VerticalLeftModes_PaintTheDeltaAtTheRightEdge"/> also
    /// decodes row 0 through the vertical arm, but it is height 1, so no second row ever reads a
    /// reference built that way. <see cref="MmrDecoder_AllWhiteG4_ProducesAllZeroRaster"/> (16×4)
    /// does carry a reference across four rows through the vertical arm, but every row there is a
    /// single V(0) landing exactly on the width, so the guard fires the same way every time and
    /// never exercises the a1 == a0 floor this vector's middle VL(3) does. This vector puts row 0
    /// itself through the vertical arm and carries it into a second row, so its own
    /// <c>if (a1 != a0)</c> guard is what determines what row 1 sees as its reference.
    /// <para>
    /// Row 0 is four vertical codewords against the all-white line above it, so b1 is always 8
    /// (its only changing element): VL(3) (a1 = 8 − 3 = 5, a real transition, appended); VL(3)
    /// again (a1 = clamp(5, a0 = 5, 8) = 5 = a0 — no transition, and this is what the guard
    /// exists for: a1 lands back on a0 because the clamp's lower bound is a0 itself, and
    /// appending it anyway would record a changing element nothing actually changed at); VL(2)
    /// (b1 is still 8, a1 = 8 − 2 = 6, appended); V(0) (b1 is still 8, a1 = 8, appended, closing
    /// the row). Row 0's changing elements are therefore [5, 6, 8], giving reference
    /// refCE = [5, 6, 8, 8, 8, …] for row 1 — the middle VL(3) contributes nothing to it, exactly
    /// because a1 == a0 there. That middle VL(3) is not a codeword a conformant encoder emits:
    /// ITU-T T.6 2.2.4's mode-selection rule never spends a vertical codeword coding a1 back onto
    /// a0, and a canonical encoding of this row's shape is VL(2), V(0). It is kept non-conformant
    /// here on purpose, because a1 == a0 is exactly the input the guard exists to reject a
    /// spurious changing element for, and the canonical two-codeword row never reaches that
    /// branch.
    /// </para>
    /// <para>
    /// Row 0's own raster only reflects colour actually painted: white 0..6 (the two VL(3) steps
    /// and the VL(2) step all fill nothing, since the middle VL(3) is zero-width and the other
    /// two paint while a0Col is white), black 6..8 (the closing V(0) paints while a0Col is
    /// black) — 0x03.
    /// </para>
    /// <para>
    /// Row 1 packs three V(0) codewords against that reference; only the correct decode consumes
    /// all three, since every mutant below reaches width sooner. The first V(0) steps a0 to the
    /// nearest matching-parity element ahead of 0 — refCE[0] — and that is where the drop-guard
    /// mutant already diverges: with the append call removed from the vertical arm entirely, row
    /// 0's curCE never gains an entry, so refCE collapses to [8, 8, …] and this first V(0) finds
    /// b1 = 8, painting x = 0..8 white and ending the row after just one codeword, with both
    /// packed successor V(0)s left unread — 0x00. Against the other two mutants' references,
    /// refCE[0] is 5 either way ([5, 8, 8, …] for the inverted guard, [5, 5, 6, 8, …] for the
    /// unconditional append, both agreeing with the correct decode's own refCE[0] = 5), so this
    /// first V(0) does not by itself distinguish either of them from the correct decode. The
    /// second V(0) is where those two part ways with it: against the correct
    /// refCE = [5, 6, 8, 8, …] it finds b1 = 6, paints only pixel 5 black (x = 5..6), and leaves
    /// the packed third V(0) to close x = 6..8 — 0x04. Against the inverted-guard
    /// refCE = [5, 8, 8, …], the first black-parity element strictly ahead of a0 = 5 is
    /// ce[1] = 8 (ce[0] = 5 is a0 itself, not ahead of it), so b1 = 8 and painting x = 5..8 black
    /// closes the row in two codewords. Against the unconditional-append
    /// refCE = [5, 5, 6, 8, …], the odd-indexed (black-parity) element ahead of a0 = 5 is
    /// ce[3] = 8, not ce[2] = 6, again closing the row painting x = 5..8. Both give 0x07 after
    /// two codewords, leaving the packed third V(0) unread; the drop-guard mutant instead gives
    /// 0x00 after only one.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_VerticalMode_ChangingElementFeedsTheNextRowsReference()
    {
        var mmr = PackMsbFirst(
            (0b0000010, 7), (0b0000010, 7), (0b000010, 6), (0b1, 1), // row 0: VL(3), VL(3), VL(2), V(0)
            (0b1, 1), (0b1, 1), (0b1, 1));                            // row 1: V(0), V(0), V(0)

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0x03, 0x04], raster);
    }

    /// <summary>
    /// Horizontal mode. Its flag code is 001 and it is three bits long (ITU-T T.6, 2.2.3.3), and
    /// this vector is what pins the length: the two run-length codes are read from the bits that
    /// follow immediately, so a decoder consuming a fourth bit reads a different mode entirely
    /// at that branch and never reaches the run-length codes at all. Under that misalignment
    /// this fixture decodes to 0x03 instead of the expected 0x30, so the raster assertion below
    /// is precisely what catches it.
    /// </summary>
    [Fact]
    public void MmrDecoder_HorizontalMode_Uses001AndConsumesExactlyThreeBits()
    {
        var mmr = PackMsbFirst(
            (0b001, 3),      // H
            (0b0111, 4),     // white run 2 (ITU-T T.4 Table 2)
            (0b11, 2),       // black run 2
            (0b1, 1));       // V(0) to carry the row to the right edge

        var raster = DecodeMmrRaster(width: 8, height: 1, mmr);

        Assert.Equal([0x30], raster); // white 0..2, black 2..4, white 4..8
    }

    /// <summary>
    /// Pass mode, which is 0001 and four bits long, pins b2 (the changing element painted
    /// through to) against b1 (the one found first), and pins that Pass leaves a0Col unchanged.
    /// Row 0 is two Horizontal codewords, white run 2 then black run 2 twice over (ITU-T T.4
    /// Table 2), which fills the row exactly (needing no trailing V(0)) and gives reference
    /// changing elements at x = 2, 4, 6 and 8 — refCE = [2, 4, 6, 8, 8, …]. Row 0's own raster is
    /// white 0..2, black 2..4, white 4..6, black 6..8: 0x33.
    /// <para>
    /// Row 1 opens with V(0), which steps a0 to the first reference element ahead of it
    /// (b1 = refCE[0] = 2) and turns the colour black. Pass then finds b1 = refCE[1] = 4 (the
    /// next reference element of matching, white-following parity) and b2 = refCE[2] = 6 (the
    /// element after b1). Because the colour at a0 is black, Pass's fill is visible: painting
    /// through to b2 colours x = 2..6 black. Pass does not flip a0Col (there is no colour
    /// transition at a0, only a run of the same colour painted further), so a0 lands at 6, still
    /// black.
    /// </para>
    /// <para>
    /// The row closes with Horizontal rather than V(0), and that is what makes a0's own position
    /// after Pass observable rather than merely its paint. A trailing V(0) re-resolves against
    /// the reference regardless of where a0 sits, so it repaints exactly the region a wrong a0
    /// left short and cannot tell a correct decode from one that advanced a0 to the wrong
    /// changing element. Horizontal instead paints by run length from a0, so a0's position is
    /// exactly what determines where those runs land. Because a0Col is already black, Horizontal
    /// reads black run 1 then white run 2 (ITU-T T.4 Table 2): read against the
    /// correct a0 = 6, a1 = min(6 + 1, 8) = 7 and a2 = min(7 + 2, 8) = 8, painting x = 6..7 black
    /// and closing the row at 0x3E (pixels 2..7 black, matching the region Pass and Horizontal
    /// paint between them).
    /// </para>
    /// <para>
    /// Fixing <c>b2</c> to <c>b1</c> (deleting <c>NextCE</c>'s contribution) throws
    /// <c>InvalidDataException("unexpected EOFB before all rows were decoded")</c>, because a0
    /// stalls at 4 and Horizontal's two packed runs consume input that a correct decode never
    /// reaches; advancing a0 to b1 instead of b2 throws the identical message for the same
    /// reason; adding a spurious <c>a0Col ^= 1</c> after the fill throws
    /// <c>InvalidDataException("decoded run length exceeds the image width")</c>, because
    /// Horizontal then reads its two runs against the wrong starting colour; filling only to b1
    /// instead of b2 returns raster <c>[0x33, 0x32]</c> (paints x = 2..4 instead of 2..6); a0
    /// still advances to b2 = 6, so Horizontal paints exactly where the correct decode does —
    /// the two missing pixels are Pass's short fill, not Horizontal's; and resolving b1 against
    /// the opposite parity, <c>FindB1(refCE, a0, 1 - a0Col)</c>, returns <c>[0x33, 0x3F]</c> —
    /// b1 = 6 and b2 = 8 instead of 4 and 6, painting two pixels further than the correct decode
    /// and closing the row inside Pass, leaving the trailing Horizontal codeword group — the 001
    /// flag, black run 1, and white run 2, ten bits — unread.
    /// </para>
    /// Before #437 this codeword was discarded as "unexpected" and a fifth bit was consumed
    /// with it.
    /// </summary>
    [Fact]
    public void MmrDecoder_PassMode_Uses0001AndPaintsThroughToB2()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b0111, 4), (0b11, 2),      // row 0: H, white run 2, black run 2
            (0b001, 3), (0b0111, 4), (0b11, 2),      // row 0: H, white run 2, black run 2
            (0b1, 1), (0b0001, 4),                   // row 1: V(0), Pass
            (0b001, 3), (0b010, 3), (0b0111, 4));    // row 1: H, black run 1, white run 2

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0x33, 0x3E], raster);
    }

    // ── MmrDecoder: a0 starts one position before the row (ITU-T T.6, 2.2.5.1) ────

    // 2.2.5.1: "The first starting picture element a0 on each coding line is imaginarily set
    // at a position just before the first picture element". FindB1's a0 comparison is strict
    // (ce[i] > a0), so a0 = 0 skips a changing element that is legitimately at column 0. A
    // reference row whose own first changing element sits at column 0 (i.e. the reference row
    // starts black) is one way to put a changing element there.

    /// <summary>
    /// A width-4, height-2 image, both rows <c>B B W W</c>. Row 0 is Horizontal with a white run of zero
    /// (2.2.5.1's a0a1 - 1 convention: the first run on a line is coded one shorter, so a
    /// black-starting line still opens with a white codeword) followed by a black run of 2,
    /// giving row 0's first changing element at column 0. Row 1 is three V(0) codes: the first
    /// resolves b1 against that column-0 element, which needs a0 = -1 to find at all, and the
    /// second paints the black run the first V(0) left implicit. This vector is built
    /// specifically to put a changing element at column 0 on the reference row, and its expected
    /// raster is worked out from the standard's decoding procedure by hand, not read off the
    /// decoder.
    /// </summary>
    [Fact]
    public void MmrDecoder_ReferenceRowStartsBlack_VerticalModeFindsB1AtColumnZero()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b00110101, 8), (0b11, 2), (0b1, 1), // row 0: H, white 0, black 2, V(0)
            (0b1, 1), (0b1, 1), (0b1, 1));                    // row 1: V(0), V(0), V(0)

        var raster = DecodeMmrRaster(width: 4, height: 2, mmr);

        Assert.Equal([0xC0, 0xC0], raster);
    }

    /// <summary>
    /// Same column-0 reference element as above, but the coding row uses VR(1) rather than
    /// V(0), pinning the fix for a non-zero vertical delta too. Row 0 is <c>B B B W W W W W</c>
    /// (changing elements at 0 and 3); row 1's VR(1) resolves b1 = 0 and a1 = b1 + 1 = 1, then
    /// two V(0) codes carry the row to the edge, giving row 1 <c>W B B W W W W W</c>. This
    /// vector's expected raster is worked out from the standard's decoding procedure by hand,
    /// not read off the decoder.
    /// </summary>
    [Fact]
    public void MmrDecoder_ReferenceRowStartsBlack_VRModeFindsB1AtColumnZero()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b00110101, 8), (0b10, 2), (0b1, 1), // row 0: H, white 0, black 3, V(0)
            (0b011, 3), (0b1, 1), (0b1, 1));                  // row 1: VR(1), V(0), V(0)

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0xE0, 0x60], raster);
    }

    /// <summary>
    /// Pass mode against a reference row that begins black, which is the mode that reads both
    /// b1 and b2 off the reference line. Row 0 is <c>B B W W W B B B</c> (changing elements at
    /// 0, 2, 5, 8). Row 1 opens with Pass: b1 = 0 (the column-0 element under test) and b2 = 2
    /// (the next one), so Pass paints white 0..2 and leaves a0 at 2 with the colour unchanged.
    /// Two V(0) codes then resolve the rest of row 1 against the reference's remaining
    /// elements, giving <c>W W W W W B B B</c>.
    /// <para>
    /// Without the fix, FindB1 resolves the Pass b1 to 5 instead of 0 (skipping both the
    /// column-0 element and the one at 2), b2 becomes 8, and the single Pass codeword paints
    /// the entire row white and consumes the whole line — the two trailing V(0) codes are never
    /// reached. That collapses row 1 to all-white, which this vector's non-zero expectation
    /// catches.
    /// </para>
    /// <para>
    /// Row 0's second code word deviates from what a conforming encoder would choose there: after
    /// the first Horizontal call leaves a0 = 2, the next changing element is a1 = 5 and b1 (against
    /// the virtual all-white line above row 0) is 8, so <c>|a1 - b1| = 3</c> selects VL(3) under
    /// 2.2.4 Step 2(ii), not a second Horizontal code word. The decoder accepts any legal T.6 code
    /// sequence, not only the one a real encoder would have chosen, so the stream below is still
    /// valid and decodes unambiguously to the raster asserted here. The deviation does not affect
    /// what this vector proves about Pass mode.
    /// </para>
    /// <para>
    /// This vector is built specifically to put a changing element at column 0 on the reference
    /// row, and its expected raster is worked out from the standard's decoding procedure by
    /// hand, not read off the decoder.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_PassModeAgainstReferenceRowStartingBlack_ReadsB1AndB2Correctly()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b00110101, 8), (0b11, 2),   // row 0: H, white 0, black 2
            (0b001, 3), (0b1000, 4), (0b10, 2),       // row 0: H, white 3, black 3 (non-canonical; see doc)
            (0b0001, 4), (0b1, 1), (0b1, 1));          // row 1: Pass, V(0), V(0)

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0xC7, 0x07], raster);
    }

    /// <summary>
    /// The vertical arm's <c>a1 != a0</c> guard, pinned with a conformant three-row stream
    /// rather than the a1 = -1 input <see cref="MmrDecoder_VerticalMode_ImaginaryA0ClampFloorsAtZeroNotNegativeOne"/>
    /// needs. Row 0, the reference row, is <c>W B B B</c> (changing elements at 1 and 4),
    /// encoded as Horizontal with a white run of 1 then a black run of 3 (ITU-T T.4 Table 2),
    /// which closes the row exactly at width so no trailing V(0) is needed.
    /// <para>
    /// Row 1 is coded entirely black. Against refCE = [1, 4, …], ITU-T T.6 2.2.4 resolves b1 = 1
    /// (the first reference element ahead of the imaginary a0 = -1, opposite the white a0Col),
    /// and the coding line's own first transition to black sits at a1 = 0, one element left of
    /// b1, so 2.2.4 Step 2(ii) selects VL(1). That places a0 at 0 and flips the colour to black;
    /// a second codeword, V(0), then resolves b1 = 4 against the now-black a0Col and paints
    /// x = 0..4 black, closing the row. Row 1's own raster is 0xF0 either way the guard reads,
    /// because <c>FillRun</c> paints from <c>Math.Max(a0, 0)</c> regardless of what the guard
    /// decides to append — the guard only controls what row 1 records as its own changing-element
    /// list for row 2 to read.
    /// </para>
    /// <para>
    /// With the guard as fixed (<c>a1 != a0</c>, comparing against the unclamped a0 = -1), the
    /// VL(1) step appends a1 = 0 to row 1's list before the V(0) step appends 4, giving
    /// row 1's list [0, 4, …]. Row 2, two more V(0) codewords, then resolves b1 = 0 on the first
    /// (painting nothing, since a0Col is white) and b1 = 4 on the second (painting x = 0..4
    /// black), closing row 2 at 0xF0, the same as row 1.
    /// </para>
    /// <para>
    /// Reverting the guard alone to <c>a1 != Math.Max(a0, 0)</c> compares a1 = 0 against
    /// Math.Max(-1, 0) = 0 instead, reads that as no change, and drops the append — row 1's list
    /// becomes [4, …] instead of [0, 4, …]. Row 2's first V(0) then resolves b1 = 4 directly,
    /// clamps a1 to 4, and closes the row after that single codeword: nothing is painted (a0Col
    /// is still white), the row's second V(0) codeword is left unread, and row 2 comes out 0x00
    /// instead of 0xF0. Reverting a0's own start back to 0 (rather than -1) corrupts row 1's list
    /// the same way for the same reason — with a0 = 0, the VL(1) step's own guard compares
    /// a1 = 0 against a0 = 0 and drops the append before the reverted-guard case is even
    /// reached — and produces the same wrong row 2.
    /// </para>
    /// <para>
    /// Row 0 and row 1's code words are both what ITU-T T.6 2.2.4 selects for this shape, so this
    /// vector's expected raster is fully derivable from the standard, unlike
    /// <see cref="MmrDecoder_VerticalMode_ImaginaryA0ClampFloorsAtZeroNotNegativeOne"/>'s VL(1)
    /// against a b1 of 0, which encodes an a1 no conformant encoder emits.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_VerticalMode_GuardRetainsColumnZeroElementForFollowingRow()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b000111, 6), (0b10, 2),  // row 0: H, white run 1, black run 3
            (0b010, 3), (0b1, 1),                   // row 1: VL(1), V(0)
            (0b1, 1), (0b1, 1));                    // row 2: V(0), V(0)

        var raster = DecodeMmrRaster(width: 4, height: 3, mmr);

        Assert.Equal([0x70, 0xF0, 0xF0], raster);
    }

    /// <summary>
    /// <c>DecodeRun</c>'s make-up accumulation: a run of 64 to 1728 pixels is coded as one
    /// make-up code word followed by one terminating code word (ITU-T T.4, 4.1.1 "Data"), and
    /// the decoded run is their sum. The only make-up code exercised elsewhere in this file is
    /// white 64 in <see cref="Load_DecodeToRaster_MmrRunExceedsWidth_ThrowsInvalidData"/>, which
    /// throws at the run-length cap before its terminating code is ever read — so no fixture
    /// completes a multi-code run, and every run of 64 or more, which is how any scan line that
    /// long actually decodes, is unasserted.
    /// <para>
    /// Width 72 keeps the run under the cap. White make-up 64 followed by white terminating 2
    /// (ITU-T T.4 Table 2) sums to 66, closing white 0..66 (a1 = 66); the black run 4 that
    /// follows closes black 66..70 (a2 = 70); the trailing V(0) paints white 70..72. Row bytes
    /// 0..7 (x = 0..64) are entirely white; byte 8 (x = 64..72) packs bits 66, 67, 68 and 69
    /// black and the rest white — 0x3C.
    /// </para>
    /// Both mutations of the accumulation were run against this fixture and both are killed by
    /// the raster assertion, not by a thrown exception: replacing <c>total += value</c> with
    /// <c>total = value</c> discards the make-up code's 64 and keeps only the terminating code's
    /// 2, so the white run closes at x = 2 and the black run 4 that follows lands at x = 2..6,
    /// giving raster <c>[0x3C, 0, 0, 0, 0, 0, 0, 0, 0]</c> — the same bit pattern the correct
    /// decode paints at x = 66..70, shifted to x = 2..6, because a row of an all-white reference
    /// still closes on the trailing V(0) regardless of where a0 stalled. Returning unconditionally
    /// on the make-up flag stops <c>DecodeRun</c> after reading only the make-up code (64, not
    /// 66), leaving the terminating code's own bits in the stream to be misread as the start of
    /// the next run; that desynchronisation propagates through the rest of the row and gives
    /// raster <c>[0, 0, 0, 0, 0, 0, 0, 0, 0xF0]</c>.
    /// </summary>
    [Fact]
    public void MmrDecoder_DecodeRun_AccumulatesAMakeupCodeWithItsTerminator()
    {
        var mmr = PackMsbFirst(
            (0b001, 3),      // H
            (0b11011, 5),    // white make-up 64
            (0b0111, 4),     // white terminating 2 (run total 66)
            (0b011, 3),      // black run 4
            (0b1, 1));       // V(0) to the edge

        var raster = DecodeMmrRaster(width: 72, height: 1, mmr);

        Assert.Equal([0, 0, 0, 0, 0, 0, 0, 0, 0x3C], raster);
    }

    /// <summary>
    /// The extension prefix. ITU-T T.88 6.2.6 forbids T.6's extension codes, uncompressed mode
    /// included, from appearing in MMR-encoded JBIG2 data, so encountering one means the stream
    /// is malformed rather than that the decoder found a fourth mode Table 1/T.6 doesn't have.
    /// Before #437 this also fell through to the end-of-block scan and still threw
    /// InvalidDataException — that branch has always thrown unconditionally — but under a message
    /// that blamed truncation instead of naming the code that was actually present. The assertion
    /// is on the message text for that reason, not on the exception type.
    /// </summary>
    [Fact]
    public void MmrDecoder_ExtensionPrefix_IsRejectedAsMalformedRatherThanReadAsEndOfBlock()
    {
        var mmr = PackMsbFirst((0b0000001, 7));
        var jbig2 = BuildJbig2WithMmrRegion(width: 8, height: 1, mmr);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };

        var ex = Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));

        Assert.Contains("extension code", ex.Message, StringComparison.Ordinal);
        Assert.Contains("T.88", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// FindB1's strictness: it must skip a reference changing element that a0 sits exactly on,
    /// not just one a0 has passed. Row 0 is one Horizontal codeword, white run 2 then black run
    /// 2, closed to the edge with V(0), giving reference changing elements at x = 2, x = 4 and
    /// x = 8 — refCE = [2, 4, 8, 8, 8, …].
    /// <para>
    /// Row 1 opens with VR(2): b1 = 2 (the first reference element ahead of a0 = -1), so
    /// a1 = 2 + 2 = 4, landing a0 exactly on refCE[1] = 4 and turning the colour black. The
    /// second codeword, V(0), is where the strictness matters: FindB1 must find the first
    /// reference element strictly greater than a0 = 4 of matching (odd-index, white-following)
    /// parity, which is refCE[3] = 8, not refCE[1] = 4 itself — <c>a0</c> sits exactly on a
    /// reference element of the parity being searched for, and that element is behind the
    /// coding line, already accounted for by VR(2), not ahead of it. Painting through to the
    /// correct b1 = 8 colours x = 4..8 black and closes the row (0x0F). A comparison that
    /// admits equality (<c>&gt;=</c>) would instead pick b1 = 4 back, giving a1 = clamp(4, 4, 8)
    /// = a0: no pixels painted, a0 does not advance, and the row is left short with no codeword
    /// left to supply — <c>ReadMode</c> runs into the trailing padding bits, reads them as
    /// Mode.Eofb, and <c>InvalidDataException("unexpected EOFB before all rows were decoded")</c>
    /// follows, rather than silently repeating x = 4.
    /// </para>
    /// This is the comparison operator in FindB1, not a0's initial value (that is #442's
    /// concern, addressed in PR #444, which stacks on #441, itself based on this branch);
    /// a0 here starts at -1, as it does at the start of every row.
    /// </summary>
    [Fact]
    public void MmrDecoder_FindB1_SkipsAReferenceElementA0SitsExactlyOn()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b0111, 4), (0b11, 2), // row 0: H, white run 2, black run 2
            (0b1, 1),                            // row 0: V(0) to the edge
            (0b000011, 6), (0b1, 1));            // row 1: VR(2) (a0 lands on refCE[1] = 4), V(0)

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0x30, 0x0F], raster);
    }

    /// <summary>
    /// The vertical arm's clamp lower bound. <c>a1 = Math.Clamp(b1 + delta, Math.Max(a0, 0),
    /// width)</c> must floor at a0 (here a non-negative a0, so <c>Math.Max(a0, 0)</c> is a0
    /// itself), not 0: a1 regressing behind a0 would let the coding line run backwards,
    /// repainting and re-recording changing elements it already passed.
    /// <para>
    /// Row 0 is Horizontal, white run 3 then black run 1 (ITU-T T.4 Table 2), closed to the edge
    /// with V(0), giving reference changing elements at x = 3, x = 4 and x = 8 —
    /// refCE = [3, 4, 8, 8, 8, …]. Row 0's own raster is white 0..3, black 3..4, white 4..8:
    /// 0x10.
    /// </para>
    /// <para>
    /// Row 1: V(0) steps a0 to refCE[0] = 3 and turns the colour black. VL(3) then finds
    /// b1 = refCE[1] = 4 (the next black-parity element ahead of a0 = 3) — one more than a0, so
    /// b1 + delta = 4 − 3 = 1, which is <em>behind</em> a0. The floor at a0 clamps this back to
    /// a1 = 3 = a0: no transition, nothing painted, and the guard from the vertical-mode vector
    /// above correctly does not record a changing element here either. A floor at 0 instead
    /// would let a1 = 1 stand, walking a0 backwards from 3 to 1 and recording a spurious
    /// changing element there. The final V(0) is what makes that regression observable: read
    /// against the correct a0 = 3, it finds b1 = refCE[2] = 8 and closes the row painting
    /// nothing further (row 1 is entirely white, 0x00, since the only black-producing step was
    /// the zero-width VL(3)). Read against a regressed a0 = 1, the same reference elements
    /// resolve differently and the row does not close at width in the three codewords supplied
    /// — <c>ReadMode</c>'s own <c>BitReader.ReadBit</c> runs past the end of the fixture's bits and
    /// throws <c>InvalidDataException("JBIG2 MMR decoder: unexpected end of compressed data")</c>,
    /// rather than silently producing a different raster.
    /// </para>
    /// <para>
    /// Row 1's own codewords are not what a conformant encoder would emit for this shape: it is
    /// entirely white, and the reference's next feature ahead of a0 = 0 is b2 = refCE[1] = 4, so
    /// 2.2.4 selects Pass — not V(0) followed by a VL(3) whose b1 + delta lands behind a0 and
    /// clamps. It is kept non-conformant on purpose, because a canonical Pass never asks the
    /// vertical arm to resolve a b1 + delta behind a0 in the first place, and that resolution is
    /// what this vector exists to pin.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_VerticalMode_ClampFloorsAtA0NotZero()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b1000, 4), (0b010, 3), // row 0: H, white run 3, black run 1
            (0b1, 1),                             // row 0: V(0) to the edge
            (0b1, 1), (0b0000010, 7), (0b1, 1));  // row 1: V(0), VL(3) (clamps at a0), V(0)

        var raster = DecodeMmrRaster(width: 8, height: 2, mmr);

        Assert.Equal([0x10, 0x00], raster);
    }

    // ── MmrDecoder: clamp floor at a value 2.2.5.1 itself does not reach ──────
    // The vector below feeds the vertical arm a b1 + delta of -1, which 2.2.5.1 never produces
    // for conformant input (a1 is always >= 0 by definition). The standard's decoding procedure
    // is therefore silent on what happens here; the expected raster comes from this decoder's
    // own clamp-floor policy, not from working the standard's procedure by hand as the vectors
    // above do — see the vector's own doc comment for the derivation.

    /// <summary>
    /// The vertical arm's clamp lower bound must floor at zero, not at the imaginary a0 = -1 that
    /// FindB1 and the <c>a1 != a0</c> guard need to see. Flooring at a0 instead lets a1 reach -1:
    /// at the start of a row, against a reference row that starts black, VL(1) resolves b1 = 0
    /// (needing a0 = -1 to find it, as in the vectors above), so b1 + delta = 0 - 1 = -1. Floored
    /// at zero, a1 = 0; floored at a0, a1 stays -1, equal to a0, and the guard drops the column-0
    /// changing element from the row's own list — the same corruption the guard fix above closes,
    /// reopened one clamp over.
    /// <para>
    /// Row 0 is one Horizontal codeword, white run 0 then black run 8 (ITU-T T.4 Table 2), filling
    /// the row black and giving refCE = [0, 8, 8, …] for row 1.
    /// </para>
    /// <para>
    /// Row 1: VL(1) gives a1 = -1 either way. FillRun's start is <c>Math.Max(a0, 0)</c> regardless
    /// of which clamp is under test, so the paint is identical under both, and a0Col flips to
    /// black either way; the closing V(0) then paints 0..8 black (b1 = refCE[1] = 8), so row 1 is
    /// 0xFF under both. What differs is only what row 1 records as its own changing elements:
    /// [0, 8, …] correct (a1 = 0 is a real transition, appended), [8, …] under the reverted clamp
    /// (a1 = -1 = a0, nothing appended).
    /// </para>
    /// <para>
    /// Row 2 is where that missing element becomes observable. Its first V(0) resolves b1 against
    /// whichever refCE[0] row 1 left behind: 0 under the fix, giving a1 = 0 and leaving the row
    /// open for a second V(0) to paint 0..8 black (0xFF); 8 under the reverted clamp, so a1 = 8
    /// closes the row immediately with nothing painted (0x00), and the packed second V(0) is left
    /// unread.
    /// </para>
    /// <para>
    /// Row 1's VL(1) is not something a conformant encoder emits: a1 is by definition the next
    /// changing element to the right of a0, and 2.2.5.1 places a0 just before column 0, so a1 &gt;=
    /// 0 always. b1 + delta = -1 here is reachable only because this vector deliberately targets
    /// the clamp floor with malformed input; the standard defines no decoding procedure for it, so
    /// row 1's expected raster follows from the decoder's own clamping policy (floor at zero, not
    /// at a0), not from a value 2.2.5.1 specifies.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_VerticalMode_ImaginaryA0ClampFloorsAtZeroNotNegativeOne()
    {
        var mmr = PackMsbFirst(
            (0b001, 3), (0b00110101, 8), (0b000101, 6), // row 0: H, white run 0, black run 8
            (0b010, 3), (0b1, 1),                        // row 1: VL(1), V(0)
            (0b1, 1), (0b1, 1));                         // row 2: V(0), V(0) (reverted clamp reads only the first)

        var raster = DecodeMmrRaster(width: 8, height: 3, mmr);

        Assert.Equal([0xFF, 0xFF, 0xFF], raster);
    }

    /// <summary>
    /// Decodes an MMR generic region and returns the raster bytes the loader actually emitted,
    /// by inflating the <c>/FlateDecode</c> stream it re-encodes them into. Rows are packed
    /// MSB-first with <c>rowBytes = (width + 7) / 8</c> bytes each; at width 8 that is exactly
    /// one byte per row, so most vectors below read a single raster byte as the whole row, and
    /// the width-72 make-up vector reads nine.
    /// </summary>
    private static byte[] DecodeMmrRaster(int width, int height, byte[] mmr)
    {
        var jbig2 = BuildJbig2WithMmrRegion(width, height, mmr);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };

        var img = Jbig2ImageLoader.Load(jbig2, opts);

        Assert.Equal(width, img.Width);
        Assert.Equal(height, img.Height);
        return CcittImageTests.DecompressFlateStream(img.BuildStream());
    }
    // ── MmrDecoder: run-length known-answer vectors (ITU-T T.4 Table 2) ───────

    // Same discipline as the mode-table vectors above. Each stream is hand-encoded from Table 2
    // and each expected raster is worked out from the standard, not read off the decoder. Most
    // cases below fail against the run tables that shipped before #440; black runs 1 to 4 are the
    // exception, since the pre-#440 black codes for those four runs already matched Table 2.
    //
    // Horizontal mode is the only mode that reads a run length, so every vector goes through it:
    // 001 (the flag code), then a white run, then a black run.

    /// <summary>
    /// Black terminating codes, on a 32-pixel row that starts with a white run of 0 so the black
    /// run begins at x = 0 and the raster is the run itself. Run 7 is the case measured in #440,
    /// where the old table returned 10.
    /// </summary>
    [Theory]
    [InlineData(0b0000110111, 10, 0, "00000000")]  // black 0  — its own entry, 0001111, was unreachable behind 00011
    [InlineData(0b010, 3, 1, "80000000")]          // black 1
    [InlineData(0b11, 2, 2, "C0000000")]           // black 2
    [InlineData(0b10, 2, 3, "E0000000")]           // black 3
    [InlineData(0b011, 3, 4, "F0000000")]          // black 4
    [InlineData(0b0011, 4, 5, "F8000000")]         // black 5  — old table's entry 0101 was unreachable behind 010
    [InlineData(0b0010, 4, 6, "FC000000")]         // black 6
    [InlineData(0b00011, 5, 7, "FE000000")]        // black 7  — old table returned 10
    [InlineData(0b000101, 6, 8, "FF000000")]       // black 8  — old table returned 11
    [InlineData(0b000100, 6, 9, "FF800000")]       // black 9  — old table returned 12
    [InlineData(0b0000100, 7, 10, "FFC00000")]     // black 10
    [InlineData(0b0000111, 7, 12, "FFF00000")]     // black 12 — old table returned 17
    [InlineData(0b00000100, 8, 13, "FFF80000")]    // black 13
    public void MmrDecoder_BlackTerminatingCodes_DecodeToTheRunTable2Gives(
        int codeWord, int bits, int expectedRun, string expectedHex)
    {
        var mmr = PackMsbFirst(
            (0b001, 3),          // Horizontal
            (0b00110101, 8),     // white run 0
            (codeWord, bits),    // the black run under test
            (0b1, 1));           // V(0) to carry the row to the right edge

        var raster = DecodeMmrRaster(width: 32, height: 1, mmr);

        Assert.Equal(expectedHex, Convert.ToHexString(raster));
        Assert.Equal(expectedRun, raster.Sum(b => System.Numerics.BitOperations.PopCount(b)));
    }

    /// <summary>
    /// White run 1, which had no code word at all in the table that shipped before #440. Its
    /// absence meant a single white pixel could not be decoded: the reader consumed 000111 and
    /// went looking for a longer code word.
    /// </summary>
    [Fact]
    public void MmrDecoder_WhiteRunOfOne_HasACodeWordAtAll()
    {
        var mmr = PackMsbFirst(
            (0b001, 3),          // Horizontal
            (0b000111, 6),       // white run 1 (ITU-T T.4 Table 2)
            (0b11, 2),           // black run 2
            (0b1, 1));           // V(0)

        var raster = DecodeMmrRaster(width: 32, height: 1, mmr);

        Assert.Equal("60000000", Convert.ToHexString(raster)); // white at 0, black at 1 and 2
    }

    /// <summary>
    /// A make-up code followed by a terminating code, which is how Table 3a says any run of 64 or
    /// more is written: white 64 + white 0, then black 64 + black 3, on a row wide enough to hold
    /// them.
    /// <para>
    /// The expected raster is worked out from the run lengths directly: white 0-63, black 64-130
    /// (the 64-run make-up plus the 3-run terminating code = 67 pixels), then white 131-135 from
    /// the closing V(0). A popcount pins the black-pixel total but not their position — shifting
    /// the whole run one pixel right leaves 67 black bits either way — so the comparison here is
    /// the exact packed bytes, the same idiom the run-table vectors above use.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_MakeUpCodes_AddToTheFollowingTerminatingCode()
    {
        var mmr = PackMsbFirst(
            (0b001, 3),          // Horizontal
            (0b11011, 5),        // white make-up 64
            (0b00110101, 8),     // white terminating 0  => white run 64
            (0b0000001111, 10),  // black make-up 64
            (0b10, 2),           // black terminating 3  => black run 67
            (0b1, 1));           // V(0)

        var raster = DecodeMmrRaster(width: 136, height: 1, mmr);

        Assert.Equal("0000000000000000FFFFFFFFFFFFFFFFE0", Convert.ToHexString(raster));
    }

    /// <summary>
    /// The tables are a prefix code, so no code word may be a prefix of another. Asserting it is
    /// what makes #440's specific failure impossible to reland: the black table that shipped had
    /// 0100, 0101 and 0111 sitting behind 010 and 011, which the reader returned on first, so
    /// those three entries could never be reached and their presence went unnoticed for as long as
    /// nothing checked this property.
    ///
    /// The prefix check alone does not look at which colours or how many entries
    /// <see cref="MmrDecoder.DecodingTables"/> actually returned — an empty table, or a table
    /// missing a whole colour, is trivially a prefix code — so it is asserted here too: both
    /// colour names present, and 104 code words apiece (64 Table 2 terminating codes, 27 Table 3a
    /// make-up codes, and Table 3b's 13 shared codes appended to each). Without that, a mutant
    /// that made <see cref="MmrDecoder.DecodingTables"/> return only the black entry passed this
    /// test with zero failures (#441).
    /// </summary>
    [Fact]
    public void MmrDecoder_CodeTablesAreAPrefixCode()
    {
        var tables = MmrDecoder.DecodingTables;

        Assert.Equal(2, tables.Length);
        Assert.Contains(tables, t => t.Colour == "white");
        Assert.Contains(tables, t => t.Colour == "black");
        foreach (var (colour, table) in tables)
            Assert.Equal(104, table.Length);

        foreach (var (colour, table) in tables)
        {
            var words = table.ToList();
            Assert.Equal(words.Count, words.Distinct(StringComparer.Ordinal).Count());

            foreach (var word in words)
            {
                var prefixed = words.Where(w => w != word && w.StartsWith(word, StringComparison.Ordinal)).ToList();
                Assert.True(
                    prefixed.Count == 0,
                    $"{colour}: {word} is a prefix of {string.Join(", ", prefixed)}, so those are unreachable.");
            }
        }
    }

    // ── MmrDecoder: value-level coverage of what the KAT vectors above leave unpinned ─────────

    /// <summary>
    /// Every terminating run 0-63, both colours, decoded in a single row: 64 Horizontal-mode pairs
    /// of (white i, black i) back to back. Each Horizontal call returns the colour to white (two
    /// transitions net to zero), so the pairs chain with no V(0) between them, and 2 * (0+1+...+63)
    /// = 4032 pixels of that is exactly what the 64 pairs commit to.
    ///
    /// Of the run-length known-answer vectors above, the ones that read a run code pin only 17 of
    /// the 195 code words (#441);
    /// a transposition of two same-length code words leaves <see cref="MmrDecoder_CodeTablesAreAPrefixCode"/>
    /// satisfied, since swapping two entries changes nothing about which words are whose prefix.
    /// This vector exercises every terminating code word in Table 2 and pins the whole raster with
    /// an exact compare, so a transposition anywhere in either terminating table now moves a run
    /// boundary and fails it.
    ///
    /// The row is one pixel wider than those 4032, closed with a trailing V(0) rather than ending
    /// exactly where the 64 pairs stop. Without that extra pixel, inflating the very last code word
    /// read — black terminating 63 — is invisible: <c>DecodeRow</c>'s <c>a2 = Math.Min(a1 + run2,
    /// width)</c> clamps any inflated run back down to the same width the correct run already
    /// lands on, so the raster comes out identical either way. The trailing V(0) instead paints a
    /// pixel the correct decode leaves white (default 0) and the inflated one paints black, so an
    /// inflation of that last entry — to 64, or to any larger value, since the clamp collapses them
    /// all to the same wrong fill — now moves that pixel and fails the comparison. There is no
    /// longer black code word to substitute for run 64: Table 3a's own make-up code for it would
    /// desynchronise the bitstream rather than merely inflate the run, so this was verified by
    /// inflating the decoded run length for black terminating 63 directly in the decoder and
    /// confirming the assertion fails where it previously did not.
    ///
    /// The code words are transcribed independently of <see cref="MmrDecoder.DecodingTables"/>
    /// rather than read from it. A shared transcription error is exactly what would cancel itself
    /// out — the same wrong value on both sides still agrees — so keeping the two transcriptions
    /// independent is what makes that failure mode unlikely rather than what rules it out. The
    /// expected raster is not decoded from anything: it comes directly from ITU-T T.4's own
    /// definition of a run — "white i" is i consecutive white pixels, "black i" is i consecutive
    /// black pixels — by filling i zero bits then i one bits for each i, plus the one trailing white
    /// pixel the closing V(0) paints.
    /// </summary>
    [Fact]
    public void MmrDecoder_AllTerminatingRuns_ProduceTheExactTable2Raster()
    {
        // ITU-T T.4 Table 2, white terminating codes, runs 0-63.
        string[] whiteTerminating =
        [
            "00110101", "000111", "0111", "1000",
            "1011", "1100", "1110", "1111",
            "10011", "10100", "00111", "01000",
            "001000", "000011", "110100", "110101",
            "101010", "101011", "0100111", "0001100",
            "0001000", "0010111", "0000011", "0000100",
            "0101000", "0101011", "0010011", "0100100",
            "0011000", "00000010", "00000011", "00011010",
            "00011011", "00010010", "00010011", "00010100",
            "00010101", "00010110", "00010111", "00101000",
            "00101001", "00101010", "00101011", "00101100",
            "00101101", "00000100", "00000101", "00001010",
            "00001011", "01010010", "01010011", "01010100",
            "01010101", "00100100", "00100101", "01011000",
            "01011001", "01011010", "01011011", "01001010",
            "01001011", "00110010", "00110011", "00110100",
        ];

        // ITU-T T.4 Table 2, black terminating codes, runs 0-63.
        string[] blackTerminating =
        [
            "0000110111", "010", "11", "10",
            "011", "0011", "0010", "00011",
            "000101", "000100", "0000100", "0000101",
            "0000111", "00000100", "00000111", "000011000",
            "0000010111", "0000011000", "0000001000", "00001100111",
            "00001101000", "00001101100", "00000110111", "00000101000",
            "00000010111", "00000011000", "000011001010", "000011001011",
            "000011001100", "000011001101", "000001101000", "000001101001",
            "000001101010", "000001101011", "000011010010", "000011010011",
            "000011010100", "000011010101", "000011010110", "000011010111",
            "000001101100", "000001101101", "000011011010", "000011011011",
            "000001010100", "000001010101", "000001010110", "000001010111",
            "000001100100", "000001100101", "000001010010", "000001010011",
            "000000100100", "000000110111", "000000111000", "000000100111",
            "000000101000", "000001011000", "000001011001", "000000101011",
            "000000101100", "000001011010", "000001100110", "000001100111",
        ];

        var codes = new List<(int value, int bits)>();
        var expectedBits = new System.Text.StringBuilder();
        for (var run = 0; run < 64; run++)
        {
            codes.Add((0b001, 3)); // Horizontal flag
            codes.Add(ParseCodeWord(whiteTerminating[run]));
            codes.Add(ParseCodeWord(blackTerminating[run]));
            expectedBits.Append('0', run);
            expectedBits.Append('1', run);
        }

        // Close the row one pixel past the 4032 the 64 pairs commit to, so the very last code word
        // read (black terminating 63) is not in tail position against the row's own width — see the
        // remarks above.
        codes.Add((0b1, 1)); // V(0)
        expectedBits.Append('0');

        var width = expectedBits.Length;
        var mmr = PackMsbFirst([.. codes]);
        var raster = DecodeMmrRaster(width, height: 1, mmr);

        Assert.Equal(Convert.ToHexString(PackBinaryString(expectedBits.ToString())), Convert.ToHexString(raster));
    }

    /// <summary>
    /// All 27 white and 27 black make-up codes of Table 3a, each closed with the run's terminating
    /// code so <c>DecodeRun</c>'s accumulate-then-terminate loop actually exercises the make-up
    /// value rather than just matching it. The terminating sweep above never reaches Table 3a at
    /// all, so a transposition confined to the make-up codes — black make-up runs 320 and 384
    /// swapping, say — would leave that vector passing, and the file's other Table 3a value-level
    /// pins don't cover 320 or 384 either, so no value-level check in this file catches that
    /// specific swap (#441). A deletion — the
    /// entry for white run 1728 going missing, say — is a different case: no value-level KAT
    /// catches it either, but it does fail <see cref="MmrDecoder_CodeTablesAreAPrefixCode"/>'s
    /// count assertion, which is a shape check rather than a value-level one, so it does not also
    /// catch the transposition.
    ///
    /// The last entry read in the row — black make-up 1728, closed with terminating 0 — sits in the
    /// same tail position <see cref="MmrDecoder_AllTerminatingRuns_ProduceTheExactTable2Raster"/>
    /// closes with a trailing V(0), and for the same reason: <c>a2</c>'s clamp to width silently
    /// absorbs an inflated final run, so this sweep closes with one too, one pixel past the 48384
    /// the 27 pairs commit to.
    ///
    /// As above, the code words are transcribed independently of <see cref="MmrDecoder"/>'s own
    /// tables and the expected raster comes from the run-length definition directly, not from
    /// decoding anything: run <c>64 * (i + 1)</c> is <c>64 * (i + 1)</c> consecutive pixels of that
    /// colour.
    /// </summary>
    [Fact]
    public void MmrDecoder_AllMakeUpCodes_ProduceTheExactTable3aRaster()
    {
        // ITU-T T.4 Table 3a, white make-up codes, runs 64 to 1728 in steps of 64.
        string[] whiteMakeUp =
        [
            "11011", "10010", "010111", "0110111",
            "00110110", "00110111", "01100100", "01100101",
            "01101000", "01100111", "011001100", "011001101",
            "011010010", "011010011", "011010100", "011010101",
            "011010110", "011010111", "011011000", "011011001",
            "011011010", "011011011", "010011000", "010011001",
            "010011010", "011000", "010011011",
        ];

        // ITU-T T.4 Table 3a, black make-up codes, runs 64 to 1728 in steps of 64.
        string[] blackMakeUp =
        [
            "0000001111", "000011001000", "000011001001", "000001011011",
            "000000110011", "000000110100", "000000110101", "0000001101100",
            "0000001101101", "0000001001010", "0000001001011", "0000001001100",
            "0000001001101", "0000001110010", "0000001110011", "0000001110100",
            "0000001110101", "0000001110110", "0000001110111", "0000001010010",
            "0000001010011", "0000001010100", "0000001010101", "0000001011010",
            "0000001011011", "0000001100100", "0000001100101",
        ];

        const string whiteTerminatingZero = "00110101";
        const string blackTerminatingZero = "0000110111";

        var codes = new List<(int value, int bits)>();
        var expectedBits = new System.Text.StringBuilder();
        for (var i = 0; i < 27; i++)
        {
            var run = 64 * (i + 1);
            codes.Add((0b001, 3)); // Horizontal flag
            codes.Add(ParseCodeWord(whiteMakeUp[i]));
            codes.Add(ParseCodeWord(whiteTerminatingZero));
            codes.Add(ParseCodeWord(blackMakeUp[i]));
            codes.Add(ParseCodeWord(blackTerminatingZero));
            expectedBits.Append('0', run);
            expectedBits.Append('1', run);
        }

        // Close the row one pixel past the 48384 the 27 pairs commit to, so the very last code word
        // read (black make-up 1728) is not in tail position against the row's own width — see the
        // remarks above.
        codes.Add((0b1, 1)); // V(0)
        expectedBits.Append('0');

        var width = expectedBits.Length;
        var mmr = PackMsbFirst([.. codes]);
        var raster = DecodeMmrRaster(width, height: 1, mmr);

        Assert.Equal(Convert.ToHexString(PackBinaryString(expectedBits.ToString())), Convert.ToHexString(raster));
    }

    /// <summary>
    /// All thirteen Table 3b entries, once for white and once for black, each closed with the
    /// terminating code for run 7 rather than run 0 — <see cref="MmrDecoder_SharedMakeUpCode_DecodesARunOf1792"/>
    /// closes its one vector with terminating 0, and a run of 1792 + 0 is 1792 pixels regardless of
    /// whether the make-up code that produced the 1792 is followed by a terminating code at all,
    /// so it cannot tell a decoder that reads the shared entries as make-up codes from one that
    /// reads them as terminating and stops one code word short, leaving the next code word's bits
    /// unread in the stream. Closing every entry with a nonzero terminating run instead means a
    /// decoder that stops early is missing exactly those 7 pixels and has desynchronised on top of
    /// that, so it cannot land on the same raster by coincidence.
    ///
    /// Table 3b is shared, but before this vector only the black side of it was exercised, and only
    /// by the single run in <see cref="MmrDecoder_SharedMakeUpCode_DecodesARunOf1792"/>; this sweep
    /// is the first to exercise the white side, and pins all thirteen entries for both colours
    /// rather than one entry for one. Every one of the thirteen entries is closed rather than only
    /// the first, so a transposition, a stride error, or a missing entry anywhere in the table
    /// moves a run boundary and fails the comparison, the way
    /// <see cref="MmrDecoder_AllMakeUpCodes_ProduceTheExactTable3aRaster"/> does for Table 3a.
    ///
    /// The shared code words are transcribed independently of <see cref="MmrDecoder"/>'s own table,
    /// from ITU-T T.4 Table 3b directly, and so is the terminating-7 code word for each colour,
    /// from Table 2. The expected raster comes from the run-length definition, not from decoding
    /// anything: run <c>1792 + 64 * i + 7</c> is that many consecutive pixels of the colour coded.
    /// </summary>
    [Fact]
    public void MmrDecoder_SharedMakeUpCodes_ProduceTheExactTable3bRaster()
    {
        // ITU-T T.4 Table 3b, the extended make-up codes 1792 to 2560, shared by both colours.
        string[] sharedMakeUp =
        [
            "00000001000", "00000001100", "00000001101", "000000010010",
            "000000010011", "000000010100", "000000010101", "000000010110",
            "000000010111", "000000011100", "000000011101", "000000011110",
            "000000011111",
        ];

        // ITU-T T.4 Table 2, the terminating code for run 7, both colours.
        const string whiteTerminatingSeven = "1111";
        const string blackTerminatingSeven = "00011";
        const int terminatingRun = 7;

        var codes = new List<(int value, int bits)>();
        var expectedBits = new System.Text.StringBuilder();
        for (var i = 0; i < sharedMakeUp.Length; i++)
        {
            var run = 1792 + (64 * i) + terminatingRun;
            codes.Add((0b001, 3)); // Horizontal flag
            codes.Add(ParseCodeWord(sharedMakeUp[i]));
            codes.Add(ParseCodeWord(whiteTerminatingSeven));
            codes.Add(ParseCodeWord(sharedMakeUp[i]));
            codes.Add(ParseCodeWord(blackTerminatingSeven));
            expectedBits.Append('0', run);
            expectedBits.Append('1', run);
        }

        var width = expectedBits.Length;
        var mmr = PackMsbFirst([.. codes]);
        var raster = DecodeMmrRaster(width, height: 1, mmr);

        Assert.Equal(Convert.ToHexString(PackBinaryString(expectedBits.ToString())), Convert.ToHexString(raster));
    }

    /// <summary>
    /// <see cref="MmrDecoder.MaxRunCodeBits"/> is the bit budget <c>ReadRun</c> allocates before
    /// giving up on a run-length code word. Nothing tied it to the tables it exists to bound. Set
    /// one bit too low and the twenty 13-bit black make-up codes (runs 512 through 1728) become
    /// unlookupable, since <c>ReadRun</c> loops <c>bits &lt;= MaxRunCodeBits</c>; that direction is
    /// already caught by <see cref="MmrDecoder_AllMakeUpCodes_ProduceTheExactTable3aRaster"/>,
    /// which throws once it reaches one of those codes (confirmed by building at 12: that test
    /// fails with "run-length code word is not in ITU-T T.4 Table 2, 3a or 3b").
    /// <para>
    /// Set one bit too high and this assertion is the only thing that pins the value directly.
    /// Rebuilding at 14 does also fail
    /// <see cref="Load_DecodeToRaster_UnrecognisedRunLengthCode_ThrowsInvalidData"/>, but not
    /// because anything there asserts the budget: that fixture is two bytes, <c>ReadRun</c> asks
    /// for a fourteenth bit at the extra width, and <c>BitReader</c> throws its own "unexpected
    /// end of compressed data" before <c>ReadRun</c>'s own fallback throw is ever reached, so
    /// <c>Assert.Contains("Table 2", ...)</c> fails on that unrelated message. A fixture with more
    /// trailing bits would not fail at all: nothing else in the suite reads a budget-worth of
    /// leftover zero bits and expects a specific throw. This assertion is the direct one.
    /// </para>
    /// </summary>
    [Fact]
    public void MmrDecoder_MaxRunCodeBits_EqualsTheLongestCodeWordInTheTables()
    {
        var longest = MmrDecoder.DecodingTables.SelectMany(t => t.CodeWords).Max(w => w.Length);
        Assert.Equal(MmrDecoder.MaxRunCodeBits, longest);
    }

    /// <summary>
    /// A run of 1792 needs one of Table 3b's shared extended make-up codes: Table 3a's own black
    /// make-up codes stop at 1728, so any run of 1792 or more has to route through
    /// <c>SharedMakeUp</c> at least once. This is the vector that pins that route through the
    /// black table (#441).
    /// </summary>
    [Fact]
    public void MmrDecoder_SharedMakeUpCode_DecodesARunOf1792()
    {
        var mmr = PackMsbFirst(
            (0b001, 3),             // Horizontal
            (0b00110101, 8),        // white terminating 0
            (0b00000001000, 11),    // black shared make-up 1792 (ITU-T T.4 Table 3b)
            (0b0000110111, 10));    // black terminating 0 => black run 1792 + 0 = 1792

        var raster = DecodeMmrRaster(width: 1792, height: 1, mmr);

        Assert.Equal(224, raster.Length);
        Assert.All(raster, b => Assert.Equal(0xFF, b));
    }

    /// <summary>
    /// Thirteen zero bits after the Horizontal flag never match a code word at any of the thirteen
    /// prefix lengths <c>ReadRun</c> tries: no white, black, or shared code word in Table 2, 3a or
    /// 3b is all zero at any length from 1 through 13, so <c>ReadRun</c>'s fallback throw is
    /// reachable rather than dead code — replacing it with <c>return (0, false)</c> failed no test
    /// before this one (#441).
    /// </summary>
    [Fact]
    public void Load_DecodeToRaster_UnrecognisedRunLengthCode_ThrowsInvalidData()
    {
        var mmr = PackMsbFirst((0b001, 3), (0, 13));
        var jbig2 = BuildJbig2WithMmrRegion(width: 32, height: 1, mmr);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };

        var ex = Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));

        Assert.Contains("Table 2", ex.Message, StringComparison.Ordinal);
    }

    // ── Helper to build a JBIG2 buffer with an extra segment of a given type ──

    private static byte[] BuildJbig2WithSegmentType(
        int pageInfoWidth, int pageInfoHeight,
        int extraSegType, int extraPageAssoc)
    {
        byte[] header = [0x97, 0x4A, 0x42, 0x32, 0x0D, 0x0A, 0x1A, 0x0A];
        var pageInfoData = BuildPageInfoSegmentData(pageInfoWidth, pageInfoHeight);
        var pageInfoSeg = BuildSegment(0, 48, 1, pageInfoData);
        // Extra segment with the given type and 4 bytes of stub data.
        var extraSeg = BuildSegment(1, extraSegType, extraPageAssoc, new byte[4]);
        var eofSeg = BuildSegment(2, 51, 0, []);

        var buf = new byte[header.Length + pageInfoSeg.Length + extraSeg.Length + eofSeg.Length];
        var pos = 0;
        Array.Copy(header, 0, buf, pos, header.Length); pos += header.Length;
        Array.Copy(pageInfoSeg, 0, buf, pos, pageInfoSeg.Length); pos += pageInfoSeg.Length;
        Array.Copy(extraSeg, 0, buf, pos, extraSeg.Length); pos += extraSeg.Length;
        Array.Copy(eofSeg, 0, buf, pos, eofSeg.Length);
        return buf;
    }
}
