// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering.Table;

namespace VellumPdf.Layout.Rendering;

/// <summary>Where a text style or a page reference sits among the elements, for
/// a message.</summary>
internal readonly record struct ElementSite(
    LayoutElementKind Kind, int Index, int Item = -1, int Child = -1, int Row = -1, int Cell = -1,
    bool Deeper = false)
{
    public override string ToString()
    {
        var text = Kind is LayoutElementKind.Header or LayoutElementKind.Footer
            ? Kind.ToString() + " band"
            : $"{Kind} {Index.ToString(CultureInfo.InvariantCulture)}";
        if (Item >= 0)
        {
            text += $" (item {Item.ToString(CultureInfo.InvariantCulture)}";
            if (Child >= 0) text += $", child {Child.ToString(CultureInfo.InvariantCulture)}";
            if (Deeper) text += ", below the second level";
            text += ")";
        }

        if (Row >= 0)
            text += $" (row {Row.ToString(CultureInfo.InvariantCulture)}, cell {Cell.ToString(CultureInfo.InvariantCulture)})";
        return text;
    }
}

/// <summary>One page reference run and the label the pagination loop last gave it.</summary>
internal sealed class PageReferenceEntry(TextRun run, ElementSite site)
{
    private TextRun? _effective;

    internal TextRun Run { get; } = run;

    internal string Anchor => Run.ReferenceAnchor!;

    internal ElementSite Site { get; } = site;

    /// <summary>The placeholder before the first resolve, as every run starts.</summary>
    internal string Label { get; private set; } = "1";

    /// <summary>True for a reference the walk over the added elements did not find.</summary>
    internal bool OnDemand { get; init; }

    /// <summary>
    /// The run a renderer lays out in place of the reference: the label, in the reference's linked
    /// style. Replaced when the label changes, so a wrap made from it is told from one made from
    /// the next label.
    /// </summary>
    internal TextRun Effective => _effective ??= new TextRun(Label, Run.LinkedStyle);

    internal void SetLabel(string label)
    {
        Label = label;
        _effective = null;
    }
}

/// <summary>
/// The strings a measuring run read for the page references of its elements, and whether they are
/// still what the table holds. A renderer keeps one from the wrap it made.
/// </summary>
internal sealed class ReferenceReads(PageReferenceTable table)
{
    private readonly List<TextRun> _runs = [];
    private readonly List<string> _labels = [];
    private long _epoch = table.Epoch;

    internal PageReferenceTable Table { get; } = table;

    internal void Add(TextRun run, string label)
    {
        _runs.Add(run);
        _labels.Add(label);
        _epoch = Table.Epoch;
    }

    /// <summary>
    /// True when the table is the one the strings were read from and every one of them is still the
    /// string it holds. The epoch answers the common case, a table no label of which changed, with
    /// one comparison.
    /// </summary>
    internal bool IsCurrent()
    {
        if (!ReferenceEquals(ListLayoutRun.Current?.References, Table)) return false;
        if (_epoch == Table.Epoch) return true;
        for (var i = 0; i < _runs.Count; i++)
            if (!string.Equals(Table.EntryFor(_runs[i]).Label, _labels[i], StringComparison.Ordinal))
                return false;
        _epoch = Table.Epoch;
        return true;
    }
}

/// <summary>What a run cell is laid out from while a label of it is known.</summary>
internal sealed class CellView(CellRunText runText, string content, string[] labels, long epoch)
{
    internal CellRunText RunText { get; } = runText;

    internal string Content { get; } = content;

    internal string[] Labels { get; } = labels;

    internal long Epoch { get; set; } = epoch;
}

/// <summary>
/// Every page reference of one layout, with the label each was last given. It is the one plan
/// dependent of a document that holds references: <see cref="DocumentRenderer"/> resolves it
/// against each measuring run's plan until its strings stop changing, and the renderers that hold a
/// reference read its label from here, through <see cref="PageReferences"/>.
/// </summary>
/// <remarks>
/// It lives on <see cref="ListLayoutRun"/>, so it flows with the execution context of the layout in
/// progress, as the list rules do. Work queued with the context suppressed does not see it.
/// </remarks>
internal sealed class PageReferenceTable : IPlanDependent
{
    private readonly Dictionary<TextRun, PageReferenceEntry> _byRun = new(ReferenceEqualityComparer.Instance);
    private readonly List<PageReferenceEntry> _walk = [];
    private readonly List<string[]> _states = [];
    private Dictionary<Cell, CellView>? _cells;

    /// <summary>Incremented each time a resolve changes a label.</summary>
    internal long Epoch { get; private set; }

    /// <summary>
    /// The most characters a label can hold, so a bound on the continuations an element can need
    /// holds for the label the loop settles on: the longest prefix any range of the document has,
    /// and a numeral. See <see cref="NumeralBound"/>.
    /// </summary>
    internal int LabelBound { get; set; }

    /// <summary>
    /// What the bound allows a page numeral. A decimal numeral of 16 digits is out of reach of a
    /// document counting pages from 1, but a range that starts at a large <c>FirstNumber</c>, or a
    /// letter or Roman style a long way in, prints a numeral longer than this, and the label is
    /// then counted at the allowance.
    /// </summary>
    internal const int NumeralBound = 16;

    /// <summary>The references the walk found, in walk order.</summary>
    internal IReadOnlyList<PageReferenceEntry> Entries => _walk;

    /// <summary>Adds the reference the walk found, once per run instance.</summary>
    internal void Add(TextRun run, ElementSite site)
    {
        if (_byRun.ContainsKey(run)) return;
        var entry = new PageReferenceEntry(run, site);
        _byRun.Add(run, entry);
        _walk.Add(entry);
    }

    /// <summary>
    /// The entry of <paramref name="run"/>. A reference the walk did not find, one a renderer of
    /// the caller's holds, gets an entry that is never resolved and that the save refuses after
    /// the layout.
    /// </summary>
    internal PageReferenceEntry EntryFor(TextRun run)
    {
        if (_byRun.TryGetValue(run, out var entry)) return entry;
        entry = new PageReferenceEntry(run, new ElementSite(LayoutElementKind.Custom, 0)) { OnDemand = true };
        _byRun.Add(run, entry);
        return entry;
    }

    // ── IPlanDependent ───────────────────────────────────────────────────────

    public bool Resolve(PaginationPlan plan)
    {
        var changed = false;
        foreach (var entry in _walk)
        {
            // A name the plan does not hold is an anchor whose element was never placed. The label
            // stays as it was; RefuseUnplaced names the anchor once the drawing pass is over.
            if (!plan.TryGetAnchorPage(entry.Anchor, out var page)) continue;

            // What {page} prints: control white space in a prefix is a space.
            var label = LayoutText.ControlSpacesToSpaces(plan.LabelOf(page));
            if (string.Equals(entry.Label, label, StringComparison.Ordinal)) continue;
            entry.SetLabel(label);
            changed = true;
        }

        if (changed) Epoch++;
        _states.Add(Snapshot());
        return changed;
    }

    public string TargetName => _walk.Count == 0 ? "" : _walk[0].Anchor;

    public string ResolvedText => _walk.Count == 0 ? "" : _walk[0].Label;

    private string[] Snapshot()
    {
        var state = new string[_walk.Count];
        for (var i = 0; i < state.Length; i++) state[i] = _walk[i].Label;
        return state;
    }

    // ── Cycles ───────────────────────────────────────────────────────────────

    /// <summary>The strings of each run, in order. Read by tests.</summary>
    internal IReadOnlyList<string[]> States => _states;

    /// <summary>
    /// The earlier run, counting from zero, whose strings the last run's strings equal, among runs
    /// 1 to k-2, or -1. The layout is a function of the strings, so the sequence repeats from
    /// there. The strings are compared as a vector of strings, never joined: "11" and "2" are not
    /// "1" and "12".
    /// </summary>
    internal int RepeatedRun() => RepeatedRun(_states);

    /// <summary>
    /// <see cref="RepeatedRun()"/> for the strings of each run in turn. Two runs are the same state
    /// when every string equals the one at its position.
    /// </summary>
    internal static int RepeatedRun(IReadOnlyList<string[]> states)
    {
        var last = states.Count - 1;
        for (var j = 0; j < last - 1; j++)
            if (states[j].AsSpan().SequenceEqual(states[last]))
                return j;
        return -1;
    }

    /// <summary>
    /// The message for a plan that does not settle: each reference whose strings changed in the
    /// last two runs, by site and anchor, with the strings it read in each run.
    /// </summary>
    internal string DescribeUnstable(string cause)
    {
        const int named = 3;
        var parts = new List<string>(named);
        var unstable = 0;
        var last = _states.Count - 1;
        for (var i = 0; i < _walk.Count; i++)
        {
            if (last < 1 || string.Equals(_states[last][i], _states[last - 1][i], StringComparison.Ordinal))
                continue;

            if (++unstable > named) continue;
            var history = string.Join(", ", _states.Select(s => $"'{s[i]}'"));
            parts.Add($"the page reference to '{_walk[i].Anchor}' on {_walk[i].Site} read {history}");
        }

        var more = unstable > named ? $", and {(unstable - named).ToString(CultureInfo.InvariantCulture)} more" : "";
        var detail = parts.Count > 0 ? ": " + string.Join("; ", parts) + more : "";
        return $"Pagination did not settle: {cause}{detail}.";
    }

    /// <summary>
    /// Throws for the first reference of the last measuring run whose label is empty or only white
    /// space, which would print nothing, naming its anchor.
    /// </summary>
    internal void RefuseBlankLabels()
    {
        foreach (var entry in _walk)
            if (string.IsNullOrWhiteSpace(entry.Label))
                throw new InvalidOperationException(
                    $"The page reference to the anchor '{entry.Anchor}' on {entry.Site} would print an empty " +
                    "label, because the page label of the anchor's page has no digits and an empty or " +
                    "white-space prefix, and a reference that prints nothing is a link with nothing to click. " +
                    "Give the page label range a prefix or a numbering style. The layout has already run, " +
                    "so this document cannot be saved again; create a new Document.");
    }

    /// <summary>
    /// Throws for the first reference no resolve gave a label, a reference a renderer of the
    /// caller's holds, naming its anchor.
    /// </summary>
    internal void RefuseUnresolved()
    {
        foreach (var entry in _byRun.Values)
            if (entry.OnDemand)
                throw new InvalidOperationException(
                    $"The page reference to the anchor '{entry.Anchor}' was laid out by a renderer the document " +
                    "did not find it in, so its label was never resolved against the pagination. " +
                    "Add the element that holds it with Document.Add. The layout has already run, so " +
                    "this document cannot be saved again; create a new Document.");
    }

    // ── Cells ────────────────────────────────────────────────────────────────

    /// <summary>
    /// What a run cell that holds a reference is laid out from now: its runs with each reference
    /// replaced by its label. Rebuilt only for a label that changed, and kept as the same object
    /// while the labels are the ones it read.
    /// </summary>
    internal CellView ViewOf(Cell cell)
    {
        if (_cells is not null && _cells.TryGetValue(cell, out var view) && view.Epoch == Epoch)
            return view;

        var runs = cell.Runs!;
        var effective = new List<TextRun>(runs.Count);
        var labels = new List<string>();
        foreach (var run in runs)
        {
            if (!run.IsReference)
            {
                effective.Add(run);
                continue;
            }

            var entry = EntryFor(run);
            labels.Add(entry.Label);
            effective.Add(entry.Effective);
        }

        _cells ??= new Dictionary<Cell, CellView>(ReferenceEqualityComparer.Instance);
        if (_cells.TryGetValue(cell, out view) && view.Labels.AsSpan().SequenceEqual(labels.ToArray()))
        {
            view.Epoch = Epoch;
            return view;
        }

        view = new CellView(new CellRunText(effective), TextRunList.Concat(effective), [.. labels], Epoch);
        _cells[cell] = view;
        return view;
    }
}

/// <summary>What the renderers that lay out a page reference ask the layout in progress.</summary>
internal static class PageReferences
{
    /// <summary>
    /// The table of the layout in progress. Outside a layout there is none, and a reference has no
    /// label to read: the caller laid out a renderer on its own.
    /// </summary>
    internal static PageReferenceTable CurrentTable(string anchor)
    {
        var run = ListLayoutRun.Current
            ?? throw new InvalidOperationException(
                $"The page reference to the anchor '{anchor}' was laid out outside a save, so there is no " +
                "pagination to take its label from. Add the element that holds it to a Document and save.");
        return run.References ??= new PageReferenceTable();
    }

    internal static bool Holds(IReadOnlyList<TextRun>? runs)
    {
        if (runs is null) return false;
        for (var i = 0; i < runs.Count; i++)
            if (runs[i].IsReference) return true;
        return false;
    }

    /// <summary>The number of reference runs in <paramref name="runs"/>.</summary>
    internal static int Count(IReadOnlyList<TextRun>? runs)
    {
        var count = 0;
        if (runs is null) return 0;
        for (var i = 0; i < runs.Count; i++)
            if (runs[i].IsReference) count++;
        return count;
    }

    /// <summary>
    /// <paramref name="runs"/> with each reference replaced by its label run, and
    /// <paramref name="reads"/> holding the strings that were read. The runs themselves, and
    /// <paramref name="reads"/> left as it was, when none is a reference, so a document without
    /// references allocates nothing here.
    /// </summary>
    internal static IReadOnlyList<TextRun> Effective(IReadOnlyList<TextRun> runs, ref ReferenceReads? reads)
    {
        var first = -1;
        for (var i = 0; i < runs.Count; i++)
            if (runs[i].IsReference)
            {
                first = i;
                break;
            }

        if (first < 0) return runs;

        var table = CurrentTable(runs[first].ReferenceAnchor!);
        reads ??= new ReferenceReads(table);
        var result = new List<TextRun>(runs.Count);
        for (var i = 0; i < runs.Count; i++)
        {
            if (!runs[i].IsReference)
            {
                result.Add(runs[i]);
                continue;
            }

            var entry = table.EntryFor(runs[i]);
            reads.Add(runs[i], entry.Label);
            result.Add(entry.Effective);
        }

        return result;
    }
}
