// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Canvas;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.IO;
using VellumPdf.IO.Linearization;
using VellumPdf.Reader;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// Tests for the Step 1 linearization machinery: PdfObjectRemapper, LinearizedLayoutPlanner,
/// the linearized write branch, and a reader round-trip.
/// </summary>
public sealed class LinearizationTests
{
    private static readonly DateTimeOffset PinnedTime = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
    private static readonly byte[] PinnedId = Convert.FromHexString("000102030405060708090A0B0C0D0E0F");

    // ── PdfObjectRemapper ────────────────────────────────────────────────────

    [Fact]
    public void Remapper_indirectRef_isMapped()
    {
        var map = new Dictionary<int, int> { [3] = 7 };
        var result = PdfObjectRemapper.Remap(new PdfIndirectReference(3), map);
        Assert.Equal(7, ((PdfIndirectReference)result).ObjectNumber);
    }

    [Fact]
    public void Remapper_unmappedRef_isUnchanged()
    {
        var map = new Dictionary<int, int> { [3] = 7 };
        var result = PdfObjectRemapper.Remap(new PdfIndirectReference(5), map);
        Assert.Equal(5, ((PdfIndirectReference)result).ObjectNumber);
    }

    [Fact]
    public void Remapper_scalar_returnedAsIs()
    {
        var map = new Dictionary<int, int> { [1] = 2 };
        var integer = new PdfInteger(42);
        Assert.Same(integer, PdfObjectRemapper.Remap(integer, map));
    }

    [Fact]
    public void Remapper_dictionary_remapsNestedRef()
    {
        var map = new Dictionary<int, int> { [1] = 99 };
        var dict = new PdfDictionary().Set(new PdfName("A"), new PdfIndirectReference(1));
        var result = (PdfDictionary)PdfObjectRemapper.Remap(dict, map);
        var val = result.Get(new PdfName("A")) as PdfIndirectReference;
        Assert.NotNull(val);
        Assert.Equal(99, val!.ObjectNumber);
    }

    [Fact]
    public void Remapper_array_remapsNestedRef()
    {
        var map = new Dictionary<int, int> { [2] = 10 };
        var arr = new PdfArray();
        arr.Add(new PdfIndirectReference(2));
        arr.Add(new PdfInteger(5));
        var result = (PdfArray)PdfObjectRemapper.Remap(arr, map);
        Assert.Equal(10, ((PdfIndirectReference)result[0]).ObjectNumber);
        Assert.Equal(5, ((PdfInteger)result[1]).Value);
    }

    [Fact]
    public void Remapper_stream_dictionaryRemappedInPlace()
    {
        var map = new Dictionary<int, int> { [4] = 12 };
        var stream = new PdfStream([1, 2, 3]);
        stream.Dictionary.Set(new PdfName("SMask"), new PdfIndirectReference(4));
        var result = (PdfStream)PdfObjectRemapper.Remap(stream, map);
        // In-place mutation: result IS the same stream.
        Assert.Same(stream, result);
        var remapped = result.Dictionary.Get(new PdfName("SMask")) as PdfIndirectReference;
        Assert.NotNull(remapped);
        Assert.Equal(12, remapped!.ObjectNumber);
    }

    // ── LinearizedLayoutPlanner ──────────────────────────────────────────────

    [Fact]
    public void Planner_singlePage_allObjectsAssignedContiguousNumbers()
    {
        // Build a minimal registry: one content stream + one page dict + page tree + info + catalog.
        var registry = new PdfObjectRegistry();
        var contentRef = registry.Reserve();
        var pageDictRef = registry.Reserve();
        var pageTreeRef = registry.Reserve();
        var infoRef = registry.Reserve();
        var catalogRef = registry.Reserve();

        registry.SetValue(contentRef, new PdfStream([]));
        registry.SetValue(pageDictRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page)
            .Set(PdfName.Contents, contentRef));
        registry.SetValue(pageTreeRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, new PdfArray([pageDictRef]))
            .Set(PdfName.Count, 1L));
        registry.SetValue(infoRef, new PdfDictionary());
        registry.SetValue(catalogRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef));

        var metaStream = new PdfStream([]);
        var metadataRef = registry.Add(metaStream);

        var layout = LinearizedLayoutPlanner.Plan(
            registry, catalogRef, pageTreeRef,
            [pageDictRef], [contentRef], infoRef, metadataRef);

        // Every original object number must be in the map.
        Assert.Contains(contentRef.ObjectNumber, layout.OldToNew);
        Assert.Contains(pageDictRef.ObjectNumber, layout.OldToNew);
        Assert.Contains(pageTreeRef.ObjectNumber, layout.OldToNew);
        Assert.Contains(infoRef.ObjectNumber, layout.OldToNew);
        Assert.Contains(catalogRef.ObjectNumber, layout.OldToNew);

        // Total size must account for all objects + lin dict + hint stream + free head.
        var expectedBody = layout.RestObjects.Count + layout.Part4Objects.Count
            + layout.Part6Objects.Count + 2; // +2 for lin dict + hint
        Assert.Equal(expectedBody + 1, layout.TotalSize); // +1 for object 0

        // The hint stream follows the lin dict and the part-4 (document-level) objects.
        Assert.Equal(layout.HintStreamObjNum, layout.LinDictObjNum + 1 + layout.Part4Objects.Count);
    }

    [Fact]
    public void Planner_twoPages_firstPageObjectsIncludeCatalogAndFirstPageDict()
    {
        var registry = new PdfObjectRegistry();
        var contentRef1 = registry.Reserve();
        var contentRef2 = registry.Reserve();
        var pageDictRef1 = registry.Reserve();
        var pageDictRef2 = registry.Reserve();
        var pageTreeRef = registry.Reserve();
        var infoRef = registry.Reserve();
        var catalogRef = registry.Reserve();

        registry.SetValue(contentRef1, new PdfStream([]));
        registry.SetValue(contentRef2, new PdfStream([]));
        registry.SetValue(pageDictRef1, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page)
            .Set(PdfName.Contents, contentRef1));
        registry.SetValue(pageDictRef2, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Page)
            .Set(PdfName.Contents, contentRef2));
        registry.SetValue(pageTreeRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, new PdfArray([pageDictRef1, pageDictRef2]))
            .Set(PdfName.Count, 2L));
        registry.SetValue(infoRef, new PdfDictionary());
        registry.SetValue(catalogRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef));

        var metadataRef = registry.Add(new PdfStream([]));

        var layout = LinearizedLayoutPlanner.Plan(
            registry, catalogRef, pageTreeRef,
            [pageDictRef1, pageDictRef2], [contentRef1, contentRef2], infoRef, metadataRef);

        // Catalog (document level) and the first-page dict (part 6) are both in the first-page section.
        var fpNums = layout.Part4Objects.Concat(layout.Part6Objects).Select(x => x.NewObjNum).ToHashSet();
        Assert.Contains(layout.CatalogObjNum, fpNums);
        Assert.Contains(layout.FirstPageObjNum, fpNums);

        // Page 2's content stream must be in the rest section.
        var restNums = layout.RestObjects.Select(x => x.NewObjNum).ToHashSet();
        var page2ContentNewNum = layout.OldToNew[contentRef2.ObjectNumber];
        Assert.Contains(page2ContentNewNum, restNums);
    }

    [Fact]
    public void Planner_pagesWithParentBacklink_doNotCollapsePartition()
    {
        // Real page dicts carry /Parent → page tree, and the page tree /Kids lists every
        // page. A naive BFS from page 0 would follow /Parent into the page tree and fan out
        // into every other page, pulling the whole document into the first-page section.
        // The planner must treat other pages' dicts as boundaries so later pages' exclusive
        // objects land in the rest section.
        var registry = new PdfObjectRegistry();
        var contentRefs = new PdfIndirectReference[3];
        var pageDictRefs = new PdfIndirectReference[3];
        for (var i = 0; i < 3; i++) contentRefs[i] = registry.Reserve();
        for (var i = 0; i < 3; i++) pageDictRefs[i] = registry.Reserve();
        var pageTreeRef = registry.Reserve();
        var infoRef = registry.Reserve();
        var catalogRef = registry.Reserve();

        for (var i = 0; i < 3; i++)
        {
            registry.SetValue(contentRefs[i], new PdfStream([]));
            registry.SetValue(pageDictRefs[i], new PdfDictionary()
                .Set(PdfName.Type, PdfName.Page)
                .Set(PdfName.Parent, pageTreeRef)      // the backlink that used to collapse the partition
                .Set(PdfName.Contents, contentRefs[i]));
        }
        registry.SetValue(pageTreeRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Pages)
            .Set(PdfName.Kids, new PdfArray(pageDictRefs.Cast<PdfObject>()))
            .Set(PdfName.Count, 3L));
        registry.SetValue(infoRef, new PdfDictionary());
        registry.SetValue(catalogRef, new PdfDictionary()
            .Set(PdfName.Type, PdfName.Catalog)
            .Set(PdfName.Pages, pageTreeRef));
        var metadataRef = registry.Add(new PdfStream([]));

        var layout = LinearizedLayoutPlanner.Plan(
            registry, catalogRef, pageTreeRef, pageDictRefs, contentRefs, infoRef, metadataRef);

        var fpNums = layout.Part4Objects.Concat(layout.Part6Objects).Select(x => x.NewObjNum).ToHashSet();
        var restNums = layout.RestObjects.Select(x => x.NewObjNum).ToHashSet();

        // Pages 2 and 3 (dicts + their exclusive content) must be in the rest section, not first-page.
        foreach (var i in new[] { 1, 2 })
        {
            var pageDictNew = layout.OldToNew[pageDictRefs[i].ObjectNumber];
            var contentNew = layout.OldToNew[contentRefs[i].ObjectNumber];
            Assert.Contains(pageDictNew, restNums);
            Assert.Contains(contentNew, restNums);
            Assert.DoesNotContain(pageDictNew, fpNums);
            Assert.DoesNotContain(contentNew, fpNums);
        }

        // The rest section is non-empty (the partition did not collapse).
        Assert.NotEmpty(layout.RestObjects);
    }

    // ── Linearized write + reader round-trip ─────────────────────────────────

    [Fact]
    public void LinearizedSave_singlePage_readsBackCorrectly()
    {
        var bytes = BuildLinearizedDoc(pageCount: 1);
        using var reader = PdfReader.Open(bytes);

        Assert.NotNull(reader.Catalog);
        var pages = reader.Catalog.Get(PdfName.Pages);
        Assert.NotNull(pages);
    }

    [Fact]
    public void LinearizedSave_multiPage_catalogAndPagesReadable()
    {
        var bytes = BuildLinearizedDoc(pageCount: 3);
        using var reader = PdfReader.Open(bytes);

        Assert.NotNull(reader.Catalog);
        var pagesRef = reader.Catalog.Get(PdfName.Pages) as PdfIndirectReference;
        Assert.NotNull(pagesRef);

        var pagesDict = reader.Resolve(pagesRef!) as PdfDictionary;
        Assert.NotNull(pagesDict);
        var count = pagesDict!.Get(PdfName.Count) as PdfInteger;
        Assert.Equal(3, (int)count!.Value);
    }

    [Fact]
    public void LinearizedSave_withPins_isByteIdentical()
    {
        var b1 = BuildLinearizedDoc(pageCount: 2);
        var b2 = BuildLinearizedDoc(pageCount: 2);
        Assert.True(b1.SequenceEqual(b2), "Two linearized builds with identical pins must be byte-identical.");
    }

    [Fact]
    public void LinearizedSave_defaultLinearizeFalse_doesNotChangeOutput()
    {
        static byte[] BuildDoc(bool linearize)
        {
            using var doc = new PdfDocument { Timestamp = PinnedTime, DocumentId = PinnedId, Linearize = linearize };
            doc.AddPage(PageSize.A4);
            var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        var classic = BuildDoc(false);
        var linearized = BuildDoc(true);

        // The classic path must be byte-stable (existing tests cover this).
        Assert.Equal(classic, BuildDoc(false));
        // The linearized output is different from classic (reordered objects).
        // They should not be equal (except in degenerate edge cases).
        // This assertion confirms the linearized path produces different bytes.
        Assert.NotEqual(classic, linearized);
    }

    private static byte[] BuildLinearizedDoc(int pageCount)
    {
        using var doc = new PdfDocument
        {
            Timestamp = PinnedTime,
            DocumentId = PinnedId,
            Linearize = true,
        };
        doc.Info.Title = $"LinearizedTest-{pageCount}pages";

        for (var i = 0; i < pageCount; i++)
        {
            var page = doc.AddPage(PageSize.A4);
            var canvas = new PdfCanvas(page);
            var font = doc.UseFont(Standard14.Helvetica);
            canvas.BeginText().SetFont(font, 12)
                .SetTextMatrix(1, 0, 0, 1, 72, 720)
                .ShowText($"Page {i + 1}")
                .EndText();
            canvas.Finish();
        }

        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }
}
