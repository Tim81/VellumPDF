// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// Shared safety limits applied by the image loaders so malformed or hostile input is
/// rejected with a clean <see cref="InvalidDataException"/> before it can drive an
/// out-of-memory allocation.
/// </summary>
internal static class ImageLimits
{
    /// <summary>Maximum decoded pixel count (width × height) any loader will accept.</summary>
    public const long MaxPixels = 100_000_000L;

    /// <summary>
    /// Maximum width or height accepted by the in-process raster decoders. The
    /// <see cref="MaxPixels"/> product limit alone permits an extreme aspect ratio
    /// (e.g. 100M×1), and a 2D decoder that keeps per-row scratch proportional to the width
    /// would then allocate hundreds of megabytes from a few bytes of input. Bounding each
    /// dimension keeps decode scratch comparable to the decoded raster. Far above any real
    /// image dimension (a million-pixel edge is tens of metres at print resolution).
    /// </summary>
    public const int MaxRasterDecodeDimension = 1_000_000;

    /// <summary>
    /// Validates image dimensions, rejecting non-positive values and pixel counts beyond
    /// <see cref="MaxPixels"/>. The <paramref name="format"/> label is used in the message.
    /// </summary>
    public static void ValidateDimensions(string format, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"{format} dimensions {width}×{height} are invalid.");
        if ((long)width * height > MaxPixels)
            throw new InvalidDataException(
                $"{format} dimensions {width}×{height} exceed the 100M pixel safety limit.");
    }
}
