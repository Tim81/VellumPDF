// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules;
using VellumPdf.Reader;

namespace VellumPdf.Conformance;

/// <summary>
/// Entry point for in-process PDF/A and PDF/UA preflight validation. Runs a registry of
/// clean-room conformance rules against a document and returns the findings.
/// </summary>
public static class PdfPreflight
{
    /// <summary>Validates the PDF contained in <paramref name="bytes"/> against <paramref name="conformance"/>.</summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    public static PreflightResult Validate(byte[] bytes, PdfConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = PdfReader.Open(bytes);
        return Validate(reader, conformance);
    }

    /// <summary>Validates the PDF read from <paramref name="stream"/> against <paramref name="conformance"/>.</summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    public static PreflightResult Validate(Stream stream, PdfConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = PdfReader.Open(stream);
        return Validate(reader, conformance);
    }

    /// <summary>Validates an already-opened <paramref name="reader"/> against <paramref name="conformance"/>.</summary>
    /// <remarks>
    /// The caller retains ownership of <paramref name="reader"/>; it is not disposed here.
    /// <para><see cref="PdfDocumentReader"/> is not thread-safe (it populates an unsynchronized
    /// object cache), so a single reader must not be validated from multiple threads concurrently.
    /// The <see cref="Validate(byte[], PdfConformance)"/> and <see cref="Validate(System.IO.Stream, PdfConformance)"/>
    /// overloads open a fresh reader per call and are safe to invoke concurrently.</para>
    /// </remarks>
    /// <exception cref="System.ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    public static PreflightResult Validate(PdfDocumentReader reader, PdfConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (!RuleRegistry.TryGetProfile(conformance, out var rules))
        {
            throw new NotSupportedException(
                $"In-process preflight for {conformance} is not implemented yet. " +
                "Tracking: https://github.com/Tim81/VellumPDF/issues/50.");
        }

        var assertions = new List<PreflightAssertion>();
        var context = new PreflightContext(reader, conformance, assertions);

        foreach (var rule in rules)
        {
            try
            {
                rule.Evaluate(context);
            }
            catch (Exception ex)
                when (ex is not OutOfMemoryException and not UnsupportedPdfFeatureException)
            {
                // A single rule throwing on a malformed-but-parseable document must not abort the
                // whole report. Record it as an error finding and continue with the other rules.
                // UnsupportedPdfFeatureException is excluded defensively: today it is only raised at
                // Open (before any rule runs), but should a future lazily-decoded reader feature let
                // a rule raise it, it means "cannot evaluate" — a distinct signal that should
                // propagate to the caller rather than be reported as a conformance violation.
                context.Report(rule.RuleId, rule.Clause, PreflightSeverity.Error,
                    $"Rule evaluation failed: {ex.Message}");
            }
        }

        return new PreflightResult(conformance, assertions);
    }
}
