// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for CcittImageLoader (CCITT Group 3/4 passthrough) and the G4 TIFF re-wrap path.
///
/// Covers:
///   • Standalone passthrough: dictionary structure, verbatim stream bytes.
///   • /DecodeParms key presence/absence rules (K, Columns, Rows, BlackIs1, EncodedByteAlign).
///   • Validation errors: empty data, non-positive dimensions.
///   • All-white T.6 (Group 4) synthetic stream used to exercise the real G4 path.
/// </summary>
public sealed class CcittImageTests
{
    // ── Standalone passthrough — structural assertions ────────────────────────

    [Fact]
    public void CcittLoad_Filter_IsCcittFaxDecode()
    {
        var img = CcittImageLoader.Load([0x01, 0x02, 0x03], columns: 16, rows: 8);
        Assert.Contains("/CCITTFaxDecode", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_BitsPerComponent_Is1()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.Contains("/BitsPerComponent 1", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_ColorSpace_IsDeviceGray()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.Contains("/DeviceGray", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_DecodeParms_Present()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.Contains("/DecodeParms", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_Default_K_Is_Minus1()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.Contains("/K -1", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_Default_Columns_Correct()
    {
        var img = CcittImageLoader.Load([0x01], columns: 16, rows: 8);
        Assert.Contains("/Columns 16", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_Default_Rows_Correct()
    {
        var img = CcittImageLoader.Load([0x01], columns: 16, rows: 8);
        Assert.Contains("/Rows 8", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_Default_BlackIs1_Absent()
    {
        // BlackIs1=false is the PDF default; it must not be emitted.
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.DoesNotContain("/BlackIs1", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_Default_EncodedByteAlign_Absent()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.DoesNotContain("/EncodedByteAlign", StreamDictText(img));
    }

    // ── Stream bytes are verbatim (no Flate re-compression) ──────────────────

    [Fact]
    public void CcittLoad_StreamBytes_AreVerbatim()
    {
        byte[] ccittData = [0xAA, 0xBB, 0xCC, 0xDD, 0xEE];
        var img = CcittImageLoader.Load(ccittData, columns: 8, rows: 5);
        var raw = WriteStream(img);

        // Locate the stream body: after "\nstream\n", before "\nendstream".
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        Assert.True(markerStart >= 0, "Stream marker not found.");
        var bodyStart = markerStart + 8;
        var endStream = FindSequence(raw, "\nendstream"u8);
        Assert.True(endStream >= 0, "endstream not found.");
        var body = raw[bodyStart..endStream];

        Assert.Equal(ccittData, body);
    }

    // ── Optional keys present when requested ─────────────────────────────────

    [Fact]
    public void CcittLoad_BlackIs1_True_Present()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1, blackIs1: true);
        Assert.Contains("/BlackIs1 true", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_EncodedByteAlign_True_Present()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1, encodedByteAlign: true);
        Assert.Contains("/EncodedByteAlign true", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_K_Zero_IsEmitted()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1, k: 0);
        Assert.Contains("/K 0", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_K_Positive_IsEmitted()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1, k: 2);
        Assert.Contains("/K 2", StreamDictText(img));
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public void CcittLoad_EmptyData_Throws()
    {
        Assert.Throws<ArgumentException>(() => CcittImageLoader.Load([], columns: 8, rows: 1));
    }

    [Fact]
    public void CcittLoad_NullData_Throws()
    {
        Assert.Throws<ArgumentException>(() => CcittImageLoader.Load(null!, columns: 8, rows: 1));
    }

    [Fact]
    public void CcittLoad_ZeroColumns_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittImageLoader.Load([0x01], columns: 0, rows: 1));
    }

    [Fact]
    public void CcittLoad_NegativeColumns_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittImageLoader.Load([0x01], columns: -1, rows: 1));
    }

    [Fact]
    public void CcittLoad_ZeroRows_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittImageLoader.Load([0x01], columns: 8, rows: 0));
    }

    [Fact]
    public void CcittLoad_NegativeRows_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CcittImageLoader.Load([0x01], columns: 8, rows: -1));
    }

    // ── All-white G4 helper ───────────────────────────────────────────────────

    [Fact]
    public void CcittLoad_AllWhiteG4_DictCorrect()
    {
        const int cols = 16;
        const int rows = 8;
        var g4 = BuildAllWhiteG4(cols, rows);
        var img = CcittImageLoader.Load(g4, columns: cols, rows: rows);

        var dict = StreamDictText(img);
        Assert.Contains("/CCITTFaxDecode", dict);
        Assert.Contains("/BitsPerComponent 1", dict);
        Assert.Contains("/DeviceGray", dict);
        Assert.Contains("/K -1", dict);
        Assert.Contains("/Columns 16", dict);
        Assert.Contains("/Rows 8", dict);
        Assert.DoesNotContain("/BlackIs1", dict);
    }

    [Fact]
    public void CcittLoad_AllWhiteG4_Width_Correct()
    {
        var g4 = BuildAllWhiteG4(24, 10);
        var img = CcittImageLoader.Load(g4, columns: 24, rows: 10);
        Assert.Equal(24, img.Width);
        Assert.Equal(10, img.Height);
    }

    // ── All-white G4 builder (reusable by conformance-oracle tests) ──────────

    /// <summary>
    /// Builds a valid T.6 (CCITT Group 4 / MMR) stream encoding an all-white image of
    /// <paramref name="columns"/> × <paramref name="rows"/> pixels.
    ///
    /// T.6 encoding for a single all-white row against an imaginary all-white reference line:
    ///   Every changing element of the coding line is at the right edge (position columns),
    ///   coinciding with the reference changing element at the same position.
    ///   That matching is encoded as Vertical mode V(0): codeword = "1" (single bit 1).
    ///   One V(0) per row encodes the entire row.
    ///
    /// After all rows, EOFB (End Of Facsimile Block) = two EOL-like patterns:
    ///   each is 000000000001 (12 bits), total 24 bits.
    ///   (EOFB = two consecutive 000000000001 words, per T.6 §2.)
    ///
    /// Bits are packed MSB-first; the final byte is zero-padded.
    ///
    /// This produces a real, decodable T.6 stream. Any conformant CCITTFaxDecode
    /// implementation with K=-1, Columns=<paramref name="columns"/>, Rows=<paramref name="rows"/>
    /// will decode it as a fully white bilevel image.
    /// </summary>
    /// <param name="columns">Image width in pixels.</param>
    /// <param name="rows">Image height in pixels.</param>
    /// <returns>The T.6-encoded byte array, MSB-first, EOFB-terminated.</returns>
    public static byte[] BuildAllWhiteG4(int columns, int rows)
    {
        // Bit stream: rows × "1" (V0), then EOFB = "000000000001" "000000000001" (24 bits).
        // Total bits = rows + 24.
        var totalBits = rows + 24;
        var totalBytes = (totalBits + 7) / 8;

        var buf = new byte[totalBytes];
        var bitPos = 0; // current bit position in buf, MSB-first

        // Emit rows × V(0) = bit "1"
        for (var r = 0; r < rows; r++)
        {
            // Set bit at bitPos (MSB-first)
            var byteIdx = bitPos / 8;
            var bitIdx = 7 - (bitPos % 8);
            buf[byteIdx] |= (byte)(1 << bitIdx);
            bitPos++;
        }

        // Emit EOFB: two × 000000000001 (12 bits each = 24 bits total).
        // "000000000001" = 0x001 in MSB-first order (11 zero bits then one 1 bit).
        for (var eofbWord = 0; eofbWord < 2; eofbWord++)
        {
            // 11 zero bits (already zero in buf) — just advance bitPos by 11
            bitPos += 11;
            // then 1 bit
            var byteIdx = bitPos / 8;
            var bitIdx = 7 - (bitPos % 8);
            buf[byteIdx] |= (byte)(1 << bitIdx);
            bitPos++;
        }

        return buf;
    }

    // ── EndOfLine parameter ───────────────────────────────────────────────────

    [Fact]
    public void CcittLoad_Default_EndOfLine_Absent()
    {
        // EndOfLine=false (the PDF default for G4) must not be emitted.
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1);
        Assert.DoesNotContain("/EndOfLine", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_EndOfLine_True_Present()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1, endOfLine: true);
        Assert.Contains("/EndOfLine true", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_EndOfLine_False_Absent()
    {
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1, endOfLine: false);
        Assert.DoesNotContain("/EndOfLine", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_G3_K0_EndOfLine_AllParams_Present()
    {
        // Group 3 1D: K=0, EndOfLine=true, EncodedByteAlign=true, BlackIs1=true
        var img = CcittImageLoader.Load([0x01], columns: 8, rows: 1,
            k: 0, blackIs1: true, encodedByteAlign: true, endOfLine: true);
        var dict = StreamDictText(img);
        Assert.Contains("/K 0", dict);
        Assert.Contains("/EndOfLine true", dict);
        Assert.Contains("/EncodedByteAlign true", dict);
        Assert.Contains("/BlackIs1 true", dict);
        Assert.Contains("/CCITTFaxDecode", dict);
    }

    // ── DecodeToRaster option ─────────────────────────────────────────────────

    [Fact]
    public void CcittLoad_DecodeMode_Passthrough_ReturnsCcittFilter()
    {
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.Passthrough };
        var img = CcittImageLoader.Load([0x01, 0x02], columns: 8, rows: 1, opts);
        Assert.Contains("/CCITTFaxDecode", StreamDictText(img));
        Assert.DoesNotContain("/FlateDecode", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_DecodeMode_Passthrough_IsDefault_WithOptions()
    {
        // Load(byte[], int, int, ImageLoadOptions) with default options → passthrough (K=-1, CCITTFaxDecode).
        var opts = ImageLoadOptions.Default;
        var img = CcittImageLoader.Load([0x01, 0x02], columns: 8, rows: 1, opts);
        Assert.Contains("/CCITTFaxDecode", StreamDictText(img));
        Assert.DoesNotContain("/FlateDecode", StreamDictText(img));
    }

    [Fact]
    public void CcittLoad_DecodeMode_DecodeToRaster_G4_ThrowsNotSupportedException()
    {
        // Load(byte[], int, int, ImageLoadOptions) always uses K=-1 (G4).
        // G4 (K<0) decode-to-raster is not supported.
        var opts = new ImageLoadOptions { DecodeMode = ImageDecodeMode.DecodeToRaster };
        Assert.Throws<NotSupportedException>(() =>
            CcittImageLoader.Load([0x01], columns: 8, rows: 1, opts));
    }

    [Fact]
    public void CcittLoad_DecodeToRaster_NullOptions_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            CcittImageLoader.Load([0x01], columns: 8, rows: 1, null!));
    }

    // ── All-white 1D MH stream builder ────────────────────────────────────────

    /// <summary>
    /// Builds a minimal 1D Modified Huffman stream for an 8×2 all-white image.
    /// No EOL codes, byte-aligned rows (Compression=2 / Modified Huffman style).
    /// White run of 8: ITU-T T.4 terminating code = <c>10011</c> (5 bits), padded to byte.
    /// </summary>
    public static byte[] BuildAllWhite1D_8wide_2rows()
    {
        // Each row: white run of 8 pixels.
        // T.4 white terminating code for run=8 is: 10011 (5 bits).
        // Pad to byte: 10011 + 000 = 0b10011000 = 0x98.
        // Two rows: [0x98, 0x98].
        return [0x98, 0x98];
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] DecompressFlateStream(VellumPdf.Core.PdfStream pdfStream)
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
        using var z = new System.IO.Compression.ZLibStream(zms, System.IO.Compression.CompressionMode.Decompress);
        using var result = new MemoryStream();
        z.CopyTo(result);
        return result.ToArray();
    }

    private static string StreamDictText(VellumPdf.Images.PdfImageXObject img)
    {
        return System.Text.Encoding.Latin1.GetString(WriteStream(img));
    }

    private static byte[] WriteStream(VellumPdf.Images.PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        var writer = new VellumPdf.IO.PdfWriter(ms);
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
}
