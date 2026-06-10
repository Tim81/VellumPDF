// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Fonts.Cff;

/// <summary>
/// Produces a subset of a CFF font table using the "blank-unused" strategy:
/// every unused glyph's CharString is replaced with a single <c>endchar</c> (0x0E) byte,
/// while the GID space and all other CFF structures are preserved intact.
/// This preserves CID==GID identity required by Type0/CIDFontType0 with Identity-H.
/// </summary>
internal static class CffSubsetter
{
    private static readonly byte[] EndcharByte = [0x0E];

    /// <summary>
    /// Subsets <paramref name="font"/>, replacing unused glyph CharStrings with a single
    /// <c>endchar</c> byte. GID 0 (.notdef) is always included.
    /// </summary>
    /// <param name="font">Parsed CFF font.</param>
    /// <param name="usedGids">Set of GIDs to keep verbatim; GID 0 is always added.</param>
    /// <returns>A new CFF table byte array with the subset applied.</returns>
    public static byte[] Subset(CffFont font, IReadOnlySet<int> usedGids)
    {
        // Ensure .notdef and sort for determinism
        var effectiveGids = new HashSet<int>(usedGids) { 0 };

        // ── 1. Build the new CharStrings INDEX ───────────────────────────────
        var charstrings = new byte[font.NumGlyphs][];
        for (var gid = 0; gid < font.NumGlyphs; gid++)
        {
            charstrings[gid] = effectiveGids.Contains(gid)
                ? font.GetCharstring(gid).ToArray()
                : EndcharByte;
        }

        var (newCsIndexBytes, csIndexSize) = BuildIndex(charstrings);

        // ── 2. Build the new Name INDEX with subset tag ───────────────────────
        var tag = ComputeSubsetTag(effectiveGids);
        var newFontName = $"{tag}+{font.FontName}";
        var nameBytes = Encoding.Latin1.GetBytes(newFontName);
        var (newNameIndexBytes, _) = BuildIndex([nameBytes]);

        // ── 3. Lay out the canonical CFF structure ───────────────────────────
        //
        //  [Header]
        //  [Name INDEX]            ← patched FontName
        //  [Top DICT INDEX]        ← rebuilt with corrected offsets
        //  [String INDEX]          ← verbatim copy
        //  [Global Subr INDEX]     ← verbatim copy
        //  [CharStrings INDEX]     ← rebuilt (blank-unused)
        //  [charset]               ← verbatim copy (if offset-based, > 2)
        //  [Encoding]              ← verbatim copy (if offset-based, > 2)
        //  [FDArray]               ← verbatim copy (if CID-keyed)
        //  [FDSelect]              ← verbatim copy (if CID-keyed)
        //  [Private DICT]          ← verbatim copy
        //
        // We place each section sequentially, then rebuild the Top DICT.

        var src = font.Data.Span;

        // Verbatim slices
        var headerBytes = src[..font.NameIndexOffset].ToArray(); // CFF header (hdrSize bytes)
        var stringIndexBytes = src.Slice(font.StringIndexOffset, font.StringIndexLength).ToArray();
        var globalSubrBytes = src.Slice(font.GlobalSubrIndexOffset, font.GlobalSubrIndexLength).ToArray();

        // charset — only copy if it is an offset (> 2); predefined enumerations need no data
        byte[] charsetBytes = [];
        var charsetIsOffset = font.CharsetOffset > 2;
        if (charsetIsOffset && font.CharsetOffset + 1 <= src.Length)
        {
            // Length is not stored explicitly — copy everything up to the next known section.
            // We derive length by comparing positions of adjacent sections.
            var charsetLen = NextSectionOffset(font) - font.CharsetOffset;
            if (charsetLen > 0 && font.CharsetOffset + charsetLen <= src.Length)
                charsetBytes = src.Slice(font.CharsetOffset, charsetLen).ToArray();
        }

        // Encoding — only copy if offset-based (> 1)
        byte[] encodingBytes = [];
        var encodingIsOffset = font.EncodingOffset > 1;
        if (encodingIsOffset && font.EncodingOffset > 0)
        {
            var encLen = NextSectionOffsetAfterEncoding(font) - font.EncodingOffset;
            if (encLen > 0 && font.EncodingOffset + encLen <= src.Length)
                encodingBytes = src.Slice(font.EncodingOffset, encLen).ToArray();
        }

        // FDArray (CID-keyed)
        byte[] fdArrayBytes = [];
        if (font.IsCidKeyed && font.FdArrayOffset > 0)
        {
            var fdArrayLen = FdArrayLength(font, src);
            if (fdArrayLen > 0)
                fdArrayBytes = src.Slice(font.FdArrayOffset, fdArrayLen).ToArray();
        }

        // FDSelect (CID-keyed)
        byte[] fdSelectBytes = [];
        if (font.IsCidKeyed && font.FdSelectOffset > 0)
        {
            var fdSelectLen = FdSelectLength(font, src);
            if (fdSelectLen > 0)
                fdSelectBytes = src.Slice(font.FdSelectOffset, fdSelectLen).ToArray();
        }

        // Private DICT
        byte[] privateDictBytes = [];
        if (font.PrivateDictSize > 0 && font.PrivateDictOffset > 0
            && font.PrivateDictOffset + font.PrivateDictSize <= src.Length)
        {
            privateDictBytes = src.Slice(font.PrivateDictOffset, font.PrivateDictSize).ToArray();
        }

        // ── 4. Compute layout offsets ────────────────────────────────────────
        var cursor = headerBytes.Length;

        var nameIndexStart = cursor;
        cursor += newNameIndexBytes.Length;

        var topDictIndexStart = cursor;
        // Top DICT INDEX is rebuilt; we need a placeholder size first.
        // We'll compute the Top DICT size iteratively (two-pass).
        cursor += 0; // will be filled in after computing top dict

        var stringIndexStart = cursor + 0; // will shift after top dict size is known
        // We do a two-pass: first compute all section sizes, then build.

        // -- Section positions (relative to start of CFF output) --
        // We know the fixed-size sections, just need to know the TopDICT INDEX size.
        // The top dict INDEX size depends on the offsets we put in it,
        // which depend on the top dict INDEX size. We break this with a fixed-size
        // encoding strategy: always use 4-byte (b0=29) integers for offsets (5 bytes each).

        // Count how many offset operands need patching and their encoded size:
        // CharStrings: 1 operand, op=17         → 5+1 = 6 bytes
        // charset (if offset): 1 operand, op=15 → 5+1 = 6 bytes
        // Encoding (if offset): 1 operand, op=16 → 5+1 = 6 bytes
        // Private (always): 2 operands + op=18  → 5+5+1 = 11 bytes
        // FDArray (if present): 1 operand + op 12 36 → 5+2 = 7 bytes
        // FDSelect (if present): 1 operand + op 12 37 → 5+2 = 7 bytes
        //
        // We rebuild the Top DICT by stripping those operators from the original
        // and appending the rebuilt ones at the end.

        var rebuildResult = RebuildTopDictBytes(
            font,
            charStringsNewOffset: 0, // placeholder
            charsetNewOffset: 0,
            encodingNewOffset: 0,
            privateDictNewOffset: 0,
            fdArrayNewOffset: 0,
            fdSelectNewOffset: 0,
            charsetIsOffset: charsetIsOffset,
            encodingIsOffset: encodingIsOffset);

        var topDictData = rebuildResult;
        var (topDictIndexBytes, _) = BuildIndex([topDictData]);
        var topDictIndexSize = topDictIndexBytes.Length;

        // Now lay out everything
        cursor = headerBytes.Length + newNameIndexBytes.Length;
        var topDictStart = cursor;
        cursor += topDictIndexSize;

        var strStart = cursor;
        cursor += stringIndexBytes.Length;

        var gsubrStart = cursor;
        cursor += globalSubrBytes.Length;

        var csStart = cursor;
        cursor += newCsIndexBytes.Length;

        var charsetStart = charsetIsOffset ? cursor : font.CharsetOffset;
        if (charsetIsOffset) cursor += charsetBytes.Length;

        var encodingStart = encodingIsOffset ? cursor : font.EncodingOffset;
        if (encodingIsOffset) cursor += encodingBytes.Length;

        int fdArrayStart = 0, fdSelectStart = 0;
        if (font.IsCidKeyed)
        {
            if (font.FdArrayOffset > 0)
            {
                fdArrayStart = cursor;
                cursor += fdArrayBytes.Length;
            }
            if (font.FdSelectOffset > 0)
            {
                fdSelectStart = cursor;
                cursor += fdSelectBytes.Length;
            }
        }

        var privateDictStart = cursor;
        cursor += privateDictBytes.Length;

        // ── 5. Rebuild Top DICT with real offsets ────────────────────────────
        topDictData = RebuildTopDictBytes(
            font,
            charStringsNewOffset: csStart,
            charsetNewOffset: charsetIsOffset ? charsetStart : font.CharsetOffset,
            encodingNewOffset: encodingIsOffset ? encodingStart : font.EncodingOffset,
            privateDictNewOffset: privateDictBytes.Length > 0 ? privateDictStart : 0,
            fdArrayNewOffset: fdArrayBytes.Length > 0 ? fdArrayStart : 0,
            fdSelectNewOffset: fdSelectBytes.Length > 0 ? fdSelectStart : 0,
            charsetIsOffset: charsetIsOffset,
            encodingIsOffset: encodingIsOffset);

        (topDictIndexBytes, _) = BuildIndex([topDictData]);

        // Sanity: top dict index size should match what we computed earlier
        if (topDictIndexBytes.Length != topDictIndexSize)
        {
            // This can happen if the rebuilt top dict size differs from placeholder.
            // Re-lay out everything with the actual size.
            topDictIndexSize = topDictIndexBytes.Length;

            cursor = headerBytes.Length + newNameIndexBytes.Length;
            topDictStart = cursor;
            cursor += topDictIndexSize;

            strStart = cursor;
            cursor += stringIndexBytes.Length;

            gsubrStart = cursor;
            cursor += globalSubrBytes.Length;

            csStart = cursor;
            cursor += newCsIndexBytes.Length;

            charsetStart = charsetIsOffset ? cursor : font.CharsetOffset;
            if (charsetIsOffset) cursor += charsetBytes.Length;

            encodingStart = encodingIsOffset ? cursor : font.EncodingOffset;
            if (encodingIsOffset) cursor += encodingBytes.Length;

            fdArrayStart = 0;
            fdSelectStart = 0;
            if (font.IsCidKeyed)
            {
                if (font.FdArrayOffset > 0)
                {
                    fdArrayStart = cursor;
                    cursor += fdArrayBytes.Length;
                }
                if (font.FdSelectOffset > 0)
                {
                    fdSelectStart = cursor;
                    cursor += fdSelectBytes.Length;
                }
            }

            privateDictStart = cursor;
            cursor += privateDictBytes.Length;

            topDictData = RebuildTopDictBytes(
                font,
                charStringsNewOffset: csStart,
                charsetNewOffset: charsetIsOffset ? charsetStart : font.CharsetOffset,
                encodingNewOffset: encodingIsOffset ? encodingStart : font.EncodingOffset,
                privateDictNewOffset: privateDictBytes.Length > 0 ? privateDictStart : 0,
                fdArrayNewOffset: fdArrayBytes.Length > 0 ? fdArrayStart : 0,
                fdSelectNewOffset: fdSelectBytes.Length > 0 ? fdSelectStart : 0,
                charsetIsOffset: charsetIsOffset,
                encodingIsOffset: encodingIsOffset);

            (topDictIndexBytes, _) = BuildIndex([topDictData]);
        }

        _ = gsubrStart; // consumed in array assembly

        // ── 6. Assemble output ───────────────────────────────────────────────
        var output = new MemoryStream(cursor + 64);
        output.Write(headerBytes);
        output.Write(newNameIndexBytes);
        output.Write(topDictIndexBytes);
        output.Write(stringIndexBytes);
        output.Write(globalSubrBytes);
        output.Write(newCsIndexBytes);
        if (charsetIsOffset && charsetBytes.Length > 0) output.Write(charsetBytes);
        if (encodingIsOffset && encodingBytes.Length > 0) output.Write(encodingBytes);
        if (font.IsCidKeyed)
        {
            if (fdArrayBytes.Length > 0) output.Write(fdArrayBytes);
            if (fdSelectBytes.Length > 0) output.Write(fdSelectBytes);
        }
        if (privateDictBytes.Length > 0) output.Write(privateDictBytes);

        return output.ToArray();
    }

    // ── Top DICT rebuilder ────────────────────────────────────────────────────

    /// <summary>
    /// Strips offset-bearing operators (15, 16, 17, 18, 12 36, 12 37) from the
    /// original Top DICT bytes, then appends them re-encoded with updated offsets.
    /// </summary>
    private static byte[] RebuildTopDictBytes(
        CffFont font,
        int charStringsNewOffset,
        int charsetNewOffset,
        int encodingNewOffset,
        int privateDictNewOffset,
        int fdArrayNewOffset,
        int fdSelectNewOffset,
        bool charsetIsOffset,
        bool encodingIsOffset)
    {
        var src = font.TopDictBytes.Span;
        var stripped = StripTopDictOperators(src, charsetIsOffset, encodingIsOffset,
            hasFdArray: font.FdArrayOffset > 0,
            hasFdSelect: font.FdSelectOffset > 0,
            hasPrivate: font.PrivateDictSize > 0);

        var ms = new MemoryStream(stripped.Length + 128);
        ms.Write(stripped);

        // Append patched operators
        // CharStrings (op 17)
        EncodeInt(ms, charStringsNewOffset);
        ms.WriteByte(17);

        // charset (op 15) — only if offset-based
        if (charsetIsOffset && charsetNewOffset > 0)
        {
            EncodeInt(ms, charsetNewOffset);
            ms.WriteByte(15);
        }

        // Encoding (op 16) — only if offset-based
        if (encodingIsOffset && encodingNewOffset > 0)
        {
            EncodeInt(ms, encodingNewOffset);
            ms.WriteByte(16);
        }

        // Private (op 18) — size + offset
        if (privateDictNewOffset > 0 && font.PrivateDictSize > 0)
        {
            EncodeInt(ms, font.PrivateDictSize);
            EncodeInt(ms, privateDictNewOffset);
            ms.WriteByte(18);
        }

        // FDArray (op 12 36)
        if (fdArrayNewOffset > 0)
        {
            EncodeInt(ms, fdArrayNewOffset);
            ms.WriteByte(12);
            ms.WriteByte(36);
        }

        // FDSelect (op 12 37)
        if (fdSelectNewOffset > 0)
        {
            EncodeInt(ms, fdSelectNewOffset);
            ms.WriteByte(12);
            ms.WriteByte(37);
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Copies Top DICT bytes, stripping the operands+operators for ops 15, 16, 17, 18,
    /// 12 36, and 12 37.  All other operators and their operands are preserved verbatim.
    /// </summary>
    private static byte[] StripTopDictOperators(
        ReadOnlySpan<byte> dict,
        bool charsetIsOffset,
        bool encodingIsOffset,
        bool hasFdArray,
        bool hasFdSelect,
        bool hasPrivate)
    {
        // We scan the dict, collecting operands as byte ranges.
        // When we hit a target operator, we skip all pending operand bytes and the operator.
        // Otherwise we flush the operands and the operator byte(s).

        var opsToStrip = new HashSet<int>
        {
            17, // CharStrings
            18, // Private
        };
        if (charsetIsOffset) opsToStrip.Add(15);
        if (encodingIsOffset) opsToStrip.Add(16);
        if (hasFdArray) opsToStrip.Add(0x1000 + 36); // 12 36
        if (hasFdSelect) opsToStrip.Add(0x1000 + 37); // 12 37

        var output = new MemoryStream(dict.Length);
        var operandStart = 0;
        var i = 0;

        while (i < dict.Length)
        {
            var b0 = dict[i];

            if (b0 <= 21)
            {
                // Operator
                int opKey;
                int opLen;
                if (b0 == 12)
                {
                    if (i + 1 >= dict.Length)
                    {
                        i++;
                        break;
                    }
                    opKey = 0x1000 + dict[i + 1];
                    opLen = 2;
                }
                else
                {
                    opKey = b0;
                    opLen = 1;
                }

                if (opsToStrip.Contains(opKey))
                {
                    // Skip: don't flush operands, don't emit operator
                    i += opLen;
                    operandStart = i; // reset operand accumulation
                }
                else
                {
                    // Flush operands + operator verbatim
                    output.Write(dict.Slice(operandStart, i - operandStart));
                    output.Write(dict.Slice(i, opLen));
                    i += opLen;
                    operandStart = i;
                }
                continue;
            }

            // Operand bytes — advance i without flushing yet
            if (b0 == 28) { i += 3; }
            else if (b0 == 29) { i += 5; }
            else if (b0 == 30)
            {
                // Real: skip nibble-pairs until end nibble 0xF
                i++;
                while (i < dict.Length)
                {
                    var nb = dict[i];
                    i++;
                    if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F) break;
                }
            }
            else if (b0 is >= 32 and <= 246) { i++; }
            else if (b0 is >= 247 and <= 250) { i += 2; }
            else if (b0 is >= 251 and <= 254) { i += 2; }
            else { i++; } // unknown: skip
        }

        return output.ToArray();
    }

    // ── CFF INDEX builder ─────────────────────────────────────────────────────

    /// <summary>
    /// Builds a CFF INDEX from the given data entries.
    /// Computes the minimum offSize from the total data length.
    /// Returns (indexBytes, totalLength).
    /// </summary>
    private static (byte[] bytes, int length) BuildIndex(byte[][] entries)
    {
        if (entries.Length == 0)
        {
            // count=0 INDEX: just 2 bytes
            return ([0x00, 0x00], 2);
        }

        // Compute total data size
        var dataSize = entries.Sum(e => e.Length);
        var offSize = ComputeOffSize(dataSize + 1); // +1 because offsets are 1-based

        var headerSize = 2 + 1 + (entries.Length + 1) * offSize; // count(2) + offSize(1) + offsets
        var totalSize = headerSize + dataSize;

        var buf = new byte[totalSize];
        var pos = 0;

        // count
        buf[pos++] = (byte)(entries.Length >> 8);
        buf[pos++] = (byte)entries.Length;

        // offSize
        buf[pos++] = (byte)offSize;

        // offsets (1-based)
        var currentOffset = 1;
        WriteOffset(buf, ref pos, offSize, currentOffset);
        for (var i = 0; i < entries.Length; i++)
        {
            currentOffset += entries[i].Length;
            WriteOffset(buf, ref pos, offSize, currentOffset);
        }

        // data
        for (var i = 0; i < entries.Length; i++)
        {
            entries[i].CopyTo(buf, pos);
            pos += entries[i].Length;
        }

        return (buf, totalSize);
    }

    private static byte ComputeOffSize(int maxOffset)
    {
        if (maxOffset <= 0xFF) return 1;
        if (maxOffset <= 0xFFFF) return 2;
        if (maxOffset <= 0xFFFFFF) return 3;
        return 4;
    }

    private static void WriteOffset(byte[] buf, ref int pos, int offSize, int value)
    {
        for (var shift = (offSize - 1) * 8; shift >= 0; shift -= 8)
            buf[pos++] = (byte)(value >> shift);
    }

    // ── DICT integer encoder ──────────────────────────────────────────────────

    /// <summary>Encodes an integer operand using the CFF DICT encoding. Prefers compact forms.</summary>
    private static void EncodeInt(Stream s, int value)
    {
        if (value is >= -107 and <= 107)
        {
            s.WriteByte((byte)(value + 139));
        }
        else if (value is >= 108 and <= 1131)
        {
            var v = value - 108;
            s.WriteByte((byte)((v >> 8) + 247));
            s.WriteByte((byte)(v & 0xFF));
        }
        else if (value is >= -1131 and <= -108)
        {
            var v = -value - 108;
            s.WriteByte((byte)((v >> 8) + 251));
            s.WriteByte((byte)(v & 0xFF));
        }
        else if (value is >= -32768 and <= 32767)
        {
            s.WriteByte(28);
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)(value & 0xFF));
        }
        else
        {
            // 4-byte form (b0=29)
            s.WriteByte(29);
            s.WriteByte((byte)(value >> 24));
            s.WriteByte((byte)(value >> 16));
            s.WriteByte((byte)(value >> 8));
            s.WriteByte((byte)(value & 0xFF));
        }
    }

    // ── Subset tag ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes a deterministic 6-uppercase-letter subset tag from the sorted GID set.
    /// Mirrors the TrueType SubsetTag() in TrueTypeFontEmbedder.cs.
    /// </summary>
    private static string ComputeSubsetTag(IEnumerable<int> gids)
    {
        var sorted = gids.OrderBy(g => g).ToArray();
        var input = new byte[sorted.Length * 2];
        for (var i = 0; i < sorted.Length; i++)
        {
            input[i * 2] = (byte)(sorted[i] >> 8);
            input[i * 2 + 1] = (byte)sorted[i];
        }
        var hash = System.Security.Cryptography.SHA256.HashData(input);
        var tag = new char[6];
        for (var i = 0; i < 6; i++)
            tag[i] = (char)('A' + hash[i] % 26);
        return new string(tag);
    }

    // ── Section length helpers ────────────────────────────────────────────────

    /// <summary>
    /// Heuristic: find the offset of the first "other" section that comes after CharStrings.
    /// Used to determine the byte range of sections that lack an explicit length.
    /// </summary>
    private static int NextSectionOffset(CffFont font)
    {
        // The sections after CharStrings in canonical layout are:
        // charset, Encoding, FDArray, FDSelect, Private.
        // We just need the one immediately after charset.
        var offsets = new List<int>();
        if (font.EncodingOffset > 2) offsets.Add(font.EncodingOffset);
        if (font.FdArrayOffset > 0) offsets.Add(font.FdArrayOffset);
        if (font.FdSelectOffset > 0) offsets.Add(font.FdSelectOffset);
        if (font.PrivateDictOffset > 0) offsets.Add(font.PrivateDictOffset);
        offsets.Add(font.Data.Length);

        var after = offsets.Where(o => o > font.CharsetOffset).DefaultIfEmpty(font.Data.Length).Min();
        return after;
    }

    private static int NextSectionOffsetAfterEncoding(CffFont font)
    {
        var offsets = new List<int>();
        if (font.FdArrayOffset > 0) offsets.Add(font.FdArrayOffset);
        if (font.FdSelectOffset > 0) offsets.Add(font.FdSelectOffset);
        if (font.PrivateDictOffset > 0) offsets.Add(font.PrivateDictOffset);
        offsets.Add(font.Data.Length);

        var after = offsets.Where(o => o > font.EncodingOffset).DefaultIfEmpty(font.Data.Length).Min();
        return after;
    }

    private static int FdArrayLength(CffFont font, ReadOnlySpan<byte> src)
    {
        if (font.FdArrayOffset <= 0 || font.FdArrayOffset >= src.Length) return 0;
        var offsets = new List<int>();
        if (font.FdSelectOffset > 0) offsets.Add(font.FdSelectOffset);
        if (font.PrivateDictOffset > 0) offsets.Add(font.PrivateDictOffset);
        offsets.Add(src.Length);
        var next = offsets.Where(o => o > font.FdArrayOffset).DefaultIfEmpty(src.Length).Min();
        return next - font.FdArrayOffset;
    }

    private static int FdSelectLength(CffFont font, ReadOnlySpan<byte> src)
    {
        if (font.FdSelectOffset <= 0 || font.FdSelectOffset >= src.Length) return 0;
        var offsets = new List<int>();
        if (font.PrivateDictOffset > 0) offsets.Add(font.PrivateDictOffset);
        offsets.Add(src.Length);
        var next = offsets.Where(o => o > font.FdSelectOffset).DefaultIfEmpty(src.Length).Min();
        return next - font.FdSelectOffset;
    }
}
