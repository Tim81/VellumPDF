// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A header or footer band repeated on every page.
/// Text supports <c>{page}</c> (current page number) and <c>{pages}</c> (total page count)
/// tokens, which are substituted at render time.
/// </summary>
public sealed class RunningBand
{
    /// <summary>Text template — may contain {page} and/or {pages}.</summary>
    /// <remarks>
    /// <b>A template too wide for the content box is truncated, not refused.</b> Before #365 it
    /// was drawn off the page at a negative coordinate with every glyph still written into the
    /// content stream, so the header or footer was invisible in every reader while its bytes
    /// were still paid for. It is now cut to fit, and the cut is reported through
    /// <c>Document.BandTruncations</c> and <c>DocumentRenderer.BandTruncations</c>.
    /// <para>The cut bounds the advance width, not the ink: side bearings and italic overhang
    /// can still paint a little past it, because nothing in this package sets a clip path.</para>
    /// <para><b>Do not rely on truncation for a template whose glyphs measure zero.</b> Control
    /// characters, the five undefined WinAnsi codes, and both symbolic standard-14 faces all
    /// measure zero width, so any length of them "fits" and is drawn in full.</para>
    /// <para>One report per band per render, naming the page that lost the most rather than the
    /// first page cut, because a <c>{page}</c> or <c>{pages}</c> token lengthens the resolved
    /// text as the number gains digits.</para>
    /// </remarks>
    public string Template { get; }

    /// <summary>The text style of the band.</summary>
    public TextStyle Style { get; }

    /// <summary>Horizontal alignment of the band text.</summary>
    /// <remarks>
    /// <b>Do not pass <see cref="HorizontalAlignment.Justify"/>.</b> It is not refused and not
    /// honoured: it falls through to left alignment. A single-line band has nothing to justify
    /// against, so there is no meaning to give it.
    /// </remarks>
    public HorizontalAlignment Alignment { get; }

    /// <summary>
    /// Reserved height in points. Defaults to <c>Style.EffectiveLeading + 4</c>.
    /// Caller may override for tighter/looser bands.
    /// </summary>
    /// <remarks>
    /// This reserves space; it does not scale the text. The band's own
    /// <see cref="TextStyle.FontSize"/> decides how large the glyphs are, so a height smaller
    /// than the text needs lets the band overlap the content rather than shrinking it.
    /// <para><b>Zero and a negative value are not refused</b>, and neither is useful: the
    /// height comes off the page's content box, so both give the band no room while it is still
    /// drawn, over the content. A later major version will reject them.</para>
    /// <para><b>A non-finite value is refused, but by none of the messages you would expect.</b>
    /// All three throw from <see cref="Document.Save(System.IO.Stream)"/> and none names the band
    /// height. Measured, one value at a time: positive infinity gives
    /// <see cref="ArgumentException"/> about the content area having no positive size; <c>NaN</c>
    /// gives <see cref="InvalidOperationException"/> about an element being too tall to fit;
    /// negative infinity gives the page-continuation cap. No file is written in any of the
    /// three.</para>
    /// </remarks>
    public double? Height { get; init; }

    /// <summary>Creates a running band from a text template, with optional style and alignment (defaults to centered).</summary>
    public RunningBand(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Template = template;
        Style = style ?? TextStyle.Default;
        Alignment = alignment;
    }

    /// <summary>Returns the effective band height (leading + small padding).</summary>
    public double EffectiveHeight => Height ?? (Style.EffectiveLeading + 4);

    /// <summary>Substitutes {page} and {pages} tokens.</summary>
    public string Resolve(int pageNumber, int totalPages) =>
        Template
            .Replace("{page}", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{pages}", totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
}
