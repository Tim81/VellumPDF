// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// Controls how bit depth is handled when loading a source image into a PDF image XObject.
/// </summary>
public enum ImageBitDepth
{
    /// <summary>
    /// Keep the source bit depth as-is. A 16-bit source is emitted as
    /// <c>BitsPerComponent 16</c> (lossless, larger file). A 1-bit source stays 1-bit.
    /// This is the default — quality is preferred over size.
    /// </summary>
    Preserve,

    /// <summary>
    /// Downsample the source to 8 bits per component before embedding.
    /// Reduces file size at the cost of precision for high-bit-depth sources.
    /// </summary>
    ReduceToEight,
}

/// <summary>
/// Controls how a compressed source codestream (CCITT, JBIG2, JPEG 2000) is mapped
/// into a PDF image XObject.
/// </summary>
public enum ImageDecodeMode
{
    /// <summary>
    /// Embed the source codestream verbatim using the matching PDF-native filter
    /// (<c>/CCITTFaxDecode</c>, <c>/JBIG2Decode</c>, <c>/JPXDecode</c>); the PDF viewer
    /// decodes it at render time. This is the default — it is lossless and preserves the
    /// original compression. Quality is preferred over in-process decoding.
    /// </summary>
    Passthrough,

    /// <summary>
    /// Decode the source codestream to raster pixels in-process and re-encode with
    /// <c>/FlateDecode</c>. Useful when the consumer needs a self-contained pixel image
    /// rather than relying on the viewer's codec support. May not be supported for every
    /// codec variant; unsupported variants throw <see cref="System.NotSupportedException"/>.
    /// </summary>
    DecodeToRaster,
}

/// <summary>
/// Options passed to image loader methods to control how the source image
/// is mapped into a PDF image XObject.
/// </summary>
public sealed record ImageLoadOptions
{
    /// <summary>
    /// Controls bit-depth handling. Defaults to <see cref="ImageBitDepth.Preserve"/>
    /// so that 16-bit sources are kept at <c>BitsPerComponent 16</c> by default.
    /// Set to <see cref="ImageBitDepth.ReduceToEight"/> to force 8-bit output.
    /// </summary>
    public ImageBitDepth BitDepth { get; init; } = ImageBitDepth.Preserve;

    /// <summary>
    /// Controls whether a compressed source codestream is passed through verbatim or
    /// decoded to raster. Defaults to <see cref="ImageDecodeMode.Passthrough"/> (lossless,
    /// viewer-decoded). Set to <see cref="ImageDecodeMode.DecodeToRaster"/> to decode
    /// in-process and re-encode as <c>/FlateDecode</c>.
    /// </summary>
    public ImageDecodeMode DecodeMode { get; init; } = ImageDecodeMode.Passthrough;

    /// <summary>The default options instance (bit depth = Preserve, decode mode = Passthrough).</summary>
    public static ImageLoadOptions Default { get; } = new();
}
