// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// The anchors and label ranges the added elements hold, found by one walk before any layout runs
/// (#262), so that what can be refused from the elements alone is refused while the document is
/// still saveable. It also remembers which anchors the drawing pass registered, so an anchor on
/// content the renderer never drew can be named afterwards.
/// </summary>
/// <remarks>
/// The walk visits every added renderer once and every list item at every depth once, with an
/// explicit stack for the depths below the second, which no renderer draws, so a deep or cyclic
/// item graph cannot overflow the call stack and costs the items it holds. A document with no
/// anchors and no labelled break allocates nothing beyond the walk's own locals.
/// </remarks>
internal sealed class NavigationPlan
{
    private readonly List<(string Name, AnchorSite Site)> _anchors = [];
    private Dictionary<string, int>? _byName;
    private HashSet<string>? _registered;

    internal bool HasAnchors => _anchors.Count > 0;

    /// <summary>Whether a <see cref="PageBreak"/> in the flow starts a label range.</summary>
    internal bool HasBreakLabels { get; private set; }

    /// <summary>
    /// Walks <paramref name="renderers"/>. Throws <see cref="InvalidOperationException"/> for an
    /// anchor two elements hold, and for a labelled break no element follows.
    /// </summary>
    internal static NavigationPlan Build(IReadOnlyList<IRenderer> renderers)
    {
        var plan = new NavigationPlan();
        HashSet<ListItem>? deep = null;
        int? trailingBreak = null;

        for (var i = 0; i < renderers.Count; i++)
        {
            switch (renderers[i])
            {
                case PageBreakRenderer { PageBreak.PageLabel: not null }:
                    plan.HasBreakLabels = true;
                    trailingBreak ??= i;
                    continue;
                case PageBreakRenderer:
                    continue;
                case ParagraphRenderer paragraph:
                    plan.Add(paragraph.Paragraph.Anchor, LayoutElementKind.Paragraph, i);
                    break;
                case HeadingRenderer heading:
                    plan.Add(heading.Element.Anchor, LayoutElementKind.Heading, i);
                    break;
                case TableRenderer table:
                    plan.Add(table.Element.Anchor, LayoutElementKind.Table, i);
                    break;
                case LayoutImageRenderer image:
                    plan.Add(image.Element.Anchor, LayoutElementKind.Image, i);
                    break;
                case ListRenderer list:
                    plan.WalkList(list.Element, i, ref deep);
                    break;
            }

            // An element follows every labelled break before it.
            trailingBreak = null;
        }

        if (trailingBreak is { } at)
            throw new InvalidOperationException(
                $"PageBreak {at.ToString(CultureInfo.InvariantCulture)} starts a page-label range, but no element follows it. " +
                "A break with nothing after it is dropped, so the page its range would start on is never created. " +
                "Add an element after the break, or remove its PageLabel.");

        return plan;
    }

    private void WalkList(ListElement list, int index, ref HashSet<ListItem>? deep)
    {
        Add(list.Anchor, LayoutElementKind.List, index);
        var items = list.Items;
        for (var item = 0; item < items.Count; item++)
        {
            var top = items[item];
            Add(top.Anchor, LayoutElementKind.List, index, item);
            if (top.Children is not { } children) continue;

            for (var child = 0; child < children.Count; child++)
            {
                var second = children[child];
                Add(second.Anchor, LayoutElementKind.List, index, item, child);
                if (second.Children is null) continue;

                // Below the second level nothing is drawn, so these items matter only for the
                // anchors they hold. The visited set ends a cycle and keeps a subtree reached from
                // many parents to one visit.
                deep ??= new HashSet<ListItem>(ReferenceEqualityComparer.Instance);
                var pending = new Stack<ListItem>();
                foreach (var next in second.Children)
                    if (deep.Add(next)) pending.Push(next);
                while (pending.Count > 0)
                {
                    var below = pending.Pop();
                    Add(below.Anchor, LayoutElementKind.List, index, item, child, deeper: true);
                    if (below.Children is null) continue;
                    foreach (var next in below.Children)
                        if (deep.Add(next)) pending.Push(next);
                }
            }
        }
    }

    private void Add(string? name, LayoutElementKind kind, int index, int item = -1, int child = -1, bool deeper = false)
    {
        if (name is null) return;

        var site = new AnchorSite(kind, index, item, child, deeper);
        _byName ??= new Dictionary<string, int>(StringComparer.Ordinal);
        if (!_byName.TryAdd(name, _anchors.Count))
            throw new InvalidOperationException(
                $"The anchor \"{name}\" is held by two elements: {_anchors[_byName[name]].Site} and {site}. " +
                "An anchor names one destination, so give one of them another name. Nothing has been laid out, " +
                "so the document can be saved once the elements are replaced.");
        _anchors.Add((name, site));
    }

    /// <summary>
    /// Notes that the drawing pass registered a destination for <paramref name="name"/>. Returns
    /// false when it already had, so a name is never registered twice.
    /// </summary>
    internal bool TryRegister(string name) => (_registered ??= new HashSet<string>(StringComparer.Ordinal)).Add(name);

    /// <summary>
    /// Throws for the first anchor, in the order the elements were added, that the drawing pass
    /// never registered: its element was not drawn, or its piece that holds the first line was not
    /// reported.
    /// </summary>
    internal void RefuseUnplaced()
    {
        foreach (var (name, site) in _anchors)
        {
            if (_registered is not null && _registered.Contains(name)) continue;
            throw new InvalidOperationException(
                $"The anchor \"{name}\" on {site} was never placed, because the layout does not draw that element " +
                "(an item of a list below the second level, for example, is neither laid out nor drawn). " +
                "The layout has already run, so this document cannot be saved again; create a new Document " +
                "and anchor an element that is drawn.");
        }
    }

    /// <summary>The kind and zero-based position of an element in the order added, for a message.</summary>
    private readonly record struct AnchorSite(LayoutElementKind Kind, int Index, int Item, int Child, bool Deeper)
    {
        public override string ToString()
        {
            var text = $"{Kind} {Index.ToString(CultureInfo.InvariantCulture)}";
            if (Item < 0) return text;
            text += $" (item {Item.ToString(CultureInfo.InvariantCulture)}";
            if (Child >= 0) text += $", child {Child.ToString(CultureInfo.InvariantCulture)}";
            if (Deeper) text += ", below the second level";
            return text + ")";
        }
    }
}
