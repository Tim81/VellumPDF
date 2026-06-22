// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.21.4.1 testNumber 1 (PDF/UA-1 font embedding). A simple font (Type1,
/// TrueType, or MMType1) that is not embedded must NOT be rendered in a visible text rendering
/// mode. Specifically the rule fires when a simple font has no embedded font program AND there is
/// at least one positively-observed text-show event for it with a text rendering mode other than 3
/// (invisible text).
/// </summary>
/// <remarks>
/// The veraPDF predicate (object PDFont, clause 7.21.4.1, testNumber 1) is:
/// <c>Subtype == "Type3" || Subtype == "Type0" || renderingMode == 3 || containsFontFile == true</c>.
/// Translated: a font is compliant when it is Type3 (embedded by construction), Type0 (composite,
/// checked separately), drawn only with rendering mode 3 (invisible text), or has an embedded font
/// program. This rule fires when NONE of those exemptions hold AND a positively-observed visible draw
/// is present.
/// <para>
/// False-positive safety: the rule fires only when ALL of:
/// <list type="bullet">
///   <item><description>the font /Subtype is Type1, TrueType, or MMType1 (not Type0, not Type3);</description></item>
///   <item><description>the font has no embedded font program (FontFile / FontFile2 / FontFile3);</description></item>
///   <item><description>at least one TextShow whose font resource name resolves to this font has a
///     positively-determined rendering mode other than 3 (i.e. RenderingMode is in 0..8 excluding 3 —
///     never –1, the "unknown" sentinel).</description></item>
/// </list>
/// When the rendering mode could not be determined (RenderingMode == -1, parse gap), that show event
/// is treated as "unknown" — NOT as a confirmed visible draw — so an unknown mode does not trigger
/// the rule. This is deliberately conservative (under-detection preferred over false positive).
/// </para>
/// <para>
/// Only fonts whose resource key appears in a <c>Tf</c> operator in a page content stream are
/// evaluated (usage-scoped, matching veraPDF). Fonts present in <c>/Resources /Font</c> but never
/// selected are not checked. Type0 and Type3 fonts are skipped unconditionally.
/// </para>
/// <para>
/// Authored from ISO 14289-1:2014, 7.21.4.1 and cross-validated against veraPDF 1.30.2 (clause
/// 7.21.4.1, testNumber 1). The visible/invisible probe results:
/// (a) non-embedded Helvetica drawn visibly → veraPDF fires 7.21.4.1-1 (exit 1);
/// (b) non-embedded Helvetica drawn only with 3 Tr → veraPDF does NOT fire 7.21.4.1-1 (3 Tr exemption);
/// (c) embedded TrueType drawn visibly → veraPDF does not fire 7.21.4.1-1.
/// </para>
/// </remarks>
internal sealed class UaFontEmbeddingRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.21.4.1-1";

    public string Clause => "ISO 14289-1:2014, 7.21.4.1";

    private static readonly PdfName _fontDescriptor = new("FontDescriptor");
    private static readonly PdfName _fontFile = new("FontFile");
    private static readonly PdfName _fontFile2 = new("FontFile2");
    private static readonly PdfName _fontFile3 = new("FontFile3");
    private static readonly PdfName _descendantFonts = new("DescendantFonts");

    public void Evaluate(PreflightContext context)
    {
        // Build a map: page → TextShows, so we can look up shows per page once.
        // Also track seen font objects (by resolution identity) to avoid duplicate findings.
        var reportedFonts = new HashSet<int>(); // object numbers already reported

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(PdfName.Font)) is not PdfDictionary fontResources)
                continue;

            var usage = ContentStreamUsage.Analyze(context, page);
            var textShows = usage.TextShows;
            if (textShows.Count == 0)
                continue;

            // For each font resource actually used on this page (Tf-selected), check it.
            foreach (var entry in fontResources.Entries)
            {
                var resourceName = entry.Key.Value;

                // Only evaluate fonts that were actually selected by a Tf on this page.
                if (!usage.UsedFonts.Contains(resourceName))
                    continue;

                if (context.Resolve(entry.Value) is not PdfDictionary font)
                    continue;

                var subtype = (context.Resolve(font.Get(PdfName.Subtype)) as PdfName)?.Value;

                // Type3 glyphs are content streams — embedded by construction; skip.
                // Type0 (composite) fonts are exempt from this rule (separate rule).
                if (subtype is "Type3" or "Type0")
                    continue;

                // Only simple fonts: Type1, TrueType, MMType1.
                if (subtype is not ("Type1" or "TrueType" or "MMType1"))
                    continue;

                var objectNumber = entry.Value is PdfIndirectReference iref ? iref.ObjectNumber : -1;

                // If this font object was already reported (on an earlier page), skip — but only AFTER
                // confirming a violation below, so that a font drawn invisibly on one page and visibly
                // on another is still caught on the page where it is visible (the dedup must not mark a
                // font seen until it actually fires).
                if (objectNumber >= 0 && reportedFonts.Contains(objectNumber))
                    continue;

                // Check whether this font has an embedded program.
                if (HasEmbeddedProgram(context, font, subtype))
                    continue; // embedded — compliant by construction

                // Non-embedded simple font: look for a positively-observed visible draw.
                // A visible draw is a TextShow for this resource name with RenderingMode in 0..8
                // excluding 3, AND never –1 (unknown).
                var hasVisibleDraw = false;
                foreach (var show in textShows)
                {
                    if (show.FontResourceName != resourceName)
                        continue;
                    // RenderingMode -1 = unknown (parse gap) — treat as NOT a confirmed visible draw.
                    // RenderingMode 3 = invisible text — exempt.
                    // Any other determined mode (0–2, 4–8) is visible.
                    if (show.RenderingMode >= 0 && show.RenderingMode != 3)
                    {
                        hasVisibleDraw = true;
                        break;
                    }
                }

                if (!hasVisibleDraw)
                    continue; // no confirmed visible draw — don't fire (FP-safe)

                // Confirmed violation: mark the font reported (dedup across pages) and emit once.
                if (objectNumber >= 0)
                    reportedFonts.Add(objectNumber);
                Report(context, font);
            }
        }
    }

    // Mirrors FontEmbeddingRule.HasEmbeddedProgram: checks FontFile / FontFile2 / FontFile3 by subtype.
    private static bool HasEmbeddedProgram(PreflightContext context, PdfDictionary font, string? subtype)
    {
        PdfDictionary? descriptor;
        string? programSubtype;

        if (subtype == "Type0")
        {
            // Handled by the caller (skipped before reaching this method).
            if (context.Resolve(font.Get(_descendantFonts)) is not PdfArray descendants
                || descendants.Count == 0
                || context.Resolve(descendants[0]) is not PdfDictionary cidFont)
                return false;
            programSubtype = (context.Resolve(cidFont.Get(PdfName.Subtype)) as PdfName)?.Value;
            descriptor = context.Resolve(cidFont.Get(_fontDescriptor)) as PdfDictionary;
        }
        else
        {
            programSubtype = subtype;
            descriptor = context.Resolve(font.Get(_fontDescriptor)) as PdfDictionary;
        }

        if (descriptor is null) return false;

        var hasFontFile = context.ResolveStream(descriptor.Get(_fontFile)) is not null;
        var hasFontFile2 = context.ResolveStream(descriptor.Get(_fontFile2)) is not null;
        var hasFontFile3 = context.ResolveStream(descriptor.Get(_fontFile3)) is not null;

        return programSubtype switch
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
        var name = (context.Resolve(font.Get(PdfName.BaseFont)) as PdfName)?.Value;
        var which = name is null ? "A font" : $"The font /{name}";
        context.Report(
            RuleId,
            Clause,
            PreflightSeverity.Error,
            $"{which} is a non-embedded simple font drawn with a visible text rendering mode. "
            + "PDF/UA-1 §7.21.4.1 requires a simple font either to embed its font program "
            + "or to be used only with text rendering mode 3 (invisible text).");
    }
}
