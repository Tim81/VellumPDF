// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Globalization;
using System.Text;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Where a word wider than its line is cut, when the document asks for it
/// (<see cref="VellumPdf.Layout.Document.BreakLongWordsAtPlaces"/>). One decision for every wrap in
/// the library: the paragraph, heading and list-item wrap, and the wrap of a table cell given as a
/// string or as runs. The band of a header or footer does not wrap, so it does not call this.
/// </summary>
/// <remarks>
/// A word wider than the line is cut after the last solidus, reverse solidus or hyphen-minus that
/// leaves the line no wider than its width, so a web address or a path ends a line at a segment.
/// No glyph is added at the cut. A word that fits is never cut, and a word with no such place that
/// fits is cut at the character that does not fit, as every wider word was before. With the setting
/// off, which is the default, none of this runs and every word is cut at the character.
/// <para>
/// The places come from UAX #14 (revision 55). Its default rules allow a break after a solidus and
/// after a hyphen-minus (LB31) unless an earlier rule forbids it. The specification advises a
/// layout system not to use the break after a solidus unless the next opportunity is a minimal
/// distance away; here it is used only for a word that cannot fit its line, so it is never used
/// for a word the line holds. The reverse solidus is this library's own tailoring, read as a
/// solidus because that is how a path is written; UAX #14 gives it the class PR. The characters
/// <c>=</c>, <c>&amp;</c>, <c>_</c> and <c>#</c> are AL and <c>.</c> is IS. Before a letter, LB28
/// (AL x AL) and LB29 (IS x AL) keep them joined to it, and before a digit LB23 (AL x NU) and
/// LB25 (IS x NU) do, so none is a place. <c>?</c> is EX and LB31 would allow a break after it,
/// but only the solidus carries the guidance for web addresses, so it is left out too.
/// </para>
/// <para>
/// A place is preferred when no rule forbids it. LB25 forbids a break before a digit after a
/// hyphen-minus, and after a solidus that follows a digit, as in <c>1/2</c>. The specification
/// suggests suppressing the second inside a web address, so such a place is used when no preferred
/// place fits on the line, and before a cut at the character.
/// </para>
/// <para>
/// The test errs towards fewer places, and is not UAX #14 rule for rule. A place needs a letter
/// or digit after the character, which leaves out what LB13 (closing marks, EX, SY), LB15d (the
/// full stop and the other IS) and the nonstarters of LB21 keep joined to what precedes them. It
/// needs a letter or digit before a hyphen-minus too, which leaves out a hyphen that starts a
/// word (LB20a) and any hyphen that follows another; the second is a coarsening, because LB31
/// allows the break after <c>--</c> before a letter. A Hebrew letter after the character, or
/// before a hyphen-minus, is no place (LB21b holds SY x HL, LB21a holds HL HY x non-HL), which is
/// coarser than the rules where it is a hyphen-minus or a reverse solidus that precedes the
/// Hebrew letter. Kana, including the halfwidth and the small forms, and the symbols of the CJK
/// block are no place after the character, because LB21 holds the small kana and the other
/// nonstarters once they are resolved to NS. A Hebrew letter before a solidus is a place, as LB21b
/// allows.
/// </para>
/// </remarks>
internal static class LongWordBreaks
{
    private const byte Preferred = 1;
    private const byte Forbidden = 2;

    // The text of a rune, taken from a table for the characters below U+0100, so that measuring a
    // long word one character at a time allocates no string for most of them.
    private static readonly string[] s_latin1 = BuildLatin1();

    private static string[] BuildLatin1()
    {
        var table = new string[256];
        for (var i = 0; i < table.Length; i++) table[i] = ((char)i).ToString();
        return table;
    }

    internal static string RuneText(Rune rune) => rune.Value < s_latin1.Length ? s_latin1[rune.Value] : rune.ToString();

    // The characters a word is cut after: the only definition, read by the pre-filter and by the
    // tier test alike.
    private static bool IsPlaceCharacter(int value) => value is '/' or '\\' or '-';

    // True while a document that has BreakLongWordsAtPlaces set lays itself out, on the thread that
    // does it. Every wrap reads it, so a built-in renderer that a renderer of the caller's lays out
    // inside that layout reads the document's setting, and one laid out anywhere else reads false
    // and cuts at the glyph. Removed in 3.0 with the setting (#750).
    [ThreadStatic]
    private static bool s_enabled;

    /// <summary>Whether a word wider than its line is cut after a place on this thread now.</summary>
    internal static bool Enabled => s_enabled;

    /// <summary>
    /// Sets <see cref="Enabled"/> to <paramref name="on"/> until the returned scope is disposed,
    /// which restores the value before it.
    /// </summary>
    internal static PlaceCutScope PlaceCuts(bool on) => new(on);

    internal readonly struct PlaceCutScope : IDisposable
    {
        private readonly bool _previous;

        internal PlaceCutScope(bool on)
        {
            _previous = s_enabled;
            s_enabled = on;
        }

        public void Dispose() => s_enabled = _previous;
    }

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
        internal ReadOnlySpan<byte> Analyse()
        {
            var word = Runes;
            var any = false;
            for (var i = 1; i < word.Length && !any; i++)
                any = IsPlaceCharacter(word[i - 1].Value);
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
    /// before: 0 for no place, 1 for a preferred place and 2 for one that LB25 forbids. Null when
    /// the word has none. It reads the word through <see cref="Word.Analyse"/>, which the wrap
    /// reads too. For tests.
    /// </summary>
    internal static byte[]? Tiers(ReadOnlySpan<Rune> word)
    {
        using var scratch = new Word(word.Length);
        foreach (var rune in word) scratch.Add(rune, 0);
        var tiers = scratch.Analyse();
        return tiers.IsEmpty ? null : tiers.ToArray();
    }

    // The tier of a break before word[i], for 0 < i < word.Length.
    private static byte TierBefore(ReadOnlySpan<Rune> word, int i)
    {
        var before = word[i - 1].Value;
        if (!IsPlaceCharacter(before)) return 0;

        // A letter or digit after the character is one that no rule keeps joined to it: LB13 holds
        // closing marks, EX and a second solidus, LB15d the full stop and the other IS, and LB21 a
        // hyphen and the nonstarters, to what precedes them. Hebrew is left out for LB21b, and
        // kana and the CJK symbols for LB21.
        var next = word[i];
        if (!IsPlainLetterOrDigit(next)) return 0;

        if (before == '-')
        {
            // LB20a holds a hyphen that starts a word and LB21a one between Hebrew and other
            // text. A letter or digit before the hyphen rules both out, and also a hyphen that
            // follows another, which LB31 would allow: that part is a coarsening.
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

    // A letter or a decimal digit that is not Hebrew, kana (halfwidth and small forms included)
    // or one of the symbols of the CJK block.
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
        var hebrew = v is >= 0x0590 and <= 0x05FF or >= 0xFB1D and <= 0xFB4F;
        var kana = v is >= 0x3000 and <= 0x30FF or >= 0x31F0 and <= 0x31FF or >= 0xFF66 and <= 0xFF9F;
        return !hebrew && !kana;
    }

    // NU of UAX #14: the decimal digits, and the Arabic decimal and thousands separators.
    private static bool IsNumeric(Rune rune) =>
        rune.Value is 0x066B or 0x066C || Rune.GetUnicodeCategory(rune) == UnicodeCategory.DecimalDigitNumber;
}
