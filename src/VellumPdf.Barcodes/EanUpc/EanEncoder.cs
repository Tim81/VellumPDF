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

    /// <summary>
    /// Normalizes and validates a UPC-E digit string, accepting it in any of four forms: 6
    /// digits (the compressed data alone; number system 0 is assumed), 7 digits (a leading
    /// number-system digit — 0 or 1 — plus the 6 compressed digits), 8 digits (as 7, plus a
    /// trailing check digit that is validated), or an 11/12-digit UPC-A number that compresses
    /// to a UPC-E symbol. Returns the canonical 8-digit form: number system, the 6 compressed
    /// digits, and the check digit — all derived from the expanded UPC-A, since UPC-E carries no
    /// check digit of its own.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="digits"/> has an unsupported length or contains a non-digit character.</exception>
    /// <exception cref="FormatException">
    /// The number system is not 0 or 1, a supplied check digit does not match the computed one,
    /// or an 11/12-digit input does not compress to a UPC-E symbol (no qualifying zero-suppression pattern).
    /// </exception>
    internal static string NormalizeAndValidateUpcE(string digits)
    {
        ArgumentNullException.ThrowIfNull(digits);

        foreach (var c in digits)
            if (!char.IsAsciiDigit(c))
                throw new ArgumentException($"UPC-E digits must be 0-9 (found '{c}').", nameof(digits));

        switch (digits.Length)
        {
            case 6:
                return BuildUpcECanonical(0, digits);

            case 7:
                return BuildUpcECanonical(RequireUpcENumberSystem(digits[0]), digits[1..]);

            case 8:
                {
                    var numberSystem = RequireUpcENumberSystem(digits[0]);
                    var canonical = BuildUpcECanonical(numberSystem, digits[1..7]);
                    var providedCheck = digits[7] - '0';
                    var computedCheck = canonical[7] - '0';
                    if (providedCheck != computedCheck)
                        throw new FormatException(
                            $"UPC-E check digit mismatch: '{digits}' carries check digit {providedCheck}, but {computedCheck} was expected.");
                    return canonical;
                }

            case 11:
            case 12:
                {
                    var upcA12 = NormalizeAndValidate(EanSymbology.UpcA, digits);
                    var (numberSystem, six) = CompressUpcAToUpcE(upcA12);
                    return BuildUpcECanonical(numberSystem, six);
                }

            default:
                throw new ArgumentException(
                    $"UPC-E requires 6, 7 or 8 digits (compressed form) or 11/12 digits (a compressible UPC-A number), but got {digits.Length}.",
                    nameof(digits));
        }
    }

    private static int RequireUpcENumberSystem(char c)
    {
        var ns = c - '0';
        if (ns is not (0 or 1))
            throw new FormatException($"UPC-E number system must be 0 or 1 (found {ns}).");
        return ns;
    }

    /// <summary>Builds the canonical 8-digit UPC-E form (number system, 6 digits, check digit) by expanding to UPC-A to derive the check digit.</summary>
    private static string BuildUpcECanonical(int numberSystem, string sixDigits)
    {
        var upcAData = ExpandUpcEToUpcA(numberSystem, sixDigits);
        var check = ComputeCheckDigit(upcAData);
        return string.Create(8, (numberSystem, sixDigits, check), static (span, state) =>
        {
            span[0] = (char)('0' + state.numberSystem);
            state.sixDigits.AsSpan().CopyTo(span[1..7]);
            span[7] = (char)('0' + state.check);
        });
    }

    /// <summary>
    /// Expands a UPC-E symbol's 6 compressed digits back to the 11-digit UPC-A data (number
    /// system plus manufacturer and product codes, check digit excluded) it represents, per the
    /// GS1 General Specifications UPC-E zero-suppression structure table: the last digit selects
    /// which of manufacturer/product carries the suppressed zeros.
    /// </summary>
    private static string ExpandUpcEToUpcA(int numberSystem, string six)
    {
        var lastDigit = six[5] - '0';
        string mfr, product;
        if (lastDigit is 0 or 1 or 2)
        {
            mfr = $"{six[0]}{six[1]}{six[5]}00";
            product = $"00{six[2]}{six[3]}{six[4]}";
        }
        else if (lastDigit == 3)
        {
            mfr = $"{six[..3]}00";
            product = $"000{six[3]}{six[4]}";
        }
        else if (lastDigit == 4)
        {
            mfr = $"{six[..4]}0";
            product = $"0000{six[4]}";
        }
        else
        {
            mfr = six[..5];
            product = $"0000{six[5]}";
        }

        // Every branch above must produce a 5-digit manufacturer code and a 5-digit product
        // code -- together with the single-digit number system, exactly the 11 data digits a
        // UPC-A check digit is computed over. This guard exists because that invariant was once
        // violated silently: the 5-9 branch passed all 6 compressed digits as the manufacturer
        // code, producing a 12-character expansion and a wrong check digit.
        if (mfr.Length != 5 || product.Length != 5)
            throw new InvalidOperationException(
                $"UPC-E expansion invariant violated: expected a 5-digit manufacturer code and " +
                $"a 5-digit product code, got {mfr.Length} and {product.Length} digits (last digit {lastDigit}).");

        return $"{numberSystem}{mfr}{product}";
    }

    /// <summary>
    /// Compresses an 11-data-digit UPC-A number system into its UPC-E form, checking the
    /// zero-suppression patterns from most to least specific (GS1 General Specifications UPC-E
    /// structure table, ordered by the resulting UPC-E symbol's last digit: 0/1/2, then 3, then
    /// 4, then 5-9).
    /// </summary>
    /// <exception cref="FormatException">The number system is not 0 or 1, or no zero-suppression pattern applies.</exception>
    private static (int NumberSystem, string SixDigits) CompressUpcAToUpcE(string upcA12)
    {
        var numberSystem = upcA12[0] - '0';
        if (numberSystem is not (0 or 1))
            throw new FormatException($"UPC-E requires UPC-A number system 0 or 1 to compress (found {numberSystem}).");

        var mfr = upcA12[1..6];
        var product = upcA12[6..11];

        string six;
        if ((mfr[2..5] is "000" or "100" or "200") && product[..2] == "00")
            six = $"{mfr[0]}{mfr[1]}{product[2]}{product[3]}{product[4]}{mfr[2]}";
        else if (mfr[3..5] == "00" && product[..3] == "000")
            six = $"{mfr[..3]}{product[3]}{product[4]}3";
        else if (mfr[4] == '0' && product[..4] == "0000")
            six = $"{mfr[..4]}{product[4]}4";
        else if (product[..4] == "0000" && product[4] is >= '5' and <= '9')
            six = $"{mfr}{product[4]}";
        else
            throw new FormatException($"'{upcA12}' does not compress to a UPC-E symbol (no qualifying zero-suppression pattern).");

        return (numberSystem, six);
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

            case EanSymbology.UpcE:
                bits = BuildUpcEBits(barcode.Digits);
                hriGroups = BuildUpcEHriGroups(barcode.Digits);
                guardExtensions = [new GuardExtension(0, 3), new GuardExtension(45, 6)];
                quietLeft = 9;
                quietRight = 7;
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

    /// <summary>
    /// Builds a UPC-E symbol's 51-module bit string: the left guard (<c>101</c>), the 6 data
    /// digits parity-coded by number system and check digit (GS1 General Specifications UPC-E
    /// parity table), and the special 6-module right guard (<c>010101</c> — unlike every other
    /// symbol in the family, UPC-E has no middle guard).
    /// </summary>
    private static string BuildUpcEBits(string digits8)
    {
        var numberSystem = digits8[0] - '0';
        var six = digits8[1..7];
        var check = digits8[7] - '0';
        var parity = (numberSystem == 0 ? EanTables.UpcESystem0Parity : EanTables.UpcESystem1Parity)[check];

        var sb = new StringBuilder(51).Append("101");
        for (var i = 0; i < 6; i++)
        {
            var digit = six[i] - '0';
            sb.Append(parity[i] == 'L' ? EanTables.L[digit] : EanTables.G[digit]);
        }

        return sb.Append("010101").ToString();
    }

    private static List<HriGroup> BuildUpcEHriGroups(string digits8) =>
    [
        new HriGroup(digits8[..1], HriAnchor.OutsideLeft, 0, 0),
        new HriGroup(digits8[1..7], HriAnchor.Below, 3, 42),
        new HriGroup(digits8[7..8], HriAnchor.OutsideRight, 50, 0),
    ];

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
