// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

internal readonly struct SfntTableRecord
{
    public Tag TagValue { get; }
    public uint Checksum { get; }
    public int Offset { get; }
    public int Length { get; }

    public SfntTableRecord(Tag tag, uint checksum, int offset, int length)
    {
        TagValue = tag; Checksum = checksum; Offset = offset; Length = length;
    }
}
