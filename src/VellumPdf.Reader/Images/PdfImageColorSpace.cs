// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary> One image's colour space (ISO 32000-2 §8.6), as much of it as <see
/// cref="ColorSpaceReader"/> exposes (#98). <see cref="Base"/>, <see cref="HighValue"/>, and <see
/// cref="Lookup"/> apply only to <see cref="PdfImageColorSpaceFamily.Indexed"/>; <see
/// cref="IccProfile"/> only to <see cref="PdfImageColorSpaceFamily.IccBased"/>. Colorants, tint
/// transforms, Lab ranges and white points, and CalGray/CalRgb gamma are not exposed in 2.4: a
/// colorant list without its tint transform, or a Lab range without its white point, is half a
/// description, and each can be added beside the consumer that turns out to need it.
/// </summary>
public sealed class PdfImageColorSpace
{
    /// <summary>Which colour-space family this is.</summary>
    public PdfImageColorSpaceFamily Family { get; }

    /// <summary> The number of colour components a sample carries in this space: 1 for DeviceGray,
    /// CalGray, Indexed, and Separation; 3 for DeviceRgb, CalRgb, and Lab; 4 for DeviceCmyk; the
    /// ICC profile's own <c>/N</c> for IccBased; and the colourant-name array's length for DeviceN,
    /// 1 to 64 (this reader's own cap; ISO 32000-2 §8.6.6.5 allows an arbitrary number).
    /// </summary>
    public int ComponentCount { get; }

    /// <summary>
    /// The base colour space an <see cref="PdfImageColorSpaceFamily.Indexed"/> space's lookup table
    /// indexes into (ISO 32000-2 §8.6.6.3), or <see langword="null"/> for every other family.
    /// </summary>
    public PdfImageColorSpace? Base { get; }

    /// <summary>
    /// <c>hival</c>, the largest valid index into an <see cref="PdfImageColorSpaceFamily.Indexed"/>
    /// space's <see cref="Lookup"/> table (ISO 32000-2 §8.6.6.3: 0 to 255), or 0 for every other
    /// family.
    /// </summary>
    public int HighValue { get; }

    /// <summary> An <see cref="PdfImageColorSpaceFamily.Indexed"/> space's lookup table: exactly
    /// <c>(HighValue + 1) * Base.ComponentCount</c> bytes, each an unsigned integer scaled into
    /// <see cref="Base"/>'s own component range (ISO 32000-2 §8.6.6.3). Empty for every other
    /// family.
    /// </summary>
    public ReadOnlyMemory<byte> Lookup { get; }

    /// <summary>
    /// An <see cref="PdfImageColorSpaceFamily.IccBased"/> space's decoded ICC profile stream bytes
    /// (ISO 32000-2 §8.6.5.5). Empty for every other family, and also empty for an IccBased space
    /// itself when the profile stream failed to decode or was refused by the decode budget, with no
    /// diagnostic of its own for either case.
    /// </summary>
    public ReadOnlyMemory<byte> IccProfile { get; }

    internal PdfImageColorSpace(
        PdfImageColorSpaceFamily family, int componentCount, PdfImageColorSpace? @base = null,
        int highValue = 0, ReadOnlyMemory<byte> lookup = default, ReadOnlyMemory<byte> iccProfile = default)
    {
        Family = family;
        ComponentCount = componentCount;
        Base = @base;
        HighValue = highValue;
        Lookup = lookup;
        IccProfile = iccProfile;
    }
}
