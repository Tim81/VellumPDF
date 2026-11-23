// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A header or footer band repeated on every page.
/// Text supports <c>{page}</c> (the current page number, or its label when the document defines
/// page-label ranges) and <c>{pages}</c> (total page count)
/// tokens, which are substituted at render time. With <see cref="ChapterToken"/> set, a save
/// also substitutes <c>{chapter}</c>, the title of the chapter the page is in.
/// </summary>
/// <remarks>
/// A null template throws from <see cref="Resolve"/> / save (#531). Overlong text is
/// truncated, and refused only in strict mode; see <see cref="Template"/>.
/// <para><b><c>{page}</c> prints the page's label once the document defines a page-label
/// range.</b> A range comes from <see cref="Document.PageLabel"/> or
/// <see cref="PageBreak.PageLabel"/>; with none, <c>{page}</c> prints the decimal page number, byte
/// for byte as it always has. With one, it prints what a viewer shows for the page, such as
/// <c>iv</c> or <c>A-9</c>, and a page before the first range reads its decimal number. The label
/// is copied as it is and is never scanned again, so a prefix that holds <c>{pages}</c> prints that
/// text. <c>{pages}</c> stays the number of pages in the document, a recto break's blank page
/// included, and that blank page has no band. <see cref="Resolve"/> is given the number and knows no
/// range, so it never substitutes a label.</para>
/// </remarks>
public sealed class RunningBand
{
    /// <summary>
    /// Whether a save substitutes <c>{chapter}</c> in <see cref="Template"/>: the title of the
    /// chapter the page is in. The default is <see langword="false"/>, which leaves the text
    /// <c>{chapter}</c> as given, as a band has always done.
    /// </summary>
    /// <remarks>
    /// <b>The save refuses two titles; the property itself refuses nothing.</b> A save of a band
    /// with the property set and no <c>{chapter}</c> in the template saves the same bytes as with
    /// it unset. The value is read when a save lays the document out.
    /// <para>A chapter is a level-0 <see cref="Heading"/> added to the document. The page shows the
    /// first one whose first line is on the page, and when none starts there, the last one drawn
    /// on an earlier page. A heading that splits over a page break counts on the page it starts
    /// on and is not a new chapter on the next. A page before the first level-0 heading shows no
    /// text where the token stands, and the rest of the template is drawn as it is written. A
    /// page that holds no band, such as the blank page a recto break inserts, shows no title, and
    /// a page where a chapter starts partway down shows that chapter, although the one before it
    /// holds the top of the page. The title is the heading's <see cref="Heading.BookmarkTitle"/>
    /// when it has one and its text otherwise. A tab, a line break, a form feed or another control
    /// white-space character in it is drawn as a space. Headings of other levels, and anything a
    /// custom <see cref="IRenderer"/> draws, start no chapter.</para>
    /// <para>The token is replaced by the title as it is, so a <c>{page}</c> or <c>{chapter}</c>
    /// in the title prints as that text. The title is measured and cut with the rest of the band, so one
    /// that does not fit is truncated and reported as <see cref="Template"/> says, and a character
    /// its font lacks is reported as for any band text. The chapter does not change the page
    /// count, because the band's height is fixed.</para>
    /// <para><b>An unpaired surrogate in the title is refused.</b> A save throws
    /// <see cref="ArgumentException"/> where the band is drawn, when the band's font is embedded
    /// and the title it prints holds one, whatever font the heading is set in; see
    /// <see cref="TextStyle.FontRef"/>.</para>
    /// <para><b>A resolved text longer than <see cref="int.MaxValue"/> characters is refused.</b> A
    /// template that repeats <c>{chapter}</c> so often that the titles it prints add up to more
    /// than that makes the save throw <see cref="InvalidOperationException"/> where the band is
    /// drawn, because no string could hold the text.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the title holds an unpaired surrogate and the band's font is
    /// embedded.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, when the resolved text passes
    /// <see cref="int.MaxValue"/> characters.
    /// </exception>
    public bool ChapterToken { get; init; }

    /// <summary>Text template — may contain {page} and/or {pages}, and {chapter} when
    /// <see cref="ChapterToken"/> is set.</summary>
    /// <remarks>
    /// A band is one line: a tab, a line feed, a carriage return, a form feed or any other
    /// white-space control character in the template is drawn as one space and measured as one,
    /// so a carriage return and line feed pair is two spaces, and no white-space control
    /// character reaches the font from it. The same holds for the text a
    /// <see cref="PageLabelRange"/> prints for <c>{page}</c>. None of these characters is reported
    /// as <see cref="LayoutDiagnosticCode.NotdefGlyph"/> or
    /// <see cref="LayoutDiagnosticCode.UnencodableCharacter"/>, so
    /// <see cref="Document.StrictDiagnostics"/> does not refuse a band for one. A control
    /// character that is not white space, such as U+0001, is written as it is;
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> says when it is reported.
    /// <para>Not checked when it is set: the constructor stores a null template, and the save
    /// throws <see cref="ArgumentNullException"/> where the band is first drawn. A template that
    /// does not fit the content box is truncated, and refused only in strict mode;
    /// <see cref="VellumPdf.Layout.Rendering.DocumentRenderer.BandTruncations"/> says when it
    /// counts as not fitting. The cut is reported through <c>Document.BandTruncations</c>,
    /// <c>DocumentRenderer.BandTruncations</c> and, with error severity, as
    /// <see cref="LayoutDiagnosticCode.BandTruncated"/> in <see cref="Document.Diagnostics"/>. With
    /// <see cref="Document.StrictDiagnostics"/> set, the save throws
    /// <see cref="InvalidOperationException"/> from the save, not from this property, and 3.0 will
    /// refuse it always. Pass an empty string for a band that draws no text, never
    /// <see langword="null"/>.</para>
    /// <para>The cut bounds the advance width, <b>not</b> the ink. Side bearings and italic
    /// overhang can still paint a little past it. Nothing in this package sets a clip path.</para>
    /// <para><b>Attention</b>: truncation does nothing for text whose glyphs measure zero, because
    /// each character is measured at the advance of the glyph the font gives it. Every glyph of
    /// <see cref="VellumPdf.Fonts.Standard14.Symbol"/> and
    /// <see cref="VellumPdf.Fonts.Standard14.ZapfDingbats"/> measures zero, and so do the control
    /// characters U+0000 to U+001F in every standard-14 font, except U+0009 to U+000D, which the
    /// band draws as spaces. Any length of such text fits and is
    /// drawn in full. Courier measures U+007F at 600 units where Helvetica measures it at zero. In
    /// an embedded font the other control characters take the font's own widths: Calibri measures
    /// U+0000 at zero, and Arial measures every C0 control at 750 units.</para>
    /// <para>You get one report per band per render, naming the page that lost the most rather
    /// than the first page cut. A <c>{page}</c> or <c>{pages}</c> token lengthens the resolved
    /// text as the number gains digits, so the worst page is the one that tells you how much
    /// shorter the template has to be.</para>
    /// <para>The cost of drawing a band on a page follows the characters that fit the content
    /// width, not the length of the template. The save produces those characters plus one of
    /// look-ahead, two when that character is a high surrogate and more text follows, and the
    /// word-boundary cut then drops some of them from what is drawn. A template of 100,000
    /// characters costs a page about what one of 200 does when both are cut at the same width.
    /// Text that measures zero is drawn in full, as the attention above says, and so costs its
    /// whole length on every page.</para>
    /// <para><b>A resolved text longer than <see cref="int.MaxValue"/> characters is refused.</b>
    /// With <see cref="ChapterToken"/> set, a template that repeats <c>{chapter}</c> so often that
    /// the titles it prints add up to more than that makes the save throw
    /// <see cref="InvalidOperationException"/>; see <see cref="ChapterToken"/>.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the band measures text that holds an unpaired surrogate in an
    /// embedded font, which can include part of the template it then does not draw; see
    /// <see cref="TextStyle.FontRef"/>.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from the save, not from this property, when the template does not fit the content box
    /// and <see cref="Document.StrictDiagnostics"/> is <see langword="true"/>.
    /// It is also raised when the resolved text passes <see cref="int.MaxValue"/> characters.
    /// </exception>
    public string Template { get; }

    /// <summary>The text style of the band.</summary>
    /// <remarks>
    /// A null style passed to the constructor is replaced by <see cref="TextStyle.Default"/>; any
    /// other is stored as given. Refusals on size, leading, font and text colour are on
    /// <see cref="TextStyle"/> and are raised from the save. A <see cref="TextStyle.LinkUri"/> in
    /// it is ignored.
    /// <para>A non-finite channel in <see cref="TextStyle.Color"/> is refused where it is written; see
    /// <see cref="Document"/>. A band that draws nothing, because its template is empty or no part of
    /// it fits the content width, writes no colour, so the channel is not refused there. <b>Do not pass</b> a non-finite channel: 3.0 refuses
    /// it.</para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the style's size or leading is refused; see
    /// <see cref="TextStyle.FontSize"/> and <see cref="TextStyle.Leading"/>.
    /// </exception>
    /// <inheritdoc cref="Heading.Style" path="/exception[@cref='T:System.IndexOutOfRangeException']"/>
    /// <exception cref="ArgumentException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from this property, when the band measures text that holds an unpaired surrogate in an
    /// embedded font, which can include part of the template it then does not draw; see
    /// <see cref="TextStyle.FontRef"/>. It is also raised when <see cref="Height"/> is null and the
    /// style's size, positive infinity included, or a finite leading is large enough to leave the
    /// page's content area no positive size; see <see cref="TextStyle.FontSize"/> and
    /// <see cref="TextStyle.Leading"/>. A channel of <see cref="TextStyle.Color"/> that is not finite
    /// raises it too, when the band is drawn and writes a colour (see the remarks), because the
    /// canvas refuses the value.
    /// </exception>
    public TextStyle Style { get; }

    /// <summary>Horizontal alignment of the band text.</summary>
    /// <remarks>
    /// <b>Only <see cref="HorizontalAlignment.Center"/> and <see cref="HorizontalAlignment.Right"/>
    /// are honoured.</b> Any other value is treated as <see cref="HorizontalAlignment.Left"/>, and
    /// nothing reports it: <see cref="HorizontalAlignment.Justify"/>, which a single-line band has
    /// nothing to justify against, and a value the enumeration does not name. The band's default is
    /// centred, so asking for justify loses the centring.
    /// <para>With <see cref="Document.MirrorMargins"/> set, a band treated as left on a left-hand
    /// page is drawn on the right, the outside edge, exactly as an explicit
    /// <see cref="HorizontalAlignment.Left"/> is; see that property.</para>
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
    /// <see cref="Document"/>. With a one-paragraph body, a
    /// <c>NaN</c> height is refused as too tall, positive infinity as a content area with no size, and
    /// negative infinity as too many page continuations on a header, but on a footer it writes
    /// <c>NaN</c> into the text matrix, which <see cref="VellumPdf.Canvas.PdfCanvas"/> refuses. Other
    /// bodies give other outcomes: a <c>NaN</c> height above a table is refused by the canvas where
    /// the table is drawn. A band with an empty template draws nothing,
    /// so a negative-infinity footer height there saves when the body fits. <b>Do not pass</b> a
    /// non-finite height: 3.0 refuses it.</para>
    /// <para>A finite height large enough to leave the content area no room is a separate case, and
    /// shares the type again: <see cref="InvalidOperationException"/> while the content area is
    /// still positive, and <see cref="ArgumentException"/> once it reaches exactly zero, on a header and a
    /// footer alike.</para>
    /// <para>A refusal raised before the save writes anything leaves the file a path held as it was.</para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// Raised from a save rather than from this property, when a finite height is large enough on
    /// its own that the content area is left with no positive size, or the height is positive
    /// infinity, or <c>NaN</c> or negative infinity where the layout writes the height into an
    /// operand, which the canvas refuses: a footer that draws text with a negative-infinity height,
    /// or a <c>NaN</c> height above a table, for example.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// Raised from a save, when the height is <c>NaN</c>, or negative infinity on a header, or a finite
    /// value large enough on its own that the content area is left positive but too small for the
    /// element. The messages differ for each cause, described above.
    /// </exception>
    public double? Height { get; init; }

    /// <summary>Creates a running band from a text template, with optional style and alignment (defaults to centered).</summary>
    /// <remarks>
    /// A null <paramref name="style"/> becomes <see cref="TextStyle.Default"/>, not the style set
    /// with <see cref="Document.SetDefaultFont"/>. A null
    /// <paramref name="template"/> is stored, and the save refuses it where the band is first
    /// drawn, with <see cref="ArgumentNullException"/>. It is not refused earlier, because a band
    /// that is replaced before the save is never drawn. <b>Do not pass</b> a null template: 3.0
    /// refuses it here. An empty template is not null and is accepted.
    /// <para>The font of a band with no style is Helvetica, which is not embedded. While
    /// <see cref="Document.Conformance"/> is not
    /// <see cref="VellumPdf.Document.PdfConformance.None"/> the save reports the element as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/> in <see cref="Document.Diagnostics"/> and
    /// still saves; only <see cref="Document.StrictDiagnostics"/> refuses it. <b>Do not leave the
    /// style out</b> of an element in a PDF/A or PDF/UA document; give it a style with an embedded
    /// font. A later major version refuses an element that falls back to Helvetica under either
    /// claim.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not from
    /// this constructor, when <paramref name="template"/> is <see langword="null"/> and the band is
    /// drawn. <c>ParamName</c> is <c>template</c>. The document cannot be saved again.
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
    /// <exception cref="InvalidOperationException">
    /// Raised from <see cref="Document.Save(System.IO.Stream)"/> and the other save overloads, not
    /// from here, when <see cref="Document.StrictDiagnostics"/> is set and text of the element is
    /// written as glyph 0, or the document has a conformance claim and the element writes Helvetica
    /// or another Standard 14 font; the save reports them as
    /// <see cref="LayoutDiagnosticCode.NotdefGlyph"/> and
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>.
    /// </exception>
    public RunningBand(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
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
    /// <c>{page}</c> becomes the number passed, as a decimal. A save prints the page's label in its
    /// place when the document defines a page-label range; this method is given no range, so it
    /// cannot.
    /// Only the exact lower-case text <c>{page}</c> or <c>{pages}</c> is substituted. The method
    /// knows no headings, so <c>{chapter}</c> is left as given whether or not
    /// <see cref="ChapterToken"/> is set; a save substitutes it. Any other
    /// brace text, <c>{Page}</c>, <c>{ page }</c> and a lone <c>{page</c> included, is left as
    /// given, and a substituted number is never scanned again. A null template is refused here with
    /// <see cref="ArgumentNullException"/>; the save refuses it the same way where the band is
    /// first drawn.
    /// <para><b>The string returned is not always what a save draws.</b> A tab, a line break, a
    /// form feed or another white-space control character in the template, and in a page label, is
    /// returned as written, where a save draws each as one space; see <see cref="Template"/>.
    /// A caller that compares this string with the text of a drawn band sees that
    /// difference.</para>
    /// <para>This builds the whole text, so its cost is the template's length. A save does not call
    /// it: the save resolves a template as it draws, and its cost per page follows the characters
    /// that fit the content width, as <see cref="Template"/> describes.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <see cref="Template"/> is <see langword="null"/>. <c>ParamName</c> is <c>template</c>.
    /// </exception>
    public string Resolve(int pageNumber, int totalPages) =>
        (Template ?? throw new ArgumentNullException("template", "The band's template is null."))
            .Replace("{page}", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{pages}", totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);

    private BandTemplate? _compiled;

    /// <summary>
    /// The template scanned into literal runs and tokens, built on first use and kept: the template
    /// is read-only, so the scan never goes stale. A null template is refused here, with the
    /// exception <see cref="Resolve"/> throws, at the point the band is first drawn.
    /// </summary>
    internal BandTemplate Compiled => _compiled ??= BandTemplate.Compile(Template, ChapterToken);
}
