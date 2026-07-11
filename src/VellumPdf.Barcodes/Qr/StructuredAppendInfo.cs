// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Qr;

/// <summary>
/// A symbol's position within a QR Code Structured Append set (ISO/IEC 18004 §8): its 0-based
/// index in the set, the set's total symbol count, and the parity byte every symbol in the set
/// shares.
/// </summary>
/// <param name="Index">The symbol's 0-based position within the set (0-15).</param>
/// <param name="Total">The set's total symbol count (1-16).</param>
/// <param name="Parity">The XOR of every byte of the original, un-split message data; identical across the whole set.</param>
internal readonly record struct StructuredAppendInfo(int Index, int Total, byte Parity);
