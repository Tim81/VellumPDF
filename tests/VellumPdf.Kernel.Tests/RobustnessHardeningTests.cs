// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Fonts.Cff;
using VellumPdf.Fonts.Sfnt;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Regression tests for the adversarial pre-release robustness-hardening pass.
/// Each test targets a specific malformed-input code path and asserts the correct
/// exception type — never an uncaught IndexOutOfRangeException or ArgumentOutOfRangeException.
/// These tests do NOT require real font files; all inputs are synthesised in memory.
/// </summary>
public sealed class RobustnessHardeningTests
{
    // ── B3: CffFont.IndexTotalLength — zero last offset ──────────────────────

    /// <summary>
    /// Constructs a minimal CFF INDEX whose last offset field is 0.
    /// A valid CFF INDEX has offset[0] == 1 so the last offset is always >= 1.
    /// The zero case must throw InvalidDataException, not silently compute a negative
    /// data-size and mis-parse everything downstream.
    /// </summary>
    [Fact]
    public void CffFont_IndexTotalLength_zeroLastOffset_throwsInvalidDataException()
    {
        // Build the smallest possible 1-byte offSize INDEX with count=1 and lastOffset=0.
        // Layout: count(2) offSize(1) offset[0](1) offset[1](1) [no data]
        var index = new byte[]
        {
            0x00, 0x01, // count = 1
            0x01,       // offSize = 1
            0x01,       // offset[0] = 1 (start; valid per spec)
            0x00,       // offset[1] = 0 (invalid: must be >= 1)
        };

        var ex = Assert.Throws<InvalidDataException>(() =>
            CffFont.IndexTotalLength(index, pos: 0, totalLen: index.Length));

        Assert.Contains("zero last offset", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── B2: CffFont.Parse — negative Name INDEX length ───────────────────────

    /// <summary>
    /// Builds a minimal CFF header + Name INDEX where the last-offset field in the
    /// INDEX is smaller than the first-offset, making the computed index length
    /// negative. Parse must throw InvalidDataException.
    /// </summary>
    [Fact]
    public void CffFont_Parse_negativeNameIndexLength_throwsInvalidDataException()
    {
        // CFF header: major=1 minor=0 hdrSize=4 offSize=1
        // Name INDEX: count=1 offSize=1 offset[0]=5 offset[1]=2
        // offset[1] < offset[0] → length = dataBase + 2 - 1 - pos < 0
        var cff = new byte[]
        {
            0x01, 0x00, 0x04, 0x01, // CFF header
            0x00, 0x01,             // Name INDEX count = 1
            0x01,                   // offSize = 1
            0x05,                   // offset[0] = 5
            0x02,                   // offset[1] = 2 (< offset[0] → negative length)
        };

        Assert.Throws<InvalidDataException>(() =>
            CffFont.Parse(new ReadOnlyMemory<byte>(cff)));
    }

    // ── B1: CffFont.Parse — privateDictSize int.MaxValue overflow ─────────────

    /// <summary>
    /// Verifies that a Top DICT Private entry whose size operand is int.MaxValue does
    /// not cause the int bounds check (privateDictOffset + privateDictSize <= len) to
    /// overflow to a negative number and slip through. The fix uses long arithmetic
    /// so the addition does not overflow. Parse must not throw ArgumentOutOfRangeException
    /// (the type that would escape the caller's InvalidDataException catch); it must
    /// either succeed (skipping the malformed private dict) or throw InvalidDataException.
    /// </summary>
    [Fact]
    public void CffFont_Parse_privateDictSizeOverflow_doesNotThrowArgumentOutOfRangeException()
    {
        // Build a minimal but structurally valid CFF so Parse reaches the private-dict
        // bounds check. We craft a Top DICT with Private (op 18) size=int.MaxValue, offset=4.
        // The bounds check (long)(4) + int.MaxValue > len is true so the private dict is
        // skipped — Parse must not throw ArgumentOutOfRangeException.

        // We use a pre-built minimal CFF from scratch.
        // CFF header: 4 bytes
        // Name INDEX: empty (count=0): 2 bytes
        // Top DICT INDEX: count=1, offSize=1, offset[0]=1, offset[1]=N, data
        //   Top DICT data: encode int.MaxValue (5-byte 0x1D form), encode 4 (offset),
        //                  then op 18 (Private). Then CharStrings (op 17) with offset=1
        //                  (will fail bounds check, but that's a different IDEx).
        //   Actually we need CharStrings to point somewhere valid to avoid a different error.
        //   Keep it simple: make Parse throw InvalidDataException on CharStrings offset
        //   being out of range — that is fine, it must NOT be ArgumentOutOfRangeException.
        using var ms = new MemoryStream();

        // CFF header
        ms.Write([0x01, 0x00, 0x04, 0x01]);

        // Name INDEX — empty
        ms.Write([0x00, 0x00]);

        // Top DICT INDEX — count=1, offSize=1
        // We'll write the top dict data first to know its length.
        var topDict = new MemoryStream();
        // Private op 18: operands are size (int.MaxValue) then offset (4)
        // Encode int.MaxValue = 0x7FFFFFFF using 5-byte form: b0=0x1D, then 4 bytes
        topDict.WriteByte(0x1D);
        topDict.WriteByte(0x7F);
        topDict.WriteByte(0xFF);
        topDict.WriteByte(0xFF);
        topDict.WriteByte(0xFF);
        // Encode offset 4 using 1-byte form: 4 + 139 = 143
        topDict.WriteByte(143);
        // op 18 (Private)
        topDict.WriteByte(18);
        // CharStrings (op 17): encode offset 1 (out of range but gives IDEx not AOORE)
        topDict.WriteByte(140); // encodes 1 (1+139=140)
        topDict.WriteByte(17);

        var topDictData = topDict.ToArray();
        var tdLen = (byte)(topDictData.Length + 1); // +1 because offsets are 1-based
        // Top DICT INDEX header
        ms.Write([0x00, 0x01, 0x01, 0x01, tdLen]);
        ms.Write(topDictData);

        // String INDEX — empty
        ms.Write([0x00, 0x00]);

        // Global Subr INDEX — empty
        ms.Write([0x00, 0x00]);

        var cff = ms.ToArray();

        // Must not throw ArgumentOutOfRangeException — it is acceptable to throw
        // InvalidDataException (e.g. because CharStrings offset 1 is out of range)
        // or to succeed. Either way ArgumentOutOfRangeException must not escape.
        try
        {
            CffFont.Parse(new ReadOnlyMemory<byte>(cff));
            // If it succeeds, the private dict was skipped — fine.
        }
        catch (InvalidDataException)
        {
            // Expected alternative — malformed CharStrings offset or similar.
        }
        // Any other exception type (ArgumentOutOfRangeException etc.) causes the test to fail.
    }

    // ── B3 via CffFont.Parse: String INDEX with zero last offset ─────────────

    /// <summary>
    /// Builds a minimal valid CFF up to the String INDEX, then gives the String INDEX
    /// a zero last-offset. IndexTotalLength is called on the String INDEX during Parse,
    /// so this exercises the B3 fix through the full Parse path.
    /// </summary>
    [Fact]
    public void CffFont_Parse_stringIndexZeroLastOffset_throwsInvalidDataException()
    {
        using var ms = new MemoryStream();

        // CFF header
        ms.Write([0x01, 0x00, 0x04, 0x01]);

        // Name INDEX — empty
        ms.Write([0x00, 0x00]);

        // Top DICT INDEX with a tiny valid entry (just op 14 = endchar, interpreted
        // as an unknown op; no crash since ParseTopDict tolerates missing ops).
        // count=1, offSize=1, offset[0]=1, offset[1]=2, data=one byte
        ms.Write([0x00, 0x01, 0x01, 0x01, 0x02, 0x8B]); // 0x8B encodes operand 0

        // String INDEX — count=1, offSize=1, offset[0]=1, offset[1]=0 (invalid!)
        ms.Write([0x00, 0x01, 0x01, 0x01, 0x00]);

        var cff = ms.ToArray();
        Assert.Throws<InvalidDataException>(() =>
            CffFont.Parse(new ReadOnlyMemory<byte>(cff)));
    }

    // ── C2: CmapTable.ParseFormat6 — range exceeds BMP ───────────────────────

    /// <summary>
    /// Builds a cmap with a format-6 subtable where firstCode + entryCount > 0x10000.
    /// ParseFormat6 must throw InvalidDataException before iterating the array.
    /// </summary>
    [Fact]
    public void CmapTable_Format6_rangeExceedsBmp_throwsInvalidDataException()
    {
        // firstCode = 0xFF00, entryCount = 0x200 → 0xFF00 + 0x200 = 0x10100 > 0x10000
        var firstCode = (ushort)0xFF00;
        var entryCount = (ushort)0x0200;

        // Build the subtable with enough fake entries to not trigger truncation first.
        var subtableData = new byte[10 + entryCount * 2];
        WriteU16Be(subtableData, 0, 6);
        WriteU16Be(subtableData, 2, (ushort)subtableData.Length);
        WriteU16Be(subtableData, 4, 0);
        WriteU16Be(subtableData, 6, firstCode);
        WriteU16Be(subtableData, 8, entryCount);
        // glyphIdArray — all zeros (gid 0 = unmapped, irrelevant since we throw first)

        var cmap = BuildSingleSubtableCmap(platform: 3, encoding: 1, subtableData: subtableData);
        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));

        var ex = Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
        Assert.Contains("BMP", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── C3: CmapTable.Parse — implausible numTabs ────────────────────────────

    /// <summary>
    /// Builds a minimal cmap whose numTables field is 0x5000 (well above 0x4000 cap).
    /// Parse must throw InvalidDataException rather than looping 20480 times.
    /// </summary>
    [Fact]
    public void CmapTable_ImplausibleNumTabs_throwsInvalidDataException()
    {
        // Just a cmap header — version(0), numTables(0x5000); no actual records needed
        // because we throw before entering the loop.
        var cmap = new byte[4];
        WriteU16Be(cmap, 0, 0);       // version
        WriteU16Be(cmap, 2, 0x5000);  // numTables = 20480

        var font = SfntFont.Parse(BuildFont(("cmap", cmap)));

        var ex = Assert.Throws<InvalidDataException>(() => CmapTable.Parse(font));
        Assert.Contains("implausible", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ── D: PdfCanvas empty-component guards ──────────────────────────────────

    [Fact]
    public void PdfCanvas_SetFillColor_noComponents_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        var ex = Assert.Throws<ArgumentException>(() =>
            canvas.SetFillColor(ReadOnlySpan<double>.Empty));

        Assert.Contains("component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PdfCanvas_SetStrokeColor_noComponents_throwsArgumentException()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);

        var ex = Assert.Throws<ArgumentException>(() =>
            canvas.SetStrokeColor(ReadOnlySpan<double>.Empty));

        Assert.Contains("component", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confirms that SetFillColor and SetStrokeColor still work normally when given
    /// a non-empty span — i.e. the guard does not affect valid callers.
    /// </summary>
    [Fact]
    public void PdfCanvas_SetFillColor_withComponents_doesNotThrow()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        // Should not throw
        canvas.SetFillColorSpace("CS1");
        canvas.SetFillColor(0.5);
    }

    [Fact]
    public void PdfCanvas_SetStrokeColor_withComponents_doesNotThrow()
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var canvas = new PdfCanvas(page);
        // Should not throw
        canvas.SetStrokeColorSpace("CS1");
        canvas.SetStrokeColor(0.5, 0.3);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a cmap table with a single encoding record pointing to the given raw
    /// subtable bytes. The subtable offset is placed right after the one record header.
    /// </summary>
    private static byte[] BuildSingleSubtableCmap(ushort platform, ushort encoding, byte[] subtableData)
    {
        var subtableOffset = 4 + 1 * 8; // cmap header(4) + one 8-byte encoding record
        var cmap = new byte[subtableOffset + subtableData.Length];
        WriteU16Be(cmap, 0, 0);             // version
        WriteU16Be(cmap, 2, 1);             // numTables
        WriteU16Be(cmap, 4, platform);
        WriteU16Be(cmap, 6, encoding);
        WriteU32Be(cmap, 8, (uint)subtableOffset);
        subtableData.CopyTo(cmap, subtableOffset);
        return cmap;
    }

    /// <summary>Builds a minimal sfnt with the given tables (mirrors MalformedInputTests.BuildFont).</summary>
    private static byte[] BuildFont(params (string Tag, byte[] Data)[] tables)
    {
        var offsets = new int[tables.Length];
        var pos = 12 + tables.Length * 16;
        for (var i = 0; i < tables.Length; i++)
        {
            offsets[i] = pos;
            pos += (tables[i].Data.Length + 3) & ~3;
        }

        var font = new byte[pos];
        WriteU32Be(font, 0, 0x00010000);
        WriteU16Be(font, 4, (ushort)tables.Length);
        for (var i = 0; i < tables.Length; i++)
        {
            var rec = 12 + i * 16;
            Encoding.ASCII.GetBytes(tables[i].Tag).CopyTo(font, rec);
            WriteU32Be(font, rec + 8, (uint)offsets[i]);
            WriteU32Be(font, rec + 12, (uint)tables[i].Data.Length);
            tables[i].Data.CopyTo(font, offsets[i]);
        }
        return font;
    }

    private static void WriteU16Be(byte[] buf, int offset, ushort value)
    {
        buf[offset] = (byte)(value >> 8);
        buf[offset + 1] = (byte)value;
    }

    private static void WriteU32Be(byte[] buf, int offset, uint value)
    {
        buf[offset] = (byte)(value >> 24);
        buf[offset + 1] = (byte)(value >> 16);
        buf[offset + 2] = (byte)(value >> 8);
        buf[offset + 3] = (byte)value;
    }
}
