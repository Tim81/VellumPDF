// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// A module range, measured from the first data module, that a 1D symbol's painter should
/// draw taller than the ordinary data bars (e.g. an EAN/UPC guard pattern).
/// </summary>
/// <param name="StartModule">The offset, in modules, of the first module in the range.</param>
/// <param name="ModuleCount">The width of the range, in modules.</param>
internal readonly record struct GuardExtension(double StartModule, double ModuleCount);

/// <summary>
/// The result of encoding a linear (1D) barcode: alternating bar/space run widths in module
/// units, plus the metadata the shared geometry calculator and painter need — quiet zones,
/// guard-bar height extensions, human-readable text groups, and an optional bearer-bar spec.
/// </summary>
internal sealed class Encoded1D
{
    /// <summary>
    /// Module-unit widths, alternating bar, space, bar, space, ... starting with a bar. Most
    /// symbologies here also end on a bar (guard/stop patterns are bar-terminated).
    /// </summary>
    public required IReadOnlyList<double> Runs { get; init; }

    /// <summary>The quiet zone to the left of the first bar, in modules.</summary>
    public required double QuietZoneLeft { get; init; }

    /// <summary>The quiet zone to the right of the last run, in modules.</summary>
    public required double QuietZoneRight { get; init; }

    /// <summary>
    /// Module ranges that are guard/start/stop bars extending taller than the data bars (e.g.
    /// EAN/UPC guard patterns). Empty when the symbology draws none.
    /// </summary>
    public IReadOnlyList<GuardExtension> GuardExtensions { get; init; } = [];

    /// <summary>Human-readable text groups rendered alongside the symbol.</summary>
    public IReadOnlyList<HriGroup> HriGroups { get; init; } = [];

    /// <summary>Bearer-bar geometry, when the symbology draws one (ITF-14 only).</summary>
    public BearerSpec? Bearer { get; init; }

    /// <summary>The total width in modules, including both quiet zones.</summary>
    public double TotalModuleWidth
    {
        get
        {
            var total = QuietZoneLeft + QuietZoneRight;
            foreach (var run in Runs) total += run;
            return total;
        }
    }
}
