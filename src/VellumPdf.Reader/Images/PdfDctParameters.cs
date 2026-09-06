// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary>
/// The DCT (JPEG) parameters an extracted image's <c>/DecodeParms</c> carries (ISO 32000-2 §7.4.8,
/// Table 13).
/// </summary>
public sealed class PdfDctParameters
{
    /// <summary>
    /// The <c>/DecodeParms /ColorTransform</c> value (0 or 1; any other value is treated as absent
    /// and reported through <see cref="PdfReaderDiagnosticCode.ImageDictionaryInvalid"/>), or
    /// <see langword="null"/> when the entry is absent. This is not the whole answer: an Adobe
    /// APP14 marker inside the JPEG payload itself overrides whatever this reports (Table 13), and
    /// with neither the dictionary entry nor an APP14 marker present, "the default value of
    /// ColorTransform shall be 1 if the image has three components and 0 otherwise" (Table 13). A
    /// caller that needs the effective value must apply both rules; this property reports only the
    /// dictionary's own declared value.
    /// </summary>
    public int? ColorTransform { get; }

    internal PdfDctParameters(int? colorTransform) => ColorTransform = colorTransform;
}
