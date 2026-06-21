// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// A single conformance check. Rules are stateless and reusable; all per-validation state
/// lives on the <see cref="PreflightContext"/> passed to <see cref="Evaluate"/>.
/// </summary>
/// <remarks>
/// Rules are registered explicitly in <see cref="RuleRegistry"/> — never discovered by
/// reflection — so the validator stays fully AOT- and trim-safe.
/// </remarks>
internal interface IConformanceRule
{
    /// <summary>Stable identifier emitted on every assertion this rule produces.</summary>
    string RuleId { get; }

    /// <summary>The specification clause this rule enforces, cited on every assertion.</summary>
    string Clause { get; }

    /// <summary>
    /// Runs the check, reporting any findings via <see cref="PreflightContext.Report"/>.
    /// A rule that finds nothing wrong reports nothing.
    /// </summary>
    void Evaluate(PreflightContext context);
}
