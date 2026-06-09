// Copyright 2026 Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Annotations;
using VellumPdf.Document;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Pinning <see cref="PdfDocument.Timestamp"/> and <see cref="PdfDocument.DocumentId"/> must make
/// identical content produce byte-identical output — the foundation for golden snapshots and
/// reproducible builds (issue #5).
/// </summary>
public sealed class DeterminismTests
{
    private static readonly DateTimeOffset PinnedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    private static byte[] BuildSample(PdfConformance conformance)
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
            Conformance = conformance,
        };
        doc.Info.Title = "Determinism";
        doc.Info.Author = "VellumPdf";

        var page = doc.AddPage(PageSize.A4);
        doc.AddOutlineEntry(new PdfOutlineEntry { Title = "Section 1", DestPage = page, Level = 0 });

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Save_withPinnedTimestampAndId_isByteIdentical()
    {
        Assert.Equal(BuildSample(PdfConformance.None), BuildSample(PdfConformance.None));
    }

    [Fact]
    public void Save_pdfA2b_withPins_isByteIdentical()
    {
        // Exercises the timestamp-dependent XMP metadata + output-intent path.
        Assert.Equal(BuildSample(PdfConformance.PdfA2b), BuildSample(PdfConformance.PdfA2b));
    }

    [Fact]
    public void DocumentId_pin_changesOutputVsComputedId_andIsStable()
    {
        static byte[] Build(byte[]? id)
        {
            using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = id };
            doc.Info.Title = "Same";
            doc.AddPage(PageSize.A4);
            var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        // Same content + timestamp: the only difference is the pinned vs content-derived /ID.
        Assert.NotEqual(Build(PinnedId), Build(null));
        // The pinned variant is itself byte-stable.
        Assert.Equal(Build(PinnedId), Build(PinnedId));
    }

    [Fact]
    public void NoDocumentIdPin_stillDeterministic_whenTimestampPinned()
    {
        // Without an explicit DocumentId, the content+timestamp-derived /ID must still be stable.
        static byte[] Build()
        {
            using var doc = new PdfDocument { Timestamp = PinnedTime };
            doc.Info.Title = "Stable";
            doc.AddPage(PageSize.A4);
            var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        Assert.Equal(Build(), Build());
    }

    [Fact]
    public void PrepareForSigning_withPins_isByteIdentical()
    {
        // The signing-placeholder document (before the signature is patched in) must be
        // reproducible with pins — this covers the DocumentId wiring on the signing path.
        static byte[] Build()
        {
            using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId };
            doc.Info.Title = "Signable";
            doc.AddPage(PageSize.A4);
            return doc.PrepareForSigning(new SignaturePlaceholderOptions());
        }

        Assert.Equal(Build(), Build());
    }
}
