// Copyright 2026 Timothy van der Ham (@Tim81)
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
