// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Buffers.Text;
using VellumPdf.Core;

namespace VellumPdf.IO;

/// <summary>
/// Writes an incremental-update xref table and trailer (ISO 32000-2 §7.5.6).
///
/// Unlike <see cref="CrossReferenceBuilder"/>, which writes a single contiguous section
/// starting at object 0, this writer accepts a sparse set of (object-number, byte-offset)
/// pairs and groups them into contiguous subsections, preceded by the mandatory free-list
/// head subsection "0 1".
///
/// Callers keep this type internal; it is only consumed by the reader-level append helper.
/// </summary>
internal static class IncrementalCrossReferenceBuilder
{
    /// <summary>
    /// Writes the incremental xref section + trailer and returns the byte offset of the
    /// <c>xref</c> keyword (the value that the NEXT revision will record as <c>/Prev</c>).
    /// </summary>
    /// <param name="writer">PDF writer positioned immediately after the last appended object.</param>
    /// <param name="writtenObjects">
    /// Triples of (objectNumber, generation, absoluteByteOffset) for every object written in
    /// this revision. Rewriting an existing object number keeps that object's existing
    /// generation — generation only advances when a freed number is reused for a different
    /// object (ISO 32000-2 §7.5.4); this builder trusts the caller to have gotten that right
    /// and just records whatever it is given. May be supplied in any order (sorted internally
    /// for the xref). Must not be empty.
    /// </param>
    /// <param name="baseSize">
    /// The /Size value from the base document's trailer. The new /Size is
    /// max(<paramref name="baseSize"/>, maxObjectNumber + 1).
    /// </param>
    /// <param name="catalogRef">
    /// Carried /Root reference from the base trailer. Its generation must match whatever
    /// <paramref name="writtenObjects"/> records for that same object number — or, if /Root
    /// was not itself rewritten in this revision, the generation the base document already has
    /// on file for it — or the appended trailer disagrees with the xref entry and object
    /// header it sits next to.
    /// </param>
    /// <param name="prevXrefOffset">The base document's startxref value, written as /Prev.</param>
    /// <param name="documentId">
    /// The base document's /ID array entries (first and second). Written verbatim — never
    /// modified in incremental updates (ISO 32000-2 §14.4).
    /// </param>
    internal static long WriteIncrementalXrefAndTrailer(
        PdfWriter writer,
        IReadOnlyList<(int ObjectNumber, int Generation, long ByteOffset)> writtenObjects,
        int baseSize,
        PdfIndirectReference catalogRef,
        long prevXrefOffset,
        PdfArray? documentId)
    {
        var sorted = WriteXrefSections(writer, writtenObjects, out var xrefOffset);

        // ── Trailer ──────────────────────────────────────────────────────────
        int maxObjNum = sorted[sorted.Count - 1].ObjectNumber;
        int newSize = Math.Max(baseSize, maxObjNum + 1);

        writer.WriteAscii("trailer\n"u8);
        var trailer = new PdfDictionary()
            .Set(PdfName.Size, newSize)
            .Set(PdfName.Root, catalogRef)
            .Set(PdfName.Prev, prevXrefOffset);

        if (documentId is not null)
            trailer.Set(PdfName.ID, documentId);

        trailer.WriteTo(writer);

        writer.WriteAscii("\nstartxref\n"u8);
        WriteInt(writer, xrefOffset);
        writer.WriteAscii("\n%%EOF\n"u8);

        return xrefOffset;
    }

    /// <summary>
    /// Writes a complete, single-revision classic cross-reference table and trailer — the shape a
    /// full rewrite needs rather than an incremental update: no <c>/Prev</c>, and the caller supplies
    /// the entire trailer dictionary (already carrying <c>/Size</c>, <c>/Root</c>, and whatever else
    /// it wants) instead of this method assembling one from a base document's own trailer.
    /// </summary>
    /// <param name="writer">PDF writer positioned where the xref table should begin.</param>
    /// <param name="writtenObjects">
    /// Triples of (objectNumber, generation, absoluteByteOffset) for every object in the document.
    /// Must not be empty; duplicate object numbers are rejected — see
    /// <see cref="WriteIncrementalXrefAndTrailer"/>'s parameter of the same name for the full
    /// contract, which this method shares.
    /// </param>
    /// <param name="trailer">
    /// The complete trailer dictionary to write verbatim. The caller decides its contents (no
    /// <c>/Prev</c> or <c>/XRefStm</c> belongs in a full single-revision rewrite); this method does
    /// not add, remove, or validate any of its entries.
    /// </param>
    internal static long WriteFullDocumentXrefAndTrailer(
        PdfWriter writer,
        IReadOnlyList<(int ObjectNumber, int Generation, long ByteOffset)> writtenObjects,
        PdfDictionary trailer)
    {
        WriteXrefSections(writer, writtenObjects, out var xrefOffset);

        writer.WriteAscii("trailer\n"u8);
        trailer.WriteTo(writer);

        writer.WriteAscii("\nstartxref\n"u8);
        WriteInt(writer, xrefOffset);
        writer.WriteAscii("\n%%EOF\n"u8);

        return xrefOffset;
    }

    /// <summary>
    /// Writes the <c>xref</c> keyword, the mandatory free-list head subsection, and one subsection
    /// per contiguous run of <paramref name="writtenObjects"/> — the part <see
    /// cref="WriteIncrementalXrefAndTrailer"/> and <see cref="WriteFullDocumentXrefAndTrailer"/> both
    /// need verbatim, since a full rewrite's xref table has the identical subsection shape as an
    /// incremental one; only the trailer that follows differs (<c>/Prev</c> present or not, and
    /// where the rest of the trailer's contents come from).
    /// </summary>
    private static List<(int ObjectNumber, int Generation, long ByteOffset)> WriteXrefSections(
        PdfWriter writer,
        IReadOnlyList<(int ObjectNumber, int Generation, long ByteOffset)> writtenObjects,
        out long xrefOffset)
    {
        if (writtenObjects.Count == 0)
            throw new ArgumentException("At least one written object is required.", nameof(writtenObjects));

        xrefOffset = writer.Position;

        writer.WriteAscii("xref\n"u8);

        // ── Free-list head subsection: "0 1\n0000000000 65535 f\r\n" ─────────
        writer.WriteAscii("0 1\n"u8);
        writer.WriteAscii("0000000000 65535 f\r\n"u8);

        // ── Group written objects into contiguous runs ────────────────────────
        // The file write-order of objects is irrelevant (each entry carries its own
        // recorded offset), but xref subsections must list object numbers in ascending
        // order. Sort defensively so callers may pass objects in any order.
        var sorted = writtenObjects.OrderBy(o => o.ObjectNumber).ToList();
        for (var i = 1; i < sorted.Count; i++)
            if (sorted[i].ObjectNumber == sorted[i - 1].ObjectNumber)
                throw new ArgumentException(
                    $"Duplicate object number {sorted[i].ObjectNumber} in the revision.",
                    nameof(writtenObjects));
        var runs = GroupIntoRuns(sorted);

        foreach (var (firstObjNum, entries) in runs)
        {
            WriteInt(writer, firstObjNum);
            writer.WriteByte((byte)' ');
            WriteInt(writer, entries.Count);
            writer.WriteByte((byte)'\n');

            foreach (var (_, generation, offset) in entries)
            {
                Write10Digits(writer, offset);
                writer.WriteByte((byte)' ');
                Write5Digits(writer, generation);
                writer.WriteAscii(" n\r\n"u8);
            }
        }

        return sorted;
    }

    private static List<(int FirstObjNum, List<(int ObjectNumber, int Generation, long ByteOffset)> Entries)> GroupIntoRuns(
        IReadOnlyList<(int ObjectNumber, int Generation, long ByteOffset)> items)
    {
        var runs = new List<(int, List<(int, int, long)>)>();
        List<(int, int, long)>? current = null;
        int prevNum = -2;

        foreach (var (objNum, generation, offset) in items)
        {
            if (objNum != prevNum + 1 || current is null)
            {
                current = [];
                runs.Add((objNum, current));
            }
            current.Add((objNum, generation, offset));
            prevNum = objNum;
        }

        return runs;
    }

    private static void WriteInt(PdfWriter w, long n)
    {
        Span<byte> buf = stackalloc byte[20];
        Utf8Formatter.TryFormat(n, buf, out var len);
        w.WriteAscii(buf[..len]);
    }

    private static void Write10Digits(PdfWriter w, long n)
    {
        if (n > 9_999_999_999L)
            throw new NotSupportedException(
                $"Byte offset {n} exceeds 9,999,999,999 — the classic xref table " +
                "cannot represent offsets beyond 10 digits.");
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

    private static void Write5Digits(PdfWriter w, int n)
    {
        // Unreachable through DssBuilder/ArchiveTimestampBuilder as things stand: both thread only
        // a PdfIndirectReference.Generation parsed off the source document, and the parser caps
        // that at the ISO 32000-2 §7.5.4 ceiling of 65535 (PdfObjectParser.ParseGenerationLenient) —
        // well inside the 5-digit field's own 99999 ceiling checked below. A document whose /Root
        // reference names a larger generation fails to open at all (PdfDocumentReader's constructor
        // requires /Root to resolve), so no caller ever reaches AppendRevision with one. Kept as a
        // guard rather than an assertion: AppendRevision's parameter is a public-facing (int, int,
        // PdfObject) triple, so a caller that builds one by hand — not through a parsed reference —
        // can still ask for a generation this field cannot hold, and a clear exception naming the
        // field's own limit beats a truncated or wrapped write.
        if (n is < 0 or > 99_999)
            throw new NotSupportedException(
                $"Generation {n} does not fit the 5-digit xref field (0–99999).");
        Span<byte> buf = stackalloc byte[5];
        buf.Fill((byte)'0');
        var tmp = n;
        for (var i = 4; i >= 0 && tmp > 0; i--)
        {
            buf[i] = (byte)('0' + tmp % 10);
            tmp /= 10;
        }
        w.WriteAscii(buf);
    }
}
