// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Fonts;

namespace VellumPdf.Kernel.Tests;

/// <summary>Which code point speaks for a glyph in the /ToUnicode CMap when some of them only got it as a stand-in, run through the CMap builder with assignments a bundled font cannot produce.</summary>
public sealed class ToUnicodeStandInTests
{
    private const int NoBreakSpace = 0x00A0;
    private const int FigureSpace = 0x2007;
    private const int NarrowNoBreakSpace = 0x202F;

    private static List<(int Gid, int CodePoint)> Entries(
        Dictionary<int, ushort> assignments, params int[] standIns)
    {
        var text = TrueTypeFontEmbedder.BuildToUnicodeCMapContent(assignments, new HashSet<int>(standIns));
        text = text[text.IndexOf("endcodespacerange", StringComparison.Ordinal)..];
        return Regex.Matches(text, @"^<([0-9A-F]{4})> <([0-9A-F]{4})>$", RegexOptions.Multiline)
            .Select(m => (Convert.ToInt32(m.Groups[1].Value, 16), Convert.ToInt32(m.Groups[2].Value, 16)))
            .ToList();
    }

    /// <summary>Two stand-ins on a glyph nothing owns: one entry, for the lowest code point.</summary>
    [Fact]
    public void TwoStandInsOnAGlyphNothingOwns_listTheLowestCodePointOnce()
    {
        var entries = Entries(
            new Dictionary<int, ushort> { [NarrowNoBreakSpace] = 98, [FigureSpace] = 98 },
            NarrowNoBreakSpace, FigureSpace);

        Assert.Equal([(98, FigureSpace)], entries);
    }

    /// <summary>The order the code points were assigned in must not decide which is listed.</summary>
    [Fact]
    public void TwoStandIns_theLowestIsListed_whicheverWasAssignedFirst()
    {
        var entries = Entries(
            new Dictionary<int, ushort> { [FigureSpace] = 98, [NarrowNoBreakSpace] = 98 },
            NarrowNoBreakSpace, FigureSpace);

        Assert.Equal([(98, FigureSpace)], entries);
    }

    [Fact]
    public void AStandInOnAGlyphARealCodePointOwns_isNotListed()
    {
        var entries = Entries(
            new Dictionary<int, ushort> { [NoBreakSpace] = 98, [NarrowNoBreakSpace] = 98, [FigureSpace] = 98 },
            NarrowNoBreakSpace, FigureSpace);

        Assert.Equal([(98, NoBreakSpace)], entries);
    }

    /// <summary>Stand-ins on different glyphs each keep their own entry.</summary>
    [Fact]
    public void StandInsOnDifferentGlyphs_areBothListed()
    {
        var entries = Entries(
            new Dictionary<int, ushort> { [NarrowNoBreakSpace] = 98, [FigureSpace] = 3 },
            NarrowNoBreakSpace, FigureSpace);

        Assert.Equal([(3, FigureSpace), (98, NarrowNoBreakSpace)], entries);
    }

    /// <summary>
    /// Code points that are not stand-ins keep their entries, whatever glyph they share, except
    /// glyph 0: the missing characters get one entry for it, U+FFFD, and no code point is listed
    /// against it.
    /// </summary>
    [Fact]
    public void CodePointsThatAreNotStandIns_keepTheirEntries_glyphZeroGetsOneReplacementEntry()
    {
        var entries = Entries(
            new Dictionary<int, ushort> { [0x4E00] = 0, [0x4E01] = 0, [0x41] = 36, [0x61] = 36 });

        Assert.Equal([(0, 0xFFFD), (36, 0x41), (36, 0x61)], entries);
    }
}
