// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Samples.Manual;

/// <summary>
/// The text styles of the manual. Every style uses an embedded Liberation face, because PDF/A-2u
/// refuses a font that is not embedded.
/// </summary>
public sealed class ManualStyles
{
    /// <summary>The brand colour used for headings and table headers.</summary>
    public static readonly ColorRgb Brand = new(0.05, 0.36, 0.42);

    /// <summary>A light tint of the brand colour for callouts and zebra rows.</summary>
    public static readonly ColorRgb Tint = new(0.90, 0.95, 0.96);

    /// <summary>The grey of rules, table borders and running text.</summary>
    public static readonly ColorRgb Grey = new(0.45, 0.48, 0.50);

    /// <summary>The colour of a link.</summary>
    public static readonly ColorRgb LinkBlue = new(0.05, 0.25, 0.65);

    private ManualStyles(LayoutDocument doc)
    {
        var serif = Load(doc, Standard14.TimesRoman);
        var serifBold = Load(doc, Standard14.TimesBold);
        var serifItalic = Load(doc, Standard14.TimesItalic);
        var sans = Load(doc, Standard14.Helvetica);
        var sansBold = Load(doc, Standard14.HelveticaBold);
        var mono = Load(doc, Standard14.Courier);

        Body = new TextStyle { FontRef = serif, FontSize = 10, Leading = 13.5 };
        Bold = new TextStyle { FontRef = serifBold, FontSize = 10, Leading = 13.5 };
        Italic = new TextStyle { FontRef = serifItalic, FontSize = 10, Leading = 13.5 };
        Code = new TextStyle { FontRef = mono, FontSize = 9, Leading = 13.5 };
        Small = new TextStyle { FontRef = sans, FontSize = 8.5, Leading = 11, Color = Grey };
        Chapter = new TextStyle { FontRef = sansBold, FontSize = 24, Leading = 29, Color = Brand };
        Section = new TextStyle { FontRef = sansBold, FontSize = 14, Leading = 18, Color = Brand };
        Subsection = new TextStyle { FontRef = sansBold, FontSize = 11.5, Leading = 15 };
        Header = new TextStyle { FontRef = sans, FontSize = 8.5, Leading = 11, Color = Grey };
        TableHead = new TextStyle { FontRef = sansBold, FontSize = 9, Leading = 12, Color = new ColorRgb(1, 1, 1) };
        TableBody = new TextStyle { FontRef = sans, FontSize = 9, Leading = 12 };
        TableBold = new TextStyle { FontRef = sansBold, FontSize = 9, Leading = 12 };
        TableCode = new TextStyle { FontRef = mono, FontSize = 8.5, Leading = 12 };
        CodeBlock = new TextStyle { FontRef = mono, FontSize = 8.5, Leading = 11.5 };
        TitleName = new TextStyle { FontRef = sansBold, FontSize = 44, Leading = 50, Color = Brand };
        TitleKind = new TextStyle { FontRef = sans, FontSize = 20, Leading = 26, Color = Grey };
        TitleLine = new TextStyle { FontRef = sans, FontSize = 11, Leading = 16 };
    }

    /// <summary>Body text.</summary>
    public TextStyle Body { get; }

    /// <summary>Bold body text.</summary>
    public TextStyle Bold { get; }

    /// <summary>Italic body text.</summary>
    public TextStyle Italic { get; }

    /// <summary>Monospace text inside a paragraph.</summary>
    public TextStyle Code { get; }

    /// <summary>Small print, for notes and the colophon.</summary>
    public TextStyle Small { get; }

    /// <summary>A chapter heading.</summary>
    public TextStyle Chapter { get; }

    /// <summary>A section heading.</summary>
    public TextStyle Section { get; }

    /// <summary>A subsection heading.</summary>
    public TextStyle Subsection { get; }

    /// <summary>The running header and footer.</summary>
    public TextStyle Header { get; }

    /// <summary>A table header cell.</summary>
    public TextStyle TableHead { get; }

    /// <summary>A table body cell.</summary>
    public TextStyle TableBody { get; }

    /// <summary>Bold text in a table body cell.</summary>
    public TextStyle TableBold { get; }

    /// <summary>A table body cell that holds a command or a code.</summary>
    public TextStyle TableCode { get; }

    /// <summary>A preformatted command block.</summary>
    public TextStyle CodeBlock { get; }

    /// <summary>The product name on the title page.</summary>
    public TextStyle TitleName { get; }

    /// <summary>The kind of document on the title page.</summary>
    public TextStyle TitleKind { get; }

    /// <summary>A line of detail on the title page.</summary>
    public TextStyle TitleLine { get; }

    /// <summary>Embeds the fonts into <paramref name="doc"/> and builds the styles.</summary>
    /// <param name="doc">The document that will hold the fonts.</param>
    /// <returns>The styles.</returns>
    public static ManualStyles Create(LayoutDocument doc) => new(doc);

    // The Standard14 package returns the bytes of the Liberation face that stands in for a
    // standard-14 name, and the Layout document registers them and hands back the font reference.
    private static FontReference Load(LayoutDocument doc, Standard14 face) =>
        doc.UseTrueTypeFont(Standard14Substitutes.GetFontProgram(face));

    /// <summary>
    /// Returns <paramref name="style"/> with a different colour and a link address.
    /// </summary>
    /// <param name="style">The style to copy.</param>
    /// <param name="uri">The absolute link address.</param>
    /// <returns>The linked style.</returns>
    public static TextStyle WithLink(TextStyle style, string uri) => new()
    {
        FontRef = style.FontRef,
        FontSize = style.FontSize,
        Leading = style.Leading,
        Color = LinkBlue,
        LinkUri = uri,
    };

    /// <summary>
    /// Returns <paramref name="style"/> with a link to the anchor of that name.
    /// </summary>
    /// <param name="style">The style to copy.</param>
    /// <param name="anchor">The name of an element's <c>Anchor</c>.</param>
    /// <param name="color">The colour of the linked text, or null to keep the style's own.</param>
    /// <returns>The linked style.</returns>
    public static TextStyle WithAnchor(TextStyle style, string anchor, ColorRgb? color = null) => new()
    {
        FontRef = style.FontRef,
        FontSize = style.FontSize,
        Leading = style.Leading,
        Color = color ?? style.Color,
        LinkAnchor = anchor,
    };

    /// <summary>Returns <paramref name="style"/> with another colour and no link.</summary>
    /// <param name="style">The style to copy.</param>
    /// <param name="color">The colour.</param>
    /// <returns>The coloured style.</returns>
    public static TextStyle WithColor(TextStyle style, ColorRgb color) => new()
    {
        FontRef = style.FontRef,
        FontSize = style.FontSize,
        Leading = style.Leading,
        Color = color,
    };
}
