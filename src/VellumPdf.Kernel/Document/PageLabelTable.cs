// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Document;

/// <summary>The numbering style of a page-label range (ISO 32000-2, Table 161, entry <c>S</c>).</summary>
internal enum PageLabelNumbering
{
    /// <summary>No numeric part: the label is the prefix alone. <c>/S</c> is left out.</summary>
    None = 0,

    /// <summary>Decimal Arabic numerals (<c>/D</c>).</summary>
    Decimal,

    /// <summary>Upper-case Roman numerals (<c>/R</c>).</summary>
    UpperRoman,

    /// <summary>Lower-case Roman numerals (<c>/r</c>).</summary>
    LowerRoman,

    /// <summary>Upper-case letters, A to Z, then AA to ZZ, then AAA (<c>/A</c>).</summary>
    UpperLetters,

    /// <summary>Lower-case letters, a to z, then aa to zz, then aaa (<c>/a</c>).</summary>
    LowerLetters,
}

/// <summary>
/// A document's page-label ranges (#262): each range starts at a page index and says how that page and the
/// pages after it are numbered, until the next range. <see cref="Build"/> turns the table into the
/// <c>/PageLabels</c> number tree (ISO 32000-2, 12.4.2 and 7.9.7). Internal: no document member holds one yet,
/// and nothing writes the tree into a catalog.
/// </summary>
/// <remarks>
/// <b>What is refused.</b> <see cref="Set"/> throws at the call for a negative start index, a first number
/// below 1, a style that is not a defined <see cref="PageLabelNumbering"/>, a second range at a start already
/// in the table, and a prefix that holds an unpaired surrogate or encodes to more than 32,767 bytes.
/// <see cref="Build"/> throws <see cref="InvalidOperationException"/> for a range that starts at or after the
/// page count (past the last page), because that needs the page count.
/// <para><see cref="Build"/> reads the table and never changes it, so a save that is retried after a
/// precondition failure builds the same objects again.</para>
/// </remarks>
internal sealed class PageLabelTable
{
    // The prefix is written as UTF-16BE with a byte order mark (PdfLiteralString.FromUnicode): 2 bytes plus 2
    // per UTF-16 code unit. 16,382 units encode to 32,766 bytes and 16,383 to 32,768. ISO 19005-2, 6.1.13
    // limits a string in a PDF/A-2 file to 32,767 bytes; that part of the standard is not held in this
    // repository, and the validator's own limits rule enforces the same number.
    internal const int MaxPrefixLength = 16_382;

    private static readonly PdfName StyleKey = new("S");
    private static readonly PdfName PrefixKey = new("P");
    private static readonly PdfName StartKey = new("St");

    private readonly SortedDictionary<int, (PageLabelNumbering Style, string? Prefix, int FirstNumber)> _ranges = [];

    /// <summary>The number of ranges set.</summary>
    internal int Count => _ranges.Count;

    /// <summary>
    /// Starts a range at <paramref name="startPageIndex"/>. Its first page is numbered
    /// <paramref name="firstNumber"/>, the next one more, and so on until the next range starts.
    /// </summary>
    /// <param name="startPageIndex">Zero-based index of the first page of the range.</param>
    /// <param name="style">How the numeric part is written.</param>
    /// <param name="prefix">Text put before the numeric part; <see langword="null"/> or empty for none.</param>
    /// <param name="firstNumber">The number of the range's first page; 1 or more (12.4.2, entry <c>St</c>).</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startPageIndex"/> is negative, <paramref name="firstNumber"/> is below 1, or <paramref name="style"/> is not a defined value.</exception>
    /// <exception cref="ArgumentException">A range already starts at <paramref name="startPageIndex"/>, or <paramref name="prefix"/> holds an unpaired surrogate or is longer than 16,382 characters.</exception>
    internal void Set(int startPageIndex, PageLabelNumbering style, string? prefix, int firstNumber)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startPageIndex);
        if (!Enum.IsDefined(style))
            throw new ArgumentOutOfRangeException(nameof(style), style, "The numbering style is not a defined value.");
        if (firstNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(firstNumber), firstNumber, "The first number of a label range must be 1 or more (ISO 32000-2, Table 161, St).");
        if (prefix is not null)
        {
            if (prefix.Length > MaxPrefixLength)
                throw new ArgumentException($"A label prefix has {prefix.Length} characters; the limit is {MaxPrefixLength}.", nameof(prefix));
            // PdfLiteralString.FromUnicode copies UTF-16 code units without checking them, so an unpaired
            // surrogate would be written as a code unit no reader can decode.
            for (var i = 0; i < prefix.Length; i++)
            {
                if (char.IsHighSurrogate(prefix[i]) && i + 1 < prefix.Length && char.IsLowSurrogate(prefix[i + 1]))
                    i++;
                else if (char.IsSurrogate(prefix[i]))
                    throw new ArgumentException($"A label prefix holds an unpaired surrogate at index {i}.", nameof(prefix));
            }
        }
        if (!_ranges.TryAdd(startPageIndex, (style, prefix, firstNumber)))
            throw new ArgumentException($"A label range already starts at page index {startPageIndex}.", nameof(startPageIndex));
    }

    /// <summary>
    /// Returns the root of the <c>/PageLabels</c> number tree, which is not registered; its other nodes, if
    /// any, are. Returns <see langword="null"/> for an empty table whatever the page count is, and leaves the
    /// registry alone. When no range starts at page 0, one is added there that numbers in decimal from 1,
    /// because 12.4.2 says the tree "shall include a value for page index 0"; those pages then read 1, 2, ...
    /// as they do in a document without labels.
    /// </summary>
    /// <param name="registry">Where the tree's nodes, if more than one, are registered.</param>
    /// <param name="pageCount">The number of pages in the document.</param>
    /// <exception cref="ArgumentNullException"><paramref name="registry"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A range starts at or after <paramref name="pageCount"/>; ISO 32000-2, Table 29 (<c>PageLabels</c>) says each page index "shall denote the first page in a labelling range".</exception>
    internal PdfDictionary? Build(PdfObjectRegistry registry, int pageCount)
    {
        ArgumentNullException.ThrowIfNull(registry);
        if (_ranges.Count == 0)
            return null;

        var entries = new List<KeyValuePair<long, PdfObject>>(_ranges.Count + 1);
        foreach (var (start, range) in _ranges)
        {
            if (start >= pageCount)
                throw new InvalidOperationException($"A page label range starts at page index {start}, but the document has {pageCount} pages.");
            entries.Add(new KeyValuePair<long, PdfObject>(start, BuildValue(range.Style, range.Prefix, range.FirstNumber)));
        }
        if (!_ranges.ContainsKey(0))
            entries.Add(new KeyValuePair<long, PdfObject>(0, BuildValue(PageLabelNumbering.Decimal, null, 1)));
        return NumberTreeWriter.Write(entries, registry);
    }

    /// <summary>
    /// A page label dictionary, direct as in 12.4.2's example, with <c>/S</c>, <c>/P</c>, <c>/St</c> in that
    /// order. <c>/S</c> is left out for <see cref="PageLabelNumbering.None"/>; <c>/P</c> for a null or empty
    /// prefix, which adds nothing to the label (Table 161's note treats a missing and an empty <c>P</c> alike);
    /// <c>/St</c> when it is 1, its default. <c>/Type /PageLabel</c> is optional and not written.
    /// </summary>
    private static PdfDictionary BuildValue(PageLabelNumbering style, string? prefix, int firstNumber)
    {
        var dictionary = new PdfDictionary();
        if (style != PageLabelNumbering.None)
            dictionary.Set(StyleKey, StyleName(style));
        if (!string.IsNullOrEmpty(prefix))
            dictionary.Set(PrefixKey, PdfLiteralString.FromUnicode(prefix));
        if (firstNumber != 1)
            dictionary.Set(StartKey, firstNumber);
        return dictionary;
    }

    private static string StyleName(PageLabelNumbering style) => style switch
    {
        PageLabelNumbering.Decimal => "D",
        PageLabelNumbering.UpperRoman => "R",
        PageLabelNumbering.LowerRoman => "r",
        PageLabelNumbering.UpperLetters => "A",
        PageLabelNumbering.LowerLetters => "a",
        _ => throw new ArgumentOutOfRangeException(nameof(style), style, null),
    };
}
