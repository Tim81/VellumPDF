// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes;

/// <summary>
/// Optional fields for a Macro PDF417 set (ISO/IEC 15438 Annex H). Every property is optional;
/// a null value simply leaves that field out of the control block. Passed to
/// <see cref="Pdf417Barcode.MacroSet(System.Collections.Generic.IReadOnlyList{string}, int, MacroPdf417Options)"/>,
/// these fields end up on the set's last symbol only, matching the convention zxing's decoder
/// expects them under.
/// </summary>
public sealed record MacroPdf417Options
{
    /// <summary>The original file's name (designator 0, Text Compaction). Null omits the field.</summary>
    public string? FileName { get; init; }

    /// <summary>
    /// The set's total segment count (designator 1, Numeric Compaction). Null lets
    /// <see cref="Pdf417Barcode.MacroSet(System.Collections.Generic.IReadOnlyList{string}, int, MacroPdf417Options)"/>
    /// fill in <c>parts.Count</c>, which is already known and free to include; set this explicitly
    /// only to declare a different logical total (e.g. more segments still to come).
    /// </summary>
    public int? SegmentCount { get; init; }

    /// <summary>
    /// The file's timestamp (designator 2, Numeric Compaction), converted to Unix epoch seconds
    /// when the control block is built. Null omits the field. Must not be before the epoch.
    /// </summary>
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>The sender's name (designator 3, Text Compaction). Null omits the field.</summary>
    public string? Sender { get; init; }

    /// <summary>The addressee's name (designator 4, Text Compaction). Null omits the field.</summary>
    public string? Addressee { get; init; }

    /// <summary>The original file's size, in bytes (designator 5, Numeric Compaction). Null omits the field. Must not be negative.</summary>
    public long? FileSize { get; init; }

    /// <summary>The original file's CCITT-16 CRC checksum (designator 6, Numeric Compaction). Null omits the field. Must be between 0 and 65535.</summary>
    public int? Checksum { get; init; }
}
