// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
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

    // Every name a style or a page reference links to, with where it sits, for the check at the
    // end of the walk. Null until the first one, so a document with none allocates nothing.
    private List<(string Name, ElementSite Site)>? _uses;
    private bool _underUa;

    /// <summary>The page references the walk found, or null when the elements hold none.</summary>
    internal PageReferenceTable? References { get; private set; }

    /// <summary>Whether an element of the document holds the anchor <paramref name="name"/>.</summary>
    internal bool Declares(string name) => _byName is not null && _byName.ContainsKey(name);

    /// <summary>Whether a <see cref="PageBreak"/> in the flow starts a label range.</summary>
    internal bool HasBreakLabels { get; private set; }

    /// <summary>
    /// Walks <paramref name="renderers"/>. Throws <see cref="InvalidOperationException"/> for an
    /// anchor two elements hold, and for a labelled break no element follows.
    /// </summary>
    internal static NavigationPlan Build(IReadOnlyList<IRenderer> renderers) =>
        Build(renderers, PdfConformance.None, null, null);

    /// <summary>
    /// The walk, which also reads every text style that links to an anchor and every page
    /// reference, and refuses before any layout what the elements alone decide: a link under a
    /// PDF/UA-1 claim, a style that sets two links, a name no element holds, and a name only an item
    /// below the second level of a list holds. The running bands are read through
    /// <paramref name="header"/> and <paramref name="footer"/>.
    /// </summary>
    internal static NavigationPlan Build(
        IReadOnlyList<IRenderer> renderers, PdfConformance conformance, RunningBand? header, RunningBand? footer)
    {
        var plan = new NavigationPlan { _underUa = conformance == PdfConformance.PdfUA1 };
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
                    plan.NoteRuns(paragraph.Paragraph.Runs, new ElementSite(LayoutElementKind.Paragraph, i));
                    break;
                case HeadingRenderer heading:
                    plan.Add(heading.Element.Anchor, LayoutElementKind.Heading, i);
                    plan.NoteRuns(heading.Element.BackingRuns, new ElementSite(LayoutElementKind.Heading, i));
                    break;
                case TableRenderer table:
                    plan.Add(table.Element.Anchor, LayoutElementKind.Table, i);
                    plan.WalkTable(table.Element, i);
                    break;
                case LayoutImageRenderer image:
                    plan.Add(image.Element.Anchor, LayoutElementKind.Image, i);
                    break;
                case PreformattedRenderer block:
                    plan.Add(block.Element.Anchor, LayoutElementKind.Preformatted, i);
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

        // The bands draw one string in one style, so only a link can sit in one.
        if (header is not null) plan.NoteStyle(header.Style, new ElementSite(LayoutElementKind.Header, 0));
        if (footer is not null) plan.NoteStyle(footer.Style, new ElementSite(LayoutElementKind.Footer, 0));

        plan.RefuseUnknownLinks();
        return plan;
    }

    // A style that links to an anchor. Skips a null style, which the layout refuses where it meets
    // one that draws text, and so never reads a property of it.
    private void NoteStyle(TextStyle? style, ElementSite site)
    {
        if (style?.LinkAnchor is not { } name) return;
        if (_underUa) throw UnderUa(name, site);
        if (style.LinkUri is not null)
            throw new InvalidOperationException(
                $"The style of {site} sets both LinkUri and LinkAnchor, and a link has one target. " +
                "Nothing has been laid out, so the document can be saved once one of them is removed.");
        (_uses ??= []).Add((name, site));
    }

    /// <summary>
    /// The runs and cell styles this thread's walks have read, so a test can see that each is read
    /// once. Not a count of anything the library reports.
    /// </summary>
    [ThreadStatic]
    internal static int StylesRead;

    private void NoteRuns(IReadOnlyList<TextRun>? runs, ElementSite site)
    {
        if (runs is null) return;
        for (var i = 0; i < runs.Count; i++)
        {
            StylesRead++;
            var run = runs[i];
            if (run is null || run.Style is null) continue;
            if (run.IsReference) NoteReference(run, site);
            else NoteStyle(run.Style, site);
        }
    }

    private void NoteReference(TextRun run, ElementSite site)
    {
        var name = run.ReferenceAnchor!;
        if (_underUa) throw UnderUa(name, site);
        if (run.Style.LinkUri is not null || run.Style.LinkAnchor is not null)
            throw new InvalidOperationException(
                $"The page reference to the anchor \"{name}\" in {site} is given a style that sets " +
                (run.Style.LinkUri is not null ? "LinkUri" : "LinkAnchor") +
                ", but a reference is always a link to its own anchor. " +
                "Nothing has been laid out, so the document can be saved once the style is replaced.");
        (_uses ??= []).Add((name, site));
        (References ??= new PageReferenceTable()).Add(run, site);
    }

    private static InvalidOperationException UnderUa(string name, ElementSite site) =>
        new($"{site} links to the anchor \"{name}\", which a PDF/UA-1 claim refuses: the link would be " +
            "untagged, and ISO 14289-1, 7.18.5 does not allow that. Nothing has been laid out, so the document " +
            "can be saved once the link is removed or the claim changed.");

    private void WalkTable(TableElement table, int index)
    {
        var rows = table.Rows;
        for (var r = 0; r < rows.Count; r++)
        {
            var cells = rows[r].Cells;
            for (var c = 0; c < cells.Count; c++)
            {
                var cell = cells[c];
                if (cell is null) continue;
                var site = new ElementSite(LayoutElementKind.Table, index, Row: r, Cell: c);

                // A cell built from runs links through its runs; its own style is not drawn there.
                if (cell.Runs is { } runs)
                {
                    NoteRuns(runs, site);
                }
                else
                {
                    StylesRead++;
                    NoteStyle(cell.Style ?? table.DefaultCellStyle, site);
                }
            }
        }
    }

    private void NoteItem(ListItem item, TextStyle style, ElementSite site)
    {
        if (item.HasNullText) return;
        NoteStyle(style, site);
        NoteRuns(item.OwnRuns, site);
    }

    // A name that no element holds, or that only an item the layout never draws holds, is refused
    // while the document can still be saved.
    private void RefuseUnknownLinks()
    {
        if (_uses is null) return;
        foreach (var (name, site) in _uses)
        {
            if (_byName is null || !_byName.TryGetValue(name, out var found))
                throw new InvalidOperationException(
                    $"{site} links to the anchor \"{name}\", but no element holds an anchor of that name. " +
                    "Nothing has been laid out, so the document can be saved once the name is fixed or an element " +
                    "is given that Anchor.");
            if (_anchors[found].Site.Deeper)
                throw new InvalidOperationException(
                    $"{site} links to the anchor \"{name}\", which sits on {_anchors[found].Site}, an item the layout " +
                    "neither lays out nor draws, so the link has nowhere to go. Nothing has been laid out, so " +
                    "the document can be saved once the anchor is on an item of the first two levels.");
        }
    }

    private void WalkList(ListElement list, int index, ref HashSet<ListItem>? deep)
    {
        Add(list.Anchor, LayoutElementKind.List, index);
        var items = list.Items;
        var defaultStyle = list.DefaultStyle ?? TextStyle.Default;
        for (var item = 0; item < items.Count; item++)
        {
            var top = items[item];
            Add(top.Anchor, LayoutElementKind.List, index, item);
            var topStyle = top.Style ?? defaultStyle;
            NoteItem(top, topStyle, new ElementSite(LayoutElementKind.List, index, item));
            if (top.Children is not { } children) continue;

            for (var child = 0; child < children.Count; child++)
            {
                var second = children[child];
                Add(second.Anchor, LayoutElementKind.List, index, item, child);
                NoteItem(second, second.Style ?? topStyle, new ElementSite(LayoutElementKind.List, index, item, child));
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
