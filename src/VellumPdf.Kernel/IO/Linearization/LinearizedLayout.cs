// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.IO.Linearization;

/// <summary>
/// The computed object numbering and write-order plan produced by
/// <see cref="LinearizedLayoutPlanner"/>. Describes which objects belong to the
/// first-page section and which to the rest section, after renumbering, plus the
/// per-page groupings the hint tables need.
/// </summary>
internal sealed class LinearizedLayout
{
    /// <summary>Maps original object numbers to new linearized object numbers.</summary>
    public IReadOnlyDictionary<int, int> OldToNew { get; }

    /// <summary>
    /// Objects in the rest section (written after the first-page section), numbered 1..RestCount.
    /// Ordered so each page's private objects are contiguous (page 1, page 2, …), followed by
    /// objects shared among later pages and then any unreferenced objects.
    /// Each entry is (new object number, remapped object value).
    /// </summary>
    public IReadOnlyList<(int NewObjNum, PdfObject Value)> RestObjects { get; }

    /// <summary>
    /// Document-level objects (catalog, page tree, info, metadata) written before the hint stream.
    /// They are not counted as belonging to any single page. Each entry is (new object number, value).
    /// </summary>
    public IReadOnlyList<(int NewObjNum, PdfObject Value)> Part4Objects { get; }

    /// <summary>
    /// The first page's own objects (its page dict, content, and resources), written after the hint
    /// stream. These are the first page's contribution to the hint tables. Each entry is (new number, value).
    /// </summary>
    public IReadOnlyList<(int NewObjNum, PdfObject Value)> Part6Objects { get; }

    /// <summary>New object number of the linearization dictionary (placeholder object).</summary>
    public int LinDictObjNum { get; }

    /// <summary>New object number of the hint stream.</summary>
    public int HintStreamObjNum { get; }

    /// <summary>New object number of the document catalog.</summary>
    public int CatalogObjNum { get; }

    /// <summary>New object number of the first page's page dictionary (/O in the lin dict).</summary>
    public int FirstPageObjNum { get; }

    /// <summary>Total object count (including object 0), i.e. /Size in the trailer.</summary>
    public int TotalSize { get; }

    /// <summary>
    /// The new object numbers that belong to each page, in page order. Index 0 is the first page's
    /// own objects (the first-page section objects); index i&gt;0 is page i's private objects, in
    /// their contiguous file order. Drives the page-offset hint table's per-page object count and length.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> PageObjectNums { get; }

    /// <summary>
    /// The shared-object table, as new object numbers: the first-page section objects followed by
    /// objects shared among later pages. Indices into this list are the shared-object identifiers
    /// used by <see cref="PageSharedRefs"/>.
    /// </summary>
    public IReadOnlyList<int> SharedTableObjNums { get; }

    /// <summary>Number of shared-table entries that belong to the first page (nshared_first_page).</summary>
    public int NsharedFirstPage { get; }

    /// <summary>
    /// Per page, the shared-object identifiers (indices into <see cref="SharedTableObjNums"/>) that the
    /// page references. Index 0 (the first page) is always empty per ISO 32000-2 §F.3.1.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> PageSharedRefs { get; }

    /// <summary>
    /// New object numbers for the outline group (root first, then items, in file order).
    /// Empty when the document has no outlines.
    /// </summary>
    public IReadOnlyList<int> OutlineObjNums { get; }

    /// <summary>
    /// True when the outline group is placed in part 6 (appended after the first page's own
    /// objects). False means part 9. VellumPdf always takes the part-6 path when outlines exist.
    /// </summary>
    public bool OutlinesInFirstPage { get; }

    internal LinearizedLayout(
        IReadOnlyDictionary<int, int> oldToNew,
        IReadOnlyList<(int, PdfObject)> restObjects,
        IReadOnlyList<(int, PdfObject)> part4Objects,
        IReadOnlyList<(int, PdfObject)> part6Objects,
        int linDictObjNum,
        int hintStreamObjNum,
        int catalogObjNum,
        int firstPageObjNum,
        int totalSize,
        IReadOnlyList<IReadOnlyList<int>> pageObjectNums,
        IReadOnlyList<int> sharedTableObjNums,
        int nsharedFirstPage,
        IReadOnlyList<IReadOnlyList<int>> pageSharedRefs,
        IReadOnlyList<int>? outlineObjNums = null,
        bool outlinesInFirstPage = false)
    {
        OldToNew = oldToNew;
        RestObjects = restObjects;
        Part4Objects = part4Objects;
        Part6Objects = part6Objects;
        LinDictObjNum = linDictObjNum;
        HintStreamObjNum = hintStreamObjNum;
        CatalogObjNum = catalogObjNum;
        FirstPageObjNum = firstPageObjNum;
        TotalSize = totalSize;
        PageObjectNums = pageObjectNums;
        SharedTableObjNums = sharedTableObjNums;
        NsharedFirstPage = nsharedFirstPage;
        PageSharedRefs = pageSharedRefs;
        OutlineObjNums = outlineObjNums ?? [];
        OutlinesInFirstPage = outlinesInFirstPage;
    }
}
