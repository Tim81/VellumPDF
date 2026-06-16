// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>Unit tests for IncrementalCrossReferenceBuilder and the PdfWriter position seed.</summary>
public sealed class IncrementalXrefTests
{
    // ── PdfWriter initial-position seed ──────────────────────────────────────

    [Fact]
    public void PdfWriter_initialPosition_zero_matchesDefault()
    {
        var ms = new MemoryStream();
        var w1 = new PdfWriter(ms);
        var w2 = new PdfWriter(ms, 0L);
        Assert.Equal(0L, w1.Position);
        Assert.Equal(0L, w2.Position);
    }

    [Fact]
    public void PdfWriter_initialPosition_seed_reportsAbsoluteOffsets()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 1000L);
        Assert.Equal(1000L, writer.Position);
        writer.WriteAscii("hello"u8);
        Assert.Equal(1005L, writer.Position);
    }

    [Fact]
    public void PdfWriter_initialPosition_negative_throws()
    {
        var ms = new MemoryStream();
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfWriter(ms, -1L));
    }

    // ── IncrementalCrossReferenceBuilder: single contiguous run ──────────────

    [Fact]
    public void IncrementalXref_singleObject_correctSubsectionAndEntry()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);

        var written = new List<(int, long)> { (3, 100L) };
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 4,
            catalogRef: catalogRef,
            prevXrefOffset: 50L,
            documentId: null);

        var text = Encoding.ASCII.GetString(ms.ToArray());

        // Free-list head subsection
        Assert.Contains("0 1\n", text);
        Assert.Contains("0000000000 65535 f\r\n", text);

        // Subsection for obj 3
        Assert.Contains("3 1\n", text);
        Assert.Contains("0000000100 00000 n\r\n", text);

        // Trailer /Prev
        Assert.Contains("/Prev 50", text);
        Assert.Contains("/Size 4", text);
        Assert.Contains("/Root 1 0 R", text);
        Assert.Contains("startxref", text);
        Assert.Contains("%%EOF", text);
    }

    [Fact]
    public void IncrementalXref_contiguousRun_singleSubsection()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);

        // Objects 3 and 4 are contiguous — must produce a single "3 2" subsection.
        var written = new List<(int, long)> { (3, 200L), (4, 350L) };
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 5,
            catalogRef: catalogRef,
            prevXrefOffset: 80L,
            documentId: null);

        var text = Encoding.ASCII.GetString(ms.ToArray());

        // Single contiguous subsection header "3 2"
        Assert.Contains("3 2\n", text);
        Assert.Contains("0000000200 00000 n\r\n", text);
        Assert.Contains("0000000350 00000 n\r\n", text);

        // Must NOT have a "4 1" header (that would mean non-contiguous grouping)
        Assert.DoesNotContain("4 1\n", text);
    }

    [Fact]
    public void IncrementalXref_nonContiguousObjects_multipleSubsections()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);

        // Objects 3, 4 (contiguous run) and 7, 8 (separate contiguous run)
        var written = new List<(int, long)>
        {
            (3, 100L), (4, 200L), (7, 500L), (8, 600L)
        };
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 9,
            catalogRef: catalogRef,
            prevXrefOffset: 50L,
            documentId: null);

        var text = Encoding.ASCII.GetString(ms.ToArray());

        // Two data subsections (plus the free-list head "0 1")
        Assert.Contains("3 2\n", text);
        Assert.Contains("7 2\n", text);
        Assert.Contains("0000000100 00000 n\r\n", text);
        Assert.Contains("0000000200 00000 n\r\n", text);
        Assert.Contains("0000000500 00000 n\r\n", text);
        Assert.Contains("0000000600 00000 n\r\n", text);
    }

    [Fact]
    public void IncrementalXref_size_isMaxOfBaseSizeAndMaxObjPlusOne()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);

        // maxObjNum = 10 → newSize = max(5, 11) = 11
        var written = new List<(int, long)> { (10, 999L) };
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 5,
            catalogRef: catalogRef,
            prevXrefOffset: 10L,
            documentId: null);

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("/Size 11", text);
    }

    [Fact]
    public void IncrementalXref_size_usesBaseSizeWhenLarger()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);

        // baseSize = 50, maxObjNum = 3 → newSize = max(50, 4) = 50
        var written = new List<(int, long)> { (3, 100L) };
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 50,
            catalogRef: catalogRef,
            prevXrefOffset: 10L,
            documentId: null);

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("/Size 50", text);
    }

    [Fact]
    public void IncrementalXref_documentId_carriedVerbatim()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);
        var idBytes = new byte[16];
        for (var i = 0; i < 16; i++) idBytes[i] = (byte)(i + 1);
        var idArr = new PdfArray([new PdfHexString(idBytes), new PdfHexString(idBytes)]);

        var written = new List<(int, long)> { (2, 100L) };
        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 3,
            catalogRef: catalogRef,
            prevXrefOffset: 20L,
            documentId: idArr);

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("/ID", text);
    }

    [Fact]
    public void IncrementalXref_emptyList_throws()
    {
        var ms = new MemoryStream();
        var writer = new PdfWriter(ms, 0L);
        var catalogRef = new PdfIndirectReference(1);

        Assert.Throws<ArgumentException>(() =>
            IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
                writer,
                [],
                baseSize: 2,
                catalogRef: catalogRef,
                prevXrefOffset: 10L,
                documentId: null));
    }

    [Fact]
    public void IncrementalXref_xrefOffset_returned_correctlySeeded()
    {
        // Seed the writer at a non-zero offset; the returned xrefOffset must equal
        // the writer's position at the start of the xref keyword.
        var baseOffset = 500L;
        var ms = new MemoryStream();
        // Pre-fill 500 bytes so the MemoryStream position matches.
        ms.Write(new byte[500]);
        var writer = new PdfWriter(ms, baseOffset);
        var catalogRef = new PdfIndirectReference(1);

        var written = new List<(int, long)> { (2, 100L) };

        // Write a small "object" manually to advance position.
        writer.WriteAscii("dummy"u8); // position = 505
        var posBeforeXref = writer.Position;

        var returnedXrefOffset = IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer,
            written,
            baseSize: 3,
            catalogRef: catalogRef,
            prevXrefOffset: 10L,
            documentId: null);

        Assert.Equal(posBeforeXref, returnedXrefOffset);
    }
}
