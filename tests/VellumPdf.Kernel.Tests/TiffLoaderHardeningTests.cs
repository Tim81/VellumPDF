// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Images;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Known-answer and bound tests for <see cref="TiffImageLoader"/> (#512, #534, #536). Every
/// fixture is built byte by byte from <see cref="Tiff"/> so each offset and count is the one the
/// test names.
/// </summary>
public sealed class TiffLoaderHardeningTests
{
    // Allocation bound for a refusal, measured per calling thread as MalformedInputTests does for
    // PNG. 1 MiB sits far under the sizes the loader allocated before it refused (1,000,002,480
    // bytes for the ColorMap count below, 800,002,400 for the raster) and far above what a refusal
    // costs, which is a few kilobytes.
    private const long RefusalAllocationBound = 1024 * 1024;

    private static readonly byte[] FourBytes = [0, 1, 2, 3];

    // ── #512, #534: a ColorMap the loader cannot use is an InvalidDataException ─

    [Theory]
    [InlineData(768u, 0x80000000u)] // a full map at a negative offset: the 198-byte case in #512
    [InlineData(3u, 0xFFFFFFFEu)]   // -2: a small negative offset
    [InlineData(1u, 0x80000000u)]
    public void ColorMap_negativeOffset_onAPaletteImage_isRefused(uint count, uint offsetField)
    {
        var tiff = Image(2, 2, photometric: 3, bps: 8, strip: FourBytes, colorMapCount: count, colorMapField: offsetField);

        var ex = Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
        Assert.Contains("ColorMap offset", ex.Message);
    }

    [Fact]
    public void ColorMap_negativeOffset_onAGreyscaleImage_isRefusedToo()
    {
        // ColorMap is read in the directory loop before any photometric check, so the photometric
        // does not matter. 2.3.3 threw IndexOutOfRangeException here as well.
        var tiff = Image(2, 2, photometric: 1, bps: 8, strip: FourBytes, colorMapCount: 6, colorMapField: 0x80000000u);

        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    [Theory]
    [InlineData(500_000_000u)]
    [InlineData(1_073_741_823u)]
    public void ColorMap_negativeOffset_withAHugeCount_isRefusedWithoutAllocatingFromTheCount(uint count)
    {
        // 2.3.3 sized `new ushort[count]` before it threw: 1,000,002,480 bytes for 500,000,000 and
        // 2,147,486,368 for 1,073,741,823, measured on the calling thread.
        var tiff = Image(2, 2, photometric: 3, bps: 8, strip: FourBytes, colorMapCount: count, colorMapField: 0x80000000u);

        // Warm-up, so one-time JIT and static-initialiser allocations are not charged below.
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));

        var before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            allocated < RefusalAllocationBound,
            $"expected under {RefusalAllocationBound} bytes allocated before refusing; measured {allocated}.");
    }

    [Fact]
    public void ColorMap_countZero_onAPaletteImage_isRefused_notAnIndexOutOfRange()
    {
        // An in-range offset (100) and no map: BuildPalette clamped every index to 0 and then read
        // element 0 of an empty array.
        var tiff = Image(2, 2, photometric: 3, bps: 8, strip: FourBytes, colorMapCount: 0, colorMapField: 100);

        var ex = Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
        Assert.Contains("empty ColorMap", ex.Message);
    }

    [Theory]
    [InlineData(0u, 100u)]          // in-range offset, nothing to read
    [InlineData(0u, 0x80000000u)]   // negative offset, but a count of zero never dereferences it
    public void ColorMap_countZero_onAGreyscaleImage_keepsLoading(uint count, uint offsetField)
    {
        // 2.3.3 loaded both of these (probed), so they keep loading. Pixels are stored as is.
        var tiff = Image(2, 2, photometric: 1, bps: 8, strip: FourBytes, colorMapCount: count, colorMapField: offsetField);

        var img = TiffImageLoader.Load(tiff);

        Assert.Equal(2, img.Width);
        Assert.Equal(2, img.Height);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void ColorMap_oneOrTwoEntries_keepsLoading_everyPixelReadsEntryZero(int entries)
    {
        // paletteSize is 0 for one and two entries as well, but colorMap[0] exists, so 2.3.3
        // loaded these and read entry 0 for all three channels. 0x1234 >> 8 = 0x12.
        var map = new byte[entries * 2];
        map[0] = 0x34;
        map[1] = 0x12;
        var tiff = Image(2, 1, photometric: 3, bps: 8, strip: [0, 3], colorMap: map);

        var rgb = RgbBytes(TiffImageLoader.Load(tiff));

        Assert.Equal<byte[]>([0x12, 0x12, 0x12, 0x12, 0x12, 0x12], rgb);
    }

    // ── #512: a directory or value offset whose bounds sum wraps ──────────────

    [Theory]
    [InlineData(0x7FFFFFFEu)]
    [InlineData(0x7FFFFFFFu)]
    public void DirectoryOffset_nearIntMax_isRefused(uint ifdOffset)
    {
        // `ifdOffset + 2 > length` wrapped negative for these and let the read through.
        var tiff = Tiff(true, [new Entry(256, 3, 1, 2)], tail: null, ifdOffset: ifdOffset);

        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    [Theory]
    [InlineData((ushort)3, 3u)] // SHORT x 3 = 6 bytes, so the value field is an offset
    [InlineData((ushort)1, 5u)] // BYTE x 5 = 5 bytes
    public void ScalarTagValueOffset_nearIntMax_isRefused(ushort type, uint count)
    {
        var tiff = Tiff(true,
        [
            new Entry(256, type, count, 0x7FFFFFFFu),
            new Entry(257, 3, 1, 2),
            new Entry(262, 3, 1, 1),
        ]);

        Assert.Throws<InvalidDataException>(() => TiffImageLoader.Load(tiff));
    }

    // ── Builders ──────────────────────────────────────────────────────────────

    /// <summary>One IFD entry. <paramref name="Blob"/> overrides <paramref name="Value"/> with its offset.</summary>
    internal readonly record struct Entry(ushort Tag, ushort Type, uint Count, uint Value, byte[]? Blob = null);

    /// <summary>
    /// A little- or big-endian TIFF: header, one IFD (entries sorted by tag), then each entry's blob
    /// in order, then <paramref name="tail"/>. <paramref name="ifdOffset"/> overrides the offset the
    /// header declares.
    /// </summary>
    internal static byte[] Tiff(bool littleEndian, Entry[] entries, byte[]? tail = null, uint? ifdOffset = null)
    {
        var sorted = entries.OrderBy(e => e.Tag).ToArray();
        var dataStart = 8 + 2 + sorted.Length * 12 + 4;
        var position = dataStart;
        var blobOffsets = new int[sorted.Length];
        for (var i = 0; i < sorted.Length; i++)
        {
            if (sorted[i].Blob is { } blob)
            {
                blobOffsets[i] = position;
                position += blob.Length;
            }
        }
        var tailOffset = position;
        position += tail?.Length ?? 0;

        var b = new byte[position];
        void W16(int o, ushort v)
        {
            if (littleEndian) { b[o] = (byte)v; b[o + 1] = (byte)(v >> 8); }
            else { b[o] = (byte)(v >> 8); b[o + 1] = (byte)v; }
        }
        void W32(int o, uint v)
        {
            for (var k = 0; k < 4; k++)
                b[littleEndian ? o + k : o + 3 - k] = (byte)(v >> (8 * k));
        }

        b[0] = b[1] = littleEndian ? (byte)0x49 : (byte)0x4D;
        W16(2, 42);
        W32(4, ifdOffset ?? 8u);
        W16(8, (ushort)sorted.Length);
        for (var i = 0; i < sorted.Length; i++)
        {
            var o = 10 + i * 12;
            W16(o, sorted[i].Tag);
            W16(o + 2, sorted[i].Type);
            W32(o + 4, sorted[i].Count);
            // An inline SHORT sits in the first two bytes of the field, as the loader reads it.
            if (sorted[i].Blob is not null) W32(o + 8, (uint)blobOffsets[i]);
            else if (sorted[i].Type == 3 && sorted[i].Count == 1 && sorted[i].Tag != 320) W16(o + 8, (ushort)sorted[i].Value);
            else W32(o + 8, sorted[i].Value);
        }
        foreach (var (e, i) in sorted.Select((e, i) => (e, i)))
            e.Blob?.CopyTo(b, blobOffsets[i]);
        tail?.CopyTo(b, tailOffset);
        return b;
    }

    /// <summary>
    /// A single-strip little-endian TIFF with the baseline tags. The strip follows the IFD and
    /// any ColorMap blob. <paramref name="colorMap"/> is written as an out-of-line blob;
    /// <paramref name="colorMapCount"/> and <paramref name="colorMapField"/> declare a ColorMap
    /// tag with that raw count and value field instead (no blob), so a test can aim the offset.
    /// </summary>
    internal static byte[] Image(
        int width, int height, int photometric, int bps, byte[] strip,
        int compression = 1, int samplesPerPixel = 1, int rowsPerStrip = 0,
        byte[]? colorMap = null, uint? colorMapCount = null, uint? colorMapField = null,
        int? predictor = null)
    {
        var entries = new List<Entry>
        {
            new(256, 3, 1, (uint)width),
            new(257, 3, 1, (uint)height),
            new(258, 3, 1, (uint)bps),
            new(259, 3, 1, (uint)compression),
            new(262, 3, 1, (uint)photometric),
            new(277, 3, 1, (uint)samplesPerPixel),
        };
        if (rowsPerStrip > 0) entries.Add(new Entry(278, 3, 1, (uint)rowsPerStrip));
        if (predictor is int p) entries.Add(new Entry(317, 3, 1, (uint)p));
        if (colorMap is not null)
            entries.Add(new Entry(320, 3, (uint)(colorMap.Length / 2), 0, colorMap));
        else if (colorMapCount is not null)
            entries.Add(new Entry(320, 3, colorMapCount.Value, colorMapField ?? 0));

        // Strip offset = start of the tail: after the header, IFD (with the two strip entries
        // added below) and every blob.
        var ifdBytes = 2 + (entries.Count + 2) * 12 + 4;
        var blobBytes = entries.Sum(e => e.Blob?.Length ?? 0);
        entries.Add(new Entry(273, 4, 1, (uint)(8 + ifdBytes + blobBytes)));
        entries.Add(new Entry(279, 4, 1, (uint)strip.Length));
        return Tiff(true, [.. entries], strip);
    }

    // The image's raster bytes, recovered through the public surface: serialise the stream and
    // inflate what follows the "stream" keyword.
    internal static byte[] RgbBytes(PdfImageXObject img)
    {
        using var ms = new MemoryStream();
        img.BuildStream().WriteTo(new VellumPdf.IO.PdfWriter(ms));
        var raw = ms.ToArray();
        var start = IndexOf(raw, "\nstream\n"u8) + 8;
        var end = IndexOf(raw, "\nendstream"u8);
        using var z = new System.IO.Compression.ZLibStream(
            new MemoryStream(raw[start..end]), System.IO.Compression.CompressionMode.Decompress);
        using var outMs = new MemoryStream();
        z.CopyTo(outMs);
        return outMs.ToArray();
    }

    private static int IndexOf(byte[] data, ReadOnlySpan<byte> needle)
    {
        var i = data.AsSpan().IndexOf(needle);
        Assert.True(i >= 0, "marker not found in the serialised stream");
        return i;
    }
}
