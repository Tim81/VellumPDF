// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// What the list walk found below each second-level item, kept for one save. The items are the
/// caller's graph: one can be its own descendant, and one subtree can be reached by exponentially
/// many paths, or by every list in the document, so each second-level item is walked once and a
/// list adds the cached counts.
/// </summary>
/// <remarks>
/// A walk uses an explicit stack, so a deep chain cannot overflow the call stack, and a visited
/// set seeded with the second-level item, so a cycle ends. The counts of an item are the distinct
/// items reachable from it, itself included, so an item reachable from two second-level items of
/// one list is counted once for each.
/// </remarks>
internal sealed class ListWalkMemo
{
    private readonly Dictionary<ListItem, (int NullText, int NullChild)> _byHolder =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The items the walks visited, so a test can pin the work to the distinct items.
    /// </summary>
    internal long NodesVisited { get; private set; }

    /// <summary>
    /// The number of distinct items at or below <paramref name="holder"/> that hold a null text,
    /// and the number that hold a null child.
    /// </summary>
    internal (int NullText, int NullChild) CountBelow(ListItem holder)
    {
        if (_byHolder.TryGetValue(holder, out var known)) return known;

        long nullText = 0, nullChild = 0;
        var visited = new HashSet<ListItem>(ReferenceEqualityComparer.Instance) { holder };
        var pending = new Stack<ListItem>();
        pending.Push(holder);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            NodesVisited++;
            // The holder's own text was checked where the list was built; a null there throws.
            if (!ReferenceEquals(item, holder) && item.HasNullText) nullText++;
            if (item.HasNullChild) nullChild++;
            if (item.Children is null) continue;
            foreach (var next in item.Children)
                if (visited.Add(next)) pending.Push(next);
        }

        var result = ((int)Math.Min(nullText, int.MaxValue), (int)Math.Min(nullChild, int.MaxValue));
        _byHolder.Add(holder, result);
        return result;
    }
}
