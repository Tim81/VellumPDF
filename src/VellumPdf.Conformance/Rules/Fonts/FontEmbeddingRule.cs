// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.4.1 (Embedding, General). The font programs for all fonts used for
/// rendering shall be embedded: a simple font's <c>/FontDescriptor</c> must carry an embedded font
/// program (<c>/FontFile</c>, <c>/FontFile2</c>, or <c>/FontFile3</c>), and a composite
/// (<c>/Type0</c>) font's descendant CIDFont must likewise embed its program. The unembedded
/// Standard-14 fonts are therefore not valid in PDF/A.
/// </summary>
/// <remarks>
/// Re-derived from ISO 19005-2:2011, 6.2.11.4.1 and ISO 32000-1:2008, 9.9, against the standard's
/// own text (#418). Clean-room: derived from the specification text, not from any third-party
/// validation profile. In this re-derivation veraPDF was consulted only to confirm that the
/// corrected clause is the one it keys the same requirement to, which is oracle use and not a
/// source. That is not true of the rule as a whole: the <c>Tf</c>-only scope below was adopted for
/// parity with veraPDF under issue #118, and re-deriving it is part of #418 rather than done here.
/// <para>
/// The summary states the clause's obligation, and this rule is stricter than it in one direction. NOTE 2 exempts a
/// font drawn only in text rendering mode 3, and nothing here implements that, so a font used
/// invisibly is reported. An attempt on this branch was withdrawn after three reviewers found seven
/// ways it read absence of evidence as evidence of absence; the correction is specified in #458 and
/// recorded as row D4 of <c>docs/conformance-divergences.md</c>.
/// </para>
/// <para>
/// This rule previously cited §6.3.4–§6.3.5. Those numbers are ISO 19005-<em>1</em> numbering, where
/// clause 6.3 is Fonts. In ISO 19005-2 clause 6.3 is Annotations, its four sub-clauses end at 6.3.4
/// "Display of annotation contents", and there is no 6.3.5 at all. Embedding is 6.2.11.4, with
/// 6.2.11.4.1 General and 6.2.11.4.2 Subset embedding.
/// </para>
/// <para><c>/Type3</c> fonts define their glyphs as content streams and so are embedded by
/// construction.</para>
/// <para>
/// Only fonts that a page actually selects via a <c>Tf</c> operator in its content stream are
/// validated (matching veraPDF, which validates only the current graphics state — issue #118).
/// Fonts present in <c>/Resources /Font</c> but never selected are not checked. Fonts used only
/// within form XObjects, Type 3 glyph procedures, or annotation appearance streams are not detected
/// here at all, which is tracked as issue #450.
/// </para>
/// </remarks>
internal sealed class FontEmbeddingRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.4.1-font-embedding";

    public string Clause => "ISO 19005-2:2011, 6.2.11.4.1";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile = new("FontFile");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _fontFile3 = new("FontFile3");

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
            CheckFont(context, font);
    }

    private void CheckFont(PreflightContext context, PdfDictionary font)
    {
        var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;

        // Type3 glyphs are content streams — embedded by construction.
        if (subtype == "Type3")
            return;

        PdfDictionary? descriptor;
        string? programSubtype;
        if (subtype == "Type0")
        {
            // The embedded program lives on the descendant CIDFont's descriptor, and the expected
            // font-program key depends on the CIDFont subtype.
            if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants
                || descendants.Count == 0
                || context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            {
                Report(context, font);
                return;
            }
            programSubtype = (context.Resolve(cidFont.Get(PdfName.Subtype)) as PdfName)?.Value;
            descriptor = context.Resolve(cidFont.Get(_fontDescriptor)) as PdfDictionary;
        }
        else
        {
            programSubtype = subtype;
            descriptor = context.Resolve(font.Get(_fontDescriptor)) as PdfDictionary;
        }

        if (descriptor is null || !HasEmbeddedProgram(context, descriptor, programSubtype))
            Report(context, font);
    }

    // The embedded program must be carried in the key appropriate to the font type
    // (ISO 32000-1 Table 126): Type1 -> FontFile or FontFile3 (CFF); TrueType / CIDFontType2 ->
    // FontFile2 or FontFile3 (OpenType); CIDFontType0 -> FontFile3. Each candidate is resolved to an
    // actual stream, so a /FontFile null or a dangling reference does not count as embedded.
    private bool HasEmbeddedProgram(PreflightContext context, PdfDictionary descriptor, string? subtype)
    {
        var hasFontFile = context.ResolveStream(descriptor.Get(_fontFile)) is not null;
        var hasFontFile2 = context.ResolveStream(descriptor.Get(_fontFile2)) is not null;
        var hasFontFile3 = context.ResolveStream(descriptor.Get(_fontFile3)) is not null;

        return subtype switch
        {
            "Type1" or "MMType1" => hasFontFile || hasFontFile3,
            "TrueType" or "CIDFontType2" => hasFontFile2 || hasFontFile3,
            "CIDFontType0" => hasFontFile3,
            // Unknown subtype: accept any embedded program rather than risk a false positive.
            _ => hasFontFile || hasFontFile2 || hasFontFile3,
        };
    }

    private void Report(PreflightContext context, PdfDictionary font)
    {
        var name = (font.Get(PdfName.BaseFont) as PdfName)?.Value;
        var which = name is null ? "A font" : $"The font /{name}";
        context.Report(
            RuleId,
            Clause,
            PreflightSeverity.Error,
            $"{which} is not embedded; PDF/A requires every font to embed its font program.");
    }
}
