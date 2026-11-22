// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Fonts;

/// <summary>
/// ISO 19005-2 §6.2.11.7.2 (PDF/A-2u — character-to-Unicode mapping). A composite (<c>/Type0</c>)
/// font that uses the <c>Identity-H</c> or <c>Identity-V</c> encoding maps character codes to
/// font-specific CIDs with no inherent Unicode meaning, so it shall provide a <c>/ToUnicode</c>
/// CMap to make its text extractable as Unicode.
/// </summary>
/// <remarks>
/// Cites ISO 19005-2:2011, 6.2.11.7.2 and ISO 32000-1:2008, 9.10. Its behaviour was checked against
/// veraPDF's PDF/A-2 profile, and ISO 19005-2 was not held when it was written. The 2026-11-21
/// audit against the clause text (#418) found it narrower: 6.2.11.7.2 binds every font outside four
/// exempt categories, and this rule targets only Type 0 fonts using <c>Identity-H</c> or
/// <c>Identity-V</c>. A Type 3 font with glyph names that are not in the Adobe Glyph List and no
/// <c>/ToUnicode</c> passes here and fails veraPDF under PDF/A-2u. The Unicode values and the
/// completeness of the mapping are not checked either. Simple fonts whose Unicode values are
/// derivable from a standard encoding, and Type 0 fonts using a predefined non-Identity CMap, are
/// mappable without a <c>/ToUnicode</c> entry and are not checked here.
/// <para>
/// Only fonts that a page actually selects via a <c>Tf</c> operator in its content stream are
/// validated (matching veraPDF, which validates only the current graphics state — issue #118).
/// Fonts present in <c>/Resources /Font</c> but never selected are not checked. Fonts used only
/// within form XObjects, Type 3 glyph procedures, or annotation appearance streams are not detected
/// here at all, which is tracked as issue #450.
/// </para>
/// </remarks>
internal sealed class ToUnicodeRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.7.2-tounicode";

    public string Clause => "ISO 19005-2:2011, 6.2.11.7.2";

    private static readonly PdfName _toUnicode = new("ToUnicode");

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
        {
            if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "Type0")
                continue;

            var encoding = (context.Resolve(font.Get(PdfName.Encoding)) as PdfName)?.Value;
            if (encoding is not ("Identity-H" or "Identity-V"))
                continue;

            // A valid /ToUnicode is a CMap *stream*; a missing, null, non-stream, or predefined
            // /Identity (a name, not a stream) value provides no real mapping and is rejected.
            if (context.ResolveStream(font.Get(_toUnicode)) is null)
            {
                var name = (font.Get(PdfName.BaseFont) as PdfName)?.Value;
                var which = name is null ? "A Type0 font" : $"The Type0 font /{name}";
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"{which} uses Identity encoding and shall provide a /ToUnicode CMap "
                    + "for PDF/A-2u conformance.");
            }
        }
    }
}
