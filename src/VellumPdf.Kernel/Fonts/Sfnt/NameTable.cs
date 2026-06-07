// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Fonts.Sfnt;

/// <summary>
/// Reads selected entries from the 'name' table (platform 3, encoding 1, lang 0x0409).
/// Provides PostScript name (nameId 6), full name (4), family name (1).
/// </summary>
internal sealed class NameTable
{
    public string? PostScriptName { get; }
    public string? FullName { get; }
    public string? FamilyName { get; }

    private NameTable(string? psName, string? fullName, string? familyName)
    {
        PostScriptName = psName; FullName = fullName; FamilyName = familyName;
    }

    public static NameTable Parse(SfntFont font)
    {
        var r = font.GetTableReader(new Tag("name"));
        var count = r.ReadU16(2);
        var strOff = r.ReadU16(4);

        string? psName = null, fullName = null, familyName = null;
        for (var i = 0; i < count; i++)
        {
            var platform = r.ReadU16(6 + i * 12);
            var encoding = r.ReadU16(6 + i * 12 + 2);
            var language = r.ReadU16(6 + i * 12 + 4);
            var nameId = r.ReadU16(6 + i * 12 + 6);
            var length = r.ReadU16(6 + i * 12 + 8);
            var offset = r.ReadU16(6 + i * 12 + 10);

            // Prefer platform 3 (Windows), encoding 1, US English
            if (platform != 3 || encoding != 1 || language != 0x0409) continue;

            var s = Encoding.BigEndianUnicode.GetString(r.Span.Slice(strOff + offset, length));
            switch (nameId)
            {
                case 1: familyName = s; break;
                case 4: fullName = s; break;
                case 6: psName = s; break;
            }
        }
        return new NameTable(psName, fullName, familyName);
    }
}
