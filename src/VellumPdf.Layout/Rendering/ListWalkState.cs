// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// The items the list walk has visited in one save, below the first level of every list that
/// reports. The
/// items are the caller's graph: one can be its own descendant, and one subtree can be reached by
/// exponentially many paths, by many second-level items, or by every list in the document. Each
/// item is visited at most once per save, so the work is linear in the items and the child links
/// the caller built.
/// </summary>
/// <remarks>
/// <b>Counting rule.</b> A null text or null child on an item is counted once, under the first
/// list, in drawing order, whose walk reaches the item. Only a list that reports takes part: one
/// the document draws from its first item. A later list, or a later second-level item of the same
/// list, that reaches the same item finds it visited and adds nothing. Among the lists that take
/// part, the total does not depend on their order; which list carries a count does.
/// <para>A walk uses an explicit stack, so a deep chain cannot overflow the call stack, and the
/// visited set ends a cycle.</para>
/// </remarks>
internal sealed class ListWalkState
{
    private readonly HashSet<ListItem> _visited = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The items the walks visited, so a test can pin the work to the distinct items.
    /// </summary>
    internal long NodesVisited { get; private set; }

    /// <summary>
    /// Visits <paramref name="child"/>, a second-level item of the list being built, and every item
    /// below it that no earlier walk visited. Returns the null texts and null children found among
    /// them. An item visited before adds nothing.
    /// </summary>
    internal (int NullText, int NullChild) Visit(ListItem child)
    {
        if (!_visited.Add(child)) return (0, 0);

        long nullText = 0, nullChild = 0;
        var pending = new Stack<ListItem>();
        pending.Push(child);
        while (pending.Count > 0)
        {
            var item = pending.Pop();
            NodesVisited++;
            // The second-level item's own text was checked where the list was built; a null there
            // throws, so it is never counted here.
            if (!ReferenceEquals(item, child) && item.HasNullText) nullText++;
            if (item.HasNullChild) nullChild++;
            if (item.Children is null) continue;
            foreach (var next in item.Children)
                if (_visited.Add(next)) pending.Push(next);
        }

        return ((int)Math.Min(nullText, int.MaxValue), (int)Math.Min(nullChild, int.MaxValue));
    }
}
