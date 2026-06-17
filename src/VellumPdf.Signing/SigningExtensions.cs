// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

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
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key,
    /// or when the chosen <see cref="PadesLevel"/> requires a client that is not set.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when encryption has already been configured on the document.
    /// </exception>
    public static void Sign(this PdfDocument doc, Stream output, PdfSignatureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Certificate.HasPrivateKey)
            throw new ArgumentException(
                "The signing certificate must include a private key.", nameof(settings));

        ValidateLevel(settings);

        // Resolve signing time once so /M (written by the Kernel) and the CMS
        // Pkcs9SigningTime attribute (written by PdfCmsSigner) share the same value.
        var effectiveSettings = ResolveSigningTime(settings);

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = doc.PrepareForSigning(options);
        SignCore(unsignedBytes, effectiveSettings, output);
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
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key,
    /// or when the chosen <see cref="PadesLevel"/> requires a client that is not set.
    /// </exception>
    /// <exception cref="NotSupportedException">
    /// Thrown when encryption has already been configured on the document.
    /// </exception>
    public static void Sign(this VellumPdf.Layout.Document doc, Stream output, PdfSignatureSettings settings)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(settings);

        if (!settings.Certificate.HasPrivateKey)
            throw new ArgumentException(
                "The signing certificate must include a private key.", nameof(settings));

        ValidateLevel(settings);

        // Resolve signing time once so /M (written by the Kernel) and the CMS
        // Pkcs9SigningTime attribute (written by PdfCmsSigner) share the same value.
        var effectiveSettings = ResolveSigningTime(settings);

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = doc.PrepareForSigning(options);
        SignCore(unsignedBytes, effectiveSettings, output);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
            var ms = new MemoryStream();
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
