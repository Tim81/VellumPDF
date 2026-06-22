// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.1 (General annotation requirements). Every annotation that is visible
/// (not hidden, not outside the crop box) and is not a Widget shall have a non-empty
/// <c>/Contents</c> entry or a non-empty annotation-level <c>/Alt</c> entry (alternate description).
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.18.1 (PDAnnot predicate for testNumber 2:
/// <c>Subtype == 'Widget' || isOutsideCropBox == true || (F &amp; 2) == 2
///    || (Contents != null &amp;&amp; Contents != '')
///    || (Alt != null &amp;&amp; Alt != '')</c>)
/// and empirically validated against veraPDF 1.30.2 (clause 7.18.1, testNumber 2). Clean-room:
/// derived from the specification text and the veraPDF profile, not from any third-party
/// implementation.
/// <para>
/// veraPDF resolves the <c>Alt</c> of this predicate from the annotation's <em>enclosing structure
/// element</em> (reached via the annotation's <c>/StructParent</c> → the structure parent tree),
/// not only from the annotation dictionary's own <c>/Alt</c> key. This was confirmed empirically
/// against veraPDF 1.30.2: an annotation with no <c>/Contents</c> and no annotation-level
/// <c>/Alt</c> but whose enclosing struct element carries an <c>/Alt</c> does NOT trigger
/// testNumber 2, whereas the same annotation with a struct element lacking <c>/Alt</c>, or with no
/// structure binding at all, does. Because resolving the struct-element <c>/Alt</c> needs the
/// tagged-content walker (a later slice), this rule is scoped FOR FALSE-POSITIVE SAFETY to fire
/// only on annotations that are <em>not bound into the structure tree at all</em> — i.e. those with
/// no <c>/StructParent</c>. An annotation that has a <c>/StructParent</c> is skipped (its alt-text
/// may legitimately live in a struct element we cannot yet read), which under-detects the
/// struct-element-without-<c>/Alt</c> case rather than risk over-rejecting a conformant tagged
/// annotation. This makes the clause Partial; full coverage awaits the structure-tree walker.
/// </para>
/// <para>
/// This rule does NOT duplicate 7.18.5-2 (the Link-specific Contents requirement). Both rules
/// fire independently when a Link annotation lacks <c>/Contents</c> — this is consistent with
/// veraPDF 1.30.2 behaviour observed during probe testing (a Link without Contents triggers both
/// clauses simultaneously), and avoids under-detecting violations on non-Link annotation types.
/// </para>
/// <para>
/// testNumber 1 of §7.18.1 (annotation-to-structure binding) requires the tagged-content walker
/// and is deferred.
/// </para>
/// </remarks>
internal sealed class UaAnnotContentsRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.1-2";

    public string Clause => "ISO 14289-1:2014, 7.18.1";

    private static readonly PdfName _contents = new("Contents");
    private static readonly PdfName _alt = new("Alt");
    private static readonly PdfName _structParent = new("StructParent");

    public void Evaluate(PreflightContext context)
    {
        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;
            for (var i = 0; i < annots.Count; i++)
            {
                if (context.Resolve(annots[i]) is not PdfDictionary annot)
                    continue;
                var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;

                // Widget annotations are unconditionally exempt from this requirement.
                if (subtype == "Widget")
                    continue;

                // Exempt: hidden or entirely outside the crop box.
                if (UaAnnotationHelper.IsExempt(context, annot, page))
                    continue;

                // Exempt (false-positive safety): the annotation is bound into the structure tree
                // via /StructParent, so its alternate description may live in the enclosing structure
                // element's /Alt — which veraPDF reads but we cannot resolve without the structure-tree
                // walker. Flagging it would over-reject a conformant tagged annotation. We therefore
                // only check annotations with NO structure binding (see the remarks above).
                if (annot.Get(_structParent) is not null)
                    continue;

                // The requirement is satisfied when /Contents OR /Alt is non-empty.
                if (HasNonEmptyString(context, annot, _contents) || HasNonEmptyString(context, annot, _alt))
                    continue;

                var label = subtype is null ? "An annotation" : $"A /{subtype} annotation";
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    $"{label} visible inside the crop box has no non-empty /Contents or /Alt entry. "
                    + "PDF/UA-1 requires every non-Widget annotation to carry an alternate text description "
                    + "in /Contents or /Alt (ISO 14289-1:2014, 7.18.1).");
            }
        }
    }

    private static bool HasNonEmptyString(PreflightContext context, PdfDictionary dict, PdfName key)
    {
        var raw = context.Resolve(dict.Get(key));
        return raw switch
        {
            PdfLiteralString s => s.Bytes.Length > 0,
            PdfHexString s => s.Bytes.Length > 0,
            _ => false,
        };
    }
}
