// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Fonts;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.4.2 test 1 (PDType1Font /CharSet completeness). An embedded, subset-tagged
/// simple Type 1 font whose <c>/FontDescriptor</c> carries a <c>/CharSet</c> string shall list
/// every glyph present in the font program (excluding <c>.notdef</c>).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.4.2 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.4.2, testNumber 1). The PDF/UA-1 predicate is identical to the PDF/A-2 §6.2.11.4.2-1
/// predicate (<c>containsFontFile == false || fontName.search(/[A-Z]{6}\+/) != 0 ||
/// CharSet == null || charSetListsAllGlyphs == true</c>), so this rule reuses the same
/// subset-tag detection, CharSet parsing, Type 1 program parser (<see cref="Type1Glyphs"/>), and
/// usage-scoping pattern as <c>FontStructureRule</c>.
/// <para>
/// Only subset-tagged fonts (a <c>XXXXXX+Name</c> BaseFont) with an embedded <c>/FontFile</c>
/// that carry a <c>/CharSet</c> are checked. A missing or parse-failing program degrades to no
/// finding (under-detection preferred over false positive). Only fonts actually selected via
/// a <c>Tf</c> operator are evaluated (usage-scoped, matching veraPDF).
/// </para>
/// <para>
/// veraPDF probe: a subset Type 1 font with an <em>incomplete</em> CharSet (omitting one present
/// glyph) → veraPDF fires clause 7.21.4.2-1. The same font with a complete CharSet → veraPDF
/// accepts (clause 7.21.4.2-1 absent). Cross-validated using the Noto Sans Shavian PFB test
/// asset that anchors the equivalent PDF/A-2 §6.2.11.4.2-1 oracle fixtures.
/// </para>
/// </remarks>
internal sealed class UaType1CharSetRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.4.2-1";

    public string Clause => "ISO 14289-1:2014, 7.21.4.2";

    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _charSet = new("CharSet");
    private static readonly PdfName _fontFile = new("FontFile");
    private static readonly PdfName _length1 = new("Length1");

    public void Evaluate(PreflightContext context)
    {
        foreach (var font in context.EnumerateUsedFonts())
        {
            var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;
            if (subtype is not ("Type1" or "MMType1"))
                continue;

            var baseFont = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
            if (baseFont is null || !IsSubsetTag(baseFont))
                continue; // CharSet-completeness constraint applies only to subset fonts.

            if (context.Resolve(font.Get(_fontDescriptor)) is not PdfDictionary descriptor)
                continue;

            if (CharSetNames(context, descriptor) is not { } declared)
                continue; // /CharSet is optional — no CharSet, no check.

            if (context.ResolveStream(descriptor.Get(_fontFile)) is not { } fontFile
                || context.DecodeStream(fontFile) is not { } programBytes)
                continue; // only an embedded Type 1 program (/FontFile) is constrained here.

            var length1 = context.Resolve(fontFile.Dictionary.Get(_length1)) is PdfInteger l ? (int)l.Value : -1;
            if (Type1Glyphs.TryEnumerate(programBytes, length1) is not { Count: > 0 } programGlyphs)
                continue; // an unparseable program degrades to no finding.

            foreach (var glyph in programGlyphs)
            {
                if (glyph != ".notdef" && !declared.Contains(glyph))
                {
                    context.Report(
                        RuleId,
                        Clause,
                        PreflightSeverity.Error,
                        "An embedded subset Type 1 font's /CharSet does not list every glyph present in the "
                        + $"font program (e.g. /{glyph} is missing), which §7.21.4.2 requires.");
                    break; // one finding per font
                }
            }
        }
    }

    // Parses a FontDescriptor /CharSet string ("/name/name…") into its set of glyph names, or null
    // when no /CharSet is present.
    private static HashSet<string>? CharSetNames(PreflightContext context, PdfDictionary descriptor)
    {
        var raw = context.Resolve(descriptor.Get(_charSet)) switch
        {
            PdfLiteralString s => s.Bytes,
            PdfHexString h => h.Bytes,
            _ => (ReadOnlyMemory<byte>?)null,
        };
        if (raw is not { } bytes)
            return null;
        var text = System.Text.Encoding.Latin1.GetString(bytes.Span);
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var part in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
            set.Add(part.Trim());
        return set;
    }

    // A subset font name is "ABCDEF+ActualName": six uppercase letters then '+'.
    private static bool IsSubsetTag(string baseFont)
    {
        if (baseFont.Length < 7 || baseFont[6] != '+')
            return false;
        for (var i = 0; i < 6; i++)
            if (baseFont[i] is < 'A' or > 'Z')
                return false;
        return true;
    }
}
