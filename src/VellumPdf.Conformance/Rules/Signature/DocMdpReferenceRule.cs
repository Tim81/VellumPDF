// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Signature;

/// <summary>
/// ISO 19005-2 §6.1.12-2 (DocMDP signature references). When the document catalog's
/// <c>/Perms</c> dictionary contains a <c>/DocMDP</c> entry, the <c>/Reference</c> array
/// of that signature dictionary shall not contain any entry with the keys
/// <c>/DigestLocation</c>, <c>/DigestMethod</c>, or <c>/DigestValue</c>.
/// </summary>
/// <remarks>
/// Authored from ISO 19005-2:2011, 6.1.12. Clean-room: derived from the specification text,
/// not from any third-party validation profile.
///
/// Traversal: catalog → /Perms /DocMDP → resolve → /Reference array → each signature reference
/// dictionary. If any dictionary in the /Reference array carries one of the forbidden keys,
/// one finding is emitted listing all forbidden keys found.
/// </remarks>
internal sealed class DocMdpReferenceRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.12-2";
    public string Clause => "ISO 19005-2:2011, 6.1.12";

    private static readonly PdfName _perms = new("Perms");
    private static readonly PdfName _docMdp = new("DocMDP");
    private static readonly PdfName _reference = new("Reference");
    private static readonly PdfName _digestLocation = new("DigestLocation");
    private static readonly PdfName _digestMethod = new("DigestMethod");
    private static readonly PdfName _digestValue = new("DigestValue");

    public void Evaluate(PreflightContext context)
    {
        // /Perms must exist in the catalog.
        if (context.Resolve(context.Catalog.Get(_perms)) is not PdfDictionary perms)
            return;

        // /DocMDP must be present in /Perms.
        if (perms.Get(_docMdp) is null)
            return;

        // Resolve the /DocMDP signature dictionary.
        if (context.Resolve(perms.Get(_docMdp)) is not PdfDictionary docMdpSig)
            return;

        // /Reference is an array of signature reference dictionaries.
        if (context.Resolve(docMdpSig.Get(_reference)) is not PdfArray refArray)
            return;

        // Walk each signature reference dictionary.
        for (var i = 0; i < refArray.Count; i++)
        {
            if (context.Resolve(refArray[i]) is not PdfDictionary sigRef)
                continue;

            // Collect forbidden keys present in this reference dictionary.
            var forbidden = new List<string>(3);
            if (sigRef.Get(_digestLocation) is not null) forbidden.Add("/DigestLocation");
            if (sigRef.Get(_digestMethod) is not null) forbidden.Add("/DigestMethod");
            if (sigRef.Get(_digestValue) is not null) forbidden.Add("/DigestValue");

            if (forbidden.Count > 0)
            {
                context.Report(
                    RuleId, Clause, PreflightSeverity.Error,
                    $"The Signature References dictionary contains {string.Join(", ", forbidden)} "
                    + "key(s) in the presence of DocMDP.");
                // One report per document is sufficient; the verdict is unaffected by how many
                // reference entries violate the rule.
                return;
            }
        }
    }
}
