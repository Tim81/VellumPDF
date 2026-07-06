// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Code39;

/// <summary>
/// Encodes Code 39 (ISO/IEC 16388) to module runs: the start character, each data character's
/// 9-element pattern separated by a single narrow inter-character gap, an optional modulo-43
/// check character, and the stop character.
/// </summary>
internal static class Code39Encoder
{
    /// <summary>Validates that every character of <paramref name="content"/> is one of the 43 standard Code 39 characters, returning it unchanged.</summary>
    /// <exception cref="ArgumentException">A character is not one of the 43 standard characters.</exception>
    internal static string ValidateStandardContent(string content)
    {
        foreach (var c in content)
            if (Code39Tables.ValueOf(c) < 0)
                throw new ArgumentException(
                    $"Code 39 content must be one of the 43 standard characters (0-9, A-Z, space, and -.$/+%); found '{c}'.",
                    nameof(content));
        return content;
    }

    /// <summary>Expands <paramref name="content"/> to its Extended Code 39 (Full ASCII) representation, one substitution per character.</summary>
    /// <exception cref="ArgumentException">A character falls outside ASCII (0-127).</exception>
    internal static string ExpandFullAscii(string content)
    {
        var sb = new StringBuilder(content.Length * 2);
        foreach (var c in content)
        {
            if (c > 127)
                throw new ArgumentException($"Full ASCII Code 39 content must be ASCII (0-127); found U+{(int)c:X4}.", nameof(content));
            sb.Append(Code39Tables.FullAsciiSubstitution(c));
        }

        return sb.ToString();
    }

    /// <summary>The modulo-43 check character value: the sum of each encoded character's value, reduced modulo 43.</summary>
    internal static int ComputeCheckValue(string encodedContent)
    {
        var sum = 0;
        foreach (var c in encodedContent) sum += Code39Tables.ValueOf(c);
        return sum % 43;
    }

    /// <summary>Encodes a <see cref="Code39Barcode"/> to module runs, quiet zones and its HRI group.</summary>
    /// <exception cref="ArgumentException">
    /// <see cref="Code39Barcode.WideNarrowRatio"/> is outside the ISO/IEC 16388 range 2.0-3.0, or
    /// <see cref="Code39Barcode.Content"/> contains a character the active mode (standard or
    /// Full ASCII) cannot represent.
    /// </exception>
    internal static Encoded1D Encode(Code39Barcode barcode)
    {
        var ratio = barcode.WideNarrowRatio;
        if (!double.IsFinite(ratio) || ratio < 2.0 || ratio > 3.0)
            throw new ArgumentException(
                $"WideNarrowRatio must be between 2.0 and 3.0 (was {ratio.ToString(CultureInfo.InvariantCulture)}).",
                nameof(barcode));

        var encodedContent = barcode.FullAscii
            ? ExpandFullAscii(barcode.Content)
            : ValidateStandardContent(barcode.Content);

        // Symbol sequence: start, each data character, an optional check character, stop.
        var symbols = new List<char>(encodedContent.Length + 3) { '*' };
        symbols.AddRange(encodedContent);
        if (barcode.CheckDigit)
            symbols.Add(Code39Tables.Characters[ComputeCheckValue(encodedContent)]);
        symbols.Add('*');

        var runs = new List<double>();
        for (var i = 0; i < symbols.Count; i++)
        {
            var pattern = symbols[i] == '*' ? Code39Tables.StartStopPattern : Code39Tables.PatternOf(symbols[i]);
            foreach (var element in pattern) runs.Add(element == 'W' ? ratio : 1);

            if (i != symbols.Count - 1)
                runs.Add(1); // single narrow-module inter-character gap
        }

        var dataModuleCount = SumRuns(runs);

        // The HRI shows the original content the caller supplied, not the expanded shift-pair
        // symbols or the start/stop/check characters — matching how a printed Code 39 label
        // captions the encoded value rather than its wire representation.
        return new Encoded1D
        {
            Runs = runs,
            QuietZoneLeft = 10,
            QuietZoneRight = 10,
            HriGroups = [new HriGroup(barcode.Content, HriAnchor.Below, 0, dataModuleCount)],
        };
    }

    private static double SumRuns(List<double> runs)
    {
        var total = 0.0;
        foreach (var run in runs) total += run;
        return total;
    }
}
