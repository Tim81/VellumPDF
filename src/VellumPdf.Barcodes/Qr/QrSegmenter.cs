// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Barcodes.Qr;

/// <summary>One contiguous run of <see cref="Mode"/>-encoded content within the original string.</summary>
/// <param name="Mode">The mode this segment is encoded in.</param>
/// <param name="CharStart">The UTF-16 index into the original string where the segment begins.</param>
/// <param name="CharLength">The number of UTF-16 code units the segment spans in the original string.</param>
/// <param name="RuneCount">The number of Unicode scalar values (characters, for the character count indicator) the segment spans.</param>
internal readonly record struct QrSegment(QrSegmentMode Mode, int CharStart, int CharLength, int RuneCount);

/// <summary>
/// Splits a string into minimal-cost numeric/alphanumeric/byte/Kanji segments. A per-rune dynamic
/// program picks, for every position, the cheapest way to either continue the current mode's run
/// or start a new one, since splitting a maximal same-mode run can never help (it only adds
/// another mode/count-indicator header) — so this per-character choice is already globally optimal
/// for the header-and-data cost model used here.
/// </summary>
internal static class QrSegmenter
{
    /// <summary>The number of modes the DP considers; must match <see cref="QrSegmentMode"/>'s member count.</summary>
    private const int ModeCount = 4;

    /// <summary>
    /// Segments <paramref name="content"/> for a specific symbol configuration.
    /// </summary>
    /// <param name="content">The text to segment.</param>
    /// <param name="headerBits">The mode-indicator-plus-character-count-indicator bit cost for a segment in the given mode.</param>
    /// <param name="byteEncoding">The encoding used to size (and, later, encode) byte-mode segments.</param>
    /// <param name="allowAlphanumeric">Whether alphanumeric mode is available (false restricts eligible alphanumeric characters to byte/numeric mode).</param>
    /// <param name="allowByte">Whether byte mode is available.</param>
    /// <param name="allowKanji">
    /// Whether Kanji mode (ISO/IEC 18004 §7.4.6) is available. Only offered on the plain-text
    /// path: GS1 element-string content restricts itself to numeric/byte (see
    /// <c>QrEncoder.PrepareGs1ElementStringContent</c>), and byte-array content bypasses
    /// segmentation entirely.
    /// </param>
    /// <exception cref="FormatException">A character is not representable in any allowed mode.</exception>
    internal static IReadOnlyList<QrSegment> Segment(
        string content,
        Func<QrSegmentMode, int> headerBits,
        Encoding byteEncoding,
        bool allowAlphanumeric,
        bool allowByte,
        bool allowKanji)
    {
        if (content.Length == 0) return [];

        var runes = new List<Rune>();
        var runeCharStart = new List<int>();
        foreach (var (rune, charStart) in EnumerateRunesWithIndex(content))
        {
            runes.Add(rune);
            runeCharStart.Add(charStart);
        }

        var n = runes.Count;
        // Array order must match the QrSegmentMode member order: the DP below indexes the
        // second dimension with (int)mode.
        var modes = new[] { QrSegmentMode.Numeric, QrSegmentMode.Alphanumeric, QrSegmentMode.Byte, QrSegmentMode.Kanji };

        // Per-rune eligibility and (for byte mode) bit cost, since byte width varies with the
        // encoding and the code point (e.g. UTF-8 multi-byte sequences).
        var eligible = new bool[n, ModeCount];
        var byteRuneBits = new int[n];
        Span<char> chars = stackalloc char[2];
        for (var i = 0; i < n; i++)
        {
            var rune = runes[i];
            eligible[i, (int)QrSegmentMode.Numeric] = rune.Value is >= '0' and <= '9';
            eligible[i, (int)QrSegmentMode.Alphanumeric] = allowAlphanumeric && rune.IsBmp && QrTables.AlphanumericValue((char)rune.Value) >= 0;
            eligible[i, (int)QrSegmentMode.Kanji] = allowKanji && ShiftJisTable.TryGetShiftJis(rune.Value, out _);

            if (allowByte)
            {
                var charCount = rune.EncodeToUtf16(chars);
                byteRuneBits[i] = 8 * byteEncoding.GetByteCount(chars[..charCount]);
                eligible[i, (int)QrSegmentMode.Byte] = true;
            }

            if (!eligible[i, 0] && !eligible[i, 1] && !eligible[i, 2] && !eligible[i, 3])
                throw new FormatException($"Character '{rune}' at position {runeCharStart[i]} cannot be encoded in any mode available to this symbol.");
        }

        // dp[i, m] = minimum bits to encode runes[0..i] such that rune i is encoded in mode m;
        // isStart[i, m] = whether rune i begins a new segment in mode m (vs. continuing rune i-1's run).
        var dp = new int[n, ModeCount];
        var runLength = new int[n, ModeCount];
        var isStart = new bool[n, ModeCount];

        for (var i = 0; i < n; i++)
        {
            for (var mi = 0; mi < ModeCount; mi++)
            {
                var mode = modes[mi];
                if (!eligible[i, mi])
                {
                    dp[i, mi] = int.MaxValue;
                    continue;
                }

                var best = int.MaxValue;
                var bestRunLength = 1;
                var bestIsStart = true;

                if (i > 0 && eligible[i - 1, mi] && dp[i - 1, mi] != int.MaxValue)
                {
                    var continued = dp[i - 1, mi] + RuneBitCost(mode, byteRuneBits[i], runLength[i - 1, mi]);
                    if (continued < best)
                    {
                        best = continued;
                        bestRunLength = runLength[i - 1, mi] + 1;
                        bestIsStart = false;
                    }
                }

                var priorBest = i == 0 ? 0 : MinReachable(dp, i - 1);
                if (priorBest != int.MaxValue)
                {
                    var started = priorBest + headerBits(mode) + RuneBitCost(mode, byteRuneBits[i], 0);
                    if (started < best)
                    {
                        best = started;
                        bestRunLength = 1;
                        bestIsStart = true;
                    }
                }

                dp[i, mi] = best;
                runLength[i, mi] = bestRunLength;
                isStart[i, mi] = bestIsStart;
            }
        }

        // Backtrace from the cheapest mode at the last rune.
        var segments = new List<QrSegment>();
        var end = n - 1;
        var mode2 = ArgMin(dp, end);
        while (end >= 0)
        {
            var start = end;
            while (!isStart[start, mode2]) start--;

            var charStart = runeCharStart[start];
            var charEnd = end + 1 < n ? runeCharStart[end + 1] : content.Length;
            segments.Add(new QrSegment(modes[mode2], charStart, charEnd - charStart, end - start + 1));

            end = start - 1;
            if (end >= 0) mode2 = ArgMin(dp, end);
        }

        segments.Reverse();
        return segments;
    }

    private static int RuneBitCost(QrSegmentMode mode, int byteBits, int positionInRun) => mode switch
    {
        QrSegmentMode.Numeric => positionInRun % 3 == 0 ? 4 : 3,
        QrSegmentMode.Alphanumeric => positionInRun % 2 == 0 ? 6 : 5,
        QrSegmentMode.Byte => byteBits,
        QrSegmentMode.Kanji => 13, // §7.4.6: every Kanji character is a fixed 13-bit codeword.
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null),
    };

    private static int MinReachable(int[,] dp, int i)
    {
        var min = int.MaxValue;
        for (var mi = 0; mi < ModeCount; mi++)
            if (dp[i, mi] < min) min = dp[i, mi];
        return min;
    }

    private static int ArgMin(int[,] dp, int i)
    {
        var best = 0;
        for (var mi = 1; mi < ModeCount; mi++)
            if (dp[i, mi] < dp[i, best]) best = mi;
        return best;
    }

    private static IEnumerable<(Rune Rune, int CharStart)> EnumerateRunesWithIndex(string content)
    {
        var charIndex = 0;
        while (charIndex < content.Length)
        {
            var rune = Rune.GetRuneAt(content, charIndex);
            yield return (rune, charIndex);
            charIndex += rune.Utf16SequenceLength;
        }
    }
}
