// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>Typography properties applied to a run of text.</summary>
public sealed class TextStyle
{
    /// <summary>A style with default values (Helvetica, 12 pt, auto leading, black).</summary>
    public static readonly TextStyle Default = new();

    /// <summary>
    /// The font to use. Accepts a <see cref="Standard14"/> value (implicit conversion)
    /// or an <see cref="EmbeddedFontHandle"/> returned by <c>Document.UseTrueTypeFont</c>.
    /// </summary>
    public FontReference FontRef { get; init; } = Standard14.Helvetica;

    /// <summary>
    /// Convenience accessor for the Standard-14 font value.
    /// Valid only when <see cref="FontRef"/> is not an embedded font.
    /// Preserved for backward compatibility with existing code.
    /// </summary>
    public Standard14 Font
    {
        get => FontRef.Standard14;
        init => FontRef = value;
    }

    /// <summary>
    /// The font size in points. Defaults to 12. Must be a finite number.
    /// </summary>
    /// <remarks>
    /// A non-finite size is refused. On a paragraph or heading style,
    /// <see cref="Document.Save(System.IO.Stream)"/> throws <see cref="InvalidOperationException"/>
    /// naming the run and the size. Every height in a layout is derived from the font size, so a
    /// non-finite size leaves nothing that can be measured or placed. A
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> style takes a band-specific route
    /// instead, detailed on the exception tags below.
    /// <para><b>Attention</b>: a size of zero or less is <b>not</b> refused. It reaches the content
    /// stream as a font operator that readers accept, so you still get a document. At zero the
    /// text is invisible. Below zero the glyphs are inverted. If you do not want either, check the
    /// value before you set it. A later major version will reject both.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when the size is not finite. On a
    /// paragraph style the message names the run and the size; a <see cref="VellumPdf.Layout.Elements.Heading"/>
    /// is laid out through the same paragraph code and reports the same way, as
    /// <c>"A paragraph run"</c>, not by the heading's own name.
    /// <para>On a <see cref="VellumPdf.Layout.Elements.RunningBand"/> style, which message fires
    /// depends on the band and on whether
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is set. Measured with
    /// <c>Height</c> left null: <c>NaN</c> gives the generic too-tall message on both bands,
    /// naming both (and which one is <c>NaN</c>), but not the size. Negative infinity names the
    /// footer band and the size, but on a header it meets the page-continuation cap and names
    /// neither. A fixed <c>Height</c> moves the throw to the band's own draw step, naming the
    /// band and the size, for five of these six band/value pairs; the sixth, a footer already at
    /// negative infinity, gives the same message and type either way. A sufficiently large but
    /// still finite size reaches this same too-tall message before it reaches positive infinity;
    /// measured on a footer with <c>Height</c> null, 570 to 576 all do.</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save when this style belongs to a
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null, and a
    /// sufficiently large size (positive infinity is simply the extreme of the same range; 580
    /// upward were measured on a footer) inflates the band's effective height until the
    /// page's content area has no positive size left. The message names the content area, not the
    /// font size. A fixed <c>Height</c> routes positive infinity to
    /// <see cref="InvalidOperationException"/> instead, described above, because the height no
    /// longer depends on the font size.
    /// </exception>
    public double FontSize { get; init; } = 12;

    /// <summary>
    /// The line leading in points; 0 (the default) means auto, which is the font size times 1.2.
    /// </summary>
    /// <remarks>
    /// Any value at or below zero selects the automatic leading. There is therefore no way to
    /// ask for lines that overlap exactly.
    /// <para>A non-finite value is <b>not</b> refused, and it does not reach the content stream
    /// either. The page is emitted with the text placed as though you had asked for automatic
    /// leading. A later major version will reject it.</para>
    /// <para>A large finite value reaches two more throws from <c>Save</c>, on a
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> style whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null: this feeds
    /// <see cref="EffectiveLeading"/> into the band's own height, and a large enough value
    /// inflates that past the page. Header and footer take the same route at the same value: the
    /// magnitude decides, not which band the style is attached to. Measured: 670 does not throw,
    /// 680 throws <see cref="InvalidOperationException"/> for too little content area, and 694
    /// throws <see cref="ArgumentException"/> once the content area itself goes non-positive. A
    /// fixed <c>Height</c> avoids both, since the band's height then stops depending on the
    /// leading.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when this style belongs to a
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null, and the leading
    /// inflates the band past the page while the content area is still positive.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save under the same condition, once the leading is large enough that the
    /// content area has no positive size left. The message names the content area, not the
    /// leading.
    /// </exception>
    public double Leading { get; init; } = 0;  // 0 = auto (font-size * 1.2)

    /// <summary>The text colour. Defaults to <see cref="ColorRgb.Black"/>.</summary>
    public ColorRgb Color { get; init; } = ColorRgb.Black;

    /// <summary>
    /// The resolved leading: <see cref="Leading"/> when it is a positive finite number, otherwise
    /// the font size times 1.2.
    /// </summary>
    /// <remarks>
    /// The finiteness test is not decoration. A NaN leading fails <c>&gt; 0</c> and always fell
    /// through to the font size, but a positive-infinity leading passed it, making every height
    /// derived from it infinite, so the element reported that it could never fit and the caller was
    /// told their page was too small. Treating every non-finite leading as automatic removes that
    /// without refusing anything that used to render.
    /// </remarks>
    public double EffectiveLeading =>
        Leading > 0 && double.IsFinite(Leading) ? Leading : FontSize * 1.2;

    /// <summary>
    /// When non-null, text rendered with this style will be wrapped in a /Link
    /// annotation pointing to this URI. Use a full URI string (e.g. "https://example.com").
    /// </summary>
    public string? LinkUri { get; init; }

    /// <summary>Measures a string using whichever font this style references.</summary>
    public double MeasureString(string text) => FontRef.MeasureString(text, FontSize);
}
