// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Binary;

namespace VellumPdf.Encryption;

/// <summary>
/// MD5, per RFC 1321 (Rivest, April 1992).
///
/// Hand-written rather than a call to the BCL's MD5 type for three reasons: the BCL hash
/// algorithms defer to the operating system's crypto library everywhere except Browser WASM,
/// where MD5 is not exposed at all — depending on it would break the legacy (R2/R3, ISO 32000-1
/// §7.6.3.3) key-derivation path under Blazor WASM; on Windows, deferring to the OS library puts
/// FIPS-policy behaviour outside this library's control; and a managed implementation stays
/// immune to a future analyzer configuration turning on CA5351 (flags MD5/DES/RC2 as weak),
/// which would otherwise fire on legitimate legacy-format decryption that has no alternative.
/// </summary>
internal static class Md5
{
    // RFC 1321 §3.4, step 4: the 64 additive constants, T[i] = floor(2^32 * abs(sin(i + 1))),
    // transcribed from the RFC's own table (Appendix A.3 lists the same 64 hex words).
    private static readonly uint[] T =
    [
        0xd76aa478, 0xe8c7b756, 0x242070db, 0xc1bdceee,
        0xf57c0faf, 0x4787c62a, 0xa8304613, 0xfd469501,
        0x698098d8, 0x8b44f7af, 0xffff5bb1, 0x895cd7be,
        0x6b901122, 0xfd987193, 0xa679438e, 0x49b40821,
        0xf61e2562, 0xc040b340, 0x265e5a51, 0xe9b6c7aa,
        0xd62f105d, 0x02441453, 0xd8a1e681, 0xe7d3fbc8,
        0x21e1cde6, 0xc33707d6, 0xf4d50d87, 0x455a14ed,
        0xa9e3e905, 0xfcefa3f8, 0x676f02d9, 0x8d2a4c8a,
        0xfffa3942, 0x8771f681, 0x6d9d6122, 0xfde5380c,
        0xa4beea44, 0x4bdecfa9, 0xf6bb4b60, 0xbebfbc70,
        0x289b7ec6, 0xeaa127fa, 0xd4ef3085, 0x04881d05,
        0xd9d4d039, 0xe6db99e5, 0x1fa27cf8, 0xc4ac5665,
        0xf4292244, 0x432aff97, 0xab9423a7, 0xfc93a039,
        0x655b59c3, 0x8f0ccc92, 0xffeff47d, 0x85845dd1,
        0x6fa87e4f, 0xfe2ce6e0, 0xa3014314, 0x4e0811a1,
        0xf7537e82, 0xbd3af235, 0x2ad7d2bb, 0xeb86d391,
    ];

    // RFC 1321 §3.4: per-round left-rotation amounts for rounds 1–4 (16 entries each).
    private static readonly int[] RotateAmounts =
    [
        7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22, 7, 12, 17, 22,
        5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20, 5, 9, 14, 20,
        4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23, 4, 11, 16, 23,
        6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21, 6, 10, 15, 21,
    ];

    /// <summary>
    /// Computes the 16-byte MD5 digest of <paramref name="data"/> in one call.
    /// </summary>
    public static byte[] HashData(ReadOnlySpan<byte> data)
    {
        var acc = new Incremental();
        acc.Append(data);
        return acc.Finish();
    }

    /// <summary>
    /// Incremental MD5 builder for inputs assembled from several pieces — PDF's password
    /// key-derivation algorithm (ISO 32000-1 §7.6.3.3, Algorithm 2) hashes a padded password
    /// concatenated with /O, /P as four little-endian bytes, the first /ID element, and
    /// (conditionally) four 0xFF bytes. Feeding those through <see cref="Append"/> avoids
    /// building one joined buffer at every call site.
    ///
    /// A reference type, not a struct: the running block buffer is a <c>byte[]</c>, so a struct
    /// copy would fork the scalar state (a, b, c, d, length) but alias that buffer between the
    /// original and the copy, corrupting both the moment either one appends past what the other
    /// already flushed. A caller that seeds one accumulator and reuses it — Algorithm 2's R>=3
    /// tail re-hashes the first n bytes fifty times — must not have that hazard available.
    /// </summary>
    internal sealed class Incremental
    {
        private uint _a;
        private uint _b;
        private uint _c;
        private uint _d;
        private ulong _messageLengthBits;
        private byte[]? _block;
        private int _blockLength;
        private bool _started;
        private bool _finished;

        /// <summary>
        /// Feeds more bytes into the running digest.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <see cref="Finish"/> was already called on this accumulator.
        /// </exception>
        public void Append(ReadOnlySpan<byte> data)
        {
            if (_finished)
                throw new InvalidOperationException($"Cannot call {nameof(Append)} after {nameof(Finish)}.");

            EnsureStarted();
            _messageLengthBits += (ulong)data.Length * 8;
            AppendRaw(data);
        }

        /// <summary>
        /// Applies RFC 1321 §3.1 padding (a single 0x80 bit followed by zeros) and the §3.2
        /// 64-bit little-endian message length, then returns the final 16-byte digest.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// <see cref="Finish"/> was already called on this accumulator — the padding bytes it
        /// wrote would re-enter the digest as data, producing a value that is not the digest of
        /// anything.
        /// </exception>
        public byte[] Finish()
        {
            if (_finished)
                throw new InvalidOperationException($"Cannot call {nameof(Finish)} twice on the same accumulator.");

            EnsureStarted();
            _finished = true;
            var messageLengthBits = _messageLengthBits;

            AppendRaw([0x80]);

            Span<byte> zeros = stackalloc byte[64];
            var padTo56 = _blockLength <= 56 ? 56 - _blockLength : 120 - _blockLength;
            AppendRaw(zeros[..padTo56]);

            Span<byte> lengthField = stackalloc byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(lengthField, messageLengthBits);
            AppendRaw(lengthField);

            var digest = new byte[16];
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(0), _a);
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(4), _b);
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(8), _c);
            BinaryPrimitives.WriteUInt32LittleEndian(digest.AsSpan(12), _d);
            return digest;
        }

        private void EnsureStarted()
        {
            if (_started) return;
            // RFC 1321 §3.3: the four fixed initialization words, low-order byte first.
            _a = 0x67452301;
            _b = 0xefcdab89;
            _c = 0x98badcfe;
            _d = 0x10325476;
            _block = new byte[64];
            _started = true;
        }

        // Copies data into the 64-byte block buffer, running RFC 1321 §3.4 on every full block,
        // without touching the running bit-length count (Finish's padding bytes must not count
        // towards the encoded message length).
        private void AppendRaw(ReadOnlySpan<byte> data)
        {
            var block = _block!; // set by EnsureStarted, called before AppendRaw on every path
            var offset = 0;
            if (_blockLength > 0)
            {
                var take = Math.Min(64 - _blockLength, data.Length);
                data[..take].CopyTo(block.AsSpan(_blockLength));
                _blockLength += take;
                offset += take;
                if (_blockLength == 64)
                {
                    ProcessBlock(block);
                    _blockLength = 0;
                }
            }

            while (data.Length - offset >= 64)
            {
                ProcessBlock(data.Slice(offset, 64));
                offset += 64;
            }

            var remaining = data.Length - offset;
            if (remaining > 0)
            {
                data[offset..].CopyTo(block.AsSpan(_blockLength));
                _blockLength += remaining;
            }
        }

        // RFC 1321 §3.4: one 64-byte block through the four auxiliary functions F, G, H, I,
        // each applied across 16 of the 64 rounds, with the message-word index and rotation
        // amount for each round fixed by the specification.
        private void ProcessBlock(ReadOnlySpan<byte> block)
        {
            Span<uint> words = stackalloc uint[16];
            for (var i = 0; i < 16; i++)
                words[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.Slice(i * 4, 4));

            var a = _a;
            var b = _b;
            var c = _c;
            var d = _d;

            for (var i = 0; i < 64; i++)
            {
                uint f;
                int g;
                if (i < 16)
                {
                    f = (b & c) | (~b & d); // F
                    g = i;
                }
                else if (i < 32)
                {
                    f = (d & b) | (~d & c); // G
                    g = (5 * i + 1) % 16;
                }
                else if (i < 48)
                {
                    f = b ^ c ^ d; // H
                    g = (3 * i + 5) % 16;
                }
                else
                {
                    f = c ^ (b | ~d); // I
                    g = (7 * i) % 16;
                }

                f += a + T[i] + words[g];
                a = d;
                d = c;
                c = b;
                b += RotateLeft(f, RotateAmounts[i]);
            }

            _a += a;
            _b += b;
            _c += c;
            _d += d;
        }

        private static uint RotateLeft(uint value, int bits) => (value << bits) | (value >> (32 - bits));
    }
}
