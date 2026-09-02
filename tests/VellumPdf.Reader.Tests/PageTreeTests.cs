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
/// Most fixtures are hand-built byte strings rather than <see cref="PdfDocument"/> output — the
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
    /// literal <c>"N 0 R"</c> text — this helper only lays the objects out, records their offsets,
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
            // A number with no supplied body is left free — a dangling reference to it resolves to
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
            .Select(i => Assert.IsType<PdfIndirectReference>(kids[i]).ObjectNumber);
        Assert.Equal(expected, reader.Pages.Select(p => p.ObjectNumber));
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
    }

    // ── 5. Forged /Parent ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ForgedParent_isIgnored_inheritanceFollowsTheRealAncestorChain()
    {
        // Object 3's /Parent points at object 4 (not its real ancestor), which carries a
        // different /MediaBox — the walk must not follow it.
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

        // Not a hard budget assertion (machine-dependent) — a generous ceiling that fails loudly if
        // the walk regresses to something quadratic in leaf count.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(20),
            $"Walking {declaredLeaves} declared leaves took {stopwatch.Elapsed}, expected a few seconds at most.");
    }

    /// <summary>
    /// Builds a flat page tree with <paramref name="leafCount"/> minimal <c>/Type /Page</c> leaves
    /// sharing one inherited <c>/MediaBox</c> on the root, entirely through string building — going
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

    [Theory]
    [InlineData("450", 90)]
    [InlineData("-90", 270)]
    [InlineData("90.0", 90)] // integer-valued real: accepted (see PageTreeWalker.NormalizeRotate)
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
