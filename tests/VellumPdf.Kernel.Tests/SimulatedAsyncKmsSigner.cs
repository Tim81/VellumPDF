// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using VellumPdf.Signing;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Wraps a real RSA or ECDsa key behind <see cref="IExternalSigner"/>, simulating a cloud
/// KMS/HSM's async sign call. Shared across test classes that need an <see cref="IExternalSigner"/>
/// test double.
/// </summary>
internal sealed class SimulatedAsyncKmsSigner : IExternalSigner
{
    private readonly RSA? _rsa;
    private readonly ECDsa? _ecdsa;
    private readonly TimeSpan _delay;
    private readonly Action? _beforeSign;

    public SimulatedAsyncKmsSigner(RSA rsa, TimeSpan delay = default, Action? beforeSign = null, HashAlgorithmName? hashAlgorithm = null)
    {
        _rsa = rsa;
        _delay = delay;
        _beforeSign = beforeSign;
        HashAlgorithm = hashAlgorithm ?? HashAlgorithmName.SHA256;
    }

    public SimulatedAsyncKmsSigner(ECDsa ecdsa, TimeSpan delay = default, Action? beforeSign = null, HashAlgorithmName? hashAlgorithm = null)
    {
        _ecdsa = ecdsa;
        _delay = delay;
        _beforeSign = beforeSign;
        HashAlgorithm = hashAlgorithm ?? HashAlgorithmName.SHA256;
    }

    public HashAlgorithmName HashAlgorithm { get; }

    public async Task<byte[]> SignAsync(ReadOnlyMemory<byte> signedAttributesDigest, CancellationToken cancellationToken = default)
    {
        _beforeSign?.Invoke();
        if (_delay > TimeSpan.Zero)
            await Task.Delay(_delay, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        if (_rsa is not null)
            return _rsa.SignHash(signedAttributesDigest.Span.ToArray(), HashAlgorithm, RSASignaturePadding.Pkcs1);

        var raw = _ecdsa!.SignHash(signedAttributesDigest.Span.ToArray());
        return EcdsaSignatureConverter.RawToDer(raw);
    }
}
