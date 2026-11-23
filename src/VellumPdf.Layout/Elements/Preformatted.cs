// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A block of text whose spaces, tabs and line ends are its layout: every space is kept, a tab
/// moves to the next tab stop, and a line ends where the text ends it.
/// </summary>
/// <remarks>
/// <b>Control characters other than tab, carriage return and line feed, U+2028, U+2029 and
/// unpaired surrogates are refused.</b> The constructor throws <see cref="ArgumentException"/>
/// naming the index and the character. A control character has no glyph in the fonts this library
/// ships, so it would be drawn as the missing-glyph box or as a raw control byte, and a paragraph
/// turns some of them into spaces, which this element must not do. Vertical tab, form feed, U+0085,
/// U+2028 and U+2029 are mandatory line breaks in UAX #14, so drawing them as characters would
/// misstate them, and none of them ends a line here. A valid surrogate pair is accepted.
/// <para><b>Lines.</b> The text splits at a carriage return followed by a line feed, at a lone
/// carriage return and at a lone line feed, and nowhere else. A line end at the very end of the
/// text ends the last line and starts none, so <c>"a\n"</c> is one line and <c>"a\n\n"</c> is two,
/// the second empty. An empty text is one empty line. Each line is drawn one
/// <see cref="TextStyle.EffectiveLeading"/> below the one before.</para>
/// <para><b>Columns and tabs.</b> A line is read as extended grapheme clusters, and each cluster is
/// one column. A tab is replaced by as many spaces as reach the next multiple of
/// <see cref="TabWidth"/> columns, a full <see cref="TabWidth"/> when it already sits on one. The
/// replacement happens before the line is measured or drawn, so no tab reaches the font. Every
/// other space is kept as written: leading, repeated and trailing spaces, and a line of spaces
/// alone. Nothing is collapsed, justified, hyphenated or broken at a word.</para>
/// <para><b>Wrap.</b> A line wider than the content width is split into segments. Each segment but
/// the last holds the longest run of whole clusters that leaves room for the continuation marker,
/// and at least one cluster, so the wrap always advances. A small hook is drawn at the right end of
/// each such segment, as an artifact, so it enters neither the structure tree nor extracted text.
/// The hook scales with the size: its stroke is 0.06 times the size wide and it reaches 0.2, 0.35
/// and 0.6 times the size above the baseline, among other heights. No
/// character is dropped: the segments of a line, joined, are its expanded text. The one text
/// drawn outside the box is a segment of a single cluster wider than the box with the marker. The
/// save lists the wrapped lines as <see cref="LayoutDiagnosticCode.PreformattedLineWrapped"/>. The
/// element splits between drawn lines and never inside one; a wrapped line can continue on the next
/// page.</para>
/// <para><b>Refused at the save, before any layout.</b> <see cref="InvalidOperationException"/> is
/// thrown, and the document stays saveable once you have changed what the message names, when the
/// content box left by <see cref="Document.Margins"/>, the page and <see cref="Margins"/> is not
/// wider than zero or is shorter than one line. Under any <see cref="Document.Conformance"/> other
/// than <see cref="VellumPdf.Document.PdfConformance.None"/> it is also thrown when the style's
/// font is not embedded, and when a character of the text, a tab's spaces included, has no glyph
/// in the embedded font. Under PDF/A-2a, PDF/A-2u and PDF/UA-1 a U+FEFF or U+FFFE is refused as
/// well, even when the font has a glyph for it, because a ToUnicode value may not be either. The
/// default style is Courier, which is not embedded, so a document that claims a standard must pass
/// an embedded font.</para>
/// <para><b>With no claim</b>, a character the embedded font has no glyph for is drawn as glyph 0,
/// and the save lists it as <see cref="LayoutDiagnosticCode.NotdefGlyph"/>, as for a paragraph;
/// <see cref="Document.StrictDiagnostics"/> refuses that save.</para>
/// <para><b>What is ignored.</b> <see cref="Document.SetDefaultFont"/> does not apply, as for lists
/// and tables. Alignment, justification and hyphenation do not exist on this element. A paragraph
/// tab stop does not apply: a tab is a number of spaces. No language is written for the
/// block.</para>
/// <para>In a tagged document each drawn piece, the part of the block on one page, is one <c>P</c>
/// structure element. Nothing in the character stream separates two lines, so a reader that follows
/// the structure order alone sees the lines run together.</para>
/// <para><b>Do not rely on</b> columns lining up in a proportional font, after a zero-width
/// character that forms a cluster of its own (a combining mark at the start of a line or after a
/// tab), after a wide or non-BMP character, or in a Standard-14 font for a character
/// WinAnsiEncoding lacks, which is drawn as one or two question marks; see
/// <see cref="TextStyle.FontRef"/>. The block draws and measures such text without refusing it,
/// and its tabs still expand to spaces. In a monospace font, a cluster of one glyph with a non-zero
/// advance is one column wide.</para>
/// </remarks>
public sealed class Preformatted
{
    // Courier is monospace in the Standard-14 set, so the default lines up without a font file.
    // One shared instance, like TextStyle.Default: every property of a style is init-only.
    private static readonly TextStyle DefaultStyle = new() { Font = Standard14.Courier, FontSize = 10 };

    /// <summary>
    /// The smallest font size accepted, in points. <c>PdfCanvas</c> writes a number to five decimal
    /// places, so anything below this is written as <c>0</c>.
    /// </summary>
    private const double SmallestWrittenSize = 5e-6;

    /// <summary>
    /// The largest font size accepted, in points. The block writes the size as a <c>Tf</c> operand
    /// and as part of each baseline, and writes the continuation marker's x as the width of the
    /// segment plus 0.48 times the size. Each number must stay within the ±3.403 × 10^38 of
    /// ISO 32000-1, Annex C, Table C.1, which ISO 19005-2, 6.1.13 applies. 1e30 is a
    /// margin, not the boundary: on a page within the 14,400-unit limit of ISO 32000-1 Annex C it
    /// leaves a factor of about 3.4 × 10^8 below the limit, so a single cluster would have to be
    /// that many ems wide to pass it.
    /// </summary>
    private const double LargestFontSize = 1e30;

    private int _tabWidth = 8;
    private EdgeInsets _margins = EdgeInsets.Zero;
    private PageBreakMode _breakBefore;

    /// <summary>The text exactly as passed, tabs and line ends included.</summary>
    /// <remarks>
    /// Never null; the constructor refuses a null. The characters it holds are those the
    /// constructor accepted; see the type for the refused set.
    /// </remarks>
    public string Text { get; }

    /// <summary>The text style: the one passed, or Courier at 10 pt when none was.</summary>
    /// <remarks>
    /// <b>A style the constructor refuses is not stored.</b> The default is Courier, 10 pt,
    /// automatic leading of 12 pt and black text. It is a separate instance from
    /// <see cref="TextStyle.Default"/>, and <see cref="Document.SetDefaultFont"/> does not change
    /// it. Every refusal on the style is raised from the constructor; see
    /// <see cref="Preformatted(string, TextStyle)"/>.
    /// </remarks>
    public TextStyle Style { get; }

    /// <summary>The distance between tab stops, in columns, from 1 to 32. The default is
    /// 8.</summary>
    /// <remarks>
    /// <b>A value below 1 or above 32 is refused.</b> This <c>init</c> accessor throws
    /// <see cref="ArgumentOutOfRangeException"/>, so the error names the line that wrote the value.
    /// A tab stop is a multiple of the value counted from the start of the line, and a tab moves to
    /// the next one; a tab at a stop moves a full width.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is below 1 or above 32.
    /// </exception>
    public int TabWidth
    {
        get => _tabWidth;
        init => _tabWidth = value is >= 1 and <= 32
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(value), value, "The tab width must be from 1 to 32 columns.");
    }

    /// <summary>The space around the block. The default is none.</summary>
    /// <remarks>
    /// <b>An edge that is not finite or is negative is refused.</b> This <c>init</c> accessor
    /// throws
    /// <see cref="ArgumentOutOfRangeException"/>. Margins that leave the content box no wider than
    /// zero, or shorter than one line, are refused by the save before any layout; see the type.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when an edge is <c>NaN</c>, infinite or negative.
    /// </exception>
    public EdgeInsets Margins
    {
        get => _margins;
        init
        {
            Require(value.Top, "Top");
            Require(value.Right, "Right");
            Require(value.Bottom, "Bottom");
            Require(value.Left, "Left");
            _margins = value;
        }
    }

    /// <summary>Whether the block starts a new page, or the next right-hand page.</summary>
    /// <remarks>
    /// <b>An undefined value is refused.</b> A number <see cref="PageBreakMode"/> does not name
    /// makes this <c>init</c> accessor throw <see cref="ArgumentOutOfRangeException"/>. The
    /// default,
    /// <see cref="PageBreakMode.None"/>, starts the block where the previous element ended. It is
    /// honoured when the block is added with <see cref="Document.Add(Preformatted)"/>; the pieces a
    /// split block continues in never break. See <see cref="PageBreakMode"/>.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Raised from this <c>init</c> accessor when the value is not one of
    /// <see cref="PageBreakMode.None"/>, <see cref="PageBreakMode.Page"/> and
    /// <see cref="PageBreakMode.Recto"/>.
    /// </exception>
    public PageBreakMode BreakBefore
    {
        get => _breakBefore;
        init => _breakBefore = PageBreakModes.Validate(value);
    }

    /// <summary>
    /// A name that a link or an outside reference can jump to: the save adds a named destination at
    /// the first line of this block. <see langword="null"/>, the default, adds none.
    /// </summary>
    /// <remarks>
    /// <b>A name outside the rule is refused by this <c>init</c> accessor.</b> It throws
    /// <see cref="ArgumentException"/> for an empty name, one longer than 127 characters, or one
    /// with a character outside <c>A-Z a-z 0-9 . _ - :</c>, as <see cref="Paragraph.Anchor"/> does,
    /// and for the same reason.
    /// <para><b>A repeated anchor is refused by the save, before any layout.</b> The save throws
    /// <see cref="InvalidOperationException"/> naming the anchor and the kind and zero-based
    /// position of both elements that hold it, whatever their kinds: a paragraph and a block
    /// holding one name are refused as two paragraphs are. Nothing has been laid out then, so the
    /// document can be saved once the elements are replaced. The same instance added twice holds
    /// its anchor twice.</para>
    /// <para>The destination is written whether or not a link refers to it, so a reader can open
    /// the file at it by name (<c>file.pdf#nameddest=name</c>). Its position is the top edge of the
    /// block's first line, below <see cref="Margins"/>, at the left edge of the page, with the
    /// viewer's zoom unchanged. When the block splits across pages, that is the page its first
    /// line is on, and no later piece reports the anchor. The destinations are written in the
    /// order of their names, so the order the elements were added in does not change a key.</para>
    /// <para><b>Do not pass</b> two names that differ only in case and expect a reader to treat
    /// them as one: names are compared byte by byte, so <c>Code</c> and <c>code</c> are two
    /// destinations.</para>
    /// </remarks>
    /// <inheritdoc cref="Paragraph.Anchor" path="/exception"/>
    public string? Anchor
    {
        get => _anchor;
        init => _anchor = AnchorNames.Validate(value);
    }

    private string? _anchor;

    /// <summary>Creates a block from a string, in the given style or in Courier at 10 pt.</summary>
    /// <remarks>
    /// <b>This constructor refuses a null text, a refused character and a refused style.</b> It
    /// throws <see cref="ArgumentNullException"/> for a null text. It throws
    /// <see cref="ArgumentException"/> for a control character other than tab, carriage return and
    /// line feed, for U+2028 and U+2029, and for an unpaired surrogate; <c>ParamName</c> is
    /// <c>text</c> and the message names the UTF-16 index and the code point, an unpaired surrogate
    /// by its code unit. It throws <see cref="ArgumentException"/> with <c>ParamName</c>
    /// <c>style</c> for each of these: a
    /// <see cref="TextStyle.FontSize"/> that is not finite, is below 0.000005, which
    /// <c>PdfCanvas</c> would write as zero, or is above 1e30;
    /// a <see cref="TextStyle.Leading"/> that is not finite
    /// (zero and negative still mean automatic leading); a <see cref="TextStyle.Color"/> channel
    /// that is not a number from 0 to 1, the range ISO 32000-2:2020, 8.6.8 gives each <c>rg</c> and
    /// <c>RG</c> operand; a <see cref="TextStyle.FontRef"/> that is a <see cref="Standard14"/>
    /// value the enumeration does not name, or Symbol or ZapfDingbats, which have no
    /// WinAnsiEncoding glyphs and measure as zero wide; and a <see cref="TextStyle.LinkUri"/>,
    /// because a link on a block that is not drawn as text runs would be dropped without a word.
    /// <para><b>The 1e30 ceiling on the font size is there for ISO 32000-1, Annex C, Table C.1.</b>
    /// It gives a real number a magnitude of at most 3.403 × 10^38, and PDF/A-2, 6.1.13 applies the
    /// limit. The block writes the size in <c>Tf</c> and in each baseline, and writes the marker's
    /// x as the width of the segment plus 0.48 times the size, so a size of 1e39 would be written
    /// and would fail veraPDF's rule 6.1.13-2 under PDF/A-2u. The ceiling holds under every
    /// <see cref="Document.Conformance"/>, including
    /// <see cref="VellumPdf.Document.PdfConformance.None"/>. The leading needs no ceiling: the save
    /// refuses a leading taller than the content box, which the page bounds.</para>
    /// <para>Everything else a style can carry is accepted. A proportional font draws, and its tabs
    /// still expand to spaces. A <see cref="TextStyle.FontRef"/> built from a null handle draws
    /// Helvetica. A handle from another document is refused by the save with
    /// <see cref="ArgumentException"/>; see <see cref="FontReference(EmbeddedFontHandle)"/>.
    /// A font size so large that one line does not fit the page is refused by the save, before any
    /// layout, with <see cref="InvalidOperationException"/>; see the type.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="text"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="text"/> holds a refused character (<c>ParamName</c> <c>text</c>), or
    /// <paramref name="style"/> is refused as described above (<c>ParamName</c> <c>style</c>).
    /// </exception>
    public Preformatted(string text, TextStyle? style = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        RejectRefusedCharacters(text);
        var chosen = style ?? DefaultStyle;
        RejectRefusedStyle(chosen);
        Text = text;
        Style = chosen;
    }

    private static void Require(double edge, string name)
    {
        if (!double.IsFinite(edge) || edge < 0)
            throw new ArgumentOutOfRangeException(
                "value",
                edge,
                FormattableString.Invariant($"The {name} margin must be a finite number that is not negative."));
    }

    private static void RejectRefusedCharacters(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsHighSurrogate(c))
            {
                if (i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    i++;
                    continue;
                }

                throw Refused(i, c, "an unpaired high surrogate");
            }

            if (char.IsLowSurrogate(c))
                throw Refused(i, c, "an unpaired low surrogate");

            if (c is '\t' or '\n' or '\r')
                continue;

            if (char.IsControl(c))
                throw Refused(i, c, "a control character other than tab, carriage return and line feed");

            if (c is '\u2028' or '\u2029')
                throw Refused(i, c, "a mandatory line break that does not end a line here");
        }
    }

    private static ArgumentException Refused(int index, char c, string what) =>
        new(
            FormattableString.Invariant($"The text holds U+{(int)c:X4} at UTF-16 index {index}, which is {what}.") +
            " A preformatted block does not accept it.",
            "text");

    private static void RejectRefusedStyle(TextStyle style)
    {
        var size = style.FontSize;
        if (!double.IsFinite(size) || size < SmallestWrittenSize)
            throw Refused(
                $"The style's FontSize is {Format(size)}. It must be a finite number of at least " +
                "0.000005 points; a smaller size is written as zero.");

        if (size > LargestFontSize)
            throw Refused(
                $"The style's FontSize is {Format(size)}. It must be at most 1E+30 points, " +
                "which keeps the numbers the block writes far inside the 3.403 × 10^38 limit of " +
                "ISO 32000-1, Annex C, Table C.1.");

        if (!double.IsFinite(style.Leading))
            throw Refused(
                $"The style's Leading is {Format(style.Leading)}. It must be a finite number; " +
                "zero or less means automatic leading.");

        var color = style.Color;
        var (name, value) =
            !IsUnit(color.R) ? ("red", color.R)
            : !IsUnit(color.G) ? ("green", color.G)
            : !IsUnit(color.B) ? ("blue", color.B)
            : default;
        if (name is not null)
            throw Refused(
                $"The style's Color has a {name} channel of {Format(value)}. " +
                "Each channel must be a number from 0 to 1 (ISO 32000-2:2020, 8.6.8).");

        var font = style.FontRef;
        if (!font.IsEmbedded)
        {
            var standard = font.Standard14;
            if (!Enum.IsDefined(standard))
                throw Refused(
                    FormattableString.Invariant(
                        $"The style's font is the Standard14 value {(int)standard}, which the enumeration does not name."));
            if (standard is Standard14.Symbol or Standard14.ZapfDingbats)
                throw Refused(
                    $"The style's font is {standard}, which has no WinAnsiEncoding glyphs and measures as zero wide, so no line could be wrapped.");
        }

        if (style.LinkUri is not null)
            throw Refused("The style has a LinkUri. A preformatted block does not draw links, so it would be dropped.");

        static bool IsUnit(double channel) => channel is >= 0 and <= 1;
    }

    private static ArgumentException Refused(string message) => new(message, "style");

    private static string Format(double value) =>
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "positive infinity"
        : double.IsNegativeInfinity(value) ? "negative infinity"
        : value.ToString("R", CultureInfo.InvariantCulture);
}
