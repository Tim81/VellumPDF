// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Elements;

/// <summary>Which of a document's two running bands a report concerns.</summary>
/// <remarks>A report from a save holds one of these two values.</remarks>
public enum RunningBandKind
{
    /// <summary>The band drawn at the top of every page.</summary>
    /// <remarks>Corresponds to <see cref="Document.Header"/>.</remarks>
    Header,

    /// <summary>The band drawn at the bottom of every page.</summary>
    /// <remarks>Corresponds to <see cref="Document.Footer"/>.</remarks>
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
/// <remarks>
/// Counts only; the text itself is not carried. A default instance has every count at zero and
/// <see cref="Band"/> set to <see cref="RunningBandKind.Header"/>.
/// </remarks>
public readonly record struct BandTruncationWarning
{
    /// <summary>Which band was cut.</summary>
    /// <remarks>
    /// Any value is stored, including one the enumeration does not name. A report from a save
    /// holds <see cref="RunningBandKind.Header"/> or <see cref="RunningBandKind.Footer"/>.
    /// </remarks>
    public RunningBandKind Band { get; init; }

    /// <summary>
    /// The one-based page carrying the worst cut, not the first cut. With a <c>{page}</c> or
    /// <c>{pages}</c> token the resolved text differs per page, so the page that lost the most is
    /// the one that tells a caller how much shorter the template has to be.
    /// </summary>
    /// <remarks>Not refused. A zero or negative page number is stored as given.</remarks>
    public int PageNumber { get; init; }

    /// <summary>How many UTF-16 code units of the resolved text were drawn on that page.</summary>
    /// <remarks>
    /// Not refused. Can exceed <see cref="ResolvedCharacters"/> if the counts were built by
    /// hand.
    /// </remarks>
    public int DrawnCharacters { get; init; }

    /// <summary>
    /// The length of the resolved text on that page, after token substitution rather than the
    /// template's own length, since substitution changes it.
    /// </summary>
    /// <remarks>Not refused. A negative length is stored as given.</remarks>
    public int ResolvedCharacters { get; init; }

    /// <summary>Creates a truncation report from the four counts.</summary>
    /// <remarks>Arguments are stored as given. Nothing is validated.</remarks>
    public BandTruncationWarning(
        RunningBandKind Band,
        int PageNumber,
        int DrawnCharacters,
        int ResolvedCharacters)
    {
        this.Band = Band;
        this.PageNumber = PageNumber;
        this.DrawnCharacters = DrawnCharacters;
        this.ResolvedCharacters = ResolvedCharacters;
    }

    /// <summary>Copies the four fields into the given variables.</summary>
    /// <remarks>
    /// Order is Band, PageNumber, DrawnCharacters, ResolvedCharacters, the order of the
    /// constructor. <see cref="DroppedCharacters"/> is not included.
    /// </remarks>
    public void Deconstruct(
        out RunningBandKind Band,
        out int PageNumber,
        out int DrawnCharacters,
        out int ResolvedCharacters)
    {
        Band = this.Band;
        PageNumber = this.PageNumber;
        DrawnCharacters = this.DrawnCharacters;
        ResolvedCharacters = this.ResolvedCharacters;
    }

    /// <summary>How many UTF-16 code units the cut dropped on that page.</summary>
    /// <remarks>
    /// <see cref="ResolvedCharacters"/> minus <see cref="DrawnCharacters"/>. Can be negative
    /// if those two were built by hand.
    /// </remarks>
    public int DroppedCharacters => ResolvedCharacters - DrawnCharacters;
}
