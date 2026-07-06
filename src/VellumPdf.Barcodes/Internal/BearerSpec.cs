// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>Bearer-bar geometry contributed to an ITF-14 symbol's footprint.</summary>
/// <param name="Style">Which sides carry a bearer bar.</param>
/// <param name="ThicknessModules">The bearer bar's thickness, in modules.</param>
internal readonly record struct BearerSpec(ItfBearerBarStyle Style, double ThicknessModules);
