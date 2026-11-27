// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for <see cref="JpxImageLoader"/> — JPEG 2000 passthrough loading.
///
/// All JPEG 2000 data is synthesised in-memory using minimal hand-crafted byte arrays.
///
/// Covers:
///   • Raw codestream (SOC FF4F): width/height/components/bpc from SIZ, verbatim bytes,
///     /Filter /JPXDecode, correct /ColorSpace, correct /BitsPerComponent.
///   • JP2 box file: signature + ftyp + jp2h(ihdr) + jp2c — same assertions;
///     embedded bytes are the jp2c payload only (not the whole file).
///   • JP2 box file with colr enumerated colour space (sRGB=16, greyscale=17).
///   • Malformed inputs: bad signature, truncated SIZ, missing SIZ, box length exceeding
///     buffer, oversized dimensions — all throw <see cref="InvalidDataException"/>.
///   • DecodeToRaster throws <see cref="NotSupportedException"/>.
/// </summary>
public sealed class JpxImageTests
{
    // ── Raw codestream (SOC FF4F) ─────────────────────────────────────────────

    [Fact]
    public void RawCodestream_Filter_IsJpxDecode()
    {
        var j2k = BuildRawCodestream(width: 10, height: 8, components: 3, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Contains("/JPXDecode", StreamDictText(img));
    }

    [Fact]
    public void RawCodestream_Width_Correct()
    {
        var j2k = BuildRawCodestream(width: 10, height: 8, components: 3, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Equal(10, img.Width);
    }

    [Fact]
    public void RawCodestream_Height_Correct()
    {
        var j2k = BuildRawCodestream(width: 10, height: 8, components: 3, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Equal(8, img.Height);
    }

    [Fact]
    public void RawCodestream_ColorSpace_Rgb_3Components()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 3, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Contains("/DeviceRGB", StreamDictText(img));
    }

    [Fact]
    public void RawCodestream_ColorSpace_Gray_1Component()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 1, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Contains("/DeviceGray", StreamDictText(img));
    }

    [Fact]
    public void RawCodestream_ColorSpace_Cmyk_4Components()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 4, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Contains("/DeviceCMYK", StreamDictText(img));
    }

    [Fact]
    public void RawCodestream_BitsPerComponent_8()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 3, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        Assert.Contains("/BitsPerComponent 8", StreamDictText(img));
    }

    [Fact]
    public void RawCodestream_BitsPerComponent_12_OmittedForPdfAConformance()
    {
        // 12 is not in PDF/A-2's allowed /BitsPerComponent set {1,2,4,8,16}. For JPXDecode the
        // entry is optional (the codestream carries the real depth), so it must be omitted rather
        // than emit a value veraPDF 6.2.8-4 would reject. Width/height/colour are still detected.
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 3, bpc: 12);
        var img = JpxImageLoader.Load(j2k);
        Assert.DoesNotContain("/BitsPerComponent", StreamDictText(img));
        Assert.Equal(4, img.Width);
    }

    [Fact]
    public void RawCodestream_BitsPerComponent_Signed_StrippedCorrectly()
    {
        // Ssiz with high bit set means signed; bit depth is still (Ssiz & 0x7F) + 1.
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 1, bpc: 8, signedSsiz: true);
        var img = JpxImageLoader.Load(j2k);
        Assert.Contains("/BitsPerComponent 8", StreamDictText(img));
    }

    [Fact]
    public void RawCodestream_WrappedInJp2_CodestreamPreservedVerbatim()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 1, bpc: 8);
        var img = JpxImageLoader.Load(j2k);
        var body = ExtractStreamBody(WriteStream(img));

        // A bare codestream is now wrapped in a minimal JP2 box structure so PDF/A-2 clause
        // 6.2.8.3 (which reads colour channels / bit depth from the JP2 boxes) is satisfied.
        // The embedded stream is therefore a JP2 file, not the bare codestream — but the
        // codestream must be preserved verbatim inside the jp2c box.
        Assert.True(StartsWithJp2Signature(body), "Embedded stream must be a JP2 box file.");
        Assert.True(ContainsSequence(body, j2k), "Original codestream must be embedded verbatim.");
        Assert.True(body.Length > j2k.Length, "The JP2 wrapper adds box structure.");
        Assert.True(ContainsBoxType(body, "ihdr"u8), "Synthesised JP2 must contain an ihdr box.");
        Assert.True(ContainsBoxType(body, "colr"u8), "Synthesised JP2 must contain a colr box.");
    }

    // ── JP2 box file ──────────────────────────────────────────────────────────

    [Fact]
    public void Jp2_Filter_IsJpxDecode()
    {
        var jp2 = BuildJp2File(width: 8, height: 6, nc: 3, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Contains("/JPXDecode", StreamDictText(img));
    }

    [Fact]
    public void Jp2_Width_Correct()
    {
        var jp2 = BuildJp2File(width: 8, height: 6, nc: 3, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Equal(8, img.Width);
    }

    [Fact]
    public void Jp2_Height_Correct()
    {
        var jp2 = BuildJp2File(width: 8, height: 6, nc: 3, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Equal(6, img.Height);
    }

    [Fact]
    public void Jp2_ColorSpace_Rgb_3Components()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 3, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Contains("/DeviceRGB", StreamDictText(img));
    }

    [Fact]
    public void Jp2_ColorSpace_Gray_1Component()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 1, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Contains("/DeviceGray", StreamDictText(img));
    }

    [Fact]
    public void Jp2_BitsPerComponent_8()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 3, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Contains("/BitsPerComponent 8", StreamDictText(img));
    }

    [Fact]
    public void Jp2_BitsPerComponent_12_OmittedForPdfAConformance()
    {
        // See RawCodestream_BitsPerComponent_12_OmittedForPdfAConformance: 12-bit is outside the
        // PDF/A-2 {1,2,4,8,16} set, so the optional JPXDecode /BitsPerComponent entry is omitted.
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 3, bpc: 12);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.DoesNotContain("/BitsPerComponent", StreamDictText(img));
    }

    [Fact]
    public void Jp2_StreamBytes_AreConformantJp2WithBoxes_NotBareCodestream()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 1, bpc: 8);
        var img = JpxImageLoader.Load(jp2.File);
        var body = ExtractStreamBody(WriteStream(img));

        // The embedded bytes are a minimal conformant JP2 (signature + ftyp + jp2h + jp2c) so the
        // ihdr/colr boxes PDF/A-2 6.2.8.3 inspects are present — NOT the bare codestream that the
        // historical behaviour embedded (which made veraPDF read 0 channels / 0 bit depth).
        Assert.True(StartsWithJp2Signature(body));
        Assert.True(ContainsBoxType(body, "jp2h"u8), "jp2h superbox must be preserved.");
        Assert.True(ContainsBoxType(body, "ihdr"u8), "ihdr box must be preserved.");
        Assert.NotEqual(jp2.Codestream, body);
        Assert.True(ContainsSequence(body, jp2.Codestream), "Codestream must be preserved verbatim.");

        // The source carries no ancillary boxes, so the minimal JP2 equals the whole source file.
        Assert.Equal(jp2.File, body);
    }

    [Fact]
    public void Jp2_AncillaryBoxes_AreDropped_NeverExpandingSize()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 3, bpc: 8, colrEnum: 16);
        // Append a top-level ancillary "xml " metadata box that PDF/A does not require for the
        // image and that the PDF carries its own metadata for. It must not bloat the embedded image.
        var xmlBox = MakeBox(0x786D6C20u /* "xml " */, "<meta>padding metadata payload bytes</meta>"u8.ToArray());
        var bloated = Concat(jp2.File, xmlBox);

        var img = JpxImageLoader.Load(bloated);
        var body = ExtractStreamBody(WriteStream(img));

        Assert.True(body.Length < bloated.Length, "Ancillary boxes must be dropped, not embedded.");
        Assert.False(ContainsSequence(body, xmlBox), "The xml box must not appear in the embedded stream.");
        // Colour/geometry boxes and the codestream survive intact.
        Assert.True(ContainsBoxType(body, "ihdr"u8));
        Assert.True(ContainsBoxType(body, "colr"u8));
        Assert.True(ContainsSequence(body, jp2.Codestream));
        // Equals the minimal JP2 (source minus the dropped ancillary box).
        Assert.Equal(jp2.File, body);
    }

    // ── JP2 colr box: enumerated colour space refinement ─────────────────────

    [Fact]
    public void Jp2_Colr_SRgb16_ReportsDeviceRgb()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 3, bpc: 8, colrEnum: 16);
        var img = JpxImageLoader.Load(jp2.File);
        Assert.Contains("/DeviceRGB", StreamDictText(img));
    }

    [Fact]
    public void Jp2_Colr_Greyscale17_ReportsDeviceGray()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 3, bpc: 8, colrEnum: 17);
        var img = JpxImageLoader.Load(jp2.File);
        // colr greyscale enum overrides the 3-component default of DeviceRGB.
        Assert.Contains("/DeviceGray", StreamDictText(img));
    }

    // ── DecodeToRaster: not supported ─────────────────────────────────────────

    [Fact]
    public void DecodeToRaster_RawCodestream_ThrowsNotSupported()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 1, bpc: 8);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() => JpxImageLoader.Load(j2k, opts));
    }

    [Fact]
    public void DecodeToRaster_Jp2File_ThrowsNotSupported()
    {
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 1, bpc: 8);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() => JpxImageLoader.Load(jp2.File, opts));
    }

    // ── Malformed / security inputs ───────────────────────────────────────────

    [Fact]
    public void Load_EmptyBytes_Throws()
    {
        Assert.Throws<ArgumentException>(() => JpxImageLoader.Load([]));
    }

    [Fact]
    public void Load_NullBytes_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => JpxImageLoader.Load(null!));
    }

    [Fact]
    public void RawCodestream_MissingSocMarker_Throws()
    {
        // Starts with wrong first byte — not a JP2 box either.
        byte[] bad = [0xFF, 0x00, 0x01, 0x02, 0x03];
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(bad));
    }

    [Fact]
    public void RawCodestream_TruncatedAfterSoc_Throws()
    {
        // Only the SOC marker, no SIZ.
        byte[] bad = [0xFF, 0x4F];
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(bad));
    }

    [Fact]
    public void RawCodestream_SizMarkerMissing_Throws()
    {
        // SOC followed by a different marker segment (COD=FF52) instead of SIZ.
        var bad = new byte[]
        {
            0xFF, 0x4F,  // SOC
            0xFF, 0x52,  // COD marker (not SIZ)
            0x00, 0x02,  // segment length = 2 (no body bytes)
            // no more markers
        };
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(bad));
    }

    [Fact]
    public void RawCodestream_TruncatedSizPayload_Throws()
    {
        // SOC + SIZ marker present, but SIZ segment Lsiz is below minimum (38).
        var bad = new byte[]
        {
            0xFF, 0x4F,  // SOC
            0xFF, 0x51,  // SIZ
            0x00, 0x04,  // Lsiz = 4 (too small, minimum is 38)
            0x00, 0x00,  // dummy body
        };
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(bad));
    }

    [Fact]
    public void RawCodestream_OversizedDimensions_Throws()
    {
        // 200000 × 200000 = 40 billion pixels — exceeds the 100M pixel safety limit.
        var j2k = BuildRawCodestream(width: 200_000, height: 200_000, components: 3, bpc: 8);
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(j2k));
    }

    [Fact]
    public void Jp2_BadSignatureMagic_Throws()
    {
        // Correct box type ("jP  ") but wrong magic bytes — not a valid JP2 file.
        // Not a raw codestream either (no SOC marker), so must throw.
        var bad = new byte[12];
        bad[4] = 0x6A; bad[5] = 0x50; bad[6] = 0x20; bad[7] = 0x20; // type "jP  "
        bad[8] = 0xDE; bad[9] = 0xAD; bad[10] = 0xBE; bad[11] = 0xEF; // wrong magic
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(bad));
    }

    [Fact]
    public void Jp2_BoxLengthExceedsBuffer_Throws()
    {
        // Valid JP2 file, then corrupt the ftyp box length to exceed the buffer.
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 1, bpc: 8);
        var corrupted = (byte[])jp2.File.Clone();
        // ftyp box starts at byte offset 12; overwrite length (bytes 12–15).
        corrupted[12] = 0xFF;
        corrupted[13] = 0xFF;
        corrupted[14] = 0xFF;
        corrupted[15] = 0xFF;
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(corrupted));
    }

    [Fact]
    public void Jp2_TruncatedBoxHeader_Throws()
    {
        // Truncate to just the signature box (12 bytes) — no jp2c box, so must throw.
        var jp2 = BuildJp2File(width: 4, height: 4, nc: 1, bpc: 8);
        var truncated = jp2.File[..12];
        Assert.Throws<InvalidDataException>(() => JpxImageLoader.Load(truncated));
    }

    // ── Passthrough option is explicit no-op ──────────────────────────────────

    [Fact]
    public void ExplicitPassthrough_RawCodestream_Works()
    {
        var j2k = BuildRawCodestream(width: 4, height: 4, components: 1, bpc: 8);
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.Passthrough };
        var img = JpxImageLoader.Load(j2k, opts);
        Assert.Equal(4, img.Width);
        Assert.Equal(4, img.Height);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string StreamDictText(PdfImageXObject img)
        => System.Text.Encoding.Latin1.GetString(WriteStream(img));

    private static byte[] WriteStream(PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        img.BuildStream().WriteTo(writer);
        return ms.ToArray();
    }

    private static byte[] ExtractStreamBody(byte[] raw)
    {
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        if (markerStart < 0) throw new InvalidOperationException("stream marker not found");
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        if (endStream < 0) throw new InvalidOperationException("endstream marker not found");
        return raw[bodyStart..endStream];
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

    private static bool ContainsSequence(byte[] haystack, ReadOnlySpan<byte> needle)
        => FindSequence(haystack, needle) >= 0;

    // True when the bytes begin with the 12-byte JP2 signature box.
    private static bool StartsWithJp2Signature(byte[] data)
    {
        ReadOnlySpan<byte> sig = [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];
        return data.Length >= 12 && data.AsSpan(0, 12).SequenceEqual(sig);
    }

    // True when a box of the given 4-byte type appears (matches the TBox field anywhere).
    private static bool ContainsBoxType(byte[] data, ReadOnlySpan<byte> type)
        => FindSequence(data, type) >= 0;

    private static byte[] Concat(params byte[][] parts)
    {
        var result = new List<byte>();
        foreach (var p in parts)
            result.AddRange(p);
        return [.. result];
    }

    // ── Raw codestream builder ────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal but structurally valid JPEG 2000 raw codestream consisting of:
    ///   SOC (FF4F) + SIZ marker segment + minimal EOC (FF D9).
    /// The SIZ segment encodes the given dimensions, component count, and bit depth.
    /// </summary>
    private static byte[] BuildRawCodestream(
        int width, int height, int components, int bpc, bool signedSsiz = false)
    {
        // SIZ segment layout (ISO 15444-1 §A.5.1):
        //   Lsiz(2) Rsiz(2) Xsiz(4) Ysiz(4) XOsiz(4) YOsiz(4)
        //   XTsiz(4) YTsiz(4) XTOsiz(4) YTOsiz(4) Csiz(2)
        //   [per component: Ssiz(1) XRsiz(1) YRsiz(1)]
        // Fixed header = 2+2+4+4+4+4+4+4+4+4+2 = 38 bytes (includes the Lsiz field itself)
        // + 3 bytes per component
        var lsiz = 38 + (3 * components);

        var buf = new List<byte>();

        // SOC marker
        buf.AddRange([0xFF, 0x4F]);

        // SIZ marker
        buf.AddRange([0xFF, 0x51]);

        // Lsiz (big-endian uint16)
        Append16(buf, (ushort)lsiz);

        // Rsiz (capabilities = 0 for baseline)
        Append16(buf, 0);

        // Xsiz = width (XOsiz=0, so width = Xsiz - XOsiz)
        Append32(buf, (uint)width);
        // Ysiz = height
        Append32(buf, (uint)height);
        // XOsiz = 0
        Append32(buf, 0);
        // YOsiz = 0
        Append32(buf, 0);
        // XTsiz = width (one tile covering the whole image)
        Append32(buf, (uint)width);
        // YTsiz = height
        Append32(buf, (uint)height);
        // XTOsiz = 0
        Append32(buf, 0);
        // YTOsiz = 0
        Append32(buf, 0);
        // Csiz (number of components, uint16)
        Append16(buf, (ushort)components);

        // Per-component descriptors: Ssiz(1) XRsiz(1) YRsiz(1)
        for (var c = 0; c < components; c++)
        {
            // Ssiz high bit = signed flag; low 7 bits = (bitDepth - 1)
            byte ssiz = (byte)((bpc - 1) & 0x7F);
            if (signedSsiz) ssiz |= 0x80;
            buf.Add(ssiz);
            buf.Add(0x01); // XRsiz = 1
            buf.Add(0x01); // YRsiz = 1
        }

        // EOC marker (minimal — real streams would have tile data here)
        buf.AddRange([0xFF, 0xD9]);

        return [.. buf];
    }

    // ── JP2 box file builder ──────────────────────────────────────────────────

    private sealed record Jp2FileResult(byte[] File, byte[] Codestream);

    /// <summary>
    /// Builds a minimal JP2 box file containing:
    ///   signature box (12 bytes) + ftyp box + jp2h(ihdr[+colr]) + jp2c box.
    /// The jp2c payload is a minimal raw codestream built by <see cref="BuildRawCodestream"/>.
    /// </summary>
    private static Jp2FileResult BuildJp2File(
        int width, int height, int nc, int bpc, int? colrEnum = null)
    {
        var codestream = BuildRawCodestream(width, height, nc, bpc);
        var buf = new List<byte>();

        // Signature box: LBox=12, TBox="jP  " (6A 50 20 20), payload = magic 0D 0A 87 0A
        Append32(buf, 12);
        buf.AddRange([0x6A, 0x50, 0x20, 0x20]); // type "jP  "
        buf.AddRange([0x0D, 0x0A, 0x87, 0x0A]); // magic

        // ftyp box: brand="jp2 ", MinV=0, compat=["jp2 "]
        var ftypPayload = new List<byte>();
        ftypPayload.AddRange([0x6A, 0x70, 0x32, 0x20]); // brand "jp2 "
        Append32(ftypPayload, 0); // MinV
        ftypPayload.AddRange([0x6A, 0x70, 0x32, 0x20]); // compat "jp2 "
        AppendBox(buf, 0x66747970u, [.. ftypPayload]); // "ftyp"

        // ihdr payload: Height(4) Width(4) NC(2) BPC(1) C(1) UnkC(1) IPR(1)
        var ihdrPayload = new List<byte>();
        Append32(ihdrPayload, (uint)height);
        Append32(ihdrPayload, (uint)width);
        Append16(ihdrPayload, (ushort)nc);
        ihdrPayload.Add((byte)((bpc - 1) & 0x7F)); // BPC
        ihdrPayload.Add(0x07);  // C = 7 (wavelet transform)
        ihdrPayload.Add(0x00);  // UnkC
        ihdrPayload.Add(0x00);  // IPR

        var ihdrBox = MakeBox(0x69686472u, [.. ihdrPayload]); // "ihdr"

        // Optional colr box
        byte[] colrBox = [];
        if (colrEnum.HasValue)
        {
            var colrPayload = new List<byte> { 0x01, 0x00, 0x00 }; // METH=1, PREC=0, APPROX=0
            Append32(colrPayload, (uint)colrEnum.Value); // EnumCS
            colrBox = MakeBox(0x636F6C72u, [.. colrPayload]); // "colr"
        }

        // jp2h superbox containing ihdr [+ colr]
        var jp2hContent = new List<byte>();
        jp2hContent.AddRange(ihdrBox);
        jp2hContent.AddRange(colrBox);
        AppendBox(buf, 0x6A703268u, [.. jp2hContent]); // "jp2h"

        // jp2c box containing the codestream
        AppendBox(buf, 0x6A703263u, codestream); // "jp2c"

        return new Jp2FileResult([.. buf], codestream);
    }

    // Writes a 4-byte big-endian uint32 to buf.
    private static void Append32(List<byte> buf, uint v)
    {
        buf.Add((byte)(v >> 24));
        buf.Add((byte)((v >> 16) & 0xFF));
        buf.Add((byte)((v >> 8) & 0xFF));
        buf.Add((byte)(v & 0xFF));
    }

    // Writes a 2-byte big-endian uint16 to buf.
    private static void Append16(List<byte> buf, ushort v)
    {
        buf.Add((byte)(v >> 8));
        buf.Add((byte)(v & 0xFF));
    }

    // Creates a JP2 box byte array: LBox(4) TBox(4) payload.
    private static byte[] MakeBox(uint type, byte[] payload)
    {
        var box = new List<byte>();
        Append32(box, (uint)(8 + payload.Length));
        Append32(box, type);
        box.AddRange(payload);
        return [.. box];
    }

    // Appends a JP2 box (LBox+TBox+payload) to buf.
    private static void AppendBox(List<byte> buf, uint type, byte[] payload)
        => buf.AddRange(MakeBox(type, payload));
}
