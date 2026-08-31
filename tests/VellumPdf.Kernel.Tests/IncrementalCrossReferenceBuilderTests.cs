// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.IO;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Unit tests for <see cref="IncrementalCrossReferenceBuilder"/>'s shared section-writing core and
/// its full-document entry point, <c>WriteFullDocumentXrefAndTrailer</c> — added for #186's decrypted
/// full-document rewrite, which needs a classic xref table with no <c>/Prev</c> and a caller-supplied
/// trailer rather than one this builder assembles from a base document.
/// </summary>
public sealed class IncrementalCrossReferenceBuilderTests
{
    [Fact]
    public void WriteFullDocumentXrefAndTrailer_SparseRuns_GroupsContiguousSubsections()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        // Two runs: {1,2,3} contiguous, then a gap, then {7,8}.
        var written = new List<(int, int, long)>
        {
            (1, 0, 100),
            (2, 0, 200),
            (3, 0, 300),
            (7, 0, 700),
            (8, 0, 800),
        };
        var trailer = new PdfDictionary().Set(PdfName.Size, 9).Set(PdfName.Root, new PdfIndirectReference(1, 0));

        IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer);
        writer.Flush();

        var text = Encoding.ASCII.GetString(ms.ToArray());

        Assert.Contains("xref\n0 1\n0000000000 65535 f\r\n", text);
        Assert.Contains("1 3\n", text);
        Assert.Contains("0000000100 00000 n\r\n", text);
        Assert.Contains("0000000200 00000 n\r\n", text);
        Assert.Contains("0000000300 00000 n\r\n", text);
        Assert.Contains("7 2\n", text);
        Assert.Contains("0000000700 00000 n\r\n", text);
        Assert.Contains("0000000800 00000 n\r\n", text);
        Assert.Contains("trailer\n", text);
        Assert.DoesNotContain("/Prev", text);
        Assert.Contains("startxref\n", text);
        Assert.EndsWith("%%EOF\n", text);
    }

    [Fact]
    public void WriteFullDocumentXrefAndTrailer_NonZeroGenerations_RoundTripThroughXrefText()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var written = new List<(int, int, long)>
        {
            (1, 0, 50),
            (2, 3, 60), // non-zero generation — a freed number reused for a different object
        };
        var trailer = new PdfDictionary().Set(PdfName.Size, 3).Set(PdfName.Root, new PdfIndirectReference(1, 0));

        IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer);
        writer.Flush();

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("0000000060 00003 n\r\n", text);
    }

    [Fact]
    public void WriteFullDocumentXrefAndTrailer_WritesNoPrevOrXRefStm()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var written = new List<(int, int, long)> { (1, 0, 20) };
        var trailer = new PdfDictionary().Set(PdfName.Size, 2).Set(PdfName.Root, new PdfIndirectReference(1, 0));

        IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer);
        writer.Flush();

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.DoesNotContain("/Prev", text);
        Assert.DoesNotContain("/XRefStm", text);
    }

    [Fact]
    public void WriteFullDocumentXrefAndTrailer_WritesCallersTrailerVerbatim()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);

        var written = new List<(int, int, long)> { (1, 0, 20), (2, 0, 40) };
        var id = new PdfArray([new PdfHexString(new byte[16]), new PdfHexString(new byte[16])]);
        var trailer = new PdfDictionary()
            .Set(PdfName.Size, 3)
            .Set(PdfName.Root, new PdfIndirectReference(1, 0))
            .Set(PdfName.ID, id);

        IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer);
        writer.Flush();

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("/Size 3", text);
        Assert.Contains("/Root 1 0 R", text);
        Assert.Contains("/ID", text);
    }

    [Fact]
    public void WriteFullDocumentXrefAndTrailer_ReturnsXrefKeywordOffset()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        writer.WriteAscii("%PDF-1.7\n"u8);
        var preambleLength = writer.Position;

        var written = new List<(int, int, long)> { (1, 0, 20) };
        var trailer = new PdfDictionary().Set(PdfName.Size, 2).Set(PdfName.Root, new PdfIndirectReference(1, 0));

        var xrefOffset = IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer);

        Assert.Equal(preambleLength, xrefOffset);
    }

    [Fact]
    public void WriteFullDocumentXrefAndTrailer_EmptyObjectList_Throws()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        var trailer = new PdfDictionary().Set(PdfName.Size, 1).Set(PdfName.Root, new PdfIndirectReference(1, 0));

        Assert.Throws<ArgumentException>(() =>
            IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, [], trailer));
    }

    [Fact]
    public void WriteFullDocumentXrefAndTrailer_DuplicateObjectNumber_Throws()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        var written = new List<(int, int, long)> { (1, 0, 20), (1, 0, 40) };
        var trailer = new PdfDictionary().Set(PdfName.Size, 2).Set(PdfName.Root, new PdfIndirectReference(1, 0));

        Assert.Throws<ArgumentException>(() =>
            IncrementalCrossReferenceBuilder.WriteFullDocumentXrefAndTrailer(writer, written, trailer));
    }

    [Fact]
    public void WriteIncrementalXrefAndTrailer_StillWritesPrev_UnaffectedByRefactor()
    {
        using var ms = new MemoryStream();
        var writer = new PdfWriter(ms);
        var written = new List<(int, int, long)> { (5, 0, 123) };

        IncrementalCrossReferenceBuilder.WriteIncrementalXrefAndTrailer(
            writer, written, baseSize: 5, catalogRef: new PdfIndirectReference(1, 0),
            prevXrefOffset: 999, documentId: null);
        writer.Flush();

        var text = Encoding.ASCII.GetString(ms.ToArray());
        Assert.Contains("/Prev 999", text);
    }
}
