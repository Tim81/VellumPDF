// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace VellumPdf.Signing;

/// <summary>
/// Default <see cref="IRevocationClient"/> that fetches revocation evidence over HTTP:
/// an OCSP response from the certificate's Authority Information Access (AIA) responder,
/// and a CRL from its CRL Distribution Points (CDP). The DER bytes are returned verbatim
/// for embedding in a PAdES B-LT Document Security Store (DSS).
/// </summary>
/// <remarks>
/// This client is resilient: if a certificate publishes no OCSP responder URL, OCSP is
/// skipped; if it publishes no usable HTTP CRL distribution point, the CRL is skipped;
/// and a network or HTTP error fetching one kind of evidence does not throw — the client
/// returns whatever it managed to obtain (possibly an empty <see cref="RevocationData"/>).
/// </remarks>
public sealed class HttpRevocationClient : IRevocationClient
{
    private static readonly HttpClient s_sharedClient = new();

    /// <summary>The default per-request timeout when none is supplied.</summary>
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);

    // OID 1.3.14.3.2.26 — SHA-1, the de-facto hash algorithm for the OCSP CertID.
    private const string Sha1Oid = "1.3.14.3.2.26";

    // OID 1.3.6.1.5.5.7.1.1 — Authority Information Access extension.
    private const string AuthorityInformationAccessOid = "1.3.6.1.5.5.7.1.1";

    // OID 2.5.29.31 — CRL Distribution Points extension.
    private const string CrlDistributionPointsOid = "2.5.29.31";

    private readonly HttpClient _httpClient;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Creates a client backed by a process-wide shared <see cref="HttpClient"/> and the
    /// default per-request timeout.
    /// </summary>
    public HttpRevocationClient()
        : this(s_sharedClient, s_defaultTimeout)
    {
    }

    /// <summary>
    /// Creates a client backed by the supplied <see cref="HttpClient"/> and timeout.
    /// </summary>
    /// <param name="httpClient">The HTTP client to use for OCSP and CRL requests.</param>
    /// <param name="timeout">The per-request timeout. Must be a positive duration.</param>
    /// <exception cref="ArgumentNullException"><paramref name="httpClient"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="timeout"/> is not positive.</exception>
    public HttpRevocationClient(HttpClient httpClient, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be a positive duration.");
        _httpClient = httpClient;
        _timeout = timeout;
    }

    /// <inheritdoc/>
    public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        ArgumentNullException.ThrowIfNull(issuer);

        var ocsp = TryFetchOcsp(certificate, issuer);
        var crl = TryFetchCrl(certificate);

        return new RevocationData { Ocsp = ocsp, Crl = crl };
    }

    // ── OCSP ────────────────────────────────────────────────────────────────────

    private ReadOnlyMemory<byte>? TryFetchOcsp(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        Uri? responder = GetOcspResponderUri(certificate);
        if (responder is null)
            return null;

        try
        {
            var requestDer = BuildOcspRequest(certificate, issuer);

            using var content = new ByteArrayContent(requestDer);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/ocsp-request");

            using var httpReq = new HttpRequestMessage(HttpMethod.Post, responder) { Content = content };
            httpReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/ocsp-response"));

            byte[] responseBytes = Send(httpReq);
            // Embed only a structurally valid, successful OCSPResponse — never an error
            // envelope (tryLater/unauthorized/…) or an HTML error page that happens to
            // start with a SEQUENCE tag.
            if (responseBytes.Length == 0 || !IsSuccessfulOcspResponse(responseBytes))
                return null;

            return responseBytes;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the first OCSP responder URI from the certificate's Authority Information
    /// Access extension (OID 1.3.6.1.5.5.7.1.1), or <see langword="null"/> if absent.
    /// </summary>
    private static Uri? GetOcspResponderUri(X509Certificate2 certificate)
    {
        foreach (var ext in certificate.Extensions)
        {
            if (ext.Oid?.Value != AuthorityInformationAccessOid)
                continue;

            X509AuthorityInformationAccessExtension aia = ext as X509AuthorityInformationAccessExtension
                ?? new X509AuthorityInformationAccessExtension(ext.RawData, ext.Critical);

            foreach (string uri in aia.EnumerateOcspUris())
            {
                if (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) &&
                    (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                {
                    return parsed;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Builds a DER-encoded OCSP <c>OCSPRequest</c> for the certificate, using a SHA-1 CertID
    /// over the issuer's distinguished name and public key. No signature, requestor name, or
    /// extensions are included.
    /// </summary>
    private static byte[] BuildOcspRequest(X509Certificate2 certificate, X509Certificate2 issuer)
    {
        byte[] issuerNameHash = SHA1.HashData(issuer.SubjectName.RawData);
        byte[] issuerKeyHash = SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData);
        byte[] serial = certificate.SerialNumberBytes.ToArray();

        var writer = new AsnWriter(AsnEncodingRules.DER);

        // OCSPRequest ::= SEQUENCE { tbsRequest TBSRequest }
        using (writer.PushSequence())
        {
            // TBSRequest ::= SEQUENCE { requestList SEQUENCE OF Request }
            using (writer.PushSequence())
            {
                // requestList
                using (writer.PushSequence())
                {
                    // Request ::= SEQUENCE { reqCert CertID }
                    using (writer.PushSequence())
                    {
                        // CertID ::= SEQUENCE { hashAlgorithm, issuerNameHash, issuerKeyHash, serialNumber }
                        using (writer.PushSequence())
                        {
                            // AlgorithmIdentifier { SHA-1, NULL }
                            using (writer.PushSequence())
                            {
                                writer.WriteObjectIdentifier(Sha1Oid);
                                writer.WriteNull();
                            }
                            writer.WriteOctetString(issuerNameHash);
                            writer.WriteOctetString(issuerKeyHash);
                            WriteSerialNumber(writer, serial);
                        }
                    }
                }
            }
        }

        return writer.Encode();
    }

    /// <summary>
    /// Writes the certificate serial number (big-endian, signed two's-complement as carried
    /// in the certificate) as an ASN.1 INTEGER.
    /// </summary>
    private static void WriteSerialNumber(AsnWriter writer, byte[] serial)
    {
        if (serial.Length == 0)
        {
            writer.WriteInteger(0);
            return;
        }

        writer.WriteInteger(serial);
    }

    // ── CRL ─────────────────────────────────────────────────────────────────────

    private ReadOnlyMemory<byte>? TryFetchCrl(X509Certificate2 certificate)
    {
        foreach (Uri cdp in GetCrlDistributionUris(certificate))
        {
            try
            {
                using var httpReq = new HttpRequestMessage(HttpMethod.Get, cdp);
                byte[] body = Send(httpReq);

                // Accept only a body that parses as a DER CertificateList; skip anything
                // else (PEM, an HTML error page, or a misrouted OCSP response).
                if (IsCertificateList(body))
                    return body;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                // Try the next distribution point.
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts HTTP(S) CRL distribution-point URIs from the certificate's CRL Distribution
    /// Points extension (OID 2.5.29.31). Returns an empty sequence if absent or unparseable.
    /// </summary>
    private static IEnumerable<Uri> GetCrlDistributionUris(X509Certificate2 certificate)
    {
        X509Extension? ext = null;
        foreach (var e in certificate.Extensions)
        {
            if (e.Oid?.Value == CrlDistributionPointsOid)
            {
                ext = e;
                break;
            }
        }

        if (ext is null)
            return [];

        var uris = new List<Uri>();
        try
        {
            ParseCrlDistributionPoints(ext.RawData, uris);
        }
        catch (AsnContentException)
        {
            return [];
        }

        return uris;
    }

    /// <summary>
    /// Parses a <c>CRLDistributionPoints ::= SEQUENCE OF DistributionPoint</c> structure,
    /// pulling HTTP(S) URIs from each <c>distributionPoint &gt; fullName &gt; GeneralName [6]</c>.
    /// </summary>
    private static void ParseCrlDistributionPoints(byte[] rawData, List<Uri> uris)
    {
        // GeneralName URI is context tag [6] (IMPLICIT IA5String).
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
        // DistributionPointName.fullName is context tag [0]; distributionPoint is context tag [0].
        var distributionPointTag = new Asn1Tag(TagClass.ContextSpecific, 0);
        var fullNameTag = new Asn1Tag(TagClass.ContextSpecific, 0);

        var outer = new AsnReader(rawData, AsnEncodingRules.DER);
        var seqOfDps = outer.ReadSequence();
        outer.ThrowIfNotEmpty();

        while (seqOfDps.HasData)
        {
            var dp = seqOfDps.ReadSequence();

            if (!dp.HasData || !dp.PeekTag().HasSameClassAndValue(distributionPointTag))
                continue;

            // distributionPoint [0] DistributionPointName
            var dpName = dp.ReadSequence(distributionPointTag);

            if (!dpName.HasData || !dpName.PeekTag().HasSameClassAndValue(fullNameTag))
                continue;

            // fullName [0] GeneralNames
            var fullName = dpName.ReadSequence(fullNameTag);
            while (fullName.HasData)
            {
                if (fullName.PeekTag().HasSameClassAndValue(uriTag))
                {
                    string value = fullName.ReadCharacterString(UniversalTagNumber.IA5String, uriTag);
                    if (Uri.TryCreate(value, UriKind.Absolute, out var parsed) &&
                        (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps))
                    {
                        uris.Add(parsed);
                    }
                }
                else
                {
                    fullName.ReadEncodedValue();
                }
            }
        }
    }

    // ── Response validation ──────────────────────────────────────────────────────

    /// <summary>
    /// Returns true when <paramref name="der"/> is a well-formed DER <c>OCSPResponse</c> whose
    /// <c>responseStatus</c> ENUMERATED is <c>successful(0)</c>. Reading the raw enumerated
    /// octets (rather than mapping to an enum) avoids any surprise on an unexpected status
    /// value. Does not verify the response signature or per-certificate status (that is the
    /// validator's responsibility).
    /// </summary>
    private static bool IsSuccessfulOcspResponse(byte[] der)
    {
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var response = reader.ReadSequence();
            var status = response.ReadEnumeratedBytes();
            return status.Length == 1 && status.Span[0] == 0x00;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true when <paramref name="der"/> parses as a DER <c>CertificateList</c>
    /// (a SEQUENCE whose first element is the <c>tbsCertList</c> SEQUENCE). This rejects an
    /// OCSP response or other DER object that is not a CRL.
    /// </summary>
    private static bool IsCertificateList(byte[] der)
    {
        if (der.Length == 0)
            return false;
        try
        {
            var reader = new AsnReader(der, AsnEncodingRules.DER);
            var certList = reader.ReadSequence();
            certList.ReadSequence(); // tbsCertList — must be a SEQUENCE
            return true;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    // ── HTTP ────────────────────────────────────────────────────────────────────

    private byte[] Send(HttpRequestMessage request)
    {
        using var cts = new CancellationTokenSource(_timeout);
        using HttpResponseMessage resp = _httpClient.Send(request, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Revocation request to {request.RequestUri} failed with HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        }

        return resp.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
    }
}
