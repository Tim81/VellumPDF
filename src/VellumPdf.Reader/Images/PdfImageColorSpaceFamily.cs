// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Reader;

/// <summary> The colour-space family <see cref="PdfImageColorSpace.Family"/> reports (ISO 32000-2
/// §8.6). Every family an image's <c>/ColorSpace</c> may legally name reaches this reader as one of
/// these except Pattern, which Table 88 marks "Not permitted with images" and which <see
/// cref="PdfReaderDiagnosticCode.ImageColorSpaceUnsupported"/> reports instead.
/// </summary>
public enum PdfImageColorSpaceFamily
{
    /// <summary>ISO 32000-2 §8.6.4.2. One component.</summary>
    DeviceGray = 0,

    /// <summary>ISO 32000-2 §8.6.4.3. Three components.</summary>
    DeviceRgb = 1,

    /// <summary>ISO 32000-2 §8.6.4.4. Four components.</summary>
    DeviceCmyk = 2,

    /// <summary>ISO 32000-2 §8.6.5.2. One component.</summary>
    CalGray = 3,

    /// <summary>ISO 32000-2 §8.6.5.3. Three components.</summary>
    CalRgb = 4,

    /// <summary>ISO 32000-2 §8.6.5.4. Three components.</summary>
    Lab = 5,

    /// <summary>ISO 32000-2 §8.6.5.5. Component count is the profile's own <c>/N</c> (1, 3, or
    /// 4).</summary>
    IccBased = 6,

    /// <summary>ISO 32000-2 §8.6.6.3. One component (an index into <see
    /// cref="PdfImageColorSpace.Base"/>'s own space).</summary>
    Indexed = 7,

    /// <summary>ISO 32000-2 §8.6.6.4. One component.</summary>
    Separation = 8,

    /// <summary>ISO 32000-2 §8.6.6.5. Component count is the space's own colourant-name array
    /// length.</summary>
    DeviceN = 9,
}
