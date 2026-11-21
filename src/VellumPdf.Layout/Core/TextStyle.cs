// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;

namespace VellumPdf.Layout.Core;

/// <summary>Typography properties applied to a run of text.</summary>
/// <remarks>
/// Refusals on <see cref="FontSize"/>, <see cref="Leading"/> and <see cref="FontRef"/>, and the
/// exception on <see cref="LinkUri"/>, fire from calls such as
/// <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/>, the other save overloads and
/// <see cref="VellumPdf.Layout.Rendering.DocumentRenderer.Render"/>, not from the property setter.
/// </remarks>
public sealed class TextStyle
{
    /// <summary>Creates a style with Helvetica, 12 pt, auto leading, and black text.</summary>
    /// <remarks>
    /// Same values as <see cref="Default"/>, as a new instance.
    /// </remarks>
    public TextStyle() { }

    /// <summary>A style with default values (Helvetica, 12 pt, auto leading, black).</summary>
    /// <remarks>
    /// One shared instance. Every property of this type is init-only, so it cannot change.
    /// <para>Its font is Helvetica, which is not embedded. Elements without a style of their own
    /// fall back to it, and under a PDF/A or PDF/UA
    /// <see cref="VellumPdf.Layout.Document.Conformance"/> the save reports each as
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.UnembeddedFont"/>; see
    /// <see cref="FontRef"/>.</para>
    /// </remarks>
    public static readonly TextStyle Default = new();

    /// <summary>
    /// The font to use. Accepts a <see cref="Standard14"/> value (implicit conversion)
    /// or an <see cref="EmbeddedFontHandle"/> returned by <c>Document.UseTrueTypeFont</c>.
    /// </summary>
    /// <remarks>
    /// Stored as given. A null handle saves in Helvetica, and a handle from another document is
    /// refused when the saving document defines no font under its resource name; see
    /// <see cref="VellumPdf.Document.PdfDocument.RegisterEmbeddedFontUsage"/>. A
    /// Standard-14 value the enumeration does not name makes the save throw once the font is
    /// selected on a page, which an empty table cell or list item with this style also does.
    /// <para><see cref="Standard14.Symbol"/> and <see cref="Standard14.ZapfDingbats"/> measure
    /// every character as zero width at any finite size (#470). A paragraph set wholly in either
    /// font is therefore never wrapped. A centred or right-aligned line set wholly in either font
    /// is placed as if it had no width. A justified line holding text in either font can run past
    /// the right edge. A run that follows a Symbol or
    /// ZapfDingbats run on the same line, in a different <see cref="TextStyle"/> instance, can
    /// start where that run starts and be drawn over it.</para>
    /// <para>A Standard-14 font writes a character WinAnsiEncoding cannot represent as
    /// <c>?</c>. The save lists each such character in your text in
    /// <see cref="VellumPdf.Layout.Document.Diagnostics"/> as
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.UnencodableCharacter"/>, which also says
    /// what it leaves out, and with <see cref="VellumPdf.Layout.Document.StrictDiagnostics"/> set
    /// it throws <see cref="InvalidOperationException"/> instead of saving. <b>Do not pass</b> text
    /// the font cannot encode; use an embedded font. A later major version refuses it
    /// always.</para>
    /// <para>An embedded font writes a character it has no glyph for as glyph 0, the <c>.notdef</c>
    /// glyph. The save lists each such character and font in
    /// <see cref="VellumPdf.Layout.Document.Diagnostics"/> as
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.NotdefGlyph"/>, with the clause that
    /// forbids it under a PDF/A or PDF/UA claim, and still saves. The font's ToUnicode map gives
    /// glyph 0 the one entry U+FFFD. <b>Do not pass</b> text the font cannot draw to such a
    /// document. A later major version refuses text the font cannot draw, and a Standard 14 font,
    /// under either claim.</para>
    /// <para>A Standard 14 font is not embedded. While
    /// <see cref="VellumPdf.Layout.Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/>, each element whose style selects one
    /// is listed in <see cref="VellumPdf.Layout.Document.Diagnostics"/> as
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.UnembeddedFont"/>, once per element and
    /// font, and the save still succeeds. With
    /// <see cref="VellumPdf.Layout.Document.StrictDiagnostics"/> set, either report refuses the
    /// save. <b>Do not pass</b> a Standard 14 font under either claim. A later major version
    /// refuses text the font cannot draw, and a Standard 14 font, under either claim.</para>
    /// <para>A <see cref="VellumPdf.Layout.Elements.Preformatted"/> refuses a Standard-14 value the
    /// enumeration does not name, Symbol and ZapfDingbats from its constructor, with
    /// <see cref="ArgumentException"/>, and under a conformance claim its save refuses a font that
    /// is not embedded.</para>
    /// </remarks>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from <see cref="VellumPdf.Layout.Document.Save(System.IO.Stream)"/> and the other
    /// save overloads, not from this property, when the reference holds a <see cref="Standard14"/>
    /// value the enumeration does not name and the font is selected on a page.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from the save, not from this property, when text in this style holds an unpaired
    /// surrogate and is measured in an embedded font. An unpaired surrogate is a UTF-16 code unit
    /// from U+D800 to U+DFFF without its partner. The built-in elements measure their text during
    /// layout, and a running band can measure part of its template that it then does not draw. Text
    /// drawn without being measured, as a custom renderer can draw it, has the surrogate replaced
    /// by U+FFFD instead. <c>ParamName</c> is <c>s</c>. A pair split between two runs of a
    /// paragraph that share one style instance is joined and measured as a pair, so it saves; split
    /// between runs of different instances, each half is unpaired and the save throws.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, when
    /// <see cref="VellumPdf.Layout.Document.StrictDiagnostics"/> is set and text in this style is
    /// written as glyph 0, or the style selects a Standard 14 font under a conformance claim; see
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="VellumPdf.Layout.LayoutDiagnosticCode.UnembeddedFont"/>.
    /// </exception>
    public FontReference FontRef { get; init; } = Standard14.Helvetica;

    /// <summary>
    /// Convenience accessor for the Standard-14 font value.
    /// Valid only when <see cref="FontRef"/> is not an embedded font.
    /// Preserved for backward compatibility with existing code.
    /// </summary>
    /// <remarks>
    /// An invalid read is not refused. On an embedded <see cref="FontRef"/> the getter
    /// returns <see cref="Standard14.Helvetica"/>, because that is what
    /// <see cref="FontReference.Standard14"/> returns on an embedded reference. Check
    /// <see cref="FontReference.IsEmbedded"/> first.
    /// <para>Setting it replaces <see cref="FontRef"/>, and initialisers run in the order
    /// written. <c>{ FontRef = handle, Font = Standard14.Courier }</c> ends in Courier, and the
    /// reverse order ends in the embedded font. Set one of the two, not both.</para>
    /// </remarks>
    /// <exception cref="IndexOutOfRangeException">
    /// Raised from a later save, as described on <see cref="FontRef"/>, when set to a value the
    /// enumeration does not name.
    /// </exception>
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
    /// <para>Each paragraph run, and a heading's text, is checked when it holds a character other
    /// than white space. The no-break spaces U+00A0, U+202F and U+2007 count as such characters; a
    /// tab and other white space do not. A table cell or list item is checked whatever its text holds.</para>
    /// <para>A <see cref="VellumPdf.Layout.Elements.Preformatted"/> refuses a size that is not
    /// finite, is below 0.000005 or is above 1e30 from its constructor, with
    /// <see cref="ArgumentException"/>, not from the save.</para>
    /// <para><b>Attention</b>: a size of zero or less is <b>not</b> refused. It reaches the content
    /// stream as a font operator that readers accept, so you still get a document. At zero the
    /// text is invisible. Below zero the glyphs are inverted. If you do not want either, check the
    /// value before you set it. A later major version will reject both.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when a checked size is not finite (see
    /// the remarks), or when a finite size makes the element too tall for the content area. On a
    /// paragraph style the message names the run and the size, as <c>"A paragraph run"</c>. A
    /// <see cref="VellumPdf.Layout.Elements.Heading"/> reports as <c>"A heading"</c> and a list item,
    /// or the marker laid out with it, as <c>"A list item"</c>. On a table-cell style the message names
    /// the row and cell instead.
    /// <para>On a <see cref="VellumPdf.Layout.Elements.RunningBand"/> style, which message fires
    /// depends on the band and on whether
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is set. Measured with
    /// <c>Height</c> left null, across all six combinations of the three non-finite sizes and the
    /// two bands: <c>NaN</c> gives the band-detailed too-tall message on both bands, naming both
    /// bands and showing which one's height reads <c>NaN</c>, but never the font size. Negative
    /// infinity names the footer band and the size, but on a header it meets the page-continuation
    /// cap and names neither. A fixed <c>Height</c> moves the throw to the band's own draw step,
    /// naming the band and the size, in five of the six. The exception is a footer whose font size
    /// is negative infinity, which gives the same message and type whether <c>Height</c> is fixed
    /// or null.</para>
    /// <para>The finite-size boundary is a property of the page, not of the value. Measured on a
    /// footer, <c>Height</c> null, A4, default 72pt margins: the last size that does not throw is
    /// 566, and 567 through 578 throw this exception as the content area shrinks toward zero. A
    /// different page moves both figures, so do not carry either to a different page.</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save when this style belongs to a
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null, and a
    /// sufficiently large size inflates the band's effective height until the page's content area
    /// has no positive size left. Measured on a footer, that is <b>579</b> upward, and positive
    /// infinity is the far end of the same range. The message names the content area, not the
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
    /// <para>A <see cref="VellumPdf.Layout.Elements.Preformatted"/> refuses a non-finite leading
    /// from its constructor, with <see cref="ArgumentException"/>. Zero and negative still mean
    /// automatic.</para>
    /// <para>A non-finite value is <b>not</b> refused, and it does not reach the content stream
    /// either. The page is emitted with the text placed as though you had asked for automatic
    /// leading. A later major version will reject it.</para>
    /// <para>A large finite value reaches the generic too-tall exception on its own, with no band
    /// involved: the content
    /// area stays the size the page and margins make it, and the element outgrows it. On a
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> style whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null the content area
    /// does shrink as well, since <see cref="EffectiveLeading"/> also feeds the band's own height,
    /// so a band reaches the throw from both directions at once. Header and footer take the same
    /// route at the same value: the magnitude decides, not which band the style is attached
    /// to.</para>
    /// <para>Every numeric boundary here is a property of the page, not of the value. Measured on
    /// a footer, <c>Height</c> null, A4, default 72pt margins: the last leading that does not
    /// throw is <b>679</b>. From 680 through 693 the leading throws
    /// <see cref="InvalidOperationException"/> while the content area shrinks from 13.9pt to
    /// 0.9pt, and every finite value from 694 up throws <see cref="ArgumentException"/> once the
    /// content area itself goes non-positive. Positive infinity is the exception: being
    /// non-finite it falls through to automatic leading, as the paragraph above says, and throws
    /// nothing. On a 300 by 300pt page with 10pt margins the first throwing leading is <b>262</b>,
    /// not 694, so do not carry either figure to a different page. A fixed <c>Height</c> avoids
    /// both routes through the band, since the band's height then stops depending on the
    /// leading.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save rather than from this property, when the leading is large enough on its
    /// own that the page's content area is left positive but too small for the element. A
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> style whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null reaches the same
    /// exception the other way round, by shrinking that area through the band's height.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save when this style belongs to a
    /// <see cref="VellumPdf.Layout.Elements.RunningBand"/> whose
    /// <see cref="VellumPdf.Layout.Elements.RunningBand.Height"/> is left null, once the leading
    /// is large enough that the content area has no positive size left. Not reachable without a
    /// band: a page's margins do not move when a plain paragraph's leading grows, so the leading
    /// alone cannot drive the content area to zero, the way a band's height drives it. The message
    /// names the content area, not the leading.
    /// </exception>
    public double Leading { get; init; } = 0;  // 0 = auto (font-size * 1.2)

    /// <summary>The text colour. Defaults to <see cref="ColorRgb.Black"/>.</summary>
    /// <remarks>
    /// Stored as given. Channels are not clamped. A channel that is not finite is refused where it is
    /// written; see <see cref="Document"/> and <see cref="ColorRgb"/>. In a paragraph run, a heading, a
    /// list item or a table cell that draws text, the element's own check names it. On a running band,
    /// and on a band that draws nothing, <see cref="VellumPdf.Canvas.PdfCanvas"/> decides: it refuses
    /// the value when the band is drawn, and an empty template writes no colour. <b>Do not pass</b> a
    /// non-finite channel: 3.0 refuses it. A <see cref="VellumPdf.Layout.Elements.Preformatted"/>
    /// refuses a channel that is not a number from 0 to 1 from its constructor, with
    /// <see cref="ArgumentException"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when a channel is <c>NaN</c> or infinite on a paragraph run, heading, list
    /// item or table cell that draws text. The message names the element and the channel. A finite
    /// channel outside 0 to 1 is not refused.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Raised from a save, when a channel is <c>NaN</c> or infinite on a running band that is drawn.
    /// The message says PDF does not support NaN or Infinity as a real number.
    /// </exception>
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
    /// When non-null, text rendered with this style will be wrapped in a /Link annotation pointing
    /// to this URI, except in table cells and running bands. Use a full URI string (e.g.
    /// "https://example.com").
    /// </summary>
    /// <remarks>
    /// <b>Attention</b>: the string is not validated. Empty and <c>not a uri</c> are written into a
    /// <c>/URI</c> action as given, and so is an absolute URI of any scheme, such as
    /// <c>javascript:alert(1)</c>. Non-ASCII characters are percent-encoded as UTF-8, and an
    /// unpaired surrogate becomes U+FFFD first. Table cells and running bands silently drop the
    /// link (#475). In a list item the marker is linked as well as the text. The link is written
    /// untagged and without an alternate description, whatever the conformance, and no exception
    /// reports it. In a document whose <c>Conformance</c> is PDF/UA-1, the link breaks ISO
    /// 14289-1, 7.18.5 (#550). A link whose rectangle lies wholly outside the page's crop box is
    /// exempt: clause 7.18.1 lifts the requirements of clause 7.18 for it. On a justified line,
    /// in a standard-14 or an embedded face, the link's rectangle starts where the linked text's
    /// first glyph is drawn and ends where the text that follows it on the line starts, spaces
    /// stretched included, or, when the linked text closes the line, where its last glyph ends.
    /// A space that closes the linked run is inside the rectangle, and so is the stretch after it.
    /// A run that draws no glyph gets no annotation. A tab with no stop to its right is drawn as a
    /// space, in a standard-14 or an embedded face, and its link covers that space, unless the
    /// space is dropped because the line ends there. A line that is not justified, and the last line of a justified paragraph, use the unstretched widths they are
    /// drawn with.
    /// <para>Do not pass a value that is not an absolute URI. A later major version will refuse
    /// one.</para>
    /// <para>A <see cref="VellumPdf.Layout.Elements.Preformatted"/> refuses any value from its
    /// constructor, with <see cref="ArgumentException"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when linked text is placed at a non-finite position, for example through
    /// a bottom of negative infinity in a renderer's result (see <see cref="LayoutResult.Full"/>). The link's rectangle
    /// is written
    /// outside the content stream, and the save refuses a non-finite coordinate there. Without a
    /// link, the same paragraph or list-item text saves. A heading placed at a non-finite height
    /// throws either way, from its bookmark; see
    /// <see cref="VellumPdf.Layout.Elements.Heading.Margins"/>.
    /// </exception>
    public string? LinkUri { get; init; }

    /// <summary>Measures a string using whichever font this style references.</summary>
    /// <remarks>
    /// A null string throws <see cref="NullReferenceException"/> from the font metrics, not
    /// <see cref="ArgumentNullException"/>. A non-finite <see cref="FontSize"/> is multiplied
    /// through; this call does not refuse it. On an embedded font, measuring adds the characters to
    /// the font's subset; see <see cref="FontReference.MeasureString"/>.
    /// </remarks>
    /// <exception cref="NullReferenceException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// The font is embedded and <paramref name="text"/> holds an unpaired surrogate, a UTF-16 code
    /// unit from U+D800 to U+DFFF without its partner. <c>ParamName</c> is <c>s</c>. A Standard-14
    /// font measures such text without an exception.
    /// </exception>
    public double MeasureString(string text) => FontRef.MeasureString(text, FontSize);
}
