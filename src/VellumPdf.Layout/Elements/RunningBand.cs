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
    /// A template too wide for the content box is truncated, not refused. The cut is reported
    /// through <c>Document.BandTruncations</c> and <c>DocumentRenderer.BandTruncations</c>, so
    /// read one of those if you need to know it happened.
    /// <para><b>NOTE</b>: before #365 an overlong template was drawn off the page at a negative
    /// coordinate, with every glyph still written into the content stream. The header or footer
    /// was then invisible in every reader while you paid for its bytes.</para>
    /// <para>The cut bounds the advance width, <b>not</b> the ink. Side bearings and italic
    /// overhang can still paint a little past it. Nothing in this package sets a clip path.</para>
    /// <para><b>Attention</b>: truncation does nothing for a template whose glyphs measure zero. Control
    /// characters, the five undefined WinAnsi codes and both symbolic standard-14 faces all
    /// measure zero width, so any length of them fits and is drawn in full.</para>
    /// <para>You get one report per band per render, naming the page that lost the most rather
    /// than the first page cut. A <c>{page}</c> or <c>{pages}</c> token lengthens the resolved
    /// text as the number gains digits, so the worst page is the one that tells you how much
    /// shorter the template has to be.</para>
    /// </remarks>
    public string Template { get; }

    /// <summary>The text style of the band.</summary>
    public TextStyle Style { get; }

    /// <summary>Horizontal alignment of the band text.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> is neither refused <b>nor</b>
    /// honoured. It falls through to left alignment. A single-line band has nothing to justify
    /// against, so there is no meaning to give it.
    /// </remarks>
    public HorizontalAlignment Alignment { get; }

    /// <summary>
    /// Reserved height in points. Defaults to <c>Style.EffectiveLeading + 4</c>.
    /// Caller may override for tighter/looser bands.
    /// </summary>
    /// <remarks>
    /// This reserves space. It does not scale the text: the band's own
    /// <see cref="TextStyle.FontSize"/> decides how large the glyphs are. A height smaller than
    /// the text needs therefore lets the band overlap the content rather than shrinking it.
    /// <para><b>Attention</b>: zero and negative values are not refused, and neither is useful. The
    /// height comes off the page's content box, so both leave the band no room while it is still
    /// drawn, over your content. A later major version will reject them.</para>
    /// <para><c>NaN</c> and positive infinity are refused on both bands; each was measured on its
    /// own, because the two take different routes. Positive infinity gives
    /// <see cref="ArgumentException"/> about the content area having no positive size, and names
    /// the margins as the parameter. <c>NaN</c> gives <see cref="InvalidOperationException"/>
    /// about an element being too tall to fit, and that one does report the height, as
    /// <c>NaNpt of running bands</c>. Negative infinity is refused the same way, but only on a
    /// header, where it gives that same <see cref="InvalidOperationException"/> for a different
    /// reason, the page-continuation cap, and names nothing about the band. A finite height large
    /// enough shares the type again: measured on a footer, 684 already throws it while the
    /// content area is still a positive 13.9pt, and so does every height from there to 697.88.
    /// At 697.89 the content area reaches exactly zero and it crosses into
    /// <see cref="ArgumentException"/>; a header takes the same route at the same values. So one
    /// exception type covers three unrelated causes here, not one apiece.</para>
    /// <para>On a footer, negative infinity is not refused at all (#520). <c>Save</c> succeeds
    /// and writes a file that stays well formed, though its content stream stops conforming. The
    /// footer's vertical position is computed as the page height minus the margin minus the
    /// band's own height; with that height at negative infinity the position becomes positive
    /// infinity, and adding it back to the band height computes infinity plus negative infinity,
    /// which IEEE 754 gives as <c>NaN</c>. That is the literal token that lands in the footer's
    /// <c>Tm</c> operator where a coordinate belongs: <c>qpdf --check</c> exits 0 on the result,
    /// while <c>pdftotext</c> reports a syntax error and drops the footer text with it.</para>
    /// <para>Every case above that does throw does so before <c>Save</c> finishes writing. For
    /// the string overloads, <see cref="Document.Save(string)"/> and
    /// <see cref="Document.SaveAsync(string, System.Threading.CancellationToken)"/>, which open
    /// the file before the layout runs, a throw here still leaves a zero-byte file in place of
    /// whatever the path held (#508).</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, when the height is positive infinity,
    /// or any finite value large enough on its own that the content area is left with no
    /// positive size.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when the height is <c>NaN</c> on either band, negative infinity on a
    /// header, or a finite value large enough on its own that the content area is left positive
    /// but too small for the element. The messages differ for each cause, described above. A
    /// footer's negative infinity does not raise at all (#520).
    /// </exception>
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
