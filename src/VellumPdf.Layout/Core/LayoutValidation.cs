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
/// Each check is deliberately narrower than the property it guards, and the boundary was measured
/// rather than assumed. Two of these inputs already threw, so only the wording changes: a non-finite
/// font size and a <c>ColSpan</c> below one. The other two produced a document whose content stream
/// was invalid, a singular or non-numeric transformation matrix, which is the other half of this
/// patch's rule. Everything adjacent renders today and is left alone: a font size of zero or less, a
/// non-finite leading, a <c>RowSpan</c> below one, and a negative image extent. Each of those is a
/// contract tightening for the next major, not a defect to fix here.
/// </summary>
internal static class LayoutValidation
{
    /// <summary>
    /// Rejects a text style whose font size is not a finite number.
    ///
    /// <c>EffectiveLeading</c> is <c>Leading > 0 ? Leading : FontSize * 1.2</c>, so a non-finite
    /// font size makes every height derived from it non-finite, every height comparison false, and
    /// the renderer returns <c>Nothing</c> having measured nothing at all. That is what the caller
    /// saw as the too-tall message.
    /// </summary>
    internal static void ValidateStyle(TextStyle style, string owner)
    {
        // Non-finite only, deliberately. A font size of zero or less renders today: measured, it
        // reaches the content stream as "/F1 0 Tf", a valid operator, and the paragraph falls back
        // to the default leading. Rejecting it would turn a rendering document into an exception,
        // which this release defers to the next major. A non-finite leading renders too, and
        // emits a valid text matrix, so it is not checked here at all.
        if (!double.IsFinite(style.FontSize))
        {
            throw new InvalidOperationException(
                $"{owner} has a font size of {Format(style.FontSize)}, which cannot be laid out. " +
                "A font size must be a finite number.");
        }
    }

    /// <summary>
    /// Rejects a cell that covers less than one column.
    ///
    /// The grid resolver takes the column count from the widest row's span sum, so a
    /// <c>ColSpan</c> of zero can leave it at zero: no width is allocated, nothing can be drawn,
    /// and the table reports that it placed nothing, which the caller saw as the too-tall
    /// message.
    /// </summary>
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
    }

    /// <summary>
    /// Rejects an image whose drawn size is zero or non-finite.
    ///
    /// A zero extent reaches the content stream as a singular transformation matrix, which cannot
    /// be inverted and which ISO 32000-2 leaves undefined for a painted XObject. A non-finite one
    /// reaches it as a token that is not a PDF number at all. Both saved without complaint and
    /// produced a document a reader cannot render (#478).
    /// </summary>
    internal static void ValidateImageExtent(double width, double height)
    {
        // Zero and non-finite only. Measured on the emitted content stream: a width of zero gives
        // "0 0 0 0 10 390 cm", a singular matrix; a width of NaN gives "NaN 0 0 NaN 10 NaN cm",
        // where NaN is not a PDF number at all. Both are invalid output, which is what makes
        // rejecting them admissible here. A negative width gives "-40 0 0 -40 10 430 cm", a
        // perfectly valid matrix that mirrors the image, so it renders and is left alone; an
        // earlier pull request decided that deliberately and its test says so.
        if (!double.IsFinite(width) || width == 0)
        {
            throw new InvalidOperationException(
                $"An image has a width of {Format(width)}, which cannot be drawn. " +
                "A width must be a finite, non-zero number; leave it unset to fill the content box.");
        }

        if (!double.IsFinite(height) || height == 0)
        {
            throw new InvalidOperationException(
                $"An image has a height of {Format(height)}, which cannot be drawn. " +
                "A height must be a finite, non-zero number; leave it unset to derive it from the width.");
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
        : value.ToString("0.####", CultureInfo.InvariantCulture);
}
