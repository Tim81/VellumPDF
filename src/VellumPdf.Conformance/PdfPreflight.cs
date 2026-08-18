// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules;
using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;
using VellumPdf.Reader;

namespace VellumPdf.Conformance;

/// <summary>
/// Entry point for in-process PDF/A and PDF/UA preflight validation. Runs a registry of
/// clean-room conformance rules against a document and returns the findings.
/// </summary>
public static class PdfPreflight
{
    private static readonly PdfName _metadataName = new("Metadata");

    /// <summary>
    /// Reads the XMP /Metadata stream from the document catalog and returns the
    /// conformance profiles the document claims via <c>pdfaid:part</c>/<c>pdfaid:conformance</c>
    /// and <c>pdfuaid:part</c>. Returns an empty list when the catalog has no /Metadata or the
    /// document makes no PDF/A or PDF/UA claim.
    /// </summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    public static IReadOnlyList<PdfConformance> DetectClaimedProfiles(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = PdfReader.Open(bytes);
        return DetectClaimedProfiles(reader);
    }

    /// <summary>
    /// Reads the XMP /Metadata stream from the document catalog and returns the
    /// conformance profiles the document claims via <c>pdfaid:part</c>/<c>pdfaid:conformance</c>
    /// and <c>pdfuaid:part</c>. Returns an empty list when the catalog has no /Metadata or the
    /// document makes no PDF/A or PDF/UA claim.
    /// </summary>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="System.IO.IOException">Reading <paramref name="stream"/> failed.</exception>
    /// <exception cref="System.ObjectDisposedException"><paramref name="stream"/> has been disposed.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    public static IReadOnlyList<PdfConformance> DetectClaimedProfiles(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = PdfReader.Open(stream);
        return DetectClaimedProfiles(reader);
    }

    private static IReadOnlyList<PdfConformance> DetectClaimedProfiles(PdfDocumentReader reader)
    {
        var metaRef = reader.Catalog.Get(_metadataName);
        if (metaRef is not PdfIndirectReference r)
            return [];

        var parsedStream = reader.ResolveStream(r);
        if (parsedStream is null)
            return [];

        var bytes = reader.GetDecodedStreamData(parsedStream);
        if (bytes is null)
            return [];

        var xmp = XmpReader.Parse(bytes);
        if (xmp is null)
            return [];

        var results = new List<PdfConformance>();

        var part = XmpReader.Get(xmp, XmpReader.Pdfaid, "part");
        var conformance = XmpReader.Get(xmp, XmpReader.Pdfaid, "conformance");

        if (part == "2")
        {
            var level = conformance?.ToUpperInvariant();
            if (level == "B")
                results.Add(PdfConformance.PdfA2B);
            else if (level == "U")
                results.Add(PdfConformance.PdfA2U);
            else if (level == "A")
                results.Add(PdfConformance.PdfA2A);
        }

        var uaPart = XmpReader.Get(xmp, XmpReader.Pdfuaid, "part");
        if (uaPart == "1")
            results.Add(PdfConformance.PdfUA1);

        return results;
    }

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
    /// <exception cref="System.IO.IOException">Reading <paramref name="stream"/> failed.</exception>
    /// <exception cref="System.ObjectDisposedException"><paramref name="stream"/> has been disposed.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    public static PreflightResult Validate(Stream stream, PdfConformance conformance)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = PdfReader.Open(stream);
        return Validate(reader, conformance);
    }

    /// <summary>Validates an already-opened <paramref name="reader"/> against <paramref name="conformance"/>.</summary>
    /// <remarks>
    /// <para>
    /// <strong>Internal deliberately, not by oversight.</strong> This overload's signature names
    /// <see cref="PdfDocumentReader"/>, a type belonging to <c>VellumPdf.Reader</c>, which is a
    /// Preview package whose public API is intentionally left unlocked so it can change during the
    /// v2.1 structural-reader work. Exposing it from <c>VellumPdf.Conformance</c> — Stable as of
    /// 2.0, with every entry recorded in <c>PublicAPI.Shipped.txt</c> — would freeze a Stable
    /// signature against a type that is expected to move, so the first rename or reshape in Reader
    /// would be both an <c>RS0017</c> build break here and a hard break for anyone calling it. The
    /// two commitments are incompatible, so this one is withheld until Reader graduates.
    /// </para>
    /// <para>
    /// <see cref="Validate(byte[], PdfConformance)"/> and
    /// <see cref="Validate(System.IO.Stream, PdfConformance)"/> are the public surface; both open a
    /// fresh reader per call. The only capability lost is reusing an already-open reader across
    /// validations, which is worth revisiting once Reader is Stable.
    /// </para>
    /// <para>
    /// The caller retains ownership of <paramref name="reader"/>; it is not disposed here.
    /// <see cref="PdfDocumentReader"/> is not thread-safe (it populates an unsynchronized object
    /// cache), so a single reader must not be validated from multiple threads concurrently. The
    /// public overloads open a fresh reader per call and are safe to invoke concurrently.
    /// </para>
    /// </remarks>
    /// <exception cref="System.ArgumentNullException"><paramref name="reader"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">A rule encountered a reader feature that is not yet
    /// supported; unlike other rule failures this is not captured as a finding but propagates to the caller.</exception>
    internal static PreflightResult Validate(PdfDocumentReader reader, PdfConformance conformance)
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
