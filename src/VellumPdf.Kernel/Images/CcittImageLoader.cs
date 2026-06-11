// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Images;

/// <summary>
/// Creates a PDF Image XObject from raw CCITT Group 3 or Group 4 compressed bytes by
/// passing them through verbatim as a <c>/CCITTFaxDecode</c> stream.
///
/// No CCITT decoding is performed — the bytes are embedded exactly as supplied.
/// CCITTFaxDecode is a PDF-native filter; the PDF viewer decodes the stream at render time.
///
/// <para><b>K parameter semantics (ISO 32000-2 Table 10):</b></para>
/// <list type="bullet">
///   <item><c>k &lt; 0</c> — Group 4 (T.6), pure 2D MMR. Most CCITT fax TIFFs use this.</item>
///   <item><c>k = 0</c> — Group 3, 1D encoding (T.4 without 2D rows).</item>
///   <item><c>k &gt; 0</c> — Group 3, mixed 1D/2D (T.4 with at most k−1 2D rows between 1D rows).</item>
/// </list>
///
/// <para><b>Columns and Rows</b> are required; they tell the decoder the image geometry.</para>
///
/// <para><b>BlackIs1</b> controls polarity: when <see langword="false"/> (the PDF default)
/// bit value 0 = black and 1 = white, which is the standard fax convention.
/// Pass <see langword="true"/> only when the source data uses the opposite convention.</para>
/// </summary>
public static class CcittImageLoader
{
    /// <summary>
    /// Wraps raw CCITT compressed bytes as a <c>/CCITTFaxDecode</c> Image XObject.
    /// </summary>
    /// <param name="ccittData">The raw CCITT-compressed bytes. Must be non-empty.</param>
    /// <param name="columns">Image width in pixels. Must be positive.</param>
    /// <param name="rows">Image height in pixels. Must be positive.</param>
    /// <param name="k">
    /// The K value for /DecodeParms: negative = Group 4 (T.6 MMR), 0 = Group 3 1D, positive = Group 3 mixed.
    /// Defaults to -1 (Group 4).
    /// </param>
    /// <param name="blackIs1">
    /// When <see langword="true"/>, emits <c>/BlackIs1 true</c> in /DecodeParms (bit 1 = black).
    /// Omitted when <see langword="false"/> (the PDF default, bit 0 = black).
    /// </param>
    /// <param name="encodedByteAlign">
    /// When <see langword="true"/>, emits <c>/EncodedByteAlign true</c> in /DecodeParms (each row
    /// is padded to a byte boundary). Omitted when <see langword="false"/> (the PDF default).
    /// </param>
    /// <returns>A <see cref="PdfImageXObject"/> with /Filter /CCITTFaxDecode and the correct /DecodeParms.</returns>
    public static PdfImageXObject Load(
        byte[] ccittData,
        int columns,
        int rows,
        int k = -1,
        bool blackIs1 = false,
        bool encodedByteAlign = false)
    {
        if (ccittData is null || ccittData.Length == 0)
            throw new ArgumentException("CCITT data must be non-empty.", nameof(ccittData));
        if (columns <= 0)
            throw new ArgumentOutOfRangeException(nameof(columns), "Columns must be positive.");
        if (rows <= 0)
            throw new ArgumentOutOfRangeException(nameof(rows), "Rows must be positive.");

        ImageLimits.ValidateDimensions("CCITT", columns, rows);

        return Build(ccittData, columns, rows, k, blackIs1, encodedByteAlign);
    }

    /// <summary>
    /// Core builder shared by <see cref="Load"/> and <see cref="TiffImageLoader"/>.
    /// Callers are responsible for validation before calling this method.
    /// </summary>
    internal static PdfImageXObject Build(
        byte[] data,
        int columns,
        int rows,
        int k,
        bool blackIs1,
        bool encodedByteAlign)
    {
        var dp = new PdfDictionary()
            .Set(new PdfName("K"), new PdfInteger(k))
            .Set(new PdfName("Columns"), new PdfInteger(columns))
            .Set(new PdfName("Rows"), new PdfInteger(rows));

        if (blackIs1)
            dp.Set(new PdfName("BlackIs1"), PdfBoolean.True);

        if (encodedByteAlign)
            dp.Set(new PdfName("EncodedByteAlign"), PdfBoolean.True);

        return new PdfImageXObject(
            columns, rows, data,
            PdfName.CCITTFaxDecode,
            ImageColorSpace.DeviceGray,
            bitsPerComponent: 1,
            decodeParms: dp);
    }
}
