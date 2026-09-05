// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Images;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="PdfDocumentReader.ExtractImages()"/> and <see cref="PdfReadPage.ExtractImages()"/>
/// (#98): the public API surface, exercised against writer-built fixtures where the Kernel writer
/// can produce the construct (via <c>InternalsVisibleTo</c> onto its image XObject writer),
/// and hand-built PDF text otherwise, for constructs no writer emits (a bare-name colour space, a
/// malformed <c>/Decode</c> array, an encrypted stream, and similar adversarial shapes).
/// </summary>
public sealed class ImageExtractionTests
{
    // ── Hand-built PDF plumbing (constructs the writer cannot produce) ──────────────────────────

    private sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    private static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var obj in objects.OrderBy(o => o.Num))
        {
            offsets[obj.Num] = (int)ms.Position;
            if (obj.Stream is null)
            {
                W($"{obj.Num} 0 obj\n{obj.Dict}\nendobj\n");
            }
            else
            {
                var trimmed = obj.Dict.TrimEnd();
                var withLength = trimmed[..^2].TrimEnd() + $" /Length {obj.Stream.Length} >>";
                W($"{obj.Num} 0 obj\n{withLength}\nstream\n");
                ms.Write(obj.Stream);
                W("\nendstream\nendobj\n");
            }
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");
        return ms.ToArray();
    }

    /// <summary>A one-page document whose page draws <c>/Im0 Do</c>, with object 10 as the image
    /// dictionary (caller-supplied, minus /Length, which this adds) and object 11 as its
    /// data.</summary>
    private static byte[] BuildOnePageWithImage(string imageDict, byte[] imageData, string? annots = null)
    {
        var pageDict = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R"
            + (annots is null ? "" : $" /Annots {annots}") + " >>";
        return BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, pageDict),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, imageDict, imageData));
    }

    private static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    // ── Writer-built fixture plumbing ────────────────────────────────────────────────────────────

    private static byte[] BuildDocWithImage(PdfImageXObject image, PdfEncryptionSettings? encryption = null)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        doc.RegisterImageXObject(page, image, "Im0");
        var canvas = new PdfCanvas(page);
        canvas.DoXObject("Im0");
        canvas.Finish();
        if (encryption is not null)
            doc.Encrypt(encryption);
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static PdfDocumentReader Open(byte[] pdfBytes, string? password = null) =>
        password is null ? PdfReader.Open(pdfBytes) : PdfReader.Open(pdfBytes, new PdfReaderOptions { Password = password });

    // ── 1. DCT passthrough ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DctPassthrough_dataIsVerbatim_encodingJpeg_canEncodePngFalse()
    {
        var jpegBytes = "NOT-A-REAL-JPEG-BUT-A-RECOGNISABLE-PASSTHROUGH-BODY-0123456789"u8.ToArray();
        var image = new PdfImageXObject(
            width: 8, height: 6, streamData: jpegBytes, filter: PdfName.DCTDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8);
        var pdf = BuildDocWithImage(image);

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        var extracted = Assert.Single(result.Images);
        Assert.Equal(jpegBytes, extracted.Data.ToArray());
        Assert.Equal(PdfImageEncoding.Jpeg, extracted.Encoding);
        Assert.Equal(".jpg", extracted.FileExtension);
        Assert.Equal(8, extracted.BitsPerComponent);
        Assert.Equal(PdfImageColorSpaceFamily.DeviceRgb, extracted.ColorSpace!.Family);
        Assert.False(extracted.CanEncodePng);
    }

    // ── 2. Encrypted twins ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void DctPassthrough_Aes256R6Encrypted_decryptsBackToTheSameJpegBytes()
    {
        var jpegBytes = "ANOTHER-RECOGNISABLE-PASSTHROUGH-BODY-abcdefghijklmnop"u8.ToArray();
        var image = new PdfImageXObject(
            width: 4, height: 4, streamData: jpegBytes, filter: PdfName.DCTDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8);
        var pdf = BuildDocWithImage(image, new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });

        using var reader = Open(pdf, "u");
        Assert.NotNull(reader.Encryption);

        // Confirms the fixture is encrypted: its own raw stream body must NOT equal the plaintext.
        var stream = GetFirstImageStream(reader);
        Assert.False(stream.RawBody.Span.SequenceEqual(jpegBytes));

        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);
        Assert.Equal(jpegBytes, extracted.Data.ToArray());
    }

    [Fact]
    public void DctPassthrough_Rc4128Encrypted_decryptsBackToTheSameJpegBytes()
    {
        var jpegBytes = "RC4-128-PASSTHROUGH-BODY-9876543210"u8.ToArray();
        var flatePlain = "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray();

        // Object identities: 1 catalog, 2 pages, 3 page, 4 content ("/Im0 Do"), 5 image, 6
        // /Encrypt.
        var content = HandBuiltEncryptedDocuments.Encrypt("enc-rc4-128.pdf", "u", 4, 0, "/Im0 Do"u8.ToArray());
        var imageBody = HandBuiltEncryptedDocuments.Encrypt("enc-rc4-128.pdf", "u", 5, 0, jpegBytes);

        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.5\n");
        var o1 = (int)ms.Position; W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position; W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
            + "/Resources << /XObject << /Im0 5 0 R >> >> /Contents 4 0 R >>\nendobj\n");
        var o4 = (int)ms.Position;
        W($"4 0 obj\n<< /Length {content.Length} >>\nstream\n");
        ms.Write(content);
        W("\nendstream\nendobj\n");
        var o5 = (int)ms.Position;
        W("5 0 obj\n<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /BitsPerComponent 8 "
            + $"/ColorSpace /DeviceRGB /Filter /DCTDecode /Length {imageBody.Length} >>\nstream\n");
        ms.Write(imageBody);
        W("\nendstream\nendobj\n");
        var o6 = (int)ms.Position;
        W($"6 0 obj\n{HandBuiltEncryptedDocuments.Rc4EncryptDict}\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 7\n");
        W("0000000000 65535 f \n");
        foreach (var off in new[] { o1, o2, o3, o4, o5, o6 })
            W($"{off:D10} 00000 n \n");
        W($"trailer\n<< /Size 7 /Root 1 0 R /Encrypt 6 0 R "
            + $"/ID [<{Convert.ToHexStringLower(HandBuiltEncryptedDocuments.Id0)}>"
            + $"<{Convert.ToHexStringLower(HandBuiltEncryptedDocuments.Id0)}>] >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray(), new PdfReaderOptions { Password = "u" });
        Assert.NotNull(reader.Encryption);

        var stream = GetFirstImageStream(reader);
        Assert.False(stream.RawBody.Span.SequenceEqual(jpegBytes));

        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);
        Assert.Equal(jpegBytes, extracted.Data.ToArray());
    }

    private static ParsedStream GetFirstImageStream(PdfDocumentReader reader)
    {
        var pages = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!));
        var kids = Assert.IsType<PdfArray>(reader.ResolveValue(pages.Get(PdfName.Kids)!));
        var page = Assert.IsType<PdfDictionary>(reader.ResolveValue(kids[0]));
        var resources = Assert.IsType<PdfDictionary>(reader.ResolveValue(page.Get(PdfName.Resources)!));
        var xobjects = Assert.IsType<PdfDictionary>(reader.ResolveValue(resources.Get(PdfName.XObject)!));
        var imageRef = xobjects.Get(new PdfName("Im0"))!;
        return reader.ResolveStream(Assert.IsType<PdfIndirectReference>(imageRef))
            ?? throw new InvalidOperationException("Im0 did not resolve to a stream.");
    }

    // ── 3. Raw Flate RGB 8-bit ───────────────────────────────────────────────────────────────────

    [Fact]
    public void RawFlateRgb8_4x3_dataMatchesSamples_pngRoundTrips()
    {
        // 4x3 RGB, 3 bytes/pixel, arbitrary but deterministic sample bytes.
        var samples = new byte[4 * 3 * 3];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = (byte)(i * 7 + 3);

        var image = new PdfImageXObject(
            width: 4, height: 3, streamData: samples, filter: PdfName.FlateDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8);
        var pdf = BuildDocWithImage(image);

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(samples, extracted.Data.ToArray());
        Assert.True(extracted.TryEncodePng(out var png));
        var xobj = PngImageLoader.Load(png!, ImageLoadOptions.Default);
        var roundTripped = FlateDecompress(ExtractStreamBody(WriteStream(xobj)));
        Assert.Equal(samples, roundTripped);
    }

    // ── 4. Gray at 1, 2, 4, 8, 16 bpc ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void Gray_atEveryBitDepth_pngIhdrDepthMatches_lossless(int bpc)
    {
        // Width chosen so bpc * width is exactly 8 bits (one byte, no padding) for every depth up
        // to 8, and one 16-bit sample for bpc 16.
        var width = bpc == 16 ? 1 : 8 / bpc;
        var rowBytes = bpc == 16 ? 2 : 1;
        var samples = bpc == 16 ? new byte[] { 0x12, 0x34 } : new byte[] { 0b10110001 };

        var colorSpace = ImageColorSpace.DeviceGray;
        var image = new PdfImageXObject(
            width: width, height: 1, streamData: samples, filter: PdfName.FlateDecode,
            colorSpace: colorSpace, bitsPerComponent: bpc);
        var pdf = BuildDocWithImage(image);

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(bpc, extracted.BitsPerComponent);
        Assert.Equal(samples, extracted.Data.ToArray());
        Assert.True(extracted.TryEncodePng(out var png));
        Assert.Equal(bpc, png![8 + 8 + 8]); // IHDR bit depth byte
        Assert.Equal(rowBytes, extracted.Data.Length);
    }

    // ── 5. Indexed ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Indexed_overDeviceRgb_highValueAndLookupExact_plteMatches()
    {
        // [/Indexed /DeviceRGB 1 <lookup>], 2 entries, image 2x1 at 8 bpc: indices 0, 1.
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 2 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace [/Indexed /DeviceRGB 1 <FF000000FF00>] /Filter /FlateDecode >>",
            Flate([0, 1]));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(PdfImageColorSpaceFamily.Indexed, extracted.ColorSpace!.Family);
        Assert.Equal(1, extracted.ColorSpace.HighValue);
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0, 0xFF, 0 }, extracted.ColorSpace.Lookup.ToArray());
        Assert.True(extracted.TryEncodePng(out var png));
        var plte = ExtractChunk(png!, "PLTE"u8);
        // At /BitsPerComponent 8 the PNG palette holds min(2^8, 256) = 256 entries; only the first
        // two (hival 1) are the lookup's own bytes, the rest a repeat of entry 1 (§8.6.6.3).
        Assert.Equal(768, plte.Length);
        Assert.Equal(new byte[] { 0xFF, 0, 0, 0, 0xFF, 0 }, plte[..6]);
    }

    [Fact]
    public void Indexed_overDeviceGray_greyExpandedToTriplets()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace [/Indexed /DeviceGray 0 <80>] /Filter /FlateDecode >>",
            Flate([0]));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.True(extracted.TryEncodePng(out var png));
        var plte = ExtractChunk(png!, "PLTE"u8);
        Assert.Equal(768, plte.Length); // min(2^8, 256) entries at /BitsPerComponent 8.
        Assert.Equal(new byte[] { 0x80, 0x80, 0x80 }, plte[..3]);
    }

    [Fact]
    public void Indexed_depthClamped_plteHasFourEntries()
    {
        // hival 255 (256-entry-capable lookup) at /BitsPerComponent 2: PNG palette is
        // min(2^2, 256) = 4 entries, the FIRST four lookup triples.
        var lookup = new byte[768];
        for (var i = 0; i < 4; i++)
        {
            lookup[i * 3] = (byte)(i * 10);
            lookup[i * 3 + 1] = (byte)(i * 10 + 1);
            lookup[i * 3 + 2] = (byte)(i * 10 + 2);
        }
        var lookupHex = Convert.ToHexStringLower(lookup);
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 1 /BitsPerComponent 2 "
            + $"/ColorSpace [/Indexed /DeviceRGB 255 <{lookupHex}>] /Filter /FlateDecode >>",
            Flate([0b00_01_10_11]));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.True(extracted.TryEncodePng(out var png));
        var plte = ExtractChunk(png!, "PLTE"u8);
        Assert.Equal(lookup[..12], plte);
    }

    // ── 6. Stencil mask ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void StencilMask_isStencilMask_noColorSpace_pngType0Depth1()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true "
            + "/Filter /FlateDecode >>",
            Flate([0b10101010]));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        // A stored 0 is the painted area under the default [0 1] (§8.9.6.2).
        Assert.True(extracted.IsStencilMask);
        Assert.Null(extracted.ColorSpace);
        Assert.Equal(1, extracted.BitsPerComponent);
        Assert.True(extracted.TryEncodePng(out var png));
        Assert.Equal(0, png![8 + 8 + 9]); // colour type 0
        Assert.Equal(1, png[8 + 8 + 8]); // bit depth 1
    }

    // ── 7. /SMask ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SMask_writerBuilt_populatesSoftMask_pngWithAlphaInterleaves()
    {
        var colorSamples = new byte[] { 10, 20, 30, 40, 50, 60 }; // 2x1 RGB
        var maskSamples = new byte[] { 0x11, 0x22 }; // 2x1 grey (alpha)

        var maskStream = new PdfStream(maskSamples);
        var image = new PdfImageXObject(
            width: 2, height: 1, streamData: colorSamples, filter: PdfName.FlateDecode,
            colorSpace: ImageColorSpace.DeviceRgb, bitsPerComponent: 8, sMask: maskStream);
        var pdf = BuildDocWithImage(image);

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        // Masks are occurrences too: the parent image and its soft mask are both entries.
        Assert.Equal(2, result.Images.Count);
        var extracted = result.Images[0];

        Assert.NotNull(extracted.SoftMask);
        Assert.Same(extracted.SoftMask, result.Images[1]);
        Assert.True(extracted.TryEncodePngWithAlpha(out var png));
        var idat = InflateIdat(png!);
        // Row: filter byte + interleaved RGBA x2 = 1 + 8 = 9 bytes.
        Assert.Equal(9, idat.Length);
        Assert.Equal(0, idat[0]); // filter byte
        Assert.Equal(new byte[] { 10, 20, 30, 0x11, 40, 50, 60, 0x22 }, idat[1..]);
    }

    [Fact]
    public void SMask_withMatte_hasMatteTrue_pngWithAlphaRefused()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /FlateDecode /SMask 11 0 R >>",
            Flate([1, 2, 3]),
            annots: null);
        // Rebuild with the SMask object appended (BuildOnePageWithImage only wires one extra
        // object).
        pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceRGB /Filter /FlateDecode /SMask 11 0 R >>", Flate([1, 2, 3])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /Matte [0 0 0] >>", Flate([0x80])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        Assert.Equal(2, result.Images.Count);
        var extracted = result.Images[0];

        Assert.True(extracted.SoftMask!.HasMatte);
        Assert.False(extracted.TryEncodePngWithAlpha(out var png));
        Assert.Null(png);
    }

    [Fact]
    public void SMask_dimensionMismatch_pngWithAlphaRefused_noDiagnostic()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 2 /Height 2 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /SMask 11 0 R >>", Flate([1, 2, 3, 4])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([0x80])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        Assert.Equal(2, result.Images.Count);
        var extracted = result.Images[0];

        Assert.NotNull(extracted.SoftMask);
        Assert.False(extracted.TryEncodePngWithAlpha(out var png));
        Assert.Null(png);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageMaskInvalid);
    }

    [Fact]
    public void SMask_wrongColorSpace_reports503_softMaskNull()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /SMask 11 0 R >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceRGB /Filter /FlateDecode >>", Flate([1, 2, 3])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Null(extracted.SoftMask);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageMaskInvalid);
    }

    // ── 8. /Mask stream ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void MaskStream_isImageMask_populatesExplicitMask()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /Mask 11 0 R >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true "
                + "/Filter /FlateDecode >>", Flate([0xFF])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        Assert.Equal(2, result.Images.Count);
        var extracted = result.Images[0];

        Assert.NotNull(extracted.ExplicitMask);
        Assert.Same(extracted.ExplicitMask, result.Images[1]);
        Assert.True(extracted.ExplicitMask!.IsExplicitMask);
        Assert.True(extracted.ExplicitMask.IsStencilMask);
    }

    [Fact]
    public void MaskStream_notAnImageMask_reports503_explicitMaskNull()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /Mask 11 0 R >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([2])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Null(extracted.ExplicitMask);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageMaskInvalid);
    }

    [Fact]
    public void MaskStream_carriesItsOwnSMask_reports503_explicitMaskDropped()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /Mask 11 0 R >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true "
                + "/Filter /FlateDecode /SMask 12 0 R >>", Flate([0xFF])),
            new Obj(12, "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate(new byte[8])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Null(extracted.ExplicitMask);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageMaskInvalid);
    }

    // ── 9. JPX ───────────────────────────────────────────────────────────────────────────────────

    private static readonly byte[] Jp2Signature =
        [0x00, 0x00, 0x00, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A];

    [Fact]
    public void Jpx_jp2Boxed_extensionJp2_noDiagnostic()
    {
        var payload = Jp2Signature.Concat("REST-OF-JP2-FILE"u8.ToArray()).ToArray();
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode >>", payload);

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(payload, extracted.Data.ToArray());
        Assert.Equal(".jp2", extracted.FileExtension);
        Assert.Equal(0, extracted.BitsPerComponent);
        Assert.False(extracted.CanEncodePng);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageJpxSignatureUnrecognised);
    }

    [Fact]
    public void Jpx_bareCodestream_extensionJ2k_reports506()
    {
        var payload = new byte[] { 0xFF, 0x4F, 0xFF, 0x51 }.Concat("REST"u8.ToArray()).ToArray();
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode >>", payload);

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(payload, extracted.Data.ToArray());
        Assert.Equal(".j2k", extracted.FileExtension);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageJpxSignatureUnrecognised);
    }

    [Fact]
    public void Jpx_neitherShape_extensionJp2_reports506()
    {
        var payload = "NEITHER-SHAPE-AT-ALL"u8.ToArray();
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode >>", payload);

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(payload, extracted.Data.ToArray());
        Assert.Equal(".jp2", extracted.FileExtension);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageJpxSignatureUnrecognised);
    }

    [Fact]
    public void Jpx_sMaskInData2_exposed()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode /SMaskInData 2 >>",
            Jp2Signature);

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(2, extracted.SMaskInData);
    }

    [Fact]
    public void Jpx_sMaskInData1_besideSMask_reports503_sMaskInDataRetained()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode "
                + "/SMaskInData 1 /SMask 11 0 R >>", Jp2Signature),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(1, extracted.SMaskInData);
        Assert.Null(extracted.SoftMask);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageMaskInvalid);
    }

    // ── 9b. /SMaskInData on a non-JPX image: Table 87 scopes it to JPXDecode alone ───────────────

    [Fact]
    public void NonJpx_sMaskInData1_besideValidSMask_smaskKept_sMaskInDataZero_reports500Not503()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /SMaskInData 1 /SMask 11 0 R >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([2])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Equal(2, result.Images.Count);
        var extracted = result.Images[0];
        Assert.Equal(0, extracted.SMaskInData);
        Assert.NotNull(extracted.SoftMask);
        Assert.Same(extracted.SoftMask, result.Images[1]);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
        Assert.DoesNotContain(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageMaskInvalid);
    }

    // ── 10. JBIG2 with /JBIG2Globals ─────────────────────────────────────────────────────────────

    [Fact]
    public void Jbig2_withGlobals_dataAndGlobalsExact_canEncodePngFalse()
    {
        var segmentBytes = "JBIG2-EMBEDDED-SEGMENTS"u8.ToArray();
        var globalsBytes = "JBIG2-GLOBAL-SEGMENTS"u8.ToArray();

        var globalsStream = new PdfStream(globalsBytes);
        var decodeParms = new PdfDictionary(); // JBIG2Globals wired in by PdfDocument during Save.
        var image = new PdfImageXObject(
            width: 4, height: 4, streamData: segmentBytes, filter: PdfName.JBIG2Decode,
            colorSpace: ImageColorSpace.DeviceGray, bitsPerComponent: 1, sMask: null,
            sMaskBitsPerComponent: 8, decodeParms: decodeParms, jbig2Globals: globalsBytes);
        var pdf = BuildDocWithImage(image);

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(segmentBytes, extracted.Data.ToArray());
        Assert.Equal(PdfImageEncoding.Jbig2, extracted.Encoding);
        Assert.Equal(globalsBytes, extracted.Jbig2!.Globals.ToArray());
        Assert.False(extracted.CanEncodePng);
    }

    [Fact]
    public void Jbig2_globalsReferenceUnresolvable_reports500_globalsEmpty_imageKept()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 8 /Height 8 /BitsPerComponent 1 "
                + "/ColorSpace /DeviceGray /Filter /JBIG2Decode "
                + "/DecodeParms << /JBIG2Globals 99 0 R >> >>", "JBIG2-SEGMENTS"u8.ToArray()));
        // Object 99 is never defined: /JBIG2Globals names it, but it does not resolve to a stream.

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal("JBIG2-SEGMENTS"u8.ToArray(), extracted.Data.ToArray());
        Assert.Empty(extracted.Jbig2!.Globals.ToArray());
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    // ── 11. CCITT ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Ccitt_withDecodeParms_allEightMembersMatch()
    {
        var payload = "CCITT-PAYLOAD"u8.ToArray();
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1728 /Height 4 /BitsPerComponent 1 "
            + "/ColorSpace /DeviceGray /Filter /CCITTFaxDecode "
            + "/DecodeParms << /K -1 /Columns 1728 /Rows 4 /BlackIs1 true /EncodedByteAlign true "
            + "/EndOfLine true /EndOfBlock false /DamagedRowsBeforeError 2 >> >>",
            payload);

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(payload, extracted.Data.ToArray());
        Assert.Equal(-1, extracted.CcittFax!.K);
        Assert.Equal(1728, extracted.CcittFax.Columns);
        Assert.Equal(4, extracted.CcittFax.Rows);
        Assert.True(extracted.CcittFax.BlackIs1);
        Assert.True(extracted.CcittFax.EncodedByteAlign);
        Assert.True(extracted.CcittFax.EndOfLine);
        Assert.False(extracted.CcittFax.EndOfBlock);
        Assert.Equal(2, extracted.CcittFax.DamagedRowsBeforeError);
        Assert.False(extracted.CanEncodePng);
    }

    [Fact]
    public void Ccitt_withoutDecodeParms_table11Defaults()
    {
        var payload = "CCITT-PAYLOAD"u8.ToArray();
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1728 /Height 4 /BitsPerComponent 1 "
            + "/ColorSpace /DeviceGray /Filter /CCITTFaxDecode >>",
            payload);

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(payload, extracted.Data.ToArray());
        Assert.Equal(0, extracted.CcittFax!.K);
        Assert.Equal(1728, extracted.CcittFax.Columns);
        Assert.Equal(0, extracted.CcittFax.Rows);
        Assert.True(extracted.CcittFax.EndOfBlock);
        Assert.False(extracted.CcittFax.BlackIs1);
        Assert.False(extracted.CcittFax.EncodedByteAlign);
        Assert.False(extracted.CcittFax.EndOfLine);
        Assert.Equal(0, extracted.CcittFax.DamagedRowsBeforeError);
    }

    [Fact]
    public void Ccitt_bitsPerComponentEight_forcedToOne_reports505()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /CCITTFaxDecode >>",
            "X"u8.ToArray());

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(1, extracted.BitsPerComponent);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    // ── 11b. Table 87's fixed depth applies even when /BitsPerComponent is absent or wrong ───────

    [Fact]
    public void Dct_noBitsPerComponent_forcedToEight_reports505_imageKept()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 "
            + "/ColorSpace /DeviceGray /Filter /DCTDecode >>",
            "JPEG"u8.ToArray());

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(8, extracted.BitsPerComponent);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    [Fact]
    public void Dct_bitsPerComponentTwelve_forcedToEight_reports505_imageKept()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 12 "
            + "/ColorSpace /DeviceGray /Filter /DCTDecode >>",
            "JPEG"u8.ToArray());

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(8, extracted.BitsPerComponent);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    [Fact]
    public void Ccitt_noBitsPerComponent_forcedToOne_reports505_imageKept()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 "
            + "/ColorSpace /DeviceGray /Filter /CCITTFaxDecode >>",
            "X"u8.ToArray());

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(1, extracted.BitsPerComponent);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    [Fact]
    public void Jbig2_noBitsPerComponent_forcedToOne_reports505_imageKept()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 "
            + "/ColorSpace /DeviceGray /Filter /JBIG2Decode >>",
            "X"u8.ToArray());

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Equal(1, extracted.BitsPerComponent);
        Assert.Single(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    // ── 12. DCT /DecodeParms ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dct_colorTransformZero_exposedExactly()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /DCTDecode /DecodeParms << /ColorTransform 0 >> >>",
            "JPEG"u8.ToArray());

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);
        Assert.Equal(0, extracted.Dct!.ColorTransform);
    }

    [Fact]
    public void Dct_noDecodeParms_colorTransformNull()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /DCTDecode >>",
            "JPEG"u8.ToArray());

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);
        Assert.Null(extracted.Dct!.ColorTransform);
    }

    /// <summary>
    /// <c>/DecodeParms</c> carries two elements against a one-element <c>/Filter</c> chain: the
    /// filter chain's own positional alignment (<c>DecodeCore</c> in Filters.cs) puts the image
    /// filter's own parms at index 0, the same index as the filter itself, not at the array's last
    /// element. A last-element heuristic would read the second dictionary instead and report 1.
    /// </summary>
    [Fact]
    public void Dct_decodeParmsLongerThanFilterChain_usesPositionallyAlignedDict_notLastElement()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /DCTDecode "
            + "/DecodeParms [<< /ColorTransform 1 >> << /ColorTransform 0 >>] >>",
            "JPEG"u8.ToArray());

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);
        Assert.Equal(1, extracted.Dct!.ColorTransform);
    }

    [Fact]
    public void Dct_colorTransformTwo_reports500_treatedAsAbsent()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceRGB /Filter /DCTDecode /DecodeParms << /ColorTransform 2 >> >>",
            "JPEG"u8.ToArray());

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Null(extracted.Dct!.ColorTransform);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    // ── 13. Inline images ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InlineImage_basic_isInline_dataExact()
    {
        var content = "BI /W 2 /H 2 /CS /G /BPC 8 /L 4 ID \x01\x02\x03\x04 EI"u8.ToArray();
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R >>"),
            new Obj(4, "<< >>", content));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.True(extracted.IsInline);
        Assert.Null(extracted.ObjectNumber);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, extracted.Data.ToArray());
    }

    [Fact]
    public void InlineImage_includeInlineImagesFalse_listEmpty()
    {
        var content = "BI /W 2 /H 2 /CS /G /BPC 8 /L 4 ID \x01\x02\x03\x04 EI"u8.ToArray();
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R >>"),
            new Obj(4, "<< >>", content));

        using var reader = Open(pdf);
        var result = reader.ExtractImages(new PdfImageExtractionOptions { IncludeInlineImages = false });

        Assert.Empty(result.Images);
    }

    [Fact]
    public void InlineImage_jbig2Filter_neverDelimitedAsAnImage_reports307()
    {
        // §7.4.7 and §8.9.7 both forbid JBIG2Decode on an inline image; the content interpreter's
        // own hasDisallowedFilter guard (shared with JPXDecode and Crypt) reports this and skips
        // the OnInlineImage callback entirely, so ImageDecoder never sees it.
        var content = "BI /W 1 /H 1 /F /JBIG2Decode /L 1 ID \x00 EI"u8.ToArray();
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R >>"),
            new Obj(4, "<< >>", content));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.InlineImageMalformed);
    }

    [Fact]
    public void InlineImage_namedIndexedColorSpace_resolvedThroughCurrentResources()
    {
        var content = "BI /W 1 /H 1 /CS /CS0 /BPC 8 /L 1 ID \x00 EI"u8.ToArray();
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /ColorSpace << /CS0 [/Indexed /DeviceRGB 1 <FF0000FFFFFF>] >> >> "
                + "/Contents 4 0 R >>"),
            new Obj(4, "<< >>", content));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal(PdfImageColorSpaceFamily.Indexed, extracted.ColorSpace!.Family);
    }

    // ── 14. Draw order, dedupe, identity ─────────────────────────────────────────────────────────

    [Fact]
    public void DrawOrder_pageLevel_noDedupe_sharesDataInstance_documentLevel_deduped()
    {
        var content = "/Im0 Do\n/Im1 Do\n/Im0 Do"u8.ToArray();
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R /Im1 11 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", content),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([2])));

        using var reader = Open(pdf);
        var page = reader.GetPage(0);

        var pageResult = page.ExtractImages();
        Assert.Equal(3, pageResult.Images.Count);
        Assert.Equal(10, pageResult.Images[0].ObjectNumber);
        Assert.Equal(11, pageResult.Images[1].ObjectNumber);
        Assert.Equal(10, pageResult.Images[2].ObjectNumber);
        Assert.Same(pageResult.Images[0], pageResult.Images[2]);

        var docResult = reader.ExtractImages();
        Assert.Equal(2, docResult.Images.Count);
        Assert.Equal(10, docResult.Images[0].ObjectNumber);
        Assert.Equal(0, docResult.Images[0].Generation);
        Assert.Equal(11, docResult.Images[1].ObjectNumber);
    }

    [Fact]
    public void DrawOrder_sharedAcrossTwoPages_documentLevelHoldsItOnce_atFirstPage()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 6 0 R >>"),
            new Obj(6, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        var extracted = Assert.Single(result.Images);
        Assert.Equal(0, extracted.PageIndex);
    }

    // ── 15. Form XObject ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ImageInsideForm_found_sameFormOnTwoPages_twoOccurrencesOneDocumentImage()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Fm0 20 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Fm0 Do"u8.ToArray()),
            new Obj(5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Fm0 20 0 R >> >> /Contents 6 0 R >>"),
            new Obj(6, "<< >>", "/Fm0 Do"u8.ToArray()),
            new Obj(20, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);

        var page0 = reader.GetPage(0).ExtractImages();
        Assert.Single(page0.Images);
        var page1 = reader.GetPage(1).ExtractImages();
        Assert.Single(page1.Images);

        var doc = reader.ExtractImages();
        Assert.Single(doc.Images);
    }

    // ── 16. Annotation appearances ───────────────────────────────────────────────────────────────

    [Fact]
    public void Annotation_stampAppearanceStream_found_withOptionFalse_none()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R /Annots [5 0 R] >>"),
            new Obj(4, "<< >>", []),
            new Obj(5, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N 20 0 R >> >>"),
            new Obj(20, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);
        var withAppearances = reader.ExtractImages();
        Assert.Single(withAppearances.Images);

        var without = reader.ExtractImages(new PdfImageExtractionOptions { IncludeAnnotationAppearances = false });
        Assert.Empty(without.Images);
    }

    [Fact]
    public void Annotation_widgetWithOnOffStates_bothFound()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R /Annots [5 0 R] >>"),
            new Obj(4, "<< >>", []),
            new Obj(5, "<< /Type /Annot /Subtype /Widget /Rect [0 0 1 1] "
                + "/AP << /N << /On 7 0 R /Off 8 0 R >> >> >>"),
            new Obj(7, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> >>", "/Im0 Do"u8.ToArray()),
            new Obj(8, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im1 11 0 R >> >> >>", "/Im1 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])),
            new Obj(11, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([2])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Equal(2, result.Images.Count);
    }

    [Fact]
    public void Annotation_apNIsImageSubtype_reports509_extractsNothingFromIt()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R /Annots [5 0 R] >>"),
            new Obj(4, "<< >>", []),
            new Obj(5, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N 10 0 R >> >>"),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Empty(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.AnnotationAppearanceUnusable);
    }

    [Fact]
    public void Annotation_oneFormNamedByTwoAnnotations_interpretedOnce()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R /Annots [5 0 R 6 0 R] >>"),
            new Obj(4, "<< >>", []),
            new Obj(5, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N 20 0 R >> >>"),
            new Obj(6, "<< /Type /Annot /Subtype /Stamp /Rect [1 1 2 2] /AP << /N 20 0 R >> >>"),
            new Obj(20, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Single(result.Images);
    }

    // §12.5.3's /F Hidden flag and Table 87's /OC entry are both rendering-time visibility, not
    // conformance; extraction reports what the file contains, so an image behind either is still
    // returned with no diagnostic naming the reason.
    [Fact]
    public void Annotation_hiddenFlag_appearanceStillWalked_imageStillReturned()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Resources << >> "
                + "/Contents 4 0 R /Annots [5 0 R] >>"),
            new Obj(4, "<< >>", []),
            // /F 2: bit 2 (Hidden), ISO 32000-2 Table 168.
            new Obj(5, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /F 2 /AP << /N 20 0 R >> >>"),
            new Obj(20, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Single(result.Images);
    }

    [Fact]
    public void ImageInOffOptionalContentGroup_stillReturned_ocNotEvaluated()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R /OCProperties << /D << /OFF [20 0 R] >> >> >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /OC 20 0 R >>", Flate([1])),
            new Obj(20, "<< /Type /OCG /Name (Layer) >>"));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();

        Assert.Single(result.Images);
    }

    // ── 16b. Nothing escapes ExtractImages() through a malformed indirect-reference chain ────────

    // Patches one cross-reference row's own offset digits to a value past the file's own length,
    // the shape PdfDocumentReader.CheckedOffset turns into InvalidDataException: resolving THAT
    // object (however it is reached) throws instead of returning a value. BuildPdf's own xref
    // table writes each row as exactly 20 bytes ("{offset:D10} 00000 n \n", or the free entry's
    // "0000000000 65535 f \n"), immediately after "xref\n0 {size}\n", so row N's own offset digits
    // sit at a byte position this method can compute directly rather than re-parsing the table.
    private static byte[] PoisonObjectOffset(byte[] pdf, int objectNumber)
    {
        var marker = "xref\n0 "u8;
        var markerStart = FindSequence(pdf, marker);
        if (markerStart < 0)
            throw new InvalidOperationException("xref table marker not found");
        var lineEnd = Array.IndexOf(pdf, (byte)'\n', markerStart + marker.Length);
        var rowsStart = lineEnd + 1;
        var rowStart = rowsStart + objectNumber * 20;

        var poisoned = (byte[])pdf.Clone();
        // 999999999 (nine digits, zero-padded to the field's own ten): parses as a valid int
        // offset, so XrefParser accepts the row; it is still far past any small fixture's own
        // length, so CheckedOffset is what refuses it, not the parse itself.
        "0999999999"u8.ToArray().CopyTo(poisoned, rowStart);
        return poisoned;
    }

    [Fact]
    public void AnnotsArrayObjectPoisoned_reports509_pageContentImageStillReturned()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R /Annots 6 0 R >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])),
            new Obj(6, "[7 0 R]"),
            new Obj(7, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] >>"));

        var poisoned = PoisonObjectOffset(pdf, 6); // the /Annots array object itself

        using var reader = PdfReader.Open(poisoned);
        var result = reader.ExtractImages();

        Assert.Single(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.AnnotationAppearanceUnusable);
    }

    [Fact]
    public void AppearanceStreamResourcesObjectPoisoned_reports300_pageContentImageStillReturned()
    {
        // Object 8 (the appearance stream) itself resolves fine; its own /Resources names object
        // 9, whose offset is poisoned. RunFormXObject resolves /Resources inside its own try, AFTER
        // WalkAnnotationAppearances has already resolved object 8 as a stream successfully, so this
        // exercises RunFormXObject's own catch specifically, not the walker's.
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            new Obj(3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] "
                + "/Resources << /XObject << /Im0 10 0 R >> >> /Contents 4 0 R /Annots [7 0 R] >>"),
            new Obj(4, "<< >>", "/Im0 Do"u8.ToArray()),
            new Obj(10, "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])),
            new Obj(7, "<< /Type /Annot /Subtype /Stamp /Rect [0 0 1 1] /AP << /N 8 0 R >> >>"),
            new Obj(8, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /Resources 9 0 R >>", []),
            new Obj(9, "<< /Font << >> >>"));

        var poisoned = PoisonObjectOffset(pdf, 9); // the appearance stream's own /Resources

        using var reader = PdfReader.Open(poisoned);
        var result = reader.ExtractImages();

        Assert.Single(result.Images);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ContentStreamLexError);
    }

    // ── 17. Diagnostics identity ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Diagnostics_firstCall_sharesInstanceWithReaderDiagnostics_secondCall_doesNot()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /ImageMask true "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode >>",
            Flate([1]));

        using var reader = Open(pdf);

        var first = reader.ExtractImages();
        Assert.NotEmpty(first.Diagnostics);
        Assert.Contains(first.Diagnostics, d => ReferenceEquals(d, reader.Diagnostics.FirstOrDefault(rd => ReferenceEquals(rd, d))));
        Assert.Contains(reader.Diagnostics, rd => first.Diagnostics.Any(d => ReferenceEquals(d, rd)));

        var second = reader.ExtractImages();
        Assert.Contains(second.Diagnostics, d => !reader.Diagnostics.Any(rd => ReferenceEquals(rd, d)));
    }

    // ── 18. /Decode ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Decode_onImageMask_exposedButNeverApplied()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true /Decode [1 0] "
            + "/Filter /FlateDecode >>",
            Flate([0b10101010]));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.Equal([1.0, 0.0], extracted.Decode);
        // Never applied: the PNG samples are the stored bytes unchanged.
        Assert.True(extracted.TryEncodePng(out var png));
        var idat = InflateIdat(png!);
        Assert.Equal(new byte[] { 0, 0b10101010 }, idat); // filter byte + the stored row.
    }

    [Fact]
    public void Decode_wrongLength_reports502_nullExposed()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Decode [0 1 0] /Filter /FlateDecode >>",
            Flate([1]));

        using var reader = Open(pdf);
        var result = reader.ExtractImages();
        var extracted = Assert.Single(result.Images);

        Assert.Null(extracted.Decode);
        Assert.Contains(result.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDecodeArrayInvalid);
    }

    [Fact]
    public void Decode_twelveElementsOnSixColorantDeviceN_exposedIntact()
    {
        var decodeArray = string.Join(" ", Enumerable.Repeat("0 1", 6));
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace [/DeviceN [/C0 /C1 /C2 /C3 /C4 /C5] /DeviceGray 5 0 R] "
            + $"/Decode [{decodeArray}] /Filter /FlateDecode >>",
            Flate([1, 2, 3, 4, 5, 6]));

        using var reader = Open(pdf);
        var extracted = Assert.Single(reader.ExtractImages().Images);

        Assert.NotNull(extracted.Decode);
        Assert.Equal(12, extracted.Decode!.Count);
    }

    // ── 19. Committed fixture corpus (no images anywhere) ───────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, string> NonDefaultPasswords =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["enc-aes-128-emptyuser.pdf"] = "",
            ["enc-aes-128-tworevisions.pdf"] = "",
            ["enc-aes-128-longpassword.pdf"] = "0123456789abcdefghijklmnopqrstuvwxyzABCD",
            ["enc-aes-128-samepassword.pdf"] = "same",
            ["enc-aes-128-pdfdocpassword.pdf"] = "pässwörd",
        };

    /// <summary>
    /// No committed fixture under <c>Fixtures/Encrypted</c> or <c>Fixtures/ThirdParty</c> contains
    /// an image XObject (<c>grep -c "Subtype */Image"</c> over all of them returns 0; every
    /// <c>/Image*</c> hit is <c>/ProcSet [/PDF /Text /ImageB /ImageC /ImageI]</c>), so this asserts
    /// only the empty result: a per-image count assertion or a <c>pdfimages -list</c> comparison
    /// could never fire against this corpus.
    /// </summary>
    [Fact]
    public void CommittedFixtureCorpus_extractsNoImages_noException_no5xxDiagnostic()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)
                        && !n.StartsWith("Fuzz/", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(names);

        foreach (var name in names)
        {
            using var s = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            var bytes = ms.ToArray();

            var isThirdParty = name.StartsWith("ThirdParty/", StringComparison.Ordinal);
            var fileName = isThirdParty ? name["ThirdParty/".Length..] : name;
            var password = isThirdParty ? null : (NonDefaultPasswords.TryGetValue(fileName, out var p) ? p : "u");

            // AllowReconstruction: true so a damaged-on-purpose fixture (e.g. broken-startxref.pdf)
            // still opens here; reconstruction itself is exercised elsewhere, so this corpus scan
            // only asks whether ExtractImages ever throws or finds an image once the document is
            // open.
            using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = password, AllowReconstruction = true });
            var result = reader.ExtractImages();

            Assert.Empty(result.Images);
            Assert.DoesNotContain(result.Diagnostics, d => (int)d.Code is >= 500 and < 600);
        }
    }

    // ── 20. Exception contract ───────────────────────────────────────────────────────────────────

    [Fact]
    public void ExtractImages_nullOptions_throwsArgumentNull_onAllFourOverloads()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1]));
        using var reader = Open(pdf);
        var page = reader.GetPage(0);

        Assert.Throws<ArgumentNullException>(() => reader.ExtractImages(null!));
        Assert.Throws<ArgumentNullException>(() => page.ExtractImages(null!));
    }

    [Fact]
    public void ExtractImages_onDisposedReader_throwsObjectDisposed()
    {
        var pdf = BuildOnePageWithImage(
            "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
            + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1]));
        var reader = Open(pdf);
        var page = reader.GetPage(0);
        reader.Dispose();

        Assert.Throws<ObjectDisposedException>(() => reader.ExtractImages());
        Assert.Throws<ObjectDisposedException>(() => page.ExtractImages());
    }

    // ── Shared helpers ───────────────────────────────────────────────────────────────────────────

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

    private static byte[] FlateDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }

    private static uint BitConverterBigEndian(ReadOnlySpan<byte> b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static byte[] ExtractChunk(byte[] png, ReadOnlySpan<byte> type)
    {
        var offset = 8;
        while (offset < png.Length)
        {
            var length = (int)BitConverterBigEndian(png.AsSpan(offset, 4));
            var chunkType = png.AsSpan(offset + 4, 4);
            if (chunkType.SequenceEqual(type))
                return png.AsSpan(offset + 8, length).ToArray();
            offset += 4 + 4 + length + 4;
        }
        throw new InvalidOperationException("chunk not found");
    }

    private static byte[] InflateIdat(byte[] png)
    {
        var idat = ExtractChunk(png, "IDAT"u8);
        using var input = new MemoryStream(idat);
        using var z = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        z.CopyTo(output);
        return output.ToArray();
    }
}
