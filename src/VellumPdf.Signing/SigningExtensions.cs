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
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key.
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

        // Resolve signing time once so /M (written by the Kernel) and the CMS
        // Pkcs9SigningTime attribute (written by PdfCmsSigner) share the same value.
        var effectiveSettings = settings.SigningTime is null
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
            }
            : settings;

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = doc.PrepareForSigning(options);
        PdfCmsSigner.Sign(unsignedBytes, effectiveSettings, output);
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
    /// Thrown when the certificate in <paramref name="settings"/> does not include a private key.
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

        // Resolve signing time once so /M (written by the Kernel) and the CMS
        // Pkcs9SigningTime attribute (written by PdfCmsSigner) share the same value.
        var effectiveSettings = settings.SigningTime is null
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
            }
            : settings;

        var options = ToPlaceholderOptions(effectiveSettings);
        var unsignedBytes = doc.PrepareForSigning(options);
        PdfCmsSigner.Sign(unsignedBytes, effectiveSettings, output);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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
        };
}
