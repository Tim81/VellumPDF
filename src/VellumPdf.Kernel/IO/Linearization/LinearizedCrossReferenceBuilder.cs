// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using VellumPdf.Core;

namespace VellumPdf.IO.Linearization;

/// <summary>
/// Emits the two classic xref sections that a linearized PDF requires.
///
/// Linearized xref layout (ISO 32000-2 Annex F):
///   — first-page xref near the top of the file, covering the first-page object block
///   — main xref at the end of the file, covering the rest objects
///   — a single trailing startxref pointing to the FIRST-PAGE xref
///   — first-page trailer /Prev → main-xref offset
///   — main trailer: no /Prev
///
/// The first-page xref is written once with fixed-width 10-digit placeholders for
/// all object offsets and for the /Prev value. Pass 2 patches those fields in place
/// without changing any surrounding byte positions.
/// </summary>
internal sealed class LinearizedCrossReferenceBuilder
{
    private readonly Dictionary<int, long> _offsets = new();

    // Positions (absolute byte offsets) of the 10-digit fields in the first-page xref
    // that must be patched after all objects are written.
    private long _prevValueByteOffset;
    private readonly Dictionary<int, long> _fpEntryByteOffsets = new(); // newObjNum → byte offset of its 10-digit field

    /// <summary>Records the file offset for a written object with the given new number.</summary>
    public void RecordOffset(int newObjNum, long offset) => _offsets[newObjNum] = offset;

    /// <summary>
    /// Writes the first-page xref section and trailer with zero-padded 10-digit placeholder
    /// values for all object offsets and for /Prev. Records the byte positions of those fields
    /// so <see cref="PatchFirstPageXref"/> can fill in real values after all objects are written.
    ///
    /// Returns the byte offset of the xref keyword (= firstPageXrefOffset).
    /// </summary>
    public long WriteFirstPageXrefPlaceholder(
        PdfWriter writer,
        int firstObjInFpSection,
        int lastObjInFpSection,
        int totalSize,
        PdfIndirectReference catalogRef,
        PdfIndirectReference? infoRef,
        ReadOnlySpan<byte> documentId)
    {
        var xrefOffset = writer.Position;

        writer.WriteAscii("xref\n"u8);

        // First-page objects subsection — one contiguous run. Object 0's free-list head
        // lives only in the main xref, matching qpdf's linearized layout.
        WriteInt(writer, firstObjInFpSection);
        writer.WriteByte((byte)' ');
        WriteInt(writer, lastObjInFpSection - firstObjInFpSection + 1);
        writer.WriteByte((byte)'\n');

        for (var n = firstObjInFpSection; n <= lastObjInFpSection; n++)
        {
            _fpEntryByteOffsets[n] = writer.Position;
            Write10Digits(writer, 0); // placeholder — patched later
            writer.WriteAscii(" 00000 n\r\n"u8);
        }

        // Trailer with /Prev as fixed-width 10-digit placeholder
        _prevValueByteOffset = WriteTrailerWithFixedPrev(
            writer, totalSize, catalogRef, infoRef, documentId, prevValue: 0);

        // First-page section ends with "startxref\n0\n%%EOF" per ISO 32000-2 §F.3.4
        writer.WriteAscii("\nstartxref\n0\n%%EOF\n"u8);

        return xrefOffset;
    }

    /// <summary>
    /// Patches the first-page xref entries and /Prev value in the backing byte array
    /// after all objects have been written and the main xref offset is known.
    /// </summary>
    public void PatchFirstPageXref(byte[] buf, long mainXrefOffset)
    {
        // Patch each first-page object's xref entry offset
        foreach (var (newObjNum, entryPos) in _fpEntryByteOffsets)
        {
            var realOffset = _offsets.TryGetValue(newObjNum, out var off) ? off : 0;
            WriteTenDigits(buf, (int)entryPos, realOffset);
        }

        // Patch /Prev with the real main-xref offset
        WriteTenDigits(buf, (int)_prevValueByteOffset, mainXrefOffset);
    }

    /// <summary>
    /// Writes the main xref section and its trailer as a single <c>0 M</c> subsection covering
    /// the free-list head plus the rest objects. Returns the offset of the xref keyword (for the
    /// first-page trailer's /Prev) and the offset of the whitespace preceding the first entry
    /// (the linearization dictionary's /T value).
    /// </summary>
    public (long XrefOffset, long TOffset) WriteMainXrefAndTrailer(
        PdfWriter writer,
        int restCount,
        int totalSize,
        PdfIndirectReference catalogRef,
        PdfIndirectReference? infoRef,
        ReadOnlySpan<byte> documentId)
    {
        var xrefOffset = writer.Position;

        writer.WriteAscii("xref\n"u8);

        // Single subsection: object 0 (free head) followed by rest objects 1..restCount.
        WriteInt(writer, 0);
        writer.WriteByte((byte)' ');
        WriteInt(writer, restCount + 1);
        var tOffset = writer.Position; // /T points at the whitespace before the first entry
        writer.WriteByte((byte)'\n');

        writer.WriteAscii("0000000000 65535 f\r\n"u8);
        for (var n = 1; n <= restCount; n++)
        {
            Write10Digits(writer, _offsets.TryGetValue(n, out var off) ? off : 0);
            writer.WriteAscii(" 00000 n\r\n"u8);
        }

        writer.WriteAscii("trailer\n"u8);
        var trailer = new PdfDictionary()
            .Set(PdfName.Size, totalSize)
            .Set(PdfName.Root, catalogRef);
        if (infoRef is not null)
            trailer.Set(PdfName.Info, infoRef);
        if (!documentId.IsEmpty && documentId.Length == 16)
        {
            trailer.Set(PdfName.ID, new PdfArray([
                new PdfHexString(documentId.ToArray()),
                new PdfHexString(documentId.ToArray()),
            ]));
        }
        trailer.WriteTo(writer);

        return (xrefOffset, tOffset);
    }

    /// <summary>Byte positions of the linearization dictionary's fixed-width offset fields.</summary>
    internal readonly record struct LinDictPlaceholders(long L, long HOffset, long HLength, long E, long T);

    /// <summary>
    /// Writes the linearization parameter dictionary (ISO 32000-2 Table F.1) as the given
    /// indirect object. Every offset/length-valued field (<c>/L</c>, both <c>/H</c> entries,
    /// <c>/E</c>, <c>/T</c>) is a fixed-width 10-digit placeholder so the dictionary's own byte
    /// length is deterministic; pass 2 patches the placeholders once the real values are known.
    /// <c>/O</c> and <c>/N</c> are known up front and written directly.
    /// </summary>
    public LinDictPlaceholders WriteLinearizationDict(
        PdfWriter w, int objNum, int firstPageObjNum, int npages)
    {
        WriteInt(w, objNum);
        w.WriteAscii(" 0 obj\n<< /Linearized 1 /L "u8);
        var lPos = w.Position;
        Write10Digits(w, 0);
        w.WriteAscii(" /H ["u8);
        var hOffPos = w.Position;
        Write10Digits(w, 0);
        w.WriteByte((byte)' ');
        var hLenPos = w.Position;
        Write10Digits(w, 0);
        w.WriteAscii("] /O "u8);
        WriteInt(w, firstPageObjNum);
        w.WriteAscii(" /E "u8);
        var ePos = w.Position;
        Write10Digits(w, 0);
        w.WriteAscii(" /N "u8);
        WriteInt(w, npages);
        w.WriteAscii(" /T "u8);
        var tPos = w.Position;
        Write10Digits(w, 0);
        w.WriteAscii(" >>\nendobj\n"u8);
        return new LinDictPlaceholders(lPos, hOffPos, hLenPos, ePos, tPos);
    }

    /// <summary>Patches the linearization dictionary's offset fields in the backing array (pass 2).</summary>
    public static void PatchLinDict(
        byte[] buf, LinDictPlaceholders p, long l, long hOffset, long hLength, long e, long t)
    {
        WriteTenDigits(buf, (int)p.L, l);
        WriteTenDigits(buf, (int)p.HOffset, hOffset);
        WriteTenDigits(buf, (int)p.HLength, hLength);
        WriteTenDigits(buf, (int)p.E, e);
        WriteTenDigits(buf, (int)p.T, t);
    }

    /// <summary>Writes the final startxref pointing to the first-page xref offset.</summary>
    public static void WriteFinalStartxref(PdfWriter writer, long firstPageXrefOffset)
    {
        writer.WriteAscii("\nstartxref\n"u8);
        WriteInt(writer, firstPageXrefOffset);
        writer.WriteAscii("\n%%EOF\n"u8);
    }

    // Writes the trailer with /Prev as a fixed-width 10-digit field.
    // Returns the absolute byte offset of the 10-digit field so it can be patched later.
    private static long WriteTrailerWithFixedPrev(
        PdfWriter writer,
        int size,
        PdfIndirectReference catalogRef,
        PdfIndirectReference? infoRef,
        ReadOnlySpan<byte> documentId,
        long prevValue)
    {
        writer.WriteAscii("trailer\n<<"u8);
        writer.WriteAscii("\n/Size "u8);
        WriteInt(writer, size);
        writer.WriteAscii("\n/Root "u8);
        WriteRef(writer, catalogRef);
        if (infoRef is not null)
        {
            writer.WriteAscii("\n/Info "u8);
            WriteRef(writer, infoRef);
        }
        if (!documentId.IsEmpty && documentId.Length == 16)
        {
            writer.WriteAscii("\n/ID ["u8);
            WriteHexId(writer, documentId);
            writer.WriteAscii(" "u8);
            WriteHexId(writer, documentId);
            writer.WriteAscii("]"u8);
        }
        writer.WriteAscii("\n/Prev "u8);
        var prevOffset = writer.Position;
        Write10Digits(writer, prevValue);
        writer.WriteAscii("\n>>"u8);
        return prevOffset;
    }

    private static void WriteTenDigits(byte[] buf, int pos, long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n > 9_999_999_999L)
            throw new NotSupportedException(
                $"Byte offset {n} exceeds 9,999,999,999 — cannot fit in 10 digits.");
        for (var i = 9; i >= 0; i--)
        {
            buf[pos + i] = (byte)('0' + n % 10);
            n /= 10;
        }
    }

    private static void WriteHexId(PdfWriter w, ReadOnlySpan<byte> id)
    {
        w.WriteByte((byte)'<');
        foreach (var b in id)
        {
            w.WriteByte((byte)(b >> 4 < 10 ? '0' + (b >> 4) : 'A' + (b >> 4) - 10));
            w.WriteByte((byte)((b & 0xF) < 10 ? '0' + (b & 0xF) : 'A' + (b & 0xF) - 10));
        }
        w.WriteByte((byte)'>');
    }

    private static void WriteRef(PdfWriter w, PdfIndirectReference r)
    {
        WriteInt(w, r.ObjectNumber);
        w.WriteAscii(" 0 R"u8);
    }

    internal static void Write10Digits(PdfWriter w, long n)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(n);
        if (n > 9_999_999_999L)
            throw new NotSupportedException(
                $"Byte offset {n} exceeds 9,999,999,999 — the classic xref table " +
                "cannot represent offsets beyond 10 digits. Use UseObjectStreams = true for files > ~9 GB.");
        Span<byte> buf = stackalloc byte[10];
        buf.Fill((byte)'0');
        var tmp = n;
        for (var i = 9; i >= 0 && tmp > 0; i--)
        {
            buf[i] = (byte)('0' + tmp % 10);
            tmp /= 10;
        }
        w.WriteAscii(buf);
    }

    private static void WriteInt(PdfWriter w, long n)
    {
        Span<byte> buf = stackalloc byte[20];
        Utf8Formatter.TryFormat(n, buf, out var len);
        w.WriteAscii(buf[..len]);
    }
}
