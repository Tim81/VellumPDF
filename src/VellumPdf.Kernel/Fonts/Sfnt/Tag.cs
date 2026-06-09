// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>4-byte sfnt table tag (e.g. "cmap", "head", "glyf").</summary>
internal readonly struct Tag : IEquatable<Tag>
{
    private readonly uint _value;

    public Tag(ReadOnlySpan<byte> bytes)
    {
        _value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public Tag(string s)
    {
        if (s.Length != 4) throw new ArgumentException("Tag must be 4 chars.", nameof(s));
        _value = ((uint)s[0] << 24) | ((uint)s[1] << 16) | ((uint)s[2] << 8) | (uint)s[3];
    }

    public bool Equals(Tag other) => _value == other._value;
    public override bool Equals(object? obj) => obj is Tag t && Equals(t);
    public override int GetHashCode() => (int)_value;
    public static bool operator ==(Tag a, Tag b) => a._value == b._value;
    public static bool operator !=(Tag a, Tag b) => a._value != b._value;

    public override string ToString() =>
        new string([(char)(_value >> 24), (char)((_value >> 16) & 0xFF),
                    (char)((_value >>  8) & 0xFF), (char)(_value & 0xFF)]);
}
