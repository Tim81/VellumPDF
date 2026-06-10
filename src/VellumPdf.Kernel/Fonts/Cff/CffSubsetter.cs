// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Fonts.Cff;

/// <summary>
/// Produces a subset of a CFF font table using the "blank-unused" strategy:
/// every unused glyph's CharString is replaced with a single <c>endchar</c> (0x0E) byte,
/// unreached global and local subroutines are replaced with a single <c>return</c> (0x0B) byte,
/// while the GID/subr-number space is preserved intact.
/// This preserves CID==GID identity required by Type0/CIDFontType0 with Identity-H.
/// </summary>
internal static class CffSubsetter
{
    private static readonly byte[] EndcharByte = [0x0E];
    private static readonly byte[] ReturnByte = [0x0B];

    /// <summary>
    /// Subsets <paramref name="font"/>, replacing unused glyph CharStrings with a single
    /// <c>endchar</c> byte, and unreached subroutines with a single <c>return</c> byte.
    /// GID 0 (.notdef) is always included.
    /// </summary>
    /// <param name="font">Parsed CFF font.</param>
    /// <param name="usedGids">Set of GIDs to keep verbatim; GID 0 is always added.</param>
    /// <returns>A new CFF table byte array with the subset applied.</returns>
    /// <exception cref="NotSupportedException">Thrown when <paramref name="font"/> is CID-keyed.</exception>
    public static byte[] Subset(CffFont font, IReadOnlySet<int> usedGids)
    {
        if (font.IsCidKeyed)
            throw new NotSupportedException(
                "CFF subroutine-closure subsetting is not supported for CID-keyed fonts.");

        // Ensure .notdef and sort for determinism
        var effectiveGids = new HashSet<int>(usedGids) { 0 };

        // ── 1. Compute subroutine closure ────────────────────────────────────
        var (usedLocalSubrs, usedGlobalSubrs) = ComputeSubrClosure(font, effectiveGids);

        // ── 2. Build the new CharStrings INDEX ───────────────────────────────
        var charstrings = new byte[font.NumGlyphs][];
        for (var gid = 0; gid < font.NumGlyphs; gid++)
        {
            charstrings[gid] = effectiveGids.Contains(gid)
                ? font.GetCharstring(gid).ToArray()
                : EndcharByte;
        }

        var (newCsIndexBytes, csIndexSize) = BuildIndex(charstrings);

        // ── 3. Build the new Global Subr INDEX ───────────────────────────────
        var newGlobalSubrs = BuildSubrEntries(font, isLocal: false, usedGlobalSubrs);
        var (newGlobalSubrBytes, _) = BuildIndex(newGlobalSubrs);

        // ── 4. Build the new Local Subr INDEX (and patched Private DICT) ─────
        // The Private DICT's Subrs operand (op 19) is a byte offset relative to
        // the start of the Private DICT. We rebuild the Private DICT, stripping
        // the old Subrs operator and re-emitting it pointing to the local subr
        // INDEX that sits immediately after the Private DICT. This keeps the
        // Subrs offset = privateDictSize (the rebuilt Private DICT byte length).
        byte[] newLocalSubrBytes;
        byte[] newPrivateDictBytes;

        if (font.LocalSubrCount > 0)
        {
            var newLocalSubrs = BuildSubrEntries(font, isLocal: true, usedLocalSubrs);
            (newLocalSubrBytes, _) = BuildIndex(newLocalSubrs);

            // Rebuild Private DICT: strip op 19, append op 19 pointing immediately after
            var privSrc = font.Data.Span.Slice(font.PrivateDictOffset, font.PrivateDictSize);
            var strippedPriv = StripPrivateDictSubrs(privSrc);

            // The local subr INDEX will be placed immediately after the Private DICT,
            // so the relative offset = strippedPriv.Length + appendedSubrsOp bytes.
            // We need to know the encoded size first. We compute iteratively with a
            // placeholder to stabilise the Private DICT size.
            newPrivateDictBytes = BuildPrivateDictWithSubrs(strippedPriv, newLocalSubrBytes.Length);
        }
        else
        {
            // No local subrs in source — emit empty local subr INDEX and verbatim Private DICT
            newLocalSubrBytes = [0x00, 0x00]; // empty INDEX (count=0)
            newPrivateDictBytes = font.PrivateDictSize > 0 && font.PrivateDictOffset > 0
                && font.PrivateDictOffset + font.PrivateDictSize <= font.Data.Length
                ? font.Data.Span.Slice(font.PrivateDictOffset, font.PrivateDictSize).ToArray()
                : [];
        }

        // ── 5. Build the new Name INDEX with subset tag ───────────────────────
        var tag = ComputeSubsetTag(effectiveGids);
        var newFontName = $"{tag}+{font.FontName}";
        var nameBytes = Encoding.Latin1.GetBytes(newFontName);
        var (newNameIndexBytes, _) = BuildIndex([nameBytes]);

        // ── 6. Lay out the canonical CFF structure ───────────────────────────
        //
        //  [Header]
        //  [Name INDEX]            ← patched FontName
        //  [Top DICT INDEX]        ← rebuilt with corrected offsets
        //  [String INDEX]          ← verbatim copy
        //  [Global Subr INDEX]     ← rebuilt (blank-unreached)
        //  [CharStrings INDEX]     ← rebuilt (blank-unused)
        //  [charset]               ← verbatim copy (if offset-based)
        //  [Encoding]              ← verbatim copy (if offset-based)
        //  [Private DICT]          ← rebuilt (Subrs op updated)
        //  [Local Subr INDEX]      ← rebuilt (blank-unreached), immediately after Private DICT
        //
        // We place each section sequentially, then rebuild the Top DICT.

        var src = font.Data.Span;

        var headerBytes = src[..font.NameIndexOffset].ToArray();
        var stringIndexBytes = src.Slice(font.StringIndexOffset, font.StringIndexLength).ToArray();

        // charset — only copy if it is an offset (> 2)
        byte[] charsetBytes = [];
        var charsetIsOffset = font.CharsetOffset > 2;
        if (charsetIsOffset && font.CharsetOffset + 1 <= src.Length)
        {
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

        // Combined Private DICT + Local Subr INDEX size
        var combinedPrivateSize = newPrivateDictBytes.Length + newLocalSubrBytes.Length;

        // ── 7. Compute layout offsets ────────────────────────────────────────

        // Two-pass to resolve Top DICT INDEX size (contains the new offsets)
        var rebuildResult = RebuildTopDictBytes(
            font,
            charStringsNewOffset: 0,
            charsetNewOffset: 0,
            encodingNewOffset: 0,
            privateDictNewOffset: 0,
            privateDictNewSize: newPrivateDictBytes.Length,
            charsetIsOffset: charsetIsOffset,
            encodingIsOffset: encodingIsOffset);

        var topDictData = rebuildResult;
        var (topDictIndexBytes, _) = BuildIndex([topDictData]);
        var topDictIndexSize = topDictIndexBytes.Length;

        // First-pass layout
        var cursor = headerBytes.Length + newNameIndexBytes.Length;
        var topDictStart = cursor;
        cursor += topDictIndexSize;

        var strStart = cursor;
        cursor += stringIndexBytes.Length;

        var gsubrStart = cursor;
        cursor += newGlobalSubrBytes.Length;

        var csStart = cursor;
        cursor += newCsIndexBytes.Length;

        var charsetStart = charsetIsOffset ? cursor : font.CharsetOffset;
        if (charsetIsOffset) cursor += charsetBytes.Length;

        var encodingStart = encodingIsOffset ? cursor : font.EncodingOffset;
        if (encodingIsOffset) cursor += encodingBytes.Length;

        var privateDictStart = cursor;
        cursor += combinedPrivateSize;

        // ── 8. Rebuild Top DICT with real offsets ────────────────────────────
        topDictData = RebuildTopDictBytes(
            font,
            charStringsNewOffset: csStart,
            charsetNewOffset: charsetIsOffset ? charsetStart : font.CharsetOffset,
            encodingNewOffset: encodingIsOffset ? encodingStart : font.EncodingOffset,
            privateDictNewOffset: newPrivateDictBytes.Length > 0 ? privateDictStart : 0,
            privateDictNewSize: newPrivateDictBytes.Length,
            charsetIsOffset: charsetIsOffset,
            encodingIsOffset: encodingIsOffset);

        (topDictIndexBytes, _) = BuildIndex([topDictData]);

        if (topDictIndexBytes.Length != topDictIndexSize)
        {
            // Re-layout with actual Top DICT INDEX size
            topDictIndexSize = topDictIndexBytes.Length;

            cursor = headerBytes.Length + newNameIndexBytes.Length;
            topDictStart = cursor;
            cursor += topDictIndexSize;

            strStart = cursor;
            cursor += stringIndexBytes.Length;

            gsubrStart = cursor;
            cursor += newGlobalSubrBytes.Length;

            csStart = cursor;
            cursor += newCsIndexBytes.Length;

            charsetStart = charsetIsOffset ? cursor : font.CharsetOffset;
            if (charsetIsOffset) cursor += charsetBytes.Length;

            encodingStart = encodingIsOffset ? cursor : font.EncodingOffset;
            if (encodingIsOffset) cursor += encodingBytes.Length;

            privateDictStart = cursor;
            cursor += combinedPrivateSize;

            topDictData = RebuildTopDictBytes(
                font,
                charStringsNewOffset: csStart,
                charsetNewOffset: charsetIsOffset ? charsetStart : font.CharsetOffset,
                encodingNewOffset: encodingIsOffset ? encodingStart : font.EncodingOffset,
                privateDictNewOffset: newPrivateDictBytes.Length > 0 ? privateDictStart : 0,
                privateDictNewSize: newPrivateDictBytes.Length,
                charsetIsOffset: charsetIsOffset,
                encodingIsOffset: encodingIsOffset);

            (topDictIndexBytes, _) = BuildIndex([topDictData]);
        }

        _ = topDictStart;
        _ = strStart;
        _ = gsubrStart;

        // ── 9. Assemble output ───────────────────────────────────────────────
        var output = new MemoryStream(cursor + 64);
        output.Write(headerBytes);
        output.Write(newNameIndexBytes);
        output.Write(topDictIndexBytes);
        output.Write(stringIndexBytes);
        output.Write(newGlobalSubrBytes);
        output.Write(newCsIndexBytes);
        if (charsetIsOffset && charsetBytes.Length > 0) output.Write(charsetBytes);
        if (encodingIsOffset && encodingBytes.Length > 0) output.Write(encodingBytes);
        if (newPrivateDictBytes.Length > 0)
        {
            output.Write(newPrivateDictBytes);
            output.Write(newLocalSubrBytes);
        }

        return output.ToArray();
    }

    // ── Subroutine closure ────────────────────────────────────────────────────

    /// <summary>
    /// Computes the transitive closure of all subroutines reachable from the kept
    /// glyph charstrings (including GID 0). Returns sets of used local and global subr
    /// indices (before bias adjustment — raw zero-based indices into the INDEX).
    /// </summary>
    private static (HashSet<int> usedLocalSubrs, HashSet<int> usedGlobalSubrs)
        ComputeSubrClosure(CffFont font, HashSet<int> effectiveGids)
    {
        var usedLocalSubrs = new HashSet<int>();
        var usedGlobalSubrs = new HashSet<int>();
        var visitedLocalSubrs = new HashSet<int>();
        var visitedGlobalSubrs = new HashSet<int>();

        // Walk each kept glyph charstring
        foreach (var gid in effectiveGids)
        {
            var cs = font.GetCharstring(gid);
            var numHints = 0;
            WalkCharstring(font, cs.Span, usedLocalSubrs, usedGlobalSubrs,
                visitedLocalSubrs, visitedGlobalSubrs, ref numHints);
        }

        return (usedLocalSubrs, usedGlobalSubrs);
    }

    /// <summary>
    /// Type2 charstring mini-interpreter. Walks the charstring bytes, tracking the
    /// operand stack and hint count so that <c>hintmask</c>/<c>cntrmask</c> mask bytes
    /// are consumed correctly. Records all <c>callsubr</c> (op 10) and
    /// <c>callgsubr</c> (op 29) targets and recurses into them.
    /// </summary>
    /// <param name="font">The CFF font providing subr INDEX access.</param>
    /// <param name="cs">Charstring bytes to walk (top-level or subroutine body).</param>
    /// <param name="usedLocalSubrs">Accumulates zero-based indices of all reachable local subrs.</param>
    /// <param name="usedGlobalSubrs">Accumulates zero-based indices of all reachable global subrs.</param>
    /// <param name="visitedLocalSubrs">Guards against re-entering the same local subr (cycle prevention).</param>
    /// <param name="visitedGlobalSubrs">Guards against re-entering the same global subr (cycle prevention).</param>
    /// <param name="numHints">
    /// Running total of hint pairs declared so far. Passed by <c>ref</c> so that hints
    /// declared inside a subroutine are visible to the caller — this is required because
    /// a <c>hintmask</c> or <c>cntrmask</c> operator inside a subroutine must consume
    /// <c>ceil(totalHints/8)</c> mask bytes, where <c>totalHints</c> includes hints from
    /// the calling charstring. Passing 0 on first entry is correct; callers must not
    /// reset this value between nested calls.
    /// </param>
    private static void WalkCharstring(
        CffFont font,
        ReadOnlySpan<byte> cs,
        HashSet<int> usedLocalSubrs,
        HashSet<int> usedGlobalSubrs,
        HashSet<int> visitedLocalSubrs,
        HashSet<int> visitedGlobalSubrs,
        ref int numHints)
    {
        // Operand stack (we only track depth + the top value for subr calls)
        Span<double> stack = stackalloc double[64];
        var stackDepth = 0;

        var i = 0;
        while (i < cs.Length)
        {
            var b0 = cs[i];

            // ── Operand encodings ─────────────────────────────────────────────
            if (b0 is >= 32 and <= 246)
            {
                // 1-byte integer: value = b0 - 139
                Push(stack, ref stackDepth, b0 - 139);
                i++;
                continue;
            }

            if (b0 is >= 247 and <= 250)
            {
                // 2-byte positive: value = (b0-247)*256 + b1 + 108
                if (i + 1 >= cs.Length) return;
                Push(stack, ref stackDepth, (b0 - 247) * 256 + cs[i + 1] + 108);
                i += 2;
                continue;
            }

            if (b0 is >= 251 and <= 254)
            {
                // 2-byte negative: value = -(b0-251)*256 - b1 - 108
                if (i + 1 >= cs.Length) return;
                Push(stack, ref stackDepth, -(b0 - 251) * 256 - cs[i + 1] - 108);
                i += 2;
                continue;
            }

            if (b0 == 28)
            {
                // 3-byte short int (signed 16-bit)
                if (i + 2 >= cs.Length) return;
                var val = (short)((cs[i + 1] << 8) | cs[i + 2]);
                Push(stack, ref stackDepth, val);
                i += 3;
                continue;
            }

            if (b0 == 255)
            {
                // 5-byte fixed 16.16 — push as double, skip 4 payload bytes
                if (i + 4 >= cs.Length) return;
                var intPart = (cs[i + 1] << 8) | cs[i + 2];
                var fracPart = (cs[i + 3] << 8) | cs[i + 4];
                Push(stack, ref stackDepth, intPart + fracPart / 65536.0);
                i += 5;
                continue;
            }

            // ── Operators (0-31, except 28) ───────────────────────────────────
            if (b0 == 12)
            {
                // Two-byte operator — we don't need to act on any two-byte op
                // for subroutine tracking; just clear stack and advance.
                if (i + 1 >= cs.Length) return;
                i += 2;
                stackDepth = 0;
                continue;
            }

            i++; // consume operator byte

            switch (b0)
            {
                case 1:  // hstem
                case 3:  // vstem
                case 18: // hstemhm
                case 23: // vstemhm
                    // Each pair of operands declares one stem hint.
                    // stackDepth may be odd if there is a width first (only for first stem group);
                    // integer division rounds down which naturally absorbs the width arg.
                    numHints += stackDepth / 2;
                    stackDepth = 0;
                    break;

                case 19: // hintmask
                case 20: // cntrmask
                    // Any remaining stack args are implicit vstem hints promoted at the first mask op.
                    // numHints is threaded via ref so it includes hints from the calling charstring
                    // context — critical for subroutines that issue hintmask without declaring hints
                    // themselves.
                    numHints += stackDepth / 2;
                    stackDepth = 0;
                    // Consume ceil(numHints/8) mask bytes. numHints must be > 0 for any valid
                    // charstring; if it is 0 here the font is malformed but we skip 0 bytes safely.
                    i += (numHints + 7) / 8;
                    break;

                case 10: // callsubr (local)
                    if (stackDepth > 0 && font.LocalSubrCount > 0)
                    {
                        var subrNum = (int)stack[stackDepth - 1] + font.LocalSubrBias;
                        stackDepth--; // pop the subr number
                        if (subrNum >= 0 && subrNum < font.LocalSubrCount)
                        {
                            usedLocalSubrs.Add(subrNum);
                            if (visitedLocalSubrs.Add(subrNum))
                            {
                                var subrCs = font.GetLocalSubr(subrNum);
                                // Thread numHints by ref so hint declarations and mask operations
                                // inside the subroutine see the cumulative count from the caller.
                                WalkCharstring(font, subrCs.Span,
                                    usedLocalSubrs, usedGlobalSubrs,
                                    visitedLocalSubrs, visitedGlobalSubrs,
                                    ref numHints);
                            }
                        }
                    }
                    break;

                case 29: // callgsubr (global)
                    if (stackDepth > 0 && font.GlobalSubrCount > 0)
                    {
                        var subrNum = (int)stack[stackDepth - 1] + font.GlobalSubrBias;
                        stackDepth--;
                        if (subrNum >= 0 && subrNum < font.GlobalSubrCount)
                        {
                            usedGlobalSubrs.Add(subrNum);
                            if (visitedGlobalSubrs.Add(subrNum))
                            {
                                var subrCs = font.GetGlobalSubr(subrNum);
                                // Thread numHints by ref — same reason as callsubr above.
                                WalkCharstring(font, subrCs.Span,
                                    usedLocalSubrs, usedGlobalSubrs,
                                    visitedLocalSubrs, visitedGlobalSubrs,
                                    ref numHints);
                            }
                        }
                    }
                    break;

                case 11: // return — end of subr
                case 14: // endchar — end of charstring
                    return;

                case 21: // rmoveto
                case 22: // hmoveto
                case 4:  // vmoveto
                    stackDepth = 0;
                    break;

                default:
                    // All other operators clear the stack
                    stackDepth = 0;
                    break;
            }
        }
    }

    private static void Push(Span<double> stack, ref int depth, double value)
    {
        if (depth < stack.Length)
            stack[depth] = value;
        depth++;
    }

    // ── Subroutine INDEX entry builder ────────────────────────────────────────

    /// <summary>
    /// Returns an array of subr entry byte arrays. Entries in <paramref name="usedSubrs"/>
    /// are copied verbatim; all others are replaced with a single <c>return</c> (0x0B) byte.
    /// </summary>
    private static byte[][] BuildSubrEntries(CffFont font, bool isLocal, HashSet<int> usedSubrs)
    {
        var count = isLocal ? font.LocalSubrCount : font.GlobalSubrCount;
        if (count == 0) return [];

        var entries = new byte[count][];
        for (var i = 0; i < count; i++)
        {
            entries[i] = usedSubrs.Contains(i)
                ? (isLocal ? font.GetLocalSubr(i) : font.GetGlobalSubr(i)).ToArray()
                : ReturnByte;
        }
        return entries;
    }

    // ── Private DICT rebuilder ────────────────────────────────────────────────

    /// <summary>
    /// Strips the Subrs operator (op 19) and its operand from the Private DICT bytes.
    /// </summary>
    private static byte[] StripPrivateDictSubrs(ReadOnlySpan<byte> dict)
    {
        var output = new MemoryStream(dict.Length);
        var operandStart = 0;
        var i = 0;

        while (i < dict.Length)
        {
            var b0 = dict[i];

            if (b0 <= 21)
            {
                if (b0 == 12)
                {
                    // Two-byte operator — preserve verbatim
                    output.Write(dict.Slice(operandStart, i - operandStart));
                    var opLen = i + 1 < dict.Length ? 2 : 1;
                    output.Write(dict.Slice(i, opLen));
                    i += opLen;
                    operandStart = i;
                }
                else if (b0 == 19)
                {
                    // Subrs (op 19) — skip operands + operator
                    i++;
                    operandStart = i;
                }
                else
                {
                    // Other single-byte operator — flush operands + operator
                    output.Write(dict.Slice(operandStart, i - operandStart));
                    output.WriteByte(b0);
                    i++;
                    operandStart = i;
                }
                continue;
            }

            // Advance through operand bytes
            if (b0 == 28) { i += 3; }
            else if (b0 == 29) { i += 5; }
            else if (b0 == 30)
            {
                i++;
                while (i < dict.Length)
                {
                    var nb = dict[i++];
                    if ((nb & 0x0F) == 0x0F || (nb >> 4) == 0x0F) break;
                }
            }
            else if (b0 is >= 32 and <= 246) { i++; }
            else if (b0 is >= 247 and <= 250) { i += 2; }
            else if (b0 is >= 251 and <= 254) { i += 2; }
            else { i++; }
        }

        // Flush any trailing bytes
        if (operandStart < dict.Length)
            output.Write(dict.Slice(operandStart));

        return output.ToArray();
    }

    /// <summary>
    /// Builds the Private DICT bytes from <paramref name="stripped"/> (sans Subrs op)
    /// and appends a new Subrs op 19 pointing to the local subr INDEX that will
    /// immediately follow this Private DICT in the output stream.
    /// The relative offset = (stripped.Length + encoded operand size + 1 byte for op 19).
    /// We iterate once to stabilise the encoded size.
    /// </summary>
    private static byte[] BuildPrivateDictWithSubrs(byte[] stripped, int localSubrIndexLength)
    {
        // The Subrs relative offset = privateDictNewSize (the local subr INDEX starts
        // immediately after the Private DICT). privateDictNewSize = stripped.Length
        // + operandBytes + 1 (op byte). We need to find the fixed point.

        // Try with a compact encoding first and verify size stability.
        // Use 5-byte form (b0=29) to guarantee one pass.
        const int opSize = 5 + 1; // 5-byte int + 1-byte op
        var privateDictSize = stripped.Length + opSize;

        var ms = new MemoryStream(privateDictSize);
        ms.Write(stripped);
        EncodeInt(ms, privateDictSize); // Subrs offset = immediately after this dict
        ms.WriteByte(19); // op Subrs

        // Verify encoded size matches
        var result = ms.ToArray();

        // If EncodeInt chose a shorter form and the size changed, redo with actual size
        var actualPrivateDictSize = result.Length;
        if (actualPrivateDictSize != privateDictSize)
        {
            // Re-encode with the actual size (compact form may be shorter)
            privateDictSize = actualPrivateDictSize;
            var ms2 = new MemoryStream(privateDictSize);
            ms2.Write(stripped);
            EncodeInt(ms2, privateDictSize);
            ms2.WriteByte(19);
            result = ms2.ToArray();

            // One more pass if it still shifted (highly unlikely but safe)
            if (result.Length != privateDictSize)
            {
                privateDictSize = result.Length;
                var ms3 = new MemoryStream(privateDictSize);
                ms3.Write(stripped);
                EncodeInt(ms3, privateDictSize);
                ms3.WriteByte(19);
                result = ms3.ToArray();
            }
        }

        return result;
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
        int privateDictNewSize,
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

        // Private (op 18) — size + offset (size = rebuilt Private DICT size only, not incl. local subrs)
        if (privateDictNewOffset > 0 && privateDictNewSize > 0)
        {
            EncodeInt(ms, privateDictNewSize);
            EncodeInt(ms, privateDictNewOffset);
            ms.WriteByte(18);
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
                    i += opLen;
                    operandStart = i;
                }
                else
                {
                    output.Write(dict.Slice(operandStart, i - operandStart));
                    output.Write(dict.Slice(i, opLen));
                    i += opLen;
                    operandStart = i;
                }
                continue;
            }

            if (b0 == 28) { i += 3; }
            else if (b0 == 29) { i += 5; }
            else if (b0 == 30)
            {
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
            else { i++; }
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
            return ([0x00, 0x00], 2);
        }

        var dataSize = entries.Sum(e => e.Length);
        var offSize = ComputeOffSize(dataSize + 1);

        var headerSize = 2 + 1 + (entries.Length + 1) * offSize;
        var totalSize = headerSize + dataSize;

        var buf = new byte[totalSize];
        var pos = 0;

        buf[pos++] = (byte)(entries.Length >> 8);
        buf[pos++] = (byte)entries.Length;
        buf[pos++] = (byte)offSize;

        var currentOffset = 1;
        WriteOffset(buf, ref pos, offSize, currentOffset);
        for (var i = 0; i < entries.Length; i++)
        {
            currentOffset += entries[i].Length;
            WriteOffset(buf, ref pos, offSize, currentOffset);
        }

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

    private static int NextSectionOffset(CffFont font)
    {
        var offsets = new List<int>();
        if (font.EncodingOffset > 2) offsets.Add(font.EncodingOffset);
        if (font.CharStringsOffset > 0) offsets.Add(font.CharStringsOffset);
        if (font.FdArrayOffset > 0) offsets.Add(font.FdArrayOffset);
        if (font.FdSelectOffset > 0) offsets.Add(font.FdSelectOffset);
        if (font.PrivateDictOffset > 0) offsets.Add(font.PrivateDictOffset);
        if (font.LocalSubrIndexOffset > 0) offsets.Add(font.LocalSubrIndexOffset);
        offsets.Add(font.Data.Length);

        return offsets.Where(o => o > font.CharsetOffset).DefaultIfEmpty(font.Data.Length).Min();
    }

    private static int NextSectionOffsetAfterEncoding(CffFont font)
    {
        var offsets = new List<int>();
        if (font.CharStringsOffset > 0) offsets.Add(font.CharStringsOffset);
        if (font.FdArrayOffset > 0) offsets.Add(font.FdArrayOffset);
        if (font.FdSelectOffset > 0) offsets.Add(font.FdSelectOffset);
        if (font.PrivateDictOffset > 0) offsets.Add(font.PrivateDictOffset);
        if (font.LocalSubrIndexOffset > 0) offsets.Add(font.LocalSubrIndexOffset);
        offsets.Add(font.Data.Length);

        return offsets.Where(o => o > font.EncodingOffset).DefaultIfEmpty(font.Data.Length).Min();
    }
}
