// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.8 testNumber 1 (PDF/UA-1: no .notdef glyph reference). A PDF/UA-1 document
/// shall not reference the .notdef glyph in any shown text. Applies regardless of text rendering
/// mode — even invisible text (Tr 3) must not reference .notdef.
/// </summary>
/// <remarks>
/// The veraPDF predicate (object Glyph, clause 7.21.8, testNumber 1) is:
/// <c>name != ".notdef"</c>. No rendering-mode exemption exists for this clause.
/// <para>
/// Scope: only composite (Type0) fonts with /Encoding = /Identity-H or /Identity-V, a descendant
/// CIDFontType2, and /CIDToGIDMap absent or /Identity. For such a font the shown bytes are 2-byte
/// big-endian codes, and code == CID == GID. Glyph index 0 is .notdef by TrueType convention.
/// </para>
/// <para>
/// All other font forms (simple 1-byte fonts, non-Identity encodings, stream /CIDToGIDMap) are
/// deferred — code-to-glyph resolution requires encoding/cmap tables we do not have; guessing
/// would risk false positives.
/// </para>
/// <para>
/// Scope is usage-based: only fonts whose resource name appears in a Tf operator on the page are
/// evaluated. A font present in /Resources/Font but never selected is not checked. Text show events
/// from ALL rendering modes are checked (no Tr exemption).
/// </para>
/// <para>
/// Cross-validated against veraPDF 1.30.2:
/// (a) draws glyph 0x0000 with Identity-H CIDFontType2 → veraPDF fires 7.21.8-1 (exit 1);
/// (b) draws normal glyphs (no 0x0000) → veraPDF does NOT fire 7.21.8-1 (exit 0).
/// </para>
/// </remarks>
internal sealed class UaNotdefGlyphRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.8-1";

    public string Clause => "ISO 14289-1:2014, 7.21.8";

    private static readonly PdfName _descendantFonts = new("DescendantFonts");
    private static readonly PdfName _encoding = new("Encoding");
    private static readonly PdfName _cidToGidMap = new("CIDToGIDMap");

    public void Evaluate(PreflightContext context)
    {
        var reported = false; // fire at most once per document

        foreach (var page in context.EnumeratePages())
        {
            if (reported) break;

            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fontResources)
                continue;

            var usage = ContentStreamUsage.Analyze(context, page);
            if (usage.TextShows.Count == 0)
                continue;

            // Build set of in-scope resource names (Identity-H/V CIDFontType2-Identity fonts).
            var inScopeNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in fontResources.Entries)
            {
                if (!usage.UsedFonts.Contains(entry.Key.Value))
                    continue;
                if (IsInScopeFont(context, entry.Value))
                    inScopeNames.Add(entry.Key.Value);
            }
            if (inScopeNames.Count == 0)
                continue;

            // Check every TextShow for this page whose font resource is in-scope.
            foreach (var show in usage.TextShows)
            {
                if (show.FontResourceName is null || !inScopeNames.Contains(show.FontResourceName))
                    continue;

                // Split bytes as 2-byte big-endian codes; code 0x0000 == .notdef.
                var bytes = show.Bytes;
                for (var i = 0; i + 1 < bytes.Length; i += 2)
                {
                    var code = (bytes[i] << 8) | bytes[i + 1];
                    if (code == 0)
                    {
                        context.Report(
                            RuleId,
                            Clause,
                            PreflightSeverity.Error,
                            "The document references the .notdef glyph (code 0x0000) in a composite font "
                            + "with Identity encoding. PDF/UA-1 §7.21.8 prohibits referencing .notdef in "
                            + "any shown text regardless of text rendering mode.");
                        reported = true;
                        break;
                    }
                }
                if (reported) break;
            }
        }
    }

    // Returns true when fontRef resolves to a Type0 font with:
    //   /Encoding = /Identity-H or /Identity-V (a name, not a stream)
    //   descendant is CIDFontType2
    //   /CIDToGIDMap is absent or the name /Identity
    private static bool IsInScopeFont(PreflightContext context, PdfObject? fontRef)
    {
        if (context.Resolve(fontRef) is not PdfDictionary font)
            return false;
        if ((context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value != "Type0")
            return false;
        if ((context.Resolve(font.Get(_encoding)) as PdfName)?.Value is not ("Identity-H" or "Identity-V"))
            return false;
        if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants || descendants.Count == 0)
            return false;
        if (context.Resolve(descendants[0]) is not PdfDictionary cidFont)
            return false;
        if ((context.Resolve(cidFont.Get(PdfName.Subtype)) as PdfName)?.Value != "CIDFontType2")
            return false;

        // /CIDToGIDMap must be absent or the name /Identity.
        // A stream /CIDToGIDMap defines an arbitrary mapping — defer (can't guarantee code==GID).
        var cidToGidMapObj = context.Resolve(cidFont.Get(_cidToGidMap));
        if (cidToGidMapObj is not null
            && (cidToGidMapObj is not PdfName cidToGidMapName
                || cidToGidMapName.Value != "Identity"))
            return false;

        return true;
    }
}
