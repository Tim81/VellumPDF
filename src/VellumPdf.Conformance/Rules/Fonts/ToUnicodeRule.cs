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
/// Authored from ISO 19005-2:2011, 6.2.11.7.2 and ISO 32000-1:2008, 9.10. Clean-room: derived from
/// the specification text, not from any third-party validation profile. This rule targets the
/// unambiguous Identity-encoded case; simple fonts whose Unicode values are derivable from a
/// standard encoding, and Type0 fonts using a predefined non-Identity CMap, are mappable without a
/// /ToUnicode entry and are validated in a later slice.
/// </remarks>
internal sealed class ToUnicodeRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.2.11.7-tounicode";

    public string Clause => "ISO 19005-2:2011, 6.2.11.7.2";

    private static readonly PdfName _toUnicode = new("ToUnicode");

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateFonts())
        {
            if ((font.Get(PdfName.Subtype) as PdfName)?.Value != "Type0")
                continue;

            var encoding = (context.Resolve(font.Get(PdfName.Encoding)) as PdfName)?.Value;
            if (encoding is not ("Identity-H" or "Identity-V"))
                continue;

            if (font.Get(_toUnicode) is null)
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
