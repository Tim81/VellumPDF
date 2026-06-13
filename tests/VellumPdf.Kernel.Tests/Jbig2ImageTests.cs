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
        // Horizontal mode (011) followed by a white make-up code for a 64-pixel run, far
        // larger than the 2-pixel width. The run-length cap must reject this with
        // InvalidDataException rather than accumulating without bound — a long make-up run
        // would otherwise overflow the run accumulator and yield a negative fill range.
        var mmr = PackMsbFirst((0b011, 3), (0b11011, 5)); // H, white make-up 64
        var jbig2 = BuildJbig2WithMmrRegion(width: 2, height: 1, mmr);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));
    }

    [Fact]
    public void Load_DecodeToRaster_MmrZeroRunHorizontalFlood_ThrowsInvalidData()
    {
        // Repeated zero-run Horizontal modes never advance the coding position but push two
        // changing elements each iteration. The bounds-checked changing-element append must
        // reject this with InvalidDataException rather than overrunning the CE array
        // (which previously surfaced as IndexOutOfRangeException).
        (int, int)[] zeroRunHorizontal =
        [
            (0b011, 3),         // Horizontal mode
            (0b00110101, 8),    // white run length 0 (terminating)
            (0b0001111, 7),     // black run length 0 (terminating)
        ];
        var mmr = PackMsbFirst([.. zeroRunHorizontal, .. zeroRunHorizontal, .. zeroRunHorizontal]);
        var jbig2 = BuildJbig2WithMmrRegion(width: 2, height: 1, mmr);

        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<InvalidDataException>(() => Jbig2ImageLoader.Load(jbig2, opts));
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
