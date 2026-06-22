// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.6, testNumber 3 (Symbolic TrueType encoding). A symbolic simple TrueType font
/// (i.e. its <c>/FontDescriptor /Flags</c> bit 3 is set, corresponding to the Symbolic flag value 4)
/// shall <em>not</em> carry an <c>/Encoding</c> entry in its font dictionary.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.6 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.6, testNumber 3). Clean-room: derived from the specification text and the veraPDF
/// validation profile (<c>PDTrueTypeFont</c> predicate:
/// <c>isSymbolic == false || Encoding == null</c>), not from any third-party implementation.
/// <para>
/// The rule fires only when the font's <c>/Subtype</c> is <c>TrueType</c> AND the font has a
/// <c>/FontDescriptor</c> AND the Symbolic bit (bit 3, value 4) is set in the descriptor's
/// <c>/Flags</c> AND an <c>/Encoding</c> entry is present in the font dictionary. A font without
/// a <c>/FontDescriptor</c>, or whose descriptor's <c>/Flags</c> has the NonSymbolic bit set (bit 6,
/// value 32), is not flagged here — the non-symbolic encoding rules are testNumbers 2 and 4.
/// </para>
/// <para>
/// Only fonts that a page actually selects via a <c>Tf</c> operator in its content stream are
/// validated (matching veraPDF, which validates only the current graphics state — issue #118).
/// Fonts present in <c>/Resources /Font</c> but never selected are not checked.
/// </para>
/// </remarks>
internal sealed class UaSymbolicFontRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.6-3";

    public string Clause => "ISO 14289-1:2014, 7.21.6";

    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _flags = new("Flags");
    private static readonly PdfName _encoding = new("Encoding");

    // ISO 32000-1 Table 121 bit 3 (1-indexed) = bit value 4 = Symbolic; bit 6 = value 32 = Nonsymbolic.
    private const int SymbolicFlag = 1 << 2;
    private const int NonSymbolicFlag = 1 << 5;

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
        {
            if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "TrueType")
                continue;

            // Symbolic status is derived from /FontDescriptor /Flags bit 3.
            // When there is no /FontDescriptor (or no /Flags) we cannot determine symbolic status —
            // skip rather than risk a false positive.
            if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
                continue;
            if (context.Resolve(descriptor.Get(_flags)) is not PdfInteger flagsObj)
                continue;

            // Treat the font as symbolic only when the Symbolic bit is set AND the Nonsymbolic bit is
            // clear. This is the false-positive-safe reading: a (malformed) font that sets BOTH flags is
            // NOT treated as symbolic here, so we never fire where veraPDF — which may classify such a
            // font as non-symbolic — would accept it. Real symbolic fonts set only the Symbolic bit, so
            // no genuine coverage is lost.
            var isSymbolic = (flagsObj.Value & SymbolicFlag) != 0 && (flagsObj.Value & NonSymbolicFlag) == 0;
            if (!isSymbolic)
                continue; // non-symbolic (or ambiguous) fonts are handled by testNumbers 2 and 4

            // Symbolic TrueType font: there must be NO /Encoding entry in the font dictionary.
            if (font.Get(_encoding) is not null)
            {
                var name = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
                var which = name is null ? "A symbolic TrueType font" : $"The symbolic TrueType font /{name}";
                context.Report(
                    RuleId,
                    Clause,
                    PreflightSeverity.Error,
                    $"{which} carries an /Encoding entry. PDF/UA-1 §7.21.6 requires symbolic "
                    + "TrueType fonts to have no /Encoding entry in their font dictionary.");
            }
        }
    }
}
