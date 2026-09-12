// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
    }

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
    /// Rejects a non-finite line width, which reaches the stream as <c>NaN w</c>.
    ///
    /// Zero is left alone, unlike an image extent: <c>0 w</c> is a defined operator asking for the
    /// thinnest line the device renders, not a degenerate matrix.
    /// </summary>
    internal static void ValidateLineWidth(double width, string owner)
    {
        if (!double.IsFinite(width))
        {
            throw new InvalidOperationException(
                $"{owner} has a line width of {Format(width)}, which cannot be drawn. " +
                "A line width must be a finite number.");
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
