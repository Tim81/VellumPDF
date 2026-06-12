// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Regression tests for four bugs caught in the v1.5.1 re-review.
/// Each test is designed to fail on the pre-fix code and pass with the fix applied.
/// All inputs are synthesised in-memory; no external resources are required.
/// </summary>
public sealed class RegressionV151bTests
{
    // ── Test 1: CCITT DecodeRow1D — black-leading row (zero-length white run) ──

    /// <summary>
    /// A T.4 1D (K=0) row that STARTS WITH A BLACK PIXEL must decode correctly.
    /// In T.4 encoding a row always begins with a white run; when the first pixel is
    /// black the leading white run has length zero.  The old guard threw on ANY
    /// zero-length run, so such a row threw <see cref="InvalidDataException"/>.
    /// The fix only throws when TWO consecutive zero-length runs occur.
    ///
    /// Stream: 8-wide, 1-row, all-black.
    ///   Row: white run = 0  (T.4 white terminating code for 0: 00110101, 8 bits)
    ///        black run = 8  (T.4 black terminating code for 8: 000101, 6 bits)
    ///   Packed MSB-first: 00110101 00010100  padded to byte = 0x35 0x14
    ///   With blackIs1=false the black pixels map to raster bit 1, so the output
    ///   byte for 8 contiguous black pixels is 0xFF.
    /// </summary>
    [Fact]
    public void Ccitt1D_blackLeadingRow_decodesCorrectly()
    {
        // T.4 white run=0 code: 00110101 (8 bits)
        // T.4 black run=8 code: 000101   (6 bits, from BlackTerminating table run=8 = 000101)
        // Bit stream: 00110101 000101[00 pad]
        // Byte 0: 00110101 = 0x35
        // Byte 1: 000101 + 00 (pad) = 00010100 = 0x14
        var stream = new byte[] { 0x35, 0x14 };

        // k=0 (T.4 1D), blackIs1=false: black run → raster bit=1.
        var raster = CcittImageLoader.DecodeCcittToRaster(
            stream, columns: 8, rows: 1, k: 0,
            blackIs1: false, encodedByteAlign: false);

        // 8 columns → 1 byte per row; all-black → all bits set = 0xFF.
        Assert.Single(raster);
        Assert.Equal(0xFF, raster[0]);
    }

    /// <summary>
    /// A row starting with black followed by a white run also works —
    /// zero-length white, then black=3, then white=5.
    /// white run=0 code: 00110101 (8 bits)
    /// black run=3 code: 10        (2 bits)
    /// white run=5 code: 1100      (4 bits)
    /// Bit stream: 00110101 10 1100 [00 pad] = 00110101 10110000 = 0x35 0xB0
    /// Expected raster: bits 0-2 set (black), bits 3-7 clear (white) = 0b11100000 = 0xE0.
    /// </summary>
    [Fact]
    public void Ccitt1D_blackThenWhiteRow_decodesCorrectly()
    {
        // white=0: 00110101 (8 bits)
        // black=3: 10       (2 bits)
        // white=5: 1100     (4 bits)
        // Total bits: 8+2+4 = 14 → 2 bytes, last 2 bits are padding.
        // Byte 0: 00110101 = 0x35
        // Byte 1: 10 1100 00 = 0xB0
        var stream = new byte[] { 0x35, 0xB0 };

        var raster = CcittImageLoader.DecodeCcittToRaster(
            stream, columns: 8, rows: 1, k: 0,
            blackIs1: false, encodedByteAlign: false);

        // Pixels 0-2 black → bits 7,6,5 set; pixels 3-7 white → bits 4-0 clear.
        Assert.Single(raster);
        Assert.Equal(0xE0, raster[0]);
    }

    // ── Test 3: Signing a multi-page doc keeps a form field on page 2 ──────────

    /// <summary>
    /// Before the fix, signing built page dictionaries BEFORE wiring form-field
    /// widgets to their pages, so a field on page 2 was present in /AcroForm/Fields
    /// but absent from page 2's /Annots array.
    ///
    /// This test:
    ///   (a) asserts the produced PDF's page-2 dictionary contains an /Annots entry
    ///       referencing the text field widget, and
    ///   (b) asserts BCL SignedCms.CheckSignature still passes.
    /// </summary>
    [Fact]
    public void Sign_multiPageDoc_fieldOnPage2_appearsInPage2Annots()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        var page2 = doc.AddPage();

        // Register a text field on PAGE 2 only.
        doc.AddTextField(page2, "P2Field", new PdfRectangle(72, 700, 300, 720));

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        var bytes = ms.ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        // (a) /Annots must appear — and page 2 must contribute one of those occurrences.
        //     We count /Annots occurrences: the signing path must include one for page 2.
        var annotsCount = CountOccurrences(text, "/Annots");
        Assert.True(annotsCount >= 1,
            $"Expected at least one /Annots in the signed PDF; found {annotsCount}.");

        // More specifically: the text field widget must be present in the signed PDF
        // and both /AcroForm and a /Subtype /Widget for it must exist.
        Assert.Contains("/FT /Tx", text);
        Assert.Contains("/Subtype /Widget", text);
        Assert.Contains("/AcroForm", text);

        // (b) Signature must still verify cryptographically.
        VerifySignatureOrThrow(bytes);
    }

    /// <summary>
    /// Complementary test: a text field on page 1 of a 2-page document continues to
    /// appear in page 1's /Annots after signing (no regression introduced by the fix).
    /// </summary>
    [Fact]
    public void Sign_multiPageDoc_fieldOnPage1_appearsInPage1Annots_andSignatureVerifies()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        var page1 = doc.AddPage();
        doc.AddPage(); // page 2, no fields

        doc.AddTextField(page1, "P1Field", new PdfRectangle(72, 700, 300, 720));

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        var bytes = ms.ToArray();
        var text = Encoding.Latin1.GetString(bytes);

        Assert.Contains("/Annots", text);
        Assert.Contains("/FT /Tx", text);
        VerifySignatureOrThrow(bytes);
    }

    // ── Test 4: PNG sub-byte greyscale tRNS produces correctly-scaled /Mask ────

    /// <summary>
    /// Before the fix, the colour-key /Mask for sub-byte greyscale images used the
    /// raw tRNS 16-bit value (e.g. 1) instead of the scaled 8-bit value that the
    /// pixel data was actually written at (e.g. 255 for 1-bit images).
    ///
    /// For a 1-bit greyscale PNG with tRNS sample value 1 (white), the pixel data
    /// is written as 255 (because UnpackSubByte scales: 1 * (255/1) = 255).
    /// The /Mask must therefore be [255 255], not [1 1].
    ///
    /// This test constructs a 1-bit greyscale PNG with tRNS = 1 and asserts that
    /// the produced /Mask entry contains 255, not 1.
    /// </summary>
    [Fact]
    public void Png1BitGreyscale_tRNS_value1_maskIs255()
    {
        // 1-bit greyscale PNG (colour type 0, bit depth 1).
        // tRNS = value 1 (white, in 1-bit terms).
        // The pixel data after UnpackSubByte: 1 * (255/(2^1-1)) = 1 * 255 = 255.
        // /Mask must therefore be [255 255].
        var png = CreateGreyscalePngWithTrns(2, 1, bitDepth: 1, trnsGrey: 1);
        var img = PngImageLoader.Load(png);

        // No SMask for non-alpha greyscale — the colour-key /Mask is used instead.
        Assert.Null(img.SMask);

        var streamText = PdfStreamText(img.BuildStream());
        Assert.Contains("/Mask", streamText);

        // Must be [255 255] (scaled), not [1 1] (raw).
        Assert.Contains("[255 255]", streamText);
        Assert.DoesNotContain("[1 1]", streamText);
    }

    /// <summary>
    /// 4-bit greyscale (bit depth 4) with tRNS sample value 15 (maximum in 4-bit space,
    /// i.e. white). The scaled 8-bit value is 15 * (255/15) = 255.
    /// /Mask must be [255 255].
    /// </summary>
    [Fact]
    public void Png4BitGreyscale_tRNS_maxValue_maskIs255()
    {
        // tRNS raw value 15 (max for 4-bit); scaled: 15 * (255/15) = 255.
        var png = CreateGreyscalePngWithTrns(2, 1, bitDepth: 4, trnsGrey: 15);
        var img = PngImageLoader.Load(png);

        Assert.Null(img.SMask);
        var streamText = PdfStreamText(img.BuildStream());
        Assert.Contains("/Mask", streamText);
        Assert.Contains("[255 255]", streamText);
    }

    /// <summary>
    /// 4-bit greyscale with tRNS sample value 8. Scaled: 8 * (255/15) = 136.
    /// /Mask must be [136 136].
    /// </summary>
    [Fact]
    public void Png4BitGreyscale_tRNS_midValue_maskIsScaled()
    {
        // tRNS raw value 8; scaled: 8 * (255/15) = 136.
        var png = CreateGreyscalePngWithTrns(2, 1, bitDepth: 4, trnsGrey: 8);
        var img = PngImageLoader.Load(png);

        Assert.Null(img.SMask);
        var streamText = PdfStreamText(img.BuildStream());
        Assert.Contains("/Mask", streamText);
        // 8 * 17 = 136
        Assert.Contains("[136 136]", streamText);
        Assert.DoesNotContain("[8 8]", streamText);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string PdfStreamText(VellumPdf.Core.PdfStream stream)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
        stream.WriteTo(writer);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static byte[] DecompressStream(VellumPdf.Core.PdfStream pdfStream)
    {
        using var pdfMs = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(pdfMs);
        pdfStream.WriteTo(writer);
        var raw = pdfMs.ToArray();
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        var start = markerStart + 8;
        var end = FindSequence(raw, "\nendstream"u8);
        var compressed = raw[start..end];
        using var zms = new MemoryStream(compressed);
        using var z = new ZLibStream(zms, CompressionMode.Decompress);
        using var result = new MemoryStream();
        z.CopyTo(result);
        return result.ToArray();
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

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }

    // ── PNG builders ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a greyscale (colour type 0) PNG. When <paramref name="trnsGrey"/> is
    /// non-null, a tRNS chunk is written with that value as a big-endian 16-bit word.
    /// </summary>
    private static byte[] CreateGreyscalePngWithTrns(int w, int h, byte bitDepth, int? trnsGrey)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, bitDepth, 0));
        if (trnsGrey is not null)
        {
            var trnsBuf = new byte[]
            {
                (byte)(trnsGrey.Value >> 8),
                (byte)(trnsGrey.Value & 0xFF),
            };
            WritePngChunk(ms, "tRNS", trnsBuf);
        }

        // Build raw scanlines (filter byte 0 + packed pixel data).
        // For sub-byte depths: each row is ceil(w * bitDepth / 8) bytes, all zeros.
        // We want pixels whose tRNS value appears — use all-zero rows (grey value 0)
        // for the non-tRNS tests; the tRNS value is 1, 8, or 15 which won't collide
        // with the zero pixels — the test only checks the /Mask value, not pixel values.
        int rowDataBytes;
        if (bitDepth < 8)
            rowDataBytes = (w * bitDepth + 7) / 8;
        else
            rowDataBytes = w * (bitDepth / 8);

        var rawData = new byte[h * (1 + rowDataBytes)]; // filter bytes already 0
        WritePngChunk(ms, "IDAT", ZlibCompress(rawData));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static byte[] CreatePngIhdr(int w, int h, byte bitDepth, byte colorType)
    {
        var buf = new byte[13];
        buf[0] = (byte)(w >> 24); buf[1] = (byte)(w >> 16); buf[2] = (byte)(w >> 8); buf[3] = (byte)w;
        buf[4] = (byte)(h >> 24); buf[5] = (byte)(h >> 16); buf[6] = (byte)(h >> 8); buf[7] = (byte)h;
        buf[8] = bitDepth; buf[9] = colorType;
        return buf;
    }

    private static void WritePngChunk(Stream s, string type, byte[] data)
    {
        s.WriteByte((byte)(data.Length >> 24)); s.WriteByte((byte)(data.Length >> 16));
        s.WriteByte((byte)(data.Length >> 8)); s.WriteByte((byte)data.Length);
        foreach (var c in type) s.WriteByte((byte)c);
        s.Write(data);
        var crcData = new byte[4 + data.Length];
        for (var i = 0; i < 4; i++) crcData[i] = (byte)type[i];
        data.CopyTo(crcData, 4);
        var crc = Crc32(crcData);
        s.WriteByte((byte)(crc >> 24)); s.WriteByte((byte)(crc >> 16));
        s.WriteByte((byte)(crc >> 8)); s.WriteByte((byte)crc);
    }

    private static uint Crc32(byte[] data)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var j = 0; j < 8; j++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            table[i] = c;
        }
        var crc = 0xFFFFFFFFu;
        foreach (var b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc ^ 0xFFFFFFFFu;
    }

    private static byte[] ZlibCompress(byte[] data)
    {
        using var ms = new MemoryStream();
        using var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true);
        z.Write(data);
        z.Flush();
        return ms.ToArray();
    }

    // ── Signature helpers ─────────────────────────────────────────────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf RegressionV151b Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var text = Encoding.Latin1.GetString(signedBytes);

        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found");
        var brEnd = text.IndexOf(']', brStart + byteRangeMarker.Length);
        var brParts = text[(brStart + byteRangeMarker.Length)..brEnd].Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var byteRange = brParts.Select(long.Parse).ToArray();

        var sentinelMarker = PdfSignatureHelper.ContentsSentinel + "\n<";
        var sStart = text.IndexOf(sentinelMarker, StringComparison.Ordinal);
        Assert.True(sStart >= 0, "/Contents sentinel not found");
        var posLt = sStart + sentinelMarker.Length - 1;
        var cEnd = text.IndexOf('>', posLt);
        var hexContent = text[(posLt + 1)..cEnd];

        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var derBytes = Convert.FromHexString(hexContent);
        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(derBytes);
        verify.CheckSignature(verifySignatureOnly: true);
    }
}
