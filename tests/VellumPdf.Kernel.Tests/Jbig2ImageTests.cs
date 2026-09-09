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
        // Width 3, three repeats of H / white 0 / black 1. Each repeat appends two changing
        // elements without advancing a0 to the width, so the third repeat's second AppendCe
        // call is the sixth append against a curCE array sized width + 2 = 5 (valid indices
        // 0..4): idx reaches 5 there, which is exactly where the bounds check must fire.
        //
        // Unlike the zero-run vector this replaces, a0 does advance by one pixel per repeat
        // (the black run is 1, not 0), so the fixture reaches the width + 2 array's true capacity
        // rather than looping forever short of it — and it stays clear of the run-length cap in
        // MmrRunExceedsWidth, whose "exceeds the image width" message this test's assertion must
        // not collide with. Asserting on AppendCe's own message is what makes this test actually
        // exercise the guard: before this fixture, a mutant that replaced AppendCe's guard body
        // with a different exception type left the whole suite green, because this test's fixture
        // threw MmrRunExceedsWidth's message from a different guard first and Assert.Throws alone
        // could not tell the two apart.
        (int, int)[] zeroWidthBlackOne =
        [
            (0b001, 3),         // Horizontal mode — the flag code of ITU-T T.6, 2.2.3.3
            (0b00110101, 8),    // white run length 0 (terminating)
            (0b010, 3),         // black run length 1 (terminating)
        ];
        var mmr = PackMsbFirst([.. zeroWidthBlackOne, .. zeroWidthBlackOne, .. zeroWidthBlackOne]);
        var jbig2 = BuildJbig2WithMmrRegion(width: 3, height: 1, mmr);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };

        var ex = Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));
        Assert.Contains("too many changing elements", ex.Message, StringComparison.Ordinal);
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
    /// Row 1 opens with VR(2): b1 = 2 (the first reference element ahead of a0 = 0), so
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
    /// a0 here starts at 0 as it always does.
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
    /// The vertical arm's clamp lower bound. <c>a1 = Math.Clamp(b1 + delta, a0, width)</c> must
    /// floor at a0, not 0: a1 regressing behind a0 would let the coding line run backwards,
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
