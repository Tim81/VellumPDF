// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Reader.Content;

/// <summary>
/// The subset of a content stream's graphics state (ISO 32000-2 §8.4) this interpreter tracks:
/// the current transformation matrix, and the text state parameters §9.3.1 makes part of the
/// graphics state (so they save and restore with <c>q</c>/<c>Q</c> along with everything else
/// here). Colour, line width, dash pattern, clipping path, and every other §8.4 parameter are
/// recognised at the operator level (Annex A Table A.1) but not tracked: this interpreter's job is
/// to keep <see cref="Ctm"/> and the text parameters current for a later caller (text extraction,
/// then image extraction, per #98) to read during a visitor callback, not to reproduce a full
/// graphics pipeline.
/// </summary>
internal sealed class GraphicsState
{
    /// <summary>The current transformation matrix, concatenated by <c>cm</c> (§8.3.4).</summary>
    internal Matrix Ctm { get; set; } = Matrix.Identity;

    /// <summary>Character spacing, <c>Tc</c> (§9.3.2), also settable by <c>"</c>'s own <c>ac</c>
    /// operand (Table 107). Added to the horizontal or vertical component of each glyph's
    /// displacement, depending on the writing mode.</summary>
    internal double CharSpacing { get; set; }

    /// <summary>Word spacing, <c>Tw</c> (§9.3.3), also settable by <c>"</c>'s own <c>aw</c> operand
    /// (Table 107). Added only after a single-byte code 32.</summary>
    internal double WordSpacing { get; set; }

    /// <summary>Horizontal scaling, <c>Tz</c> (§9.3.4), as a percentage; 100 is unscaled.</summary>
    internal double HorizontalScaling { get; set; } = 100;

    /// <summary>Leading, <c>TL</c> (§9.3.5): the line-to-line advance <c>T*</c>, <c>'</c>, and
    /// <c>"</c> use, and what <c>TD</c> sets from its own <c>ty</c> operand.</summary>
    internal double Leading { get; set; }

    /// <summary>
    /// The font operand from the last <c>Tf</c> or <c>gs</c>-with-<c>/Font</c> (§9.3.1 Table 103,
    /// §8.4.5 Table 57): a <see cref="PdfName"/> naming a <c>/Resources /Font</c> entry for
    /// <c>Tf</c>, or the <see cref="PdfObject"/> an ExtGState's own <c>/Font</c> array names as its
    /// font directly (Table 57 requires that to be an indirect reference to a font dictionary) for
    /// <c>gs</c>. Not resolved by this interpreter either way: font resolution and glyph
    /// positioning are for a later caller to do, not this interpreter's own job.
    /// </summary>
    internal PdfObject? Font { get; set; }

    /// <summary>
    /// The <c>/Resources</c> dictionary that was current when <see cref="Font"/> was last set
    /// (ISO 32000-2 §9.3.1 Table 103: <c>Tf</c> binds "a font resource in the Font subdictionary
    /// of the current resource dictionary" at the point it executes, not at the point a later
    /// text-showing operator reads it). Needed only to resolve a <c>Tf</c>-set <see cref="PdfName"/>
    /// against the correct <c>/Font</c> subdictionary: an ExtGState's <c>/Font</c> array (§8.4.5
    /// Table 57) names its font dictionary directly, so a caller resolving <see cref="Font"/> for
    /// that case never consults this property at all. §8.10.1's implicit save carries this
    /// alongside <see cref="Font"/> into a Form XObject's graphics state (<see cref="Clone"/>), so
    /// text shown inside a form that never calls its own <c>Tf</c> resolves the invoker's font
    /// name against the invoker's resources, not the form's.
    /// </summary>
    internal PdfDictionary? FontResources { get; set; }

    /// <summary>Font size in unscaled text-space units, from the same <c>Tf</c> or <c>gs</c> call
    /// that set <see cref="Font"/>.</summary>
    internal double FontSize { get; set; }

    /// <summary>Text rendering mode, <c>Tr</c> (§9.3.6): 0–7 per Table 104, not validated here.</summary>
    internal int RenderMode { get; set; }

    /// <summary>Text rise, <c>Ts</c> (§9.3.7): vertical displacement, in unscaled text-space units.</summary>
    internal double Rise { get; set; }

    /// <summary>Deep-copies this state for a <c>q</c> push; <c>Q</c> discards the top and restores
    /// the state it copied from.</summary>
    internal GraphicsState Clone() => (GraphicsState)MemberwiseClone();
}
