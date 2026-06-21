// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Conformance;

/// <summary>
/// A single finding produced by a conformance rule: one violated (or noted) requirement,
/// located at an optional object reference. Designed to be comparable to a veraPDF
/// rule check so results can be cross-validated.
/// </summary>
public sealed class PreflightAssertion
{
    /// <summary>
    /// Stable identifier of the rule that produced this assertion (e.g.
    /// <c>"ISO19005-2:6.1.2-output-intent"</c>). Stable across releases so callers can
    /// suppress or map specific findings.
    /// </summary>
    public string RuleId { get; }

    /// <summary>
    /// Human-readable citation of the specification clause the rule enforces
    /// (e.g. <c>"ISO 32000-2:2020, 7.7.2"</c>).
    /// </summary>
    public string Clause { get; }

    /// <summary>The severity of the finding.</summary>
    public PreflightSeverity Severity { get; }

    /// <summary>A human-readable description of the finding.</summary>
    public string Message { get; }

    /// <summary>
    /// The object the finding relates to, formatted as <c>"N 0 R"</c>, or <see langword="null"/>
    /// when the finding is about the document as a whole.
    /// </summary>
    public string? ObjectRef { get; }

    internal PreflightAssertion(
        string ruleId,
        string clause,
        PreflightSeverity severity,
        string message,
        string? objectRef)
    {
        RuleId = ruleId;
        Clause = clause;
        Severity = severity;
        Message = message;
        ObjectRef = objectRef;
    }

    /// <summary>Returns a single-line, diagnostic-friendly rendering of the assertion.</summary>
    public override string ToString()
    {
        var where = ObjectRef is null ? string.Empty : $" [{ObjectRef}]";
        return $"{Severity} {RuleId} ({Clause}){where}: {Message}";
    }
}
