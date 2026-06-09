// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Elements;

/// <summary>
/// A header or footer band repeated on every page.
/// Text supports <c>{page}</c> (current page number) and <c>{pages}</c> (total page count)
/// tokens, which are substituted at render time.
/// </summary>
public sealed class RunningBand
{
    /// <summary>Text template — may contain {page} and/or {pages}.</summary>
    public string Template { get; }

    /// <summary>The text style of the band.</summary>
    public TextStyle Style { get; }

    /// <summary>Horizontal alignment of the band text.</summary>
    public HorizontalAlignment Alignment { get; }

    /// <summary>
    /// Reserved height in points. Defaults to <c>Style.EffectiveLeading + 4</c>.
    /// Caller may override for tighter/looser bands.
    /// </summary>
    public double? Height { get; init; }

    /// <summary>Creates a running band from a text template, with optional style and alignment (defaults to centered).</summary>
    public RunningBand(string template, TextStyle? style = null, HorizontalAlignment alignment = HorizontalAlignment.Center)
    {
        Template = template;
        Style = style ?? TextStyle.Default;
        Alignment = alignment;
    }

    /// <summary>Returns the effective band height (leading + small padding).</summary>
    public double EffectiveHeight => Height ?? (Style.EffectiveLeading + 4);

    /// <summary>Substitutes {page} and {pages} tokens.</summary>
    public string Resolve(int pageNumber, int totalPages) =>
        Template
            .Replace("{page}", pageNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal)
            .Replace("{pages}", totalPages.ToString(System.Globalization.CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
}
