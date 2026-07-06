// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Barcodes.Internal;

/// <summary>One parsed (application identifier, value) pair from a GS1 element string.</summary>
/// <param name="Ai">The application identifier, 2-4 digits.</param>
/// <param name="Value">The AI's data value, excluding any separator.</param>
internal readonly record struct Gs1Element(string Ai, string Value);

/// <summary>
/// The result of parsing a GS1 element string: the same data in the two forms this package's
/// symbologies and their human-readable labels need.
/// </summary>
/// <param name="EncoderPayload">
/// The digit/character stream to hand to a symbol encoder, with U+001D (GS) standing in for
/// FNC1 wherever one is required — the convention <c>Code128Encoder</c> already consumes.
/// </param>
/// <param name="Hri">The parenthesized-AI human-readable form, e.g. <c>(01)09501101020917(17)261231</c>.</param>
/// <param name="Elements">The parsed (AI, value) pairs, in encounter order.</param>
internal readonly record struct Gs1ElementStringResult(string EncoderPayload, string Hri, IReadOnlyList<Gs1Element> Elements);

/// <summary>
/// Parses and re-renders GS1 element strings — application-identifier-tagged data such as a
/// GTIN plus a batch/lot number and an expiration date. This is the one parser Code 128's GS1-128
/// human-readable label, GS1 QR Code, and GS1 DataMatrix all build on; it is decode-free (it
/// never reads a symbol's bars back into an element string, only the reverse).
///
/// <para>
/// Two equivalent input conventions are accepted and normalized to the same result: the raw
/// digit/character stream with U+001D (GS) separators between variable-length AI values (how
/// <c>Code128Barcode.Content</c> already carries GS1-128 data), and the human-readable
/// parenthesized-AI notation, e.g. <c>(01)09501101020917(17)261231(10)ABC123</c>.
/// </para>
///
/// <para>
/// Whether a separator is required between two element strings depends on whether the first
/// one's value has a length fixed by its AI (GS1 General Specifications, Section 3, "GS1
/// Application Identifier definitions" — the "AIs with predefined length" figure lists exactly
/// which ones do): a predefined-fixed-length value is never followed by a separator, because a
/// decoder already knows how many characters to take; any other AI's value is variable-length
/// and must be followed by a separator unless it is the last element string in the data.
/// </para>
/// </summary>
internal static class Gs1ElementString
{
    // AIs with a value of predefined fixed length (GS1 General Specifications Section 3,
    // "AIs with predefined length" figure). Keyed by the AI itself for the 2- and 3-digit
    // entries; the four-digit weight/dimension family below is keyed by its 2-digit prefix
    // because its 3rd and 4th digits select the unit and decimal-point position, not the AI's
    // meaning as a fixed-length marker.
    private static readonly Dictionary<string, int> TwoDigitFixedLength = new()
    {
        ["00"] = 18, // SSCC
        ["01"] = 14, // GTIN
        ["02"] = 14, // GTIN of contained trade items
        ["11"] = 6,  // production date, YYMMDD
        ["12"] = 6,  // due date, YYMMDD
        ["13"] = 6,  // packaging date, YYMMDD
        ["15"] = 6,  // best before date, YYMMDD
        ["16"] = 6,  // sell by date, YYMMDD
        ["17"] = 6,  // expiration date, YYMMDD
        ["20"] = 2,  // variant
    };

    // Global Location Number references: ship-to, bill-to, purchased-from, ship-for, physical
    // location, invoicing party, production/service location, party — all a fixed 13 digits.
    private static readonly HashSet<string> GlnThreeDigitAis =
        ["410", "411", "412", "413", "414", "415", "416", "417"];

    // The variable-measure family: a 4-digit AI (2-digit prefix below + a unit/quantity digit +
    // a decimal-point-position digit) followed by a fixed 6-digit value. 31 = product net
    // weight/length/width/depth/area/volume in SI units; 32 = the same in US customary units;
    // 33 = logistic (gross) weight/dimensions in SI units; 34 = the same in US customary units;
    // 35 = areas (product and logistic) in US customary units, troy and avoirdupois ounces; 36
    // = volumes (product and logistic) in assorted units.
    private static readonly HashSet<string> WeightDimensionFamilyPrefixes =
        ["31", "32", "33", "34", "35", "36"];

    /// <summary>
    /// Parses a GS1 element string in either the raw-digit-stream or parenthesized-AI
    /// convention (detected from whether the first character is <c>(</c>) and normalizes it.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="input"/> is null, empty, or has no application identifiers.</exception>
    /// <exception cref="FormatException"><paramref name="input"/> is not well-formed GS1 element-string data.</exception>
    internal static Gs1ElementStringResult Parse(string input)
    {
        ArgumentException.ThrowIfNullOrEmpty(input);
        return input[0] == '(' ? ParseParenthesized(input) : ParseRawPayload(input);
    }

    private static Gs1ElementStringResult ParseRawPayload(string input)
    {
        var elements = new List<Gs1Element>();
        var position = 0;
        while (position < input.Length)
        {
            if (input[position] == '')
                throw new FormatException($"Unexpected separator at position {position}: no element string precedes it.");

            var aiLength = DetermineRawAiCodeLength(input.AsSpan(position));
            if (position + aiLength > input.Length || !AllDigits(input.AsSpan(position, aiLength)))
                throw new FormatException($"Expected a {aiLength}-digit application identifier at position {position}.");

            var ai = input.Substring(position, aiLength);
            var valueStart = position + aiLength;

            if (TryGetFixedValueLength(ai, out var fixedLength))
            {
                if (valueStart + fixedLength > input.Length)
                    throw new FormatException($"AI {ai} requires a {fixedLength}-character value; input is truncated.");

                var value = input.Substring(valueStart, fixedLength);
                ValidateValueCharacters(ai, value);
                elements.Add(new Gs1Element(ai, value));
                position = valueStart + fixedLength;
            }
            else
            {
                var separatorIndex = input.IndexOf('', valueStart);
                var valueEnd = separatorIndex < 0 ? input.Length : separatorIndex;
                if (valueEnd == valueStart)
                    throw new FormatException($"AI {ai} has an empty value.");

                var value = input[valueStart..valueEnd];
                ValidateValueCharacters(ai, value);
                elements.Add(new Gs1Element(ai, value));
                position = separatorIndex < 0 ? input.Length : separatorIndex + 1;
            }
        }

        if (elements.Count == 0)
            throw new ArgumentException("GS1 element string has no application identifiers.", nameof(input));

        return BuildResult(elements);
    }

    private static Gs1ElementStringResult ParseParenthesized(string input)
    {
        var elements = new List<Gs1Element>();
        var position = 0;
        while (position < input.Length)
        {
            if (input[position] != '(')
                throw new FormatException($"Expected '(' at position {position}.");

            var close = input.IndexOf(')', position + 1);
            if (close < 0)
                throw new FormatException("Unterminated '(': missing a matching ')'.");

            var ai = input[(position + 1)..close];
            if (ai.Length is < 2 or > 4 || !AllDigits(ai))
                throw new FormatException($"'{ai}' is not a 2-4 digit application identifier.");

            var valueStart = close + 1;
            var nextOpen = input.IndexOf('(', valueStart);
            var valueEnd = nextOpen < 0 ? input.Length : nextOpen;
            if (valueEnd == valueStart)
                throw new FormatException($"AI {ai} has an empty value.");

            var value = input[valueStart..valueEnd];
            ValidateValueCharacters(ai, value);
            if (TryGetFixedValueLength(ai, out var fixedLength) && value.Length != fixedLength)
                throw new FormatException($"AI {ai} requires a {fixedLength}-character value; found {value.Length}.");

            elements.Add(new Gs1Element(ai, value));
            position = valueEnd;
        }

        if (elements.Count == 0)
            throw new ArgumentException("GS1 element string has no application identifiers.", nameof(input));

        return BuildResult(elements);
    }

    private static Gs1ElementStringResult BuildResult(List<Gs1Element> elements)
    {
        var payload = new StringBuilder();
        var hri = new StringBuilder();
        for (var i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            payload.Append(element.Ai).Append(element.Value);
            hri.Append('(').Append(element.Ai).Append(')').Append(element.Value);

            var isLast = i == elements.Count - 1;
            if (!isLast && !TryGetFixedValueLength(element.Ai, out _))
                payload.Append('');
        }

        return new Gs1ElementStringResult(payload.ToString(), hri.ToString(), elements);
    }

    /// <summary>
    /// Determines how many leading digits of a raw payload form the application identifier: 4
    /// when they match the variable-measure family, 3 when they match a GLN reference, 2
    /// otherwise (every AI not in the predefined-length table that this package currently needs
    /// to round-trip — batch/lot, serial, and similar — is 2 digits).
    /// </summary>
    private static int DetermineRawAiCodeLength(ReadOnlySpan<char> remaining)
    {
        if (remaining.Length >= 4 && AllDigits(remaining[..4]) && TryGetFixedValueLength(new string(remaining[..4]), out _))
            return 4;
        if (remaining.Length >= 3 && AllDigits(remaining[..3]) && TryGetFixedValueLength(new string(remaining[..3]), out _))
            return 3;
        return 2;
    }

    private static bool TryGetFixedValueLength(string ai, out int valueLength)
    {
        if (ai.Length == 2 && TwoDigitFixedLength.TryGetValue(ai, out valueLength)) return true;
        if (ai.Length == 3 && GlnThreeDigitAis.Contains(ai)) { valueLength = 13; return true; }
        if (ai.Length == 4 && WeightDimensionFamilyPrefixes.Contains(ai[..2])) { valueLength = 6; return true; }
        valueLength = 0;
        return false;
    }

    private static bool AllDigits(ReadOnlySpan<char> s)
    {
        foreach (var c in s)
            if (!char.IsAsciiDigit(c))
                return false;
        return true;
    }

    private static void ValidateValueCharacters(string ai, string value)
    {
        foreach (var c in value)
            if (c is < ' ' or > '~')
                throw new FormatException($"AI {ai} value contains a character outside the printable ASCII range: U+{(int)c:X4}.");
    }
}
