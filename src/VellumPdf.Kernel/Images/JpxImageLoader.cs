// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Creates a PDF Image XObject from JPEG 2000 data by passing the codestream
/// through verbatim as a <c>/JPXDecode</c> stream.
///
/// <para>Input may be either of the two standard JPEG 2000 container formats:</para>
/// <list type="bullet">
///   <item>
///     <b>JP2 box file</b> — begins with the 12-byte JP2 signature box
///     (<c>00 00 00 0C 6A 50 20 20 0D 0A 87 0A</c>). The box structure is walked
///     to extract geometry from the <c>jp2h</c>/<c>ihdr</c> box and colour space from
///     the optional <c>colr</c> box; the contiguous codestream (<c>jp2c</c>) box payload
///     is then embedded.
///   </item>
///   <item>
///     <b>Raw codestream</b> (<c>.j2k</c>/<c>.j2c</c>) — begins with the SOC marker
///     (<c>FF 4F</c>). The <c>SIZ</c> marker is scanned to extract width, height,
///     component count, and bit depth; the entire byte array is embedded verbatim.
///   </item>
/// </list>
///
/// <para>No JPEG 2000 decode-to-raster is performed in this release —
/// <see cref="ImageDecodeMode.DecodeToRaster"/> throws <see cref="NotSupportedException"/>.</para>
///
/// <para><b>Colour space inference</b> (Csiz/NC component count):</para>
/// <list type="table">
///   <item><term>1 component</term><description><c>DeviceGray</c></description></item>
///   <item><term>3 components</term><description><c>DeviceRGB</c> (refined to sRGB when the <c>colr</c> enumerated method = 16)</description></item>
///   <item><term>4 components</term><description><c>DeviceCMYK</c></description></item>
/// </list>
/// ICC and indexed colour spaces from JP2 <c>colr</c> boxes are not yet handled;
/// component-count inference is used as a fallback.
/// </summary>
public static class JpxImageLoader
{
    // JP2 signature box: length(4) type(4) magic(4) = 12 bytes total.
    // Type bytes = 6A 50 20 20 ("jP  "), magic = 0D 0A 87 0A.
    private static ReadOnlySpan<byte> Jp2SignatureBoxType => [0x6A, 0x50, 0x20, 0x20];
    private static ReadOnlySpan<byte> Jp2Magic => [0x0D, 0x0A, 0x87, 0x0A];

    // JP2 box type codes (big-endian uint32 = 4 ASCII chars).
    private const uint BoxTypeJp2h = 0x6A703268; // "jp2h"
    private const uint BoxTypeFtyp = 0x66747970; // "ftyp"
    private const uint BoxTypeIhdr = 0x69686472; // "ihdr"
    private const uint BoxTypeColr = 0x636F6C72; // "colr"
    private const uint BoxTypeJp2c = 0x6A703263; // "jp2c" (contiguous codestream)

    // JPEG 2000 SOC marker (start of codestream).
    private const ushort MarkerSOC = 0xFF4F;
    // JPEG 2000 SIZ marker (image and tile size).
    private const ushort MarkerSIZ = 0xFF51;

    // colr enumColourspace values (ISO 15444-1 Table I.11).
    private const int ColrEnumSRgb = 16;
    private const int ColrEnumGreyscale = 17;

    /// <summary>
    /// Loads JPEG 2000 data using default options (passthrough, no raster decode).
    /// </summary>
    /// <param name="jpx">
    /// The JPEG 2000 data — either a JP2 box file or a raw codestream (<c>.j2k</c>/<c>.j2c</c>).
    /// Must be non-null and non-empty.
    /// </param>
    /// <returns>A <see cref="PdfImageXObject"/> with <c>/Filter /JPXDecode</c>.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the data is malformed, truncated, or its dimensions exceed
    /// <see cref="ImageLimits.MaxPixels"/>.
    /// </exception>
    public static PdfImageXObject Load(byte[] jpx)
        => Load(jpx, ImageLoadOptions.Default);

    /// <summary>
    /// Loads JPEG 2000 data with explicit options.
    /// </summary>
    /// <param name="jpx">
    /// The JPEG 2000 data — either a JP2 box file or a raw codestream (<c>.j2k</c>/<c>.j2c</c>).
    /// Must be non-null and non-empty.
    /// </param>
    /// <param name="options">
    /// Controls decode behaviour. <see cref="ImageDecodeMode.Passthrough"/> (the default) embeds
    /// the codestream verbatim. <see cref="ImageDecodeMode.DecodeToRaster"/> is not yet implemented
    /// and throws <see cref="NotSupportedException"/>.
    /// </param>
    /// <returns>A <see cref="PdfImageXObject"/> with <c>/Filter /JPXDecode</c>.</returns>
    /// <exception cref="InvalidDataException">
    /// Thrown when the data is malformed, truncated, or its dimensions exceed
    /// <see cref="ImageLimits.MaxPixels"/>.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when <paramref name="options"/> requests <see cref="ImageDecodeMode.DecodeToRaster"/>.
    /// Full JPEG 2000 decode (DWT + EBCOT + MQ arithmetic) is not yet supported; use
    /// <see cref="ImageDecodeMode.Passthrough"/> instead.
    /// </exception>
    public static PdfImageXObject Load(byte[] jpx, ImageLoadOptions options)
    {
        ArgumentNullException.ThrowIfNull(jpx);
        if (jpx.Length == 0)
            throw new ArgumentException("JPEG 2000 data must be non-empty.", nameof(jpx));
        ArgumentNullException.ThrowIfNull(options);

        if (options.DecodeMode == ImageDecodeMode.DecodeToRaster)
            throw new NotSupportedException(
                "JPEG 2000 decode-to-raster is not yet supported; use Passthrough.");

        return IsJp2BoxFile(jpx) ? LoadJp2(jpx) : LoadRawCodestream(jpx);
    }

    // ── Format detection ──────────────────────────────────────────────────────

    private static bool IsJp2BoxFile(byte[] data)
    {
        // Minimum: 4 (length) + 4 (type) + 4 (magic) = 12 bytes.
        // Signature box: length=0x0000000C, type="jP  " (6A 50 20 20), magic=0D 0A 87 0A.
        if (data.Length < 12)
            return false;
        // Check box type (bytes 4–7).
        if (!data.AsSpan(4, 4).SequenceEqual(Jp2SignatureBoxType))
            return false;
        // Check magic (bytes 8–11).
        if (!data.AsSpan(8, 4).SequenceEqual(Jp2Magic))
            return false;
        return true;
    }

    // ── Raw codestream path ───────────────────────────────────────────────────

    private static PdfImageXObject LoadRawCodestream(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0xFF || data[1] != 0x4F)
            throw new InvalidDataException(
                "Not a JPEG 2000 file: missing SOC marker (FF4F) and not a JP2 box file.");

        var (width, height, components, bpc) = ParseSizFromCodestream(data, offset: 0);

        ImageLimits.ValidateDimensions("JPEG 2000", width, height);

        var cs = InferColorSpace(components, colrEnum: null);
        return new PdfImageXObject(width, height, data, PdfName.JPXDecode, cs, bpc);
    }

    // ── JP2 box file path ─────────────────────────────────────────────────────

    private static PdfImageXObject LoadJp2(byte[] data)
    {
        int width = 0, height = 0, nc = 0, bpc = 8;
        int? colrEnum = null;
        int jp2cOffset = -1;
        int jp2cLength = -1;

        var pos = 0;
        while (pos < data.Length)
        {
            var (boxType, boxPayloadOffset, boxPayloadLength, boxTotalLength) =
                ReadBox(data, pos);

            if (boxPayloadOffset < 0)
                break; // end-of-file sentinel (length=0 box consumed the rest)

            switch (boxType)
            {
                case BoxTypeJp2h:
                    ParseJp2Header(data, boxPayloadOffset, boxPayloadLength,
                        ref width, ref height, ref nc, ref bpc, ref colrEnum);
                    break;

                case BoxTypeJp2c:
                    jp2cOffset = boxPayloadOffset;
                    jp2cLength = boxPayloadLength;
                    break;
            }

            pos += boxTotalLength;
        }

        if (jp2cOffset < 0)
            throw new InvalidDataException("JP2 file contains no contiguous codestream (jp2c) box.");

        // If jp2h was missing or did not yield dimensions, parse from the codestream's SIZ.
        if (width == 0 || height == 0)
        {
            if (jp2cLength < 2 || data[jp2cOffset] != 0xFF || data[jp2cOffset + 1] != 0x4F)
                throw new InvalidDataException(
                    "jp2c codestream is missing the SOC marker (FF4F).");
            var (w, h, c, b) = ParseSizFromCodestream(data, jp2cOffset);
            width = w;
            height = h;
            nc = c;
            bpc = b;
        }

        ImageLimits.ValidateDimensions("JPEG 2000 (JP2)", width, height);

        // Extract the jp2c payload (the actual codestream bytes).
        var codestream = data[jp2cOffset..(jp2cOffset + jp2cLength)];
        var cs = InferColorSpace(nc, colrEnum);
        return new PdfImageXObject(width, height, codestream, PdfName.JPXDecode, cs, bpc);
    }

    // ── Box reading ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads one JP2 box header at <paramref name="pos"/> in <paramref name="data"/>.
    /// Returns (boxType, payloadOffset, payloadLength, totalBoxLength).
    /// When payloadOffset is -1 the caller must stop (EOF box consumed).
    /// </summary>
    private static (uint boxType, int boxPayloadOffset, int boxPayloadLength, int boxTotalLength)
        ReadBox(byte[] data, int pos)
    {
        // Every box: 4-byte length + 4-byte type = 8-byte header minimum.
        if (pos + 8 > data.Length)
            throw new InvalidDataException(
                $"JP2 box at offset {pos} is truncated (need 8 bytes for header).");

        var rawLength = ReadUInt32Be(data, pos);
        var boxType = ReadUInt32Be(data, pos + 4);

        long payloadOffset;
        long totalBoxLength;

        if (rawLength == 1)
        {
            // XLBox: 8-byte extended length follows the 8-byte header.
            if (pos + 16 > data.Length)
                throw new InvalidDataException(
                    $"JP2 box at offset {pos} uses XLBox but is truncated (need 16 bytes for header).");
            var xlBox = ReadUInt64Be(data, pos + 8);
            if (xlBox < 16)
                throw new InvalidDataException(
                    $"JP2 XLBox at offset {pos} contains invalid length {xlBox}.");
            totalBoxLength = (long)xlBox;
            payloadOffset = pos + 16;
        }
        else if (rawLength == 0)
        {
            // Length == 0: this box extends to the end of the file.
            totalBoxLength = data.Length - pos;
            payloadOffset = pos + 8;
        }
        else
        {
            if (rawLength < 8)
                throw new InvalidDataException(
                    $"JP2 box at offset {pos} has invalid length {rawLength} (< 8).");
            totalBoxLength = rawLength;
            payloadOffset = pos + 8;
        }

        var payloadLength = totalBoxLength - (payloadOffset - pos);

        // Bounds-check: box must not extend beyond the buffer.
        if (payloadOffset + payloadLength > data.Length)
            throw new InvalidDataException(
                $"JP2 box at offset {pos} (type 0x{boxType:X8}) claims length {totalBoxLength}" +
                $" which exceeds the buffer ({data.Length} bytes).");

        if (payloadLength < 0)
            throw new InvalidDataException(
                $"JP2 box at offset {pos} has a negative computed payload length.");

        if (rawLength == 0)
        {
            // The box consumed the rest of the file; signal EOF to the caller.
            return (boxType, (int)payloadOffset, (int)payloadLength, (int)totalBoxLength);
        }

        return (boxType, (int)payloadOffset, (int)payloadLength, (int)totalBoxLength);
    }

    // ── JP2 header (jp2h) superbox ────────────────────────────────────────────

    private static void ParseJp2Header(
        byte[] data, int offset, int length,
        ref int width, ref int height, ref int nc, ref int bpc, ref int? colrEnum)
    {
        var end = offset + length;
        var pos = offset;

        while (pos < end)
        {
            if (pos + 8 > end)
                break; // truncated sub-box — stop gracefully

            var rawLength = ReadUInt32Be(data, pos);
            var subType = ReadUInt32Be(data, pos + 4);

            long subTotal;
            int subPayloadOffset;

            if (rawLength == 1)
            {
                if (pos + 16 > end)
                    throw new InvalidDataException("JP2 jp2h sub-box uses XLBox but is truncated.");
                var xl = ReadUInt64Be(data, pos + 8);
                if (xl < 16)
                    throw new InvalidDataException("JP2 jp2h sub-box XLBox value is invalid.");
                subTotal = (long)xl;
                subPayloadOffset = pos + 16;
            }
            else if (rawLength == 0)
            {
                subTotal = end - pos;
                subPayloadOffset = pos + 8;
            }
            else
            {
                if (rawLength < 8)
                    throw new InvalidDataException(
                        $"JP2 jp2h sub-box at offset {pos} has invalid length {rawLength}.");
                subTotal = rawLength;
                subPayloadOffset = pos + 8;
            }

            var subPayloadLength = (int)(subTotal - (subPayloadOffset - pos));
            if (subPayloadOffset + subPayloadLength > data.Length)
                throw new InvalidDataException(
                    $"JP2 jp2h sub-box at offset {pos} (type 0x{subType:X8}) exceeds buffer.");

            switch (subType)
            {
                case BoxTypeIhdr:
                    ParseIhdr(data, subPayloadOffset, subPayloadLength,
                        ref height, ref width, ref nc, ref bpc);
                    break;

                case BoxTypeColr:
                    TryParseColr(data, subPayloadOffset, subPayloadLength, ref colrEnum);
                    break;
            }

            pos += (int)subTotal;
        }
    }

    // ihdr payload: Height(4) Width(4) NC(2) BPC(1) C(1) UnkC(1) IPR(1) = 14 bytes minimum.
    private static void ParseIhdr(
        byte[] data, int offset, int length,
        ref int height, ref int width, ref int nc, ref int bpc)
    {
        if (length < 14)
            throw new InvalidDataException(
                $"JP2 ihdr box payload is too short ({length} bytes; need 14).");

        height = (int)ReadUInt32Be(data, offset);
        width = (int)ReadUInt32Be(data, offset + 4);
        nc = ReadUInt16Be(data, offset + 8);
        var bpcRaw = data[offset + 10];
        // BPC = 0xFF means components have different bit depths; treat as 8.
        bpc = bpcRaw == 0xFF ? 8 : (bpcRaw & 0x7F) + 1;
    }

    // colr payload: METH(1) PREC(1) APPROX(1) [EnumCS(4) if METH=1] [ICCProfile if METH=2].
    private static void TryParseColr(byte[] data, int offset, int length, ref int? colrEnum)
    {
        if (length < 3)
            return; // malformed but non-fatal for colour space purposes

        var meth = data[offset];
        if (meth == 1 && length >= 7)
        {
            // Enumerated colour space method: 4-byte enum value follows METH/PREC/APPROX.
            colrEnum = (int)ReadUInt32Be(data, offset + 3);
        }
        // METH=2 (ICC profile) and METH=3 (any-ICC/palette) are not yet handled;
        // component-count inference is used as a fallback in those cases.
    }

    // ── SIZ marker parsing ────────────────────────────────────────────────────

    /// <summary>
    /// Scans forward from <paramref name="offset"/> (which must point at an SOC marker FF4F)
    /// to find the SIZ marker (FF51) and parses width, height, component count, and bit depth.
    /// </summary>
    private static (int width, int height, int components, int bpc)
        ParseSizFromCodestream(byte[] data, int offset)
    {
        // SIZ must immediately follow SOC in a valid JPEG 2000 main header.
        // We scan the main header markers (allowing optional SOT before SIZ won't occur in
        // well-formed streams but we stop at any tile-part or EOC).
        var pos = offset + 2; // skip SOC
        while (pos + 1 < data.Length)
        {
            if (data[pos] != 0xFF)
                throw new InvalidDataException(
                    $"JPEG 2000 codestream: expected marker (0xFF) at offset {pos}, got 0x{data[pos]:X2}.");

            var marker = (ushort)((data[pos] << 8) | data[pos + 1]);
            pos += 2;

            if (marker == MarkerSIZ)
                return ParseSiz(data, pos);

            // Every main-header marker (except SOC and EOC) carries a 2-byte segment length.
            if (pos + 1 >= data.Length)
                throw new InvalidDataException(
                    "JPEG 2000 codestream is truncated reading marker segment length.");

            var segLen = ReadUInt16Be(data, pos);
            if (segLen < 2)
                throw new InvalidDataException(
                    $"JPEG 2000 codestream: invalid marker segment length {segLen} at offset {pos}.");

            pos += segLen;
        }

        throw new InvalidDataException("JPEG 2000 codestream: SIZ marker not found.");
    }

    /// <summary>
    /// Parses the SIZ marker segment payload at <paramref name="pos"/>
    /// (i.e. <paramref name="pos"/> points to the 2-byte Lsiz length field).
    /// </summary>
    private static (int width, int height, int components, int bpc) ParseSiz(byte[] data, int pos)
    {
        // SIZ payload layout (ISO 15444-1 §A.5.1):
        //   Lsiz(2) Rsiz(2) Xsiz(4) Ysiz(4) XOsiz(4) YOsiz(4) XTsiz(4) YTsiz(4)
        //   XTOsiz(4) YTOsiz(4) Csiz(2) [per-component Ssiz(1) XRsiz(1) YRsiz(1)] ...
        // Minimum bytes needed to read through Csiz: 2+2+4+4+4+4+4+4+4+4+2 = 38 bytes.
        const int MinSizLength = 38;

        if (pos + 1 >= data.Length)
            throw new InvalidDataException(
                "JPEG 2000 SIZ marker: truncated (cannot read Lsiz).");

        var lsiz = ReadUInt16Be(data, pos);
        if (lsiz < MinSizLength)
            throw new InvalidDataException(
                $"JPEG 2000 SIZ marker: Lsiz={lsiz} is below the minimum {MinSizLength}.");

        // Bound the segment against the buffer.
        if (pos + lsiz > data.Length)
            throw new InvalidDataException(
                $"JPEG 2000 SIZ marker segment claims Lsiz={lsiz} but only {data.Length - pos} bytes remain.");

        // Field offsets relative to pos (the Lsiz field):
        //   +0 Lsiz(2), +2 Rsiz(2), +4 Xsiz(4), +8 Ysiz(4), +12 XOsiz(4), +16 YOsiz(4)
        //   +20 XTsiz(4), +24 YTsiz(4), +28 XTOsiz(4), +32 YTOsiz(4), +36 Csiz(2)
        var xsiz = (int)ReadUInt32Be(data, pos + 4);
        var ysiz = (int)ReadUInt32Be(data, pos + 8);
        var xosiz = (int)ReadUInt32Be(data, pos + 12);
        var yosiz = (int)ReadUInt32Be(data, pos + 16);
        var csiz = ReadUInt16Be(data, pos + 36);

        var width = xsiz - xosiz;
        var height = ysiz - yosiz;

        if (width <= 0 || height <= 0)
            throw new InvalidDataException(
                $"JPEG 2000 SIZ: computed dimensions {width}×{height} are invalid " +
                $"(Xsiz={xsiz}, XOsiz={xosiz}, Ysiz={ysiz}, YOsiz={yosiz}).");

        if (csiz < 1 || csiz > 16384)
            throw new InvalidDataException(
                $"JPEG 2000 SIZ: Csiz={csiz} is out of range.");

        // Per-component Ssiz: 1 byte each at pos+38, pos+41, … (+3 per component: Ssiz YRsiz YRsiz).
        // We only need the first component's Ssiz for the BitsPerComponent value.
        var ssizOffset = pos + 38;
        if (ssizOffset >= data.Length)
            throw new InvalidDataException(
                "JPEG 2000 SIZ: truncated before first component descriptor.");

        var ssiz0 = data[ssizOffset];
        // Ssiz high bit = 1 indicates signed component; bit depth = (Ssiz & 0x7F) + 1.
        var bpc = (ssiz0 & 0x7F) + 1;

        return (width, height, csiz, bpc);
    }

    // ── Colour space inference ────────────────────────────────────────────────

    private static ImageColorSpace InferColorSpace(int components, int? colrEnum)
    {
        // Prefer enumerated colour space from the colr box when available.
        if (colrEnum.HasValue)
        {
            return colrEnum.Value switch
            {
                ColrEnumSRgb => ImageColorSpace.DeviceRgb,
                ColrEnumGreyscale => ImageColorSpace.DeviceGray,
                _ => FallbackFromComponents(components),
            };
        }

        return FallbackFromComponents(components);
    }

    private static ImageColorSpace FallbackFromComponents(int components) => components switch
    {
        1 => ImageColorSpace.DeviceGray,
        4 => ImageColorSpace.DeviceCmyk,
        _ => ImageColorSpace.DeviceRgb,
    };

    // ── Binary helpers ────────────────────────────────────────────────────────

    private static uint ReadUInt32Be(byte[] data, int offset)
        => ((uint)data[offset] << 24) |
           ((uint)data[offset + 1] << 16) |
           ((uint)data[offset + 2] << 8) |
           data[offset + 3];

    private static ulong ReadUInt64Be(byte[] data, int offset)
        => ((ulong)data[offset] << 56) |
           ((ulong)data[offset + 1] << 48) |
           ((ulong)data[offset + 2] << 40) |
           ((ulong)data[offset + 3] << 32) |
           ((ulong)data[offset + 4] << 24) |
           ((ulong)data[offset + 5] << 16) |
           ((ulong)data[offset + 6] << 8) |
           data[offset + 7];

    private static ushort ReadUInt16Be(byte[] data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);
}
