// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance.Rules;

/// <summary>
/// The state shared across all rules for a single validation pass: the document under test
/// and the sink that collects findings. Provides convenience accessors so individual rules
/// do not each reach into <see cref="PdfDocumentReader"/> internals.
/// </summary>
internal sealed class PreflightContext
{
    private readonly List<PreflightAssertion> _assertions;

    internal PreflightContext(
        PdfDocumentReader reader,
        PdfConformance conformance,
        List<PreflightAssertion> assertions)
    {
        Reader = reader;
        Conformance = conformance;
        _assertions = assertions;
    }

    /// <summary>The document being validated.</summary>
    public PdfDocumentReader Reader { get; }

    /// <summary>The conformance level being validated against.</summary>
    public PdfConformance Conformance { get; }

    /// <summary>The document catalog (/Root) dictionary.</summary>
    public PdfDictionary Catalog => Reader.Catalog;

    /// <summary>
    /// Resolves <paramref name="obj"/> through any indirect reference, returning the target
    /// value. Returns <see langword="null"/> when the input is null or cannot be resolved.
    /// </summary>
    public PdfObject? Resolve(PdfObject? obj) => obj is null ? null : Reader.ResolveValue(obj);

    /// <summary>Records a finding for the current validation pass.</summary>
    /// <param name="ruleId">Stable rule identifier (typically the rule's <see cref="IConformanceRule.RuleId"/>).</param>
    /// <param name="clause">Specification clause citation.</param>
    /// <param name="severity">The finding's severity.</param>
    /// <param name="message">Human-readable description.</param>
    /// <param name="objectRef">Optional <c>"N 0 R"</c> object location.</param>
    public void Report(
        string ruleId,
        string clause,
        PreflightSeverity severity,
        string message,
        string? objectRef = null)
        => _assertions.Add(new PreflightAssertion(ruleId, clause, severity, message, objectRef));
}
