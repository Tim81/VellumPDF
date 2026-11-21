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
/// that as "this element can never fit" and raises the too-tall message. For a font size of
/// <c>NaN</c> or a <c>ColSpan</c> of zero that message is wrong: the input is unusable, and
/// reducing the content or enlarging the page cannot help.
///
/// A check is made in the layout only where the input yields an invalid file or an exception, the
/// rule the <c>Document</c> remarks state, and it runs inside the element's own <c>Layout</c> call:
/// the font size and colour of text that is drawn, a <c>ColSpan</c> below one, a line separator's width, margins
/// and colour, a table cell's padding, a non-finite table border width, an image extent that is
/// zero or not finite. The other non-finite insets, indents, band heights and colours are not
/// checked here. A negative table border width, a table cell's text colour and a running band's
/// font size are refused where they are drawn, because a table or cell that is laid out and not
/// drawn writes none of them. <c>PdfCanvas</c> refuses what would be written as <c>NaN</c> or
/// <c>Infinity</c>.
///
/// What is left alone renders valid content, so refusing it would turn a working document into an
/// exception: a font size of zero or less, a <c>RowSpan</c> below one, a negative image extent, and
/// a positive-infinity image width, which the over-wide clamp resolves to the content box before
/// anything here sees it. Those are contract tightenings for the next major.
/// </summary>
internal static class LayoutValidation
{
    /// <summary>
    /// The magnitude below which <c>PdfCanvas</c>'s own number format writes the token <c>0</c>, or
    /// <c>-0</c> for a small negative: 5e-6 writes <c>0.00001</c>, 4.9e-6 writes <c>0</c>. The extent
    /// check tests this rather than equality with zero, because what makes the matrix singular is the
    /// token written, not the value held.
    /// </summary>
    private const double SmallestWrittenExtent = 5e-6;

    /// <summary>
    /// Rejects a text style whose font size is not a finite number.
    ///
    /// The three non-finite values take different routes: a NaN size makes every height comparison
    /// false, so the renderer reports placing nothing; a positive-infinity size reports a height
    /// larger than any page; a negative-infinity size is clamped to zero by the line-height maximum
    /// and would write <c>/F1 -Infinity Tf</c>, which is not a PDF number.
    /// </summary>
    internal static void ValidateStyle(TextStyle style, string owner)
    {
        ValidateFontSize(style, owner);
        ValidateColor(style.Color, owner, "text colour");
    }

    /// <summary>
    /// The font-size half of <see cref="ValidateStyle"/>, for the callers that check a colour only
    /// where it is written: a running band and a table cell.
    /// </summary>
    internal static void ValidateFontSize(TextStyle style, string owner)
    {
        // Non-finite only, deliberately. A font size of zero or less renders today and reaches the
        // stream verbatim -- "/F1 0 Tf" at zero, "/F1 -12 Tf" at -12 -- a valid operator either
        // way, so refusing it would turn a working document into an exception. Deferred.
        //
        // Leading is not checked: EffectiveLeading derives any non-finite leading from the font size.
        if (!double.IsFinite(style.FontSize))
        {
            throw new InvalidOperationException(
                $"{owner} has a font size of {Format(style.FontSize)}, which cannot be laid out. " +
                "A font size must be a finite number.");
        }
    }

    /// <summary>
    /// Rejects a colour with a channel that is not a finite number. A channel is written straight
    /// into an <c>rg</c> or <c>RG</c> operand, so <c>NaN</c> reached the stream as <c>NaN 0 0 rg</c>
    /// and infinity as <c>Infinity 0 0 rg</c>, neither of which is a PDF number.
    ///
    /// Finite is all this checks. A channel outside 0 to 1 still parses and renders, so refusing it
    /// would turn a working document into an exception; the range is documented on the colour
    /// types and its refusal is deferred to the next major. <c>PdfCanvas</c> refuses the same values
    /// when it writes the operand; this check names the element the caller wrote.
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
        // ColSpan only: a RowSpan below one renders, because nothing derives the column count from it.
        if (cell.ColSpan < 1)
        {
            throw new InvalidOperationException(
                $"Table row {rowIndex}, cell {cellIndex} has a ColSpan of {cell.ColSpan}. " +
                "A span must cover at least one column.");
        }

        ValidateInsets(cell.Padding, $"Table row {rowIndex}, cell {cellIndex}");

        // The background colour is not checked here; the canvas refuses a non-finite channel where it
        // is written.
    }

    /// <summary>
    /// Rejects an image whose drawn extent is written as zero, or is not finite.
    ///
    /// A zero extent gives a transformation matrix with no inverse: a zero width writes
    /// <c>0 0 0 0 10 390 cm</c>. ISO 32000-2, 8.3.4, NOTE 3 records that such a matrix has no
    /// unique inverse and that painting through one "can result in unpredictable behaviour"; the note
    /// is informative. A non-finite extent writes <c>NaN 0 0 NaN 10 NaN cm</c>, which is not a PDF
    /// number at all.
    /// </summary>
    internal static void ValidateImageExtent(double width, double height)
    {
        Check(width, "width");
        Check(height, "height");

        static void Check(double value, string which)
        {
            if (double.IsFinite(value) && Math.Abs(value) >= SmallestWrittenExtent) return;

            // No "leave it unset" advice: margins that consume the content box can reach zero
            // while the property was never set.
            throw new InvalidOperationException(
                $"An image would be drawn with a {which} of {Format(value)}, which cannot be " +
                $"painted. A drawn {which} must be finite and at least " +
                $"{Format(SmallestWrittenExtent)} points in magnitude; anything smaller is written " +
                "as zero, giving a transformation matrix with no inverse.");
        }
    }

    /// <summary>
    /// Rejects a non-finite line width, which reaches the stream as <c>NaN w</c>, and a negative
    /// one, which reaches it as <c>-3 w</c>.
    ///
    /// ISO 32000-2, 8.4.3.2 (Line width) says the operand "shall be a non-negative number", so a
    /// negative width is a non-conforming operand even though it parses. That is why this refuses
    /// it where the neighbouring out-of-range inputs, such as a colour channel above 1, are only
    /// documented (the one exception is a row background, which <c>TableElement.Add(Row)</c>
    /// refuses at the call): those render a valid file, and this writes an operand the standard
    /// forbids.
    ///
    /// Zero is left alone, unlike an image extent: <c>0 w</c> is a defined operator asking for the
    /// thinnest line the device renders, not a degenerate matrix. The same clause says such lines
    /// "should not be used" because the result is device-dependent, which is a caller warning
    /// rather than a refusal and is carried by the member documentation.
    /// </summary>
    internal static void ValidateLineWidth(double width, string owner)
    {
        ValidateLineWidthIsFinite(width, owner);

        if (width < 0)
        {
            throw new InvalidOperationException(
                $"{owner} has a line width of {Format(width)}, which cannot be drawn. " +
                "A line width must not be negative (ISO 32000-2, 8.4.3.2).");
        }
    }

    /// <summary>
    /// The finite half of <see cref="ValidateLineWidth"/>, for a caller that checks the sign where the
    /// width is written: a table's border width, whose sign is checked only where a cell border is drawn.
    /// </summary>
    internal static void ValidateLineWidthIsFinite(double width, string owner)
    {
        if (!double.IsFinite(width))
        {
            throw new InvalidOperationException(
                $"{owner} has a line width of {Format(width)}, which cannot be drawn. " +
                "A line width must be a finite number.");
        }
    }

    /// <summary>
    /// Rejects insets that are not finite, for the two elements where such an inset yields an invalid
    /// file or an exception for every edge and every one of <c>NaN</c>, positive and negative
    /// infinity: a line separator's margins and a table cell's padding. Every other inset is not
    /// checked here.
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
    /// Rejects a row background whose channel is not a number from 0 to 1, the DeviceRGB component
    /// range of ISO 32000-2:2020, 8.6.4.3. The value is tested as given, not as the token
    /// <c>PdfCanvas</c> would write, so 1.000001 is refused although it is written as <c>1</c>.
    /// </summary>
    /// <param name="color">The row's background, or null for none.</param>
    /// <param name="paramName">The name of the parameter the row arrived through.</param>
    internal static void ValidateRowBackground(ColorRgb? color, string paramName)
    {
        if (color is not { } c) return;

        var channel = !IsUnit(c.R) ? ("red", c.R)
            : !IsUnit(c.G) ? ("green", c.G)
            : !IsUnit(c.B) ? ("blue", c.B)
            : default;
        if (channel.Item1 is null) return;

        throw new ArgumentOutOfRangeException(
            paramName,
            channel.Item2,
            $"The row's background has a {channel.Item1} channel of {FormatExact(channel.Item2)}. " +
            "ISO 32000-2:2020, 8.6.4.3 says each DeviceRGB component is a number from 0.0 to 1.0.");

        static bool IsUnit(double value) => value is >= 0 and <= 1;
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

    /// <summary>
    /// Renders a rejected number exactly, so a value that <see cref="Format"/> would round to zero
    /// is still told apart from zero.
    /// </summary>
    private static string FormatExact(double value) =>
        double.IsFinite(value)
            ? value.ToString("R", CultureInfo.InvariantCulture)
            : Format(value);
}
