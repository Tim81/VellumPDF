// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules.Structure;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// Maps each <see cref="PdfConformance"/> level to the ordered list of rules that define it.
/// Profiles are built from explicit, hand-written rule lists — no reflection, no assembly
/// scanning — so the validator is fully AOT- and trim-compatible.
/// </summary>
internal static class RuleRegistry
{
    // Baseline structural rules shared by every conformance level. As coverage grows
    // (issues #109–#113) each level's profile is composed from these plus its level-specific rules.
    private static readonly IConformanceRule[] CommonStructure =
    [
        new DocumentCatalogRule(),
    ];

    private static readonly IConformanceRule[] PdfA2BRules = [.. CommonStructure];

    /// <summary>
    /// Returns the rule profile for <paramref name="conformance"/>, or <see langword="false"/>
    /// when no profile is registered yet for that level.
    /// </summary>
    public static bool TryGetProfile(PdfConformance conformance, out IReadOnlyList<IConformanceRule> rules)
    {
        switch (conformance)
        {
            case PdfConformance.PdfA2B:
                rules = PdfA2BRules;
                return true;
            default:
                rules = [];
                return false;
        }
    }
}
