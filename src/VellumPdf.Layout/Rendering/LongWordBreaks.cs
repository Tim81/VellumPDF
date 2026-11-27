// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Globalization;
using System.Text;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Where a word wider than its line is cut. One decision for every wrap in the library: the
/// paragraph, heading and list-item wrap, and the wrap of a table cell given as a string or as
/// runs. The band of a header or footer does not wrap, so it does not call this.
/// </summary>
/// <remarks>
/// A word wider than the line is cut after the last solidus, reverse solidus or hyphen-minus that
/// leaves the line no wider than its width, so a web address or a path ends a line at a segment.
/// No glyph is added at the cut. A word that fits is never cut, and a word with no such place that
/// fits is cut at the character that does not fit, as every wider word was before.
/// <para>
/// The places come from UAX #14 (revision 55). Its default rules allow a break after a solidus and
/// after a hyphen-minus (LB31) unless an earlier rule forbids it. The specification advises a
/// layout system not to use the break after a solidus unless the next opportunity is a minimal
/// distance away; here it is used only for a word that cannot fit its line, so it is never used
/// for a word the line holds. The reverse solidus is this library's own tailoring, read as a
/// solidus because that is how a path is written; UAX #14 gives it the class PR. The characters
/// <c>=</c>, <c>&amp;</c>, <c>_</c> and <c>#</c> are AL and <c>.</c> is IS, which LB28 and LB29
/// keep joined to the letter after them, so none is a place. <c>?</c> is EX and LB31 would allow a break after it, but only the solidus
/// carries the guidance for web addresses, so it is left out too.
/// </para>
/// <para>
/// A place is preferred when no rule forbids it. LB25 forbids a break before a digit after a
/// hyphen-minus, and after a solidus that follows a digit, as in <c>1/2</c>. The specification
/// suggests suppressing the second inside a web address, so such a place is used when no preferred
/// place fits on the line, and before a cut at the character.
/// </para>
/// <para>
/// The test errs towards fewer places. A place needs a letter or digit after the character, and a
/// letter or digit before a hyphen-minus (LB20a holds a hyphen that starts a word, and LB21 a
/// second hyphen). Hebrew letters on either side are left out (LB21a, LB21b), and so are kana and
/// the symbols of the CJK block, which include nonstarters (LB21).
/// </para>
/// </remarks>
internal static class LongWordBreaks
{
    private const byte Preferred = 1;
    private const byte Forbidden = 2;

    /// <summary>
    /// The characters of one word and the width of each, with the cut decision made from them. The
    /// buffers are rented, so a word of any length is measured and walked without a string or an
    /// array allocated for it, which the table's count of lines at every candidate width relies on.
    /// </summary>
    internal sealed class Word : IDisposable
    {
        private Rune[] _runes;
        private double[] _widths;
        private byte[]? _tiers;

        internal Word(int capacity)
        {
            _runes = ArrayPool<Rune>.Shared.Rent(Math.Max(capacity, 1));
            _widths = ArrayPool<double>.Shared.Rent(_runes.Length);
        }

        /// <summary>The number of characters added.</summary>
        internal int Count { get; private set; }

        internal ReadOnlySpan<Rune> Runes => _runes.AsSpan(0, Count);

        internal ReadOnlySpan<double> Widths => _widths.AsSpan(0, Count);

        internal void Add(Rune rune, double width)
        {
            if (Count == _runes.Length)
            {
                var runes = ArrayPool<Rune>.Shared.Rent(Count * 2);
                var widths = ArrayPool<double>.Shared.Rent(runes.Length);
                _runes.AsSpan(0, Count).CopyTo(runes);
                _widths.AsSpan(0, Count).CopyTo(widths);
                ArrayPool<Rune>.Shared.Return(_runes);
                ArrayPool<double>.Shared.Return(_widths);
                _runes = runes;
                _widths = widths;
            }

            _runes[Count] = rune;
            _widths[Count] = width;
            Count++;
        }

        /// <summary>
        /// The number of lines the word takes when each character is placed at its width and a line
        /// takes characters while they fit. A line's end is the last place that leaves it no wider
        /// than <paramref name="maxWidth"/>, and the character that does not fit when there is
        /// none. The first character of a line is always placed, and a word with no characters has
        /// no line. <paramref name="from"/> and <paramref name="below"/> narrow to the interval of
        /// line widths over which every comparison made here comes out the same, as the wrap of a
        /// table cell records it. <paramref name="starts"/>, when given, receives the index at
        /// which each line begins.
        /// </summary>
        internal int Lines(double maxWidth, ref double from, ref double below, List<int>? starts)
        {
            if (Count == 0) return 0;

            var tiers = Analyse();
            var lines = 1;
            var start = 0;
            starts?.Add(0);
            while (true)
            {
                var overflowAt = -1;
                var lineW = _widths[start];
                for (var i = start + 1; i < Count; i++)
                {
                    var grown = lineW + _widths[i];
                    if (grown > maxWidth)
                    {
                        below = Math.Min(below, grown);
                        overflowAt = i;
                        break;
                    }

                    from = Math.Max(from, grown);
                    lineW = grown;
                }

                if (overflowAt < 0) return lines;

                start = LineEnd(tiers, start, overflowAt);
                starts?.Add(start);
                lines++;
            }
        }

        // The tiers of the places of the word, or empty when it has none.
        private ReadOnlySpan<byte> Analyse()
        {
            var word = Runes;
            var any = false;
            for (var i = 1; i < word.Length && !any; i++)
                any = word[i - 1].Value is '/' or '\\' or '-';
            if (!any) return default;

            _tiers ??= ArrayPool<byte>.Shared.Rent(_runes.Length);
            if (_tiers.Length < Count)
            {
                ArrayPool<byte>.Shared.Return(_tiers);
                _tiers = ArrayPool<byte>.Shared.Rent(_runes.Length);
            }

            var tiers = _tiers.AsSpan(0, Count);
            tiers.Clear();
            var found = false;
            for (var i = 1; i < word.Length; i++)
            {
                tiers[i] = TierBefore(word, i);
                found |= tiers[i] != 0;
            }

            return found ? tiers : default;
        }

        public void Dispose()
        {
            ArrayPool<Rune>.Shared.Return(_runes);
            ArrayPool<double>.Shared.Return(_widths);
            if (_tiers is not null) ArrayPool<byte>.Shared.Return(_tiers);
            _runes = [];
            _widths = [];
            _tiers = null;
        }
    }

    /// <summary>
    /// Where the line that starts at <paramref name="start"/> ends when the character at
    /// <paramref name="overflowAt"/> is the first that does not fit: the last preferred place after
    /// <paramref name="start"/> and no later than <paramref name="overflowAt"/>, else the last
    /// place LB25 forbids, else <paramref name="overflowAt"/>, a cut at the glyph. The result is
    /// always after <paramref name="start"/>, so every line holds a character.
    /// <paramref name="tiers"/> is empty for a word with no place; otherwise it holds, for the
    /// character each break falls before, 0 for no place, 1 for a preferred place and 2 for one
    /// that LB25 forbids.
    /// </summary>
    internal static int LineEnd(ReadOnlySpan<byte> tiers, int start, int overflowAt)
    {
        if (tiers.IsEmpty) return overflowAt;

        var forbidden = -1;
        for (var p = overflowAt; p > start; p--)
        {
            var tier = tiers[p];
            if (tier == Preferred) return p;
            if (tier == Forbidden && forbidden < 0) forbidden = p;
        }

        return forbidden >= 0 ? forbidden : overflowAt;
    }

    /// <summary>
    /// The tier of each break place of a word, by the index of the character the break falls
    /// before: 0 for no place, 1 for a preferred place and 2 for one that LB25 forbids. For tests.
    /// </summary>
    internal static byte[]? Tiers(ReadOnlySpan<Rune> word)
    {
        var tiers = new byte[word.Length];
        var found = false;
        for (var i = 1; i < word.Length; i++)
        {
            tiers[i] = TierBefore(word, i);
            found |= tiers[i] != 0;
        }

        return found ? tiers : null;
    }

    // The tier of a break before word[i], for 0 < i < word.Length.
    private static byte TierBefore(ReadOnlySpan<Rune> word, int i)
    {
        var before = word[i - 1].Value;
        if (before is not ('/' or '\\' or '-')) return 0;

        // A letter or digit after the character is one that no rule keeps joined to it: LB13 and
        // LB21 hold closing marks, a full stop, a second solidus or hyphen and nonstarters to
        // what precedes them. Hebrew is left out for LB21b, and kana and the CJK symbols for LB21.
        var next = word[i];
        if (!IsPlainLetterOrDigit(next)) return 0;

        if (before == '-')
        {
            // LB20a holds a hyphen that starts a word, LB21 one that follows another hyphen, and
            // LB21a a hyphen between Hebrew and other text. A letter or digit before it rules
            // all of them out.
            if (i < 2 || !IsPlainLetterOrDigit(word[i - 2])) return 0;
            // LB25: HY x NU.
            return Rune.GetUnicodeCategory(next) == UnicodeCategory.DecimalDigitNumber ? Forbidden : Preferred;
        }

        if (Rune.GetUnicodeCategory(next) != UnicodeCategory.DecimalDigitNumber) return Preferred;

        // LB25: NU (SY | IS)* x NU. The SY and IS characters that can sit in a path or an address
        // are the solidus, the reverse solidus, the full stop, the comma, the colon and the
        // semicolon.
        var j = i - 1;
        while (j >= 0 && word[j].Value is '/' or '\\' or '.' or ',' or ':' or ';') j--;
        return j >= 0 && IsNumeric(word[j]) ? Forbidden : Preferred;
    }

    // A letter or a decimal digit that is not Hebrew, kana or one of the CJK symbols.
    private static bool IsPlainLetterOrDigit(Rune rune)
    {
        switch (Rune.GetUnicodeCategory(rune))
        {
            case UnicodeCategory.UppercaseLetter:
            case UnicodeCategory.LowercaseLetter:
            case UnicodeCategory.TitlecaseLetter:
            case UnicodeCategory.OtherLetter:
            case UnicodeCategory.DecimalDigitNumber:
                break;
            default:
                return false;
        }

        var v = rune.Value;
        return !(v is >= 0x0590 and <= 0x05FF || v is >= 0xFB1D and <= 0xFB4F || v is >= 0x3000 and <= 0x30FF);
    }

    // NU of UAX #14: the decimal digits, and the Arabic decimal and thousands separators.
    private static bool IsNumeric(Rune rune) =>
        rune.Value is 0x066B or 0x066C || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber;
}
