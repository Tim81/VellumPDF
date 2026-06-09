// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// The 14 standard PDF fonts (ISO 32000-2 §9.6.2.2).
/// These are always available in PDF viewers; no embedding required.
/// </summary>
public enum Standard14
{
    /// <summary>Helvetica.</summary>
    Helvetica,

    /// <summary>Helvetica-Bold.</summary>
    HelveticaBold,

    /// <summary>Helvetica-Oblique.</summary>
    HelveticaOblique,

    /// <summary>Helvetica-BoldOblique.</summary>
    HelveticaBoldOblique,

    /// <summary>Times-Roman.</summary>
    TimesRoman,

    /// <summary>Times-Bold.</summary>
    TimesBold,

    /// <summary>Times-Italic.</summary>
    TimesItalic,

    /// <summary>Times-BoldItalic.</summary>
    TimesBoldItalic,

    /// <summary>Courier.</summary>
    Courier,

    /// <summary>Courier-Bold.</summary>
    CourierBold,

    /// <summary>Courier-Oblique.</summary>
    CourierOblique,

    /// <summary>Courier-BoldOblique.</summary>
    CourierBoldOblique,

    /// <summary>Symbol.</summary>
    Symbol,

    /// <summary>ZapfDingbats.</summary>
    ZapfDingbats,
}
