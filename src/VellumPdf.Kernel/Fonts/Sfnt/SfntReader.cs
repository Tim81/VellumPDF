// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Big-endian binary reader over a raw sfnt (TrueType/OpenType) font byte buffer.
/// All multi-byte reads are big-endian as required by the sfnt spec.
/// </summary>
internal sealed class SfntReader
{
    private readonly ReadOnlyMemory<byte> _data;

    public SfntReader(ReadOnlyMemory<byte> data) => _data = data;

    public int Length => _data.Length;
    public ReadOnlySpan<byte> Span => _data.Span;

    public byte ReadU8(int offset)
    {
        Require(offset, 1);
        return _data.Span[offset];
    }

    public ushort ReadU16(int offset)
    {
        Require(offset, 2);
        var s = _data.Span;
        return (ushort)((s[offset] << 8) | s[offset + 1]);
    }

    public short ReadI16(int offset) => (short)ReadU16(offset);

    public uint ReadU32(int offset)
    {
        Require(offset, 4);
        var s = _data.Span;
        return ((uint)s[offset] << 24) | ((uint)s[offset + 1] << 16) | ((uint)s[offset + 2] << 8) | s[offset + 3];
    }

    public int ReadI32(int offset) => (int)ReadU32(offset);

    public Tag ReadTag(int offset)
    {
        Require(offset, 4);
        return new(_data.Span[offset..]);
    }

    public ReadOnlyMemory<byte> Slice(int offset, int length)
    {
        Require(offset, length);
        return _data.Slice(offset, length);
    }

    // Validates that `size` bytes are readable at `offset`, converting truncated/malformed
    // font input into a clean InvalidDataException instead of IndexOutOfRangeException.
    // The long cast prevents offset + size overflowing Int32 on hostile input.
    private void Require(int offset, int size)
    {
        if (offset < 0 || size < 0 || (long)offset + size > _data.Length)
            throw new InvalidDataException(
                $"Malformed font: cannot read {size} byte(s) at offset {offset}; buffer length is {_data.Length}.");
    }
}
