// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Conformance.Rules.Fonts;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.4.2 test 2 (PDCIDFont /CIDSet completeness). An embedded, subset-tagged
/// <c>CIDFontType2</c> whose <c>/FontDescriptor</c> carries a <c>/CIDSet</c> bitmap shall
/// identify exactly the CIDs present in the font program.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.21.4.2 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.4.2, testNumber 2). The PDF/UA-1 predicate is identical to the PDF/A-2 §6.2.11.4.2-2
/// predicate (<c>containsFontFile == false || fontName.search(/[A-Z]{6}\+/) != 0 ||
/// containsCIDSet == false || cidSetListsAllGlyphs == true</c>), so this rule reuses the same
/// subset-tag detection, <see cref="SfntMetrics"/> glyph-count reader, and CIDSet bitmap
/// verification as <c>FontStructureRule</c>.
/// <para>
/// Only subset-tagged CIDFontType2 fonts with an Identity <c>/CIDToGIDMap</c> (or none) are
/// checked. A non-Identity stream CIDToGIDMap makes the present-CID set depend on that map, so
/// the check is skipped there (deferred edge). A missing or unparseable program degrades to no
/// finding. Only fonts actually selected via a <c>Tf</c> operator are evaluated (usage-scoped,
/// matching veraPDF).
/// </para>
/// <para>
/// veraPDF probe: a subset CIDFontType2 with an incorrect /CIDSet (marking too few bits) →
/// veraPDF fires clause 7.21.4.2-2. The same font with a correct /CIDSet → veraPDF accepts.
/// Cross-validated using the DejaVu Sans TTF test asset that anchors the §6.2.11.4.2-2 fixtures.
/// </para>
/// </remarks>
internal sealed class UaCidSetRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.4.2-2";

    public string Clause => "ISO 14289-1:2014, 7.21.4.2";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");
    private static readonly PdfName _cidSet = new("CIDSet");

    public void Evaluate(PreflightContext context)
    {
        var reported = new HashSet<int>();

        foreach (var font in context.EnumerateUsedFonts())
        {
            if (context.Resolve(font.Get(PdfName.Subtype)) is not PdfName { Value: "Type0" })
                continue;

            if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants
                || descendants.Count == 0)
                continue;

            for (var i = 0; i < descendants.Count; i++)
            {
                var d = descendants[i];
                if (d is PdfIndirectReference r && !reported.Add(r.ObjectNumber))
                    continue; // already checked this CIDFont

                if (context.Resolve(d) is not PdfDictionary cidFont)
                    continue;

                CheckCidFont(context, cidFont);
            }
        }
    }

    private void CheckCidFont(PreflightContext context, PdfDictionary cidFont)
    {
        if (context.Resolve(cidFont.Get(PdfName.Subtype)) is not PdfName { Value: "CIDFontType2" })
            return;

        var baseFont = (context.Resolve(cidFont.Get(PdfName.BaseFont)) as PdfName)?.Value;
        if (baseFont is null || !IsSubsetTag(baseFont))
            return; // CIDSet-completeness constraint applies only to subset fonts.

        // A stream CIDToGIDMap makes the present-CID set depend on that map — deferred to avoid FP.
        if (context.Resolve(cidFont.Get(_cidToGidMap)) is { } map && map is not PdfName { Value: "Identity" })
            return;

        if (context.Resolve(cidFont.Get(_fontDescriptor)) is not PdfDictionary descriptor)
            return;

        // /CIDSet is optional; when absent there is nothing to check.
        if (context.ResolveStream(descriptor.Get(_cidSet)) is not { } cidSetStream)
            return;

        // Parse the embedded TrueType program to determine the glyph count.
        if (context.ResolveStream(descriptor.Get(_fontFile2)) is not { } ff2Stream
            || context.DecodeStream(ff2Stream) is not { } ff2Bytes)
            return; // not embedded — nothing to check (embedding rule is separate)

        if (SfntMetrics.TryParse(ff2Bytes) is not { } metrics)
            return; // unparseable program — degrade to no finding

        if (context.DecodeStream(cidSetStream) is not { } cidSet)
            return;

        if (!CidSetListsAllGlyphs(cidSet, metrics.NumGlyphs))
            context.Report(
                RuleId,
                Clause,
                PreflightSeverity.Error,
                "An embedded subset CIDFontType2's /CIDSet does not identify exactly the CIDs present in "
                + "the font program, which §7.21.4.2 requires.");
    }

    // True when every CID in 0..numGlyphs−1 has its bit set and every bit beyond is clear.
    private static bool CidSetListsAllGlyphs(byte[] cidSet, int numGlyphs)
    {
        var totalBits = cidSet.Length * 8;
        for (var i = 0; i < numGlyphs; i++)
            if (i >= totalBits || (cidSet[i / 8] & (0x80 >> (i % 8))) == 0)
                return false;
        for (var i = numGlyphs; i < totalBits; i++)
            if ((cidSet[i / 8] & (0x80 >> (i % 8))) != 0)
                return false;
        return true;
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
