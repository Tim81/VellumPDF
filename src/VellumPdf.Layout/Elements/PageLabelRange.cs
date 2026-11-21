// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// How a run of pages is numbered in a viewer's page box: a style, an optional prefix and the
/// number of the first page (ISO 32000-2, 12.4.2). A range lasts until the next one starts.
/// </summary>
/// <remarks>
/// <b>A bad style, first number or prefix is refused by the constructor.</b> An undefined
/// <see cref="PdfPageLabelStyle"/> and a first number below 1 throw
/// <see cref="ArgumentOutOfRangeException"/>. A prefix with an unpaired surrogate, or one longer
/// than 16,382 characters, throws <see cref="ArgumentException"/>; 16,382 UTF-16 characters are the
/// most that fit the 32,767 bytes ISO 19005-2, 6.1.13 allows a string, which the library applies to
/// the prefix whatever the conformance level. A first number whose own label, prefix included,
/// would be longer than 32,767 characters throws <see cref="ArgumentOutOfRangeException"/> too,
/// naming <c>firstNumber</c>. With no prefix, a letter label is refused from 851,943 up (it is
/// <c>(number - 1) / 26 + 1</c> characters, so 851,942 is accepted at exactly 32,767), and a
/// Roman-numeral label from 32,767,001 up (one <c>M</c> per thousand, then the numeral for the
/// last three digits, so 32,767,000 is accepted and 32,766,888 is not). A decimal label has at
/// most 10 digits, so it stays within the limit. A prefix lowers both limits: each of its
/// characters takes one from the label, which is 26 numbers for letters and 1,000 for Roman
/// numerals.
/// <para>Where a range starts is not set here. <see cref="Document.PageLabel"/> starts one at the
/// first page and <see cref="PageBreak.PageLabel"/> at the page the next element lands on. One
/// instance may be used in several places and several documents, because it never changes.</para>
/// <para><b>Do not give a range a first number so large that a later page's label passes 32,767
/// characters.</b> The constructor checks the first page's label only. A save refuses a later
/// page whose label is too long, with <see cref="InvalidOperationException"/> naming the page,
/// when a running band shows it as <c>{page}</c>; the document cannot be saved again, because the
/// layout had started.</para>
/// </remarks>
public sealed class PageLabelRange
{
    /// <summary>Creates a range.</summary>
    /// <remarks>
    /// <b>Every argument is checked here.</b> See the type for what is refused. A
    /// <paramref name="prefix"/> of <see langword="null"/> or an empty string is no prefix, and a
    /// <see cref="PdfPageLabelStyle.None"/> style with no prefix labels its pages with the empty
    /// string. The prefix is written as the label dictionary's <c>/P</c> string, and is never
    /// scanned for <c>{page}</c>: a running band prints a page's label as it is.
    /// </remarks>
    /// <param name="style">How the numeric part is written.</param>
    /// <param name="prefix">Text put before the numeric part.</param>
    /// <param name="firstNumber">The number of the range's first page; 1 or more.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="style"/> is not a member of
    /// <see cref="PdfPageLabelStyle"/>, <paramref name="firstNumber"/> is below 1, or the first
    /// page's label would be longer than 32,767 characters.</exception>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> holds an unpaired surrogate or
    /// is longer than 16,382 characters.</exception>
    public PageLabelRange(PdfPageLabelStyle style, string? prefix = null, int firstNumber = 1)
    {
        if (!Enum.IsDefined(style))
            throw new ArgumentOutOfRangeException(nameof(style), style, "The numbering style is not a defined value.");
        if (firstNumber < 1)
            throw new ArgumentOutOfRangeException(nameof(firstNumber), firstNumber, "The first number of a label range must be 1 or more.");
        if (prefix is not null)
        {
            if (prefix.Length > PageLabelTable.MaxPrefixLength)
                throw new ArgumentException(
                    $"A label prefix has {prefix.Length} characters; the limit is {PageLabelTable.MaxPrefixLength}.", nameof(prefix));
            for (var i = 0; i < prefix.Length; i++)
            {
                if (char.IsHighSurrogate(prefix[i]) && i + 1 < prefix.Length && char.IsLowSurrogate(prefix[i + 1]))
                    i++;
                else if (char.IsSurrogate(prefix[i]))
                    throw new ArgumentException($"A label prefix holds an unpaired surrogate at index {i}.", nameof(prefix));
            }
        }

        try
        {
            _ = PageLabelFormatter.Format(style, prefix, firstNumber);
        }
        catch (ArgumentOutOfRangeException)
        {
            throw new ArgumentOutOfRangeException(
                nameof(firstNumber), firstNumber, "The label of the range's first page would be longer than 32767 characters.");
        }

        Style = style;
        Prefix = prefix;
        FirstNumber = firstNumber;
    }

    /// <summary>How the numeric part of a label is written.</summary>
    public PdfPageLabelStyle Style { get; }

    /// <summary>Text put before the numeric part, or <see langword="null"/> for none.</summary>
    public string? Prefix { get; }

    /// <summary>The number of the range's first page.</summary>
    public int FirstNumber { get; }
}
