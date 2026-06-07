// Copyright 2026 Timothy van der Ham (@Tim81)
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

    public byte ReadU8(int offset) => _data.Span[offset];
    public ushort ReadU16(int offset) => (ushort)((_data.Span[offset] << 8) | _data.Span[offset + 1]);
    public short ReadI16(int offset) => (short)ReadU16(offset);
    public uint ReadU32(int offset) =>
        ((uint)_data.Span[offset] << 24) |
        ((uint)_data.Span[offset + 1] << 16) |
        ((uint)_data.Span[offset + 2] << 8) |
         (uint)_data.Span[offset + 3];

    public int ReadI32(int offset) => (int)ReadU32(offset);

    public Tag ReadTag(int offset) => new(_data.Span[offset..]);

    public ReadOnlyMemory<byte> Slice(int offset, int length) => _data.Slice(offset, length);
}
