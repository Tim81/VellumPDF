// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.IO.Linearization;

/// <summary>
/// The computed object numbering and write-order plan produced by
/// <see cref="LinearizedLayoutPlanner"/>. Describes which objects belong to the
/// first-page section and which to the rest section, after renumbering.
/// </summary>
internal sealed class LinearizedLayout
{
    /// <summary>Maps original object numbers to new linearized object numbers.</summary>
    public IReadOnlyDictionary<int, int> OldToNew { get; }

    /// <summary>
    /// Objects in the rest section (written first, numbered 1..RestCount).
    /// Each entry is (new object number, remapped object value).
    /// </summary>
    public IReadOnlyList<(int NewObjNum, PdfObject Value)> RestObjects { get; }

    /// <summary>
    /// Objects in the first-page section (written after the rest section, numbered RestCount+1..).
    /// Includes the linearization-dict slot and the hint-stream slot.
    /// </summary>
    public IReadOnlyList<(int NewObjNum, PdfObject Value)> FirstPageObjects { get; }

    /// <summary>New object number of the linearization dictionary (placeholder object).</summary>
    public int LinDictObjNum { get; }

    /// <summary>New object number of the placeholder hint stream.</summary>
    public int HintStreamObjNum { get; }

    /// <summary>New object number of the document catalog.</summary>
    public int CatalogObjNum { get; }

    /// <summary>New object number of the first page's page dictionary (/O in the lin dict).</summary>
    public int FirstPageObjNum { get; }

    /// <summary>Total object count (including object 0), i.e. /Size in the trailer.</summary>
    public int TotalSize { get; }

    /// <summary>Per-page lists of new object numbers, in page order (used by hint tables in Step 2+).</summary>
    public IReadOnlyList<IReadOnlyList<int>> PageObjectGroups { get; }

    internal LinearizedLayout(
        IReadOnlyDictionary<int, int> oldToNew,
        IReadOnlyList<(int, PdfObject)> restObjects,
        IReadOnlyList<(int, PdfObject)> firstPageObjects,
        int linDictObjNum,
        int hintStreamObjNum,
        int catalogObjNum,
        int firstPageObjNum,
        int totalSize,
        IReadOnlyList<IReadOnlyList<int>> pageObjectGroups)
    {
        OldToNew = oldToNew;
        RestObjects = restObjects;
        FirstPageObjects = firstPageObjects;
        LinDictObjNum = linDictObjNum;
        HintStreamObjNum = hintStreamObjNum;
        CatalogObjNum = catalogObjNum;
        FirstPageObjNum = firstPageObjNum;
        TotalSize = totalSize;
        PageObjectGroups = pageObjectGroups;
    }
}
