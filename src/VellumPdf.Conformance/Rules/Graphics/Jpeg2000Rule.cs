// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;

namespace VellumPdf.Conformance.Rules.Graphics;

/// <summary>
/// ISO 19005-2 §6.2.8.3 tests 1–5: structural constraints on JPEG2000 image data (JPXDecode
/// image XObjects). All five tests are evaluated by a single defensive JP2 box parser.
/// </summary>
/// <remarks>
/// <para>
/// <strong>§6.2.8.3-1 (colour channels):</strong>
/// The number of colour channels in the JPEG2000 data shall be 1, 3, or 4.
/// NC is read from the <c>ihdr</c> box (JP2 box file) or Csiz from the SIZ marker (raw codestream).
/// </para>
/// <para>
/// <strong>§6.2.8.3-2 (APPROX field):</strong>
/// If the number of colour space specifications in the JPEG2000 data is greater than 1, there shall
/// be exactly one colour space specification that has the value 0x01 in the APPROX field.
/// Applies only to JP2 box files (a raw codestream has no <c>colr</c> boxes).
/// </para>
/// <para>
/// <strong>§6.2.8.3-3 (METH field):</strong>
/// The value of the METH entry in each <c>colr</c> box shall be 0x01, 0x02, or 0x03.
/// Applies only to JP2 box files.
/// </para>
/// <para>
/// <strong>§6.2.8.3-4 (CIEJab prohibited):</strong>
/// JPEG2000 enumerated colour space 19 (CIEJab) shall not be used (a <c>colr</c> box with
/// METH==1 and EnumCS==19 is forbidden). Applies only to JP2 box files.
/// </para>
/// <para>
/// <strong>§6.2.8.3-5 (bit depth):</strong>
/// The bit depth of the JPEG2000 data shall have a value in the range 1 to 38. All colour channels
/// shall have the same bit depth. If the <c>ihdr</c> BPC field is 0xFF, the per-component bit depths
/// are read from the <c>bpcc</c> box; for a raw codestream they come from the Ssiz fields in SIZ.
/// </para>
/// <para>
/// <strong>Scope:</strong> all JPXDecode image XObjects drawn via <c>Do</c> in any page's resource
/// graph (exactly matching <see cref="ForbiddenXObjectRule"/>). An XObject present in resources but
/// never drawn is not checked (matches veraPDF's content-usage scoping).
/// </para>
/// <para>
/// <strong>Defensive operation:</strong> any parse failure, truncation, or unexpected structure in
/// the JP2 data silently stops the scan and produces no finding. Malformed input never triggers a
/// spurious violation.
/// </para>
/// <para>
/// <strong>Coverage:</strong> both JP2 box files (signature 00 00 00 0C 6A 50 20 20…) and raw
/// JPEG2000 codestreams (SOC marker FF 4F) are handled. For raw codestreams clauses -2, -3, and -4
/// do not apply (no <c>colr</c> boxes exist); -1 and -5 are fully checked via the SIZ marker.
/// </para>
/// <para>
/// Clean-room: authored from ISO 19005-2:2011 §6.2.8.3 and ISO 15444-1 §A.5.1 / §I.5.3.
/// Cross-validated against veraPDF 1.30.2: a conformant JP2 (NC=3, METH=1, EnumCS=16,
/// uniform BPC=8, single colr with APPROX=0) passes all five rules with zero findings.
/// </para>
/// </remarks>
internal sealed class Jpeg2000Rule : IConformanceRule
{
    // Primary rule id (the first of the five tests).
    public string RuleId => RuleId1;

    public string Clause => ClauseRef;

    private const string RuleId1 = "ISO19005-2:6.2.8.3-1";
    private const string RuleId2 = "ISO19005-2:6.2.8.3-2";
    private const string RuleId3 = "ISO19005-2:6.2.8.3-3";
    private const string RuleId4 = "ISO19005-2:6.2.8.3-4";
    private const string RuleId5 = "ISO19005-2:6.2.8.3-5";
    private const string ClauseRef = "ISO 19005-2:2011, 6.2.8.3";

    // JP2 box type codes (big-endian uint32 = 4 ASCII chars).
    private const uint BoxTypeJp2h = 0x6A703268u; // "jp2h"
    private const uint BoxTypeIhdr = 0x69686472u; // "ihdr"
    private const uint BoxTypeBpcc = 0x62706363u; // "bpcc"
    private const uint BoxTypeColr = 0x636F6C72u; // "colr"

    // JPEG2000 SOC marker (start of codestream) and SIZ marker.
    private const ushort MarkerSOC = 0xFF4F;
    private const ushort MarkerSIZ = 0xFF51;

    // CIEJab enumerated colour space value (ISO 15444-1 Table I.11).
    private const uint EnumCsCieJab = 19;

    private static readonly PdfName _xobject = new("XObject");
    private static readonly PdfName _filter = new("Filter");
    private static readonly PdfName _jpxDecode = new("JPXDecode");

    public void Evaluate(PreflightContext context)
    {
        // Each image XObject object number is checked at most once even if referenced on
        // multiple pages. ALL per-document state is local to this method (thread safety).
        var checkedImages = new HashSet<int>();

        foreach (var page in context.EnumeratePages())
        {
            if (context.ResolveInherited(page, PdfName.Resources) is not PdfDictionary resources)
                continue;
            if (context.Resolve(resources.Get(_xobject)) is not PdfDictionary xobjects)
                continue;

            // Only check XObjects that are actually drawn (matches ForbiddenXObjectRule scoping).
            var drawn = ContentStreamUsage.Analyze(context, page).DrawnXObjects;

            foreach (var entry in xobjects.Entries)
            {
                if (!drawn.Contains(entry.Key.Value))
                    continue;

                // Deduplicate by indirect object number.
                if (entry.Value is PdfIndirectReference r && !checkedImages.Add(r.ObjectNumber))
                    continue;

                if (context.ResolveStream(entry.Value) is not { } stream)
                    continue;

                var subtype = (context.Resolve(stream.Dictionary.Get(PdfName.Subtype)) as PdfName)?.Value;
                if (subtype != "Image")
                    continue;

                if (!IsJpxDecodeImage(context, stream.Dictionary))
                    continue;

                // The raw (undecoded) stream body is the JP2 / JPEG2000 data.
                var jp2Bytes = stream.RawBody.Span;
                if (jp2Bytes.IsEmpty)
                    continue;

                try
                {
                    CheckJp2Data(context, jp2Bytes);
                }
                catch
                {
                    // Any unhandled parse exception → silent no-op (defensive).
                }
            }
        }
    }

    // Returns true when the image XObject's /Filter is /JPXDecode (name or last element of array).
    private bool IsJpxDecodeImage(PreflightContext context, PdfDictionary imageDict)
    {
        var filterObj = context.Resolve(imageDict.Get(_filter));
        switch (filterObj)
        {
            case PdfName name:
                return name.Value == "JPXDecode";

            case PdfArray arr:
                // An array filter chain: the last element is the outermost compression.
                // JPXDecode is never used as an intermediate filter, so matching the last name suffices.
                if (arr.Count > 0 && context.Resolve(arr[arr.Count - 1]) is PdfName lastName)
                    return lastName.Value == "JPXDecode";
                return false;

            default:
                return false;
        }
    }

    // Parses the JP2 or raw-codestream bytes and emits findings for each violated clause.
    private void CheckJp2Data(PreflightContext context, ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
            return;

        // Detect format by first two bytes.
        var firstWord = (ushort)((data[0] << 8) | data[1]);
        if (firstWord == MarkerSOC)
        {
            // Raw JPEG2000 codestream — check clauses -1 and -5 via SIZ marker.
            CheckRawCodestream(context, data);
        }
        else if (data.Length >= 12 && IsJp2BoxFile(data))
        {
            // JP2 box file — parse jp2h superbox for ihdr, bpcc, colr boxes.
            CheckJp2BoxFile(context, data);
        }
        // Unknown format → silent skip (defensive; never a spurious finding).
    }

    // Checks for the JP2 signature box: LBox=12, TBox="jP  ", magic=0D 0A 87 0A.
    private static bool IsJp2BoxFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12)
            return false;
        // TBox at offset 4: "jP  " = 6A 50 20 20
        if (data[4] != 0x6A || data[5] != 0x50 || data[6] != 0x20 || data[7] != 0x20)
            return false;
        // Magic payload: 0D 0A 87 0A
        return data[8] == 0x0D && data[9] == 0x0A && data[10] == 0x87 && data[11] == 0x0A;
    }

    // ── Raw codestream path ────────────────────────────────────────────────────────────────────────

    private void CheckRawCodestream(PreflightContext context, ReadOnlySpan<byte> data)
    {
        // Locate the SIZ marker (must immediately follow SOC per ISO 15444-1 §A.4).
        // SOC = FF 4F (2 bytes), then SIZ marker = FF 51.
        if (data.Length < 4)
            return;
        var marker = (ushort)((data[2] << 8) | data[3]);
        if (marker != MarkerSIZ)
            return;

        // SIZ segment: Lsiz(2) at offset 4; minimum Lsiz is 38 (no components).
        if (data.Length < 6)
            return;
        var lsiz = ReadUInt16(data, 4);
        if (lsiz < 38)
            return;

        // Segment payload starts at offset 4 (includes Lsiz itself). Total segment length = Lsiz.
        // End of SIZ segment = offset 4 + Lsiz.
        var segEnd = 4 + lsiz;
        if (data.Length < segEnd)
            return;

        // SIZ fields (offsets relative to start of SIZ segment payload, i.e. data[4..]):
        // Rsiz(2) Xsiz(4) Ysiz(4) XOsiz(4) YOsiz(4) XTsiz(4) YTsiz(4) XTOsiz(4) YTOsiz(4) Csiz(2)
        // = 2+4+4+4+4+4+4+4+4+2 = 36 bytes of fixed fields (after Lsiz).
        // So Csiz is at offset 4+2+36 = 42, i.e. data[4+2+34] = data[40].
        // Wait: Lsiz(2) is at data[4], Rsiz(2) at data[6], Xsiz(4) at data[8], Ysiz(4) at data[12],
        // XOsiz(4) at data[16], YOsiz(4) at data[20], XTsiz(4) at data[24], YTsiz(4) at data[28],
        // XTOsiz(4) at data[32], YTOsiz(4) at data[36], Csiz(2) at data[40].
        if (data.Length < 42)
            return;
        var csiz = ReadUInt16(data, 40); // number of components

        // §6.2.8.3-1: NC must be 1, 3, or 4.
        if (csiz is not (1 or 3 or 4))
        {
            context.Report(RuleId1, ClauseRef, PreflightSeverity.Error,
                $"The JPEG2000 codestream has {csiz} colour channel(s); the number shall be 1, 3, or 4.");
        }

        // Per-component Ssiz bytes start at data[42]: 3 bytes per component (Ssiz, XRsiz, YRsiz).
        var compBase = 42;
        var compBlockSize = 3;
        if (data.Length < compBase + csiz * compBlockSize)
            return;

        // §6.2.8.3-5: bit-depth in 1..38; all channels equal.
        int? firstBitDepth = null;
        var uniformBitDepths = true;
        var allInRange = true;

        for (var i = 0; i < csiz; i++)
        {
            var ssiz = data[compBase + i * compBlockSize];
            var bitDepth = (ssiz & 0x7F) + 1;
            if (bitDepth is < 1 or > 38)
                allInRange = false;
            if (firstBitDepth is null)
                firstBitDepth = bitDepth;
            else if (bitDepth != firstBitDepth.Value)
                uniformBitDepths = false;
        }

        if (!allInRange || !uniformBitDepths)
        {
            var msg = !allInRange
                ? "The bit depth of one or more JPEG2000 colour channels is outside the permitted range of 1 to 38."
                : "All colour channels in the JPEG2000 data shall have the same bit depth.";
            context.Report(RuleId5, ClauseRef, PreflightSeverity.Error, msg);
        }

        // Clauses -2, -3, -4 do not apply to raw codestreams (no colr boxes).
    }

    // ── JP2 box file path ──────────────────────────────────────────────────────────────────────────

    private void CheckJp2BoxFile(PreflightContext context, ReadOnlySpan<byte> data)
    {
        // Walk top-level boxes to find jp2h.
        var pos = 0;
        while (pos < data.Length)
        {
            if (!ReadBoxHeader(data, pos, out var boxType, out var payloadOffset, out var boxEnd))
                return;

            if (boxType == BoxTypeJp2h)
            {
                // Found the jp2h superbox — parse its children.
                CheckJp2hBox(context, data[payloadOffset..boxEnd]);
                return;
            }

            if (boxEnd <= pos)
                return; // safety: prevent infinite loop
            pos = boxEnd;
        }
        // No jp2h found → silent skip (defensive).
    }

    private void CheckJp2hBox(PreflightContext context, ReadOnlySpan<byte> jp2hPayload)
    {
        int nc = 0;
        var bpcRaw = (byte)0;
        var bpcBoxBytes = ReadOnlySpan<byte>.Empty;
        var colrBoxes = new List<ColrBox>();

        // Parse child boxes of jp2h.
        var pos = 0;
        while (pos < jp2hPayload.Length)
        {
            if (!ReadBoxHeader(jp2hPayload, pos, out var boxType, out var payloadOffset, out var boxEnd))
                break;

            var payload = jp2hPayload[payloadOffset..boxEnd];

            if (boxType == BoxTypeIhdr)
            {
                // ihdr payload: Height(4) Width(4) NC(2) BPC(1) C(1) UnkC(1) IPR(1) = 14 bytes
                if (payload.Length >= 14)
                {
                    nc = ReadUInt16(payload, 8);
                    bpcRaw = payload[10];
                }
            }
            else if (boxType == BoxTypeBpcc)
            {
                // bpcc payload: one byte per component (bit-depth-1 | sign-bit).
                bpcBoxBytes = payload;
            }
            else if (boxType == BoxTypeColr)
            {
                // colr payload: METH(1) PREC(1) APPROX(1) [EnumCS(4) | ICC...]
                if (payload.Length >= 3)
                {
                    var meth = payload[0];
                    var approx = payload[2];
                    uint? enumCs = null;
                    if (meth == 1 && payload.Length >= 7)
                        enumCs = ReadUInt32(payload, 3);
                    colrBoxes.Add(new ColrBox(meth, approx, enumCs));
                }
            }

            if (boxEnd <= pos)
                break;
            pos = boxEnd;
        }

        // §6.2.8.3-1: NC must be 1, 3, or 4.
        if (nc is not (1 or 3 or 4) && nc != 0)
        {
            context.Report(RuleId1, ClauseRef, PreflightSeverity.Error,
                $"The JPEG2000 data has {nc} colour channel(s); the number shall be 1, 3, or 4.");
        }

        // §6.2.8.3-2: if more than one colr box, exactly one shall have APPROX==1.
        if (colrBoxes.Count > 1)
        {
            var approxOneCount = 0;
            foreach (var colr in colrBoxes)
                if (colr.Approx == 1)
                    approxOneCount++;
            if (approxOneCount != 1)
            {
                context.Report(RuleId2, ClauseRef, PreflightSeverity.Error,
                    $"The JPEG2000 data has {colrBoxes.Count} colour space specifications "
                    + $"but {approxOneCount} have APPROX=0x01; exactly one shall have APPROX=0x01.");
            }
        }

        // §6.2.8.3-3: every colr box METH must be 0x01, 0x02, or 0x03.
        foreach (var colr in colrBoxes)
        {
            if (colr.Meth is not (1 or 2 or 3))
            {
                context.Report(RuleId3, ClauseRef, PreflightSeverity.Error,
                    $"A 'colr' box in the JPEG2000 data has METH=0x{colr.Meth:X2}; "
                    + "the value shall be 0x01, 0x02, or 0x03.");
                break; // one finding per image for this clause
            }
        }

        // §6.2.8.3-4: JPEG2000 enumerated colour space 19 (CIEJab) shall not be used.
        foreach (var colr in colrBoxes)
        {
            if (colr.Meth == 1 && colr.EnumCs == EnumCsCieJab)
            {
                context.Report(RuleId4, ClauseRef, PreflightSeverity.Error,
                    "The JPEG2000 data uses enumerated colour space 19 (CIEJab), which is not permitted.");
                break; // one finding per image
            }
        }

        // §6.2.8.3-5: bit depths in range 1..38 and all equal.
        if (nc > 0)
        {
            int[]? bitDepths;
            if (bpcRaw == 0xFF)
            {
                // Per-component bit depths from bpcc box.
                if (bpcBoxBytes.IsEmpty || bpcBoxBytes.Length < nc)
                    return; // Can't verify — missing bpcc; skip defensively.
                bitDepths = new int[nc];
                for (var i = 0; i < nc; i++)
                    bitDepths[i] = (bpcBoxBytes[i] & 0x7F) + 1;
            }
            else
            {
                // Uniform bit depth from ihdr BPC.
                var depth = (bpcRaw & 0x7F) + 1;
                bitDepths = new int[nc];
                for (var i = 0; i < nc; i++)
                    bitDepths[i] = depth;
            }

            var allInRange = true;
            var uniform = true;
            var firstDepth = bitDepths[0];
            for (var i = 0; i < bitDepths.Length; i++)
            {
                if (bitDepths[i] is < 1 or > 38)
                    allInRange = false;
                if (bitDepths[i] != firstDepth)
                    uniform = false;
            }

            if (!allInRange || !uniform)
            {
                var msg = !allInRange
                    ? "The bit depth of one or more JPEG2000 colour channels is outside the permitted range of 1 to 38."
                    : "All colour channels in the JPEG2000 data shall have the same bit depth.";
                context.Report(RuleId5, ClauseRef, PreflightSeverity.Error, msg);
            }
        }
    }

    // ── JP2 box parsing helpers ────────────────────────────────────────────────────────────────────

    // Reads a box header at offset `pos` in `data`. Returns false on truncation.
    // payloadOffset: data index where the box payload starts.
    // boxEnd: data index where this box ends (exclusive).
    private static bool ReadBoxHeader(
        ReadOnlySpan<byte> data, int pos,
        out uint boxType, out int payloadOffset, out int boxEnd)
    {
        boxType = 0;
        payloadOffset = pos;
        boxEnd = pos;

        if (pos + 8 > data.Length)
            return false;

        var lbox = ReadUInt32(data, pos);
        boxType = ReadUInt32(data, pos + 4);

        long totalLength;
        int headerLength;

        if (lbox == 0)
        {
            // Box extends to end of file.
            totalLength = data.Length - pos;
            headerLength = 8;
        }
        else if (lbox == 1)
        {
            // Extended 8-byte XLBox follows the 8-byte standard header.
            if (pos + 16 > data.Length)
                return false;
            var xlbox = ReadUInt64(data, pos + 8);
            if (xlbox > (ulong)(data.Length - pos))
                return false;
            totalLength = (long)xlbox;
            headerLength = 16;
        }
        else
        {
            // Standard: LBox includes the 8-byte header.
            if (lbox < 8)
                return false;
            totalLength = lbox;
            headerLength = 8;
        }

        if (totalLength > data.Length - pos)
            return false;

        payloadOffset = pos + headerLength;
        boxEnd = (int)(pos + totalLength);
        return true;
    }

    // ── Binary read helpers ────────────────────────────────────────────────────────────────────────

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset)
        => (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset)
        => (ushort)((data[offset] << 8) | data[offset + 1]);

    private static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset)
    {
        var hi = ReadUInt32(data, offset);
        var lo = ReadUInt32(data, offset + 4);
        return ((ulong)hi << 32) | lo;
    }

    // ── Data types ─────────────────────────────────────────────────────────────────────────────────

    private sealed class ColrBox(byte meth, byte approx, uint? enumCs)
    {
        public byte Meth { get; } = meth;
        public byte Approx { get; } = approx;
        public uint? EnumCs { get; } = enumCs;
    }
}
