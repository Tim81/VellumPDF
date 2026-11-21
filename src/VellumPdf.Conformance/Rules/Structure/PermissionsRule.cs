// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules.Structure;

/// <summary>
/// ISO 19005-2 §6.1.12 (Permissions). A permissions dictionary — the value of the document catalog's
/// <c>/Perms</c> key — shall contain no keys other than <c>/UR3</c> and <c>/DocMDP</c>
/// (ISO 32000-1:2008, 12.8.4, Table 258).
/// </summary>
/// <remarks>
/// Cites ISO 19005-2:2011, 6.1.12. Its behaviour was checked against veraPDF's PDF/A-2 profile, and
/// ISO 19005-2 was not held when it was written. The 2026-11-21 audit against the clause text
/// (#418) found agreement. A pure object-graph presence check on the catalog's <c>/Perms</c>
/// dictionary.
/// <para>
/// The §6.1.12-2 constraint (a signature reference dictionary reached through a <c>/DocMDP</c>
/// transform shall not carry <c>/DigestLocation</c>, <c>/DigestMethod</c>, or <c>/DigestValue</c>)
/// is checked by <see cref="Signature.DocMdpReferenceRule"/>.
/// </para>
/// </remarks>
internal sealed class PermissionsRule : IConformanceRule
{
    public string RuleId => "ISO19005-2:6.1.12-1-permissions";

    public string Clause => "ISO 19005-2:2011, 6.1.12";

    private static readonly PdfName _perms = new("Perms");

    public void Evaluate(PreflightContext context)
    {
        if (context.Resolve(context.Catalog.Get(_perms)) is not PdfDictionary perms)
            return;

        foreach (var entry in perms.Entries)
        {
            if (entry.Key.Value is "UR3" or "DocMDP")
                continue;
            context.Report(
                RuleId, Clause, PreflightSeverity.Error,
                $"The permissions dictionary contains the key "
                + $"/{DiagnosticExcerpt.Quote(entry.Key.Value)}; only /UR3 and /DocMDP "
                + "are permitted in PDF/A-2.");
            return; // One report suffices; the verdict is unaffected by the count.
        }
    }
}
