// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Canvas;

/// <summary>
/// The glyph-0 writes of one canvas: at most <see cref="MaxEntries"/> distinct (character, font
/// resource) entries in order of first occurrence, and a count of the occurrences that did not fit.
/// Created on the first glyph 0, so a canvas without one allocates nothing for it.
/// </summary>
internal sealed class NotdefGlyphTally
{
    /// <summary>
    /// The most entries held per canvas and per document. It is the cap the layout channel uses,
    /// not a figure any standard gives; it bounds memory against text that produces a distinct
    /// entry per character.
    /// </summary>
    internal const int MaxEntries = 1000;

    // A code point that cannot be a Unicode scalar stands for "character not known".
    internal const int UnknownCodePoint = 0x110000;

    private readonly List<NotdefGlyphWarning> _entries = [];
    private readonly Dictionary<(int CodePoint, string? Resource), int> _index = [];

    internal NotdefGlyphTally()
    {
    }

    private NotdefGlyphTally(NotdefGlyphTally source)
    {
        foreach (var e in source._entries)
        {
            _index[(CodePointOf(e.Character), e.FontResourceName)] = _entries.Count;
            _entries.Add(new NotdefGlyphWarning(e.Character, e.FontResourceName, e.FontName, e.Count));
        }
        Dropped = source.Dropped;
        Total = source.Total;
    }

    internal IReadOnlyList<NotdefGlyphWarning> Entries => _entries;

    /// <summary>Occurrences not stored because the entry would have been past the cap.</summary>
    internal int Dropped { get; private set; }

    /// <summary>Every occurrence, stored or dropped. Changes whenever anything is added.</summary>
    internal int Total { get; private set; }

    internal static int CodePointOf(Rune? character) => character?.Value ?? UnknownCodePoint;

    internal void Add(Rune? character, string? resource, string? fontName, int count = 1)
    {
        Total = Saturate(Total, count);
        var key = (CodePointOf(character), resource);
        if (_index.TryGetValue(key, out var at))
        {
            var entry = _entries[at];
            entry.Count = Saturate(entry.Count, count);
            return;
        }

        if (_entries.Count >= MaxEntries)
        {
            Dropped = Saturate(Dropped, count);
            return;
        }

        _index[key] = _entries.Count;
        _entries.Add(new NotdefGlyphWarning(character, resource, fontName, count));
    }

    /// <summary>A copy that later writes to this tally do not change.</summary>
    internal NotdefGlyphTally Snapshot() => new(this);

    internal static int Saturate(int current, int add) =>
        add > int.MaxValue - current ? int.MaxValue : current + add;
}
