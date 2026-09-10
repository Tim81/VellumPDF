// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Elements;

/// <summary>Which of a document's two running bands a report concerns.</summary>
public enum RunningBandKind
{
    /// <summary>The band drawn at the top of every page.</summary>
    Header,

    /// <summary>The band drawn at the bottom of every page.</summary>
    Footer,
}

/// <summary>
/// Reports that a running band's resolved text was wider than the content box and was cut to fit.
///
/// A band that does not fit used to be drawn off the page with every glyph still written into the
/// content stream, so the header or footer was absent from every viewer while its bytes were paid
/// for, and a caller had no way to learn this short of reading the content stream. Truncating makes
/// the loss visible in the page; this makes it detectable in code.
///
/// The text itself is deliberately not carried. Two existing channels bound what a diagnostic
/// retains — the reader's excerpt helper caps a quoted token and the conformance context caps a
/// finding's message — and a template can be arbitrarily long, so holding one here would turn a
/// caller's input into a comparably sized retained allocation. The counts are what a caller acts
/// on: they already hold the template, and now know how much of it survived.
/// </summary>
/// <param name="Band">Which band was cut.</param>
/// <param name="PageNumber">
/// The one-based page carrying the worst cut, not the first cut. With a <c>{page}</c> or
/// <c>{pages}</c> token the resolved text differs per page, so the page that lost the most is the
/// one that tells a caller how much shorter the template has to be.
/// </param>
/// <param name="DrawnCharacters">How many characters of the resolved text were drawn on that page.</param>
/// <param name="ResolvedCharacters">
/// The length of the resolved text on that page, after token substitution rather than the
/// template's own length, since substitution changes it.
/// </param>
public readonly record struct BandTruncationWarning(
    RunningBandKind Band,
    int PageNumber,
    int DrawnCharacters,
    int ResolvedCharacters)
{
    /// <summary>How many characters the cut dropped on that page.</summary>
    public int DroppedCharacters => ResolvedCharacters - DrawnCharacters;
}
