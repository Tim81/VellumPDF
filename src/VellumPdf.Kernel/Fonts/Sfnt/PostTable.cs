// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts.Sfnt;

/// <summary>Reads ItalicAngle from the 'post' table.</summary>
internal sealed class PostTable
{
    public double ItalicAngle { get; }  // Fixed 16.16
    public bool   IsFixedPitch { get; }

    private PostTable(double angle, bool fixedPitch) { ItalicAngle = angle; IsFixedPitch = fixedPitch; }

    public static PostTable Parse(SfntFont font)
    {
        var r     = font.GetTableReader(new Tag("post"));
        var raw   = (int)r.ReadU32(4);  // Fixed 16.16 italic angle
        var angle = raw / 65536.0;
        var fixed_ = r.ReadU32(12) != 0;
        return new PostTable(angle, fixed_);
    }
}
