// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Formats.Asn1;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for PAdES B-T (RFC 3161 signature timestamp) support.
/// All tests are fully offline and deterministic.
/// </summary>
public sealed class TimestampTests
{
    // Pinned timestamp used across all tests so they are deterministic.
    private static readonly DateTimeOffset s_pinnedTime =
        new(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

    // ── Signing certificate helper (same pattern as SignatureTests) ────────────

    private static X509Certificate2 CreateTestCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest(
            "CN=VellumPdf Test",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddYears(1));
    }

    // ── Test 1: B-T signing succeeds and passes BCL CheckSignature ────────────

    [Fact]
    public void BT_signed_doc_passes_BCL_CheckSignature()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            TimestampClient = tsaClient,
        };

        var signedBytes = SignOnePageDoc(cert, settings: settings);
        VerifySignatureOrThrow(signedBytes);
        // Reaching here means CheckSignature did not throw.
    }

    // ── Test 2: CMS contains unsigned attribute OID 1.2.840.113549.1.9.16.2.14 ─

    [Fact]
    public void BT_signed_doc_contains_timestamp_unsigned_attribute()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            TimestampClient = tsaClient,
        };

        var signedBytes = SignOnePageDoc(cert, settings: settings);
        var (byteRange, contentsInfo) = ParseSignatureFields(signedBytes);

        var contentsBytes = Convert.FromHexString(contentsInfo.HexContent);

        // Decode the outer SignedCms (the PDF signature).
        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var outerCms = new SignedCms(new ContentInfo(signedContent), detached: true);
        outerCms.Decode(contentsBytes);

        var si = outerCms.SignerInfos[0];

        // Find the signature-timestamp unsigned attribute by OID.
        // UnsignedAttributes indexer takes an int (position), so we iterate to find by OID.
        CryptographicAttributeObject? tsAttr = null;
        foreach (CryptographicAttributeObject attr in si.UnsignedAttributes)
        {
            if (attr.Oid.Value == "1.2.840.113549.1.9.16.2.14")
            {
                tsAttr = attr;
                break;
            }
        }
        Assert.NotNull(tsAttr);
        Assert.True(tsAttr.Values.Count > 0);

        // The attribute value must decode as a valid RFC 3161 token.
        var tokenDer = tsAttr.Values[0].RawData;
        Assert.True(Rfc3161TimestampToken.TryDecode(tokenDer, out var token, out _));
        Assert.NotNull(token);
    }

    // ── Test 3: size guard still fires with an explicit tiny reserve + timestamp ─

    [Fact]
    public void Timestamp_size_exceeded_throws_InvalidOperationException()
    {
        using var cert = CreateTestCertificate();
        var tsaClient = new TestTimestampClient(s_pinnedTime);

        // Force an absurdly small size so even the timestamped signature cannot fit.
        var settings = new PdfSignatureSettings
        {
            Certificate = cert,
            TimestampClient = tsaClient,
            EstimatedSignatureSizeBytes = 16,
        };

        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();

        Assert.Throws<InvalidOperationException>(() => doc.Sign(ms, settings));
    }

    // ── Test 4: HttpTimestampClient offline unit tests ─────────────────────────

    [Fact]
    public void HttpTimestampClient_sends_correct_content_type_and_returns_token()
    {
        var tsaClient = new TestTimestampClient(s_pinnedTime);
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("test payload"));

        // Build a valid reply for the stub handler: parse the incoming request,
        // then return a token from our TestTimestampClient.
        var handler = new StubTimestampHandler(tsaClient);
        var httpClient = new HttpClient(handler);

        var url = new Uri("http://tsa.example.invalid/ts");
        var client = new HttpTimestampClient(url, httpClient);

        var tokenDer = client.GetTimestampToken(digest, HashAlgorithmName.SHA256);

        Assert.NotNull(tokenDer);
        Assert.True(tokenDer.Length > 0);
        Assert.True(Rfc3161TimestampToken.TryDecode(tokenDer, out _, out _));
        Assert.Equal("application/timestamp-query", handler.ReceivedContentType);
        Assert.Equal(url, handler.ReceivedUri);
    }

    [Fact]
    public void HttpTimestampClient_throws_on_http_failure()
    {
        var handler = new FailingHttpHandler(HttpStatusCode.InternalServerError);
        var httpClient = new HttpClient(handler);
        var client = new HttpTimestampClient(new Uri("http://tsa.example.invalid/ts"), httpClient);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("test"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => client.GetTimestampToken(digest, HashAlgorithmName.SHA256));
        Assert.Contains("500", ex.Message);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static byte[] SignOnePageDoc(
        X509Certificate2 cert,
        string markerText = "VELLUM_TS_TEST",
        PdfSignatureSettings? settings = null)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var font = doc.UseFont(Standard14.Helvetica);
        var canvas = new PdfCanvas(page);
        canvas.BeginText()
              .SetFont(font, 12)
              .SetTextMatrix(1, 0, 0, 1, 72, 720)
              .ShowText(markerText)
              .EndText();
        canvas.Finish();

        var sigSettings = settings ?? new PdfSignatureSettings { Certificate = cert };
        var ms = new MemoryStream();
        doc.Sign(ms, sigSettings);
        return ms.ToArray();
    }

    private static void VerifySignatureOrThrow(byte[] signedBytes)
    {
        var (byteRange, contentsInfo) = ParseSignatureFields(signedBytes);

        var seg0Len = (int)byteRange[1];
        var seg1Start = (int)byteRange[2];
        var seg1Len = (int)byteRange[3];
        var signedContent = new byte[seg0Len + seg1Len];
        Buffer.BlockCopy(signedBytes, 0, signedContent, 0, seg0Len);
        Buffer.BlockCopy(signedBytes, seg1Start, signedContent, seg0Len, seg1Len);

        var contentsBytes = Convert.FromHexString(contentsInfo.HexContent);

        var verify = new SignedCms(new ContentInfo(signedContent), detached: true);
        verify.Decode(contentsBytes);
        verify.CheckSignature(verifySignatureOnly: true);
    }

    private record ContentsInfo(long PosLt, int TokenLen, string HexContent);

    private static (long[] ByteRange, ContentsInfo Contents) ParseSignatureFields(byte[] bytes)
    {
        var text = Encoding.Latin1.GetString(bytes);

        const string byteRangeMarker = "/ByteRange [";
        var brStart = text.IndexOf(byteRangeMarker, StringComparison.Ordinal);
        Assert.True(brStart >= 0, "/ByteRange not found in signed PDF");
        var brBracket = brStart + byteRangeMarker.Length - 1;
        var brEnd = text.IndexOf(']', brBracket);
        Assert.True(brEnd >= 0, "/ByteRange closing ']' not found");
        var brContent = text[(brBracket + 1)..brEnd].Trim();
        var brParts = brContent.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(4, brParts.Length);
        var byteRange = brParts.Select(long.Parse).ToArray();

        const string contentsMarker = "/Contents <";
        var cStart = text.IndexOf(contentsMarker, StringComparison.Ordinal);
        Assert.True(cStart >= 0, "/Contents not found in signed PDF");
        var posLt = cStart + contentsMarker.Length - 1;
        var cEnd = text.IndexOf('>', posLt);
        Assert.True(cEnd >= 0, "/Contents closing '>' not found");
        var hexContent = text[(posLt + 1)..cEnd];
        var tokenLen = 1 + hexContent.Length + 1;

        return (byteRange, new ContentsInfo(posLt, tokenLen, hexContent));
    }

    // ── Stub HTTP handlers ─────────────────────────────────────────────────────

    /// <summary>
    /// A stub <see cref="HttpMessageHandler"/> that responds to RFC 3161 timestamp queries
    /// using <see cref="TestTimestampClient"/> to issue the token, wrapped in a minimal
    /// <c>TimeStampResp</c> DER envelope (PKIStatusInfo{granted} + TimeStampToken).
    /// </summary>
    private sealed class StubTimestampHandler : HttpMessageHandler
    {
        private readonly TestTimestampClient _tsa;
        public string? ReceivedContentType { get; private set; }
        public Uri? ReceivedUri { get; private set; }

        public StubTimestampHandler(TestTimestampClient tsa) => _tsa = tsa;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ReceivedContentType = request.Content?.Headers.ContentType?.MediaType;
            ReceivedUri = request.RequestUri;

            var requestBytes = request.Content!.ReadAsByteArrayAsync(cancellationToken).GetAwaiter().GetResult();

            // Decode the RFC 3161 request to extract the digest.
            Assert.True(Rfc3161TimestampRequest.TryDecode(requestBytes, out var tsReq, out _));

            var digest = tsReq!.GetMessageHash();
            var hashAlg = new HashAlgorithmName(tsReq.HashAlgorithmId.FriendlyName ?? "SHA256");
            var tokenDer = _tsa.GetTimestampToken(digest.Span, hashAlg);

            // Build TimeStampResp ::= SEQUENCE { status PKIStatusInfo, timeStampToken [OPTIONAL] }
            // PKIStatusInfo ::= SEQUENCE { status INTEGER (0 = granted) }
            var respWriter = new AsnWriter(AsnEncodingRules.DER);
            using (respWriter.PushSequence())
            {
                // PKIStatusInfo
                using (respWriter.PushSequence())
                {
                    respWriter.WriteInteger(0); // status = granted
                }
                // timeStampToken (ContentInfo — the raw SignedCms we already have)
                respWriter.WriteEncodedValue(tokenDer);
            }
            var responseBytes = respWriter.Encode();

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(responseBytes),
            };
            response.Content.Headers.ContentType =
                new System.Net.Http.Headers.MediaTypeHeaderValue("application/timestamp-reply");
            return response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class FailingHttpHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        public FailingHttpHandler(HttpStatusCode status) => _status = status;

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
            => new(_status);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(Send(request, cancellationToken));
    }
}
