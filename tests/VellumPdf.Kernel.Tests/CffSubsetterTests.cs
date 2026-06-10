// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text.RegularExpressions;
using VellumPdf.Fonts.Cff;
using VellumPdf.Fonts.Sfnt;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Round-trip tests for the CFF parser and subsetter.
/// Requires TeX Gyre OTF fonts to be present locally or on CI.
/// Tests are silently skipped when no font is found.
/// </summary>
public sealed partial class CffSubsetterTests
{
    // ── Font finder ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns OTF font paths to test against. Checks Windows user fonts and Linux CI paths.
    /// Returns an empty list when no fonts are found (tests will skip).
    /// </summary>
    private static IReadOnlyList<string> FindTestFonts()
    {
        var windowsUserFonts = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Windows", "Fonts");

        string[] candidates =
        [
            // Windows user-installed TeX Gyre fonts (downloaded per task spec)
            Path.Combine(windowsUserFonts, "texgyreheros-regular.otf"),
            Path.Combine(windowsUserFonts, "texgyretermes-regular.otf"),
            // Linux CI — fonts-texgyre apt package
            "/usr/share/fonts/opentype/texgyre/texgyreheros-regular.otf",
            "/usr/share/fonts/opentype/texgyre/texgyretermes-regular.otf",
            "/usr/share/fonts/opentype/texgyre/texgyreadventor-regular.otf",
            "/usr/share/fonts/opentype/texgyre/texgyrecursor-regular.otf",
            "/usr/share/fonts/opentype/texgyre/texgyrebonum-regular.otf",
        ];

        return candidates.Where(File.Exists).ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ReadOnlyMemory<byte> LoadCffTable(string otfPath)
    {
        var fontData = File.ReadAllBytes(otfPath);
        var sfnt = SfntFont.Parse(fontData);
        return sfnt.GetTableBytes(new Tag("CFF "));
    }

    [GeneratedRegex(@"^[A-Z]{6}\+")]
    private static partial Regex SubsetTagPattern();

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public void CffFont_parsesAllFoundFonts()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return; // skip: no fonts available

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData); // must not throw
            Assert.True(font.NumGlyphs > 0, $"{Path.GetFileName(path)}: NumGlyphs should be > 0");
            Assert.False(string.IsNullOrEmpty(font.FontName),
                $"{Path.GetFileName(path)}: FontName should not be empty");
        }
    }

    [Fact]
    public void Subset_roundTrip_parsesSuccessfully()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            // Used set: .notdef + GIDs 3..12 (clamped to actual numGlyphs)
            var maxGid = Math.Min(12, font.NumGlyphs - 1);
            var usedGids = Enumerable.Range(3, Math.Max(0, maxGid - 2))
                .Append(0)
                .ToHashSet();

            var subsetBytes = CffSubsetter.Subset(font, usedGids);

            // Must re-parse without exception
            var subset = CffFont.Parse(subsetBytes);
            Assert.Equal(font.NumGlyphs, subset.NumGlyphs);
        }
    }

    [Fact]
    public void Subset_usedGlyphs_preserveOriginalCharstrings()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            var maxGid = Math.Min(12, font.NumGlyphs - 1);
            var usedGids = Enumerable.Range(3, Math.Max(0, maxGid - 2))
                .Append(0)
                .ToHashSet();

            var subsetBytes = CffSubsetter.Subset(font, usedGids);
            var subset = CffFont.Parse(subsetBytes);

            foreach (var gid in usedGids)
            {
                var original = font.GetCharstring(gid).ToArray();
                var subsetted = subset.GetCharstring(gid).ToArray();
                Assert.Equal(original, subsetted);
            }
        }
    }

    [Fact]
    public void Subset_unusedGlyphs_replacedWithEndchar()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            var maxGid = Math.Min(12, font.NumGlyphs - 1);
            var usedGids = Enumerable.Range(3, Math.Max(0, maxGid - 2))
                .Append(0)
                .ToHashSet();

            var subsetBytes = CffSubsetter.Subset(font, usedGids);
            var subset = CffFont.Parse(subsetBytes);

            // Check a sample of unused GIDs
            for (var gid = 1; gid <= maxGid; gid++)
            {
                if (usedGids.Contains(gid)) continue;
                var cs = subset.GetCharstring(gid).ToArray();
                Assert.Equal([0x0E], cs);
            }
        }
    }

    [Fact]
    public void Subset_outputIsSmaller()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            var maxGid = Math.Min(12, font.NumGlyphs - 1);
            var usedGids = Enumerable.Range(3, Math.Max(0, maxGid - 2))
                .Append(0)
                .ToHashSet();

            var subsetBytes = CffSubsetter.Subset(font, usedGids);
            var originalSize = cffData.Length;
            var subsetSize = subsetBytes.Length;

            Console.WriteLine(
                $"[CffSubsetterTests] {Path.GetFileName(path)}: " +
                $"original CFF={originalSize:N0} bytes, subset CFF={subsetSize:N0} bytes, " +
                $"reduction={100.0 * (originalSize - subsetSize) / originalSize:F1}%");

            Assert.True(subsetSize < originalSize,
                $"{Path.GetFileName(path)}: subset ({subsetSize} bytes) should be smaller than original ({originalSize} bytes)");
        }
    }

    [Fact]
    public void Subset_isDeterministic()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            var maxGid = Math.Min(12, font.NumGlyphs - 1);
            var usedGids = Enumerable.Range(3, Math.Max(0, maxGid - 2))
                .Append(0)
                .ToHashSet();

            var first = CffSubsetter.Subset(font, usedGids);
            var second = CffSubsetter.Subset(font, usedGids);

            Assert.Equal(first, second);

            // Also verify with a new IReadOnlySet instance to confirm no HashSet iteration order leakage
            var usedGids2 = new SortedSet<int>(usedGids);
            var third = CffSubsetter.Subset(font, usedGids2);
            Assert.Equal(first, third);
        }
    }

    [Fact]
    public void Subset_fontNameHasSubsetTag()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            var maxGid = Math.Min(12, font.NumGlyphs - 1);
            var usedGids = Enumerable.Range(3, Math.Max(0, maxGid - 2))
                .Append(0)
                .ToHashSet();

            var subsetBytes = CffSubsetter.Subset(font, usedGids);
            var subset = CffFont.Parse(subsetBytes);

            Assert.Matches(SubsetTagPattern(), subset.FontName);
        }
    }

    [Fact]
    public void Subset_numGlyphsUnchanged()
    {
        var fonts = FindTestFonts();
        if (fonts.Count == 0) return;

        foreach (var path in fonts)
        {
            var cffData = LoadCffTable(path);
            var font = CffFont.Parse(cffData);

            var usedGids = new HashSet<int> { 0, 1, 5 };
            var subsetBytes = CffSubsetter.Subset(font, usedGids);
            var subset = CffFont.Parse(subsetBytes);

            Assert.Equal(font.NumGlyphs, subset.NumGlyphs);
        }
    }
}
