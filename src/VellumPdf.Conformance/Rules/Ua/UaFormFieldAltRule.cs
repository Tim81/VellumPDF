// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.1, testNumber 3 — form field alternate name requirement.
/// Every form field (leaf or container) in the AcroForm field tree shall have a <c>/TU</c>
/// (tooltip / alternate field name) entry, OR every Widget annotation associated with that
/// field shall have a non-empty <c>/Alt</c> entry on its enclosing structure element.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.18.1 (testNumber 3). Clean-room.
///
/// <para><strong>Exemptions (per §7.18):</strong> a Widget annotation is exempt from §7.18
/// requirements when it is hidden (<c>F &amp; 2</c>) or its <c>/Rect</c> lies entirely outside the
/// page's effective crop box. A field whose ALL associated Widget annotations are exempt is
/// itself exempt from this rule (no visible, non-hidden widget ⇒ the user cannot interact with it
/// and the accessibility concern does not apply).</para>
///
/// <para><strong>Field / Widget relationship:</strong> in PDF, a field dictionary and its Widget
/// annotation can be merged into a single dictionary (the "merged" form). The rule therefore
/// checks the field dict itself as a Widget candidate when it has a <c>/Subtype /Widget</c> entry
/// (merged form), and also walks any explicit <c>/Kids</c> array for Widget annotation children.
/// </para>
///
/// <para><strong>/Alt on the struct element:</strong> the /Alt value is checked on the DIRECT
/// enclosing structure element of the Widget annotation (via <c>/StructParent</c> →
/// <c>/ParentTree</c>), mirroring the approach used in <see cref="UaAnnotContentsRule"/>.
/// An /Alt on the annotation dictionary itself does not satisfy the requirement (the spec refers
/// to the structure element). If the /StructParent lookup fails, /Alt is treated as absent
/// (conservative / FP-safe).</para>
///
/// <para><strong>FP safety:</strong> the rule only fires when (a) the field has no /TU AND
/// (b) at least one non-exempt Widget annotation associated with the field also lacks /Alt on its
/// struct element. A field with no Widget annotations at all (a pure container with /Kids containing
/// only sub-fields) is not itself a leaf field and is not fired upon — the leaf fields in its /Kids
/// will be checked individually.</para>
/// </remarks>
internal sealed class UaFormFieldAltRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.1-3";

    public string Clause => "ISO 14289-1:2014, 7.18.1";

    private static readonly PdfName _acroForm = new("AcroForm");
    private static readonly PdfName _fields = new("Fields");
    private static readonly PdfName _tu = new("TU");
    private static readonly PdfName _alt = new("Alt");
    private static readonly PdfName _structParent = new("StructParent");
    private static readonly PdfName _ft = new("FT");
    private static readonly PdfName _parent = new("Parent");

    private const int MaxFieldDepth = 64;

    public void Evaluate(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_acroForm)) is not PdfDictionary acroForm)
            return;
        if (context.Resolve(acroForm.Get(_fields)) is not PdfArray fields)
            return;

        var tree = StructureTree.Analyze(context);

        // Build a page-lookup map for Widget annotation exemption checks.
        var pagesByAnnot = BuildAnnotPageMap(context);

        var visited = new HashSet<int>();
        WalkFields(context, tree, fields, pagesByAnnot, visited, depth: 0);
    }

    // Builds a map from Widget annotation indirect-object-number → page dictionary.
    // Used by UaAnnotationHelper.IsExempt which needs a page reference.
    private static Dictionary<int, PdfDictionary> BuildAnnotPageMap(PreflightContext context)
    {
        var map = new Dictionary<int, PdfDictionary>();
        foreach (var page in context.EnumeratePages())
        {
            if (context.Resolve(page.Get(PdfName.Annots)) is not PdfArray annots)
                continue;
            for (var i = 0; i < annots.Count; i++)
            {
                if (annots[i] is not PdfIndirectReference r)
                    continue;
                if (context.Resolve(annots[i]) is not PdfDictionary annot)
                    continue;
                var subtype = (context.Resolve(annot.Get(PdfName.Subtype)) as PdfName)?.Value;
                if (subtype == "Widget")
                    map.TryAdd(r.ObjectNumber, page);
            }
        }
        return map;
    }

    private void WalkFields(
        PreflightContext context,
        StructureTree tree,
        PdfArray fields,
        Dictionary<int, PdfDictionary> pagesByAnnot,
        HashSet<int> visited,
        int depth)
    {
        if (depth > MaxFieldDepth)
            return;

        for (var i = 0; i < fields.Count; i++)
        {
            int objNum = -1;
            if (fields[i] is PdfIndirectReference r)
            {
                objNum = r.ObjectNumber;
                if (!visited.Add(objNum))
                    continue;
            }

            if (context.Resolve(fields[i]) is not PdfDictionary field)
                continue;

            // Inherit /FT from parent chain if not present locally (for diagnostics only).
            var ft = (context.Resolve(field.Get(_ft)) as PdfName)?.Value;

            // Collect Widget annotations associated with this field dict.
            // Case 1: the field IS also a Widget (merged form) — /Subtype /Widget on same dict.
            // Case 2: the /Kids array contains Widget annotation children (not sub-fields).
            var widgets = new List<(PdfDictionary Widget, int ObjNum)>();
            CollectWidgets(context, field, objNum, widgets);

            // Determine whether this is a terminal field (has widgets or no kids at all).
            var hasSelfWidget = IsWidget(context, field);
            var hasKids = context.Resolve(field.Get(PdfName.Kids)) is PdfArray;

            bool isTerminalField = hasSelfWidget || !hasKids;

            // Only evaluate leaf / terminal fields that have widget annotations.
            // Pure container fields without any widgets are not subject to the rule.
            if (widgets.Count > 0 || isTerminalField)
            {
                EvaluateField(context, tree, field, ft, widgets, pagesByAnnot);
            }

            // Recurse into /Kids for sub-fields.
            if (context.Resolve(field.Get(PdfName.Kids)) is PdfArray kids)
                WalkFields(context, tree, kids, pagesByAnnot, visited, depth + 1);
        }
    }

    private static bool IsWidget(PreflightContext context, PdfDictionary dict)
    {
        var subtype = (context.Resolve(dict.Get(PdfName.Subtype)) as PdfName)?.Value;
        return subtype == "Widget";
    }

    // Collects the Widget annotations that are direct /Kids of this field dict.
    private static void CollectWidgets(
        PreflightContext context,
        PdfDictionary field,
        int fieldObjNum,
        List<(PdfDictionary Widget, int ObjNum)> widgets)
    {
        // Case 1: merged field+widget — the field dict itself has /Subtype /Widget.
        if (IsWidget(context, field))
        {
            widgets.Add((field, fieldObjNum));
            return;
        }

        // Case 2: explicit /Kids children that are Widget annotations (not sub-fields).
        if (context.Resolve(field.Get(PdfName.Kids)) is not PdfArray kids)
            return;

        for (var i = 0; i < kids.Count; i++)
        {
            int childObjNum = -1;
            if (kids[i] is PdfIndirectReference cr)
                childObjNum = cr.ObjectNumber;

            if (context.Resolve(kids[i]) is not PdfDictionary child)
                continue;

            if (IsWidget(context, child))
                widgets.Add((child, childObjNum));
        }
    }

    private void EvaluateField(
        PreflightContext context,
        StructureTree tree,
        PdfDictionary field,
        string? ft,
        List<(PdfDictionary Widget, int ObjNum)> widgets,
        Dictionary<int, PdfDictionary> pagesByAnnot)
    {
        // Satisfied when the field has a /TU entry (any value, incl. empty — veraPDF
        // checks key presence; ISO 14289-1 says "an alternate field name" which implies presence).
        if (field.Get(_tu) is not null)
            return;

        // No /TU. Check each associated Widget annotation.
        // The field satisfies the rule when ALL non-exempt widgets have /Alt on their struct elem.
        // The field VIOLATES the rule when ANY non-exempt widget lacks /Alt on its struct elem.
        // If there are no non-exempt widgets, the field is exempt from the rule.
        bool anyNonExemptWidget = false;
        bool anyMissingAlt = false;

        foreach (var (widget, widgetObjNum) in widgets)
        {
            // Find the page for this widget (needed for crop-box exemption check).
            PdfDictionary? page = null;
            if (widgetObjNum >= 0)
                pagesByAnnot.TryGetValue(widgetObjNum, out page);

            // Hidden or outside crop box → exempt.
            if (page is not null && UaAnnotationHelper.IsExempt(context, widget, page))
                continue;

            // Also check the hidden-flag exemption directly on the widget dict even when
            // the page lookup failed (merged-form widget may not appear in page /Annots).
            if (page is null)
            {
                var flags = FlagValue(context, widget);
                if ((flags & 2) != 0) // Hidden flag
                    continue;
            }

            anyNonExemptWidget = true;

            // Check /Alt on the direct enclosing structure element.
            if (WidgetHasStructAlt(context, tree, widget))
                continue;

            anyMissingAlt = true;
            break; // One violation is sufficient to fire.
        }

        // If there are non-exempt widgets and at least one lacks /Alt → violation.
        if (anyNonExemptWidget && anyMissingAlt)
        {
            var fieldLabel = ft is null ? "A form field" : $"A /{ft} form field";
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                $"{fieldLabel} has no /TU (alternate field name) entry, and at least one of its "
                + "associated visible Widget annotations does not have an /Alt entry on its enclosing "
                + "structure element. ISO 14289-1:2014 §7.18.1 requires every interactive form field "
                + "to carry either a /TU or an /Alt on its Widget annotation's structure element "
                + "(ISO 14289-1:2014, 7.18.1, testNumber 3).");
        }
    }

    private static bool WidgetHasStructAlt(PreflightContext context, StructureTree tree, PdfDictionary widget)
    {
        if (context.Resolve(widget.Get(_structParent)) is not PdfInteger spInt)
            return false;
        var node = tree.StructParentOf(spInt.Value);
        if (node is null)
            return false;
        return node.Dict.Get(_alt) is not null;
    }

    private static long FlagValue(PreflightContext context, PdfDictionary dict)
    {
        if (context.Resolve(dict.Get(new PdfName("F"))) is PdfInteger fi)
            return fi.Value;
        if (context.Resolve(dict.Get(new PdfName("F"))) is PdfReal fr)
            return (long)fr.Value;
        return 0;
    }
}
