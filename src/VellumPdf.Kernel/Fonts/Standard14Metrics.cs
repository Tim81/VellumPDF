// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

namespace VellumPdf.Fonts;

/// <summary>
/// Advance-width tables for the 14 standard PDF fonts, derived from the
/// Adobe Font Metrics (AFM) data published in the PDF specification
/// (ISO 32000-2 Annex D).  Values are in 1/1000 of a point per unit size.
/// Only Latin-1 / WinAnsi range (32–255) is covered; unmapped glyphs → 0.
/// </summary>
public static class Standard14Metrics
{
    // Helvetica — same widths as Arial (proportional sans-serif).
    private static readonly ushort[] Helvetica =
    [
        278,278,355,556,556,889,667,222,333,333,389,584,278,333,278,278, // 32-47
        556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556, // 48-63
        1015,667,667,722,722,667,611,778,722,278,500,667,556,833,722,778, // 64-79
        667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556, // 80-95
        222,556,556,500,556,556,278,556,556,222,222,500,222,833,556,556, // 96-111
        556,556,333,500,278,556,500,722,500,500,500,334,260,334,584,  0, // 112-127
        0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  // 128-143
        0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  // 144-159
        278,333,556,556,556,556,260,556,333,737,370,556,584,333,737,552, // 160-175
        400,549,333,333,333,576,556,333,333,333,365,556,834,834,834,611, // 176-191
        667,667,667,667,667,667,1000,722,667,667,667,667,278,278,278,278, // 192-207
        722,722,778,778,778,778,778,584,778,722,722,722,722,667,667,611, // 208-223
        556,556,556,556,556,556,889,500,556,556,556,556,278,278,278,278, // 224-239
        556,556,556,556,556,556,556,549,611,556,556,556,556,500,556,500, // 240-255
    ];

    private static readonly ushort[] HelveticaBold =
    [
        278,333,474,556,556,889,722,278,333,333,389,584,278,333,278,278,
        556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,
        975,722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,
        667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,
        278,556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,
        611,611,389,556,333,611,556,778,556,556,500,389,280,389,584,  0,
        0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
        0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
        278,333,556,556,556,556,280,556,333,737,370,556,584,333,737,552,
        400,549,333,333,333,576,556,333,333,333,365,556,834,834,834,611,
        722,722,722,722,722,722,1000,722,667,667,667,667,278,278,278,278,
        722,722,778,778,778,778,778,584,778,722,722,722,722,667,667,611,
        556,556,556,556,556,556,889,556,556,556,556,556,278,278,278,278,
        611,611,611,611,611,611,611,549,611,611,611,611,611,556,611,556,
    ];

    private static readonly ushort[] TimesRoman =
    [
        250,333,408,500,500,833,778,333,333,333,500,564,250,333,250,278,
        500,500,500,500,500,500,500,500,500,500,278,278,564,564,564,444,
        921,722,667,667,722,611,556,722,722,333,389,722,611,889,722,722,
        556,722,667,556,611,722,722,944,722,722,611,333,278,333,469,500,
        333,444,500,444,500,444,333,500,500,278,278,500,278,778,500,500,
        500,500,333,389,278,500,500,722,500,500,444,480,200,480,541,  0,
        0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
        0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,  0,
        250,333,500,500,500,500,200,500,333,760,276,500,564,333,760,500,
        400,564,300,300,333,500,500,333,333,300,330,500,750,750,750,500,
        722,722,667,667,722,611,556,722,722,333,389,722,611,889,722,722,
        556,722,667,556,611,722,722,944,722,722,611,333,278,333,469,500,
        444,444,500,444,500,444,333,500,500,278,278,500,278,778,500,500,
        500,500,333,389,278,500,500,722,500,500,444,480,200,480,541,  0,
    ];

    // Courier — all characters are 600 units wide (monospaced).
    private static readonly ushort[] Courier = Enumerable.Repeat((ushort)600, 224).ToArray();

    private static readonly Dictionary<Standard14, ushort[]> _tables = new()
    {
        [Standard14.Helvetica]           = Helvetica,
        [Standard14.HelveticaBold]       = HelveticaBold,
        [Standard14.HelveticaOblique]    = Helvetica,     // same widths as regular
        [Standard14.HelveticaBoldOblique]= HelveticaBold, // same widths as bold
        [Standard14.TimesRoman]          = TimesRoman,
        [Standard14.TimesBold]           = TimesRoman,    // approximation; full tables in Phase 2
        [Standard14.TimesItalic]         = TimesRoman,
        [Standard14.TimesBoldItalic]     = TimesRoman,
        [Standard14.Courier]             = Courier,
        [Standard14.CourierBold]         = Courier,
        [Standard14.CourierOblique]      = Courier,
        [Standard14.CourierBoldOblique]  = Courier,
    };

    /// <summary>
    /// Returns the advance width of <paramref name="c"/> in 1/1000ths of a point,
    /// or 0 if outside the supported range or not a text font (Symbol, ZapfDingbats).
    /// </summary>
    public static int GetWidth(Standard14 font, char c)
    {
        if (!_tables.TryGetValue(font, out var table)) return 0;
        var idx = (int)c - 32;
        return idx >= 0 && idx < table.Length ? table[idx] : 0;
    }

    /// <summary>Measures a string width at the given point size.</summary>
    public static double MeasureString(Standard14 font, string text, double pointSize)
    {
        var w = 0.0;
        foreach (var c in text) w += GetWidth(font, c);
        return w / 1000.0 * pointSize;
    }
}
