// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Barcodes.Internal;

namespace VellumPdf.Barcodes.EanUpc;

/// <summary>
/// Encodes EAN-13, EAN-8 and UPC-A (plus EAN-2/EAN-5 add-ons) to module runs (GS1 General
/// Specifications §5.2; Wikipedia International Article Number / EAN-5 / EAN-2). UPC-A is
/// encoded as the 0-prefixed EAN-13 it structurally is: the same twelve L/G/R digit patterns
/// and guards, just with different human-readable grouping.
/// </summary>
internal static class EanEncoder
{
    /// <summary>
    /// Validates <paramref name="digits"/> against the digit count required by
    /// <paramref name="symbology"/>: the data-digit count alone (the check digit is computed) or
    /// the data-digit count plus one (the check digit is validated). Returns the canonical,
    /// full-length digit string, including the check digit.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="digits"/> has the wrong length or contains a non-digit character.</exception>
    /// <exception cref="FormatException">A supplied check digit does not match the computed one.</exception>
    internal static string NormalizeAndValidate(EanSymbology symbology, string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);

        var dataLength = symbology switch
        {
            EanSymbology.Ean13 => 12,
            EanSymbology.Ean8 => 7,
            EanSymbology.UpcA => 11,
            _ => throw new ArgumentOutOfRangeException(nameof(symbology), symbology, "Unknown EAN/UPC symbology."),
        };

        if (digits.Length != dataLength && digits.Length != dataLength + 1)
            throw new ArgumentException(
                $"{symbology} requires {dataLength} digits (check digit computed) or {dataLength + 1} digits (check digit validated), but got {digits.Length}.",
                nameof(digits));

        foreach (var c in digits)
            if (!char.IsAsciiDigit(c))
                throw new ArgumentException($"{symbology} digits must be 0-9 (found '{c}').", nameof(digits));

        var check = ComputeCheckDigit(digits.AsSpan(0, dataLength));
        if (digits.Length == dataLength)
            return digits + (char)('0' + check);

        var providedCheck = digits[dataLength] - '0';
        if (providedCheck != check)
            throw new FormatException(
                $"{symbology} check digit mismatch: '{digits}' carries check digit {providedCheck}, but {check} was expected.");

        return digits;
    }

    /// <summary>Validates a 2- or 5-digit EAN add-on, returning it unchanged.</summary>
    /// <exception cref="ArgumentException">The add-on is not 2 or 5 ASCII digits.</exception>
    internal static string ValidateAddOn(string addOn)
    {
        ArgumentNullException.ThrowIfNull(addOn);
        if (addOn.Length != 2 && addOn.Length != 5)
            throw new ArgumentException("An EAN add-on must be 2 or 5 digits.", nameof(addOn));

        foreach (var c in addOn)
            if (!char.IsAsciiDigit(c))
                throw new ArgumentException($"Add-on digits must be 0-9 (found '{c}').", nameof(addOn));

        return addOn;
    }

    /// <summary>
    /// The universal GTIN check-digit algorithm (shared by EAN-13, EAN-8, UPC-A and every other
    /// GTIN length): starting from the rightmost data digit, alternate weights 3, 1, 3, 1, ...
    /// The check digit is the value that brings the weighted sum to a multiple of 10.
    /// </summary>
    internal static int ComputeCheckDigit(ReadOnlySpan<char> dataDigits)
    {
        var sum = 0;
        var weight = 3;
        for (var i = dataDigits.Length - 1; i >= 0; i--)
        {
            sum += (dataDigits[i] - '0') * weight;
            weight = weight == 3 ? 1 : 3;
        }

        return (10 - (sum % 10)) % 10;
    }

    /// <summary>The EAN-5 add-on checksum: the five digits weighted 3, 9, 3, 9, 3 (left to right), summed and reduced mod 10.</summary>
    internal static int ComputeEan5Checksum(ReadOnlySpan<char> fiveDigits)
    {
        ReadOnlySpan<int> weights = [3, 9, 3, 9, 3];
        var sum = 0;
        for (var i = 0; i < 5; i++) sum += (fiveDigits[i] - '0') * weights[i];
        return sum % 10;
    }

    /// <summary>Encodes an <see cref="EanBarcode"/> to module runs, quiet zones, guard extensions and HRI groups.</summary>
    internal static Encoded1D Encode(EanBarcode barcode)
    {
        string bits;
        List<HriGroup> hriGroups;
        List<GuardExtension> guardExtensions;
        double quietLeft;
        double quietRight;

        switch (barcode.Symbology)
        {
            case EanSymbology.Ean13:
                bits = BuildEan13LikeBits(barcode.Digits);
                hriGroups = BuildEan13HriGroups(barcode.Digits);
                guardExtensions = GuardExtensionsFor95Modules();
                quietLeft = 11;
                quietRight = 7;
                break;

            case EanSymbology.Ean8:
                bits = BuildEan8Bits(barcode.Digits);
                hriGroups = BuildEan8HriGroups(barcode.Digits);
                guardExtensions =
                [
                    new GuardExtension(0, 3),
                    new GuardExtension(31, 5),
                    new GuardExtension(64, 3),
                ];
                quietLeft = 7;
                quietRight = 7;
                break;

            case EanSymbology.UpcA:
                bits = BuildEan13LikeBits("0" + barcode.Digits);
                hriGroups = BuildUpcAHriGroups(barcode.Digits);
                guardExtensions = GuardExtensionsFor95Modules();
                quietLeft = 9;
                quietRight = 9;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(barcode), barcode.Symbology, "Unknown EAN/UPC symbology.");
        }

        var runs = ToRuns(bits);
        var mainModuleCount = bits.Length;

        if (barcode.AddOn is { } addOn)
        {
            addOn = ValidateAddOn(addOn);
            var addOnBits = BuildAddOnBits(addOn);
            var addOnRuns = ToRuns(addOnBits);

            // GS1: a 9-module light gap separates the main symbol from the add-on. The main
            // symbol's guard always ends on a bar, and the add-on's own start pattern ("01011")
            // begins with a 1-module light run, so 8 further modules of gap are prepended and
            // merged into it to keep the run sequence alternating.
            runs.Add(8 + addOnRuns[0]);
            runs.AddRange(addOnRuns.Skip(1));

            hriGroups.Add(new HriGroup(addOn, HriAnchor.Above, mainModuleCount + 9, SumRuns(addOnRuns) - addOnRuns[0]));

            quietRight = 5;
        }

        return new Encoded1D
        {
            Runs = runs,
            QuietZoneLeft = quietLeft,
            QuietZoneRight = quietRight,
            GuardExtensions = guardExtensions,
            HriGroups = hriGroups,
        };
    }

    private static List<GuardExtension> GuardExtensionsFor95Modules() =>
    [
        new GuardExtension(0, 3),
        new GuardExtension(45, 5),
        new GuardExtension(92, 3),
    ];

    private static string BuildEan13LikeBits(string digits13)
    {
        var sb = new StringBuilder(95).Append("101");
        var parity = EanTables.Ean13FirstDigitParity[digits13[0] - '0'];
        for (var i = 0; i < 6; i++)
        {
            var digit = digits13[1 + i] - '0';
            sb.Append(parity[i] == 'L' ? EanTables.L[digit] : EanTables.G[digit]);
        }

        sb.Append("01010");
        for (var i = 0; i < 6; i++)
        {
            var digit = digits13[7 + i] - '0';
            sb.Append(EanTables.R[digit]);
        }

        return sb.Append("101").ToString();
    }

    private static string BuildEan8Bits(string digits8)
    {
        var sb = new StringBuilder(67).Append("101");
        for (var i = 0; i < 4; i++) sb.Append(EanTables.L[digits8[i] - '0']);
        sb.Append("01010");
        for (var i = 0; i < 4; i++) sb.Append(EanTables.R[digits8[4 + i] - '0']);
        return sb.Append("101").ToString();
    }

    private static string BuildAddOnBits(string addOn)
    {
        var parity = addOn.Length == 2
            ? EanTables.Ean2Parity[int.Parse(addOn) % 4]
            : EanTables.Ean5Parity[ComputeEan5Checksum(addOn)];

        var sb = new StringBuilder().Append("01011");
        for (var i = 0; i < addOn.Length; i++)
        {
            if (i > 0) sb.Append("01");
            var digit = addOn[i] - '0';
            sb.Append(parity[i] == 'L' ? EanTables.L[digit] : EanTables.G[digit]);
        }

        return sb.ToString();
    }

    private static List<HriGroup> BuildEan13HriGroups(string digits13) =>
    [
        new HriGroup(digits13[..1], HriAnchor.OutsideLeft, 0, 0),
        new HriGroup(digits13[1..7], HriAnchor.Below, 3, 42),
        new HriGroup(digits13[7..13], HriAnchor.Below, 50, 42),
    ];

    private static List<HriGroup> BuildEan8HriGroups(string digits8) =>
    [
        new HriGroup(digits8[..4], HriAnchor.Below, 3, 28),
        new HriGroup(digits8[4..8], HriAnchor.Below, 31, 28),
    ];

    private static List<HriGroup> BuildUpcAHriGroups(string digits12) =>
    [
        new HriGroup(digits12[..1], HriAnchor.OutsideLeft, 0, 0),
        new HriGroup(digits12[1..6], HriAnchor.Below, 10, 35),
        new HriGroup(digits12[6..11], HriAnchor.Below, 50, 35),
        new HriGroup(digits12[11..12], HriAnchor.OutsideRight, 94, 0),
    ];

    private static List<double> ToRuns(string bits)
    {
        var runs = new List<double>();
        var i = 0;
        while (i < bits.Length)
        {
            var start = i;
            var c = bits[i];
            while (i < bits.Length && bits[i] == c) i++;
            runs.Add(i - start);
        }

        return runs;
    }

    private static double SumRuns(List<double> runs)
    {
        var total = 0.0;
        foreach (var r in runs) total += r;
        return total;
    }
}
