// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Annotations;

/// <summary>
/// Annotation flags — the bits of an annotation dictionary's <c>/F</c> entry
/// (ISO 32000-1:2008, Table 165).
/// </summary>
/// <remarks>
/// <see cref="Print"/> alone is the value PDF/A requires: ISO 19005-2 §6.3.2 requires the Print bit
/// set and <see cref="Hidden"/>, <see cref="Invisible"/> and <see cref="NoView"/> clear on every
/// non-Popup annotation. That is why it is the default on
/// <see cref="PdfLinkAnnotation.Flags"/> — writer output is PDF/A-conformant unless a caller
/// deliberately changes it.
/// </remarks>
[Flags]
public enum PdfAnnotationFlags
{
    /// <summary>No flags set. Not PDF/A-conformant: §6.3.2 requires <see cref="Print"/>.</summary>
    None = 0,

    /// <summary>
    /// Bit 1. Do not render an annotation whose <c>/Subtype</c> the viewer does not recognise.
    /// Has no effect on annotation types the viewer does support.
    /// </summary>
    Invisible = 1 << 0,

    /// <summary>Bit 2. Do not render or print the annotation, and do not let the user interact with it.</summary>
    Hidden = 1 << 1,

    /// <summary>Bit 3. Print the annotation. Required by ISO 19005-2 §6.3.2 for PDF/A.</summary>
    Print = 1 << 2,

    /// <summary>Bit 4. Do not scale the annotation's appearance with the viewer's zoom level.</summary>
    NoZoom = 1 << 3,

    /// <summary>Bit 5. Do not rotate the annotation's appearance with the page.</summary>
    NoRotate = 1 << 4,

    /// <summary>Bit 6. Render the annotation when printing but not on screen.</summary>
    NoView = 1 << 5,

    /// <summary>Bit 7. Do not allow the user to interact with the annotation, but still render it.</summary>
    ReadOnly = 1 << 6,

    /// <summary>Bit 8. Do not allow the annotation to be deleted or its properties to be changed.</summary>
    Locked = 1 << 7,

    /// <summary>Bit 9. Invert the interpretation of the annotation's <c>/NoView</c> flag for certain events.</summary>
    ToggleNoView = 1 << 8,

    /// <summary>Bit 10. Do not allow the annotation's contents to be modified by the user.</summary>
    LockedContents = 1 << 9,
}
