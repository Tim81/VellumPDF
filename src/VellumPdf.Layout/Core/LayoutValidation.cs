// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;

namespace VellumPdf.Layout.Core;

/// <summary>
/// Names the input a renderer cannot work with, at the point the renderer would otherwise give up.
///
/// A renderer that cannot place an element returns <c>Nothing</c>, and <c>DocumentRenderer</c> reads
/// that as "this element can never fit" and raises the too-tall message. For a genuinely oversized
/// element that is the right answer. For a font size of <c>NaN</c> or a <c>ColSpan</c> of zero it is
/// not: the element is not too tall, its input is unusable, and the suggested remedy of reducing its
/// content or enlarging the page cannot help (#481).
///
/// Each check is narrower than the property it guards, and every boundary below was measured at the
/// value stated rather than inferred from a neighbouring one. That distinction is not academic: NaN,
/// positive infinity and negative infinity take three different branches here, and an earlier draft
/// of this file measured one of them and asserted all three.
///
/// What these inputs did before is not uniform, so it is stated per input rather than per group. A
/// NaN or positive-infinity font size on a paragraph run already threw, so only the wording changes.
/// A <c>ColSpan</c> below one already threw when it fell in the row that sets the column count, and
/// otherwise rendered while silently dropping the cell the caller wrote. Everything else here saved
/// a document whose content stream was invalid.
///
/// What is left alone, each rendering valid content today, because refusing it would turn a working
/// document into an exception: a font size of zero or less, a <c>RowSpan</c> below one, a negative
/// image extent, and a positive-infinity image width, which #472's over-wide clamp resolves to the
/// content box before anything here sees it. Those are contract tightenings for the next major.
/// </summary>
internal static class LayoutValidation
{
    /// <summary>
    /// The magnitude below which <c>PdfCanvas</c>'s own number format writes the token <c>0</c>, or
    /// <c>-0</c> for a small negative. Measured at the boundary: 5e-6 writes <c>0.00001</c>, 4.9e-6
    /// writes <c>0</c>.
    ///
    /// The extent check tests this rather than equality with zero, because what makes the matrix
    /// singular is the token written, not the value held. An earlier draft refused only an exact
    /// zero, and a width of 4e-6 went on emitting <c>0 0 0 0 10 390 cm</c> — the very matrix the
    /// check exists to prevent.
    /// </summary>
    private const double SmallestWrittenExtent = 5e-6;

    /// <summary>
    /// Rejects a text style whose font size is not a finite number.
    ///
    /// The three non-finite values reach the too-tall message by three different routes, and only
    /// the first is the mechanism usually described. A NaN size makes every height comparison false,
    /// so the renderer measures nothing and reports placing nothing. A positive-infinity size
    /// reports a height larger than any page, reaching the same message by the ordinary route. A
    /// negative-infinity size is clamped to zero by the line-height maximum, falls back to the
    /// default leading, and used to save successfully while writing <c>/F1 -Infinity Tf</c>, which
    /// is not a PDF number.
    /// </summary>
    internal static void ValidateStyle(TextStyle style, string owner)
    {
        // Non-finite only, deliberately. A font size of zero or less renders today and reaches the
        // stream verbatim -- "/F1 0 Tf" at zero, "/F1 -12 Tf" at -12 -- a valid operator either
        // way, so refusing it would turn a working document into an exception. Deferred.
        //
        // Leading is not checked here. EffectiveLeading now treats any non-finite leading as
        // "derive it from the font size", the same as zero, so none reaches a measurement. It used
        // to: a positive-infinity leading was taken by the positive branch and raised the too-tall
        // message, an unfixed instance of the very defect this file addresses.
        if (!double.IsFinite(style.FontSize))
        {
            throw new InvalidOperationException(
                $"{owner} has a font size of {Format(style.FontSize)}, which cannot be laid out. " +
                "A font size must be a finite number.");
        }

        ValidateColor(style.Color, owner, "text colour");
    }

    /// <summary>
    /// Rejects a colour with a channel that is not a finite number. A channel is written straight
    /// into an <c>rg</c> or <c>RG</c> operand, so <c>NaN</c> reached the stream as <c>NaN 0 0 rg</c>
    /// and infinity as <c>Infinity 0 0 rg</c>, neither of which is a PDF number (#509).
    ///
    /// Finite is all this checks. A channel outside 0 to 1 is not a PDF number problem: the token
    /// still parses and the file renders, so refusing it would turn a working document into an
    /// exception. The range is documented on the colour types instead, and the refusal is deferred
    /// to the next major (#484).
    ///
    /// <c>PdfCanvas</c> refuses the same values when it writes the operand, so a colour that
    /// reaches the stream by some route this check does not cover still fails loudly. This check is
    /// the one that can name the element the caller wrote, which the canvas cannot.
    /// </summary>
    /// <param name="color">The colour to check.</param>
    /// <param name="owner">What the colour belongs to, such as <c>A line separator</c>.</param>
    /// <param name="what">Which colour it is, such as <c>colour</c> or <c>border colour</c>.</param>
    internal static void ValidateColor(ColorRgb color, string owner, string what)
    {
        var channel = !double.IsFinite(color.R) ? ("red", color.R)
            : !double.IsFinite(color.G) ? ("green", color.G)
            : !double.IsFinite(color.B) ? ("blue", color.B)
            : default;
        if (channel.Item1 is null) return;

        throw new InvalidOperationException(
            $"{owner} has a {what} whose {channel.Item1} channel is {Format(channel.Item2)}, " +
            "which cannot be written to a content stream. Every colour channel must be a finite number.");
    }

    /// <summary>Whether every edge of the insets is a finite number.</summary>
    internal static bool IsFinite(EdgeInsets insets) =>
        double.IsFinite(insets.Left) && double.IsFinite(insets.Top) &&
        double.IsFinite(insets.Right) && double.IsFinite(insets.Bottom);

    /// <summary>Whether every channel of the colour is a finite number.</summary>
    internal static bool IsFinite(ColorRgb color) =>
        double.IsFinite(color.R) && double.IsFinite(color.G) && double.IsFinite(color.B);

    /// <summary>
    /// Rejects a cell that covers less than one column, or whose padding is not finite.
    ///
    /// The grid resolver takes the column count from the widest row's span sum, so a
    /// <c>ColSpan</c> of zero can leave it at zero: no width is allocated, nothing can be drawn,
    /// and the table reports that it placed nothing, which the caller saw as the too-tall message.
    /// In a row that does not set the column count, the same cell was instead dropped silently,
    /// with the rest of the table rendering normally.
    /// </summary>
    /// <param name="cell">The cell to check.</param>
    /// <param name="rowIndex">The row's index in the table.</param>
    /// <param name="cellIndex">
    /// The cell's index within its row <em>as the caller wrote it</em>, not the column it landed in.
    /// The two diverge as soon as an earlier cell spans more than one column, and naming the column
    /// would point the caller at a cell they never wrote.
    /// </param>
    internal static void ValidateCell(Cell cell, int rowIndex, int cellIndex)
    {
        // ColSpan only. A RowSpan below one renders today, measured, because nothing derives the
        // column count from it; rejecting it would turn a rendering document into an exception and
        // belongs with the other contract tightenings in the next major.
        if (cell.ColSpan < 1)
        {
            throw new InvalidOperationException(
                $"Table row {rowIndex}, cell {cellIndex} has a ColSpan of {cell.ColSpan}. " +
                "A span must cover at least one column.");
        }

        ValidateInsets(cell.Padding, $"Table row {rowIndex}, cell {cellIndex}");
        if (cell.Background is { } background)
            ValidateColor(background, $"Table row {rowIndex}, cell {cellIndex}", "background colour");
    }

    /// <summary>
    /// Rejects an image whose drawn extent is written as zero, or is not finite.
    ///
    /// A zero extent gives a transformation matrix with no inverse: a zero width writes
    /// <c>0 0 0 0 10 390 cm</c> and a zero height <c>280 0 0 0 10 390 cm</c>. ISO 32000-2, 8.3.4,
    /// NOTE 3 records that a matrix whose a, b, c and d entries are all zero maps every user
    /// coordinate to the same device coordinate so there is no unique inverse, that such matrices
    /// "generally arise from unintended operations, such as scaling by 0", and that painting
    /// through one "can result in unpredictable behaviour". That note is informative rather than a
    /// requirement, and its scope is painting graphics objects generally rather than images alone.
    ///
    /// A non-finite extent writes <c>NaN 0 0 NaN 10 NaN cm</c>, where the token is not a PDF number
    /// at all. Both of these saved without complaint and produced a document a reader cannot
    /// render (#478).
    /// </summary>
    internal static void ValidateImageExtent(double width, double height)
    {
        Check(width, "width");
        Check(height, "height");

        static void Check(double value, string which)
        {
            if (double.IsFinite(value) && Math.Abs(value) >= SmallestWrittenExtent) return;

            // No "leave it unset" advice: the drawn extent can reach zero from margins that consume
            // the content box while the property was never set at all, and telling such a caller to
            // unset it would point them at the wrong input.
            throw new InvalidOperationException(
                $"An image would be drawn with a {which} of {Format(value)}, which cannot be " +
                $"painted. A drawn {which} must be finite and at least " +
                $"{Format(SmallestWrittenExtent)} points in magnitude; anything smaller is written " +
                "as zero, giving a transformation matrix with no inverse.");
        }
    }

    /// <summary>
    /// Rejects a non-finite line width, which reaches the stream as <c>NaN w</c>, and a negative
    /// one, which reaches it as <c>-3 w</c> (#557).
    ///
    /// ISO 32000-2, 8.4.3.2 (Line width) says the operand "shall be a non-negative number", so a
    /// negative width is a non-conforming operand even though it parses. That is why this refuses
    /// it where the neighbouring out-of-range inputs, such as a colour channel above 1, are only
    /// documented: those render a valid file, and this writes an operand the standard forbids.
    ///
    /// Zero is left alone, unlike an image extent: <c>0 w</c> is a defined operator asking for the
    /// thinnest line the device renders, not a degenerate matrix. The same clause says such lines
    /// "should not be used" because the result is device-dependent, which is a caller warning
    /// rather than a refusal and is carried by the member documentation.
    /// </summary>
    internal static void ValidateLineWidth(double width, string owner)
    {
        if (!double.IsFinite(width))
        {
            throw new InvalidOperationException(
                $"{owner} has a line width of {Format(width)}, which cannot be drawn. " +
                "A line width must be a finite number.");
        }

        if (width < 0)
        {
            throw new InvalidOperationException(
                $"{owner} has a line width of {Format(width)}, which cannot be drawn. " +
                "A line width must not be negative (ISO 32000-2, 8.4.3.2).");
        }
    }

    /// <summary>
    /// Rejects insets that are not finite. Padding and margins are written straight into a
    /// rectangle and a text matrix, so one non-finite inset emits <c>10 NaN 280 NaN re</c> and
    /// <c>1 0 0 1 NaN NaN Tm</c>.
    /// </summary>
    internal static void ValidateInsets(EdgeInsets insets, string owner)
    {
        if (!double.IsFinite(insets.Left) || !double.IsFinite(insets.Top) ||
            !double.IsFinite(insets.Right) || !double.IsFinite(insets.Bottom))
        {
            throw new InvalidOperationException(
                $"{owner} has a non-finite inset. Every inset must be a finite number.");
        }
    }

    /// <summary>
    /// Rejects a list indent of <c>NaN</c> or negative infinity, the two values that reach a text
    /// matrix as a token that is not a PDF number (#532).
    ///
    /// A nested child's marker takes the indent as its left inset directly, and a flat item whose
    /// longest word will not fit beside its marker takes it as its gutter, so either path wrote
    /// <c>1 0 0 1 -Infinity 263.6 Tm</c>. <c>NaN</c> reached the same operand on every path, because
    /// <c>Math.Max</c> with <c>NaN</c> is <c>NaN</c>. A flat list whose words fit stayed safe for
    /// negative infinity only because the gutter widened to the marker's own width, which is why
    /// the flat case looked correct when this was first reported.
    ///
    /// Positive infinity is deliberately not checked here. It writes no invalid token, a nested list
    /// already throws at the content-width boundary, and on a flat list it discards the item text,
    /// which is its own defect (#476) and renders a valid file today. A finite negative indent is
    /// likewise left alone.
    /// </summary>
    internal static void ValidateListIndent(double indent, string owner)
    {
        if (!double.IsNaN(indent) && !double.IsNegativeInfinity(indent)) return;

        throw new InvalidOperationException(
            $"{owner} has an indent of {Format(indent)}, which cannot be laid out. " +
            "An indent must be a finite number.");
    }

    /// <summary>
    /// Rejects a document margin that is not a finite number, naming the edge.
    ///
    /// The margin check that existed compares the horizontal and vertical sums against the page, and
    /// a comparison against <c>NaN</c> is false, so <c>NaN</c> passed it. Negative infinity is below
    /// every page size and passed it too. Both then surfaced later as a message about something
    /// else: <c>NaN</c> as an element that is too tall, negative infinity as 50,000 page
    /// continuations, the second after the renderer had done that work. Positive infinity was
    /// already caught by the sum, with a message that named a total rather than the edge (#502).
    ///
    /// Raised as <see cref="ArgumentException"/> with the parameter name <c>margins</c>, which is
    /// what the sum check beside it throws, so a caller's existing handler for a bad margin still
    /// catches every bad margin. A negative finite margin is not refused: it saves today, a reader
    /// clips what lands outside the page, and refusing it would turn a rendering document into an
    /// exception. It is documented on <c>Document.Margins</c> and left to the next major.
    /// </summary>
    internal static void ValidateMargins(EdgeInsets margins)
    {
        Check(margins.Left, "left");
        Check(margins.Top, "top");
        Check(margins.Right, "right");
        Check(margins.Bottom, "bottom");

        static void Check(double value, string edge)
        {
            if (double.IsFinite(value)) return;
            throw new ArgumentException(
                $"The {edge} margin is {Format(value)}, which cannot be laid out. " +
                "A margin must be a finite number.",
                "margins");
        }
    }

    /// <summary>
    /// Rejects a running band whose reserved height is not a finite number, for a header and a
    /// footer alike, so the two cannot disagree (#520).
    ///
    /// They disagreed. The header height comes off the top of the content box, so a non-finite
    /// value poisoned the Y cursor and a later check refused it. The footer height comes off the
    /// content box's height only, so negative infinity made that height positive infinity, the body
    /// placed normally, and the band was then drawn at <c>+inf</c> plus <c>-inf</c>: the file saved
    /// with <c>NaN</c> in a text matrix. Positive infinity and <c>NaN</c> were refused on both, by
    /// two different exception types across three messages.
    ///
    /// Only an explicit <c>Height</c> is checked. A band with no height derives one from its style,
    /// and a style that makes that derived height unusable is refused by its own checks.
    /// </summary>
    internal static void ValidateBandHeight(RunningBand band, string owner)
    {
        if (band.Height is not { } height || double.IsFinite(height)) return;

        throw new InvalidOperationException(
            $"{owner} has a height of {Format(height)}, which cannot be laid out. " +
            "A band height must be a finite number.");
    }

    /// <summary>
    /// Rejects explicit column widths whose sum is not finite (#546).
    ///
    /// Individual non-finite entries are already read as auto, so every entry reaching the sum is
    /// finite, but finite entries can still overflow when added. The resolver scales explicit
    /// widths by one ratio when they overrun the room available, so an infinite sum made that ratio
    /// zero and every column was written at width 0.
    ///
    /// Only the overflow is refused. A single dominant but finite entry, such as 1e200 beside 60
    /// and 70, scales the others to nearly nothing and still renders, so refusing it would turn a
    /// rendering document into an exception; it is documented on <c>ColWidths</c> and left for the
    /// next major (#484).
    /// </summary>
    internal static void ValidateColumnWidthSum(double sum)
    {
        if (double.IsFinite(sum)) return;

        throw new InvalidOperationException(
            "A table's explicit column widths add up to positive infinity, so they cannot be " +
            "scaled to fit the page and every column would be drawn at width 0. Use smaller " +
            "column widths.");
    }

    /// <summary>
    /// Rejects a table that resolves to no columns. This is the third refusal #481 names, and the
    /// one route to it that no span check covers: a table with no rows, or whose rows hold no
    /// cells, reaches a column count of zero without any bad <c>ColSpan</c>.
    /// </summary>
    internal static void ValidateColumnCount(int columnCount)
    {
        if (columnCount < 1)
        {
            throw new InvalidOperationException(
                "A table resolved to no columns, so it has nothing to draw. " +
                "Add a row holding at least one cell.");
        }
    }

    /// <summary>
    /// Renders a rejected number for a message. <c>NaN</c> and the infinities are the values this
    /// exists for and they have no useful "0.##" form, so they are named rather than formatted.
    /// </summary>
    private static string Format(double value) =>
        double.IsNaN(value) ? "NaN"
        : double.IsPositiveInfinity(value) ? "positive infinity"
        : double.IsNegativeInfinity(value) ? "negative infinity"
        : value.ToString("0.#######", CultureInfo.InvariantCulture);
}
