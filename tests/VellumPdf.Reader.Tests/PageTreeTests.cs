// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Reflection;
using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;

namespace VellumPdf.Reader.Tests;

/// <summary>
/// Exercises the page-tree walk (#98 part 2): <see cref="PdfDocumentReader.PageCount"/>,
/// <see cref="PdfDocumentReader.Pages"/>, and <see cref="PdfDocumentReader.GetPage(int)"/> against
/// ISO 32000-2 §7.7.3's own tree shape, its inheritance rule (§7.7.3.4), and the walker's caps.
/// Most fixtures are hand-built byte strings rather than <see cref="PdfDocument"/> output: the
/// writer only ever emits a single flat page-tree node, so the adversarial shapes here (nested
/// intermediates, a forged <c>/Parent</c>, a cycle, a lying <c>/Count</c>) need to be written by
/// hand to exist at all.
/// </summary>
public sealed class PageTreeTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private static byte[] SaveDocToBytes(PdfDocument doc)
    {
        var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    private static byte[] LoadFixture(string name)
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException(
                $"Embedded fixture '{name}' not found. Check the EmbeddedResource glob in the csproj.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Builds a classic (non-stream) cross-reference table from a set of already-formatted indirect
    /// object bodies. Every reference between the bodies has to be written out by the caller as
    /// literal <c>"N 0 R"</c> text; this helper only lays the objects out, records their offsets,
    /// and writes the xref/trailer/startxref around them.
    /// </summary>
    private static byte[] BuildPdf(int rootObjectNumber, params (int Num, string Body)[] objects)
    {
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.ASCII.GetBytes(s));

        W("%PDF-1.7\n");

        var maxNum = objects.Max(o => o.Num);
        var offsets = new int?[maxNum + 1];
        foreach (var (num, body) in objects.OrderBy(o => o.Num))
        {
            offsets[num] = (int)ms.Position;
            W($"{num} 0 obj\n{body}\nendobj\n");
        }

        var xrefOffset = (int)ms.Position;
        W($"xref\n0 {maxNum + 1}\n");
        W("0000000000 65535 f \n");
        for (var i = 1; i <= maxNum; i++)
        {
            // A number with no supplied body is left free: a dangling reference to it resolves to
            // null via the same path any other undefined object does (ISO 32000-2 §7.3.10: "not be
            // considered an error").
            W(offsets[i] is { } offset
                ? $"{offset:D10} 00000 n \n"
                : "0000000000 65535 f \n");
        }
        W($"trailer\n<< /Size {maxNum + 1} /Root {rootObjectNumber} 0 R >>\n");
        W($"startxref\n{xrefOffset}\n%%EOF\n");

        return ms.ToArray();
    }

    private static PdfDocumentReader Open(byte[] bytes) => PdfReader.Open(bytes);

    // ── 1. Writer-built document ─────────────────────────────────────────────────────────────────

    [Fact]
    public void WriterBuiltDocument_reportsThreePagesInOrder_withMatchingObjectNumbersAndMediaBox()
    {
        var box = new PdfRectangle(0, 0, 500, 700);
        using var doc = new PdfDocument();
        doc.AddPage(box);
        doc.AddPage(box);
        doc.AddPage(box);

        using var reader = Open(SaveDocToBytes(doc));

        Assert.Equal(3, reader.PageCount);
        Assert.Equal(3, reader.Pages.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(i, reader.Pages[i].Index);
            Assert.Equal(box.LlX, reader.Pages[i].MediaBox.LlX);
            Assert.Equal(box.LlY, reader.Pages[i].MediaBox.LlY);
            Assert.Equal(box.UrX, reader.Pages[i].MediaBox.UrX);
            Assert.Equal(box.UrY, reader.Pages[i].MediaBox.UrY);
        }

        // ObjectNumber matches the xref: resolving the /Kids array directly gives the ground truth.
        var pagesDict = Assert.IsType<PdfDictionary>(reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!));
        var kids = Assert.IsType<PdfArray>(reader.ResolveValue(pagesDict.Get(PdfName.Kids)!));
        var expected = Enumerable.Range(0, kids.Count)
            .Select(i => (int?)Assert.IsType<PdfIndirectReference>(kids[i]).ObjectNumber);
        Assert.Equal(expected, reader.Pages.Select(p => p.ObjectNumber));

        for (var i = 0; i < 3; i++)
            Assert.Same(reader.Pages[i], reader.GetPage(i));
    }

    [Fact]
    public void GetPage_outOfRange_throws()
    {
        using var doc = new PdfDocument();
        doc.AddPage();
        doc.AddPage();
        using var reader = Open(SaveDocToBytes(doc));

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetPage(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetPage(-1));
    }

    // ── 2. Nested intermediates ───────────────────────────────────────────────────────────────────

    [Fact]
    public void NestedIntermediateNodes_yieldPagesInDocumentOrder()
    {
        // Root Kids = [nested Pages node, direct page] -> [page4, page5, page6] in that order.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 6 0 R] /Count 3 >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R 5 0 R] /Count 2 >>"),
            (4, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 100 100] >>"),
            (5, "<< /Type /Page /Parent 3 0 R /MediaBox [0 0 100 100] >>"),
            (6, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(3, reader.PageCount);
        Assert.Equal([4, 5, 6], reader.Pages.Select(p => p.ObjectNumber));
    }

    // ── 3. /Count lies ────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)] // undercounts three real kids
    [InlineData(100)] // overcounts two real kids
    public void PageCount_ignoresACount_andReflectsTheActualWalk(int lyingCount)
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, $"<< /Type /Pages /Kids [3 0 R 4 0 R] /Count {lyingCount} >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"),
            (4, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(2, reader.PageCount);
    }

    // ── 4. Inheritance ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void InheritableAttributes_resolveFromNearestAncestor_pageOwnEntryWinsOutright()
    {
        // Root: /MediaBox [0 0 200 300] /Rotate 90, Kids = [nested Pages, leaf-own, leaf-inherits-root]
        // Nested Pages: /MediaBox [0 0 100 100] (own, overrides root), Kids = [leaf-inherits-nested]
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 5 0 R 6 0 R] /Count 3 /MediaBox [0 0 200 300] /Rotate 90 >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 /MediaBox [0 0 100 100] >>"),
            (4, "<< /Type /Page /Parent 3 0 R >>"), // inherits nested MediaBox, root Rotate
            (5, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 50 50] >>"), // own MediaBox wins outright
            (6, "<< /Type /Page /Parent 2 0 R >>")); // inherits root MediaBox and Rotate

        using var reader = Open(bytes);
        Assert.Equal(3, reader.PageCount);

        var nearestAncestorPage = reader.Pages.Single(p => p.ObjectNumber == 4);
        AssertRectangle(0, 0, 100, 100, nearestAncestorPage.MediaBox);
        Assert.Equal(90, nearestAncestorPage.Rotate);

        var ownAttributePage = reader.Pages.Single(p => p.ObjectNumber == 5);
        AssertRectangle(0, 0, 50, 50, ownAttributePage.MediaBox);

        var rootInheritedPage = reader.Pages.Single(p => p.ObjectNumber == 6);
        AssertRectangle(0, 0, 200, 300, rootInheritedPage.MediaBox);
        Assert.Equal(90, rootInheritedPage.Rotate);

        // Dictionary is the page's OWN dictionary, not a merged view carrying its inherited
        // MediaBox in: resolving each object number directly gives back the exact same instance,
        // and a page that inherits its MediaBox has no /MediaBox key of its own at all.
        Assert.Same(reader.ResolveValue(new PdfIndirectReference(4, 0)), nearestAncestorPage.Dictionary);
        Assert.Null(nearestAncestorPage.Dictionary.Get(PdfName.MediaBox));
        Assert.Same(reader.ResolveValue(new PdfIndirectReference(5, 0)), ownAttributePage.Dictionary);
        Assert.Same(reader.ResolveValue(new PdfIndirectReference(6, 0)), rootInheritedPage.Dictionary);
        Assert.Null(rootInheritedPage.Dictionary.Get(PdfName.MediaBox));
    }

    // ── 5. Forged /Parent ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForgedParent_isIgnored_inheritanceFollowsTheRealAncestorChain()
    {
        // Object 3's /Parent points at object 4 (not its real ancestor), which carries a
        // different /MediaBox; the walk must not follow it.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 300 300] >>"),
            (3, "<< /Type /Page /Parent 4 0 R >>"),
            (4, "<< /Type /Pages /Kids [] /Count 0 /MediaBox [0 0 999 999] >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        AssertRectangle(0, 0, 300, 300, reader.Pages[0].MediaBox);
    }

    // ── 6. Cycle ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void KidReferencingItsOwnAncestor_reportsCycle_andReturnsThePagesFoundBeforeIt()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [4 0 R 3 0 R] /Count 2 /MediaBox [0 0 100 100] >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [2 0 R] /Count 0 >>"), // cycles back to the root
            (4, "<< /Type /Page /Parent 2 0 R >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.Equal(4, reader.Pages[0].ObjectNumber);

        var diagnostic = Assert.Single(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeCycle);
        Assert.Equal(2, diagnostic.ObjectNumber);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, diagnostic.Severity);
    }

    // ── 6b. Shared /Kids array object (exponential-walk guard) ───────────────────────────────────

    [Fact]
    public void SharedKidsArrayObject_isDetectedAsACycle_beforeExhaustingAnyBudget()
    {
        // Object 9 is BOTH the root's own /Kids array AND, via two direct (not indirect) dictionary
        // elements inside that same array, each child's /Kids too. A tree walk that only guards
        // against a repeated NODE object (as opposed to a repeated /Kids ARRAY object) never revisits
        // the same object number here, because the two children themselves are direct dictionaries
        // (object number 0) rather than indirect references: only the shared array they both point
        // back at, object 9, ever repeats. Depth doubles every level with no cap of its own to stop
        // it, so a walker that does not also key its cycle guard on the /Kids array's own object
        // number never returns.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids 9 0 R /Count 1 >>"),
            (9, "[ << /Type /Pages /Kids 9 0 R >> << /Type /Pages /Kids 9 0 R >> ]"));

        var stopwatch = Stopwatch.StartNew();
        using var reader = Open(bytes);
        var pageCount = reader.PageCount;
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            $"Walking the shared-/Kids-array fixture took {stopwatch.Elapsed}, expected well under 2s.");
        Assert.Equal(0, pageCount);
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.PageTreeCycle && d.Severity == PdfReaderDiagnosticSeverity.Warning);
    }

    // ── 6c. Pure node-count budget (no object ever repeats) ──────────────────────────────────────

    [Fact]
    public void ManyDistinctEmptyNodes_stopsAtTheKidsExaminedBudget_withNoCycleInvolved()
    {
        // Every object number below is distinct: nothing here ever repeats, so the shared-/Kids
        // cycle guard above cannot be what stops this walk. Each of the MaxKidsExamined + 1 root
        // kids is its own /Type /Pages node with an explicit, present-but-empty /Kids [] (a legal
        // empty subtree, not a malformed one), so none of them counts toward the 100,000-leaf cap
        // either, so only the total number of /Kids elements examined can stop this walk.
        const int nodeCount = 1_000_001;
        var bytes = BuildManyEmptyPagesNodesPdf(nodeCount);

        var stopwatch = Stopwatch.StartNew();
        using var reader = Open(bytes);
        var pageCount = reader.PageCount;
        stopwatch.Stop();

        Assert.Equal(0, pageCount);
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.PageTreeNodeLimitExceeded && d.Severity == PdfReaderDiagnosticSeverity.Warning);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeCycle);

        // Not a hard budget assertion (machine-dependent): a generous ceiling that fails loudly if
        // the walk regresses to something quadratic in the number of kids examined.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Walking {nodeCount} distinct empty nodes took {stopwatch.Elapsed}, expected well under 30s.");
    }

    /// <summary>
    /// Builds a root whose <c>/Kids</c> array directly lists <paramref name="nodeCount"/> distinct
    /// <c>&lt;&lt; /Type /Pages /Kids [] &gt;&gt;</c> objects, string-built rather than through
    /// <see cref="BuildPdf"/> for the same O(n) reason as <see cref="BuildLeafCapPdf"/>.
    /// </summary>
    private static byte[] BuildManyEmptyPagesNodesPdf(int nodeCount)
    {
        var sb = new StringBuilder(nodeCount * 40 + 4096);
        sb.Append("%PDF-1.7\n");

        var totalObjects = nodeCount + 3; // catalog (1), root Pages (2), nodes (3..nodeCount+2)
        var offsets = new int[totalObjects];

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = new StringBuilder(nodeCount * 8);
        kids.Append('[');
        for (var i = 0; i < nodeCount; i++)
        {
            if (i > 0)
                kids.Append(' ');
            kids.Append(i + 3).Append(" 0 R");
        }
        kids.Append(']');

        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids ").Append(kids).Append(" /Count 0 >>\nendobj\n");

        for (var i = 0; i < nodeCount; i++)
        {
            var objNum = i + 3;
            offsets[objNum] = sb.Length;
            sb.Append(objNum).Append(" 0 obj\n<< /Type /Pages /Kids [] >>\nendobj\n");
        }

        var xrefOffset = sb.Length;
        sb.Append("xref\n0 ").Append(totalObjects).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var objNum = 1; objNum < totalObjects; objNum++)
            sb.Append(offsets[objNum].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(totalObjects).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── 7. Depth ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void DeepChain_stopsAtTheDepthCap_andReturnsThePagesFoundUnderIt()
    {
        // 300 levels deep. Level i (for i < 300) is a Pages node with two kids: a leaf and the next
        // level's Pages node; level 300 is a plain leaf. One real page per level below the cap
        // proves the walk returns "less", not "nothing".
        const int chainDepth = 300;
        var objects = new List<(int Num, string Body)>
        {
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
        };

        // Object numbering: 2, 4, 6, ... are Pages nodes (one per level); 3, 5, 7, ... are that
        // level's own leaf. Level i's Pages node is object 2*i; its leaf is object 2*i + 1.
        for (var level = 1; level <= chainDepth; level++)
        {
            var pagesObj = 2 * level;
            var leafObj = pagesObj + 1;
            var isLast = level == chainDepth;
            var kids = isLast ? $"[{leafObj} 0 R]" : $"[{leafObj} 0 R {pagesObj + 2} 0 R]";
            objects.Add((pagesObj, $"<< /Type /Pages /Kids {kids} /Count 1 /MediaBox [0 0 100 100] >>"));
            objects.Add((leafObj, "<< /Type /Page >>"));
        }

        var bytes = BuildPdf(1, objects.ToArray());
        using var reader = Open(bytes);

        // PageTreeWalker.MaxDepth (256) levels open: the root (level 1) plus 255 pushed intermediate
        // nodes reach level 256; pushing level 257 is refused. Levels 1..256 each contributed one
        // leaf before the cap.
        Assert.Equal(256, reader.PageCount);
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.PageTreeDepthExceeded && d.Severity == PdfReaderDiagnosticSeverity.Warning);
    }

    // ── 8. Leaf cap ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void LeafCap_stopsAt100000_andReportsTheLimit()
    {
        const int declaredLeaves = 100_001;
        var bytes = BuildLeafCapPdf(declaredLeaves);

        var stopwatch = Stopwatch.StartNew();
        using var reader = Open(bytes);
        var pageCount = reader.PageCount;
        stopwatch.Stop();

        Assert.Equal(100_000, pageCount);
        Assert.Contains(
            reader.Diagnostics,
            d => d.Code == PdfReaderDiagnosticCode.PageTreeLeafLimitExceeded && d.Severity == PdfReaderDiagnosticSeverity.Warning);

        // Not a hard budget assertion (machine-dependent): a generous ceiling that fails loudly if
        // the walk regresses to something quadratic in leaf count.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Walking {declaredLeaves} declared leaves took {stopwatch.Elapsed}, expected a few seconds at most.");
    }

    /// <summary>
    /// Builds a flat page tree with <paramref name="leafCount"/> minimal <c>/Type /Page</c> leaves
    /// sharing one inherited <c>/MediaBox</c> on the root, entirely through string building: going
    /// through <see cref="PdfDocument"/> or the tuple-based <see cref="BuildPdf"/> helper (both
    /// O(n²) at this size, the latter via its per-object offset array scan) would make a
    /// 100,001-leaf fixture too slow to build in a test.
    /// </summary>
    private static byte[] BuildLeafCapPdf(int leafCount)
    {
        var sb = new StringBuilder(leafCount * 32 + 4096);
        sb.Append("%PDF-1.7\n");

        var totalObjects = leafCount + 3; // catalog (1), pages root (2), leaves (3..leafCount+2)
        var offsets = new int[totalObjects];

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");

        var kids = new StringBuilder(leafCount * 8);
        kids.Append('[');
        for (var i = 0; i < leafCount; i++)
        {
            if (i > 0)
                kids.Append(' ');
            kids.Append(i + 3).Append(" 0 R");
        }
        kids.Append(']');

        offsets[2] = sb.Length;
        sb.Append("2 0 obj\n<< /Type /Pages /Kids ").Append(kids)
          .Append(" /Count ").Append(leafCount)
          .Append(" /MediaBox [0 0 612 792] >>\nendobj\n");

        for (var i = 0; i < leafCount; i++)
        {
            var objNum = i + 3;
            offsets[objNum] = sb.Length;
            sb.Append(objNum).Append(" 0 obj\n<< /Type /Page >>\nendobj\n");
        }

        var xrefOffset = sb.Length;
        sb.Append("xref\n0 ").Append(totalObjects).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var objNum = 1; objNum < totalObjects; objNum++)
            sb.Append(offsets[objNum].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(totalObjects).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ── 9. Missing / malformed page tree ─────────────────────────────────────────────────────────

    [Fact]
    public void MissingPagesEntry_reportsPageTreeMissing()
    {
        var bytes = BuildPdf(1, (1, "<< /Type /Catalog >>"));
        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeMissing);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void PagesEntryNotADictionary_reportsPageTreeMissing()
    {
        var bytes = BuildPdf(
            1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "(not a dictionary)"));
        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeMissing);
    }

    [Fact]
    public void KidsNotAnArray_onTheRoot_reportsPageTreeMissing()
    {
        var bytes = BuildPdf(
            1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids 5 /Count 1 >>"));
        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeMissing);
    }

    // ── 9a. Root classification ──────────────────────────────────────────────────────────────────

    [Fact]
    public void RootTypedAsAPage_withKids_reportsPageTreeMissing_insteadOfWalkingIt()
    {
        // /Type /Page on the root wins over the /Kids array sitting right next to it, the same way
        // it would for any other node reached through /Kids (see ClassifyByType); the root is not
        // exempt from that rule just because Walk reaches it a different way.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Page /Kids [3 0 R] >>"),
            (3, "<< /Type /Page /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeMissing);
        Assert.Equal(PdfReaderDiagnosticSeverity.Error, d.Severity);
    }

    [Fact]
    public void RootTypedAsAPage_withoutKids_reportsPageTreeMissing()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Page /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeMissing);
    }

    [Fact]
    public void RootIsTheCatalogItself_withKidsBoltedOn_reportsPageTreeMissing()
    {
        // Object 1 is both the catalog AND its own /Pages entry (a self-reference). Its /Type is
        // /Catalog, neither /Pages nor /Page, so it is skipped the same way any other wrong /Type
        // would be, even though it happens to carry a /Kids array of its own.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 1 0 R /Kids [2 0 R] >>"),
            (2, "<< /Type /Page /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeMissing);
        Assert.Equal(1, d.ObjectNumber);
    }

    [Fact]
    public void RootWithEmptyKids_yieldsZeroPages_withNoDiagnosticAtAll()
    {
        // ISO 32000-2 §7.7.3 does not require a document to have at least one page: an empty tree
        // is a valid zero-page document, not a defect worth reporting.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [] /Count 0 >>"));

        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        Assert.Empty(reader.Diagnostics);
    }

    [Fact]
    public void KidThatIsAnInteger_isSkipped_withPageTreeKidNotDictionary()
    {
        var bytes = BuildPdf(
            1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "5"));
        using var reader = Open(bytes);

        Assert.Equal(0, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeKidNotDictionary);
        Assert.Equal(3, d.ObjectNumber);
        Assert.Equal(PdfReaderDiagnosticSeverity.Warning, d.Severity);
    }

    // ── 9b. Node/leaf classification ─────────────────────────────────────────────────────────────

    [Fact]
    public void KidClassifiedByType_wrongTypeIsSkipped_neitherNodeNorPage()
    {
        // Root /Kids [1 0 R 3 0 R 4 0 R]: object 1 is the catalog itself (reused), object 3 is a
        // /Type /Font dictionary (neither /Type /Pages nor /Type /Page), and object 4 is a real
        // page. Both non-page objects report PageTreeNodeMalformed and contribute nothing.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [1 0 R 3 0 R 4 0 R] /Count 1 >>"),
            (3, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"),
            (4, "<< /Type /Page /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.Equal(4, reader.Pages[0].ObjectNumber);

        var malformed = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.PageTreeNodeMalformed).ToList();
        Assert.Equal(2, malformed.Count);
        Assert.Contains(malformed, d => d.ObjectNumber == 1);
        Assert.Contains(malformed, d => d.ObjectNumber == 3);
    }

    [Fact]
    public void TypePagesWithNoUsableKids_reportsNodeMalformed_contributesNoChildren()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 1 >>"),
            (3, "<< /Type /Pages >>"), // no /Kids at all: malformed, contributes nothing
            (5, "<< /Type /Page /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeNodeMalformed);
        Assert.Equal(3, d.ObjectNumber);
    }

    [Fact]
    public void TypePagesWithEmptyKids_isSilent_legalEmptySubtree()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 1 >>"),
            (3, "<< /Type /Pages /Kids [] >>"), // legal empty subtree: silent
            (5, "<< /Type /Page /MediaBox [0 0 100 100] >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeNodeMalformed);
    }

    [Fact]
    public void TypePageWithStrayKids_isTreatedAsALeaf_kidsIgnored_withDiagnostic()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            // A /Type /Page object that also carries /Kids: /Type wins, it is still a leaf, and the
            // stray /Kids never gets walked (object 9 does not exist, so a walk that mistakenly
            // treated this as a node would report PageTreeKidNotDictionary instead).
            (3, "<< /Type /Page /MediaBox [0 0 100 100] /Kids [9 0 R] >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeNodeMalformed);
        Assert.Equal(3, d.ObjectNumber);
        Assert.DoesNotContain(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeKidNotDictionary);
    }

    [Fact]
    public void KidWithNoTypeAndNoKids_isALeafSilently()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /MediaBox [0 0 100 100] >>")); // no /Type at all, no /Kids: tolerated as a leaf

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeNodeMalformed);
    }

    [Fact]
    public void DirectDictionaryKid_isOnePage_withNullObjectNumber()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [<< /Type /Page /MediaBox [0 0 100 100] >>] /Count 1 >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.Null(reader.Pages[0].ObjectNumber);
    }

    // ── 9c. Malformed indirect targets recover instead of throwing ──────────────────────────────

    [Fact]
    public void MiddleKidWithUnresolvableRotate_stillYieldsThreePages_withRotateZeroAndDiagnostic()
    {
        // Object 5 alone holds the 40-digit literal. PdfObjectParser.ParseLong overflows long on
        // it and throws while parsing object 5's OWN body, not object 3's (the page dict itself
        // parses fine; only resolving its /Rotate indirect reference fails). Before this fix, that
        // exception escaped PageTreeWalker.Walk entirely and PdfDocumentReader.Pages lost every
        // page in the document, not just this one kid.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 4 0 R 6 0 R] /Count 3 /MediaBox [0 0 100 100] >>"),
            (3, "<< /Type /Page >>"),
            (4, "<< /Type /Page /Rotate 5 0 R >>"),
            (5, "9999999999999999999999999999999999999999"), // overflows long: throws while parsing
            (6, "<< /Type /Page >>"));

        using var reader = Open(bytes);

        Assert.Equal(3, reader.PageCount);
        var middle = reader.Pages.Single(p => p.ObjectNumber == 4);
        Assert.Equal(0, middle.Rotate);

        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(4, d.ObjectNumber);
        Assert.Contains("Rotate", d.Message);
    }

    [Fact]
    public void MediaBoxElementWithUnresolvableTarget_fallsBackWithDiagnostic()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /MediaBox [0 0 100 5 0 R] >>"),
            (5, "9999999999999999999999999999999999999999"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        AssertRectangle(0, 0, 612, 792, reader.Pages[0].MediaBox);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Contains("MediaBox", d.Message);
    }

    // A real literal with 310+ integer digits (ISO 32000-2 §7.3.3, Annex C.1's implementation-limited
    // range) parses to +/-Infinity under double.TryParse; before this fix that reached PdfReal's
    // constructor, which throws ArgumentException rather than InvalidDataException, so
    // PageTreeWalker.TryResolve (which only catches InvalidDataException) let it escape
    // PageCount/Pages/GetPage entirely instead of reporting and recovering the way every other
    // malformed indirect target already does.
    private static readonly string HugeRealLiteral = "1" + new string('0', 309) + ".0";

    [Fact]
    public void RealOutOfRange_resolvingItsOwnObject_throwsInvalidDataException_notArgumentException()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /MediaBox [0 0 100 100] /UserUnit " + HugeRealLiteral + " >>"));
        using var reader = Open(bytes);

        var ex = Assert.Throws<InvalidDataException>(() => reader.Resolve(3));
        Assert.IsNotType<ArgumentException>(ex);
    }

    [Fact]
    public void RealOutOfRange_inALeafsOwnMediaBox_fallsBackToLetter_withDiagnostic_noThrow()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, "<< /Type /Page /MediaBox [0 0 100 5 0 R] >>"),
            (5, HugeRealLiteral));
        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        AssertRectangle(0, 0, 612, 792, reader.Pages[0].MediaBox);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(3, d.ObjectNumber);
        Assert.Contains("MediaBox", d.Message);
    }

    [Fact]
    public void RealOutOfRange_inAnAncestorsMediaBox_fallsBackToLetter_withOneDiagnostic_noThrow()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 5 0 R] >>"),
            (3, "<< /Type /Page >>"),
            (5, HugeRealLiteral));
        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        AssertRectangle(0, 0, 612, 792, reader.Pages[0].MediaBox);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(2, d.ObjectNumber);
        Assert.Null(d.PageIndex);
        Assert.Contains("MediaBox", d.Message);
    }

    [Fact]
    public void RealOutOfRange_asAnExtraKidsElement_isSkipped_withPageTreeKidNotDictionary_noThrow()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>"),
            (3, "<< /Type /Page /MediaBox [0 0 100 100] >>"),
            (5, HugeRealLiteral)); // object 5's ENTIRE body is the huge literal, not a dictionary
        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.Equal(3, reader.Pages[0].ObjectNumber);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageTreeKidNotDictionary);
        Assert.Equal(5, d.ObjectNumber);
    }

    // ── 10. Attribute normalisation ───────────────────────────────────────────────────────────────

    [Fact]
    public void MediaBox_reversedCorners_normalises_noDiagnostic()
    {
        var bytes = OnePageDocument("/MediaBox [612 792 0 0]");
        using var reader = Open(bytes);

        AssertRectangle(0, 0, 612, 792, reader.Pages[0].MediaBox);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void MediaBox_threeElementArray_fallsBackToLetter_withDiagnostic()
    {
        var bytes = OnePageDocument("/MediaBox [0 0 612]");
        using var reader = Open(bytes);

        AssertRectangle(0, 0, 612, 792, reader.Pages[0].MediaBox);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(0, d.PageIndex);
        Assert.Contains("MediaBox", d.Message);
    }

    [Fact]
    public void CropBox_absent_equalsMediaBox_noDiagnostic()
    {
        var bytes = OnePageDocument("/MediaBox [0 0 300 400]");
        using var reader = Open(bytes);

        AssertRectangle(0, 0, 300, 400, reader.Pages[0].CropBox);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void CropBox_extendingPastMediaBox_isClippedToTheIntersection_noDiagnostic()
    {
        // ISO 32000-2 §14.11.2.1: a crop box extending past the media box is treated as its
        // intersection with it, not exposed as written.
        var bytes = OnePageDocument("/MediaBox [0 0 100 100] /CropBox [-500 -500 900 900]");
        using var reader = Open(bytes);

        AssertRectangle(0, 0, 100, 100, reader.Pages[0].CropBox);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void CropBox_disjointFromMediaBox_fallsBackToMediaBox_withDiagnostic()
    {
        var bytes = OnePageDocument("/MediaBox [0 0 100 100] /CropBox [200 200 300 300]");
        using var reader = Open(bytes);

        AssertRectangle(0, 0, 100, 100, reader.Pages[0].CropBox);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Contains("CropBox", d.Message);
    }

    [Fact]
    public void CropBox_partiallyOverlappingMediaBox_isClippedToTheIntersection_noDiagnostic()
    {
        var bytes = OnePageDocument("/MediaBox [0 0 100 100] /CropBox [50 50 150 150]");
        using var reader = Open(bytes);

        AssertRectangle(50, 50, 100, 100, reader.Pages[0].CropBox);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void CropBox_zeroWidthAndContainedInMediaBox_isKeptAsWritten_noDiagnostic()
    {
        // ISO 32000-2 §7.9.5's NOTE permits a zero-width or zero-height rectangle; it must not be
        // silently replaced by MediaBox just because it has no area.
        var bytes = OnePageDocument("/MediaBox [0 0 612 792] /CropBox [100 100 100 300]");
        using var reader = Open(bytes);

        AssertRectangle(100, 100, 100, 300, reader.Pages[0].CropBox);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void CropBox_touchingMediaBoxAtOneEdge_isKeptAsAZeroWidthCrop_noDiagnostic()
    {
        // The intersection's x-span collapses to a single point (x0 == x1 == 612) while the
        // y-span still overlaps: strict '<' treats that as touching, not disjoint.
        var bytes = OnePageDocument("/MediaBox [0 0 612 792] /CropBox [612 0 700 792]");
        using var reader = Open(bytes);

        AssertRectangle(612, 0, 612, 792, reader.Pages[0].CropBox);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void OwnMalformedMediaBox_fallsBackToAValidAncestor_beforeLetter()
    {
        // The page's own /MediaBox is malformed (3 elements); the root's is valid. Falling straight
        // to Letter here would silently discard a perfectly good ancestor value.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 400 500] >>"),
            (3, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400] >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        AssertRectangle(0, 0, 400, 500, reader.Pages[0].MediaBox);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(3, d.ObjectNumber);
    }

    [Theory]
    [InlineData("450", 90)]
    [InlineData("-90", 270)]
    [InlineData("90.0", 90)] // integer-valued real: accepted (see PageTreeWalker.ResolveRotateAttribute)
    public void Rotate_normalisesFoldedOrRealValue_noDiagnostic(string rawRotate, int expected)
    {
        var bytes = OnePageDocument($"/MediaBox [0 0 100 100] /Rotate {rawRotate}");
        using var reader = Open(bytes);

        Assert.Equal(expected, reader.Pages[0].Rotate);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    [Fact]
    public void Rotate_notAMultipleOf90_normalisesToZero_withDiagnostic()
    {
        var bytes = OnePageDocument("/MediaBox [0 0 100 100] /Rotate 45");
        using var reader = Open(bytes);

        Assert.Equal(0, reader.Pages[0].Rotate);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Contains("Rotate", d.Message);
    }

    [Fact]
    public void AncestorMalformedRotate_isSkipped_grandparentsValidValueContinuesTheChain()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate 90 >>"),
            (3, "<< /Type /Pages /Parent 2 0 R /Kids [4 0 R] /Count 1 /Rotate 45 /MediaBox [0 0 100 100] >>"),
            (4, "<< /Type /Page /Parent 3 0 R >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.Equal(90, reader.Pages[0].Rotate);

        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(3, d.ObjectNumber);
        Assert.Null(d.PageIndex);
        Assert.Contains("Rotate", d.Message);
    }

    [Fact]
    public void LeafsOwnValidRotate_overridesAValidAncestor_noDiagnostic()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 /Rotate 90 /MediaBox [0 0 100 100] >>"),
            (3, "<< /Type /Page /Parent 2 0 R /Rotate 180 >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        Assert.Equal(180, reader.Pages[0].Rotate);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
    }

    // ── 10a. Diagnostic wording for a direct (object-number-0) source ──────────────────────────────

    [Fact]
    public void MalformedAttributeOnADirectNode_namesItADirectPageTreeNode_notObjectZero()
    {
        // /Pages is a direct dictionary embedded in the catalog rather than an indirect reference,
        // so the root node itself carries object number 0.
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages << /Type /Pages /Kids [3 0 R] /MediaBox [0 0 100] >> >>"),
            (3, "<< /Type /Page >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(
            "MediaBox on a direct page-tree node did not resolve to a 4-element numeric array "
            + "(ISO 32000-2 §7.9.5); the nearest valid ancestor value, if any, is used instead.",
            d.Message);
        Assert.Null(d.ObjectNumber);
    }

    [Fact]
    public void MalformedAttributeOnADirectLeaf_namesItThePageDictionary_notObjectZero()
    {
        var bytes = BuildPdf(
            rootObjectNumber: 1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [<< /Type /Page /MediaBox [0 0 100 100] /Rotate 45 >>] /Count 1 >>"));

        using var reader = Open(bytes);

        Assert.Equal(1, reader.PageCount);
        var d = Assert.Single(reader.Diagnostics, x => x.Code == PdfReaderDiagnosticCode.PageAttributeInvalid);
        Assert.Equal(
            "Rotate 45 on the page dictionary is not a multiple of 90 (ISO 32000-2 §7.7.3.3); the "
            + "nearest valid ancestor value, if any, is used instead.",
            d.Message);
        Assert.Null(d.ObjectNumber);
    }

    // ── 10b. Attribute resolution is O(nodes), not O(nodes × leaves) ───────────────────────────────

    [Fact]
    public void DeepChainOfMalformedAncestors_reportsOncePerNode_notOncePerLeaf()
    {
        // 200 nested indirect /Type /Pages nodes, each carrying an own /MediaBox, /CropBox, and
        // /Rotate that are all malformed, with the innermost node fanning out to several thousand
        // minimal leaves that inherit from the whole chain. A walk that re-scanned every ancestor
        // for every leaf would report each ancestor's defect once PER LEAF, costing depth times
        // leaves candidate checks per attribute; this asserts it is reported once PER NODE instead
        // (DiagnosticSink dedupes by (code, object, page), so the three attribute reports for one
        // node collapse into the one PageAttributeInvalid entry that key allows), and that the walk
        // stays fast doing it.
        const int nodeDepth = 200;
        const int leafCount = 5_000;
        var bytes = BuildDeepMalformedAttributeChainPdf(nodeDepth, leafCount, out var nodeObjectNumbers);

        var stopwatch = Stopwatch.StartNew();
        using var reader = Open(bytes);
        var pageCount = reader.PageCount;
        stopwatch.Stop();

        Assert.Equal(leafCount, pageCount);
        Assert.All(reader.Pages, p => AssertRectangle(0, 0, 612, 792, p.MediaBox));
        Assert.All(reader.Pages, p => Assert.Equal(0, p.Rotate));

        var invalid = reader.Diagnostics.Where(d => d.Code == PdfReaderDiagnosticCode.PageAttributeInvalid).ToList();

        // One report per ancestor node, and nothing else: every leaf inherits silently once its own
        // absent entries fall through to an already-resolved (if all-malformed) ancestor chain, so
        // nothing here is reported per leaf at all.
        Assert.Equal(nodeDepth, invalid.Count);
        Assert.All(invalid, d => Assert.Null(d.PageIndex));
        foreach (var objectNumber in nodeObjectNumbers)
            Assert.Equal(1, invalid.Count(d => d.ObjectNumber == objectNumber));

        // Not a hard budget assertion (machine-dependent); a generous ceiling that fails loudly if
        // attribute resolution regresses to rescanning the ancestor chain per leaf.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Walking {nodeDepth} malformed ancestors over {leafCount} leaves took {stopwatch.Elapsed}, "
            + "expected well under 5s.");
    }

    /// <summary>
    /// Builds a chain of <paramref name="nodeDepth"/> nested indirect <c>/Type /Pages</c> objects
    /// (object numbers 2 through <c>1 + nodeDepth</c>), each carrying its own malformed
    /// <c>/MediaBox</c>, <c>/CropBox</c>, and <c>/Rotate</c>, with the innermost node's <c>/Kids</c>
    /// fanning out to <paramref name="leafCount"/> minimal <c>/Type /Page</c> leaves. String-built
    /// rather than through the tuple-based <see cref="BuildPdf"/> helper for the same O(n) reason as
    /// <see cref="BuildLeafCapPdf"/>.
    /// </summary>
    private static byte[] BuildDeepMalformedAttributeChainPdf(
        int nodeDepth, int leafCount, out int[] nodeObjectNumbers)
    {
        var sb = new StringBuilder(nodeDepth * 128 + leafCount * 32 + 4096);
        sb.Append("%PDF-1.7\n");

        const int firstNodeObj = 2;
        var firstLeafObj = firstNodeObj + nodeDepth;
        var totalObjects = firstLeafObj + leafCount; // exclusive upper bound
        var offsets = new int[totalObjects];

        offsets[1] = sb.Length;
        sb.Append("1 0 obj\n<< /Type /Catalog /Pages ").Append(firstNodeObj).Append(" 0 R >>\nendobj\n");

        nodeObjectNumbers = new int[nodeDepth];
        for (var level = 0; level < nodeDepth; level++)
        {
            var objNum = firstNodeObj + level;
            nodeObjectNumbers[level] = objNum;
            offsets[objNum] = sb.Length;

            string kids;
            if (level < nodeDepth - 1)
            {
                kids = $"[{objNum + 1} 0 R]";
            }
            else
            {
                var kb = new StringBuilder(leafCount * 8);
                kb.Append('[');
                for (var i = 0; i < leafCount; i++)
                {
                    if (i > 0)
                        kb.Append(' ');
                    kb.Append(firstLeafObj + i).Append(" 0 R");
                }
                kb.Append(']');
                kids = kb.ToString();
            }

            sb.Append(objNum).Append(" 0 obj\n<< /Type /Pages /Kids ").Append(kids)
              .Append(" /MediaBox [0 0 100] /CropBox [1 2 3] /Rotate 45 >>\nendobj\n");
        }

        for (var i = 0; i < leafCount; i++)
        {
            var objNum = firstLeafObj + i;
            offsets[objNum] = sb.Length;
            sb.Append(objNum).Append(" 0 obj\n<< /Type /Page >>\nendobj\n");
        }

        var xrefOffset = sb.Length;
        sb.Append("xref\n0 ").Append(totalObjects).Append('\n');
        sb.Append("0000000000 65535 f \n");
        for (var objNum = 1; objNum < totalObjects; objNum++)
            sb.Append(offsets[objNum].ToString("D10")).Append(" 00000 n \n");
        sb.Append("trailer\n<< /Size ").Append(totalObjects).Append(" /Root 1 0 R >>\n");
        sb.Append("startxref\n").Append(xrefOffset).Append("\n%%EOF\n");

        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    private static byte[] OnePageDocument(string pageAttributes) =>
        BuildPdf(
            1,
            (1, "<< /Type /Catalog /Pages 2 0 R >>"),
            (2, "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"),
            (3, $"<< /Type /Page /Parent 2 0 R {pageAttributes} >>"));

    // ── 11. Laziness ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_doesNotWalkThePageTree_untilPageCountIsRead()
    {
        var bytes = BuildPdf(1, (1, "<< /Type /Catalog >>")); // broken: no /Pages at all

        using var reader = Open(bytes);
        Assert.DoesNotContain(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeMissing);

        _ = reader.PageCount;
        Assert.Contains(reader.Diagnostics, d => d.Code == PdfReaderDiagnosticCode.PageTreeMissing);
    }

    // ── 12. Encrypted fixture ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void EncryptedFixture_pagesAreReachableThroughTheDecryptingResolver()
    {
        using var reader = PdfReader.Open(LoadFixture("enc-aes-128-emptyuser.pdf"));

        Assert.True(reader.PageCount >= 1);
        var mediaBox = reader.GetPage(0).MediaBox;
        Assert.True(mediaBox.Width > 0);
        Assert.True(mediaBox.Height > 0);
    }

    // ── Shared assertion helper ───────────────────────────────────────────────────────────────────

    private static void AssertRectangle(double llx, double lly, double urx, double ury, PdfRectangle actual)
    {
        Assert.Equal(llx, actual.LlX);
        Assert.Equal(lly, actual.LlY);
        Assert.Equal(urx, actual.UrX);
        Assert.Equal(ury, actual.UrY);
    }
}
