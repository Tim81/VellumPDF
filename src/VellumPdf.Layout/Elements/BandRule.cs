// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>Which edge of a <see cref="RunningBand"/> a <see cref="BandRule"/> lies on.</summary>
public enum BandRulePosition
{
    /// <summary>On the band's top edge, where a footer's rule separates it from the page's content.</summary>
    Above,

    /// <summary>On the band's bottom edge, where a header's rule separates it from the page's content.</summary>
    Below,
}

/// <summary>
/// A horizontal line drawn along one edge of a <see cref="RunningBand"/>, across the page's content
/// width.
/// </summary>
/// <remarks>
/// <b>A width that is not a finite number of at least zero, and a colour channel outside 0 to 1,
/// are refused when the rule is built.</b> The constructor throws
/// <see cref="ArgumentOutOfRangeException"/>. A width of zero draws the thinnest line the device
/// renders.
/// <para>On a zoned band the line lies inside the band: with <see cref="BandRulePosition.Above"/> it
/// occupies the top <see cref="Width"/> points of the band and with
/// <see cref="BandRulePosition.Below"/> the bottom ones, so a rule does not move the page's content.
/// The band keeps its text and its logos 1 point clear of it and refuses a height that cannot; see
/// <see cref="RunningBand"/>. On a string band the line is centred half its width inside the same edge
/// and nothing checks the band's height against it, so a width beyond the band's height reaches past
/// the band. It runs from the content box's left edge to its right edge, which under
/// <see cref="Document.MirrorMargins"/> are the margins of the page's own side. It is drawn on every
/// page that has the band; a blank page a recto break inserts has no band and so no rule.</para>
/// </remarks>
public sealed class BandRule
{
    /// <summary>Creates a rule.</summary>
    /// <param name="position">The edge of the band the rule lies on.</param>
    /// <param name="width">The line width in points; 0.5 by default.</param>
    /// <param name="color">The line colour, or <see langword="null"/> for black.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="width"/> is not a finite number of at least zero, <paramref name="position"/>
    /// is not a named value, or a channel of <paramref name="color"/> is not a number from 0 to 1.
    /// </exception>
    public BandRule(BandRulePosition position, double width = 0.5, ColorRgb? color = null)
    {
        if (!Enum.IsDefined(position))
            throw new ArgumentOutOfRangeException(nameof(position), position, "The position is not a named value.");
        if (!double.IsFinite(width) || width < 0)
            throw new ArgumentOutOfRangeException(
                nameof(width), width,
                FormattableString.Invariant(
                    $"A rule's width must be a finite number of at least zero (was {width})."));
        Position = position;
        Width = width;
        Color = LayoutValidation.ValidateUnitColor(color ?? ColorRgb.Black, nameof(color));
    }

    /// <summary>The edge of the band the rule lies on.</summary>
    public BandRulePosition Position { get; }

    /// <summary>The line width in points.</summary>
    public double Width { get; }

    /// <summary>The line colour.</summary>
    public ColorRgb Color { get; }
}
