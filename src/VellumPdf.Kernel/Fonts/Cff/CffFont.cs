// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Fonts.Cff;

/// <summary>
/// Parses a CFF (Compact Font Format) table as specified in Adobe Technical Note 5176.
/// Suitable for OpenType-CFF ('OTTO') fonts.
/// </summary>
internal sealed class CffFont
{
    // ── CFF structure positions ──────────────────────────────────────────────

    /// <summary>Raw CFF table bytes (the 'CFF ' SFNT table).</summary>
    public ReadOnlyMemory<byte> Data { get; }

    /// <summary>Number of glyphs in the font (length of CharStrings INDEX).</summary>
    public int NumGlyphs { get; }

    /// <summary>True if the font uses CID-keyed structure (has ROS operator in Top DICT).</summary>
    public bool IsCidKeyed { get; }

    /// <summary>The PostScript FontName as stored in the Name INDEX.</summary>
    public string FontName { get; }

    // ── Top DICT operator values ─────────────────────────────────────────────

    /// <summary>Offset from start of CFF data to CharStrings INDEX (op 17).</summary>
    public int CharStringsOffset { get; }

    /// <summary>Offset from start of CFF data to charset (op 15); or predefined value (0=ISOAdobe, 1=Expert, 2=ExpertSubset).</summary>
    public int CharsetOffset { get; }

    /// <summary>Offset from start of CFF data to Encoding (op 16); or predefined (0=Standard, 1=Expert).</summary>
    public int EncodingOffset { get; }

    /// <summary>Private DICT size (op 18, first operand).</summary>
    public int PrivateDictSize { get; }

    /// <summary>Private DICT offset from start of CFF (op 18, second operand).</summary>
    public int PrivateDictOffset { get; }

    /// <summary>FDArray offset (op 12 36); 0 if not present.</summary>
    public int FdArrayOffset { get; }

    /// <summary>FDSelect offset (op 12 37); 0 if not present.</summary>
    public int FdSelectOffset { get; }

    // ── INDEX positions ──────────────────────────────────────────────────────

    /// <summary>Byte offset where the Name INDEX starts within CFF data.</summary>
    public int NameIndexOffset { get; }

    /// <summary>Byte offset where the Top DICT INDEX starts within CFF data.</summary>
    public int TopDictIndexOffset { get; }

    /// <summary>Byte offset where the String INDEX starts within CFF data.</summary>
    public int StringIndexOffset { get; }

    /// <summary>Byte offset where the Global Subr INDEX starts within CFF data.</summary>
    public int GlobalSubrIndexOffset { get; }

    /// <summary>Byte length of the entire Name INDEX.</summary>
    public int NameIndexLength { get; }

    /// <summary>Byte length of the entire Top DICT INDEX.</summary>
    public int TopDictIndexLength { get; }

    /// <summary>Byte length of the entire String INDEX.</summary>
    public int StringIndexLength { get; }

    /// <summary>Byte length of the entire Global Subr INDEX.</summary>
    public int GlobalSubrIndexLength { get; }

    /// <summary>Byte offset of CharStrings INDEX within CFF data (== CharStringsOffset).</summary>
    public int CharStringsIndexOffset { get; }

    /// <summary>Byte length of the entire CharStrings INDEX.</summary>
    public int CharStringsIndexLength { get; }

    // ── Raw Top DICT bytes (for re-emitting with patched offsets) ────────────

    /// <summary>Raw bytes of the Top DICT data (inside the Top DICT INDEX entry).</summary>
    public ReadOnlyMemory<byte> TopDictBytes { get; }

    // ── Subroutine INDEX positions ────────────────────────────────────────────

    /// <summary>Number of entries in the Global Subr INDEX (0 if INDEX is empty).</summary>
    public int GlobalSubrCount { get; }

    /// <summary>
    /// Type2 bias for the Global Subr INDEX.
    /// 107 if count &lt; 1240, 1131 if count &lt; 33900, 32768 otherwise.
    /// </summary>
    public int GlobalSubrBias { get; }

    /// <summary>
    /// Byte offset within CFF data where the Local Subr INDEX begins.
    /// 0 if the Private DICT does not contain a Subrs operator (op 19).
    /// </summary>
    public int LocalSubrIndexOffset { get; }

    /// <summary>Byte length of the Local Subr INDEX. 0 when not present.</summary>
    public int LocalSubrIndexLength { get; }

    /// <summary>Number of entries in the Local Subr INDEX. 0 when not present.</summary>
    public int LocalSubrCount { get; }

    /// <summary>
    /// Type2 bias for the Local Subr INDEX.
    /// 107 if count &lt; 1240, 1131 if count &lt; 33900, 32768 otherwise.
    /// </summary>
    public int LocalSubrBias { get; }

    private CffFont(
        ReadOnlyMemory<byte> data,
        int numGlyphs,
        bool isCidKeyed,
        string fontName,
        int charStringsOffset,
        int charsetOffset,
        int encodingOffset,
        int privateDictSize,
        int privateDictOffset,
        int fdArrayOffset,
        int fdSelectOffset,
        int nameIndexOffset,
        int nameIndexLength,
        int topDictIndexOffset,
        int topDictIndexLength,
        int stringIndexOffset,
        int stringIndexLength,
        int globalSubrIndexOffset,
        int globalSubrIndexLength,
        int globalSubrCount,
        int charStringsIndexOffset,
        int charStringsIndexLength,
        ReadOnlyMemory<byte> topDictBytes,
        int localSubrIndexOffset,
        int localSubrIndexLength,
        int localSubrCount)
    {
        Data = data;
        NumGlyphs = numGlyphs;
        IsCidKeyed = isCidKeyed;
        FontName = fontName;
        CharStringsOffset = charStringsOffset;
        CharsetOffset = charsetOffset;
        EncodingOffset = encodingOffset;
        PrivateDictSize = privateDictSize;
        PrivateDictOffset = privateDictOffset;
        FdArrayOffset = fdArrayOffset;
        FdSelectOffset = fdSelectOffset;
        NameIndexOffset = nameIndexOffset;
        NameIndexLength = nameIndexLength;
        TopDictIndexOffset = topDictIndexOffset;
        TopDictIndexLength = topDictIndexLength;
        StringIndexOffset = stringIndexOffset;
        StringIndexLength = stringIndexLength;
        GlobalSubrIndexOffset = globalSubrIndexOffset;
        GlobalSubrIndexLength = globalSubrIndexLength;
        GlobalSubrCount = globalSubrCount;
        GlobalSubrBias = ComputeBias(globalSubrCount);
        CharStringsIndexOffset = charStringsIndexOffset;
        CharStringsIndexLength = charStringsIndexLength;
        TopDictBytes = topDictBytes;
        LocalSubrIndexOffset = localSubrIndexOffset;
        LocalSubrIndexLength = localSubrIndexLength;
        LocalSubrCount = localSubrCount;
        LocalSubrBias = ComputeBias(localSubrCount);
    }

    /// <summary>
    /// Parses CFF table bytes. Throws <see cref="InvalidDataException"/> on malformed input.
    /// </summary>
    public static CffFont Parse(ReadOnlyMemory<byte> cffData)
    {
        var s = cffData.Span;
        var len = s.Length;

        // ── CFF Header (§6) ──────────────────────────────────────────────────
        if (len < 4)
            throw new InvalidDataException("CFF data too short for header.");
        var major = s[0];
        var minor = s[1];
        _ = minor; // not used but checked for future compatibility
        if (major != 1)
            throw new InvalidDataException($"Unsupported CFF major version {major}; only version 1 is supported.");
        var hdrSize = s[2];
        // offSize at s[3] applies to absolute offsets; not used for header skip.

        int pos = hdrSize; // skip past CFF header

        // ── Name INDEX (§7) ──────────────────────────────────────────────────
        var nameIndexOffset = pos;
        var (nameCount, nameOffsets, nameDataBase) = ReadIndexOffsets(s, pos, len);
        int nameIndexLength;
        if (nameCount == 0)
        {
            nameIndexLength = 2; // just the count field
        }
        else
        {
            var lastNameEnd = (int)nameOffsets[nameCount];
            nameIndexLength = nameDataBase + lastNameEnd - 1 - pos;
        }
        pos += nameIndexLength;

        string fontName = string.Empty;
        if (nameCount > 0)
        {
            var nameStart = (int)nameOffsets[0];
            var nameEnd = (int)nameOffsets[1];
            var nameLen = nameEnd - nameStart;
            if (nameLen < 0 || nameDataBase + nameEnd - 1 > len)
                throw new InvalidDataException("CFF Name INDEX: name data out of range.");
            fontName = Encoding.Latin1.GetString(s.Slice(nameDataBase + nameStart - 1, nameLen));
        }

        // ── Top DICT INDEX (§8) ──────────────────────────────────────────────
        var topDictIndexOffset = pos;
        var (topDictCount, topDictOffsets, topDictDataBase) = ReadIndexOffsets(s, pos, len);
        int topDictIndexLength;
        if (topDictCount == 0)
        {
            topDictIndexLength = 2;
        }
        else
        {
            var lastEnd = (int)topDictOffsets[topDictCount];
            topDictIndexLength = topDictDataBase + lastEnd - 1 - pos;
        }
        pos += topDictIndexLength;

        ReadOnlyMemory<byte> topDictBytes = ReadOnlyMemory<byte>.Empty;
        if (topDictCount > 0)
        {
            var tdStart = (int)topDictOffsets[0];
            var tdEnd = (int)topDictOffsets[1];
            var tdLen = tdEnd - tdStart;
            if (tdLen < 0)
                throw new InvalidDataException("CFF Top DICT INDEX: negative entry length.");
            var tdAbsStart = topDictDataBase + tdStart - 1;
            if (tdAbsStart < 0 || tdAbsStart + tdLen > len)
                throw new InvalidDataException("CFF Top DICT INDEX: data out of range.");
            topDictBytes = cffData.Slice(tdAbsStart, tdLen);
        }

        // ── String INDEX (§10) ────────────────────────────────────────────────
        var stringIndexOffset = pos;
        int stringIndexLength = IndexTotalLength(s, pos, len);
        pos += stringIndexLength;

        // ── Global Subr INDEX (§16) ───────────────────────────────────────────
        var globalSubrIndexOffset = pos;
        int globalSubrIndexLength = IndexTotalLength(s, pos, len);
        // Read count for bias computation
        var globalSubrCount = 0;
        if (pos + 2 <= len)
            globalSubrCount = (s[pos] << 8) | s[pos + 1];
        pos += globalSubrIndexLength;

        // ── Parse Top DICT for key offsets ────────────────────────────────────
        var (charStringsOffset, charsetOffset, encodingOffset,
             privateDictSize, privateDictOffset,
             fdArrayOffset, fdSelectOffset, isCidKeyed) = ParseTopDict(topDictBytes.Span);

        // ── CharStrings INDEX ─────────────────────────────────────────────────
        if (charStringsOffset <= 0)
            throw new InvalidDataException("CFF Top DICT missing CharStrings offset (op 17).");
        if (charStringsOffset > len)
            throw new InvalidDataException($"CFF CharStrings offset {charStringsOffset} exceeds data length {len}.");

        var charStringsIndexOffset = charStringsOffset;
        var (csCount, _, _) = ReadIndexOffsets(s, charStringsOffset, len);
        var charStringsIndexLength = IndexTotalLength(s, charStringsOffset, len);

        // ── Local Subr INDEX (from Private DICT, op 19) ───────────────────────
        // The Private DICT Subrs operator (op 19) holds a byte offset relative
        // to the start of the Private DICT. The Local Subr INDEX is at:
        //   privateDictOffset + subrRelativeOffset
        var localSubrIndexOffset = 0;
        var localSubrIndexLength = 0;
        var localSubrCount = 0;

        if (privateDictSize > 0 && privateDictOffset > 0
            && privateDictOffset + privateDictSize <= len)
        {
            var privSpan = s.Slice(privateDictOffset, privateDictSize);
            var subrRelOffset = ParsePrivateDictSubrsOffset(privSpan);
            if (subrRelOffset > 0)
            {
                var absLocalSubrOffset = privateDictOffset + subrRelOffset;
                if (absLocalSubrOffset + 2 <= len)
                {
                    localSubrIndexOffset = absLocalSubrOffset;
                    localSubrIndexLength = IndexTotalLength(s, absLocalSubrOffset, len);
                    localSubrCount = (s[absLocalSubrOffset] << 8) | s[absLocalSubrOffset + 1];
                }
            }
        }

        return new CffFont(
            cffData,
            numGlyphs: csCount,
            isCidKeyed: isCidKeyed,
            fontName: fontName,
            charStringsOffset: charStringsOffset,
            charsetOffset: charsetOffset,
            encodingOffset: encodingOffset,
            privateDictSize: privateDictSize,
            privateDictOffset: privateDictOffset,
            fdArrayOffset: fdArrayOffset,
            fdSelectOffset: fdSelectOffset,
            nameIndexOffset: nameIndexOffset,
            nameIndexLength: nameIndexLength,
            topDictIndexOffset: topDictIndexOffset,
            topDictIndexLength: topDictIndexLength,
            stringIndexOffset: stringIndexOffset,
            stringIndexLength: stringIndexLength,
            globalSubrIndexOffset: globalSubrIndexOffset,
            globalSubrIndexLength: globalSubrIndexLength,
            globalSubrCount: globalSubrCount,
            charStringsIndexOffset: charStringsIndexOffset,
            charStringsIndexLength: charStringsIndexLength,
            topDictBytes: topDictBytes,
            localSubrIndexOffset: localSubrIndexOffset,
            localSubrIndexLength: localSubrIndexLength,
            localSubrCount: localSubrCount);
    }

    /// <summary>Returns the raw charstring bytes for <paramref name="gid"/>.</summary>
    public ReadOnlyMemory<byte> GetCharstring(int gid)
    {
        if (gid < 0 || gid >= NumGlyphs)
            throw new ArgumentOutOfRangeException(nameof(gid), $"GID {gid} is out of range [0, {NumGlyphs}).");
        var s = Data.Span;
        var (_, offsets, dataBase) = ReadIndexOffsets(s, CharStringsIndexOffset, Data.Length);
        var start = (int)offsets[gid];
        var end = (int)offsets[gid + 1];
        var absStart = dataBase + start - 1;
        var csLen = end - start;
        if (csLen < 0 || absStart < 0 || absStart + csLen > Data.Length)
            throw new InvalidDataException($"CFF CharStrings INDEX: GID {gid} data out of range.");
        return Data.Slice(absStart, csLen);
    }

    /// <summary>Returns the raw bytes for Global Subr entry <paramref name="index"/>.</summary>
    public ReadOnlyMemory<byte> GetGlobalSubr(int index)
    {
        if (GlobalSubrCount == 0)
            throw new InvalidOperationException("Font has no Global Subr INDEX.");
        return GetSubrEntry(Data.Span, GlobalSubrIndexOffset, Data.Length, index);
    }

    /// <summary>Returns the raw bytes for Local Subr entry <paramref name="index"/>.</summary>
    public ReadOnlyMemory<byte> GetLocalSubr(int index)
    {
        if (LocalSubrCount == 0)
            throw new InvalidOperationException("Font has no Local Subr INDEX.");
        return GetSubrEntry(Data.Span, LocalSubrIndexOffset, Data.Length, index);
    }

    // ── INDEX helpers ─────────────────────────────────────────────────────────

    private ReadOnlyMemory<byte> GetSubrEntry(ReadOnlySpan<byte> s, int indexOffset, int totalLen, int entryIndex)
    {
        var (count, offsets, dataBase) = ReadIndexOffsets(s, indexOffset, totalLen);
        if (entryIndex < 0 || entryIndex >= count)
            throw new ArgumentOutOfRangeException(nameof(entryIndex),
                $"Subr index {entryIndex} out of range [0, {count}).");
        var start = (int)offsets[entryIndex];
        var end = (int)offsets[entryIndex + 1];
        var absStart = dataBase + start - 1;
        var entryLen = end - start;
        if (entryLen < 0 || absStart < 0 || absStart + entryLen > totalLen)
            throw new InvalidDataException($"Subr INDEX entry {entryIndex} data out of range.");
        return Data.Slice(absStart, entryLen);
    }

    /// <summary>
    /// Reads the count + offsets array from an INDEX at <paramref name="pos"/>.
    /// Returns (count, offsets[0..count], absoluteDataBase).
    /// Offsets are 1-based per CFF spec; dataBase is the abs offset of offset[0]==1.
    /// </summary>
    internal static (int count, uint[] offsets, int dataBase) ReadIndexOffsets(
        ReadOnlySpan<byte> s, int pos, int totalLen)
    {
        if (pos + 2 > totalLen)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: insufficient data for count.");
        var count = (s[pos] << 8) | s[pos + 1];
        if (count == 0)
            return (0, [], pos + 2);

        if (pos + 3 > totalLen)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: insufficient data for offSize.");
        var offSize = s[pos + 2];
        if (offSize < 1 || offSize > 4)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: invalid offSize {offSize}.");

        // offsets array: (count+1) entries of offSize bytes each
        var offsetsArrayStart = pos + 3;
        var offsetsArrayLen = (count + 1) * offSize;
        if (offsetsArrayStart + offsetsArrayLen > totalLen)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: offset array exceeds data.");

        var offsets = new uint[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var o = offsetsArrayStart + i * offSize;
            uint v = 0;
            for (var b = 0; b < offSize; b++)
                v = (v << 8) | s[o + b];
            offsets[i] = v;
        }

        // dataBase is the position where offset[0]==1 maps to (i.e. start of data section)
        var dataBase = offsetsArrayStart + offsetsArrayLen;
        return (count, offsets, dataBase);
    }

    /// <summary>
    /// Returns the total byte length of the INDEX at <paramref name="pos"/> (including the header).
    /// </summary>
    internal static int IndexTotalLength(ReadOnlySpan<byte> s, int pos, int totalLen)
    {
        if (pos + 2 > totalLen)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: insufficient data for count.");
        var count = (s[pos] << 8) | s[pos + 1];
        if (count == 0)
            return 2; // just the 2-byte count=0

        if (pos + 3 > totalLen)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: insufficient data for offSize.");
        var offSize = s[pos + 2];
        if (offSize < 1 || offSize > 4)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: invalid offSize {offSize}.");

        var offsetsArrayLen = (count + 1) * offSize;
        var dataBase = pos + 3 + offsetsArrayLen;

        // Read the last offset to find data size
        var lastOffsetPos = pos + 3 + count * offSize;
        if (lastOffsetPos + offSize > totalLen)
            throw new InvalidDataException($"CFF INDEX at offset {pos}: last offset out of range.");
        uint lastOffset = 0;
        for (var b = 0; b < offSize; b++)
            lastOffset = (lastOffset << 8) | s[lastOffsetPos + b];

        // Data section size = lastOffset - 1 (offsets are 1-based)
        var dataSize = (int)lastOffset - 1;
        return 3 + offsetsArrayLen + dataSize;
    }

    // ── Private DICT decoder ──────────────────────────────────────────────────

    /// <summary>
    /// Scans the Private DICT body for op 19 (Subrs) and returns the relative byte
    /// offset of the Local Subr INDEX (relative to the start of the Private DICT).
    /// Returns 0 if the operator is absent.
    /// </summary>
    internal static int ParsePrivateDictSubrsOffset(ReadOnlySpan<byte> dict)
    {
        var operands = new List<double>(4);
        var i = 0;

        while (i < dict.Length)
        {
            var b0 = dict[i];

            // Operators: 0-21 (excluding 28)
            if (b0 <= 21)
            {
                if (b0 == 12)
                {
                    // Two-byte operator — skip
                    i += 2;
                }
                else
                {
                    if (b0 == 19 && operands.Count > 0) // Subrs
                        return (int)operands[0];
                    i++;
                }
                operands.Clear();
                continue;
            }

            // Operand encodings (same as Top DICT)
            if (b0 == 28)
            {
                if (i + 2 >= dict.Length) break;
                operands.Add((short)((dict[i + 1] << 8) | dict[i + 2]));
                i += 3;
            }
            else if (b0 == 29)
            {
                if (i + 4 >= dict.Length) break;
                var val = (dict[i + 1] << 24) | (dict[i + 2] << 16) | (dict[i + 3] << 8) | dict[i + 4];
                operands.Add(val);
                i += 5;
            }
            else if (b0 == 30)
            {
                // Real — skip nibble-pairs until end nibble 0xF
                i++;
                while (i < dict.Length)
                {
                    var nb = dict[i++];
                    if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F) break;
                }
                operands.Add(0);
            }
            else if (b0 is >= 32 and <= 246)
            {
                operands.Add(b0 - 139);
                i++;
            }
            else if (b0 is >= 247 and <= 250)
            {
                if (i + 1 >= dict.Length) break;
                operands.Add((b0 - 247) * 256 + dict[i + 1] + 108);
                i += 2;
            }
            else if (b0 is >= 251 and <= 254)
            {
                if (i + 1 >= dict.Length) break;
                operands.Add(-(b0 - 251) * 256 - dict[i + 1] - 108);
                i += 2;
            }
            else
            {
                i++;
            }
        }

        return 0; // no Subrs operator found
    }

    // ── Top DICT decoder ──────────────────────────────────────────────────────

    private static (
        int charStringsOffset,
        int charsetOffset,
        int encodingOffset,
        int privateDictSize,
        int privateDictOffset,
        int fdArrayOffset,
        int fdSelectOffset,
        bool isCidKeyed)
    ParseTopDict(ReadOnlySpan<byte> dict)
    {
        var charStringsOffset = 0;
        var charsetOffset = 0;   // 0 = ISOAdobe predefined (not an offset)
        var encodingOffset = 0;  // 0 = Standard predefined (not an offset)
        var privateDictSize = 0;
        var privateDictOffset = 0;
        var fdArrayOffset = 0;
        var fdSelectOffset = 0;
        var isCidKeyed = false;

        var operands = new List<double>(8);
        var i = 0;

        while (i < dict.Length)
        {
            var b0 = dict[i];

            // ── Operators ────────────────────────────────────────────────────
            if (b0 <= 21)
            {
                if (b0 == 12)
                {
                    // Two-byte operator
                    i++;
                    if (i >= dict.Length)
                        throw new InvalidDataException("CFF Top DICT: truncated two-byte operator.");
                    var b1 = dict[i];
                    switch (b1)
                    {
                        case 30: // ROS — marks CID-keyed
                            isCidKeyed = true;
                            break;
                        case 36: // FDArray
                            fdArrayOffset = operands.Count > 0 ? (int)operands[0] : 0;
                            break;
                        case 37: // FDSelect
                            fdSelectOffset = operands.Count > 0 ? (int)operands[0] : 0;
                            break;
                    }
                }
                else
                {
                    switch (b0)
                    {
                        case 15: // charset
                            charsetOffset = operands.Count > 0 ? (int)operands[0] : 0;
                            break;
                        case 16: // Encoding
                            encodingOffset = operands.Count > 0 ? (int)operands[0] : 0;
                            break;
                        case 17: // CharStrings
                            charStringsOffset = operands.Count > 0 ? (int)operands[0] : 0;
                            break;
                        case 18: // Private — two operands: size offset
                            if (operands.Count >= 2)
                            {
                                privateDictSize = (int)operands[0];
                                privateDictOffset = (int)operands[1];
                            }
                            break;
                    }
                }
                operands.Clear();
                i++;
                continue;
            }

            // ── Operands ─────────────────────────────────────────────────────
            if (b0 == 28)
            {
                // 2-byte signed integer
                if (i + 2 >= dict.Length)
                    throw new InvalidDataException("CFF Top DICT: truncated 2-byte integer (b0=28).");
                var val = (short)((dict[i + 1] << 8) | dict[i + 2]);
                operands.Add(val);
                i += 3;
            }
            else if (b0 == 29)
            {
                // 4-byte signed integer
                if (i + 4 >= dict.Length)
                    throw new InvalidDataException("CFF Top DICT: truncated 4-byte integer (b0=29).");
                var val = (dict[i + 1] << 24) | (dict[i + 2] << 16) | (dict[i + 3] << 8) | dict[i + 4];
                operands.Add(val);
                i += 5;
            }
            else if (b0 == 30)
            {
                // Real number — skip nibble-encoded bytes until 0xF nibble
                i++;
                var done = false;
                while (i < dict.Length && !done)
                {
                    var nb = dict[i];
                    if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F)
                        done = true;
                    i++;
                }
                // We just skip real operands — offsets are always integers
                operands.Add(0);
            }
            else if (b0 is >= 32 and <= 246)
            {
                operands.Add(b0 - 139);
                i++;
            }
            else if (b0 is >= 247 and <= 250)
            {
                if (i + 1 >= dict.Length)
                    throw new InvalidDataException("CFF Top DICT: truncated 2-byte integer (b0=247-250).");
                operands.Add((b0 - 247) * 256 + dict[i + 1] + 108);
                i += 2;
            }
            else if (b0 is >= 251 and <= 254)
            {
                if (i + 1 >= dict.Length)
                    throw new InvalidDataException("CFF Top DICT: truncated 2-byte integer (b0=251-254).");
                operands.Add(-(b0 - 251) * 256 - dict[i + 1] - 108);
                i += 2;
            }
            else
            {
                // Unknown byte — skip
                i++;
            }
        }

        return (charStringsOffset, charsetOffset, encodingOffset,
                privateDictSize, privateDictOffset,
                fdArrayOffset, fdSelectOffset, isCidKeyed);
    }

    // ── Bias helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Computes the Type2 subr number bias from an INDEX count.
    /// Per Adobe Technical Note 5177 §4.7.
    /// </summary>
    internal static int ComputeBias(int count)
    {
        if (count < 1240) return 107;
        if (count < 33900) return 1131;
        return 32768;
    }
}
