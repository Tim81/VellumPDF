// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.IO.Linearization;

/// <summary>
/// Writes an MSB-first bit stream, as required by the linearization hint tables
/// (ISO 32000-2 §F.7.1). Bits fill each byte from the high bit down; a partially
/// filled byte is flushed with zero padding by <see cref="SkipToNextByte"/> or
/// <see cref="ToArray"/>.
///
/// This mirrors the bit order qpdf produces and validates: values are written
/// big-endian within their field, and each hint-table column is padded to a byte
/// boundary between fields.
/// </summary>
internal sealed class BitWriter
{
    private readonly List<byte> _bytes = [];
    private int _current;      // bits accumulated in the working byte (high bits first)
    private int _bitsFilled;   // number of bits used in the working byte (0..7)

    /// <summary>
    /// Writes the low <paramref name="bitCount"/> bits of <paramref name="value"/>,
    /// most-significant bit first. A <paramref name="bitCount"/> of 0 writes nothing.
    /// </summary>
    public void WriteBits(uint value, int bitCount)
    {
        if (bitCount is < 0 or > 32)
            throw new ArgumentOutOfRangeException(nameof(bitCount), bitCount, "Bit count must be between 0 and 32.");

        for (var i = bitCount - 1; i >= 0; i--)
        {
            var bit = (int)((value >> i) & 1u);
            _current = (_current << 1) | bit;
            _bitsFilled++;
            if (_bitsFilled == 8)
            {
                _bytes.Add((byte)_current);
                _current = 0;
                _bitsFilled = 0;
            }
        }
    }

    /// <summary>
    /// Pads the current byte with zero bits so the next write starts on a byte boundary.
    /// A no-op when already aligned. Matches qpdf's <c>skipToNextByte</c>, called after
    /// each hint-table column.
    /// </summary>
    public void SkipToNextByte()
    {
        if (_bitsFilled == 0)
            return;
        _current <<= 8 - _bitsFilled;
        _bytes.Add((byte)_current);
        _current = 0;
        _bitsFilled = 0;
    }

    /// <summary>The number of whole bytes written so far (excludes a partially filled byte).</summary>
    public int ByteCount => _bytes.Count;

    /// <summary>
    /// Returns the written bytes, flushing any partially filled final byte with zero padding.
    /// </summary>
    public byte[] ToArray()
    {
        SkipToNextByte();
        return [.. _bytes];
    }
}
