// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
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

    /// <summary>
    /// A hand-built (not VellumPdf-written — this library's own writer never emits a nonzero
    /// generation) PDF whose catalog sits at generation 1 (<c>/Root 1 1 R</c>) and whose
    /// <c>/Extra</c> entry (object 5, <c>&lt;&lt; /Marker /Old &gt;&gt;</c>) sits at generation 2.
    /// </summary>
    private static byte[] BuildNonZeroGenerationBasePdf()
    {
        var ms = new MemoryStream();
        void Write(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        Write("%PDF-1.4\n");
        var obj1Offset = (int)ms.Position;
        Write("1 1 obj\n<< /Type /Catalog /Pages 2 0 R /Extra 5 2 R >>\nendobj\n");
        var obj2Offset = (int)ms.Position;
        Write("2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n");
        var obj5Offset = (int)ms.Position;
        Write("5 2 obj\n<< /Marker /Old >>\nendobj\n");

        var xrefOffset = (int)ms.Position;
        Write("xref\n");
        Write("0 3\n");
        Write($"{0:D10} 65535 f \n");
        Write($"{obj1Offset:D10} 00001 n \n");
        Write($"{obj2Offset:D10} 00000 n \n");
        Write("5 1\n");
        Write($"{obj5Offset:D10} 00002 n \n");
        Write("trailer\n<< /Size 6 /Root 1 1 R >>\n");
        Write($"startxref\n{xrefOffset}\n%%EOF\n");

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

        var updated = reader.AppendRevision([(newObjNum, 0, newObj)]);

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
        var updated = reader.AppendRevision([(newObjNum, 0, newObj)]);

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
        var updated = reader.AppendRevision([(newObjNum, 0, newObj)]);

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

        var updated = reader.AppendRevision([(catalogObjNum, 0, updatedCatalog)]);

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
        var updated = reader.AppendRevision([(newObjNum, 0, newObj)]);

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
        var bytes2 = reader1.AppendRevision([(obj2Num, 0, obj2)]);

        using var reader2 = PdfReader.Open(bytes2);
        int obj3Num = reader2.Size;
        var obj3 = new PdfDictionary().Set(new PdfName("Rev"), new PdfName("Two"));
        var bytes3 = reader2.AppendRevision([(obj3Num, 0, obj3)]);

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
        var bytes2 = reader1.AppendRevision([(obj2Num, 0, obj2)]);

        using var reader2 = PdfReader.Open(bytes2);
        int firstAppendedXref = reader2.StartXrefOffset;

        // The first appended xref must be beyond the original file.
        Assert.True(firstAppendedXref > baseStartXref);

        int obj3Num = reader2.Size;
        var obj3 = new PdfDictionary().Set(new PdfName("R"), new PdfInteger(2));
        var bytes3 = reader2.AppendRevision([(obj3Num, 0, obj3)]);

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
        var objs = new List<(int, int, PdfObject)>
        {
            (base_, 0, new PdfDictionary().Set(new PdfName("K"), new PdfName("A"))),
            (base_ + 1, 0, new PdfDictionary().Set(new PdfName("K"), new PdfName("B")))
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

    // ── Nonzero-generation objects (#121 C1) ────────────────────────────────────

    [Fact]
    public void AppendRevision_nonZeroGenerationCatalogAndObject_roundTripsAndReopens()
    {
        // #121 C1: a document whose /Root -- and another object it references -- sit at a
        // nonzero generation must still be appendable and reopenable. This is exactly the path
        // DssBuilder and ArchiveTimestampBuilder use to add LTV/archive-timestamp material: both
        // rewrite the existing catalog, and ArchiveTimestampBuilder also rewrites the first page.
        // Before this fix, reopening the result of appending to a gen-1 /Root threw "Malformed
        // PDF: /Root does not resolve to a dictionary"; rewriting a non-catalog object at the
        // wrong generation failed silently instead (Resolve returned null, no exception).
        var bytes = BuildNonZeroGenerationBasePdf();
        using var reader = PdfReader.Open(bytes);

        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        Assert.Equal(1, rootRef.Generation);

        var extraRef = (PdfIndirectReference)reader.Catalog.Get(new PdfName("Extra"))!;
        Assert.Equal(2, extraRef.Generation);

        var newCatalog = new PdfDictionary()
            .Set(PdfName.Type, new PdfName("Catalog"))
            .Set(new PdfName("Pages"), reader.Catalog.Get(new PdfName("Pages"))!)
            .Set(new PdfName("Extra"), reader.Catalog.Get(new PdfName("Extra"))!)
            .Set(new PdfName("Touched"), new PdfName("Yes"));

        var newExtra = new PdfDictionary().Set(new PdfName("Marker"), new PdfName("New"));

        // Both rewritten objects keep their existing generation -- neither is a freed number
        // being reused, so neither generation advances (ISO 32000-2 §7.5.4).
        var updated = reader.AppendRevision([
            (rootRef.ObjectNumber, rootRef.Generation, newCatalog),
            (extraRef.ObjectNumber, extraRef.Generation, newExtra),
        ]);

        // The reopen itself is the C1 acceptance criterion.
        using var reader2 = PdfReader.Open(updated);

        var touched = reader2.Catalog.Get(new PdfName("Touched"));
        Assert.Equal("Yes", ((PdfName)touched!).Value);

        // The non-catalog object must resolve too -- silently vanishing was the other half of C1.
        var extraRef2 = (PdfIndirectReference)reader2.Catalog.Get(new PdfName("Extra"))!;
        var resolvedExtra = reader2.ResolveValue(extraRef2) as PdfDictionary;
        Assert.NotNull(resolvedExtra);
        var marker = (PdfName)resolvedExtra!.Get(new PdfName("Marker"))!;
        Assert.Equal("New", marker.Value);
    }

    [Fact]
    public void AppendRevision_wrongGenerationForRewrittenRoot_throwsBeforeWriting()
    {
        // AppendRevision's defensive consistency check: rewriting /Root's object number at the
        // wrong generation must fail loudly at append time, not silently produce a file whose
        // trailer, object header, and xref entry disagree with each other (see C1 above).
        var bytes = BuildNonZeroGenerationBasePdf();
        using var reader = PdfReader.Open(bytes);

        var rootRef = (PdfIndirectReference)reader.Trailer.Get(PdfName.Root)!;
        var newCatalog = new PdfDictionary().Set(PdfName.Type, new PdfName("Catalog"));

        Assert.Throws<ArgumentException>(() =>
            reader.AppendRevision([(rootRef.ObjectNumber, 0, newCatalog)]));
    }
}
