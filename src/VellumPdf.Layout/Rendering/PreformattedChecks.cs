// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;

namespace VellumPdf.Layout.Rendering;

/// <summary>
/// What the save refuses about a <see cref="Preformatted"/> before any layout runs. A refusal that
/// came after the first renderer measured would burn the document, because a renderer caches its
/// wrap; one raised here leaves the document saveable once the caller has changed what the message
/// names.
/// </summary>
internal static class PreformattedChecks
{
    /// <summary>
    /// Checks every <see cref="PreformattedRenderer"/> among <paramref name="renderers"/>, in
    /// order, and throws <see cref="InvalidOperationException"/> for the first failure. A document
    /// that holds none costs one pass over the list.
    /// </summary>
    internal static void Run(IReadOnlyList<IRenderer> renderers, PdfConformance claim, LayoutBox contentArea)
    {
        for (var i = 0; i < renderers.Count; i++)
        {
            if (renderers[i] is not PreformattedRenderer renderer)
                continue;

            var element = renderer.Element;
            CheckGeometry(i, element, contentArea);
            if (claim == PdfConformance.None)
                continue;

            CheckFont(i, element, claim);
            CheckCharacters(i, element, claim);
        }
    }

    private static void CheckGeometry(int index, Preformatted element, LayoutBox contentArea)
    {
        var box = contentArea.Deflate(element.Margins);
        var leading = element.Style.EffectiveLeading;
        if (box.Width > 0 && box.Height >= leading)
            return;

        throw new InvalidOperationException(
            FormattableString.Invariant(
                $"Preformatted element {index} cannot hold a line: its content box is {box.Width:0.##} by {box.Height:0.##} pt and one line is {leading:0.##} pt tall.") +
            " Reduce the font size, the element's margins or the document's margins, or increase the page size.");
    }

    private static string ClaimName(PdfConformance claim) => claim switch
    {
        PdfConformance.PdfA2b => "PDF/A-2b",
        PdfConformance.PdfA2u => "PDF/A-2u",
        PdfConformance.PdfA2a => "PDF/A-2a",
        PdfConformance.PdfUA1 => "PDF/UA-1",
        _ => claim.ToString(),
    };

    private static string Clause(PdfConformance claim, string pdfa, string pdfua) =>
        claim == PdfConformance.PdfUA1 ? "ISO 14289-1:2014, " + pdfua : "ISO 19005-2:2011, " + pdfa;

    private static void CheckFont(int index, Preformatted element, PdfConformance claim)
    {
        var fontRef = element.Style.FontRef;
        if (fontRef.IsEmbedded)
            return;

        var name = new PdfFontResource(fontRef.Standard14, "F").BaseFontName;
        throw new InvalidOperationException(
            FormattableString.Invariant(
                $"Preformatted element {index} is set in {name}, which is not embedded, and the document claims {ClaimName(claim)}.") +
            $" {Clause(claim, "6.2.11.4.1", "7.21.4.1")} requires every font it uses to be embedded." +
            " Set the style's FontRef to an embedded font, or set Document.Conformance to None.");
    }

    // Walks the text as scalars without registering a glyph, so a refusal leaves the subset as it
    // was. A tab is checked as the space it expands to.
    private static void CheckCharacters(int index, Preformatted element, PdfConformance claim)
    {
        var handle = element.Style.FontRef.Embedded;
        var banUnicodeNoncharacters = claim is PdfConformance.PdfA2u or PdfConformance.PdfA2a or PdfConformance.PdfUA1;
        var text = element.Text;
        var line = 1;
        for (var i = 0; i < text.Length;)
        {
            var c = text[i];
            if (c == '\r')
            {
                line++;
                i += i + 1 < text.Length && text[i + 1] == '\n' ? 2 : 1;
                continue;
            }

            if (c == '\n')
            {
                line++;
                i++;
                continue;
            }

            var scalar = c == '\t' ? ' ' : char.IsHighSurrogate(c) && i + 1 < text.Length ? char.ConvertToUtf32(c, text[i + 1]) : c;
            if (banUnicodeNoncharacters && scalar is 0xFEFF or 0xFFFE)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Preformatted element {index} holds U+{scalar:X4} at index {i} of its text (line {line}), and the document claims {ClaimName(claim)}.") +
                    $" {Clause(claim, "6.2.11.7.2", "7.21.7")} does not allow a ToUnicode value of U+FEFF or U+FFFE." +
                    " Remove the character, or set Document.Conformance to None.");
            }

            if (handle.PeekGlyphId(scalar) == 0)
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant(
                        $"Preformatted element {index} holds U+{scalar:X4} at index {i} of its text (line {line}), which font {handle.ResourceName} has no glyph for, and the document claims {ClaimName(claim)}.") +
                    $" {Clause(claim, "6.2.11.8", "7.21.8")} does not allow a reference to the .notdef glyph." +
                    " Use a font that covers the character, or set Document.Conformance to None.");
            }

            i += char.IsHighSurrogate(c) ? 2 : 1;
        }
    }
}
