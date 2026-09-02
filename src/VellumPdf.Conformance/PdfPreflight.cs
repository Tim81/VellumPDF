// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Conformance.Rules;
using VellumPdf.Conformance.Rules.Metadata;
using VellumPdf.Core;
using VellumPdf.Encryption;
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
    /// <exception cref="Reader.PdfPasswordException">The PDF is encrypted and its empty user password
    /// does not authenticate. This overload opens the document with no password; use
    /// <see cref="DetectClaimedProfiles(byte[], string)"/> for a document that needs one.</exception>
    public static IReadOnlyList<PdfConformance> DetectClaimedProfiles(byte[] bytes)
        => DetectClaimedProfiles(bytes, password: null);

    /// <summary>
    /// Reads the XMP /Metadata stream from the document catalog and returns the
    /// conformance profiles the document claims via <c>pdfaid:part</c>/<c>pdfaid:conformance</c>
    /// and <c>pdfuaid:part</c>. Returns an empty list when the catalog has no /Metadata or the
    /// document makes no PDF/A or PDF/UA claim.
    /// </summary>
    /// <param name="bytes">The PDF file bytes.</param>
    /// <param name="password">
    /// The password to open the document with, or <see langword="null"/> for a document that uses
    /// none. <see langword="null"/> and <see cref="string.Empty"/> are equivalent: both mean "the
    /// empty user password".
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    /// <exception cref="Reader.PdfPasswordException">The PDF is encrypted and <paramref name="password"/>
    /// does not authenticate as either the owner or the user password.</exception>
    public static IReadOnlyList<PdfConformance> DetectClaimedProfiles(byte[] bytes, string? password)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = password });
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
    /// <exception cref="Reader.PdfPasswordException">The PDF is encrypted and its empty user password
    /// does not authenticate. This overload opens the document with no password; use
    /// <see cref="DetectClaimedProfiles(Stream, string)"/> for a document that needs one.</exception>
    public static IReadOnlyList<PdfConformance> DetectClaimedProfiles(Stream stream)
        => DetectClaimedProfiles(stream, password: null);

    /// <summary>
    /// Reads the XMP /Metadata stream from the document catalog and returns the
    /// conformance profiles the document claims via <c>pdfaid:part</c>/<c>pdfaid:conformance</c>
    /// and <c>pdfuaid:part</c>. Returns an empty list when the catalog has no /Metadata or the
    /// document makes no PDF/A or PDF/UA claim.
    /// </summary>
    /// <param name="stream">The PDF data.</param>
    /// <param name="password">
    /// The password to open the document with, or <see langword="null"/> for a document that uses
    /// none. <see langword="null"/> and <see cref="string.Empty"/> are equivalent: both mean "the
    /// empty user password".
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="System.IO.IOException">Reading <paramref name="stream"/> failed.</exception>
    /// <exception cref="System.ObjectDisposedException"><paramref name="stream"/> has been disposed.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    /// <exception cref="Reader.PdfPasswordException">The PDF is encrypted and <paramref name="password"/>
    /// does not authenticate as either the owner or the user password.</exception>
    public static IReadOnlyList<PdfConformance> DetectClaimedProfiles(Stream stream, string? password)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = PdfReader.Open(stream, new PdfReaderOptions { Password = password });
        return DetectClaimedProfiles(reader);
    }

    // A document whose /StmF resolves to a crypt filter method this handler does not implement has no
    // decodable stream in it: not the content, not the metadata, not the object streams that hold
    // most of its objects. Rules would each hit that separately and report it as a finding against
    // whatever clause they happen to cover, so a crypt-filter problem came out as a FAIL stamped with
    // output-intent and transparency clauses the file never violated. "Cannot evaluate" is the honest
    // answer, and it is the answer /Adobe.PubSec already gets.
    //
    // Both entry points need it. Validate is the obvious one; auto-detection is the one a caller
    // reaches first, and for a long time only Validate had it.
    private static void ThrowIfNoStreamCanBeDecoded(PdfDocumentReader reader)
    {
        if (reader.Encryption?.StreamCipher == PdfCipherAlgorithm.Unsupported)
        {
            throw new UnsupportedPdfFeatureException(
                "The document's /StmF crypt filter names a /CF entry it does not define, or a method "
                + "this library does not implement, so none of its streams can be decoded.");
        }
    }

    private static IReadOnlyList<PdfConformance> DetectClaimedProfiles(PdfDocumentReader reader)
    {
        var metaRef = reader.Catalog.Get(_metadataName);
        if (metaRef is not PdfIndirectReference r)
        {
            // No XMP to read. On an ordinary document that is simply "no claim"; on one whose streams
            // cannot be decoded at all it is the wrong answer, because it sends the caller to -p and
            // that path then fails for the real reason. Say the real reason here.
            ThrowIfNoStreamCanBeDecoded(reader);
            return [];
        }

        var parsedStream = reader.ResolveStream(r);
        if (parsedStream is null)
        {
            ThrowIfNoStreamCanBeDecoded(reader);
            return [];
        }

        // Auto-detection needs exactly one stream — the XMP packet — so the "cannot evaluate" answer
        // belongs to THAT stream, not to the document-wide /StmF. A document with /EncryptMetadata
        // false leaves its metadata in the clear whatever /StmF resolved to (the arrangement
        // --cleartext-metadata produces, which two fixtures carry), and gating on /StmF refused such a
        // file a claim it could perfectly well have read. Letting the decode decide is exact about the
        // OUTCOME: it succeeds where the metadata is readable and throws where it is not.
        //
        // The `when` below only decides which exception NAME the caller sees, and it is not exhaustive:
        // a metadata stream carrying its own /Crypt specifier that names an unimplemented /CFM throws
        // while the document-wide /StmF is perfectly supported, and that one still surfaces as
        // InvalidDataException. Refusing to evaluate is right either way; the label is imprecise in a
        // case no producer emits.
        byte[]? bytes;
        try
        {
            bytes = reader.GetDecodedStreamData(parsedStream);
        }
        catch (InvalidDataException) when (reader.Encryption?.StreamCipher == PdfCipherAlgorithm.Unsupported)
        {
            throw new UnsupportedPdfFeatureException(
                "The document's /StmF crypt filter names a /CF entry it does not define, or a method "
                + "this library does not implement, so its metadata stream cannot be decoded.");
        }

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
    /// <remarks>
    /// An encrypted document is opened with no password — equivalent to
    /// <see cref="PdfReader.Open(byte[])"/> — so this succeeds only for one that needs none, or
    /// whose empty user password is sufficient. Use
    /// <see cref="Validate(byte[], PdfConformance, string)"/> for a document requiring a real
    /// password.
    /// </remarks>
    /// <exception cref="System.ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and its empty user password does not authenticate.</exception>
    public static PreflightResult Validate(byte[] bytes, PdfConformance conformance)
        => Validate(bytes, conformance, password: null);

    /// <summary>Validates the PDF contained in <paramref name="bytes"/> against <paramref name="conformance"/>.</summary>
    /// <param name="bytes">The PDF file bytes.</param>
    /// <param name="conformance">The conformance level to validate against.</param>
    /// <param name="password">
    /// The password to open the document with, or <see langword="null"/> for a document that uses
    /// none. <see langword="null"/> and <see cref="string.Empty"/> are equivalent: both mean "the
    /// empty user password".
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="bytes"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    /// <exception cref="PdfPasswordException"><paramref name="password"/> does not authenticate as
    /// either the owner or the user password.</exception>
    public static PreflightResult Validate(byte[] bytes, PdfConformance conformance, string? password)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        using var reader = PdfReader.Open(bytes, new PdfReaderOptions { Password = password });
        return Validate(reader, conformance);
    }

    /// <summary>Validates the PDF read from <paramref name="stream"/> against <paramref name="conformance"/>.</summary>
    /// <remarks>See <see cref="Validate(byte[], PdfConformance)"/>'s remarks: an encrypted document
    /// is opened with no password. Use <see cref="Validate(Stream, PdfConformance, string)"/> for a
    /// document requiring a real password.</remarks>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="System.IO.IOException">Reading <paramref name="stream"/> failed.</exception>
    /// <exception cref="System.ObjectDisposedException"><paramref name="stream"/> has been disposed.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    /// <exception cref="PdfPasswordException">The PDF is encrypted and its empty user password does not authenticate.</exception>
    public static PreflightResult Validate(Stream stream, PdfConformance conformance)
        => Validate(stream, conformance, password: null);

    /// <summary>Validates the PDF read from <paramref name="stream"/> against <paramref name="conformance"/>.</summary>
    /// <param name="stream">The PDF data.</param>
    /// <param name="conformance">The conformance level to validate against.</param>
    /// <param name="password">
    /// The password to open the document with, or <see langword="null"/> for a document that uses
    /// none. <see langword="null"/> and <see cref="string.Empty"/> are equivalent: both mean "the
    /// empty user password".
    /// </param>
    /// <exception cref="System.ArgumentNullException"><paramref name="stream"/> is null.</exception>
    /// <exception cref="System.NotSupportedException">No rule profile is registered for <paramref name="conformance"/> yet.</exception>
    /// <exception cref="System.IO.InvalidDataException">The input is not a well-formed PDF.</exception>
    /// <exception cref="System.IO.IOException">Reading <paramref name="stream"/> failed.</exception>
    /// <exception cref="System.ObjectDisposedException"><paramref name="stream"/> has been disposed.</exception>
    /// <exception cref="UnsupportedPdfFeatureException">The PDF uses a reader feature that is not yet supported.</exception>
    /// <exception cref="PdfPasswordException"><paramref name="password"/> does not authenticate as
    /// either the owner or the user password.</exception>
    public static PreflightResult Validate(Stream stream, PdfConformance conformance, string? password)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = PdfReader.Open(stream, new PdfReaderOptions { Password = password });
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

        ThrowIfNoStreamCanBeDecoded(reader);

        var assertions = new List<PreflightAssertion>();
        var context = new PreflightContext(reader, conformance, assertions);

        foreach (var rule in rules)
        {
            try
            {
                rule.Evaluate(context);
            }
            catch (Exception ex)
                when (ex is not OutOfMemoryException and not UnsupportedPdfFeatureException and not PdfPasswordException)
            {
                // A single rule throwing on a malformed-but-parseable document must not abort the
                // whole report. Record it as an error finding and continue with the other rules.
                //
                // Since #97, this now also catches InvalidDataException raised mid-rule by a stream
                // whose crypt filter is unsupported (a /StmF, /StrF, or /Crypt /Name that names a
                // /CF entry the document does not define — see CryptFilterResolver and
                // StandardSecurityDecryptor.Decrypt's CryptFilterMethod.Unsupported case). That is
                // deliberate, not an oversight: it is a malformed-/Encrypt-dictionary condition,
                // structurally the same as any other malformed stream a rule might hit (a bad
                // predictor, a truncated body), and this catch already treats those as an error
                // finding rather than a hard failure. It still "fails loudly" in the sense that
                // matters — the caller sees an explicit error finding naming the failure, not
                // silently-wrong (decrypted-as-ciphertext) content — just not by propagating.
                //
                // UnsupportedPdfFeatureException and PdfPasswordException are excluded defensively:
                // today both are only raised at Open (before any rule runs, so before this loop even
                // starts), but should a future lazily-decoded reader feature let a rule raise either,
                // "cannot evaluate" and "wrong password" are both a distinct signal that should
                // propagate to the caller rather than be reported as a conformance violation.
                context.Report(rule.RuleId, rule.Clause, PreflightSeverity.Error,
                    $"Rule evaluation failed: {ex.Message}");
            }
        }

        return new PreflightResult(conformance, assertions);
    }
}
