// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using VellumPdf.Fonts.Cff;
using VellumPdf.Fonts.Sfnt;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// A parsed font reads each INDEX's offsets once, not once per glyph and per subroutine. The allocation
/// assertions compare what a subset costs per kept glyph against the size of the font's own offset
/// arrays, so they hold on any machine and under any load.
/// </summary>
public sealed class CffIndexCacheTests(ITestOutputHelper output)
{
    private static ReadOnlyMemory<byte> Table() =>
        SfntFont.Parse(EmbeddedFontFixtures.Load("SourceSans3-ExtraLight.otf")).GetTableBytes(new Tag("CFF "));

    private static HashSet<int> FirstGlyphs(int count) => [.. Enumerable.Range(0, count)];

    // Allocation of one subset on a font parsed before the measurement starts. One warm-up subset
    // first, so one-time work (JIT, static tables) is not charged to the measured one.
    private static long Allocated(int kept)
    {
        var table = Table();
        var warm = CffFont.Parse(table);
        CffSubsetter.Subset(warm, FirstGlyphs(Math.Min(kept, warm.NumGlyphs)));

        var font = CffFont.Parse(table);
        var gids = FirstGlyphs(Math.Min(kept, font.NumGlyphs));
        var before = GC.GetAllocatedBytesForCurrentThread();
        CffSubsetter.Subset(font, gids);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void Subset_allocatesInProportionToKeptGlyphs_notKeptTimesFontGlyphs()
    {
        var font = CffFont.Parse(Table());
        var all = font.NumGlyphs;
        int[] sizes = [50, 400, all];
        var allocated = sizes.Select(Allocated).ToArray();
        for (var i = 0; i < sizes.Length; i++)
            output.WriteLine($"{sizes[i]} glyphs: {allocated[i]} bytes");

        // Reading one INDEX's offsets allocates (glyphs + 1) uint values. Re-reading them per kept
        // glyph would add that much for every glyph kept; the slope between two subsets must stay
        // below a fraction of it.
        var offsetArrayBytes = 4L * (all + 1);
        for (var i = 1; i < sizes.Length; i++)
        {
            var slope = (double)(allocated[i] - allocated[i - 1]) / (sizes[i] - sizes[i - 1]);
            Assert.True(
                slope < offsetArrayBytes / 8.0,
                $"{sizes[i - 1]} to {sizes[i]} glyphs added {slope:F0} bytes per glyph; one offset array is {offsetArrayBytes} bytes.");
        }
    }

    // Reads an entry from the INDEX at its position without the font's cache.
    private static byte[] Uncached(ReadOnlyMemory<byte> table, int indexPosition, int entry)
    {
        var (_, offsets, dataBase) = CffFont.ReadIndexOffsets(table.Span, indexPosition, table.Length);
        var start = (int)offsets[entry];
        return table.Slice(dataBase + start - 1, (int)offsets[entry + 1] - start).ToArray();
    }

    [Fact]
    public void EveryEntryOfEveryIndex_equalsTheEntryReadWithoutTheCache()
    {
        var table = Table();
        var font = CffFont.Parse(table);

        // Asked in an order that alternates the three INDEXes, so an offset array kept for one is
        // never the one the next call needs.
        var subrs = Math.Min(font.GlobalSubrCount, font.LocalSubrCount);
        Assert.True(subrs > 0, "the fixture font has both a Global and a Local Subr INDEX");
        for (var i = 0; i < subrs; i++)
        {
            var gid = i % font.NumGlyphs;
            Assert.Equal(Uncached(table, font.CharStringsIndexOffset, gid), font.GetCharstring(gid).ToArray());
            Assert.Equal(Uncached(table, font.GlobalSubrIndexOffset, i), font.GetGlobalSubr(i).ToArray());
            Assert.Equal(Uncached(table, font.LocalSubrIndexOffset, i), font.GetLocalSubr(i).ToArray());
        }
    }

    // A CFF with a CharStrings, a Global Subr and a Local Subr INDEX of three entries each and
    // different bytes in every entry, laid out by hand so no installed font is needed. Equal counts
    // are what make an offset array kept under the wrong key answer with another INDEX's bytes.
    private static byte[] SyntheticCff()
    {
        static byte[] Index(params byte[][] entries)
        {
            if (entries.Length == 0)
                return [0, 0]; // an empty INDEX is its count alone

            var o = new List<byte> { (byte)(entries.Length >> 8), (byte)entries.Length, 1 };
            var at = 1;
            o.Add((byte)at);
            foreach (var e in entries)
            {
                at += e.Length;
                o.Add((byte)at);
            }

            foreach (var e in entries)
                o.AddRange(e);
            return [.. o];
        }

        static byte[] Int5(int v) => [29, (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

        byte[] header = [1, 0, 4, 1];
        var name = Index("Syn"u8.ToArray());
        const int topDictLength = 5 + 1 + 5 + 5 + 1; // CharStrings, then Private size and offset
        var topDictIndexLength = 2 + 1 + 2 + topDictLength;
        var strings = Index();
        var global = Index([0x11, 0x0B], [0x12, 0x13, 0x0B], [0x14, 0x0B]);
        var charStrings = Index([0x0E], [0x15, 0x0E], [0x16, 0x17, 0x0E]);
        var privateDict = new byte[] { 29, 0, 0, 0, 6, 19 }; // Subrs: the INDEX follows the dictionary
        var local = Index([0x21, 0x0B], [0x22, 0x0B], [0x23, 0x24, 0x0B]);

        var charStringsAt = header.Length + name.Length + topDictIndexLength + strings.Length + global.Length;
        var privateAt = charStringsAt + charStrings.Length;
        var topDict = Int5(charStringsAt).Append((byte)17)
            .Concat(Int5(privateDict.Length)).Concat(Int5(privateAt)).Append((byte)18).ToArray();
        Assert.Equal(topDictLength, topDict.Length);
        var topDictIndex = Index(topDict);

        return [.. header, .. name, .. topDictIndex, .. strings, .. global, .. charStrings, .. privateDict, .. local];
    }

    [Fact]
    public void IndexesOfEqualCount_keepTheirOwnOffsets()
    {
        var table = SyntheticCff();
        var font = CffFont.Parse(table);
        Assert.Equal(3, font.NumGlyphs);
        Assert.Equal(3, font.GlobalSubrCount);
        Assert.Equal(3, font.LocalSubrCount);

        // Interleaved, twice over, so each INDEX is asked for after the other two have been cached.
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal(Uncached(table, font.CharStringsIndexOffset, i), font.GetCharstring(i).ToArray());
                Assert.Equal(Uncached(table, font.GlobalSubrIndexOffset, i), font.GetGlobalSubr(i).ToArray());
                Assert.Equal(Uncached(table, font.LocalSubrIndexOffset, i), font.GetLocalSubr(i).ToArray());
            }
        }

        // The three INDEXes really differ, or the comparison above could not tell them apart.
        Assert.NotEqual(font.GetGlobalSubr(1).ToArray(), font.GetLocalSubr(1).ToArray());
        Assert.NotEqual(font.GetCharstring(1).ToArray(), font.GetGlobalSubr(1).ToArray());
    }

    [Fact]
    public void Subsets_ofOneFontAtOnce_matchSubsetsOfFreshFonts()
    {
        var table = Table();
        var shared = CffFont.Parse(table);
        int[] sizes = [1, 50, 400, shared.NumGlyphs];
        var expected = sizes.Select(n => CffSubsetter.Subset(CffFont.Parse(table), FirstGlyphs(n))).ToArray();

        // No caller shares a parsed font today (each save parses its own copy). Eight threads
        // subset one object anyway, so a later caller that shares one finds the cache sound.
        var results = new byte[8][][];
        Parallel.For(0, 8, new ParallelOptions { MaxDegreeOfParallelism = 8 }, t =>
        {
            results[t] = sizes.Select(n => CffSubsetter.Subset(shared, FirstGlyphs(n))).ToArray();
        });

        foreach (var perThread in results)
        {
            for (var i = 0; i < sizes.Length; i++)
                Assert.Equal(expected[i], perThread[i]);
        }
    }

    [Fact]
    public void Subset_bytesAreThoseOfTheUncachedReader()
    {
        var table = Table();
        var font = CffFont.Parse(table);
        int[] sizes = [1, 50, 400, font.NumGlyphs];
        var actual = sizes.Select(n =>
            Convert.ToHexString(SHA256.HashData(CffSubsetter.Subset(CffFont.Parse(table), FirstGlyphs(n))))).ToArray();
        Assert.Equal(PinnedHashes, actual);
    }

    // SHA-256 of the subset of glyphs 0..n-1 for n = 1, 50, 400 and every glyph, taken from a build
    // that reads each INDEX's offsets on every call.
    private static readonly string[] PinnedHashes = [
        "A0E9F810F6FD0353CC5B67F5BECA8899313B5C1BED784F25D233EEFD2CC3485C",
        "99241CFA7CB088AEA693C402239C536929569D614808ED51E0DF82A183AB45EA",
        "0C1B911E67718728E2FF3C17C6D51E7BC1425D806B60B66D0A9F3110F1838DBE",
        "D752AF21175BAD6DA1ADC6E60E971DFCF86691A48E6641C131E501C61DA01D86",
    ];
}
