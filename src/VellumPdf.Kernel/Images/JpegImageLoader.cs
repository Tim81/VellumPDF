// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Creates a PDF Image XObject by passing raw JPEG bytes through as DCTDecode data.
/// No JPEG decoding is performed — the bytes are embedded verbatim.
/// </summary>
public static class JpegImageLoader
{
    /// <summary>
    /// Reads JPEG markers to extract width, height, and component count,
    /// then wraps the raw bytes as a DCTDecode Image XObject.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// The bytes do not begin with the SOI marker, or no frame header can be found.
    /// </exception>
    /// <remarks>
    /// This loader decodes nothing. The bytes are embedded verbatim as DCTDecode data and only
    /// the frame header is read. A file this accepts is therefore not necessarily a file a reader
    /// can render: corruption past the header passes straight into the document.
    /// <para>Attention: this is the one loader that does <b>not</b> check the declared size, and
    /// the difference is large. A 25-byte file whose frame header declares 65535 by 65535 returns
    /// an image of 4,294,836,225 pixels. Every other loader refuses anything above 100,000,000.
    /// Nothing allocates a raster here, since the bytes pass through, but the width and height
    /// reach the image dictionary and a consumer that trusts them can be made to allocate from
    /// them. If the input is untrusted, check the dimensions yourself.</para>
    /// <para><see langword="null"/> is not checked either. It raises
    /// <see cref="NullReferenceException"/>, not <see cref="ArgumentNullException"/>.</para>
    /// </remarks>
    public static PdfImageXObject Load(byte[] jpegBytes)
    {
        var (width, height, components) = ReadSof(jpegBytes);
        var cs = components switch
        {
            1 => ImageColorSpace.DeviceGray,
            4 => ImageColorSpace.DeviceCmyk,
            _ => ImageColorSpace.DeviceRgb,
        };
        return new PdfImageXObject(width, height, jpegBytes, PdfName.DCTDecode, cs, 8);
    }

    private static (int width, int height, int components) ReadSof(byte[] data)
    {
        // Validate the SOI (Start Of Image) marker before scanning.
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0xD8)
            throw new InvalidDataException("Not a JPEG file (missing FFD8 SOI marker).");

        var i = 2; // past SOI
        while (i + 1 < data.Length)
        {
            if (data[i] != 0xFF) break;
            var marker = data[i + 1];
            i += 2;
            if (marker == 0xD9) break; // EOI

            // Every marker segment except SOI/EOI carries a 2-byte length (ITU-T T.81 §B.1.1.4).
            // Table B.1 marks SOI and EOI with an asterisk because each "stands alone, that is,
            // ... is not the start of a marker segment" (§B.1.1.3) — neither has one to read.
            if (i + 1 >= data.Length) break;
            var length = (data[i] << 8) | data[i + 1];
            if (length < 2)
                throw new InvalidDataException("Malformed JPEG: invalid marker segment length.");

            // SOF markers: C0-C3, C5-C7, C9-CB, CD-CF. Table B.1 assigns the gaps to other
            // marker segments, not to frame headers: C4 is DHT (Define Huffman table), C8 is
            // JPG (reserved for JPEG extensions), CC is DAC (Define arithmetic coding
            // conditioning).
            if ((marker >= 0xC0 && marker <= 0xC3) ||
                (marker >= 0xC5 && marker <= 0xC7) ||
                (marker >= 0xC9 && marker <= 0xCB) ||
                (marker >= 0xCD && marker <= 0xCF))
            {
                // SOF payload (relative to the length field): precision(+2), height(+3..+4),
                // width(+5..+6), components(+7) — the P/Y/X/Nf field order after Lf in the
                // frame header syntax of Figure B.3 (ITU-T T.81 §B.2.2). Bound the read before
                // touching those bytes.
                if (i + 7 >= data.Length)
                    throw new InvalidDataException("Malformed JPEG: truncated SOF segment.");
                var h = (data[i + 3] << 8) | data[i + 4];
                var w = (data[i + 5] << 8) | data[i + 6];
                var c = data[i + 7];
                return (w, h, c);
            }
            i += length;
        }
        throw new InvalidDataException("Could not find SOF marker in JPEG data.");
    }
}
