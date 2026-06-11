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

    /// <summary>The default options instance (bit depth = Preserve).</summary>
    public static ImageLoadOptions Default { get; } = new();
}
