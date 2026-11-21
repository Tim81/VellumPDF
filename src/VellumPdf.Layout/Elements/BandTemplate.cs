// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Elements;

/// <summary>
/// The substitutions a band template knows, one per entry of <see cref="BandTokens"/>.
/// </summary>
internal enum BandToken
{
    /// <summary><c>{page}</c>: the number of the page the band is drawn on.</summary>
    Page = 0,

    /// <summary><c>{pages}</c>: the total page count.</summary>
    Pages = 1,
}

/// <summary>
/// The one table of tokens a band template substitutes. A new token is a new
/// <see cref="BandToken"/> member, a row here and a value in <see cref="BandText.Reset"/>; nothing
/// else scans a template.
/// </summary>
internal static class BandTokens
{
    /// <summary>
    /// Token text, matched ordinally and case-sensitively. No entry may be a prefix of another, so
    /// that at most one entry matches at a given position: <c>{page}</c> is not a prefix of
    /// <c>{pages}</c> because its sixth character is a brace and the other's is <c>s</c>.
    /// </summary>
    private static readonly (string Text, BandToken Kind)[] Table =
    [
        ("{page}", BandToken.Page),
        ("{pages}", BandToken.Pages),
    ];

    internal static int Count => Table.Length;

    internal static ReadOnlySpan<(string Text, BandToken Kind)> Entries => Table;
}

/// <summary>
/// A band template scanned once into literal runs and tokens, so that resolving it for a page needs
/// no pass over the template.
/// </summary>
/// <remarks>
/// One left-to-right scan gives the same text as replacing <c>{page}</c> and then <c>{pages}</c>
/// with ordinal comparison, which is what <see cref="RunningBand.Resolve"/> does. The two tokens
/// cannot overlap, because each holds one brace, at its start, and neither is a prefix of the
/// other. A replacement is made of digits and a minus sign, so it can neither create nor destroy an
/// occurrence of the other token, which is made of braces and letters. Immutable once built, so one
/// instance may serve every save and every thread.
/// </remarks>
internal sealed class BandTemplate
{
    internal readonly string Source;

    // A literal run is (start, length) into Source with kind -1; a token is its BandToken value.
    internal readonly int[] Starts;
    internal readonly int[] Lengths;
    internal readonly sbyte[] Kinds;

    /// <summary>Characters of <see cref="Source"/> outside every token.</summary>
    internal readonly int LiteralLength;

    /// <summary>How many times each <see cref="BandToken"/> occurs, indexed by its value.</summary>
    internal readonly int[] TokenCounts;

    private BandTemplate(string source, int[] starts, int[] lengths, sbyte[] kinds, int literalLength, int[] counts)
    {
        Source = source;
        Starts = starts;
        Lengths = lengths;
        Kinds = kinds;
        LiteralLength = literalLength;
        TokenCounts = counts;
    }

    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> is <see langword="null"/>. <c>ParamName</c> is <c>template</c>,
    /// as for <see cref="RunningBand.Resolve"/>.
    /// </exception>
    internal static BandTemplate Compile(string template)
    {
        if (template is null) throw new ArgumentNullException("template", "The band's template is null.");

        var starts = new List<int>();
        var lengths = new List<int>();
        var kinds = new List<sbyte>();
        var counts = new int[BandTokens.Count];
        var literal = 0;
        var runStart = 0;
        var pos = 0;

        while (true)
        {
            var brace = template.IndexOf('{', pos);
            if (brace < 0) break;

            var matched = false;
            foreach (var (text, kind) in BandTokens.Entries)
            {
                if (!template.AsSpan(brace).StartsWith(text, StringComparison.Ordinal)) continue;

                if (brace > runStart)
                {
                    starts.Add(runStart);
                    lengths.Add(brace - runStart);
                    kinds.Add(-1);
                    literal += brace - runStart;
                }

                starts.Add(brace);
                lengths.Add(text.Length);
                kinds.Add((sbyte)kind);
                counts[(int)kind]++;
                pos = runStart = brace + text.Length;
                matched = true;
                break;
            }

            if (!matched) pos = brace + 1;
        }

        if (template.Length > runStart)
        {
            starts.Add(runStart);
            lengths.Add(template.Length - runStart);
            kinds.Add(-1);
            literal += template.Length - runStart;
        }

        return new BandTemplate(template, [.. starts], [.. lengths], [.. kinds], literal, counts);
    }
}

/// <summary>
/// One band's template resolved for one page, produced on demand: a caller that reads the first
/// <c>n</c> characters causes about <c>n</c> to be produced, whatever the template's length.
/// </summary>
/// <remarks>
/// One instance serves every page of a save: <see cref="Reset"/> points it at the next page and
/// keeps its buffer, so a page costs the characters read and nothing else. Not thread-safe.
/// </remarks>
internal sealed class BandText
{
    private readonly string[] _values = new string[BandTokens.Count];
    private char[] _buffer = new char[64];
    private int _produced;
    private int _segment;
    private int _offset;

    internal BandText(BandTemplate template) => Template = template;

    internal BandTemplate Template { get; }

    /// <summary>Length of the whole resolved text, computed without producing it.</summary>
    internal int Length { get; private set; }

    /// <summary>Characters produced for the current page so far.</summary>
    internal int Produced => _produced;

    internal void Reset(int pageNumber, int totalPages)
    {
        _values[(int)BandToken.Page] = pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _values[(int)BandToken.Pages] = totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture);

        long length = Template.LiteralLength;
        for (var k = 0; k < _values.Length; k++)
            length += (long)Template.TokenCounts[k] * _values[k].Length;
        // Defensive: a string over 1,073,741,823 characters throws OutOfMemoryException first, a
        // token is at least 6 characters and a number is at most 11 characters, so this cannot overflow.
        Length = checked((int)length);

        _produced = 0;
        _segment = 0;
        _offset = 0;
    }

    internal char this[int index]
    {
        get
        {
            Produce(index + 1);
            return _buffer[index];
        }
    }

    /// <summary>The <paramref name="length"/> characters from <paramref name="start"/>.</summary>
    internal string Slice(int start, int length)
    {
        Produce(start + length);
        return new string(_buffer, start, length);
    }

    /// <summary>The first <paramref name="length"/> characters.</summary>
    internal string Prefix(int length) => Slice(0, length);

    // Extends the buffer to hold `count` characters, or the whole text when that is shorter.
    private void Produce(int count)
    {
        if (count > Length) count = Length;
        if (_produced >= count) return;

        if (_buffer.Length < count) Array.Resize(ref _buffer, Math.Max(count, _buffer.Length * 2));

        var template = Template;
        while (_produced < count)
        {
            // A token's run in the template is its text; its run in the output is the value.
            var kind = template.Kinds[_segment];
            var runLength = kind < 0 ? template.Lengths[_segment] : _values[kind].Length;
            var take = Math.Min(runLength - _offset, count - _produced);

            if (kind < 0)
                template.Source.CopyTo(template.Starts[_segment] + _offset, _buffer, _produced, take);
            else
                _values[kind].CopyTo(_offset, _buffer, _produced, take);

            _produced += take;
            _offset += take;
            if (_offset == runLength)
            {
                _segment++;
                _offset = 0;
            }
        }
    }
}

