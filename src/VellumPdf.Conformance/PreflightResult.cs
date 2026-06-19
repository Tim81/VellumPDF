// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Collections.ObjectModel;

namespace VellumPdf.Conformance;

/// <summary>
/// The outcome of validating a document against a <see cref="PdfConformance"/> level:
/// an overall verdict plus the individual <see cref="PreflightAssertion"/> findings.
/// </summary>
public sealed class PreflightResult
{
    /// <summary>The conformance level the document was validated against.</summary>
    public PdfConformance Conformance { get; }

    /// <summary>
    /// Every finding produced during validation, in rule-evaluation order.
    /// Includes <see cref="PreflightSeverity.Warning"/> and <see cref="PreflightSeverity.Info"/>
    /// findings as well as <see cref="PreflightSeverity.Error"/> ones.
    /// </summary>
    public IReadOnlyList<PreflightAssertion> Assertions { get; }

    /// <summary>
    /// <see langword="true"/> when no <see cref="PreflightSeverity.Error"/> assertion was produced.
    /// </summary>
    public bool IsCompliant { get; }

    internal PreflightResult(PdfConformance conformance, IReadOnlyList<PreflightAssertion> assertions)
    {
        Conformance = conformance;

        var compliant = true;
        foreach (var a in assertions)
        {
            if (a.Severity == PreflightSeverity.Error)
            {
                compliant = false;
                break;
            }
        }
        IsCompliant = compliant;

        // Seal the list so a caller cannot downcast Assertions to its backing List and mutate it
        // (which would leave IsCompliant — computed once here — inconsistent with the contents).
        Assertions = new ReadOnlyCollection<PreflightAssertion>([.. assertions]);
    }
}
