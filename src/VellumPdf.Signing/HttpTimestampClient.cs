// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;

namespace VellumPdf.Signing;

/// <summary>
/// Obtains an RFC 3161 timestamp token from a remote Time Stamping Authority (TSA)
/// via HTTP POST, as specified in RFC 3161 §3.4.
/// </summary>
public sealed class HttpTimestampClient : ITimestampClient
{
    private static readonly HttpClient s_sharedClient = new();

    /// <summary>The default per-request timeout when none is supplied.</summary>
    private static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(30);

    private readonly Uri _tsaUrl;
    private readonly HttpClient _httpClient;
    private readonly bool _requestTsaCertificate;
    private readonly TimeSpan _timeout;

    /// <summary>
    /// Initialises a new <see cref="HttpTimestampClient"/>.
    /// </summary>
    /// <param name="tsaUrl">The HTTP(S) URL of the TSA's timestamping endpoint.</param>
    /// <param name="httpClient">
    /// An optional <see cref="HttpClient"/> to use for requests.
    /// When <see langword="null"/>, a shared static instance is used.
    /// A caller-provided client is never disposed by this class.
    /// </param>
    /// <param name="requestTsaCertificate">
    /// When <see langword="true"/> (the default), the TSA is asked to include its signing
    /// certificate in the response token.
    /// </param>
    /// <param name="timeout">
    /// The per-request timeout. When <see langword="null"/>, a 30-second default is used.
    /// Bounds how long a slow or unresponsive TSA can stall signing; applied independently
    /// of any timeout configured on a shared <paramref name="httpClient"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="timeout"/> is not a positive duration.
    /// </exception>
    public HttpTimestampClient(
        Uri tsaUrl,
        HttpClient? httpClient = null,
        bool requestTsaCertificate = true,
        TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(tsaUrl);
        if (timeout is { } t && t <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be a positive duration.");
        _tsaUrl = tsaUrl;
        _httpClient = httpClient ?? s_sharedClient;
        _requestTsaCertificate = requestTsaCertificate;
        _timeout = timeout ?? s_defaultTimeout;
    }

    /// <inheritdoc/>
    public byte[] GetTimestampToken(ReadOnlySpan<byte> messageDigest, HashAlgorithmName hashAlgorithm)
    {
        var req = Rfc3161TimestampRequest.CreateFromHash(
            messageDigest.ToArray(),
            hashAlgorithm,
            requestedPolicyId: null,
            nonce: null,
            requestSignerCertificates: _requestTsaCertificate,
            extensions: null);

        var requestBytes = req.Encode();

        var content = new ByteArrayContent(requestBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");

        var httpReq = new HttpRequestMessage(HttpMethod.Post, _tsaUrl)
        {
            Content = content,
        };

        using var cts = new CancellationTokenSource(_timeout);

        HttpResponseMessage httpResp;
        byte[] responseBytes;
        try
        {
            httpResp = _httpClient.Send(httpReq, cts.Token);
            using (httpResp)
            {
                if (!httpResp.IsSuccessStatusCode)
                    throw new InvalidOperationException(
                        $"TSA request to {_tsaUrl} failed with HTTP {(int)httpResp.StatusCode} {httpResp.ReasonPhrase}.");

                responseBytes = httpResp.Content.ReadAsByteArrayAsync(cts.Token).GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException ex)
        {
            throw new InvalidOperationException(
                $"TSA request to {_tsaUrl} timed out after {_timeout.TotalSeconds:0.##}s.", ex);
        }

        var token = req.ProcessResponse(responseBytes, out _);
        return token.AsSignedCms().Encode();
    }
}
