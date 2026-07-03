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

        // Free-list head subsection (object 0)
        writer.WriteAscii("0 1\n"u8);
        writer.WriteAscii("0000000000 65535 f\r\n"u8);

        // First-page objects subsection — one contiguous run
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
    /// Writes the main xref section and its trailer. Returns the offset of the xref keyword.
    /// </summary>
    public long WriteMainXrefAndTrailer(
        PdfWriter writer,
        int restCount,
        int totalSize,
        PdfIndirectReference catalogRef,
        PdfIndirectReference? infoRef,
        ReadOnlySpan<byte> documentId)
    {
        var xrefOffset = writer.Position;

        writer.WriteAscii("xref\n"u8);

        // Free-list head
        writer.WriteAscii("0 1\n"u8);
        writer.WriteAscii("0000000000 65535 f\r\n"u8);

        // Rest objects: 1..restCount (contiguous)
        if (restCount > 0)
        {
            WriteInt(writer, 1);
            writer.WriteByte((byte)' ');
            WriteInt(writer, restCount);
            writer.WriteByte((byte)'\n');
            for (var n = 1; n <= restCount; n++)
            {
                Write10Digits(writer, _offsets.TryGetValue(n, out var off) ? off : 0);
                writer.WriteAscii(" 00000 n\r\n"u8);
            }
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

        return xrefOffset;
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
