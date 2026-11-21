// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// Selects a font and shows text for the three places that draw text: paragraphs (and the headings
/// and list text built on them), table cells and running bands. Each keeps its own text object,
/// colour and text matrix, and the order of them; only the font selection and the text operator are
/// shared, so a report made here covers every built-in element.
/// </summary>
internal static class TextEmitter
{
    /// <summary>
    /// Writes the <c>Tf</c> operator for <paramref name="style"/>, unless
    /// <paramref name="current"/> already holds the same font reference at the same size. The size
    /// is compared as the token the canvas writes, so a difference below its five decimals writes
    /// no second <c>Tf</c>, and a run in the same font at another size writes one. A caller that
    /// passes a fresh null writes a <c>Tf</c> every time.
    /// </summary>
    /// <remarks>
    /// Under a conformance claim, a named Standard 14 font that is selected is reported as
    /// <see cref="LayoutDiagnosticCode.UnembeddedFont"/>, once per <c>Tf</c> written. The report
    /// follows the write, so a size the canvas refuses reports nothing, and a value the enumeration
    /// does not name is never reported: reading its name is what makes the save throw, and the
    /// report would turn that exception into the strict refusal.
    /// </remarks>
    internal static void SelectFont(DrawContext ctx, PdfCanvas canvas, TextStyle style, ref SelectedFont? current)
    {
        // NaN and the infinities format without throwing and never equal a stored token, so the
        // canvas still refuses them.
        var sizeToken = PdfNumberFormat.FormatReal(style.FontSize);
        if (current.HasValue
            && FontRefEqual(current.Value.Font, style.FontRef)
            && current.Value.SizeToken == sizeToken)
            return;

        if (style.FontRef.IsEmbedded)
        {
            var resourceName = ctx.UseEmbeddedFont(style.FontRef.Embedded);
            canvas.SetFontByName(resourceName, style.FontSize);
        }
        else
        {
            var fontResource = ctx.GetFont(style.Font);
            canvas.SetFont(fontResource, style.FontSize);
            if (ctx.Diagnostics is { } sink && sink.Conformance != PdfConformance.None && Enum.IsDefined(style.Font))
                sink.Report(
                    LayoutDiagnosticCode.UnembeddedFont,
                    (long)style.Font,
                    static font => new PdfFontResource((Standard14)font, "F").BaseFontName
                        + " is a Standard 14 font and is not embedded. Give this element a style with an embedded"
                        + " font; SetDefaultFont applies only to Document.Add(string).",
                    perElement: true,
                    clause: LayoutDiagnosticSink.ClauseFor(LayoutDiagnosticCode.UnembeddedFont, sink.Conformance));
        }
        current = new SelectedFont(style.FontRef, sizeToken);
    }

    /// <summary>
    /// Shows <paramref name="text"/> in the style's font: an embedded font as a glyph run that the
    /// canvas lists glyph 0 for, a Standard 14 font as a WinAnsi string.
    /// </summary>
    internal static void Show(PdfCanvas canvas, TextStyle style, string text)
    {
        if (style.FontRef.IsEmbedded)
            canvas.ShowEmbeddedText(style.FontRef.Embedded, text);
        else
            canvas.ShowText(text);
    }

    /// <summary>
    /// The font in force in a text object: the reference, and the size as the token written for it.
    /// </summary>
    internal readonly record struct SelectedFont(FontReference Font, string SizeToken);

    private static bool FontRefEqual(FontReference a, FontReference b)
    {
        if (a.IsEmbedded != b.IsEmbedded) return false;
        return a.IsEmbedded
            ? ReferenceEquals(a.Embedded, b.Embedded)
            : a.Standard14 == b.Standard14;
    }
}
