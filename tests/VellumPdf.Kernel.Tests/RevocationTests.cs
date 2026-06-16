// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Offline unit tests for <see cref="HttpRevocationClient"/> and the
/// <see cref="IRevocationClient"/> surface used by PAdES B-LT.
/// All tests are fully offline and deterministic — no real network calls.
/// </summary>
public sealed class RevocationTests
{
    private const string AuthorityInformationAccessOid = "1.3.6.1.5.5.7.1.1";
    private const string CrlDistributionPointsOid = "2.5.29.31";

    // Canned response bodies returned by the fake handler.
    private static readonly byte[] s_cannedOcsp = [0x30, 0x03, 0x0A, 0x01, 0x00];
    private static readonly byte[] s_cannedCrl = [0x30, 0x04, 0x02, 0x01, 0x2A];

    // ── Certificate helpers ──────────────────────────────────────────────────────

    private static X509Certificate2 CreateCertificate(
        string subject = "CN=VellumPdf Test Leaf",
        Action<CertificateRequest>? configure = null)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            subject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        configure?.Invoke(req);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    private static X509Certificate2 CreateCertWithAia(string ocspUri)
    {
        return CreateCertificate(configure: req =>
            req.CertificateExtensions.Add(BuildAiaOcspExtension(ocspUri)));
    }

    private static X509Certificate2 CreateCertWithCdp(string crlUri)
    {
        return CreateCertificate(configure: req =>
            req.CertificateExtensions.Add(BuildCdpExtension(crlUri)));
    }

    /// <summary>
    /// Builds an Authority Information Access extension carrying a single
    /// id-ad-ocsp (1.3.6.1.5.5.7.48.1) access description with the given URI.
    /// </summary>
    private static X509Extension BuildAiaOcspExtension(string ocspUri)
    {
        // AuthorityInfoAccessSyntax ::= SEQUENCE OF AccessDescription
        // AccessDescription ::= SEQUENCE { accessMethod OID, accessLocation GeneralName }
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);
        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                writer.WriteObjectIdentifier("1.3.6.1.5.5.7.48.1"); // id-ad-ocsp
                writer.WriteCharacterString(UniversalTagNumber.IA5String, ocspUri, uriTag);
            }
        }

        return new X509Extension(new Oid(AuthorityInformationAccessOid), writer.Encode(), critical: false);
    }

    /// <summary>
    /// Builds a CRL Distribution Points extension with one distribution point whose
    /// fullName is a single GeneralName URI.
    /// </summary>
    private static X509Extension BuildCdpExtension(string crlUri)
    {
        // CRLDistributionPoints ::= SEQUENCE OF DistributionPoint
        // DistributionPoint ::= SEQUENCE { distributionPoint [0] DistributionPointName OPTIONAL ... }
        // DistributionPointName ::= CHOICE { fullName [0] GeneralNames, ... }
        var distributionPointTag = new Asn1Tag(TagClass.ContextSpecific, 0);
        var fullNameTag = new Asn1Tag(TagClass.ContextSpecific, 0);
        var uriTag = new Asn1Tag(TagClass.ContextSpecific, 6);

        var writer = new AsnWriter(AsnEncodingRules.DER);
        using (writer.PushSequence())
        {
            using (writer.PushSequence())
            {
                using (writer.PushSequence(distributionPointTag))
                {
                    using (writer.PushSequence(fullNameTag))
                    {
                        writer.WriteCharacterString(UniversalTagNumber.IA5String, crlUri, uriTag);
                    }
                }
            }
        }

        return new X509Extension(new Oid(CrlDistributionPointsOid), writer.Encode(), critical: false);
    }

    // ── OCSP request DER shape ────────────────────────────────────────────────────

    [Fact]
    public void Ocsp_request_has_correct_content_type_and_certid()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithAia("http://ocsp.example.invalid/respond");

        var handler = new FakeHandler { OcspResponse = s_cannedOcsp };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Equal("application/ocsp-request", handler.OcspRequestContentType);
        Assert.NotNull(handler.OcspRequestBody);

        var (serial, nameHash, keyHash) = DecodeCertId(handler.OcspRequestBody!);

        Assert.Equal(leaf.SerialNumberBytes.ToArray(), serial);
        Assert.Equal(SHA1.HashData(issuer.SubjectName.RawData), nameHash);
        Assert.Equal(SHA1.HashData(issuer.PublicKey.EncodedKeyValue.RawData), keyHash);

        Assert.NotNull(data.Ocsp);
        Assert.Equal(s_cannedOcsp, data.Ocsp!.Value.ToArray());
    }

    [Fact]
    public void Ocsp_posts_to_aia_uri()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithAia("http://ocsp.example.invalid/respond");

        var handler = new FakeHandler { OcspResponse = s_cannedOcsp };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        client.GetRevocationData(leaf, issuer);

        Assert.Equal(new Uri("http://ocsp.example.invalid/respond"), handler.OcspRequestUri);
    }

    [Fact]
    public void No_aia_means_no_ocsp_attempted()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertificate(); // no AIA

        var handler = new FakeHandler { OcspResponse = s_cannedOcsp };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Ocsp);
        Assert.Null(handler.OcspRequestUri);
    }

    // ── CDP parsing ───────────────────────────────────────────────────────────────

    [Fact]
    public void Crl_fetched_from_http_cdp()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithCdp("http://crl.example.invalid/list.crl");

        var handler = new FakeHandler { CrlResponse = s_cannedCrl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.NotNull(data.Crl);
        Assert.Equal(s_cannedCrl, data.Crl!.Value.ToArray());
        Assert.Equal(new Uri("http://crl.example.invalid/list.crl"), handler.CrlRequestUri);
    }

    [Fact]
    public void Non_http_cdp_is_skipped()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithCdp("ldap://ldap.example.invalid/cn=crl");

        var handler = new FakeHandler { CrlResponse = s_cannedCrl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Crl);
        Assert.Null(handler.CrlRequestUri);
    }

    // ── Resilience ────────────────────────────────────────────────────────────────

    [Fact]
    public void Ocsp_failure_does_not_block_crl()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertificate(configure: req =>
        {
            req.CertificateExtensions.Add(BuildAiaOcspExtension("http://ocsp.example.invalid/respond"));
            req.CertificateExtensions.Add(BuildCdpExtension("http://crl.example.invalid/list.crl"));
        });

        var handler = new FakeHandler
        {
            OcspStatus = HttpStatusCode.InternalServerError,
            CrlResponse = s_cannedCrl,
        };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Ocsp);
        Assert.NotNull(data.Crl);
        Assert.Equal(s_cannedCrl, data.Crl!.Value.ToArray());
    }

    [Fact]
    public void Ocsp_thrown_exception_does_not_propagate()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithAia("http://ocsp.example.invalid/respond");

        var handler = new FakeHandler { ThrowOnOcsp = true };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Ocsp);
        Assert.Null(data.Crl);
        Assert.True(data.IsEmpty);
    }

    [Fact]
    public void Non_positive_timeout_throws()
    {
        using var http = new HttpClient(new FakeHandler());
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HttpRevocationClient(http, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new HttpRevocationClient(http, TimeSpan.FromSeconds(-1)));
    }

    // ── In-process fake IRevocationClient (Phase 5 will reuse this pattern) ────────

    [Fact]
    public void Fake_revocation_client_implements_interface()
    {
        IRevocationClient fake = new FakeRevocationClient(s_cannedOcsp, s_cannedCrl);
        using var cert = CreateCertificate();
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");

        var data = fake.GetRevocationData(cert, issuer);

        Assert.Equal(s_cannedOcsp, data.Ocsp!.Value.ToArray());
        Assert.Equal(s_cannedCrl, data.Crl!.Value.ToArray());
        Assert.False(data.IsEmpty);
    }

    /// <summary>
    /// A trivial in-process <see cref="IRevocationClient"/> returning canned evidence
    /// without any network calls. Phase 5 (DSS/VRI) reuses this pattern.
    /// </summary>
    private sealed class FakeRevocationClient : IRevocationClient
    {
        private readonly byte[]? _ocsp;
        private readonly byte[]? _crl;

        public FakeRevocationClient(byte[]? ocsp, byte[]? crl)
        {
            _ocsp = ocsp;
            _crl = crl;
        }

        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
            => new()
            {
                Ocsp = _ocsp is null ? null : new ReadOnlyMemory<byte>(_ocsp),
                Crl = _crl is null ? null : new ReadOnlyMemory<byte>(_crl),
            };
    }

    // ── CertID decoder ────────────────────────────────────────────────────────────

    /// <summary>
    /// Decodes a DER OCSPRequest and returns the first CertID's serial number bytes,
    /// issuer name hash, and issuer key hash.
    /// </summary>
    private static (byte[] Serial, byte[] NameHash, byte[] KeyHash) DecodeCertId(byte[] ocspRequestDer)
    {
        var reader = new AsnReader(ocspRequestDer, AsnEncodingRules.DER);
        var ocspRequest = reader.ReadSequence();      // OCSPRequest
        var tbsRequest = ocspRequest.ReadSequence();  // TBSRequest
        var requestList = tbsRequest.ReadSequence();  // requestList SEQUENCE OF Request
        var request = requestList.ReadSequence();     // Request
        var certId = request.ReadSequence();          // CertID

        var algId = certId.ReadSequence();            // AlgorithmIdentifier
        algId.ReadObjectIdentifier();                 // hashAlgorithm OID (SHA-1)

        var nameHash = certId.ReadOctetString();
        var keyHash = certId.ReadOctetString();

        var serial = certId.ReadIntegerBytes().ToArray();

        return (serial, nameHash, keyHash);
    }

    // ── Fake HTTP handler ─────────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        public byte[]? OcspResponse { get; set; }
        public byte[]? CrlResponse { get; set; }
        public HttpStatusCode OcspStatus { get; set; } = HttpStatusCode.OK;
        public bool ThrowOnOcsp { get; set; }

        public string? OcspRequestContentType { get; private set; }
        public byte[]? OcspRequestBody { get; private set; }
        public Uri? OcspRequestUri { get; private set; }
        public Uri? CrlRequestUri { get; private set; }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                if (ThrowOnOcsp)
                    throw new HttpRequestException("Simulated OCSP transport failure.");

                OcspRequestUri = request.RequestUri;
                OcspRequestContentType = request.Content?.Headers.ContentType?.MediaType;
                OcspRequestBody = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

                if (OcspStatus != HttpStatusCode.OK)
                    return new HttpResponseMessage(OcspStatus);

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(OcspResponse ?? []),
                };
            }

            // GET — CRL
            CrlRequestUri = request.RequestUri;
            if (CrlResponse is null)
                return new HttpResponseMessage(HttpStatusCode.NotFound);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(CrlResponse),
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
