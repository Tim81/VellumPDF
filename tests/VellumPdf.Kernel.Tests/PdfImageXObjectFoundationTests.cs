// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Images;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Foundation tests for Step 0 + Step 1 of the v1.3 image-codec work:
/// ImageLoadOptions defaults, CCITTFaxDecode passthrough, and /DecodeParms emission.
/// </summary>
public sealed class PdfImageXObjectFoundationTests
{
    [Fact]
    public void ImageLoadOptions_Default_BitDepth_isPreserve()
    {
        Assert.Equal(ImageBitDepth.Preserve, ImageLoadOptions.Default.BitDepth);
    }

    [Fact]
    public void PdfImageXObject_CCITTFaxDecode_passthroughBytesAndDict()
    {
        // Arrange: synthetic "CCITT data" — any bytes will do, the loader passes them verbatim.
        byte[] fakeData = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        var decodeParms = new PdfDictionary()
            .Set(new PdfName("K"), new PdfInteger(-1))
            .Set(new PdfName("Columns"), new PdfInteger(8));

        var img = new PdfImageXObject(
            width: 8,
            height: 1,
            streamData: fakeData,
            filter: PdfName.CCITTFaxDecode,
            colorSpace: ImageColorSpace.DeviceGray,
            bitsPerComponent: 1,
            sMask: null,
            sMaskBitsPerComponent: 8,
            decodeParms: decodeParms);

        var stream = img.BuildStream();

        // Serialise to bytes so we can inspect what was written.
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        stream.WriteTo(writer);
        var raw = ms.ToArray();
        var text = System.Text.Encoding.Latin1.GetString(raw);

        // Dict must contain the filter and DecodeParms.
        Assert.Contains("/CCITTFaxDecode", text);
        Assert.Contains("/DecodeParms", text);
        Assert.Contains("/K", text);
        Assert.Contains("/Columns", text);
        Assert.Contains("/BitsPerComponent 1", text);

        // The raw stream body must be the verbatim input bytes (not Flate-recompressed).
        // RawPdfStream writes: dict + "\nstream\n" + raw + "\nendstream".
        var markerStart = FindSequence(raw, "\nstream\n"u8);
        Assert.True(markerStart >= 0, "stream marker not found");
        var bodyStart = markerStart + 8;
        var bodyEnd = FindSequence(raw, "\nendstream"u8);
        Assert.True(bodyEnd > bodyStart, "endstream marker not found");

        var body = raw[bodyStart..bodyEnd];
        Assert.Equal(fakeData, body);
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
