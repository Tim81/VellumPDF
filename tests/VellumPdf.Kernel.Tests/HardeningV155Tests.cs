// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Annotations;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Images;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Regression tests for the v1.5.5 hardening findings (issues #85, #83).
/// All inputs are synthesised in-memory; no external resources are required.
/// </summary>
public sealed class HardeningV155Tests
{
    // ── Item A (#85): PNG tRNS transparency ─────────────────────────────────

    /// <summary>
    /// A colour-type-3 (indexed) PNG with a tRNS table must produce an /SMask
    /// whose alpha samples match the per-palette-index alpha values.
    /// </summary>
    [Fact]
    public void PngIndexedWithTrns_hasSmask()
    {
        // 2×1 indexed PNG: palette index 0 → red (alpha 0 = transparent),
        // palette index 1 → blue (alpha 128 = semi-transparent).
        var png = CreateIndexedPngWithTrns(2, 1,
            palette: [255, 0, 0,   // index 0: red
                      0, 0, 255],  // index 1: blue
            trns: [0, 128],        // alpha: index0=0, index1=128
            pixelRow: [0x01]);     // packed 4-bit row: high nibble=0, low nibble=1
        var img = PngImageLoader.Load(png);
        Assert.NotNull(img.SMask);
    }

    [Fact]
    public void PngIndexedWithTrns_smaskAlphaMatchesTrnsTable()
    {
        // 2×1 indexed PNG, 8-bit:
        // pixel 0 = index 0 (alpha 0 = fully transparent)
        // pixel 1 = index 1 (alpha 255 = fully opaque)
        var png = CreateIndexedPng8BitWithTrns(2, 1,
            palette: [255, 0, 0,   // index 0: red
                      0, 0, 255],  // index 1: blue
            trns: [0, 255],        // alpha: index0=0, index1=255
            pixels: [0, 1]);       // pixel 0→index0, pixel 1→index1
        var img = PngImageLoader.Load(png);
        Assert.NotNull(img.SMask);
        var alpha = DecompressStream(img.SMask!);
        Assert.Equal(2, alpha.Length);
        Assert.Equal(0, alpha[0]);   // index 0 → alpha 0
        Assert.Equal(255, alpha[1]); // index 1 → alpha 255
    }

    [Fact]
    public void PngIndexedWithTrns_smaskAlphaDefaultsToOpaqueForMissingEntries()
    {
        // 3-pixel indexed PNG; tRNS only covers first 2 indices.
        // Pixel at index 2 (beyond tRNS) must get alpha 255.
        var png = CreateIndexedPng8BitWithTrns(3, 1,
            palette: [255, 0, 0,   // index 0: red
                      0, 255, 0,   // index 1: green
                      0, 0, 255],  // index 2: blue
            trns: [0, 128],        // only 2 entries; index 2 defaults to 255
            pixels: [0, 1, 2]);
        var img = PngImageLoader.Load(png);
        Assert.NotNull(img.SMask);
        var alpha = DecompressStream(img.SMask!);
        Assert.Equal(3, alpha.Length);
        Assert.Equal(0, alpha[0]);
        Assert.Equal(128, alpha[1]);
        Assert.Equal(255, alpha[2]); // beyond tRNS → opaque
    }

    [Fact]
    public void PngIndexedWithoutTrns_noSmask()
    {
        // An indexed PNG with no tRNS chunk must produce no SMask.
        var png = CreateIndexedPng8BitWithTrns(2, 1,
            palette: [255, 0, 0, 0, 0, 255],
            trns: null,
            pixels: [0, 1]);
        var img = PngImageLoader.Load(png);
        Assert.Null(img.SMask);
    }

    /// <summary>
    /// A greyscale (colour type 0) PNG with a tRNS chunk must produce a /Mask
    /// colour-key array in the image dictionary (not an SMask stream).
    /// </summary>
    [Fact]
    public void PngGreyscaleWithTrns_hasMaskInStreamDict()
    {
        // 2×1 greyscale 8-bit PNG; tRNS = grey value 0 is transparent.
        var png = CreateGreyscalePngWithTrns(2, 1, bitDepth: 8, trnsGrey: 0);
        var img = PngImageLoader.Load(png);

        // No SMask — colour-key mask is used instead.
        Assert.Null(img.SMask);

        var streamText = PdfStreamText(img.BuildStream());
        Assert.Contains("/Mask", streamText);
        // /Mask [0 0] for 8-bit grey value 0
        Assert.Contains("[0 0]", streamText);
    }

    [Fact]
    public void PngRgbWithTrns_hasMaskInStreamDict()
    {
        // 2×1 RGB 8-bit PNG; tRNS = (255, 0, 0) is the transparent colour.
        var png = CreateRgbPngWithTrns(2, 1, bitDepth: 8, trnsR: 255, trnsG: 0, trnsB: 0);
        var img = PngImageLoader.Load(png);

        Assert.Null(img.SMask);
        var streamText = PdfStreamText(img.BuildStream());
        Assert.Contains("/Mask", streamText);
        // /Mask [255 255 0 0 0 0] for RGB (255,0,0)
        Assert.Contains("[255 255 0 0 0 0]", streamText);
    }

    [Fact]
    public void PngGreyscaleWithoutTrns_noMask()
    {
        var png = CreateGreyscalePngWithTrns(2, 1, bitDepth: 8, trnsGrey: null);
        var img = PngImageLoader.Load(png);
        Assert.Null(img.SMask);
        var streamText = PdfStreamText(img.BuildStream());
        Assert.DoesNotContain("/Mask", streamText);
    }

    // ── Item B (#83): Outline /Count sign convention ─────────────────────────

    /// <summary>
    /// An outline item with IsExpanded = false must emit a negative /Count value.
    /// </summary>
    [Fact]
    public void OutlineEntry_collapsed_emitsNegativeCount()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // Parent closed, two children
        doc.AddOutlineEntry(new PdfOutlineEntry
        {
            Title = "Parent",
            DestPage = page,
            Level = 0,
            IsExpanded = false,
        });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Child 1", DestPage = page, Level = 1 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Child 2", DestPage = page, Level = 1 });

        var content = SaveToString(doc);
        // Collapsed parent with 2 visible children → /Count -2
        Assert.Contains("/Count -2", content);
    }

    /// <summary>
    /// Default (all open) outline — positive /Count unchanged from existing behaviour.
    /// </summary>
    [Fact]
    public void OutlineEntry_allOpen_defaultBehaviourPreserved()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Chapter", DestPage = page, Level = 0 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Sec 1", DestPage = page, Level = 1 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Sec 2", DestPage = page, Level = 1 });

        var content = SaveToString(doc);
        // Open parent with 2 children → /Count 2 (same as before)
        Assert.Contains("/Count 2", content);
        // No negative counts
        Assert.DoesNotContain("/Count -", content);
    }

    [Fact]
    public void OutlineEntry_rootCountExcludesChildrenOfClosedItems()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();

        // Root sees 2 top-level items. The second is collapsed so its children
        // are not counted in the root /Count.
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Open Top", DestPage = page, Level = 0, IsExpanded = true });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Child of Open", DestPage = page, Level = 1 });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Closed Top", DestPage = page, Level = 0, IsExpanded = false });
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Child of Closed", DestPage = page, Level = 1 });

        var content = SaveToString(doc);
        // Root /Count: 2 top-level + 1 visible child of "Open Top" = 3
        // (child of "Closed Top" is hidden because parent is collapsed)
        Assert.Contains("/Count 3", content);
    }

    // ── Item C (#83): Signing path merges existing AcroForm fields ───────────

    /// <summary>
    /// A document with a text field that is then signed must contain both the
    /// text field and the signature field in /AcroForm /Fields.
    /// </summary>
    [Fact]
    public void SignedDoc_withTextField_bothFieldsInAcroForm()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.Finish();

        doc.AddTextField(page, "myText", new PdfRectangle(50, 700, 200, 720));

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        var text = Encoding.Latin1.GetString(ms.ToArray());

        // Both fields must appear in the output.
        Assert.Contains("/FT /Tx", text);   // text field
        Assert.Contains("/FT /Sig", text);  // signature field
        Assert.Contains("/SigFlags 3", text);
    }

    /// <summary>
    /// The signature on a document that also has form fields must still verify
    /// via BCL <see cref="SignedCms.CheckSignature"/>.
    /// </summary>
    [Fact]
    public void SignedDoc_withTextField_signatureStillVerifies()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        var font = doc.UseFont(Standard14.Helvetica);
        canvas.BeginText().SetFont(font, 12).SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText("test").EndText();
        canvas.Finish();

        doc.AddTextField(page, "name", new PdfRectangle(50, 650, 200, 670));

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        VerifySignatureOrThrow(ms.ToArray());
    }

    /// <summary>
    /// A signed document with no extra form fields still works correctly (regression guard).
    /// </summary>
    [Fact]
    public void SignedDoc_withNoOtherFields_signatureVerifies()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        VerifySignatureOrThrow(ms.ToArray());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SaveToString(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

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

    // ── PNG builders ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a 4-bit indexed PNG with a tRNS chunk.
    /// <paramref name="pixelRow"/> is one packed row (no filter byte).
    /// </summary>
    private static byte[] CreateIndexedPngWithTrns(
        int w, int h, byte[] palette, byte[] trns, byte[] pixelRow)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, 4, 3));
        WritePngChunk(ms, "PLTE", palette);
        WritePngChunk(ms, "tRNS", trns);
        // IDAT: filter byte 0 + packed row
        var packedRowBytes = (w * 4 + 7) / 8;
        var rawData = new byte[h * (1 + packedRowBytes)];
        for (var row = 0; row < h; row++)
        {
            rawData[row * (1 + packedRowBytes)] = 0; // filter = None
            for (var b = 0; b < pixelRow.Length && b < packedRowBytes; b++)
                rawData[row * (1 + packedRowBytes) + 1 + b] = pixelRow[b];
        }
        WritePngChunk(ms, "IDAT", ZlibCompress(rawData));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates an 8-bit indexed PNG. <paramref name="trns"/> may be null (no tRNS chunk).
    /// </summary>
    private static byte[] CreateIndexedPng8BitWithTrns(
        int w, int h, byte[] palette, byte[]? trns, byte[] pixels)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, 8, 3));
        WritePngChunk(ms, "PLTE", palette);
        if (trns is not null)
            WritePngChunk(ms, "tRNS", trns);
        // IDAT: rows with filter byte 0 + pixel data
        var rawData = new byte[h * (1 + w)];
        for (var row = 0; row < h; row++)
        {
            rawData[row * (1 + w)] = 0; // filter = None
            for (var col = 0; col < w; col++)
                rawData[row * (1 + w) + 1 + col] = col < pixels.Length ? pixels[row * w + col] : (byte)0;
        }
        WritePngChunk(ms, "IDAT", ZlibCompress(rawData));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates a greyscale (colour type 0) PNG. When <paramref name="trnsGrey"/> is non-null,
    /// a tRNS chunk is written with that 16-bit big-endian grey value.
    /// </summary>
    private static byte[] CreateGreyscalePngWithTrns(int w, int h, byte bitDepth, int? trnsGrey)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, bitDepth, 0));
        if (trnsGrey is not null)
        {
            // tRNS for greyscale: 2 bytes, big-endian 16-bit sample
            var trnsBuf = new byte[] { (byte)(trnsGrey.Value >> 8), (byte)(trnsGrey.Value & 0xFF) };
            WritePngChunk(ms, "tRNS", trnsBuf);
        }
        var bytesPerPixel = bitDepth == 16 ? 2 : 1;
        var rawData = new byte[h * (1 + w * bytesPerPixel)];
        // all zeros = grey value 0
        WritePngChunk(ms, "IDAT", ZlibCompress(rawData));
        WritePngChunk(ms, "IEND", []);
        return ms.ToArray();
    }

    /// <summary>
    /// Creates an RGB (colour type 2) PNG. When tRNS values are provided, a tRNS chunk
    /// is written with those 16-bit big-endian R, G, B values.
    /// </summary>
    private static byte[] CreateRgbPngWithTrns(int w, int h, byte bitDepth,
        int trnsR, int trnsG, int trnsB)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WritePngChunk(ms, "IHDR", CreatePngIhdr(w, h, bitDepth, 2));
        // tRNS: 6 bytes (three 16-bit big-endian samples)
        var trnsBuf = new byte[]
        {
            (byte)(trnsR >> 8), (byte)(trnsR & 0xFF),
            (byte)(trnsG >> 8), (byte)(trnsG & 0xFF),
            (byte)(trnsB >> 8), (byte)(trnsB & 0xFF),
        };
        WritePngChunk(ms, "tRNS", trnsBuf);
        var bytesPerPixel = bitDepth == 16 ? 6 : 3;
        var rawData = new byte[h * (1 + w * bytesPerPixel)];
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

    // ── Issue #89: no sentinel comment in signed output (veraPDF 6.4.3-1) ────

    /// <summary>
    /// Regression test for GitHub issue #89: the signed byte stream must contain no
    /// proprietary %VELLUM comment between /Contents and its hex-string value.
    /// veraPDF clause 6.4.3-1 rejects a /Contents token whose value is not a direct
    /// hex string.
    /// </summary>
    [Fact]
    public void Signed_doc_contents_value_has_no_sentinel_comment()
    {
        using var cert = CreateTestCertificate();
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        canvas.Finish();

        var ms = new MemoryStream();
        doc.Sign(ms, new PdfSignatureSettings { Certificate = cert });
        var signedBytes = ms.ToArray();
        var text = Encoding.Latin1.GetString(signedBytes);

        // No proprietary sentinel comment may appear anywhere in the output.
        Assert.DoesNotContain("%VELLUM", text);

        // Locate the signature dictionary's /Contents key by searching within the
        // ByteRange context. The signature dict always contains "/ByteRange [" followed
        // shortly by "\n/Contents " then the hex value. Find /ByteRange first to
        // skip any page-content /Contents entries that appear earlier in the file.
        const string byteRangeMarker = "/ByteRange [";
        var brIdx = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brIdx >= 0, "/ByteRange not found in signed PDF");
        const string contentsKey = "/Contents ";
        var ckIdx = text.IndexOf(contentsKey, brIdx + byteRangeMarker.Length, StringComparison.Ordinal);
        Assert.True(ckIdx >= 0, "Signature /Contents key not found after /ByteRange in signed PDF");

        // The char immediately after "/Contents " (plus optional PDF whitespace, but
        // no '%' comment) must be '<'. A newline between the key and the hex value is
        // valid PDF whitespace; a '%' comment is what veraPDF 6.4.3-1 rejects.
        var nextNonWs = ckIdx + contentsKey.Length;
        while (nextNonWs < text.Length && text[nextNonWs] is ' ' or '\t' or '\r' or '\n')
            nextNonWs++;
        Assert.Equal('<', text[nextNonWs]);
    }

    // ── Signature helpers ─────────────────────────────────────────────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf HardeningV155 Test",
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

        // Locate the '<' of the /Contents hex string by anchoring on /ByteRange:
        // the first '<' after the ByteRange ']' is the /Contents opening angle bracket.
        var posLt = text.IndexOf('<', brEnd);
        Assert.True(posLt >= 0, "/Contents '<' not found after /ByteRange");
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
