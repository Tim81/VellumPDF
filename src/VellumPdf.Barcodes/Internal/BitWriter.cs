// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Barcodes.Internal;

/// <summary>
/// Writes an MSB-first bit stream. Bits fill each byte from the high bit down; a partially
/// filled final byte is flushed with zero padding by <see cref="ToArray"/>.
///
/// Used by the QR/Micro QR bit-stream builders (mode indicators, character counts, data,
/// terminators and pad codewords all pack this way per ISO/IEC 18004).
/// </summary>
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _current;      // bits accumulated in the working byte (high bits first)
    private int _bitsFilled;   // number of bits used in the working byte (0..7)

    /// <summary>The number of bits written so far.</summary>
    public int BitCount { get; private set; }

    /// <summary>
    /// Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>,
    /// most-significant bit first. A <paramref name="bitCount"/> of 0 writes nothing.
    /// </summary>
    public void WriteBits(int value, int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be between 0 and 32.");

        for (var i = bitCount - 1; i >= 0; i--)
        {
            var bit = (value >> i) & 1;
            _current = (_current << 1) | bit;
            _bitsFilled++;
            BitCount++;
            if (_bitsFilled == 8)
            {
                _bytes.Add((byte)_current);
                _current = 0;
                _bitsFilled = 0;
            }
        }
    }

    /// <summary>Writes a single bit (0 or 1).</summary>
    public void WriteBit(int bit) => WriteBits(bit, 1);

    /// <summary>
    /// Returns the written bytes, flushing any partially filled final byte with zero padding.
    /// Does not mutate this writer's state, so more bits can still be appended afterwards.
    /// </summary>
    public byte[] ToArray()
    {
        if (_bitsFilled == 0)
            return [.. _bytes];

        var padded = new byte[_bytes.Count + 1];
        for (var i = 0; i < _bytes.Count; i++) padded[i] = _bytes[i];
        padded[_bytes.Count] = (byte)(_current << (8 - _bitsFilled));
        return padded;
    }
}
