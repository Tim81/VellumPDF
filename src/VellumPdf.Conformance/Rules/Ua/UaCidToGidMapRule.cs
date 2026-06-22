// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.3.2 (CIDFontType2 CIDToGIDMap). Every embedded <c>CIDFontType2</c> font in a
/// PDF/UA-1 document shall carry a <c>/CIDToGIDMap</c> entry whose value is either the name
/// <c>/Identity</c> or a stream that maps CIDs to glyph indices.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.3.2 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.3.2, testNumber 1). Clean-room: derived from the specification text and the veraPDF
/// validation profile (<c>PDCIDFont</c> predicate:
/// <c>Subtype != "CIDFontType2" || CIDToGIDMap != null || containsFontFile == false</c>), not from
/// any third-party implementation.
/// <para>
/// The rule fires only when: (a) the font's <c>/Subtype</c> is <c>CIDFontType2</c>, AND (b) the
/// descendant CIDFont has an embedded font program (i.e. <c>containsFontFile == true</c>), AND
/// (c) the <c>/CIDToGIDMap</c> entry is absent. A CIDFontType2 with no embedded program is not
/// constrained (the embedding requirement is separate — §7.21.4.1). A non-<c>CIDFontType2</c>
/// descendant (e.g. <c>CIDFontType0</c>) is also exempt.
/// </para>
/// <para>
/// Only fonts that a page actually selects via a <c>Tf</c> operator in its content stream are
/// validated (matching veraPDF, which validates only the current graphics state — issue #118).
/// Fonts present in <c>/Resources /Font</c> but never selected are not checked.
/// </para>
/// </remarks>
internal sealed class UaCidToGidMapRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.3.2-1";

    public string Clause => "ISO 14289-1:2014, 7.21.3.2";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _fontFile3 = new("FontFile3");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
        {
            if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "Type0")
                continue;

            if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants
                || descendants.Count == 0
                || context.Resolve(descendants[0]) is not PdfDictionary cidFont)
                continue;

            if ((context.Resolve(cidFont.Get(PdfName.Subtype)) as PdfName)?.Value != "CIDFontType2")
                continue;

            // Only embedded CIDFontType2 fonts are required to carry /CIDToGIDMap (veraPDF predicate:
            // containsFontFile == false → exempt). Check for either FontFile2 or FontFile3 presence.
            if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
                continue;
            if (context.ResolveStream(descriptor.Get(_fontFile2)) is null
                && context.ResolveStream(descriptor.Get(_fontFile3)) is null)
                continue; // not embedded, exempt

            // The /CIDToGIDMap entry must be present (any value — Identity name or stream both satisfy).
            if (cidFont.Get(_cidToGidMap) is null)
            {
                var name = (cidFont.Get(PdfName.BaseFont) as PdfName)?.Value
                    ?? (font.Get(PdfName.BaseFont) as PdfName)?.Value;
                var which = name is null ? "A CIDFontType2 font" : $"The CIDFontType2 font /{name}";
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"{which} is missing the required /CIDToGIDMap entry. PDF/UA-1 §7.21.3.2 "
                    + "requires every embedded CIDFontType2 to carry a /CIDToGIDMap (name /Identity "
                    + "or a stream mapping CIDs to glyph indices).");
            }
        }
    }
}
