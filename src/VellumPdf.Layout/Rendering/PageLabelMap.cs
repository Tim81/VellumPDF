// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Document;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// The label ranges of one pass, keyed by the page index each starts at, and the label each page
/// reads (ISO 32000-2, 12.4.2). A range is a placement: it starts at the page the element after a
/// <see cref="PageBreak"/> lands on, so a map built from a pass's placements agrees with the pages
/// that pass drew.
/// </summary>
/// <remarks>
/// Indices are whatever the caller passes, so a map is consistent as long as its adds and its
/// lookups use the same base. A range that starts at an index that already has one replaces it,
/// and a lookup before the first range reads the implicit decimal numbering a viewer uses
/// without labels, which is what the kernel writes for page index 0 when no range starts there.
/// </remarks>
internal sealed class PageLabelMap
{
    // Ascending by start. A pass places pages in order, so Add only ever appends or replaces the
    // last entry.
    private readonly List<(int Start, PageLabelRange Range)> _ranges = [];

    internal bool IsEmpty => _ranges.Count == 0;

    internal IReadOnlyList<(int Start, PageLabelRange Range)> Ranges => _ranges;

    /// <summary>
    /// Starts <paramref name="range"/> at <paramref name="start"/>. A range already there is
    /// replaced, which is the rule for two ranges that land on one page: the later one wins.
    /// </summary>
    internal void Add(int start, PageLabelRange range)
    {
        if (_ranges.Count > 0 && _ranges[^1].Start >= start)
        {
            // Placements arrive in page order, so this is the same page; a start before the last
            // one would be a placement out of order, which the loop cannot produce.
            _ranges[^1] = (_ranges[^1].Start, range);
            return;
        }

        _ranges.Add((start, range));
    }

    /// <summary>
    /// The label of the page at <paramref name="pageIndex"/>: its range's prefix and numeral, or
    /// the decimal page number when no range has started yet.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The label would be longer than 32,767 characters, which a range whose first number is large
    /// reaches a few pages in.
    /// </exception>
    internal string LabelOf(int pageIndex)
    {
        var found = -1;
        for (var i = _ranges.Count - 1; i >= 0; i--)
        {
            if (_ranges[i].Start > pageIndex) continue;
            found = i;
            break;
        }

        if (found < 0) return ((long)pageIndex + 1).ToString(CultureInfo.InvariantCulture);

        var (start, range) = _ranges[found];
        var number = (long)range.FirstNumber + (pageIndex - start);
        try
        {
            return PageLabelFormatter.Format(range.Style, range.Prefix, number);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new InvalidOperationException(
                $"The label of page {pageIndex + 1} would be longer than {PageLabelFormatter.MaxLength} characters: " +
                $"its range starts at {range.FirstNumber} and numbers up from there. Start the range at a smaller number.");
        }
    }
}
