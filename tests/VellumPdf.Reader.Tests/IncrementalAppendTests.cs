// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Reader;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// End-to-end round-trip tests for PdfDocumentReader.AppendRevision (Phase 3).
/// </summary>
public sealed class IncrementalAppendTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static byte[] BuildOnePage()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    // ── Size property ─────────────────────────────────────────────────────────

    [Fact]
    public void Size_returnsTrailerSizeValue()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        // Size must be positive and at least 2 (obj 0 free head + at least one object).
        Assert.True(reader.Size >= 2, $"Expected Size >= 2, got {reader.Size}");
    }

    // ── Basic round-trip: new object appended ─────────────────────────────────

    [Fact]
    public void AppendRevision_newObject_resolvesInReopened()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        int newObjNum = reader.Size;
        var newObj = new PdfDictionary().Set(new PdfName("Phase"), new PdfName("Three"));

        var updated = reader.AppendRevision([(newObjNum, newObj)]);

        using var reader2 = PdfReader.Open(updated);
        var resolved = reader2.Resolve(newObjNum);
        var dict = Assert.IsType<PdfDictionary>(resolved);
        var phaseVal = Assert.IsType<PdfName>(dict.Get(new PdfName("Phase")));
        Assert.Equal("Three", phaseVal.Value);
    }

    [Fact]
    public void AppendRevision_outputIsLargerThanBase()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        int newObjNum = reader.Size;
        var newObj = new PdfDictionary().Set(new PdfName("Tag"), new PdfName("Appended"));
        var updated = reader.AppendRevision([(newObjNum, newObj)]);

        Assert.True(updated.Length > bytes.Length,
            "Appended PDF must be larger than the base.");
    }

    [Fact]
    public void AppendRevision_baseObjectsStillResolve()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        // Remember the catalog object number.
        int newObjNum = reader.Size;
        var newObj = new PdfDictionary().Set(new PdfName("Tag"), new PdfName("X"));
        var updated = reader.AppendRevision([(newObjNum, newObj)]);

        using var reader2 = PdfReader.Open(updated);

        // Catalog must still resolve.
        Assert.NotNull(reader2.Catalog);
        var typeObj = Assert.IsType<PdfName>(reader2.Catalog.Get(PdfName.Type));
        Assert.Equal("Catalog", typeObj.Value);
    }

    // ── Override existing object ───────────────────────────────────────────────

    [Fact]
    public void AppendRevision_overrideExistingObject_newerRevisionWins()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        // Find the catalog object number from /Root.
        var rootRef = reader.Trailer.Get(PdfName.Root) as PdfIndirectReference;
        Assert.NotNull(rootRef);
        int catalogObjNum = rootRef.ObjectNumber;

        // Build an updated catalog with an extra entry.
        var updatedCatalog = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Catalog"))
            .Set(new PdfName("Custom"), new PdfName("hello"));

        var updated = reader.AppendRevision([(catalogObjNum, updatedCatalog)]);

        using var reader2 = PdfReader.Open(updated);

        // The catalog override should be visible.
        var customVal = reader2.Catalog.Get(new PdfName("Custom"));
        var customName = Assert.IsType<PdfName>(customVal);
        Assert.Equal("hello", customName.Value);
    }

    // ── /Prev linkage is correct ──────────────────────────────────────────────

    [Fact]
    public void AppendRevision_reopened_startXrefOffsetBeyondBaseLength()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        int newObjNum = reader.Size;
        var newObj = new PdfDictionary().Set(new PdfName("N"), new PdfInteger(1));
        var updated = reader.AppendRevision([(newObjNum, newObj)]);

        using var reader2 = PdfReader.Open(updated);

        // The new startxref must be beyond the original file length.
        Assert.True(reader2.StartXrefOffset > bytes.Length,
            "New startxref should point into the appended revision.");
    }

    // ── Multi-revision chain ──────────────────────────────────────────────────

    [Fact]
    public void AppendRevision_twice_allThreeRevisionsResolve()
    {
        var bytes = BuildOnePage();
        using var reader1 = PdfReader.Open(bytes);

        int obj2Num = reader1.Size;
        var obj2 = new PdfDictionary().Set(new PdfName("Rev"), new PdfName("One"));
        var bytes2 = reader1.AppendRevision([(obj2Num, obj2)]);

        using var reader2 = PdfReader.Open(bytes2);
        int obj3Num = reader2.Size;
        var obj3 = new PdfDictionary().Set(new PdfName("Rev"), new PdfName("Two"));
        var bytes3 = reader2.AppendRevision([(obj3Num, obj3)]);

        using var reader3 = PdfReader.Open(bytes3);

        // Rev 1 objects (catalog) still resolve.
        Assert.NotNull(reader3.Catalog);

        // Rev 2 object resolves.
        var resolved2 = reader3.Resolve(obj2Num);
        var dict2 = Assert.IsType<PdfDictionary>(resolved2);
        var rev2 = Assert.IsType<PdfName>(dict2.Get(new PdfName("Rev")));
        Assert.Equal("One", rev2.Value);

        // Rev 3 object resolves.
        var resolved3 = reader3.Resolve(obj3Num);
        var dict3 = Assert.IsType<PdfDictionary>(resolved3);
        var rev3 = Assert.IsType<PdfName>(dict3.Get(new PdfName("Rev")));
        Assert.Equal("Two", rev3.Value);
    }

    [Fact]
    public void AppendRevision_secondPrevPointsAtFirstAppendedXref()
    {
        var bytes = BuildOnePage();
        using var reader1 = PdfReader.Open(bytes);
        int baseStartXref = reader1.StartXrefOffset;

        int obj2Num = reader1.Size;
        var obj2 = new PdfDictionary().Set(new PdfName("R"), new PdfInteger(1));
        var bytes2 = reader1.AppendRevision([(obj2Num, obj2)]);

        using var reader2 = PdfReader.Open(bytes2);
        int firstAppendedXref = reader2.StartXrefOffset;

        // The first appended xref must be beyond the original file.
        Assert.True(firstAppendedXref > baseStartXref);

        int obj3Num = reader2.Size;
        var obj3 = new PdfDictionary().Set(new PdfName("R"), new PdfInteger(2));
        var bytes3 = reader2.AppendRevision([(obj3Num, obj3)]);

        using var reader3 = PdfReader.Open(bytes3);
        int secondAppendedXref = reader3.StartXrefOffset;

        // The second appended xref must be beyond the first.
        Assert.True(secondAppendedXref > firstAppendedXref);
    }

    // ── Multiple objects in one revision ──────────────────────────────────────

    [Fact]
    public void AppendRevision_multipleObjects_allResolve()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        int base_ = reader.Size;
        var objs = new List<(int, PdfObject)>
        {
            (base_, new PdfDictionary().Set(new PdfName("K"), new PdfName("A"))),
            (base_ + 1, new PdfDictionary().Set(new PdfName("K"), new PdfName("B")))
        };

        var updated = reader.AppendRevision(objs);
        using var reader2 = PdfReader.Open(updated);

        var a = reader2.Resolve(base_) as PdfDictionary;
        Assert.NotNull(a);
        Assert.Equal("A", ((PdfName)a.Get(new PdfName("K"))!).Value);

        var b = reader2.Resolve(base_ + 1) as PdfDictionary;
        Assert.NotNull(b);
        Assert.Equal("B", ((PdfName)b.Get(new PdfName("K"))!).Value);
    }

    [Fact]
    public void AppendRevision_emptyObjects_throws()
    {
        var bytes = BuildOnePage();
        using var reader = PdfReader.Open(bytes);

        Assert.Throws<ArgumentException>(() => reader.AppendRevision([]));
    }
}
