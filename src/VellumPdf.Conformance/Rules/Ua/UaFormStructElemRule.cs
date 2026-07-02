// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;
using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Ua;

/// <summary>
/// ISO 14289-1 §7.18.4, testNumber 2 — Form structure element child constraint.
/// A <c>Form</c> structure element that does not have a <c>/Role</c> attribute shall have
/// exactly one child, and that child shall be an object reference (<c>/OBJR</c>) to the
/// interactive form field.
/// </summary>
/// <remarks>
/// Authored from ISO 14289-1:2014, 7.18.4 (testNumber 2). Clean-room.
///
/// <para><strong>Scope:</strong> fires only when the Form struct element lacks a <c>/Role</c>
/// attribute in its <c>/A</c> (attribute) dict. A Form element WITH a /Role attribute takes on a
/// different semantic (a layout role) and is exempt from the single-OBJR-child constraint.
/// This scoping keeps the check FP-safe: we never fire on a conformant document that uses
/// /Role to indicate a layout role.</para>
///
/// <para><strong>Child counting:</strong> the structure tree walker (<see cref="StructureTree"/>)
/// partitions /K into StructElem children (<see cref="StructureTreeNode.Children"/>) and
/// non-element kids (<see cref="StructureTreeNode.HasNonElementKids"/> — integers / MCR / OBJR).
/// A Form element has one OBJR child when:
/// <list type="bullet">
///   <item>It has NO StructElem children (<see cref="StructureTreeNode.Children"/> is empty).</item>
///   <item>It has exactly one non-element kid (<see cref="StructureTreeNode.HasNonElementKids"/> is
///     true) and that single kid is an /OBJR dict (confirmed by re-reading the raw /K).</item>
/// </list>
/// This re-read of the raw /K is intentional: the tree walker collapses non-element kids into a
/// single boolean flag (<c>HasNonElementKids</c>), so we need to count them from the dict
/// directly. The count is bounded by the depth cap in the tree walker, so no additional guard is
/// needed.</para>
/// </remarks>
internal sealed class UaFormStructElemRule : IConformanceRule
{
    public string RuleId => "ISO14289-1:7.18.4-2";

    public string Clause => "ISO 14289-1:2014, 7.18.4";

    private static readonly PdfName _a = new("A");
    private static readonly PdfName _role = new("Role");
    private static readonly PdfName _k = new("K");

    public void Evaluate(PreflightContext context)
    {
        var tree = StructureTree.Analyze(context);

        foreach (var node in tree.AllNodes)
        {
            if (node.StandardType != "Form")
                continue;

            // Exempt: Form struct element with a /Role attribute in its /A dict.
            if (HasRoleAttribute(context, node))
                continue;

            // No /Role — this Form element must have exactly one child, an OBJR.
            // StructureTreeNode.Children holds StructElem children; non-element kids
            // (OBJR, MCR, bare integers) are counted from the raw /K array.
            if (node.Children.Count != 0)
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "A Form structure element without a /Role attribute has StructElem children, "
                    + "but §7.18.4 requires it to have exactly one child — an OBJR reference to "
                    + "the interactive form field (ISO 14289-1:2014, 7.18.4, testNumber 2).");
                continue;
            }

            // Count and validate the non-element kids.
            CountNonElementKids(context, node, out int nonElemCount, out bool firstIsObjr);

            if (nonElemCount != 1 || !firstIsObjr)
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    "A Form structure element without a /Role attribute does not have exactly one "
                    + "OBJR child. §7.18.4 requires a Form structure element (without /Role) to "
                    + "contain exactly one child, which shall be an object reference (OBJR) to the "
                    + "associated interactive form field (ISO 14289-1:2014, 7.18.4, testNumber 2).");
            }
        }
    }

    // Returns true when the node's /A dict has a /Role entry.
    private static bool HasRoleAttribute(PreflightContext context, StructureTreeNode node)
    {
        var aObj = context.Resolve(node.Dict.Get(_a));
        if (aObj is PdfDictionary attrDict)
            return attrDict.Get(_role) is not null;

        // /A may also be an array of attribute dictionaries.
        if (aObj is PdfArray attrArray)
        {
            for (var i = 0; i < attrArray.Count; i++)
            {
                if (context.Resolve(attrArray[i]) is PdfDictionary ad && ad.Get(_role) is not null)
                    return true;
            }
        }
        return false;
    }

    // Counts non-element kids (MCID integers, /MCR, /OBJR) in the raw /K of the node.
    // Also determines whether the first non-element kid is an OBJR.
    private static void CountNonElementKids(
        PreflightContext context,
        StructureTreeNode node,
        out int count,
        out bool firstIsObjr)
    {
        count = 0;
        firstIsObjr = false;

        var kObj = node.Dict.Get(_k);
        if (kObj is null)
            return;

        var resolved = context.Resolve(kObj);

        if (resolved is PdfArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
            {
                var kid = context.Resolve(arr[i]);
                if (IsNonElementKid(kid))
                {
                    count++;
                    if (count == 1)
                        firstIsObjr = IsObjr(kid);
                }
            }
        }
        else
        {
            // Single-kid /K (not an array).
            if (IsNonElementKid(resolved))
            {
                count = 1;
                firstIsObjr = IsObjr(resolved);
            }
        }
    }

    // Returns true when the resolved kid object is a non-StructElem kid.
    private static bool IsNonElementKid(PdfObject? kid)
    {
        if (kid is PdfInteger)
            return true;
        if (kid is PdfDictionary d)
        {
            var t = (d.Get(PdfName.Type) as PdfName)?.Value;
            return t is "MCR" or "OBJR";
        }
        return false;
    }

    // Returns true when the kid is an /OBJR dict.
    private static bool IsObjr(PdfObject? kid)
    {
        if (kid is not PdfDictionary d)
            return false;
        var t = (d.Get(PdfName.Type) as PdfName)?.Value;
        return t == "OBJR";
    }
}
