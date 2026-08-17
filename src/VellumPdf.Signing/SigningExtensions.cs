// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Document;

namespace VellumPdf.Signing;

/// <summary>
/// Extension methods that add PAdES/PKCS#7 digital-signature support to
/// <see cref="VellumPdf.Document.PdfDocument"/> and <see cref="VellumPdf.Layout.Document"/>.
/// Keeping signing in this optional package preserves the zero-dependency guarantee of
/// <c>VellumPdf.Kernel</c> and <c>VellumPdf.Layout</c>.
/// </summary>
public static class SigningExtensions
{
    /// <summary>
    /// Signs this document and writes a PAdES/PKCS#7-signed PDF to <paramref name="output"/>.
    ///
    /// <para>
    /// The signing process:
    /// <list type="number">
    ///   <item>Build the complete PDF (to an in-memory buffer) with an invisible AcroForm
    ///     signature field and placeholder <c>/ByteRange</c> / <c>/Contents</c> values.</item>
    ///   <item>Locate the <c>/Contents</c> placeholder hex token and compute the real
    ///     ByteRange (the two contiguous byte ranges that exclude the hex token).</item>
    ///   <item>Patch <c>/ByteRange</c> in-place with the real offsets.</item>
    ///   <item>Compute a detached SHA-256 CMS signature over the signed content
    ///     (bytes selected by the ByteRange).</item>
    ///   <item>Hex-encode the DER signature and patch <c>/Contents</c> in-place.</item>
    ///   <item>Apply post-processing for B-LT/B-LTA levels (DSS, archive timestamp).</item>
    ///   <item>Write the result to <paramref name="output"/>.</item>
    /// </list>
    /// </para>
    ///
    /// <para>Encryption and signing are mutually exclusive; throws
    /// <see cref="NotSupportedException"/> when <see cref="PdfDocument.Encrypt"/> has been called.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="doc"/>, <paramref name="output"/>, or
    /// <paramref name="settings"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key
    /// and neither <see cref="PdfSignatureSettings.ExternalPrivateKey"/> nor
    /// <see cref="PdfSignatureSettings.ExternalSigner"/> is set, or when the chosen
    /// <see cref="PadesLevel"/> requires a client that is not set.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when encryption has already been configured on the document, or when
    /// <see cref="PdfSignatureSettings.ExternalSigner"/> is set (it requires <c>SignAsync</c>).
    /// </exception>
    public static void Sign(this PdfDocument doc, Stream output, PdfSignatureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSigningKeyPresent(settings);
        ValidateCertificateSerial(settings);
        ValidateNoExternalSignerForSyncSign(settings);
        ValidateLevel(settings);

        // Resolve signing time once so /M (written by the Kernel) and the CMS
        // Pkcs9SigningTime attribute (written by PdfCmsSigner) share the same value.
        var effectiveSettings = ResolveSigningTime(settings);

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = doc.PrepareForSigning(options);
        SignCore(unsignedBytes, effectiveSettings, output);
    }

    /// <summary>
    /// Asynchronously signs this document and writes a PAdES/PKCS#7-signed PDF to
    /// <paramref name="output"/>. Mirrors <see cref="Sign(PdfDocument, Stream, PdfSignatureSettings)"/>,
    /// but the CPU-bound document build runs on a thread-pool thread via
    /// <see cref="Task.Run(Action)"/>, and any PAdES B-T/B-LT/B-LTA network calls (TSA
    /// timestamping, OCSP/CRL fetches) are awaited instead of blocking the calling thread.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="doc"/>, <paramref name="output"/>, or
    /// <paramref name="settings"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key
    /// and <see cref="PdfSignatureSettings.ExternalPrivateKey"/> is not set, or when the chosen
    /// <see cref="PadesLevel"/> requires a client that is not set.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when encryption has already been configured on the document.
    /// </exception>
    // RS0026 flags multiple overloads with optional parameters as a future-ambiguity risk;
    // PdfDocument and Layout.Document share no implicit conversion, so overload resolution
    // can never be ambiguous between these two extension methods.
#pragma warning disable RS0026
    public static async Task SignAsync(this PdfDocument doc, Stream output, PdfSignatureSettings settings, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSigningKeyPresent(settings);
        ValidateCertificateSerial(settings);
        ValidateLevel(settings);

        var effectiveSettings = ResolveSigningTime(settings);

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = await Task.Run(() => doc.PrepareForSigning(options), cancellationToken).ConfigureAwait(false);
        await SignCoreAsync(unsignedBytes, effectiveSettings, output, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Renders this layout document and writes a PAdES/PKCS#7-signed PDF to
    /// <paramref name="output"/>.
    ///
    /// <para>Encryption and signing are mutually exclusive; throws
    /// <see cref="NotSupportedException"/> when <see cref="VellumPdf.Layout.Document.Encrypt"/>
    /// has been called.</para>
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="doc"/>, <paramref name="output"/>, or
    /// <paramref name="settings"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key
    /// and neither <see cref="PdfSignatureSettings.ExternalPrivateKey"/> nor
    /// <see cref="PdfSignatureSettings.ExternalSigner"/> is set, or when the chosen
    /// <see cref="PadesLevel"/> requires a client that is not set.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when encryption has already been configured on the document, or when
    /// <see cref="PdfSignatureSettings.ExternalSigner"/> is set (it requires <c>SignAsync</c>).
    /// </exception>
    public static void Sign(this VellumPdf.Layout.Document doc, Stream output, PdfSignatureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSigningKeyPresent(settings);
        ValidateCertificateSerial(settings);
        ValidateNoExternalSignerForSyncSign(settings);
        ValidateLevel(settings);

        // Resolve signing time once so /M (written by the Kernel) and the CMS
        // Pkcs9SigningTime attribute (written by PdfCmsSigner) share the same value.
        var effectiveSettings = ResolveSigningTime(settings);

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = doc.PrepareForSigning(options);
        SignCore(unsignedBytes, effectiveSettings, output);
    }

    /// <summary>
    /// Asynchronously renders this layout document and writes a PAdES/PKCS#7-signed PDF to
    /// <paramref name="output"/>. Mirrors
    /// <see cref="Sign(VellumPdf.Layout.Document, Stream, PdfSignatureSettings)"/>, but the
    /// CPU-bound layout and document build run on a thread-pool thread via
    /// <see cref="Task.Run(Action)"/>, and any PAdES B-T/B-LT/B-LTA network calls (TSA
    /// timestamping, OCSP/CRL fetches) are awaited instead of blocking the calling thread.
    /// </summary>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="doc"/>, <paramref name="output"/>, or
    /// <paramref name="settings"/> is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key
    /// and none of <see cref="PdfSignatureSettings.ExternalPrivateKey"/> or
    /// <see cref="PdfSignatureSettings.ExternalSigner"/> is set, or when the chosen
    /// <see cref="PadesLevel"/> requires a client that is not set.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when encryption has already been configured on the document.
    /// </exception>
#pragma warning disable RS0026
    public static async Task SignAsync(this VellumPdf.Layout.Document doc, Stream output, PdfSignatureSettings settings, CancellationToken cancellationToken = default)
#pragma warning restore RS0026
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        ValidateSigningKeyPresent(settings);
        ValidateCertificateSerial(settings);
        ValidateLevel(settings);

        var effectiveSettings = ResolveSigningTime(settings);

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = await Task.Run(() => doc.PrepareForSigning(options), cancellationToken).ConfigureAwait(false);
        await SignCoreAsync(unsignedBytes, effectiveSettings, output, cancellationToken).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates that a signing key is available: attached to
    /// <see cref="PdfSignatureSettings.Certificate"/>, or supplied separately via
    /// <see cref="PdfSignatureSettings.ExternalPrivateKey"/> or
    /// <see cref="PdfSignatureSettings.ExternalSigner"/>. Throws
    /// <see cref="ArgumentException"/> when none is present.
    /// </summary>
    private static void ValidateSigningKeyPresent(PdfSignatureSettings settings)
    {
        if (!settings.Certificate.HasPrivateKey
            && settings.ExternalPrivateKey is null
            && settings.ExternalSigner is null)
            throw new ArgumentException(
                "The signing certificate must include a private key, or " +
                "PdfSignatureSettings.ExternalPrivateKey or PdfSignatureSettings.ExternalSigner " +
                "must be set.", nameof(settings));
    }

    /// <summary>
    /// Rejects a certificate whose serial number is not minimally DER-encoded, before
    /// <see cref="SignedCms"/> is reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// .NET's X.509 parser accepts a serial carrying a redundant leading pad byte, but every DER
    /// encoder rejects it — including the BCL's own <c>IssuerAndSerialNumberAsn.Encode</c>, which
    /// <see cref="SignedCms.ComputeSignature(CmsSigner)"/> calls while building the
    /// <c>SignerInfo</c>. Left alone, that surfaces as <c>ArgumentException: The first 9 bits of
    /// the integer value all have the same value</c> from deep inside the BCL, which says nothing
    /// about the certificate or what to do about it (issue #167).
    /// </para>
    /// <para>
    /// The serial cannot be normalized on the in-process path: the encoding happens inside
    /// <see cref="SignedCms"/>, from the <see cref="X509Certificate2"/> itself, so there is nothing
    /// for this library to rewrite. Re-issuing the certificate is the only real fix, so the failure
    /// names that.
    /// </para>
    /// <para>
    /// <strong>Every signing path is rejected, including <see cref="IExternalSigner"/>.</strong> An
    /// earlier version of this check ran only on the in-process path and its message recommended
    /// the external-signer path as a way through, because <see cref="ExternalSignerCms"/> writes
    /// the <c>SignerInfo</c> itself and normalizes the serial on the way, and the result verifies
    /// under <see cref="SignedCms.CheckSignature(bool)"/>. That recommendation was wrong. The
    /// normalized <c>SignerInfo.IssuerAndSerialNumber</c> then no longer matches the raw serial in
    /// the certificate carried in <c>SignedData.certificates</c>, and a verifier that resolves the
    /// signer by comparing those DER bytes cannot find it. Submitting such a signature to the EU
    /// DSS validator returns <c>noSignatureFound</c> — no signature, no certificate — while an
    /// otherwise identical document signed the same way with a minimally-encoded serial is found
    /// and reported as PAdES-BES. Producing a signature that .NET accepts and a conformant PAdES
    /// validator cannot even locate is worse than refusing to sign, so this path refuses too.
    /// </para>
    /// <para>
    /// <strong>Reachable on Windows only.</strong> Whether a certificate with such a serial can be
    /// loaded at all is platform-dependent: Windows accepts it, while Linux's OpenSSL-backed parser
    /// rejects it as <c>ASN1 corrupted data</c> before an <see cref="X509Certificate2"/> exists. So
    /// on non-Windows platforms this check cannot fire — the certificate never gets far enough to
    /// be passed in. The guard is kept unconditional rather than platform-gated because the cost is
    /// one span comparison and the alternative is a platform-specific code path guarding against a
    /// platform-specific parser behaviour, which is harder to reason about than the check itself.
    /// </para>
    /// </remarks>
    private static void ValidateCertificateSerial(PdfSignatureSettings settings)
    {
        if (Asn1SerialNumber.IsMinimal(settings.Certificate.SerialNumberBytes.Span))
            return;

        throw new ArgumentException(
            "settings.Certificate has a serial number that is not minimally DER-encoded: its "
            + $"content octets are 0x{Convert.ToHexString(settings.Certificate.SerialNumberBytes.Span)}, "
            + "which carries a redundant leading pad byte. ITU-T X.690 §8.3.2 requires the shortest "
            + "two's-complement encoding, so a CMS SignerInfo cannot identify this certificate — "
            + ".NET's X.509 parser tolerates the encoding when reading, but every DER encoder "
            + "rejects it when writing. The certificate is mis-issued and needs re-issuing by its "
            + "CA. Normalizing the serial is not a workaround: the SignerInfo would then no longer "
            + "match the certificate embedded alongside it, and a PAdES validator that resolves the "
            + "signer by comparing those bytes finds no signature at all.",
            nameof(settings));
    }

    /// <summary>
    /// Validates that <see cref="PdfSignatureSettings.ExternalSigner"/>, which requires an
    /// async signing call, is not used with the synchronous <c>Sign</c> overloads. Throws
    /// <see cref="NotSupportedException"/> when it is set.
    /// </summary>
    private static void ValidateNoExternalSignerForSyncSign(PdfSignatureSettings settings)
    {
        if (settings.ExternalSigner is not null)
            throw new NotSupportedException(
                "PdfSignatureSettings.ExternalSigner requires an async signing call and is " +
                "not supported by the synchronous Sign overloads. Use SignAsync instead.");
    }

    /// <summary>
    /// Validates that the chosen <see cref="PadesLevel"/> has all required clients configured.
    /// Throws <see cref="ArgumentException"/> on any violation.
    /// </summary>
    private static void ValidateLevel(PdfSignatureSettings settings)
    {
        if (settings.Level >= PadesLevel.B_T && settings.TimestampClient is null)
            throw new ArgumentException(
                "PAdES B-T/B-LT/B-LTA require a signature timestamp; set PdfSignatureSettings.TimestampClient.",
                nameof(settings));

        if (settings.Level >= PadesLevel.B_LT && settings.RevocationClient is null)
            throw new ArgumentException(
                "PAdES B-LT/B-LTA require PdfSignatureSettings.RevocationClient to fetch OCSP/CRL evidence.",
                nameof(settings));
    }

    /// <summary>
    /// Returns <paramref name="settings"/> unchanged when <c>SigningTime</c> is already set,
    /// or a copy with <c>SigningTime = UtcNow</c> otherwise. All other properties are preserved.
    /// </summary>
    private static PdfSignatureSettings ResolveSigningTime(PdfSignatureSettings settings)
        => settings.SigningTime is null
            ? new PdfSignatureSettings
            {
                Certificate = settings.Certificate,
                ExternalPrivateKey = settings.ExternalPrivateKey,
                ExternalSigner = settings.ExternalSigner,
                SignerName = settings.SignerName,
                Reason = settings.Reason,
                Location = settings.Location,
                ContactInfo = settings.ContactInfo,
                SigningTime = DateTimeOffset.UtcNow,
                EstimatedSignatureSizeBytes = settings.EstimatedSignatureSizeBytes,
                SubFilter = settings.SubFilter,
                TimestampClient = settings.TimestampClient,
                SignaturePage = settings.SignaturePage,
                Level = settings.Level,
                RevocationClient = settings.RevocationClient,
            }
            : settings;

    /// <summary>
    /// Core signing pipeline shared by both public <c>Sign</c> overloads.
    /// Signs the unsigned placeholder bytes and writes the final (possibly multi-revision)
    /// PDF to <paramref name="output"/>, applying DSS and archive-timestamp post-processing
    /// according to <see cref="PdfSignatureSettings.Level"/>.
    /// </summary>
    private static void SignCore(byte[] unsignedBytes, PdfSignatureSettings settings, Stream output)
    {
        if (settings.Level >= PadesLevel.B_LT)
        {
            // Buffer into a MemoryStream so post-processing can work on the full byte array.
            using var ms = new MemoryStream();
            PdfCmsSigner.Sign(unsignedBytes, settings, ms);
            var signed = ms.ToArray();

            // B-LT and B-LTA both require a DSS revision with revocation evidence for the
            // signature and its timestamp. The archive timestamp (B-LTA) must cover this DSS,
            // so it is added afterwards.
            signed = DssBuilder.AddLongTermValidation(signed, settings.RevocationClient!);

            if (settings.Level == PadesLevel.B_LTA)
            {
                signed = ArchiveTimestampBuilder.AddArchiveTimestamp(signed, settings.TimestampClient!);

                // ETSI B-LTA: add a final cumulative DSS so the archive timestamp's own TSA
                // chain + revocation (and a /VRI for the DocTimeStamp token) are embedded.
                // DssBuilder enumerates every signature field, so the just-added DocTimeStamp
                // is now included alongside the original signature.
                signed = DssBuilder.AddLongTermValidation(signed, settings.RevocationClient!);
            }

            output.Write(signed, 0, signed.Length);
        }
        else
        {
            // B-B and B-T: write directly to the caller's stream (no extra buffering).
            PdfCmsSigner.Sign(unsignedBytes, settings, output);
        }
    }

    /// <summary>
    /// Asynchronous counterpart of <see cref="SignCore"/>, awaiting the TSA/OCSP/CRL network
    /// calls made by <see cref="PdfCmsSigner"/>, <see cref="DssBuilder"/>, and
    /// <see cref="ArchiveTimestampBuilder"/> for PAdES B-T/B-LT/B-LTA levels.
    /// </summary>
    private static async Task SignCoreAsync(byte[] unsignedBytes, PdfSignatureSettings settings, Stream output, CancellationToken cancellationToken)
    {
        if (settings.Level >= PadesLevel.B_LT)
        {
            using var ms = new MemoryStream();
            await PdfCmsSigner.SignAsync(unsignedBytes, settings, ms, cancellationToken).ConfigureAwait(false);
            var signed = ms.ToArray();

            signed = await DssBuilder.AddLongTermValidationAsync(signed, settings.RevocationClient!, cancellationToken).ConfigureAwait(false);

            if (settings.Level == PadesLevel.B_LTA)
            {
                signed = await ArchiveTimestampBuilder.AddArchiveTimestampAsync(signed, settings.TimestampClient!, cancellationToken).ConfigureAwait(false);

                // ETSI B-LTA: add a final cumulative DSS so the archive timestamp's own TSA
                // chain + revocation (and a /VRI for the DocTimeStamp token) are embedded.
                signed = await DssBuilder.AddLongTermValidationAsync(signed, settings.RevocationClient!, cancellationToken).ConfigureAwait(false);
            }

            await output.WriteAsync(signed, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // B-B and B-T: write directly to the caller's stream (no extra buffering).
            await PdfCmsSigner.SignAsync(unsignedBytes, settings, output, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SignaturePlaceholderOptions ToPlaceholderOptions(PdfSignatureSettings settings)
        => new()
        {
            SubFilter = settings.SubFilter,
            EstimatedSignatureSizeBytes = PdfCmsSigner.EffectiveReserve(settings),
            SignerName = settings.SignerName,
            Reason = settings.Reason,
            Location = settings.Location,
            ContactInfo = settings.ContactInfo,
            SigningTime = settings.SigningTime,
            SignaturePage = settings.SignaturePage,
        };
}
