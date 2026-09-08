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
/// validation profile. veraPDF was consulted only to confirm that the corrected clause is the one
/// it keys the same requirement to, which is oracle use and not a source.
/// <para>
/// This rule previously cited §6.3.4–§6.3.5. Those numbers are ISO 19005-<em>1</em> numbering, where
/// clause 6.3 is Fonts. In ISO 19005-2 clause 6.3 is Annotations, its four sub-clauses end at 6.3.4
/// "Display of annotation contents", and there is no 6.3.5 at all. Embedding is 6.2.11.4, with
/// 6.2.11.4.1 General and 6.2.11.4.2 Subset embedding.
/// </para>
/// <c>/Type3</c> fonts define their glyphs as content streams and so are embedded by construction.
/// <para>
/// §6.2.11.4.1 scopes the requirement to fonts "used for rendering", and its NOTE 2 confirms that
/// text rendering mode 3 neither strokes, fills nor clips — a font drawn only in that mode is not
/// rendered, and so falls outside the requirement's own scope. Usage is tallied per font across the
/// whole document, not per page: each content stream starts from its own initial graphics state, so
/// a page's <c>Tr</c> setting has no bearing on any other page, but the font object itself persists
/// across pages, and the same font can be shown invisibly on one page and visibly on another. A font
/// is exempt only when it has a confirmed mode-3 show and no show that is anything else — every
/// visible mode, mode 7 (clip-only, which is not rendered either but is not what the NOTE names),
/// and the -1 sentinel for an unparseable <c>Tr</c> operand, all defeat the exemption because none
/// of them is a confirmed mode 3.
/// </para>
/// <para>
/// §6.2.11.4.1's "glyph referenced from a content stream" justifies narrowing the check away from
/// everything in <c>/Resources /Font</c>. The <c>Tf</c> boundary itself — a font is checked once it
/// is selected, whether or not it is ever shown — came from matching veraPDF, which validates only
/// the current graphics state (issue #118), and is a stricter proxy for the clause's line than
/// "glyph referenced" alone would require. Narrowing further, to require an actual show, is a
/// separate change with no tracking issue open for it yet.
/// </para>
/// <para>
/// A form XObject, a Type 3 glyph procedure, and an annotation appearance stream are each a content
/// stream this scan does not read, so a font used only inside one of them — never selected by
/// <c>Tf</c> on the page itself — is still not checked here at all. But when a page's own scan finds
/// evidence that one of those streams exists — a drawn form XObject, an annotation carrying an
/// appearance stream, or a selected Type 3 font — the mode-3 exemption above is suppressed for the
/// whole document instead of applied on an incomplete picture: a visible show hiding in the
/// unscanned stream would otherwise wrongly exempt a font this scan can only see at mode 3.
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
    private static readonly PdfName _ap = new("AP");
    private static readonly PdfName _form = new("Form");
    private static readonly PdfName _type3 = new("Type3");

    public void Evaluate(PreflightContext context)
    {
        // PreflightContext.EnumerateUsedFonts can't carry a rendering mode, so the scan is
        // inlined here instead of extending that shared enumerator (nine other rules depend on
        // its signature). Usage is aggregated across every page before any exemption is decided —
        // see the class remarks for why a document-wide tally, not a per-page one, is correct.
        var fontsByKey = new Dictionary<object, PdfDictionary>();
        var keyOrder = new List<object>();
        var hasMode3Show = new HashSet<object>();
        var hasNonMode3Show = new HashSet<object>();

        // Set once any page's scan finds evidence it could be missing a text show (a form
        // XObject, a Type3 font, an annotation appearance, or content that failed to parse fully).
        // When set, the exemption below is not applied to any font on any page: a blind spot in
        // the scan must never turn into a false exemption, so the whole document falls back to
        // the pre-exemption behaviour of checking every used font.
        var exemptionUnsafe = false;

        foreach (var page in context.EnumeratePages())
        {
            var resources = context.ResolveInherited(page, PdfName.Resources) as PdfDictionary;
            var fontResources = resources is null
                ? null
                : context.Resolve(resources.Get(PdfName.Font)) as PdfDictionary;

            var usage = ContentStreamUsage.Analyze(context, page);

            // Asked of every page, including one that selects no font of its own. A form XObject
            // or an annotation appearance on such a page can still draw a font that some other
            // page shows only at mode 3, so skipping the page here would leave exactly the blind
            // spot this flag exists to close.
            if (!exemptionUnsafe && PageMayHideTextShow(context, page, resources, fontResources, usage))
                exemptionUnsafe = true;

            if (fontResources is null)
                continue;

            // Tally shows by resource name once per page, so the font loop below is one lookup
            // per font rather than a rescan of every show for every font.
            var modeThreeShows = new HashSet<string>(StringComparer.Ordinal);
            var nonModeThreeShows = new HashSet<string>(StringComparer.Ordinal);
            foreach (var show in usage.TextShows)
            {
                if (show.FontResourceName is not { } name)
                    continue;
                if (show.RenderingMode == 3)
                    modeThreeShows.Add(name);
                else
                    nonModeThreeShows.Add(name);
            }

            foreach (var entry in fontResources.Entries)
            {
                var resourceName = entry.Key.Value;
                if (!usage.UsedFonts.Contains(resourceName))
                    continue;
                if (context.Resolve(entry.Value) is not PdfDictionary font)
                    continue;

                // Key on the resolved font's object number when indirect, or on the dictionary
                // instance when direct — the same resource name can name a different font on a
                // different page, so the key has to follow the font, not the name.
                var key = entry.Value is PdfIndirectReference iref ? (object)iref.ObjectNumber : font;
                if (fontsByKey.TryAdd(key, font))
                    keyOrder.Add(key);

                if (modeThreeShows.Contains(resourceName))
                    hasMode3Show.Add(key);
                if (nonModeThreeShows.Contains(resourceName))
                    hasNonMode3Show.Add(key);
            }
        }

        foreach (var key in keyOrder)
        {
            // Exempt (NOTE 2) only when a confirmed mode-3 show exists and nothing else was shown
            // for this font. RenderingMode != 3 catches every visible mode and mode 7 (add to clip,
            // not rendered but also not what the NOTE names) alike, plus the -1 unparseable-Tr
            // sentinel — none of those is a confirmed 3, so none of them can grant the exemption.
            if (!exemptionUnsafe && hasMode3Show.Contains(key) && !hasNonMode3Show.Contains(key))
                continue;
            CheckFont(context, fontsByKey[key]);
        }
    }

    // True when this page has evidence the scan above could be missing a text show, so any
    // mode-3 tally it produced cannot be trusted to grant the exemption.
    private static bool PageMayHideTextShow(
        PreflightContext context,
        PdfDictionary page,
        PdfDictionary? resources,
        PdfDictionary? fontResources,
        ContentUsage usage)
    {
        if (usage.ContentIncomplete)
            return true;

        // A drawn form XObject's own content stream is not read by this scan; a visible show
        // inside it would wrongly exempt a font shown only at mode 3 on the page itself. Image
        // XObjects are excluded deliberately: they carry no text of their own, and treating them
        // the same way would defeat the exemption for the case it mainly exists to serve — a
        // scanned page with an invisible OCR text layer drawn over the page image.
        if (usage.DrawnXObjects.Count > 0
            && resources is not null
            && context.Resolve(resources.Get(PdfName.XObject)) is PdfDictionary xObjects)
        {
            foreach (var drawnName in usage.DrawnXObjects)
            {
                if (context.Resolve(xObjects.Get(new PdfName(drawnName))) is PdfDictionary xObject
                    && context.Resolve(xObject.Get(PdfName.Subtype)) is PdfName subtype
                    && subtype.Value == _form.Value)
                {
                    return true;
                }
            }
        }

        // An annotation's appearance stream is a content stream too, and is likewise unread here.
        if (context.Resolve(page.Get(PdfName.Annots)) is PdfArray annots)
        {
            for (var i = 0; i < annots.Count; i++)
            {
                if (context.Resolve(annots[i]) is PdfDictionary annot
                    && context.Resolve(annot.Get(_ap)) is PdfDictionary)
                {
                    return true;
                }
            }
        }

        // A Type3 font's glyph procedures are content streams that this scan never reads either.
        if (fontResources is null)
            return false;

        foreach (var entry in fontResources.Entries)
        {
            if (!usage.UsedFonts.Contains(entry.Key.Value))
                continue;
            if (context.Resolve(entry.Value) is PdfDictionary font
                && context.Resolve(font.Get(PdfName.Subtype)) is PdfName fontSubtype
                && fontSubtype.Value == _type3.Value)
            {
                return true;
            }
        }

        return false;
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
