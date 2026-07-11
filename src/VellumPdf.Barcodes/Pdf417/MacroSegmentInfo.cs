// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Pdf417;

/// <summary>
/// A symbol's position within a Macro PDF417 set (ISO/IEC 15438 Annex H): its 0-based segment
/// index, the file id shared by the whole set, whether it is the set's last segment, and the
/// optional fields to carry. Only the last segment carries a control-block terminator, and, by
/// convention (matching zxing's decoder), only the last segment carries optional fields.
/// </summary>
/// <param name="SegmentIndex">The symbol's 0-based position within the set (0-99998).</param>
/// <param name="FileId">The non-negative identifier every symbol in the set shares.</param>
/// <param name="IsLast">Whether this is the set's last segment, which alone carries the terminator codeword and any optional fields.</param>
/// <param name="Options">The optional fields to encode. Ignored unless <paramref name="IsLast"/> is true.</param>
internal readonly record struct MacroSegmentInfo(int SegmentIndex, int FileId, bool IsLast, MacroPdf417Options? Options);
