// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Aztec;

// The five-mode state machine and binary-shift routing below are an original design over the
// ISO/IEC 24778 clause 7.3.2 character tables (see AztecTables.cs): the tables and the shift/latch
// codes they define are unavoidably identical to any correct implementation (they are the
// specification), but the greedy segmentation heuristic that decides when to shift, latch or drop
// into binary is authored fresh for this package, modelled structurally on the latch/shift
// state-machine shape of DataMatrixHighLevelEncoder and Pdf417HighLevelEncoder. zint and zxing-cpp
// are used only as decode/cross-check oracles in this package's tests, never as source.

/// <summary>
/// Aztec Code high-level encoding (ISO/IEC 24778 clause 7.3.2): compacts a byte stream into a raw
/// bit stream, switching between the five character modes (<see cref="AztecMode.Upper"/>,
/// <see cref="AztecMode.Lower"/>, <see cref="AztecMode.Mixed"/>, <see cref="AztecMode.Punct"/>,
/// <see cref="AztecMode.Digit"/>) and dropping into binary shift for bytes none of the five modes
/// reach directly (0x00, the control range 0x0E-0x1A, and every byte 128-255). The returned bit
/// list is the message content only — bit-stuffing into codewords and error correction are applied
/// afterward by <c>AztecEncoder</c>, since both depend on the codeword size the chosen symbol uses.
/// </summary>
internal static class AztecHighLevelEncoder
{
    private static readonly AztecMode[] AllModes =
    [
        AztecMode.Upper, AztecMode.Lower, AztecMode.Mixed, AztecMode.Punct, AztecMode.Digit,
    ];

    /// <summary>Encodes <paramref name="content"/> as a flat sequence of message bits (MSB first per code), starting in <see cref="AztecMode.Upper"/>.</summary>
    internal static List<bool> Encode(ReadOnlySpan<byte> content)
    {
        var bits = new List<bool>(content.Length * 6);
        var mode = AztecMode.Upper;
        var i = 0;

        while (i < content.Length)
        {
            if (!IsTextRepresentable(content[i]))
            {
                var start = i;
                while (i < content.Length && !IsTextRepresentable(content[i])) i++;
                mode = EmitBinaryShift(bits, mode, content[start..i]);
                continue;
            }

            var value = content[i];
            if (AztecTables.TryGetCode(mode, value, out var directCode))
            {
                AppendBits(bits, directCode, AztecTables.CodeBits(mode));
                i++;
                continue;
            }

            // A shift exists only for two specific transitions (any-of-Upper/Lower/Mixed/Digit to
            // Punct, and Lower/Digit to Upper); every other mode change requires a latch, even for
            // a single character (see AztecTables' latch/shift graph). Prefer a shift over a latch
            // only when just this one character needs the alternate mode — a longer run is cheaper
            // latched, since a shift's cost repeats per character while a latch is paid once.
            var shiftTarget = FindShiftTarget(mode, value);
            if (shiftTarget is { } target && RunLength(content, i, target) <= 1)
            {
                AppendBits(bits, AztecTables.GetShiftCode(mode, target), AztecTables.CodeBits(mode));
                AztecTables.TryGetCode(target, value, out var shiftedCode);
                AppendBits(bits, shiftedCode, AztecTables.CodeBits(target));
                i++;
                continue;
            }

            var latchTarget = BestLatchTarget(content, i, value);
            mode = LatchTo(bits, mode, latchTarget);
            // Loop again without advancing i: the byte now has a direct code in the new mode.
        }

        return bits;
    }

    /// <summary>Whether <paramref name="value"/> has a literal code in any of the five character modes; a byte without one (0x00, 0x0E-0x1A, or 128-255) can only be carried through binary shift.</summary>
    private static bool IsTextRepresentable(byte value)
    {
        foreach (var mode in AllModes)
            if (AztecTables.TryGetCode(mode, value, out _))
                return true;
        return false;
    }

    /// <summary>The mode (if any) reachable from <paramref name="from"/> by a single shift code that can encode <paramref name="value"/>.</summary>
    private static AztecMode? FindShiftTarget(AztecMode from, byte value)
    {
        foreach (var candidate in AllModes)
        {
            if (candidate == from) continue;
            if (AztecTables.GetShiftCode(from, candidate) < 0) continue;
            if (AztecTables.TryGetCode(candidate, value, out _)) return candidate;
        }

        return null;
    }

    /// <summary>How many consecutive bytes starting at <paramref name="index"/> <paramref name="mode"/> can encode directly, without needing another mode change.</summary>
    private static int RunLength(ReadOnlySpan<byte> content, int index, AztecMode mode)
    {
        var count = 0;
        while (index + count < content.Length && AztecTables.TryGetCode(mode, content[index + count], out _))
            count++;
        return count;
    }

    /// <summary>
    /// Chooses which mode to latch to for the byte at <paramref name="index"/>: the mode (among
    /// those that can encode it) covering the longest immediate run, ties broken by a fixed
    /// preference order (Upper, Lower, Digit, Mixed, Punct) so encoding stays deterministic.
    /// </summary>
    private static AztecMode BestLatchTarget(ReadOnlySpan<byte> content, int index, byte value)
    {
        AztecMode best = default;
        var bestRun = -1;
        foreach (var candidate in AllModes)
        {
            if (!AztecTables.TryGetCode(candidate, value, out _)) continue;
            var run = RunLength(content, index, candidate);
            if (run > bestRun)
            {
                bestRun = run;
                best = candidate;
            }
        }

        return best;
    }

    /// <summary>Latches from <paramref name="from"/> to <paramref name="to"/>, emitting one code directly or, when no single-hop latch exists, a short chain through intermediate modes.</summary>
    private static AztecMode LatchTo(List<bool> bits, AztecMode from, AztecMode to)
    {
        foreach (var (hopFrom, code) in FindLatchPath(from, to))
            AppendBits(bits, code, AztecTables.CodeBits(hopFrom));
        return to;
    }

    /// <summary>
    /// Breadth-first search over the five-mode latch graph (<see cref="AztecTables.GetDirectLatchCode"/>):
    /// most mode pairs latch directly, but a few (e.g. Lower to Punct, Mixed to Digit) have no
    /// single-hop code and must route through an intermediate mode.
    /// </summary>
    private static List<(AztecMode From, int Code)> FindLatchPath(AztecMode from, AztecMode to)
    {
        if (from == to) return [];

        var previous = new Dictionary<AztecMode, (AztecMode Mode, int Code)>();
        var queue = new Queue<AztecMode>();
        queue.Enqueue(from);
        var visited = new HashSet<AztecMode> { from };

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in AllModes)
            {
                if (visited.Contains(next)) continue;
                var code = AztecTables.GetDirectLatchCode(current, next);
                if (code < 0) continue;

                previous[next] = (current, code);
                if (next == to) goto found;

                visited.Add(next);
                queue.Enqueue(next);
            }
        }

    found:
        var path = new List<(AztecMode, int)>();
        var mode = to;
        while (mode != from)
        {
            var (previousMode, code) = previous[mode];
            path.Insert(0, (previousMode, code));
            mode = previousMode;
        }

        return path;
    }

    /// <summary>
    /// Emits a binary shift (ISO/IEC 24778 clause 7.3.2): a shift code (latching to
    /// <see cref="AztecMode.Upper"/> first when <paramref name="mode"/> is <see cref="AztecMode.Punct"/>
    /// or <see cref="AztecMode.Digit"/>, neither of which has a direct binary-shift code), then a
    /// 5-bit length (1-31 bytes), or — for 32 or more bytes — a 5-bit zero followed by an 11-bit
    /// length less 31, then the raw bytes themselves, 8 bits apiece with no further transformation.
    /// Binary shift always reverts to the mode active when it was issued once the run ends, so the
    /// returned mode is that same (possibly just-latched) mode.
    /// </summary>
    private static AztecMode EmitBinaryShift(List<bool> bits, AztecMode mode, ReadOnlySpan<byte> run)
    {
        if (mode is AztecMode.Punct or AztecMode.Digit)
            mode = LatchTo(bits, mode, AztecMode.Upper);

        AppendBits(bits, BinaryShiftCode(mode), AztecTables.CodeBits(mode));

        if (run.Length <= 31)
        {
            AppendBits(bits, run.Length, 5);
        }
        else
        {
            AppendBits(bits, 0, 5);
            AppendBits(bits, run.Length - 31, 11);
        }

        foreach (var b in run) AppendBits(bits, b, 8);
        return mode;
    }

    private static int BinaryShiftCode(AztecMode mode) => mode switch
    {
        AztecMode.Upper => AztecTables.UpperBinaryShift,
        AztecMode.Lower => AztecTables.LowerBinaryShift,
        AztecMode.Mixed => AztecTables.MixedBinaryShift,
        _ => throw new InvalidOperationException($"{mode} has no binary shift code; it must first latch to Upper, Lower or Mixed."),
    };

    /// <summary>Appends the low <paramref name="bitCount"/> bits of <paramref name="value"/>, most significant bit first.</summary>
    private static void AppendBits(List<bool> bits, int value, int bitCount)
    {
        for (var i = bitCount - 1; i >= 0; i--)
            bits.Add(((value >> i) & 1) != 0);
    }
}
