// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Encryption;

/// <summary>
/// Abstraction over the per-file encryption engine used by the PDF writer.
/// String and stream objects call <see cref="Encrypt"/> to transform their
/// byte payload before serialisation.
/// </summary>
public interface IPdfEncryptor
{
    /// <summary>
    /// Encrypts <paramref name="data"/> and returns the encrypted bytes
    /// (including any required prefix, e.g. the AES IV for V5/AESv3).
    /// </summary>
    byte[] Encrypt(ReadOnlySpan<byte> data);
}
