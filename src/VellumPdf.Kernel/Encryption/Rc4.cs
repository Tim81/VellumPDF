// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Encryption;

/// <summary>
/// RC4 (Arcfour) stream cipher, per draft-kaukonen-cipher-arcfour-03 (Kaukonen &amp; Thayer,
/// 1999-07-14), §3.1 (key scheduling) and §3.2 (pseudo-random generation).
///
/// The .NET BCL has never shipped RC4 — it was dropped from the crypto surface as legacy and
/// weak — so a legacy PDF's RC4-encrypted strings and streams (the Standard security handler's
/// V=1/V=2 revisions, ISO 32000-1 §7.6.2) need a hand-written implementation regardless of
/// platform.
/// </summary>
internal static class Rc4
{
    /// <summary>
    /// Applies RC4 keystream XOR to <paramref name="data"/> with the given <paramref name="key"/>.
    /// RC4 is symmetric — the same operation encrypts and decrypts — so this method serves both
    /// directions. PDF uses key lengths from 5 bytes (40-bit) to 16 bytes (128-bit). A key longer
    /// than 256 bytes is accepted, but only its first 256 bytes reach the schedule: the KSA runs
    /// 256 iterations, so it cycles a shorter key and truncates a longer one. It must be
    /// non-empty — this is a programming-error contract,
    /// not a file-format one. A caller deriving a key from parsed PDF data (a malformed /Length,
    /// a truncated /O) must validate that data itself and raise <see cref="InvalidDataException"/>
    /// before calling in; letting an empty key reach here would otherwise surface as an
    /// <see cref="ArgumentException"/> that the reader's malformed-file handling does not expect.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is empty.</exception>
    public static byte[] Transform(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        if (key.IsEmpty)
            throw new ArgumentException("RC4 key must be at least 1 byte.", nameof(key));

        Span<byte> s = stackalloc byte[256];
        KeySchedule(key, s);

        var output = new byte[data.Length];
        byte i = 0;
        byte j = 0;
        for (var n = 0; n < data.Length; n++)
        {
            i++;
            j += s[i];
            (s[i], s[j]) = (s[j], s[i]);
            var k = s[(byte)(s[i] + s[j])];
            output[n] = (byte)(data[n] ^ k);
        }

        return output;
    }

    // Key-scheduling algorithm (KSA), draft §3.1: seed S with the identity permutation, then
    // scramble it by repeatedly swapping against the key stream (repeated to fill 256 bytes).
    private static void KeySchedule(ReadOnlySpan<byte> key, Span<byte> s)
    {
        for (var i = 0; i < 256; i++)
            s[i] = (byte)i;

        byte j = 0;
        for (var i = 0; i < 256; i++)
        {
            j += (byte)(s[i] + key[i % key.Length]);
            (s[i], s[j]) = (s[j], s[i]);
        }
    }
}
