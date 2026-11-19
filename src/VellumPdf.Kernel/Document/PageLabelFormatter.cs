// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;

namespace VellumPdf.Document;

/// <summary>
/// Computes the label a reader shows for a page, from the numbering style, prefix and number of its
/// range (ISO 32000-2, 12.4.2). Internal: Layout calls it for page-number references and turns a refusal
/// into its own save-time refusal; the Kernel itself never formats a label.
/// </summary>
/// <remarks>
/// <b>What is refused.</b> <see cref="Format"/> throws <see cref="ArgumentOutOfRangeException"/> for a number
/// below 1, for a style that is not a defined <see cref="PageLabelNumbering"/>, and for a label longer than
/// <see cref="MaxLength"/> characters. The length is worked out arithmetically before anything is allocated,
/// so a number near <see cref="int.MaxValue"/> in letters (82,595,525 characters) costs nothing.
/// <para><see cref="MaxLength"/> is an allocation bound, chosen to match the string length ISO 19005-2,
/// 6.1.13 allows for a one-byte encoding. It does not by itself keep a string in a content stream within
/// that clause when the font uses a two-byte encoding, so a caller that shows the label must apply its own
/// limit.</para>
/// </remarks>
internal static class PageLabelFormatter
{
    /// <summary>The longest label produced, prefix included, in characters.</summary>
    internal const int MaxLength = 32_767;

    private static readonly (int Value, string Upper, string Lower)[] RomanDigits =
    [
        (900, "CM", "cm"), (500, "D", "d"), (400, "CD", "cd"), (100, "C", "c"), (90, "XC", "xc"),
        (50, "L", "l"), (40, "XL", "xl"), (10, "X", "x"), (9, "IX", "ix"), (5, "V", "v"), (4, "IV", "iv"), (1, "I", "i"),
    ];

    /// <summary>
    /// The label of a page whose number within its range is <paramref name="number"/>: the prefix, then the
    /// number in <paramref name="style"/>. <see cref="PageLabelNumbering.None"/> gives the prefix alone, and a
    /// <see langword="null"/> prefix counts as empty.
    /// </summary>
    /// <remarks>
    /// Decimal is written in invariant digits. Roman numerals use the subtractive form (4 is <c>iv</c>, 1994 is
    /// <c>MCMXCIV</c>) for 1 to 3999, and one <c>M</c> for each further thousand above that (4000 is
    /// <c>MMMM</c>): 12.4.2 sets no rule above 3999, so this is the library's choice. Letters follow Table 161,
    /// "A to Z for the first 26 pages, AA to ZZ for the next 26": the letter repeats, so 27 is <c>AA</c>, 52 is
    /// <c>ZZ</c> and 53 is <c>AAA</c>. It is not base 26, and the length is <c>(number - 1) / 26 + 1</c>.
    /// <para>The number is a <see cref="long"/> because the caller works it out as the range's first number plus
    /// the offset of the page, which passes <see cref="int.MaxValue"/> for a range that starts near it.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="number"/> is below 1, <paramref name="style"/> is not a defined value, or the label, prefix included, is longer than <see cref="MaxLength"/> characters.</exception>
    internal static string Format(PageLabelNumbering style, string? prefix, long number)
    {
        if (!Enum.IsDefined(style))
            throw new ArgumentOutOfRangeException(nameof(style), style, "The numbering style is not a defined value.");
        if (number < 1)
            throw new ArgumentOutOfRangeException(nameof(number), number, "A page label number must be 1 or more.");

        prefix ??= string.Empty;
        if (style == PageLabelNumbering.None)
        {
            CheckLength(prefix.Length, 0, number);
            return prefix;
        }

        string numeric;
        switch (style)
        {
            case PageLabelNumbering.Decimal:
                // At most 19 digits for a long, so there is nothing to bound before building it.
                CheckLength(prefix.Length, 0, number);
                numeric = number.ToString(CultureInfo.InvariantCulture);
                break;
            case PageLabelNumbering.UpperRoman:
            case PageLabelNumbering.LowerRoman:
                var upper = style == PageLabelNumbering.UpperRoman;
                var thousands = number / 1000;
                var rest = Roman((int)(number % 1000), upper);
                // The thousands are the only unbounded part; check before building them.
                CheckLength(prefix.Length, thousands + rest.Length, number);
                numeric = string.Concat(new string(upper ? 'M' : 'm', (int)thousands), rest);
                break;
            default:
                var length = (number - 1) / 26 + 1;
                CheckLength(prefix.Length, length, number);
                var letter = (char)((style == PageLabelNumbering.UpperLetters ? 'A' : 'a') + (int)((number - 1) % 26));
                numeric = new string(letter, (int)length);
                break;
        }
        return prefix.Length == 0 ? numeric : string.Concat(prefix, numeric);
    }

    private static void CheckLength(int prefixLength, long numericLength, long number)
    {
        if (prefixLength + numericLength > MaxLength)
            throw new ArgumentOutOfRangeException(nameof(number), number, $"The label would be longer than {MaxLength} characters.");
    }

    // 0 to 999 in the subtractive form.
    private static string Roman(int value, bool upper)
    {
        var builder = new System.Text.StringBuilder(8);
        var remaining = value;
        foreach (var (digit, upperText, lowerText) in RomanDigits)
        {
            while (remaining >= digit)
            {
                builder.Append(upper ? upperText : lowerText);
                remaining -= digit;
            }
        }
        return builder.ToString();
    }
}
