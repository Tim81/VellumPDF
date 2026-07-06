// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Barcodes.EanUpc;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.Itf;

/// <summary>
/// Encodes ITF-14 (Interleaved 2-of-5) to module runs (GS1 General Specifications §5.3;
/// Wikipedia Interleaved 2 of 5). Each digit is a two-out-of-five pattern of five bars or five
/// spaces (weights 1, 2, 4, 7 and a fifth "parity" position); digits are encoded in pairs, the
/// first as bars and the second as the interleaved spaces between them.
/// </summary>
internal static class Itf14Encoder
{
    /// <summary>
    /// For each digit 0-9, whether each of its five bar/space elements is wide (<c>true</c>)
    /// or narrow (<c>false</c>): weights 1, 2, 4, 7 and a fifth position completing every
    /// pattern to exactly two wide elements (Wikipedia's Interleaved 2 of 5 encoding table).
    /// </summary>
    private static readonly bool[][] WidePattern =
    [
        [false, false, true, true, false],  // 0 = nnWWn
        [true, false, false, false, true],  // 1 = WnnnW
        [false, true, false, false, true],  // 2 = nWnnW
        [true, true, false, false, false],  // 3 = WWnnn
        [false, false, true, false, true],  // 4 = nnWnW
        [true, false, true, false, false],  // 5 = WnWnn
        [false, true, true, false, false],  // 6 = nWWnn
        [false, false, false, true, true],  // 7 = nnnWW
        [true, false, false, true, false],  // 8 = WnnWn
        [false, true, false, true, false],  // 9 = nWnWn
    ];

    /// <summary>
    /// Validates <paramref name="digits"/> as 13 digits (the check digit is computed) or 14
    /// digits (the check digit is validated, using the universal GTIN algorithm). Returns the
    /// canonical 14-digit string.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="digits"/> has the wrong length or contains a non-digit character.</exception>
    /// <exception cref="FormatException">A supplied check digit does not match the computed one.</exception>
    internal static string NormalizeAndValidate(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);
        if (digits.Length != 13 && digits.Length != 14)
            throw new ArgumentException(
                $"ITF-14 requires 13 digits (check digit computed) or 14 digits (check digit validated), but got {digits.Length}.",
                nameof(digits));

        foreach (var c in digits)
            if (!char.IsAsciiDigit(c))
                throw new ArgumentException($"ITF-14 digits must be 0-9 (found '{c}').", nameof(digits));

        var check = EanEncoder.ComputeCheckDigit(digits.AsSpan(0, 13));
        if (digits.Length == 13)
            return digits + (char)('0' + check);

        var providedCheck = digits[13] - '0';
        if (providedCheck != check)
            throw new FormatException(
                $"ITF-14 check digit mismatch: '{digits}' carries check digit {providedCheck}, but {check} was expected.");

        return digits;
    }

    /// <summary>Encodes an <see cref="Itf14Barcode"/> to module runs, quiet zones, HRI text and bearer-bar geometry.</summary>
    /// <exception cref="ArgumentException"><see cref="Itf14Barcode.WideNarrowRatio"/> is outside the GS1 range 2.25-3.0.</exception>
    internal static Encoded1D Encode(Itf14Barcode barcode)
    {
        var ratio = barcode.WideNarrowRatio;
        if (!double.IsFinite(ratio) || ratio < 2.25 || ratio > 3.0)
            throw new ArgumentException($"WideNarrowRatio must be between 2.25 and 3.0 (was {ratio}).", nameof(barcode));

        var digits = barcode.Digits;
        var runs = new List<double> { 1, 1, 1, 1 }; // start: narrow bar, space, bar, space

        for (var i = 0; i < 14; i += 2)
        {
            var barDigit = WidePattern[digits[i] - '0'];
            var spaceDigit = WidePattern[digits[i + 1] - '0'];
            for (var j = 0; j < 5; j++)
            {
                runs.Add(barDigit[j] ? ratio : 1);
                runs.Add(spaceDigit[j] ? ratio : 1);
            }
        }

        runs.Add(ratio); // stop: wide bar
        runs.Add(1);     // narrow space
        runs.Add(1);     // narrow bar

        var dataModuleCount = SumRuns(runs);

        BearerSpec? bearer = barcode.BearerBars == ItfBearerBarStyle.None
            ? null
            : new BearerSpec(barcode.BearerBars, 2.0); // GS1: bearer thickness is at least 2 modules

        return new Encoded1D
        {
            Runs = runs,
            QuietZoneLeft = 10,
            QuietZoneRight = 10,
            HriGroups = [new HriGroup(digits, HriAnchor.Below, 0, dataModuleCount)],
            Bearer = bearer,
        };
    }

    private static double SumRuns(List<double> runs)
    {
        var total = 0.0;
        foreach (var r in runs) total += r;
        return total;
    }
}
