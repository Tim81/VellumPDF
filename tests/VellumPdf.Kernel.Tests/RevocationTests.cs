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
    // OCSPResponse ::= SEQUENCE { responseStatus ENUMERATED successful(0) }
    private static readonly byte[] s_cannedOcsp = [0x30, 0x03, 0x0A, 0x01, 0x00];
    // CertificateList ::= SEQUENCE { tbsCertList SEQUENCE {} } (minimal, structurally valid)
    private static readonly byte[] s_cannedCrl = [0x30, 0x02, 0x30, 0x00];

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
        // A CRL issuer needs Basic Constraints (CA) and a Subject Key Identifier
        // (CertificateRevocationListBuilder requires both to sign and to derive the CRL's AKI).
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(
            certificateAuthority: true, hasPathLengthConstraint: false, pathLengthConstraint: 0, critical: true));
        req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
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
    /// Builds a real DER CRL signed by <paramref name="issuerWithKey"/>, optionally listing
    /// <paramref name="revoked"/> as revoked. The issuer must have a private key.
    /// </summary>
    private static byte[] BuildCrl(X509Certificate2 issuerWithKey, X509Certificate2? revoked = null)
    {
        var builder = new CertificateRevocationListBuilder();
        if (revoked is not null)
            builder.AddEntry(revoked, DateTimeOffset.UtcNow.AddHours(-1));
        return builder.Build(
            issuerWithKey,
            crlNumber: 1,
            nextUpdate: DateTimeOffset.UtcNow.AddDays(7),
            hashAlgorithm: HashAlgorithmName.SHA256,
            rsaSignaturePadding: RSASignaturePadding.Pkcs1);
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
    public void Ocsp_error_status_response_is_rejected()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithAia("http://ocsp.example.invalid/respond");

        // OCSPResponse with responseStatus tryLater(3) — must not be embedded.
        var handler = new FakeHandler { OcspResponse = [0x30, 0x03, 0x0A, 0x01, 0x03] };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Ocsp);
    }

    [Fact]
    public void Non_certificatelist_crl_body_is_rejected()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithCdp("http://crl.example.invalid/list.crl");

        // SEQUENCE { INTEGER } is not a CertificateList (tbsCertList must be a SEQUENCE).
        var handler = new FakeHandler { CrlResponse = [0x30, 0x04, 0x02, 0x01, 0x2A] };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Crl);
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
        // The test leaf is self-signed, so it is its own issuer; this CRL does not revoke it.
        var crl = BuildCrl(leaf);

        var handler = new FakeHandler { CrlResponse = crl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.NotNull(data.Crl);
        Assert.Equal(crl, data.Crl!.Value.ToArray());
        Assert.Equal(new Uri("http://crl.example.invalid/list.crl"), handler.CrlRequestUri);
    }

    [Fact]
    public void Crl_that_revokes_the_certificate_is_rejected()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithCdp("http://crl.example.invalid/list.crl");
        var crl = BuildCrl(leaf, revoked: leaf); // lists the leaf's own serial number

        var handler = new FakeHandler { CrlResponse = crl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        Assert.Null(client.GetRevocationData(leaf, issuer).Crl);
    }

    [Fact]
    public void Crl_revoking_a_nonMinimalSerial_certificate_is_rejected()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();

        // The certificate carries a redundant leading pad (0x00 01 02 03 04); a real CA's CRL is
        // DER, so it lists the same serial minimally (01 02 03 04). Before the fix the comparison
        // was raw-versus-minimal, so it never matched: IsValidCrlForCertificate reported the CRL as
        // valid evidence and it was embedded in the /DSS — asserting that the signing certificate
        // is good, using the very document that revokes it.
        using var cert = NonMinimalSerialCertificate.Create(configure: WithCdp);
        using var crlIssuer = MatchingCrlIssuerFor(cert);

        Assert.Equal([0x00, 0x01, 0x02, 0x03, 0x04], cert.SerialNumberBytes.ToArray());

        var crl = BuildCrlRevokingSerial(crlIssuer, [0x01, 0x02, 0x03, 0x04]);

        var handler = new FakeHandler { CrlResponse = crl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        Assert.Null(client.GetRevocationData(cert, cert).Crl);
    }

    [Fact]
    public void Crl_notRevoking_a_nonMinimalSerial_certificate_is_still_accepted()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();

        // The other direction, so the fix is not just "reject everything": normalizing both sides
        // must not make two DIFFERENT serials compare equal. This CRL revokes 01 02 03 05, one
        // greater than the certificate's value, so the CRL is legitimate evidence.
        using var cert = NonMinimalSerialCertificate.Create(configure: WithCdp);
        using var crlIssuer = MatchingCrlIssuerFor(cert);

        var crl = BuildCrlRevokingSerial(crlIssuer, [0x01, 0x02, 0x03, 0x05]);

        var handler = new FakeHandler { CrlResponse = crl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        Assert.NotNull(client.GetRevocationData(cert, cert).Crl);
    }

    [Fact]
    public void Ocsp_isSkipped_forA_nonMinimalSerial_certificate()
    {
        NonMinimalSerialCertificate.SkipIfUnsupported();

        // A CertID can only carry a DER INTEGER, so a non-minimal serial would have to be sent
        // normalized — asking the responder about a different serial, which that CA may well have
        // issued to another certificate. A "good" answer about a sibling would then be embedded in
        // the /DSS looking authoritative. DssBuilder reaches this for every certificate in the
        // chain, including the TSA's, none of which the signing-time precondition inspects, so the
        // safe outcome is no evidence rather than wrong evidence.
        using var cert = NonMinimalSerialCertificate.Create(
            configure: req => req.CertificateExtensions.Add(
                BuildAiaOcspExtension("http://ocsp.example.invalid/")));

        var handler = new FakeHandler { OcspResponse = [0x30, 0x03, 0x0A, 0x01, 0x00] };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        Assert.Null(client.GetRevocationData(cert, cert).Ocsp);
        Assert.Null(handler.OcspRequestUri); // no request was even attempted
    }

    /// <summary>Points the certificate at a CRL distribution point so the client will fetch one.</summary>
    private static void WithCdp(CertificateRequest request)
        => request.CertificateExtensions.Add(BuildCdpExtension("http://crl.example.invalid/list.crl"));

    /// <summary>
    /// A CA whose subject DN equals <paramref name="certificate"/>'s issuer DN, which is all
    /// <c>IsValidCrlForCertificate</c> checks — it compares DNs and does not verify the CRL
    /// signature.
    /// </summary>
    /// <remarks>
    /// A separate certificate rather than reusing the non-minimal one as its own CA:
    /// <c>CertificateRevocationListBuilder</c> derives the CRL's AuthorityKeyIdentifier from the
    /// issuer's serial and refuses a non-minimal one outright — the same DER rule this whole issue
    /// is about, enforced against the test setup.
    /// </remarks>
    private static X509Certificate2 MatchingCrlIssuerFor(X509Certificate2 certificate)
        => CreateCertificate(certificate.IssuerName.Name!);

    /// <summary>
    /// Builds a DER CRL signed by <paramref name="issuerWithKey"/> that revokes
    /// <paramref name="serial"/> verbatim, so the entry's encoding can be chosen independently of
    /// the certificate's own (which is the whole point of the raw-versus-minimal case).
    /// </summary>
    private static byte[] BuildCrlRevokingSerial(X509Certificate2 issuerWithKey, byte[] serial)
    {
        var builder = new CertificateRevocationListBuilder();
        builder.AddEntry(serial, DateTimeOffset.UtcNow.AddHours(-1));
        return builder.Build(
            issuerWithKey,
            crlNumber: 1,
            nextUpdate: DateTimeOffset.UtcNow.AddDays(7),
            hashAlgorithm: HashAlgorithmName.SHA256,
            rsaSignaturePadding: RSASignaturePadding.Pkcs1);
    }

    [Fact]
    public void Crl_from_a_different_issuer_is_rejected()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithCdp("http://crl.example.invalid/list.crl");
        using var other = CreateCertificate("CN=Some Other CA");
        var crl = BuildCrl(other); // issuer DN does not match the leaf's issuer

        var handler = new FakeHandler { CrlResponse = crl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        Assert.Null(client.GetRevocationData(leaf, issuer).Crl);
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

        var crl = BuildCrl(leaf);
        var handler = new FakeHandler
        {
            OcspStatus = HttpStatusCode.InternalServerError,
            CrlResponse = crl,
        };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = client.GetRevocationData(leaf, issuer);

        Assert.Null(data.Ocsp);
        Assert.NotNull(data.Crl);
        Assert.Equal(crl, data.Crl!.Value.ToArray());
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

    // ── Async I/O surface (#54) ────────────────────────────────────────────────

    [Fact]
    public async Task GetRevocationDataAsync_fetchesOcspAndCrl()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertificate(configure: req =>
        {
            req.CertificateExtensions.Add(BuildAiaOcspExtension("http://ocsp.example.invalid/respond"));
            req.CertificateExtensions.Add(BuildCdpExtension("http://crl.example.invalid/list.crl"));
        });

        var crl = BuildCrl(leaf);
        var handler = new FakeHandler { OcspResponse = s_cannedOcsp, CrlResponse = crl };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = await client.GetRevocationDataAsync(leaf, issuer, TestContext.Current.CancellationToken);

        Assert.NotNull(data.Ocsp);
        Assert.Equal(s_cannedOcsp, data.Ocsp!.Value.ToArray());
        Assert.NotNull(data.Crl);
        Assert.Equal(crl, data.Crl!.Value.ToArray());
    }

    [Fact]
    public async Task GetRevocationDataAsync_ocspFailure_doesNotBlockCrl()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertificate(configure: req =>
        {
            req.CertificateExtensions.Add(BuildAiaOcspExtension("http://ocsp.example.invalid/respond"));
            req.CertificateExtensions.Add(BuildCdpExtension("http://crl.example.invalid/list.crl"));
        });

        var crl = BuildCrl(leaf);
        var handler = new FakeHandler
        {
            OcspStatus = HttpStatusCode.InternalServerError,
            CrlResponse = crl,
        };
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(5));

        var data = await client.GetRevocationDataAsync(leaf, issuer, TestContext.Current.CancellationToken);

        Assert.Null(data.Ocsp);
        Assert.NotNull(data.Crl);
        Assert.Equal(crl, data.Crl!.Value.ToArray());
    }

    [Fact]
    public async Task GetRevocationDataAsync_externalCancellation_propagatesDirectly()
    {
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");
        using var leaf = CreateCertWithAia("http://ocsp.example.invalid/respond");

        var handler = new HangingHandler();
        using var http = new HttpClient(handler);
        var client = new HttpRevocationClient(http, TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.GetRevocationDataAsync(leaf, issuer, cts.Token));
    }

    [Fact]
    public async Task IRevocationClient_asyncDefault_forwardsToSyncImplementation()
    {
        IRevocationClient fake = new FakeRevocationClient(s_cannedOcsp, s_cannedCrl);
        using var cert = CreateCertificate();
        using var issuer = CreateCertificate("CN=VellumPdf Test Issuer");

        // FakeRevocationClient does not override GetRevocationDataAsync, so this exercises
        // the interface's default implementation forwarding to the synchronous method.
        var data = await fake.GetRevocationDataAsync(cert, issuer, TestContext.Current.CancellationToken);

        Assert.Equal(s_cannedOcsp, data.Ocsp!.Value.ToArray());
        Assert.Equal(s_cannedCrl, data.Crl!.Value.ToArray());
    }

    /// <summary>A handler that blocks until the request is cancelled, to exercise cancellation.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.WaitHandle.WaitOne();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("Unreachable: handler should be cancelled first.");
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
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
