// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Text;
using VellumPdf.Core;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// <see cref="ImageDecoder"/> (#98): Table 87's rules dictionary-shape by dictionary-shape,
/// <c>ExpectedSampleDataLength</c> arithmetic, and the per-call image cache.
/// </summary>
public sealed class ImageDecoderTests
{
    private sealed record Obj(int Num, string Dict, byte[]? Stream = null);

    private static byte[] BuildPdf(int rootObjectNumber, params Obj[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));
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

    private static byte[] Flate(byte[] raw)
    {
        var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            z.Write(raw);
        return ms.ToArray();
    }

    private static (PdfDocumentReader Reader, ImageDecoder Decoder, DiagnosticSink Sink) Setup(
        byte[] pdfBytes)
    {
        var reader = PdfReader.Open(pdfBytes);
        var sink = new DiagnosticSink(cap: 100);
        var decoder = new ImageDecoder(reader, reader.Limits, sink);
        return (reader, decoder, sink);
    }

    private static ParsedStream Image(PdfDocumentReader reader, int objectNumber) =>
        reader.ResolveStream(objectNumber) ?? throw new InvalidOperationException("not a stream");

    // ── Table 87 dictionary-shape rules ─────────────────────────────────────────────────────────

    [Fact]
    public void Width_asReal_reportsInvalid_skipsImage()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1.5 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.Null(image);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    [Fact]
    public void Height_zero_reportsInvalid_skipsImage()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 0 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.Null(image);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    [Fact]
    public void BitsPerComponent_threeIsInvalid_skipsImage()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 3 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.Null(image);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    [Fact]
    public void BitsPerComponent_absentOnFlateImage_reportsInvalid_skipsImage()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.Null(image);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    [Fact]
    public void BitsPerComponent_absentOnJpxImage_isZero_noDiagnostic()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /Filter /JPXDecode >>",
                [0, 0, 0, 0x0C, 0x6A, 0x50, 0x20, 0x20, 0x0D, 0x0A, 0x87, 0x0A]));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.Equal(0, image!.BitsPerComponent);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    [Fact]
    public void ImageMask_withColorSpace_reportsInvalid_ignoresColorSpace_keepsMask()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([0xFF])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.True(image!.IsStencilMask);
        Assert.Null(image.ColorSpace);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    [Fact]
    public void ImageMask_withBitsPerComponentEight_forcedToOne()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /ImageMask true "
                + "/BitsPerComponent 8 /Filter /FlateDecode >>", Flate([0xFF])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.Equal(1, image!.BitsPerComponent);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    [Fact]
    public void RunLengthDecode_lastFilter_bitsPerComponentFour_forcedToEight()
    {
        // §8.9.5 Table 87: RunLengthDecode always delivers 8-bit samples, even when the dictionary
        // (wrongly) declares 4. RunLengthDecode's own byte-for-byte literal-run encoding of a
        // single byte is [0x00, value] (a length byte of 0 means "copy the next 1 byte").
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 4 "
                + "/ColorSpace /DeviceGray /Filter /RunLengthDecode >>", [0x00, 0x2A]));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.Equal(8, image!.BitsPerComponent);
        Assert.Equal(PdfImageEncoding.Raw, image.Encoding);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageBitsPerComponentOverridden);
    }

    [Fact]
    public void DecodeParmsBitsPerComponent_disagreesWithDictionary_reportsInvalid_dictionaryWins()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 8 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode "
                + "/DecodeParms << /Predictor 2 /Columns 8 /Colors 1 /BitsPerComponent 4 >> >>",
                Flate([1])));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.Equal(8, image!.BitsPerComponent);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageDictionaryInvalid);
    }

    // ── ExpectedSampleDataLength arithmetic ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, 1, 1, 1)]
    [InlineData(7, 1, 1, 1)]
    [InlineData(8, 1, 1, 1)]
    [InlineData(9, 1, 1, 2)]
    [InlineData(1, 1, 8, 1)]
    [InlineData(7, 1, 8, 7)]
    [InlineData(1, 3, 8, 3)]
    [InlineData(3, 4, 16, 24)] // 3 samples * 4 components * 16 bits = 384 bits = 24 bytes.
    public void ExpectedSampleDataLength_matchesRowBytesTimesHeight(
        int width, int components, int bpc, int expectedRowBytes)
    {
        var colorSpaceName = components switch { 1 => "/DeviceGray", 3 => "/DeviceRGB", _ => "/DeviceCMYK" };
        var sampleBytes = new byte[expectedRowBytes]; // one row; height 1
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                $"<< /Type /XObject /Subtype /Image /Width {width} /Height 1 /BitsPerComponent {bpc} "
                + $"/ColorSpace {colorSpaceName} /Filter /FlateDecode >>", Flate(sampleBytes)));
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.Equal(expectedRowBytes, image!.Data.Length);
        Assert.DoesNotContain(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageSampleDataShort);
    }

    [Fact]
    public void ShortBuffer_reports504_dataLengthUnchanged_noPadding()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 4 /Height 4 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([1, 2, 3]))); // needs 16 bytes
        var (reader, decoder, sink) = Setup(pdf);

        var image = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(image);
        Assert.Equal(3, image!.Data.Length);
        Assert.False(image.CanEncodePng);
        Assert.Single(sink.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.ImageSampleDataShort);
    }

    // ── Caches ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SameXObjectDrawnTwice_decodesOnce_sharesDataInstance()
    {
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([0x42])));
        var (reader, decoder, sink) = Setup(pdf);

        var first = decoder.Decode(Image(reader, 10), null, 0, sink);
        var second = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(first);
        Assert.NotNull(second);
        // The per-call image cache returns the SAME instance on a second decode of the same
        // (objectNumber, generation, role), so the two occurrences share one Data buffer.
        Assert.Same(first, second);
    }

    [Fact]
    public void SameStream_asDrawnImageAndAsSMask_producesDistinctInstances()
    {
        // Object 11 is both object 10's own /SMask and, independently, drawn directly: the cache
        // key's role component must keep the two decodes from colliding.
        var pdf = BuildPdf(1,
            new Obj(1, "<< /Type /Catalog /Pages 2 0 R >>"),
            new Obj(2, "<< /Type /Pages /Kids [] /Count 0 >>"),
            new Obj(10,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode /SMask 11 0 R >>", Flate([0x42])),
            new Obj(11,
                "<< /Type /XObject /Subtype /Image /Width 1 /Height 1 /BitsPerComponent 8 "
                + "/ColorSpace /DeviceGray /Filter /FlateDecode >>", Flate([0x7F])));
        var (reader, decoder, sink) = Setup(pdf);

        var asDrawnImage = decoder.Decode(Image(reader, 11), null, 0, sink);
        var parent = decoder.Decode(Image(reader, 10), null, 0, sink);

        Assert.NotNull(asDrawnImage);
        Assert.NotNull(parent);
        Assert.NotNull(parent!.SoftMask);
        Assert.NotSame(asDrawnImage, parent.SoftMask);
        Assert.False(asDrawnImage!.IsSoftMask);
        Assert.True(parent.SoftMask!.IsSoftMask);
        // Same underlying bytes, decoded independently under each role.
        Assert.True(asDrawnImage.Data.Span.SequenceEqual(parent.SoftMask.Data.Span));
    }
}
