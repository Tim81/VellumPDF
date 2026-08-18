// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

public sealed class RevisionAndEndOffsetTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteStr(MemoryStream ms, string s) =>
        ms.Write(Encoding.ASCII.GetBytes(s));

    /// <summary>
    /// Minimal single-revision PDF with a plain classic xref.
    /// Returns the bytes and the byte offsets of each object by 1-based index.
    /// </summary>
    private static (byte[] Bytes, int[] ObjectOffsets, int XrefOffset) BuildSimplePdf()
    {
        var ms = new MemoryStream();
        void W(string s) => WriteStr(ms, s);

        W("%PDF-1.4\n");
        var offsets = new int[5];

        offsets[1] = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        offsets[2] = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");

        offsets[3] = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        offsets[4] = (int)ms.Position;
        W("4 0 obj\n<< /Custom /Value >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 5\n");
        W($"{0:D10} 65535 f \n");
        W($"{offsets[1]:D10} 00000 n \n");
        W($"{offsets[2]:D10} 00000 n \n");
        W($"{offsets[3]:D10} 00000 n \n");
        W($"{offsets[4]:D10} 00000 n \n");
        W("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return (ms.ToArray(), offsets, xrefOffset);
    }

    // ── UncompressedObjectEndOffset ───────────────────────────────────────────

    [Fact]
    public void EndOffset_normalObject_returnsPositionAfterEndobj()
    {
        // Capture exact position of "endobj" terminator for object 4.
        var ms = new MemoryStream();
        void W(string s) => WriteStr(ms, s);

        W("%PDF-1.4\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Hello /World >>\n");
        var endobjStart = (int)ms.Position;
        W("endobj\n"); // "endobj" is 6 bytes; position just after those 6 bytes is what we expect
        var posAfterEndobj3 = endobjStart + 6; // exclusive end: just past the 'j' in "endobj"

        var xrefOffset = (int)ms.Position;
        W("xref\n0 4\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W($"{o3:D10} 00000 n \n");
        W("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray());
        var result = reader.UncompressedObjectEndOffset(3);

        Assert.NotNull(result);
        Assert.Equal(posAfterEndobj3, result!.Value);
    }

    [Fact]
    public void EndOffset_absentObject_returnsNull()
    {
        var (bytes, _, _) = BuildSimplePdf();
        using var reader = PdfReader.Open(bytes);

        var result = reader.UncompressedObjectEndOffset(99);

        Assert.Null(result);
    }

    [Fact]
    public void EndOffset_outOfRangeOffset_returnsNull()
    {
        // Build a PDF where object 4 is present in the xref but its offset points beyond
        // the end of the file. The xref table lies, so PdfDocumentReader must guard against
        // the arithmetic overflow / out-of-bounds access and return null rather than throwing.
        var ms = new MemoryStream();
        void W(string s) => WriteStr(ms, s);

        W("%PDF-1.4\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        var o3 = (int)ms.Position;
        W("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] >>\nendobj\n");

        var fileSize = (int)ms.Position; // capture before writing xref

        // Object 4 is listed in the xref with an offset beyond the file — never actually written.
        const int bogusOffset = 999999999;

        var xrefOffset = (int)ms.Position;
        W("xref\n0 5\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W($"{o3:D10} 00000 n \n");
        W($"{bogusOffset:D10} 00000 n \n"); // obj 4 at a far-beyond-end offset
        W("trailer\n<< /Size 5 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        var pdfBytes = ms.ToArray();
        using var reader = PdfReader.Open(pdfBytes);

        // Object 4's xref entry exists (Uncompressed kind) but its offset is beyond file length.
        // UncompressedObjectEndOffset must return null instead of throwing or reading garbage.
        var result = reader.UncompressedObjectEndOffset(4);

        Assert.Null(result);
    }

    [Fact]
    public void EndOffset_objectStreamMember_returnsNull()
    {
        // Build an xref-stream PDF with object-stream members (type-2 entries).
        using var doc = new PdfDocument();
        doc.UseObjectStreams = true;
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        var bytes = ms.ToArray();

        using var reader = PdfReader.Open(bytes);

        // Find a type-2 (object-stream) object by scanning for one that returns null.
        // We know object stream docs have type-2 entries; UncompressedObjectOffset returns null for them.
        int? objStmMember = null;
        foreach (var objNum in reader.ObjectNumbers)
        {
            if (reader.UncompressedObjectOffset(objNum) is null)
            {
                objStmMember = objNum;
                break;
            }
        }

        Assert.NotNull(objStmMember);
        var result = reader.UncompressedObjectEndOffset(objStmMember!.Value);
        Assert.Null(result);
    }

    [Fact]
    public void EndOffset_truncatedWindow_returnsNull()
    {
        // Object 3's endobj is beyond the scan window — use a tiny maxScanBytes.
        var ms = new MemoryStream();
        void W(string s) => WriteStr(ms, s);

        W("%PDF-1.4\n");
        var o1 = (int)ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var o2 = (int)ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var o3 = (int)ms.Position;
        // Object body is long enough that a small maxScanBytes won't reach endobj.
        W("3 0 obj\n<< /LongKey /LongValue >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        W("xref\n0 4\n");
        W($"{0:D10} 65535 f \n");
        W($"{o1:D10} 00000 n \n");
        W($"{o2:D10} 00000 n \n");
        W($"{o3:D10} 00000 n \n");
        W("trailer\n<< /Size 4 /Root 1 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        using var reader = PdfReader.Open(ms.ToArray());

        // Scan only 5 bytes from object 3's start — not enough to reach "endobj".
        var result = reader.UncompressedObjectEndOffset(3, maxScanBytes: 5);
        Assert.Null(result);
    }

    // ── Revisions ─────────────────────────────────────────────────────────────

    [Fact]
    public void Revisions_singleRevision_hasOneEntryMatchingXrefOffset()
    {
        var (bytes, _, xrefOffset) = BuildSimplePdf();
        using var reader = PdfReader.Open(bytes);

        Assert.Single(reader.Revisions);
        Assert.Equal(xrefOffset, reader.Revisions[0].XrefOffset);
    }

    [Fact]
    public void Revisions_twoRevisions_oldestFirstWithDistinctIncreasingOffsets()
    {
        // Build a single-page PDF, then append a revision and re-open.
        using var doc = new PdfDocument();
        doc.AddPage();
        var baseMs = new MemoryStream();
        doc.Save(baseMs);
        var baseBytes = baseMs.ToArray();

        using var reader1 = PdfReader.Open(baseBytes);
        int baseXrefOffset = reader1.StartXrefOffset;

        // Append a new object in a second revision.
        int newObjNum = reader1.Size;
        var newObj = new PdfDictionary().Set(new PdfName("Rev"), new PdfName("Two"));
        var updatedBytes = reader1.AppendRevision([(newObjNum, 0, newObj)]);

        using var reader2 = PdfReader.Open(updatedBytes);

        Assert.Equal(2, reader2.Revisions.Count);

        // Oldest-first: revision 0 is the base, revision 1 is the appended one.
        var oldest = reader2.Revisions[0];
        var newest = reader2.Revisions[1];

        Assert.Equal(baseXrefOffset, oldest.XrefOffset);
        Assert.True(newest.XrefOffset > oldest.XrefOffset,
            $"Expected newest xref offset ({newest.XrefOffset}) > oldest ({oldest.XrefOffset})");
    }
}
