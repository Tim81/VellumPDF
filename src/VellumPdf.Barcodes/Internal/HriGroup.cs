// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>Placement of a human-readable interpretation (HRI) text group relative to a 1D symbol.</summary>
internal enum HriAnchor
{
    /// <summary>Centred beneath the span of modules it annotates.</summary>
    Below,

    /// <summary>Set outside the symbol's guard bars, to the left of the left quiet zone.</summary>
    OutsideLeft,

    /// <summary>Set outside the symbol's guard bars, to the right of the right quiet zone.</summary>
    OutsideRight,

    /// <summary>Centred above the span of modules it annotates (EAN-2/EAN-5 add-ons).</summary>
    Above,
}

/// <summary>
/// A run of human-readable digits rendered alongside a 1D symbol, anchored to a span of
/// encoded modules measured from the start of the data (excluding the left quiet zone).
/// </summary>
/// <param name="Text">The characters to render.</param>
/// <param name="Anchor">Where the text sits relative to the symbol.</param>
/// <param name="StartModule">The module offset, from the first data module, where the annotated span begins.</param>
/// <param name="ModuleSpan">The width, in modules, of the span the group is centred over.</param>
internal readonly record struct HriGroup(string Text, HriAnchor Anchor, double StartModule, double ModuleSpan);
