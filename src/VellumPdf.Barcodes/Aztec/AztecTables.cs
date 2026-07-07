// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Aztec;

// Character tables authored from ISO/IEC 24778's five encodation-mode tables (Upper, Lower,
// Mixed, Punct, Digit); zint and zxing-cpp are used only as decode/cross-check oracles in this
// package's tests, never as source.

/// <summary>The five Aztec Code high-level encodation modes (ISO/IEC 24778 clause 7.3.2).</summary>
internal enum AztecMode
{
    /// <summary>Upper-case letters and space; the initial mode.</summary>
    Upper = 0,

    /// <summary>Lower-case letters and space.</summary>
    Lower = 1,

    /// <summary>Control characters and the handful of symbols none of the other four modes reach directly.</summary>
    Mixed = 2,

    /// <summary>Punctuation.</summary>
    Punct = 3,

    /// <summary>Digits 0-9 plus comma, period and space; the only 4-bit-per-character mode.</summary>
    Digit = 4,
}

/// <summary>What a single 5-bit (or, in <see cref="AztecMode.Digit"/>, 4-bit) code represents.</summary>
internal enum AztecCodeKind : byte
{
    /// <summary>One literal source byte, given by <see cref="AztecTableEntry.Byte1"/>.</summary>
    Literal,

    /// <summary>Two literal source bytes packed into a single code (e.g. Punct's "CR LF" or ". " shortcuts).</summary>
    LiteralPair,

    /// <summary>Shifts to <see cref="AztecMode.Punct"/> for the one character that follows, then reverts.</summary>
    PunctShift,

    /// <summary>Shifts to <see cref="AztecMode.Upper"/> for the one character that follows, then reverts.</summary>
    UpperShift,

    /// <summary>Latches to <see cref="AztecMode.Lower"/> for every following character until the next latch.</summary>
    LowerLatch,

    /// <summary>Latches to <see cref="AztecMode.Upper"/> for every following character until the next latch.</summary>
    UpperLatch,

    /// <summary>Latches to <see cref="AztecMode.Mixed"/> for every following character until the next latch.</summary>
    MixedLatch,

    /// <summary>Latches to <see cref="AztecMode.Digit"/> for every following character until the next latch.</summary>
    DigitLatch,

    /// <summary>Latches to <see cref="AztecMode.Punct"/> for every following character until the next latch.</summary>
    PunctLatch,

    /// <summary>Shifts to binary (byte) mode for a run of raw bytes introduced by a length field, then reverts.</summary>
    BinaryShift,

    /// <summary>FLG(n): FNC1 (n=0) or an ECI designator (n=1-6). Not produced by this encoder (no GS1/ECI support in this release).</summary>
    Flag,
}

/// <summary>One entry of an Aztec Code encodation-mode table.</summary>
internal readonly record struct AztecTableEntry(AztecCodeKind Kind, byte Byte1 = 0, byte Byte2 = 0)
{
    internal static AztecTableEntry Literal(byte value) => new(AztecCodeKind.Literal, value);

    internal static AztecTableEntry LiteralPair(byte first, byte second) => new(AztecCodeKind.LiteralPair, first, second);
}

/// <summary>
/// The five Aztec Code encodation-mode tables (ISO/IEC 24778 clause 7.3.2, Table 3): Upper, Lower
/// and Mixed each hold 32 codes (a 5-bit code space); Punct also holds 32; Digit holds only 16 (a
/// 4-bit code space, the one mode narrower than 5 bits, matching its smaller character repertoire).
/// </summary>
internal static class AztecTables
{
    // ── Upper: codes 0-31 ──
    internal const int UpperPunctShift = 0;
    internal const int UpperLowerLatch = 28;
    internal const int UpperMixedLatch = 29;
    internal const int UpperDigitLatch = 30;
    internal const int UpperBinaryShift = 31;

    internal static readonly AztecTableEntry[] Upper =
    [
        new(AztecCodeKind.PunctShift),                 // 0  P/S
        AztecTableEntry.Literal((byte)' '),            // 1  SP
        AztecTableEntry.Literal((byte)'A'),             // 2
        AztecTableEntry.Literal((byte)'B'),             // 3
        AztecTableEntry.Literal((byte)'C'),             // 4
        AztecTableEntry.Literal((byte)'D'),             // 5
        AztecTableEntry.Literal((byte)'E'),             // 6
        AztecTableEntry.Literal((byte)'F'),             // 7
        AztecTableEntry.Literal((byte)'G'),             // 8
        AztecTableEntry.Literal((byte)'H'),             // 9
        AztecTableEntry.Literal((byte)'I'),             // 10
        AztecTableEntry.Literal((byte)'J'),             // 11
        AztecTableEntry.Literal((byte)'K'),             // 12
        AztecTableEntry.Literal((byte)'L'),             // 13
        AztecTableEntry.Literal((byte)'M'),             // 14
        AztecTableEntry.Literal((byte)'N'),             // 15
        AztecTableEntry.Literal((byte)'O'),             // 16
        AztecTableEntry.Literal((byte)'P'),             // 17
        AztecTableEntry.Literal((byte)'Q'),             // 18
        AztecTableEntry.Literal((byte)'R'),             // 19
        AztecTableEntry.Literal((byte)'S'),             // 20
        AztecTableEntry.Literal((byte)'T'),             // 21
        AztecTableEntry.Literal((byte)'U'),             // 22
        AztecTableEntry.Literal((byte)'V'),             // 23
        AztecTableEntry.Literal((byte)'W'),             // 24
        AztecTableEntry.Literal((byte)'X'),             // 25
        AztecTableEntry.Literal((byte)'Y'),             // 26
        AztecTableEntry.Literal((byte)'Z'),             // 27
        new(AztecCodeKind.LowerLatch),                  // 28 L/L
        new(AztecCodeKind.MixedLatch),                  // 29 M/L
        new(AztecCodeKind.DigitLatch),                  // 30 D/L
        new(AztecCodeKind.BinaryShift),                 // 31 B/S
    ];

    // ── Lower: codes 0-31 ──
    internal const int LowerPunctShift = 0;
    internal const int LowerUpperShift = 28;
    internal const int LowerMixedLatch = 29;
    internal const int LowerDigitLatch = 30;
    internal const int LowerBinaryShift = 31;

    internal static readonly AztecTableEntry[] Lower =
    [
        new(AztecCodeKind.PunctShift),                  // 0  P/S
        AztecTableEntry.Literal((byte)' '),             // 1  SP
        AztecTableEntry.Literal((byte)'a'),             // 2
        AztecTableEntry.Literal((byte)'b'),             // 3
        AztecTableEntry.Literal((byte)'c'),             // 4
        AztecTableEntry.Literal((byte)'d'),             // 5
        AztecTableEntry.Literal((byte)'e'),             // 6
        AztecTableEntry.Literal((byte)'f'),             // 7
        AztecTableEntry.Literal((byte)'g'),             // 8
        AztecTableEntry.Literal((byte)'h'),             // 9
        AztecTableEntry.Literal((byte)'i'),             // 10
        AztecTableEntry.Literal((byte)'j'),             // 11
        AztecTableEntry.Literal((byte)'k'),             // 12
        AztecTableEntry.Literal((byte)'l'),             // 13
        AztecTableEntry.Literal((byte)'m'),             // 14
        AztecTableEntry.Literal((byte)'n'),             // 15
        AztecTableEntry.Literal((byte)'o'),             // 16
        AztecTableEntry.Literal((byte)'p'),             // 17
        AztecTableEntry.Literal((byte)'q'),             // 18
        AztecTableEntry.Literal((byte)'r'),             // 19
        AztecTableEntry.Literal((byte)'s'),             // 20
        AztecTableEntry.Literal((byte)'t'),             // 21
        AztecTableEntry.Literal((byte)'u'),             // 22
        AztecTableEntry.Literal((byte)'v'),             // 23
        AztecTableEntry.Literal((byte)'w'),             // 24
        AztecTableEntry.Literal((byte)'x'),             // 25
        AztecTableEntry.Literal((byte)'y'),             // 26
        AztecTableEntry.Literal((byte)'z'),             // 27
        new(AztecCodeKind.UpperShift),                  // 28 U/S
        new(AztecCodeKind.MixedLatch),                  // 29 M/L
        new(AztecCodeKind.DigitLatch),                  // 30 D/L
        new(AztecCodeKind.BinaryShift),                 // 31 B/S
    ];

    // ── Mixed: codes 0-31. Covers the control characters and symbols reachable in none of Upper,
    // Lower, Punct or Digit: Ctrl-A through Ctrl-M (1-13), then Esc/FS/GS/RS/US (27, 28-31), then
    // @ \ ^ _ ` | ~ DEL. ──
    internal const int MixedPunctShift = 0;
    internal const int MixedLowerLatch = 28;
    internal const int MixedUpperLatch = 29;
    internal const int MixedPunctLatch = 30;
    internal const int MixedBinaryShift = 31;

    internal static readonly AztecTableEntry[] Mixed =
    [
        new(AztecCodeKind.PunctShift),                  // 0  P/S
        AztecTableEntry.Literal((byte)' '),             // 1  SP
        AztecTableEntry.Literal(1),                     // 2  ^A
        AztecTableEntry.Literal(2),                     // 3  ^B
        AztecTableEntry.Literal(3),                     // 4  ^C
        AztecTableEntry.Literal(4),                     // 5  ^D
        AztecTableEntry.Literal(5),                     // 6  ^E
        AztecTableEntry.Literal(6),                     // 7  ^F
        AztecTableEntry.Literal(7),                     // 8  ^G
        AztecTableEntry.Literal(8),                     // 9  ^H
        AztecTableEntry.Literal(9),                     // 10 ^I (tab)
        AztecTableEntry.Literal(10),                    // 11 ^J (LF)
        AztecTableEntry.Literal(11),                    // 12 ^K
        AztecTableEntry.Literal(12),                    // 13 ^L
        AztecTableEntry.Literal(13),                    // 14 ^M (CR)
        AztecTableEntry.Literal(27),                    // 15 ^[ (Esc)
        AztecTableEntry.Literal(28),                    // 16 ^\ (FS)
        AztecTableEntry.Literal(29),                    // 17 ^] (GS)
        AztecTableEntry.Literal(30),                    // 18 ^^ (RS)
        AztecTableEntry.Literal(31),                    // 19 ^_ (US)
        AztecTableEntry.Literal((byte)'@'),             // 20
        AztecTableEntry.Literal((byte)'\\'),            // 21
        AztecTableEntry.Literal((byte)'^'),             // 22
        AztecTableEntry.Literal((byte)'_'),             // 23
        AztecTableEntry.Literal((byte)'`'),             // 24
        AztecTableEntry.Literal((byte)'|'),             // 25
        AztecTableEntry.Literal((byte)'~'),             // 26
        AztecTableEntry.Literal(127),                   // 27 DEL
        new(AztecCodeKind.LowerLatch),                  // 28 L/L
        new(AztecCodeKind.UpperLatch),                  // 29 U/L
        new(AztecCodeKind.PunctLatch),                  // 30 P/L
        new(AztecCodeKind.BinaryShift),                 // 31 B/S
    ];

    // ── Punct: codes 0-31. Code 0 (FLG(n)) is out of scope for this release (no FNC1/ECI support). ──
    internal const int PunctUpperLatch = 31;

    internal static readonly AztecTableEntry[] Punct =
    [
        new(AztecCodeKind.Flag),                                        // 0  FLG(n)
        AztecTableEntry.Literal(13),                                    // 1  CR
        AztecTableEntry.LiteralPair(13, 10),                            // 2  CR LF
        AztecTableEntry.LiteralPair((byte)'.', (byte)' '),              // 3  . SP
        AztecTableEntry.LiteralPair((byte)',', (byte)' '),              // 4  , SP
        AztecTableEntry.LiteralPair((byte)':', (byte)' '),              // 5  : SP
        AztecTableEntry.Literal((byte)'!'),                             // 6
        AztecTableEntry.Literal((byte)'"'),                             // 7
        AztecTableEntry.Literal((byte)'#'),                             // 8
        AztecTableEntry.Literal((byte)'$'),                             // 9
        AztecTableEntry.Literal((byte)'%'),                             // 10
        AztecTableEntry.Literal((byte)'&'),                             // 11
        AztecTableEntry.Literal((byte)'\''),                            // 12
        AztecTableEntry.Literal((byte)'('),                             // 13
        AztecTableEntry.Literal((byte)')'),                             // 14
        AztecTableEntry.Literal((byte)'*'),                             // 15
        AztecTableEntry.Literal((byte)'+'),                             // 16
        AztecTableEntry.Literal((byte)','),                             // 17
        AztecTableEntry.Literal((byte)'-'),                             // 18
        AztecTableEntry.Literal((byte)'.'),                             // 19
        AztecTableEntry.Literal((byte)'/'),                             // 20
        AztecTableEntry.Literal((byte)':'),                             // 21
        AztecTableEntry.Literal((byte)';'),                             // 22
        AztecTableEntry.Literal((byte)'<'),                             // 23
        AztecTableEntry.Literal((byte)'='),                             // 24
        AztecTableEntry.Literal((byte)'>'),                             // 25
        AztecTableEntry.Literal((byte)'?'),                             // 26
        AztecTableEntry.Literal((byte)'['),                             // 27
        AztecTableEntry.Literal((byte)']'),                             // 28
        AztecTableEntry.Literal((byte)'{'),                             // 29
        AztecTableEntry.Literal((byte)'}'),                             // 30
        new(AztecCodeKind.UpperLatch),                                  // 31 U/L
    ];

    // ── Digit: codes 0-15 (4-bit). ──
    internal const int DigitPunctShift = 0;
    internal const int DigitUpperLatch = 14;
    internal const int DigitUpperShift = 15;

    internal static readonly AztecTableEntry[] Digit =
    [
        new(AztecCodeKind.PunctShift),                  // 0  P/S
        AztecTableEntry.Literal((byte)' '),             // 1  SP
        AztecTableEntry.Literal((byte)'0'),             // 2
        AztecTableEntry.Literal((byte)'1'),             // 3
        AztecTableEntry.Literal((byte)'2'),             // 4
        AztecTableEntry.Literal((byte)'3'),             // 5
        AztecTableEntry.Literal((byte)'4'),             // 6
        AztecTableEntry.Literal((byte)'5'),             // 7
        AztecTableEntry.Literal((byte)'6'),             // 8
        AztecTableEntry.Literal((byte)'7'),             // 9
        AztecTableEntry.Literal((byte)'8'),             // 10
        AztecTableEntry.Literal((byte)'9'),             // 11
        AztecTableEntry.Literal((byte)','),             // 12
        AztecTableEntry.Literal((byte)'.'),             // 13
        new(AztecCodeKind.UpperLatch),                  // 14 U/L
        new(AztecCodeKind.UpperShift),                  // 15 U/S
    ];

    private static readonly int[] UpperCodeOf = BuildReverseLookup(Upper);
    private static readonly int[] LowerCodeOf = BuildReverseLookup(Lower);
    private static readonly int[] MixedCodeOf = BuildReverseLookup(Mixed);
    private static readonly int[] PunctCodeOf = BuildReverseLookup(Punct);
    private static readonly int[] DigitCodeOf = BuildReverseLookup(Digit);

    /// <summary>The number of bits one code occupies in <paramref name="mode"/>: 4 for <see cref="AztecMode.Digit"/>, 5 for every other mode.</summary>
    internal static int CodeBits(AztecMode mode) => mode == AztecMode.Digit ? 4 : 5;

    /// <summary>Returns whether <paramref name="value"/> has a direct literal code in <paramref name="mode"/>, and that code.</summary>
    internal static bool TryGetCode(AztecMode mode, byte value, out int code)
    {
        code = ReverseLookupFor(mode)[value];
        return code >= 0;
    }

    /// <summary>Returns the shift code (ISO/IEC 24778 Table 3) taking <paramref name="from"/> to <paramref name="to"/> for one character, or <c>-1</c> if no direct shift exists between them.</summary>
    internal static int GetShiftCode(AztecMode from, AztecMode to) => (from, to) switch
    {
        (AztecMode.Upper, AztecMode.Punct) => UpperPunctShift,
        (AztecMode.Lower, AztecMode.Punct) => LowerPunctShift,
        (AztecMode.Mixed, AztecMode.Punct) => MixedPunctShift,
        (AztecMode.Digit, AztecMode.Punct) => DigitPunctShift,
        (AztecMode.Lower, AztecMode.Upper) => LowerUpperShift,
        (AztecMode.Digit, AztecMode.Upper) => DigitUpperShift,
        _ => -1,
    };

    /// <summary>Returns the latch code (ISO/IEC 24778 Table 3) taking <paramref name="from"/> directly to <paramref name="to"/>, or <c>-1</c> if no single-hop latch exists (see <c>AztecHighLevelEncoder.FindLatchPath</c> for the multi-hop case).</summary>
    internal static int GetDirectLatchCode(AztecMode from, AztecMode to) => (from, to) switch
    {
        (AztecMode.Upper, AztecMode.Lower) => UpperLowerLatch,
        (AztecMode.Upper, AztecMode.Mixed) => UpperMixedLatch,
        (AztecMode.Upper, AztecMode.Digit) => UpperDigitLatch,
        (AztecMode.Lower, AztecMode.Mixed) => LowerMixedLatch,
        (AztecMode.Lower, AztecMode.Digit) => LowerDigitLatch,
        (AztecMode.Mixed, AztecMode.Lower) => MixedLowerLatch,
        (AztecMode.Mixed, AztecMode.Upper) => MixedUpperLatch,
        (AztecMode.Mixed, AztecMode.Punct) => MixedPunctLatch,
        (AztecMode.Punct, AztecMode.Upper) => PunctUpperLatch,
        (AztecMode.Digit, AztecMode.Upper) => DigitUpperLatch,
        _ => -1,
    };

    private static int[] ReverseLookupFor(AztecMode mode) => mode switch
    {
        AztecMode.Upper => UpperCodeOf,
        AztecMode.Lower => LowerCodeOf,
        AztecMode.Mixed => MixedCodeOf,
        AztecMode.Punct => PunctCodeOf,
        AztecMode.Digit => DigitCodeOf,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown Aztec mode."),
    };

    private static int[] BuildReverseLookup(AztecTableEntry[] table)
    {
        var lookup = new int[256];
        Array.Fill(lookup, -1);
        for (var code = 0; code < table.Length; code++)
            if (table[code].Kind == AztecCodeKind.Literal)
                lookup[table[code].Byte1] = code;
        return lookup;
    }
}
