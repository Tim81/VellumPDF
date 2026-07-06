// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Code39;

/// <summary>
/// The Code 39 (ISO/IEC 16388) character set: the 43-character modulo-43 value table, each
/// character's 9-element bar/space pattern, and the Full ASCII (Extended Code 39) shift-pair
/// substitution table (AIM USS-39, also published as ANSI/AIM BC1-1995).
/// </summary>
/// <remarks>
/// The character order and every bar/space pattern below were transcribed directly from Table 2
/// ("USS-39 Character Structure") of the AIM Uniform Symbology Specification for Code 39, and
/// independently checked character-by-character against that table: it lists each character's
/// five bars and four spaces as separate wide(1)/narrow(0) digit strings, which this table
/// interleaves into the single 9-element sequence <see cref="PatternOf"/> returns.
/// </remarks>
internal static class Code39Tables
{
    /// <summary>
    /// The 43 standard characters, in modulo-43 value order (0-42): the ten digits, the 26
    /// letters, then the seven special characters minus, period, space, dollar, slash, plus and
    /// percent. A character's index in this string is its check-digit value. Order matches
    /// Table 2 of the AIM USS-39 specification.
    /// </summary>
    internal const string Characters = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    /// <summary>
    /// Each character's 9-element bar/space pattern (bar, space, bar, space, bar, space, bar,
    /// space, bar), 'N' = narrow, 'W' = wide. Every pattern has exactly 3 wide elements of 9 —
    /// the "3 of 9" the symbology is named for. Indices match <see cref="Characters"/>.
    /// </summary>
    private static readonly string[] Patterns =
    [
        "NNNWWNWNN", "WNNWNNNNW", "NNWWNNNNW", "WNWWNNNNN", "NNNWWNNNW", // 0-4
        "WNNWWNNNN", "NNWWWNNNN", "NNNWNNWNW", "WNNWNNWNN", "NNWWNNWNN", // 5-9
        "WNNNNWNNW", "NNWNNWNNW", "WNWNNWNNN", "NNNNWWNNW", "WNNNWWNNN", // A-E
        "NNWNWWNNN", "NNNNNWWNW", "WNNNNWWNN", "NNWNNWWNN", "NNNNWWWNN", // F-J
        "WNNNNNNWW", "NNWNNNNWW", "WNWNNNNWN", "NNNNWNNWW", "WNNNWNNWN", // K-O
        "NNWNWNNWN", "NNNNNNWWW", "WNNNNNWWN", "NNWNNNWWN", "NNNNWNWWN", // P-T
        "WWNNNNNNW", "NWWNNNNNW", "WWWNNNNNN", "NWNNWNNNW", "WWNNWNNNN", // U-Y
        "NWWNWNNNN",                                                    // Z
        "NWNNNNWNW", "WWNNNNWNN", "NWWNNNWNN",                          // - . (space)
        "NWNWNWNNN", "NWNWNNNWN", "NWNNNWNWN", "NNNWNWNWN",              // $ / + %
    ];

    /// <summary>
    /// The start/stop character's pattern (an asterisk, <c>*</c>). Not part of the 43-character
    /// value table — it carries no check-digit value. Per AIM USS-39, this sentinel pattern is
    /// unique among all 44 patterns this symbology defines, which is what lets a reader recognize
    /// it as a delimiter and scan the symbol in either direction.
    /// </summary>
    internal const string StartStopPattern = "NWNNWNWNN";

    /// <summary>
    /// The Extended Code 39 (Full ASCII) substitution for each ASCII code point 0-127: either a
    /// single standard character (when the code point is already one of the 43) or a
    /// two-character shift pair — a <c>$</c>, <c>/</c>, <c>%</c> or <c>+</c> precedence code
    /// followed by a letter A-Z (AIM USS-39 Full ASCII table).
    /// </summary>
    private static readonly string[] FullAsciiSubstitutions =
    [
        "%U", "$A", "$B", "$C", "$D", "$E", "$F", "$G", "$H", "$I", "$J", "$K", "$L", "$M", "$N", "$O", // 0-15
        "$P", "$Q", "$R", "$S", "$T", "$U", "$V", "$W", "$X", "$Y", "$Z", "%A", "%B", "%C", "%D", "%E", // 16-31
        " ", "/A", "/B", "/C", "/D", "/E", "/F", "/G", "/H", "/I", "/J", "/K", "/L", "-", ".", "/O",     // 32-47
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9", "/Z", "%F", "%G", "%H", "%I", "%J",            // 48-63
        "%V", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O",                 // 64-79
        "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "%K", "%L", "%M", "%N", "%O",             // 80-95
        "%W", "+A", "+B", "+C", "+D", "+E", "+F", "+G", "+H", "+I", "+J", "+K", "+L", "+M", "+N", "+O",  // 96-111
        "+P", "+Q", "+R", "+S", "+T", "+U", "+V", "+W", "+X", "+Y", "+Z", "%P", "%Q", "%R", "%S", "%T",  // 112-127
    ];

    /// <summary>Looks up a standard character's modulo-43 value (0-42), or -1 if <paramref name="c"/> is not one of the 43 characters.</summary>
    internal static int ValueOf(char c) => Characters.IndexOf(c);

    /// <summary>Looks up a standard character's 9-element bar/space pattern.</summary>
    /// <exception cref="ArgumentException"><paramref name="c"/> is not one of the 43 standard characters.</exception>
    internal static string PatternOf(char c)
    {
        var index = Characters.IndexOf(c);
        if (index < 0)
            throw new ArgumentException(
                $"'{c}' is not a standard Code 39 character (0-9, A-Z, space, or -.$/+%).", nameof(c));
        return Patterns[index];
    }

    /// <summary>Looks up the Full ASCII substitution for an ASCII code point.</summary>
    /// <exception cref="ArgumentException"><paramref name="asciiCode"/> falls outside 0-127.</exception>
    internal static string FullAsciiSubstitution(int asciiCode)
    {
        if ((uint)asciiCode >= 128)
            throw new ArgumentException($"Full ASCII Code 39 content must be ASCII (0-127); found code point {asciiCode}.", nameof(asciiCode));
        return FullAsciiSubstitutions[asciiCode];
    }
}
