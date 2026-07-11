// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Numerics;

namespace VellumPdf.Barcodes.Pdf417;

/// <summary>
/// Builds a Macro PDF417 control block (ISO/IEC 15438 Annex H): the codeword sequence appended
/// after a symbol's data codewords, inside the data region its Reed-Solomon error correction
/// covers (<see cref="Pdf417Encoder"/> appends it to the compacted content before computing
/// dimensions and error correction, so a decoder recovers it the same way it recovers ordinary
/// data). Layout: marker 928, a fixed 2-codeword segment index, the file id (a single codeword
/// here), then any optional fields (each introduced by 923 and a 1-codeword designator), and
/// finally the terminator 922 on the set's last segment only.
/// </summary>
internal static class MacroControlBlock
{
    private const int BeginControlBlock = 928;
    private const int BeginOptionalField = 923;
    private const int Terminator = 922;

    private const int DesignatorFileName = 0;
    private const int DesignatorSegmentCount = 1;
    private const int DesignatorTimestamp = 2;
    private const int DesignatorSender = 3;
    private const int DesignatorAddressee = 4;
    private const int DesignatorFileSize = 5;
    private const int DesignatorChecksum = 6;

    /// <summary>The decimal-digit width the segment index is zero-padded to before the leading-1/base-900 conversion.</summary>
    private const int SegmentIndexDigits = 5;

    /// <summary>The highest segment index the fixed 2-codeword field can hold (<see cref="SegmentIndexDigits"/> nines).</summary>
    internal const int MaxSegmentIndex = 99998;

    /// <summary>The highest file id a single codeword can hold.</summary>
    internal const int MaxFileId = 899;

    /// <summary>Builds the control block codewords for <paramref name="info"/>.</summary>
    internal static List<int> Build(MacroSegmentInfo info)
    {
        var codewords = new List<int> { BeginControlBlock };
        codewords.AddRange(EncodeSegmentIndex(info.SegmentIndex));

        // File id: a single codeword (0-899, ISO/IEC 15438 Annex H.2). A decoder reads file id
        // codewords one at a time, formatting each as a zero-padded 3-digit decimal and
        // concatenating, until it hits the next 923 (optional field) or 922 (terminator) codeword,
        // so one codeword equal to the file id itself round-trips (42 -> "042" -> 42). Whatever
        // immediately follows (an optional field, the terminator, or nothing at all on a non-last
        // segment) cleanly bounds it, but that boundary must actually be one of those two
        // codewords: see the Pdf417Encoder remark on why pad codewords never trail the control
        // block.
        codewords.Add(info.FileId);

        if (info.IsLast && info.Options is { } options)
        {
            AddOptionalField(codewords, DesignatorFileName, options.FileName, Pdf417HighLevelEncoder.EncodeTextValue);
            AddOptionalField(codewords, DesignatorSegmentCount, options.SegmentCount, Pdf417HighLevelEncoder.EncodeNumericValue);
            AddOptionalField(codewords, DesignatorTimestamp, options.Timestamp, Pdf417HighLevelEncoder.EncodeNumericValue);
            AddOptionalField(codewords, DesignatorSender, options.Sender, Pdf417HighLevelEncoder.EncodeTextValue);
            AddOptionalField(codewords, DesignatorAddressee, options.Addressee, Pdf417HighLevelEncoder.EncodeTextValue);
            AddOptionalField(codewords, DesignatorFileSize, options.FileSize, Pdf417HighLevelEncoder.EncodeNumericValue);
            AddOptionalField(codewords, DesignatorChecksum, options.Checksum, Pdf417HighLevelEncoder.EncodeNumericValue);
        }

        if (info.IsLast) codewords.Add(Terminator);

        return codewords;
    }

    /// <summary>
    /// Encodes <paramref name="segmentIndex"/> (0-<see cref="MaxSegmentIndex"/>) as exactly 2
    /// base-900 codewords, following the same "prepend a synthetic leading 1 digit" convention
    /// Numeric Compaction uses (<see cref="Pdf417HighLevelEncoder"/>'s numeric encoder): zero-pad
    /// to <see cref="SegmentIndexDigits"/> decimal digits, prepend "1", convert to base 900. A
    /// decoder strips that leading digit back off after converting the 2 codewords back to
    /// decimal, which is why it must be present: omitting it (a plain <c>index/900, index%900</c>
    /// split) decodes to a value zxing-cpp rejects whenever the result doesn't start with '1'.
    /// </summary>
    private static int[] EncodeSegmentIndex(int segmentIndex)
    {
        var padded = segmentIndex.ToString(CultureInfo.InvariantCulture).PadLeft(SegmentIndexDigits, '0');
        var value = BigInteger.Parse("1" + padded, CultureInfo.InvariantCulture);
        return [(int)(value / 900), (int)(value % 900)];
    }

    private static void AddOptionalField(List<int> codewords, int designator, string? value, Func<string, List<int>> encode)
    {
        if (value is null) return;
        codewords.Add(BeginOptionalField);
        codewords.Add(designator);
        codewords.AddRange(encode(value));
    }

    private static void AddOptionalField(List<int> codewords, int designator, int? value, Func<string, List<int>> encode) =>
        AddOptionalField(codewords, designator, value?.ToString(CultureInfo.InvariantCulture), encode);

    private static void AddOptionalField(List<int> codewords, int designator, long? value, Func<string, List<int>> encode) =>
        AddOptionalField(codewords, designator, value?.ToString(CultureInfo.InvariantCulture), encode);
}
