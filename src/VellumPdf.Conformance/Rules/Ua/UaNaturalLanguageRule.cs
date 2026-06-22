// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.2 natural-language determination rules (B6 batch):
/// <list type="bullet">
///   <item>7.2-21 (PDStructElem): a StructElem with <c>/ActualText</c> must have a determinable language.</item>
///   <item>7.2-22 (PDStructElem): a StructElem with <c>/Alt</c> must have a determinable language.</item>
///   <item>7.2-23 (PDStructElem): a StructElem with <c>/E</c> must have a determinable language.</item>
///   <item>7.2-24 (PDAnnot): an annotation with a non-empty <c>/Contents</c> must have a
///         determinable language (via its direct enclosing struct element's <c>/Lang</c> or the
///         catalog <c>/Lang</c>).</item>
///   <item>7.2-25 (PDFormField): a form field with a <c>/TU</c> entry must have a determinable
///         language (via its associated struct element's <c>/Lang</c> or the catalog <c>/Lang</c>).</item>
/// </list>
/// </summary>
/// <remarks>
/// <para><strong>gContainsCatalogLang short-circuit (the dominant path).</strong>
/// ISO 14289-1 §7.2 already requires a non-empty catalog <c>/Lang</c> (UaLangRule, 7.2-lang).
/// Every conformant PDF/UA-1 file therefore has a catalog <c>/Lang</c>, which satisfies ALL five
/// rules unconditionally (<c>gContainsCatalogLang == true</c>). These rules can only fire in a
/// document that already fails 7.2-lang, making them low-FP-risk. When the catalog carries a
/// non-empty <c>/Lang</c>, this rule emits nothing and returns immediately.</para>
///
/// <para><strong>Clause semantics confirmed by veraPDF 1.30.2 probing:</strong></para>
/// <list type="bullet">
///   <item>7.2-21/22/23: fires when the StructElem has the attribute (<c>/ActualText</c>/
///     <c>/Alt</c>/<c>/E</c>) AND neither the element nor any ancestor has <c>/Lang</c>.
///     An empty <c>/Lang ()</c> value counts as <c>containsLang = true</c> (veraPDF does not
///     require a non-empty tag, just the key's presence).
///     A struct element with NO <c>/Alt</c> (etc.) does not trigger 7.2-22 even without
///     <c>/Lang</c> — the attribute must be present to activate the check.</item>
///   <item>7.2-24: the annotation's direct enclosing struct element (reached via the annotation's
///     <c>/StructParent</c> → the StructTreeRoot's <c>/ParentTree</c>) must carry <c>/Lang</c>.
///     An annotation-dict-level <c>/Lang</c> does NOT satisfy the predicate (confirmed empirically).
///     Ancestor struct-element <c>/Lang</c> values do NOT satisfy (only the direct struct parent
///     counts — confirmed empirically). If the annotation has no <c>/StructParent</c> or the
///     ParentTree lookup fails, the rule fires (FP-safe: null → no determinable language).</item>
///   <item>7.2-25: the Widget annotation that carries the form field's <c>/TU</c> must have a
///     <c>/StructParent</c> whose enclosing struct element has <c>/Lang</c>. Field-dict-level
///     <c>/Lang</c> does NOT satisfy (confirmed empirically). Ancestor struct-element <c>/Lang</c>
///     does NOT satisfy either. The check uses the same StructParentOf lookup as 7.2-24.</item>
/// </list>
///
/// <para><strong>Empty ActualText/Alt/E:</strong> an empty string value (<c>()</c>) still
/// activates the check (the attribute is present). veraPDF 1.30.2 fires 7.2-21 for
/// <c>/ActualText ()</c> with no lang; consistent behaviour confirmed for <c>/Alt ()</c>
/// (which is the veraPDF 7.3-1 sense) and <c>/E ()</c>.</para>
///
/// <para><strong>Scope for 7.2-25:</strong> form fields that are pure non-Widget form fields
/// (not backed by an annotation) are reachable only through the AcroForm tree, but veraPDF's
/// <c>containsLang</c> for PDFormField appears to require the struct-element link provided by
/// the Widget annotation's <c>/StructParent</c>. Without a struct parent binding the rule cannot
/// be satisfied anyway, so the check is applied consistently: fire unless the widget's struct
/// element has <c>/Lang</c>.</para>
///
/// <para><strong>7.2-33 (XMPLangAlt):</strong> deferred — requires parsing the XMP packet to
/// locate dc:title lang-alt arrays and checking for an x-default entry. The rule only fires
/// when the catalog also lacks <c>/Lang</c> (i.e. <c>gContainsCatalogLang = false</c>), so it
/// is already covered for conformant documents. See ConformanceCatalog Deferred note.</para>
///
/// <para>Clean-room: derived from ISO 14289-1:2014 §7.2 and empirically validated against
/// veraPDF 1.30.2. Not derived from any third-party implementation.</para>
/// </remarks>
internal sealed class UaNaturalLanguageRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.2-21-22-23-24-25";

    public string Clause => "ISO 14289-1:2014, 7.2";

    private static readonly PdfName _lang = new("Lang");
    private static readonly PdfName _alt = new("Alt");
    private static readonly PdfName _actualText = new("ActualText");
    private static readonly PdfName _e = new("E");
    private static readonly PdfName _contents = new("Contents");
    private static readonly PdfName _tu = new("TU");
    private static readonly PdfName _structParent = new("StructParent");
    private static readonly PdfName _acroForm = new("AcroForm");
    private static readonly PdfName _fields = new("Fields");

    private const int MaxFieldDepth = 64;

    public void Evaluate(PreflightContext context)
    {
        // ── gContainsCatalogLang short-circuit ────────────────────────────────
        // If the catalog has a non-empty /Lang, every lang-determination rule
        // passes unconditionally (gContainsCatalogLang == true in veraPDF terms).
        // This is the dominant, FP-safe path: a conformant PDF/UA-1 document
        // always has a catalog /Lang (UaLangRule), so this rule emits nothing.
        var catalogLangObj = context.Resolve(context.Catalog.Get(_lang));
        if (IsNonEmptyString(catalogLangObj))
            return;

        // Catalog has no /Lang (or an empty one) — run per-element checks.
        var tree = StructureTree.Analyze(context);

        // ── 7.2-21/22/23: StructElem attribute (/ActualText, /Alt, /E) ────────
        // Fire when the element carries the attribute AND neither it nor any
        // ancestor in the structure tree has a /Lang entry (key presence, any
        // value including empty, counts as containsLang=true per veraPDF probe).
        CheckStructElems(context, tree);

        // ── 7.2-24: Annotation /Contents ─────────────────────────────────────
        // Fire when the annotation has a non-empty /Contents AND its direct
        // enclosing struct element (via /StructParent → /ParentTree) lacks /Lang.
        CheckAnnotations(context, tree);

        // ── 7.2-25: FormField /TU ─────────────────────────────────────────────
        // Walk the AcroForm /Fields tree; fire when a field has a /TU entry AND
        // its Widget annotation's direct enclosing struct element lacks /Lang.
        CheckFormFields(context, tree);
    }

    // ── 7.2-21/22/23 ─────────────────────────────────────────────────────────

    private void CheckStructElems(PreflightContext context, StructureTree tree)
    {
        foreach (var node in tree.AllNodes)
        {
            var dict = node.Dict;

            // Check each attribute independently; a node may carry more than one.
            if (dict.Get(_actualText) is not null && !HasLangInChain(context, node))
            {
                context.Report(
                    "ISO14289-1:7.2-21",
                    Clause,
                    PreflightSeverity.Error,
                    "A structure element with an /ActualText entry has no determinable natural language: "
                    + "neither the element nor any ancestor carries a /Lang entry, and the document "
                    + "catalog has no /Lang (ISO 14289-1:2014, 7.2, testNumber 21).");
            }

            if (dict.Get(_alt) is not null && !HasLangInChain(context, node))
            {
                context.Report(
                    "ISO14289-1:7.2-22",
                    Clause,
                    PreflightSeverity.Error,
                    "A structure element with an /Alt entry has no determinable natural language: "
                    + "neither the element nor any ancestor carries a /Lang entry, and the document "
                    + "catalog has no /Lang (ISO 14289-1:2014, 7.2, testNumber 22).");
            }

            if (dict.Get(_e) is not null && !HasLangInChain(context, node))
            {
                context.Report(
                    "ISO14289-1:7.2-23",
                    Clause,
                    PreflightSeverity.Error,
                    "A structure element with an /E (expansion) entry has no determinable natural language: "
                    + "neither the element nor any ancestor carries a /Lang entry, and the document "
                    + "catalog has no /Lang (ISO 14289-1:2014, 7.2, testNumber 23).");
            }
        }
    }

    // Returns true when this node OR any ancestor has a /Lang key (any value,
    // including empty string — veraPDF treats /Lang () as containsLang=true).
    private static bool HasLangInChain(PreflightContext context, StructureTreeNode node)
    {
        var current = node;
        while (current is not null)
        {
            if (current.Dict.Get(_lang) is not null)
                return true;
            current = current.Parent;
        }
        return false;
    }

    // ── 7.2-24 ───────────────────────────────────────────────────────────────

    private void CheckAnnotations(PreflightContext context, StructureTree tree)
    {
        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;

            for (var i = 0; i < annots.Count; i++)
            {
                if (context.Resolve(annots[i]) is not PdfDictionary annot)
                    continue;

                // Only fire when the annotation has a non-empty /Contents.
                if (!HasNonEmptyString(context.Resolve(annot.Get(_contents))))
                    continue;

                // containsLang satisfied when the direct enclosing struct element
                // (via /StructParent → /ParentTree) has a /Lang key.
                // Annotation-dict /Lang does NOT satisfy (empirically confirmed).
                // Ancestor struct-element /Lang does NOT satisfy (empirically confirmed).
                // If /StructParent is absent or ParentTree lookup fails → fire (FP-safe: no lang).
                if (context.Resolve(annot.Get(_structParent)) is PdfInteger spInt)
                {
                    var structElem = tree.StructParentOf((int)spInt.Value);
                    if (structElem is not null && structElem.Dict.Get(_lang) is not null)
                        continue; // direct struct elem has /Lang — satisfied
                }

                // No struct parent with /Lang → violation.
                var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;
                var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";
                context.Report(
                    "ISO14289-1:7.2-24",
                    Clause,
                    PreflightSeverity.Error,
                    $"{label} has a non-empty /Contents entry but no determinable natural language: "
                    + "its direct enclosing structure element has no /Lang, and the document catalog "
                    + "has no /Lang (ISO 14289-1:2014, 7.2, testNumber 24).");
            }
        }
    }

    // ── 7.2-25 ───────────────────────────────────────────────────────────────

    private void CheckFormFields(PreflightContext context, StructureTree tree)
    {
        if (context.Resolve(context.Catalog.Get(_acroForm)) is not PdfDictionary acroForm)
            return;
        if (context.Resolve(acroForm.Get(_fields)) is not PdfArray fields)
            return;

        var visited = new HashSet<int>();
        WalkFields(context, tree, fields, visited, depth: 0);
    }

    private void WalkFields(
        PreflightContext context,
        StructureTree tree,
        PdfArray fields,
        HashSet<int> visited,
        int depth)
    {
        if (depth > MaxFieldDepth)
            return;

        for (var i = 0; i < fields.Count; i++)
        {
            // Cycle guard.
            if (fields[i] is PdfIndirectReference r && !visited.Add(r.ObjectNumber))
                continue;
            if (context.Resolve(fields[i]) is not PdfDictionary field)
                continue;

            // Fire when the field has a /TU entry.
            if (field.Get(_tu) is not null)
            {
                // containsLang satisfied when the Widget annotation's direct enclosing struct
                // element (via /StructParent → /ParentTree) has a /Lang key.
                // Field-dict /Lang does NOT satisfy (empirically confirmed).
                // Ancestor struct-element /Lang does NOT satisfy (empirically confirmed).
                var hasDeterminableLang = false;

                if (context.Resolve(field.Get(_structParent)) is PdfInteger spInt)
                {
                    var structElem = tree.StructParentOf((int)spInt.Value);
                    if (structElem is not null && structElem.Dict.Get(_lang) is not null)
                        hasDeterminableLang = true;
                }

                if (!hasDeterminableLang)
                {
                    var ft = (context.Resolve(field.Get(new PdfName("FT"))) as PdfName)?.Value;
                    var fieldLabel = ft is null ? "A form field" : $"A /{ft} form field";
                    context.Report(
                        "ISO14289-1:7.2-25",
                        Clause,
                        PreflightSeverity.Error,
                        $"{fieldLabel} has a /TU (tooltip) entry but no determinable natural language: "
                        + "its enclosing structure element has no /Lang, and the document catalog "
                        + "has no /Lang (ISO 14289-1:2014, 7.2, testNumber 25).");
                }
            }

            // Recurse into child fields (/Kids).
            if (context.Resolve(field.Get(PdfName.Kids)) is PdfArray kids)
                WalkFields(context, tree, kids, visited, depth + 1);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Returns true when the PDF object is a non-empty literal or hex string.
    private static bool IsNonEmptyString(PdfObject? obj) => obj switch
    {
        PdfLiteralString s => s.Bytes.Length > 0,
        PdfHexString h => h.Bytes.Length > 0,
        _ => false,
    };

    // Returns true when the PDF object is a non-empty literal or hex string
    // (used for /Contents and /TU checks).
    private static bool HasNonEmptyString(PdfObject? obj) => IsNonEmptyString(obj);
}
