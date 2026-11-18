// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A header or footer band repeated on every page.
/// Text supports <c>{page}</c> (current page number) and <c>{pages}</c> (total page count)
/// tokens, which are substituted at render time.
/// </summary>
/// <remarks>
/// A null template throws from <see cref="Resolve"/> / save (#531). Overlong text is
/// truncated, not refused; see <see cref="Template"/>.
/// </remarks>
public sealed class RunningBand
{
    /// <summary>Text template — may contain {page} and/or {pages}.</summary>
    /// <remarks>
    /// Never null: the constructor refuses a null template (#531). A template that does not fit the
    /// content box is truncated, not refused;
    /// <see cref="VellumPdf.Layout.Rendering.DocumentRenderer.BandTruncations"/> says when it
    /// counts as not fitting. The cut is reported through <c>Document.BandTruncations</c> and
    /// <c>DocumentRenderer.BandTruncations</c>, so read one of those if you need to know it
    /// happened. A <see langword="null"/> template is a different matter: the constructor does not
    /// check it and <see cref="Resolve"/> dereferences it during the save, so pass an empty string
    /// for a band that draws no text.
    /// <para><b>NOTE</b>: before #365 an overlong template was drawn off the page at a negative
    /// coordinate, with every glyph still written into the content stream. The header or footer
    /// was then invisible in every reader while you paid for its bytes.</para>
    /// <para>The cut bounds the advance width, <b>not</b> the ink. Side bearings and italic
    /// overhang can still paint a little past it. Nothing in this package sets a clip path.</para>
    /// <para><b>Attention</b>: truncation does nothing for text whose glyphs measure zero, because
    /// each character is measured at the advance of the glyph the font gives it. Every glyph of
    /// <see cref="VellumPdf.Fonts.Standard14.Symbol"/> and
    /// <see cref="VellumPdf.Fonts.Standard14.ZapfDingbats"/> measures zero, and so do the control
    /// characters U+0000 to U+001F in every standard-14 font. Any length of such text fits and is
    /// drawn in full. Courier measures U+007F at 600 units where Helvetica measures it at zero. In
    /// an embedded font the control characters take the font's own widths: Calibri measures U+0000
    /// and U+000D at zero, and Arial measures every C0 control at 750 units.</para>
    /// <para>You get one report per band per render, naming the page that lost the most rather
    /// than the first page cut. A <c>{page}</c> or <c>{pages}</c> token lengthens the resolved
    /// text as the number gains digits, so the worst page is the one that tells you how much
    /// shorter the template has to be.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the band measures text that holds an unpaired surrogate in an
    /// embedded font, which can include part of the template it then does not draw; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public string Template { get; }

    /// <summary>The text style of the band.</summary>
    /// <remarks>
    /// A null style passed to the constructor is replaced by <see cref="TextStyle.Default"/>; any
    /// other is stored as given. Refusals on size, leading and font are on <see cref="TextStyle"/>
    /// and are raised from the save. A <see cref="TextStyle.LinkUri"/> in it is ignored (#475).
    /// <para>A non-finite channel in <see cref="TextStyle.Color"/> is refused where it is written; see
    /// <see cref="Document"/>. A band that draws nothing, because its template is empty or no part of
    /// it fits the content width, writes no colour, so the channel is not refused there: that saved a
    /// valid file on 1c61fa6 and still does. <b>Do not pass</b> a non-finite channel: 3.0 refuses
    /// it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the band measures text that holds an unpaired surrogate in an
    /// embedded font, which can include part of the template it then does not draw; see
    /// <see cref="TextStyle.FontRef"/>. It is also raised when <see cref="Height"/> is null and the
    /// style's size, positive infinity included, or a finite leading is large enough to leave the
    /// page's content area no positive size; see <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    public TextStyle Style { get; }

    /// <summary>Horizontal alignment of the band text.</summary>
    /// <remarks>
    /// <see cref="HorizontalAlignment.Justify"/> is neither refused nor honoured. It falls through
    /// to left alignment, and so does a value the enumeration does not name. A single-line band has
    /// nothing to justify against. The band's default is centred. Asking for justify loses the
    /// centring, and nothing reports it.
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
    /// drawn, over your content. 3.0 will reject them.</para>
    /// <para>A height that is not a finite number is not checked before the layout; see
    /// <see cref="Document"/>. The layout runs as it did on 1c61fa6. A <c>NaN</c> height is refused as
    /// too tall, positive infinity as a content area with no size, and negative infinity as too many
    /// page continuations on a header, but on a footer it writes <c>NaN</c> into the text matrix, which
    /// <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses. A band with an empty template draws nothing,
    /// so a negative-infinity footer height there saves when the body fits. <b>Do not pass</b> a
    /// non-finite height: 3.0 refuses it.</para>
    /// <para>A finite height large enough to leave the content area no room is a separate case, and
    /// shares the type again: measured on a footer, 684 already throws
    /// <see cref="InvalidOperationException"/> while the content area is still a positive 13.9pt,
    /// and so does every height from there to 697.88. At 697.89 the content area reaches exactly
    /// zero and the throw becomes <see cref="ArgumentException"/>; a header takes the same route at
    /// the same values.</para>
    /// <para>A refusal raised before the save writes anything leaves the file a path held as it was
    /// (#508).</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, when a finite height is large enough on
    /// its own that the content area is left with no positive size, or the height is positive
    /// infinity, or negative infinity on a footer that draws text and writes <c>NaN</c> into a text
    /// matrix, which the canvas refuses.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when the height is <c>NaN</c>, or negative infinity on a header, or a finite
    /// value large enough on its own that the content area is left positive but too small for the
    /// element. The messages differ for each cause, described above.
    /// </exception>
    public double? Height { get; init; }

    /// <summary>Creates a running band from a text template, with optional style and alignment (defaults to centered).</summary>
    /// <remarks>
    /// A null <paramref name="style"/> becomes <see cref="TextStyle.Default"/>. A null
    /// <paramref name="template"/> is refused here (#531). It used to be stored, and
    /// <see cref="Resolve"/> threw <see cref="NullReferenceException"/> from deep inside the
    /// renderer when any of the four save overloads laid the band out, a long way from the
    /// constructor that took the null. An empty template is not null and is accepted.
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="template"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the band measures text that holds an unpaired surrogate in an
    /// embedded font, which can include part of the template it then does not draw; see
    /// <see cref="TextStyle.FontRef"/>. It is also raised when <see cref="Height"/> is null and the
    /// style's size, positive infinity included, or a finite leading is large enough to leave the
    /// page's content area no positive size; see <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this constructor, when the style holds a <see cref="VellumPdf.Fonts.Standard14"/> value
    /// the enumeration does not name and the font is selected on a page; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    public RunningBand(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        ArgumentNullException.ThrowIfNull(template);
        Template = template;
        Style = style ?? TextStyle.Default;
        Alignment = alignment;
    }

    /// <summary>Returns the effective band height (leading + small padding).</summary>
    /// <remarks>
    /// <see cref="Height"/> when set, otherwise leading plus 4. A set non-finite or negative
    /// <see cref="Height"/> is returned as given.
    /// </remarks>
    public double EffectiveHeight => Height ?? (Style.EffectiveLeading + 4);

    /// <summary>Substitutes <c>{page}</c> and <c>{pages}</c> in <see cref="Template"/>.</summary>
    /// <remarks>
    /// Any other brace text is left as given. <see cref="Template"/> is never null (#531), so this
    /// does not throw for it.
    /// </remarks>
    public string Resolve(int pageNumber, int totalPages) =>
        Template
            .Replace("{page}", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{pages}", totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
}
