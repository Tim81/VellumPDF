// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Layout.Core;

/// <summary>
/// A position in a <see cref="Elements.Paragraph"/> where the text after a tab character
/// (U+0009) is placed, with the alignment it takes there and the leader that fills the space
/// before it.
/// </summary>
/// <remarks>
/// A stop applies through <see cref="Elements.Paragraph.TabStops"/>; the rule for placing text at
/// it is described there. A stop is immutable once built. This type does not know the width of
/// the content area. A position that exceeds that width by 5e-6 points or more is refused when
/// the document is saved for a paragraph it lays out itself, and is treated as absent, with a
/// diagnostic, for a renderer of yours that lays the paragraph out narrower; see
/// <see cref="Elements.Paragraph.TabStops"/>. A position of zero is accepted and never used.
/// </remarks>
public sealed class TabStop
{
    private char _decimalCharacter = '.';

    /// <summary>
    /// Creates a stop at <paramref name="position"/> points from the paragraph's left edge.
    /// </summary>
    /// <remarks>
    /// <b>A position that is not a finite number, or is negative, is refused.</b> The constructor
    /// throws <see cref="ArgumentOutOfRangeException"/>, because a text position cannot be
    /// <c>NaN</c> or infinite and a stop left of the edge could never be reached from the
    /// paragraph's start. <b>An <paramref name="alignment"/> or <paramref name="leader"/> that
    /// the enumeration does not name is refused</b> the same way. A position of zero is accepted.
    /// <para>The constructor does not know the paragraph's width, so a position past the right
    /// edge is dealt with later: the save refuses it for a paragraph the document lays out, and a
    /// renderer of yours that lays the paragraph out narrower treats it as absent; see
    /// <see cref="Elements.Paragraph.TabStops"/>. <b>Do not pass</b> zero: it is accepted, but a
    /// tab looks for a stop greater than the pen, so no tab uses it, and 3.0 will refuse it.</para>
    /// </remarks>
    /// <param name="position">
    /// Distance in points from the left edge of the paragraph's content, which is the content area
    /// of the page with <see cref="Elements.Paragraph.Margins"/> taken off.
    /// </param>
    /// <param name="alignment">How the text sits against the position.</param>
    /// <param name="leader">What fills the space before the text.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="position"/> is <c>NaN</c>, infinite or negative, or
    /// <paramref name="alignment"/> or <paramref name="leader"/> is not a defined value.
    /// </exception>
    public TabStop(double position, TabAlignment alignment = TabAlignment.Left, TabLeader leader = TabLeader.None)
    {
        if (!double.IsFinite(position) || position < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(position),
                "A tab stop position must be a finite number of points, zero or more.");
        }
        if (!Enum.IsDefined(alignment))
            throw new ArgumentOutOfRangeException(nameof(alignment), "The tab alignment is not a defined value.");
        if (!Enum.IsDefined(leader))
            throw new ArgumentOutOfRangeException(nameof(leader), "The tab leader is not a defined value.");

        Position = position;
        Alignment = alignment;
        Leader = leader;
    }

    /// <summary>Distance in points from the left edge of the paragraph's content.</summary>
    /// <remarks>Always finite and zero or more.</remarks>
    public double Position { get; }

    /// <summary>How the text after the tab sits against <see cref="Position"/>.</summary>
    /// <remarks>Always a defined value.</remarks>
    public TabAlignment Alignment { get; }

    /// <summary>What fills the space before the text.</summary>
    /// <remarks>Always a defined value.</remarks>
    public TabLeader Leader { get; }

    /// <summary>
    /// The character a <see cref="TabAlignment.Decimal"/> stop lines up on. The default is a full
    /// stop (<c>.</c>).
    /// </summary>
    /// <remarks>
    /// <b>A white-space, control or surrogate character is refused.</b> The setter throws
    /// <see cref="ArgumentException"/>, because such a character cannot be told apart in a figure
    /// column and a surrogate half is not a character on its own. It has no effect on a stop
    /// whose alignment is not <see cref="TabAlignment.Decimal"/>. A figure column that uses a
    /// decimal comma sets <c>','</c>; the library does not pick the character from the culture.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The value is white space, a control character or a surrogate.
    /// </exception>
    public char DecimalCharacter
    {
        get => _decimalCharacter;
        init
        {
            if (char.IsWhiteSpace(value) || char.IsControl(value) || char.IsSurrogate(value))
            {
                throw new ArgumentException(
                    "A decimal character must not be white space, a control character or a surrogate.",
                    nameof(value));
            }
            _decimalCharacter = value;
        }
    }
}
