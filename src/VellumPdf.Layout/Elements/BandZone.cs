// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// One third of a zoned <see cref="RunningBand"/>: a line of styled text runs, a small image, or
/// nothing.
/// </summary>
/// <remarks>
/// <b>What can be known from the zone alone is refused when it is built; what depends on the document
/// is refused by the save.</b> The constructors and <see cref="Logo"/> throw for a null zone part, a
/// run with no style, a page reference, a style that sets a link, a
/// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name, a font size that is
/// not a positive finite number of at least 5e-6, a colour channel outside 0 to 1, an unpaired
/// surrogate in a run's text, and an image with no area. The save refuses what needs the document: a
/// font handle that another document made (<see cref="ArgumentException"/>, naming the handle), a
/// logo that does not fit its band, and a band height too small for the zones; see
/// <see cref="RunningBand"/>. A zone is immutable, and one instance may be given to more than one
/// band.
/// <para><b>Do not pass</b> a run in <see cref="VellumPdf.Fonts.Standard14.Symbol"/> or
/// <see cref="VellumPdf.Fonts.Standard14.ZapfDingbats"/>. Every glyph of those faces measures zero, so
/// the band can neither cut such a run nor keep the next run of the zone off it: the runs are drawn at
/// one x. The zone itself stays at its place in the content box. 3.0 measures them.</para>
/// <para>A zone holds text runs or one image, never both. A band that wants a logo beside a page
/// number puts them in two zones.</para>
/// <para>Each run is resolved as a <see cref="RunningBand.Template"/> is: <c>{page}</c> and
/// <c>{pages}</c> always, and <c>{chapter}</c> when the band sets
/// <see cref="RunningBand.ChapterToken"/>. A token never spans two runs.</para>
/// </remarks>
public sealed class BandZone
{
    private readonly TextRun[] _runs;

    /// <summary>A zone that draws nothing. It is the value to pass for a side a band leaves empty.</summary>
    public static BandZone Empty { get; } = new([], null);

    private BandZone(TextRun[] runs, LayoutImage? image)
    {
        _runs = runs;
        Image = image;
        Runs = new ReadOnlyCollection<TextRun>(_runs);
    }

    /// <summary>The runs the zone draws, left to right. Empty for an image zone and for <see cref="Empty"/>.</summary>
    public IReadOnlyList<TextRun> Runs { get; }

    /// <summary>The image the zone draws, or <see langword="null"/> for a text zone.</summary>
    /// <remarks>
    /// Only the <see cref="LayoutImage.Image"/> of the element is read. Its
    /// <see cref="LayoutImage.Width"/>, <see cref="LayoutImage.Height"/>,
    /// <see cref="LayoutImage.Margins"/>, <see cref="LayoutImage.Alignment"/>,
    /// <see cref="LayoutImage.FitToPage"/>, <see cref="LayoutImage.BreakBefore"/>,
    /// <see cref="LayoutImage.KeepWithNext"/>, <see cref="LayoutImage.Anchor"/>,
    /// <see cref="LayoutImage.AltText"/> and <see cref="LayoutImage.SpaceAfter"/> are ignored and
    /// nothing reports it, because a band sizes its image and draws it as decoration with no
    /// alternative text. <b>Do not rely</b> on any of them to move or describe the image.
    /// </remarks>
    public LayoutImage? Image { get; }

    /// <summary>Creates a text zone from runs.</summary>
    /// <param name="runs">
    /// The runs, drawn left to right on one line. An empty sequence gives a zone that draws nothing.
    /// </param>
    /// <remarks>
    /// <b>A run that cannot be drawn is refused here.</b> The constructor throws
    /// <see cref="ArgumentException"/> for a null run, a run with no style, a run made by
    /// <see cref="TextRun.PageReference"/>, a style that sets <see cref="TextStyle.LinkUri"/> or
    /// <see cref="TextStyle.LinkAnchor"/> (a band is decoration and writes no link), and text that
    /// holds an unpaired surrogate. It throws <see cref="ArgumentOutOfRangeException"/> for a
    /// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name, for a font size
    /// that is not a finite number of at least 5e-6 (a smaller size is written as 0), and for a colour
    /// channel that is not a number from 0 to 1. A font handle that another document made is refused
    /// by the save. A tab, a line feed
    /// or another white-space control character in a run is drawn as one space, as in a template.
    /// <para>The sequence is copied, so changing it afterwards changes nothing.</para>
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="runs"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// A run is <see langword="null"/>, has no style, is a page reference, holds an unpaired
    /// surrogate, or its style sets a link.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A run's font size is not a finite number of at least 5e-6, its font is a
    /// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name, or a channel of its
    /// colour is not a number from 0 to 1.
    /// </exception>
    public BandZone(IEnumerable<TextRun> runs)
        : this(Check(runs), null)
    {
    }

    /// <summary>Creates a text zone of one run.</summary>
    /// <param name="text">The text, which may hold <c>{page}</c>, <c>{pages}</c> and <c>{chapter}</c>.</param>
    /// <param name="style">The style of the text.</param>
    /// <remarks>The refusals are those of <see cref="BandZone(IEnumerable{TextRun})"/>.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="style"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The style sets a link, or the text holds an unpaired surrogate.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The style's font size is not a finite number of at least 5e-6, its font is a
    /// <see cref="VellumPdf.Fonts.Standard14"/> value the enumeration does not name, or a channel of
    /// its colour is not a number from 0 to 1.
    /// </exception>
    public BandZone(string text, TextStyle style)
        : this(Check([new TextRun(text, style ?? throw new ArgumentNullException(nameof(style)))]), null)
    {
    }

    /// <summary>Creates an image zone.</summary>
    /// <param name="image">
    /// The image. It is drawn as tall as the band and as wide as its proportions give, as
    /// decoration.
    /// </param>
    /// <returns>A zone that draws the image.</returns>
    /// <remarks>
    /// <b>An image with no area is refused here.</b> The call throws
    /// <see cref="ArgumentException"/> when the image's pixel width or height is not positive. A
    /// logo that is too wide for its band, or whose size would be written as zero, is refused by the
    /// save, naming the band; see <see cref="RunningBand"/>. Which properties of
    /// <paramref name="image"/> are read is on <see cref="Image"/>. The image follows the image rules
    /// of the document's conformance claim as a body image does: a DeviceCMYK logo under PDF/A needs a
    /// CMYK output intent, and the save does not report one that lacks it.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="image"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The image's pixel width or height is not positive.</exception>
    public static BandZone Logo(LayoutImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (image.Image.Width <= 0 || image.Image.Height <= 0)
            throw new ArgumentException(
                FormattableString.Invariant(
                    $"The image is {image.Image.Width} x {image.Image.Height} pixels and has no area."),
                nameof(image));
        return new BandZone([], image);
    }

    private static bool HasUnpairedSurrogate(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                i++;
            else if (char.IsSurrogate(text[i]))
                return true;
        }

        return false;
    }

    private static TextRun[] Check(IEnumerable<TextRun> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        var copy = runs.ToArray();
        for (var i = 0; i < copy.Length; i++)
        {
            var run = copy[i];
            if (run is null)
                throw new ArgumentException(
                    FormattableString.Invariant($"The run at index {i} is null."), nameof(runs));
            if (run.Style is null)
                throw new ArgumentException(
                    FormattableString.Invariant($"The run at index {i} has no style."), nameof(runs));
            if (run.IsReference)
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The run at index {i} is a page reference, which a band does not draw."),
                    nameof(runs));
            if (run.Style.LinkUri is not null || run.Style.LinkAnchor is not null)
                throw new ArgumentException(
                    FormattableString.Invariant(
                        $"The style of the run at index {i} sets a link, which a band does not write."),
                    nameof(runs));
            if (HasUnpairedSurrogate(run.Text))
                throw new ArgumentException(
                    FormattableString.Invariant($"The text of the run at index {i} holds an unpaired surrogate."),
                    nameof(runs));
            var font = run.Style.FontRef;
            if (!font.IsEmbedded && !Enum.IsDefined(font.Standard14))
                throw new ArgumentOutOfRangeException(
                    nameof(runs), (int)font.Standard14,
                    FormattableString.Invariant(
                        $"The style of the run at index {i} names the Standard14 value {(int)font.Standard14}, which the enumeration does not define."));
            var size = run.Style.FontSize;

            // Below 5e-6 the canvas writes the size as 0, so the text would be drawn at no size.
            if (!(size >= 5e-6) || !double.IsFinite(size))
                throw new ArgumentOutOfRangeException(
                    nameof(runs), size,
                    FormattableString.Invariant(
                        $"The style of the run at index {i} has a font size of {size}. A band's font size must be a finite number of at least 5e-6."));
            LayoutValidation.ValidateRunColor(run.Style.Color, i, nameof(runs));
        }

        return copy;
    }
}
