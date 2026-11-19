// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Images;

/// <summary>
/// Options for <see cref="GifImageLoader.Load(byte[], GifLoadOptions)"/>.
/// </summary>
/// <remarks>
/// A record rather than a <see langword="bool"/> parameter, so a later option can be added without
/// a new overload.
/// </remarks>
public sealed record GifLoadOptions
{
    /// <summary>
    /// Whether the frame is placed on the logical screen (GIF89a sections 18 and 20) instead of
    /// being returned at its own size. Defaults to <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// <b>With the option set, a screen the frame does not fit is refused.</b>
    /// <see cref="GifImageLoader.Load(byte[], GifLoadOptions)"/> throws
    /// <see cref="InvalidDataException"/> for a screen with a width or height of 0, a frame that
    /// extends past the screen, and a screen of more than 100 million pixels.
    /// <para>The canvas is allocated at the screen's size, 4 bytes a pixel, so the option costs up
    /// to about 400 MB for a file of a few dozen bytes. Do not enable it for untrusted files
    /// without checking the screen size first.</para>
    /// <para>The image is as large as the screen, so a Layout document that places it changes
    /// size with it. A placed image too tall for a page makes the document's save throw
    /// <see cref="InvalidOperationException"/> (measured with a 40 x 2,000 screen, with and without
    /// an explicit width).</para>
    /// <para><b>Do not rely on the default being <see langword="false"/>.</b> Version 3.0 may
    /// place the frame by default.</para>
    /// </remarks>
    public bool PlaceOnLogicalScreen { get; init; }

    /// <summary>The default options: the frame is returned at its own size.</summary>
    public static GifLoadOptions Default { get; } = new();
}
