// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary> How <see cref="PdfExtractedImage.Data"/> is shaped (#98). <see cref="Raw"/> is decoded
/// samples, laid out per ISO 32000-2 §8.9.3; every other member is the stored, still-encoded
/// payload, exposed verbatim because this reader never transcodes an image it cannot losslessly
/// re-encode.
/// </summary>
public enum PdfImageEncoding
{
    /// <summary>Decoded samples (every filter this reader fully decodes: FlateDecode, LZWDecode,
    /// ASCIIHexDecode, ASCII85Decode, RunLengthDecode, or no filter at all).</summary>
    Raw = 0,

    /// <summary>The stored <c>DCTDecode</c> payload (ISO 32000-2 §7.4.8), a JPEG file.</summary>
    Jpeg = 1,

    /// <summary>The stored <c>JPXDecode</c> payload (ISO 32000-2 §7.4.9), a JPEG 2000 file or
    /// codestream; see <see cref="PdfExtractedImage.FileExtension"/> for which.</summary>
    Jpx = 2,

    /// <summary>The stored <c>JBIG2Decode</c> payload (ISO 32000-2 §7.4.7), an
    /// embedded-organisation JBIG2 segment sequence. ISO 32000-2 does not itself state
    /// whether a decoded JBIG2 bitmap's 1 bit means black or white; JBIG2 decoders conventionally
    /// deliver 1 as black, the opposite of a 1-bit <c>/DeviceGray</c> sample under the default
    /// <c>/Decode [0 1]</c>, so a caller that decodes this payload and paints the result as
    /// <c>/DeviceGray</c> samples must invert it.</summary>
    Jbig2 = 3,

    /// <summary>The stored <c>CCITTFaxDecode</c> payload (ISO 32000-2 §7.4.6), a Group 3 or Group 4
    /// fax-encoded bitstream.</summary>
    CcittFax = 4,
}
