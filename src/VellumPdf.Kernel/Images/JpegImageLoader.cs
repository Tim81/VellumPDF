// Copyright 2026 Timothy van der Ham (@Tim81)
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

            // Every marker segment except SOI/EOI carries a 2-byte length.
            if (i + 1 >= data.Length) break;
            var length = (data[i] << 8) | data[i + 1];
            if (length < 2)
                throw new InvalidDataException("Malformed JPEG: invalid marker segment length.");

            // SOF markers: C0-C3, C5-C7, C9-CB, CD-CF
            if ((marker >= 0xC0 && marker <= 0xC3) ||
                (marker >= 0xC5 && marker <= 0xC7) ||
                (marker >= 0xC9 && marker <= 0xCB) ||
                (marker >= 0xCD && marker <= 0xCF))
            {
                // SOF payload (relative to the length field): precision(+2), height(+3..+4),
                // width(+5..+6), components(+7). Bound the read before touching those bytes.
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
