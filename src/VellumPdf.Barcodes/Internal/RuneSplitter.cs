// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Splits a string into a fixed number of roughly-equal parts on Unicode scalar boundaries,
/// never through a surrogate pair. Shared by <c>QrCode</c>'s Structured Append auto-split
/// (ISO/IEC 18004 §8) and <c>Pdf417Barcode</c>'s Macro PDF417 auto-split (ISO/IEC 15438 Annex H).
/// </summary>
internal static class RuneSplitter
{
    /// <summary>Splits <paramref name="content"/> into <paramref name="partCount"/> parts of nearly equal rune count, the first <c>content.Length % partCount</c> parts one rune longer.</summary>
    /// <exception cref="FormatException"><paramref name="content"/> contains an unpaired UTF-16 surrogate.</exception>
    internal static IReadOnlyList<string> SplitByRune(string content, int partCount)
    {
        var runeStarts = new List<int>();
        for (var i = 0; i < content.Length;)
        {
            runeStarts.Add(i);
            Rune rune;
            try
            {
                rune = Rune.GetRuneAt(content, i);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException($"\"{content}\" contains an unpaired UTF-16 surrogate at index {i}.", ex);
            }

            i += rune.Utf16SequenceLength;
        }

        runeStarts.Add(content.Length);
        var runeCount = runeStarts.Count - 1;
        var baseSize = runeCount / partCount;
        var remainder = runeCount % partCount;

        var parts = new string[partCount];
        var runeIndex = 0;
        for (var i = 0; i < partCount; i++)
        {
            var size = baseSize + (i < remainder ? 1 : 0);
            var charStart = runeStarts[runeIndex];
            runeIndex += size;
            parts[i] = content[charStart..runeStarts[runeIndex]];
        }

        return parts;
    }
}
