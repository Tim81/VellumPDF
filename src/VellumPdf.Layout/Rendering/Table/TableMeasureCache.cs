// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Rendering.Table;

/// <summary>
/// What one table decision has measured of its cells, so that measuring the table again does not
/// measure every cell again. The decision that reads some cells unbroken measures the whole table
/// once per step; most steps change no cell's text, mode or width, and a cell's measurements
/// depend on nothing else.
/// </summary>
/// <remarks>
/// <para>The cache holds, per cell and per mode (breaks kept or read unbroken), the cell's longest
/// word and widest line, which depend on its text and mode alone, and the number of lines it wraps
/// into, with the interval of widths over which that number holds. It lives for one decision and
/// is dropped with it, so a table that is edited between saves is measured from nothing.</para>
/// <para>Each entry is exact: a lookup returns what measuring again would return, bit for bit, or
/// misses.</para>
/// </remarks>
internal sealed class TableMeasureCache(int rowCount)
{
    private readonly CellMeasure[]?[] _rows = new CellMeasure[rowCount][];

    /// <summary>The entry of a cell in one mode, created empty on first use.</summary>
    public ref CellMeasure Of(int row, int cell, int cellCount, bool honourBreaks)
    {
        var slots = _rows[row] ??= new CellMeasure[cellCount * 2];
        return ref slots[(cell * 2) + (honourBreaks ? 0 : 1)];
    }
}

/// <summary>One cell's cached measurements in one mode.</summary>
internal struct CellMeasure
{
    /// <summary>True when <see cref="LongestWord"/> and <see cref="WidestLine"/> are set.</summary>
    public bool HasWidths;

    /// <summary>The widest word of the lines the cell is measured in, without padding.</summary>
    public double LongestWord;

    /// <summary>The widest of the lines the cell is measured in, without padding.</summary>
    public double WidestLine;

    /// <summary>True when <see cref="Lines"/> and the interval that holds it are set.</summary>
    public bool HasLines;

    /// <summary>
    /// True when the count depends on the exact width, because a word wider than the width was
    /// broken at its characters. <see cref="From"/> then holds that width and <see cref="Below"/>
    /// is unused.
    /// </summary>
    public bool Pinned;

    /// <summary>The number of lines the cell wraps into.</summary>
    public int Lines;

    /// <summary>
    /// The lowest width at which every comparison of the wrap comes out as it did.
    /// </summary>
    public double From;

    /// <summary>
    /// The lowest width from which one of those comparisons comes out differently.
    /// </summary>
    public double Below;

    /// <summary>
    /// True when the cell wraps into <see cref="Lines"/> lines at <paramref name="width"/>.
    /// </summary>
    public readonly bool Holds(double width) =>
        HasLines && (Pinned ? width.Equals(From) : From <= width && width < Below);
}

/// <summary>
/// A hook for tests of the table caches: counts what the caches answered, and when
/// <see cref="Verify"/> is set measures again on every hit and throws when the answer differs.
/// It is thread-static and null by default, so a save pays one null test for it.
/// </summary>
internal sealed class TableMeasureProbe
{
    [ThreadStatic]
    private static TableMeasureProbe? s_current;

    /// <summary>The probe the saving thread reports to, or null.</summary>
    public static TableMeasureProbe? Current
    {
        get => s_current;
        set => s_current = value;
    }

    /// <summary>
    /// When set, a cached line count is checked against a fresh wrap on every hit.
    /// </summary>
    public bool Verify { get; init; }

    /// <summary>Cells whose line count a measurement took from the cache.</summary>
    public int WrapHits { get; set; }

    /// <summary>Cells whose line count a measurement wrapped.</summary>
    public int WrapMisses { get; set; }

    /// <summary>Cells whose longest word and widest line a resolution took from the cache.</summary>
    public int WidthHits { get; set; }

    /// <summary>Cells whose longest word and widest line a resolution measured.</summary>
    public int WidthMisses { get; set; }

    /// <summary>The number of times the decision measured the whole table.</summary>
    public int Passes { get; set; }
}
