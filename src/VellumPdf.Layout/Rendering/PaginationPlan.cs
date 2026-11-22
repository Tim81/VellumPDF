// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Where each reported target landed in one pass, a measuring run or the drawing pass, and how
/// many pages the pass counted.
/// Page indices are 0-based and relative to the first page of the layout, whatever pages the
/// <see cref="VellumPdf.Document.PdfDocument"/> already held.
/// </summary>
internal sealed class PaginationPlan
{
    private readonly Dictionary<object, (int PageIndex, double Y)>? _targets;
    private readonly PageLabelMap? _labels;

    /// <summary>
    /// What the first measuring run resolves against: every target is on the first page, at y 0. A
    /// fixed answer, so the first run does not depend on anything a previous run produced.
    /// </summary>
    internal static PaginationPlan Placeholder { get; } = new(null, 1);

    /// <param name="placements">
    /// The placements the run reported, or null for the placeholder. A key reported twice keeps its
    /// first placement.
    /// </param>
    /// <param name="pageCount">The pages the run counted.</param>
    /// <param name="documentLabel">The range that starts at the first page, or null.</param>
    internal PaginationPlan(
        IReadOnlyList<Placement>? placements, int pageCount, Elements.PageLabelRange? documentLabel = null)
    {
        PageCount = pageCount;
        Placements = placements ?? [];
        if (placements is null)
            return;
        _targets = new Dictionary<object, (int, double)>(ReferenceEqualityComparer.Instance);
        foreach (var p in placements)
        {
            _targets.TryAdd(p.Key, (p.PageIndex, p.Y));

            // A range is a placement: the labelled break reported the page the next element is on,
            // so the labels follow the pages the run laid out.
            if (p.Renderer is PageBreakRenderer { PageBreak.PageLabel: { } range })
            {
                _labels ??= StartLabels(documentLabel);
                _labels.Add(p.PageIndex, range);
            }
        }

        if (_labels is null && documentLabel is not null)
            _labels = StartLabels(documentLabel);
    }

    private static PageLabelMap StartLabels(Elements.PageLabelRange? documentLabel)
    {
        var labels = new PageLabelMap();
        if (documentLabel is not null) labels.Add(0, documentLabel);
        return labels;
    }

    internal int PageCount { get; }

    /// <summary>What the run reported, in report order. Empty for the placeholder.</summary>
    internal IReadOnlyList<Placement> Placements { get; }

    internal bool IsPlaceholder => _targets is null;

    /// <summary>
    /// The page index and layout y of <paramref name="key"/>. The placeholder answers true with
    /// (0, 0) for every key; any other plan answers false for a key nothing reported.
    /// </summary>
    internal bool TryGetTarget(object key, out int pageIndex, out double y)
    {
        if (_targets is null)
        {
            pageIndex = 0;
            y = 0;
            return true;
        }

        if (_targets.TryGetValue(key, out var found))
        {
            (pageIndex, y) = found;
            return true;
        }

        pageIndex = 0;
        y = 0;
        return false;
    }

    /// <summary>
    /// The label of the page, as a viewer shows it: the decimal number when the document defines no
    /// range, otherwise the page's range's prefix and numeral. Indices are relative to the first
    /// page of the layout, like every other index in the plan.
    /// </summary>
    /// <exception cref="InvalidOperationException">The label would be longer than 32,767
    /// characters.</exception>
    internal string LabelOf(int pageIndex) =>
        _labels is null
            ? (pageIndex + 1).ToString(CultureInfo.InvariantCulture)
            : _labels.LabelOf(pageIndex);
}

/// <summary>
/// A renderer whose text depends on the plan. <see cref="DocumentRenderer"/> resolves it between
/// measuring runs, so every string it draws names the page its target is drawn on.
/// </summary>
internal interface IPlanDependent
{
    /// <summary>
    /// Takes the strings this renderer will lay out and draw from <paramref name="plan"/>. Returns
    /// true when they differ from the ones it held, and then drops whatever it cached from them,
    /// such as its wrap. A renderer that returns false keeps its cache.
    /// </summary>
    bool Resolve(PaginationPlan plan);

    /// <summary>Names the target, for the message about a plan that does not settle.</summary>
    string TargetName { get; }

    /// <summary>The text the last <see cref="Resolve"/> left, for the same message.</summary>
    string ResolvedText { get; }
}
