// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;
using VellumPdf.TestSupport;
using static VellumPdf.Kernel.Tests.TreeTestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #262: the name rule, the named destination table and its <c>/Dests</c> name tree (ISO 32000-2,
/// 12.3.2.4 and 7.9.6). Exact bytes are asserted as written; structure is read back through
/// <c>VellumPdf.Reader</c>.
/// </summary>
public sealed class NamedDestinationTableTests
{
    private static string Text(PdfObject value)
    {
        using var ms = new MemoryStream();
        value.WriteTo(new PdfWriter(ms));
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static string ObjectText(PdfObjectRegistry registry, int objectNumber) =>
        Text(registry.Entries().Single(e => e.ObjectNumber == objectNumber).Value);

    // A reserved slot with a placeholder value, so ObjectText can enumerate the registry.
    private static PdfIndirectReference ReservePage(PdfObjectRegistry registry)
    {
        var reference = registry.Reserve();
        registry.SetValue(reference, new PdfDictionary());
        return reference;
    }

    private static PdfPage NewPage() => new(new PdfRectangle(0, 0, 200, 200));

    // ---- the name rule ------------------------------------------------------------------------

    [Theory]
    [InlineData("chap-1")]
    [InlineData("a")]
    [InlineData("A.b_c-d:e")]
    [InlineData("0")]
    public void Name_accepted_encodesAsTheAsciiBytes(string name)
    {
        DestinationName.Validate(name, "p");
        Assert.Equal(Encoding.ASCII.GetBytes(name), DestinationName.Encode(name));
    }

    [Fact]
    public void Name_of127Characters_isAccepted()
    {
        var name = new string('x', 127);
        DestinationName.Validate(name, "p");
        Assert.Equal(127, DestinationName.Encode(name).Length);
    }

    [Fact]
    public void Name_null_isRefusedWithTheParameterName()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => DestinationName.Validate(null, "label"));
        Assert.Equal("label", ex.ParamName);
        Assert.Throws<ArgumentNullException>(() => DestinationName.Encode(null!));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("a b", "index 1 is U+0020")]
    [InlineData("é", "index 0 is U+00E9")]
    [InlineData("a#b", "index 1 is U+0023")]
    [InlineData("a/b", "index 1 is U+002F")]
    [InlineData("a(b", "index 1 is U+0028")]
    [InlineData("a)b", "index 1 is U+0029")]
    [InlineData("a%b", "index 1 is U+0025")]
    [InlineData("a<b", "index 1 is U+003C")]
    [InlineData("a\U0001F600", "index 1 is U+1F600")]
    public void Name_breakingTheRule_isRefused_andTheMessageNamesTheDefect(string name, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => DestinationName.Validate(name, "p"));
        Assert.IsNotType<ArgumentNullException>(ex);
        Assert.Equal("p", ex.ParamName);
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Name_withAnUnpairedSurrogate_namesTheCodeUnit()
    {
        // Built at run time: a lone surrogate in an attribute argument does not survive test
        // discovery.
        var high = Assert.Throws<ArgumentException>(() => DestinationName.Validate("ab" + (char)0xD800, "p"));
        Assert.Contains("index 2 is U+D800", high.Message);
        var low = Assert.Throws<ArgumentException>(() => DestinationName.Validate("a" + (char)0xDC00 + (char)0xD800, "p"));
        Assert.Contains("index 1 is U+DC00", low.Message);
    }

    [Fact]
    public void Name_of128Characters_isRefused_withTheLength_andWithoutQuotingIt()
    {
        var ex = Assert.Throws<ArgumentException>(() => DestinationName.Validate(new string('x', 128), "p"));
        Assert.Contains("128 characters", ex.Message);
        Assert.DoesNotContain(new string('x', 33), ex.Message);
    }

    [Fact]
    public void Name_tooLong_isReportedAsTooLong_evenWhenItAlsoHoldsABadCharacter()
    {
        var ex = Assert.Throws<ArgumentException>(() => DestinationName.Validate(new string('é', 200), "p"));
        Assert.Contains("200 characters", ex.Message);
        Assert.DoesNotContain("U+00E9", ex.Message);
    }

    // ---- Add ----------------------------------------------------------------------------------

    [Fact]
    public void Add_refusesABadName_aNullPage_andADuplicate_withoutChangingTheTable()
    {
        var table = new NamedDestinationTable();
        var page = NewPage();
        table.Add("a", page, 0, 0);

        Assert.Throws<ArgumentNullException>(() => table.Add(null!, page, 0, 0));
        Assert.Throws<ArgumentException>(() => table.Add("a b", page, 0, 0));
        var nullPage = Assert.Throws<ArgumentNullException>(() => table.Add("b", null!, 0, 0));
        Assert.Equal("page", nullPage.ParamName);
        var duplicate = Assert.Throws<ArgumentException>(() => table.Add("a", page, 1, 1));
        Assert.Contains("'a'", duplicate.Message);
        Assert.Equal("name", duplicate.ParamName);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Add_checksInTheOrderNamePageLeftTopDuplicate()
    {
        var table = new NamedDestinationTable();
        var page = NewPage();
        table.Add("a", page, 0, 0);

        // Both coordinates bad: left is reported.
        var both = Assert.Throws<ArgumentOutOfRangeException>(() => table.Add("b", page, double.NaN, double.NaN));
        Assert.Equal("left", both.ParamName);
        // A duplicate name with a bad coordinate: the coordinate is reported, not the duplicate.
        var duplicate = Assert.Throws<ArgumentOutOfRangeException>(() => table.Add("a", page, double.NaN, 0));
        Assert.Equal("left", duplicate.ParamName);
        // A bad name with a null page: the name is reported (ArgumentException, not
        // ArgumentNullException).
        var name = Assert.Throws<ArgumentException>(() => table.Add("a b", null!, 0, 0));
        Assert.Equal("name", name.ParamName);
        Assert.Equal(1, table.Count);
    }

    [Theory]
    [InlineData(double.NaN, 0.0, "left")]
    [InlineData(double.PositiveInfinity, 0.0, "left")]
    [InlineData(double.NegativeInfinity, 0.0, "left")]
    [InlineData(3.404e38, 0.0, "left")]
    [InlineData(-3.404e38, 0.0, "left")]
    [InlineData(0.0, double.NaN, "top")]
    [InlineData(0.0, double.PositiveInfinity, "top")]
    [InlineData(0.0, double.NegativeInfinity, "top")]
    [InlineData(0.0, 3.404e38, "top")]
    [InlineData(0.0, -3.404e38, "top")]
    public void Add_refusesACoordinateThatIsNotAFiniteReal(double left, double top, string paramName)
    {
        var table = new NamedDestinationTable();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => table.Add("a", NewPage(), left, top));
        Assert.Equal(paramName, ex.ParamName);
        Assert.Equal(0, table.Count);
    }

    [Theory]
    [InlineData(2147483647.0)]
    [InlineData(-2147483647.0)]
    [InlineData(2147483648.0)]
    [InlineData(-2147483648.0)]
    [InlineData(3.403e38)]
    [InlineData(-3.403e38)]
    public void Add_acceptsEveryValueUpToTheRealRangeOfTheStandard(double value)
    {
        var table = new NamedDestinationTable();
        table.Add("a", NewPage(), value, value);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Build_writesTheBoundaryCoordinatesAsIntegerTokensInRange()
    {
        // PdfReal writes a whole value with no decimal point, so the file holds an integer token,
        // and Table C.1 of ISO 32000-1 stops an integer at 2,147,483,647.
        var registry = new PdfObjectRegistry();
        var page = NewPage();
        var pageRef = ReservePage(registry);
        var table = new NamedDestinationTable();
        table.Add("a", page, 2147483647.0, -2147483647.0);

        table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef });

        Assert.Equal("[1 0 R /XYZ 2147483647 -2147483647 null]", ObjectText(registry, 2));
    }

    [Fact]
    public void Build_writesAWholeValueOutsideTheIntegerRangeAsARealToken()
    {
        var registry = new PdfObjectRegistry();
        var page = NewPage();
        var pageRef = ReservePage(registry);
        var table = new NamedDestinationTable();
        table.Add("a", page, 2147483648.0, -2147483648.0);

        table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef });

        Assert.Equal("[1 0 R /XYZ 2147483648.0 -2147483648.0 null]", ObjectText(registry, 2));
    }

    // ---- exact bytes --------------------------------------------------------------------------

    [Fact]
    public void Build_oneDestination_writesTheRootAndAnIndirectXyzArray()
    {
        var registry = new PdfObjectRegistry();
        var page = NewPage();
        var pageRef = ReservePage(registry);
        var table = new NamedDestinationTable();
        table.Add("chap-1", page, 0, 150);

        var root = table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef });

        Assert.NotNull(root);
        Assert.Equal("<<\n/Names [(chap-1) 2 0 R]\n>>", Text(root));
        Assert.Equal("[1 0 R /XYZ 0 150 null]", ObjectText(registry, 2));
        Assert.Equal(2, registry.ObjectCount);
    }

    [Fact]
    public void Build_writesFractionalCoordinatesAsReals()
    {
        var registry = new PdfObjectRegistry();
        var page = NewPage();
        var pageRef = ReservePage(registry);
        var table = new NamedDestinationTable();
        table.Add("a", page, 36.5, -12.25);

        table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef });

        Assert.Equal("[1 0 R /XYZ 36.5 -12.25 null]", ObjectText(registry, 2));
    }

    [Fact]
    public void Build_registersTheArraysInNameOrder_whateverTheOrderTheyWereAddedIn()
    {
        var registry = new PdfObjectRegistry();
        var p1 = NewPage();
        var p2 = NewPage();
        var r1 = ReservePage(registry);
        var r2 = ReservePage(registry);
        var table = new NamedDestinationTable();
        table.Add("b", p2, 2, 2);
        table.Add("a", p1, 1, 1);
        table.Add("B", p1, 3, 3);

        var root = table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [p1] = r1, [p2] = r2 });

        // Ordinal: "B" (0x42) sorts before "a" (0x61) before "b" (0x62).
        Assert.Equal("[1 0 R /XYZ 3 3 null]", ObjectText(registry, 3));
        Assert.Equal("[1 0 R /XYZ 1 1 null]", ObjectText(registry, 4));
        Assert.Equal("[2 0 R /XYZ 2 2 null]", ObjectText(registry, 5));
        Assert.Equal("<<\n/Names [(B) 3 0 R (a) 4 0 R (b) 5 0 R]\n>>", Text(root!));
    }

    [Fact]
    public void Build_valuesAreIndirect_soADirectValueWouldChangeTheRoot()
    {
        var registry = new PdfObjectRegistry();
        var page = NewPage();
        var pageRef = registry.Reserve();
        var table = new NamedDestinationTable();
        table.Add("a", page, 1, 2);
        var root = table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef })!;

        var names = Assert.IsType<PdfArray>(root.Get(new PdfName("Names")));
        Assert.IsType<PdfIndirectReference>(names[1]);
    }

    // ---- sizes --------------------------------------------------------------------------------

    private static NamedDestinationTable Fill(int count, PdfPage page)
    {
        var table = new NamedDestinationTable();
        for (var i = 0; i < count; i++)
            table.Add($"d{i:D6}", page, i, i + 0.5);
        return table;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(64, 64)]
    [InlineData(65, 67)]
    [InlineData(10_000, 10_160)]
    public void Build_addsOneArrayPerDestinationAndTheTreeNodes(int count, int expectedAdded)
    {
        var registry = new PdfObjectRegistry();
        var page = NewPage();
        var pageRef = registry.Reserve();
        var before = registry.ObjectCount;

        var root = Fill(count, page).Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef });

        Assert.Equal(expectedAdded, registry.ObjectCount - before);
        if (count == 0)
        {
            Assert.Null(root);
            return;
        }
        var kids = root!.Get(new PdfName("Kids")) as PdfArray;
        switch (count)
        {
            case 1:
            case 64:
                Assert.Null(kids);
                Assert.Equal(count * 2, Assert.IsType<PdfArray>(root.Get(new PdfName("Names"))).Count);
                break;
            case 65:
                Assert.Equal(2, kids!.Count);
                break;
            case 10_000:
                Assert.Equal(3, kids!.Count);
                break;
        }
    }

    [Fact]
    public void Build_missingPage_isRefused_namingTheDestination_andRegistersNothing()
    {
        var registry = new PdfObjectRegistry();
        var known = NewPage();
        var foreign = NewPage();
        var knownRef = registry.Reserve();
        var table = new NamedDestinationTable();
        table.Add("aaa", known, 0, 0);
        table.Add("zzz", foreign, 0, 0);
        var before = registry.ObjectCount;

        var ex = Assert.Throws<InvalidOperationException>(() => table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [known] = knownRef }));

        Assert.Contains("'zzz'", ex.Message);
        Assert.Contains("does not belong to this document", ex.Message);
        Assert.Equal(before, registry.ObjectCount);
    }

    [Fact]
    public void Build_nullArguments_areRefused()
    {
        var table = new NamedDestinationTable();
        var pages = new Dictionary<PdfPage, PdfIndirectReference>();
        Assert.Equal("registry", Assert.Throws<ArgumentNullException>(() => table.Build(null!, pages)).ParamName);
        Assert.Equal("pageRefs", Assert.Throws<ArgumentNullException>(() => table.Build(new PdfObjectRegistry(), null!)).ParamName);
    }

    [Fact]
    public void Build_twice_buildsTheSameObjectsAgain_andLeavesTheTableAlone()
    {
        var page = NewPage();
        var table = new NamedDestinationTable();
        table.Add("a", page, 1, 2);
        table.Add("b", page, 3, 4);

        string Run()
        {
            var registry = new PdfObjectRegistry();
            var pageRef = ReservePage(registry);
            var root = table.Build(registry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef })!;
            return Text(root) + "|" + ObjectText(registry, 2) + "|" + ObjectText(registry, 3);
        }

        Assert.Equal(Run(), Run());
        Assert.Equal(2, table.Count);
    }

    // ---- read back through the reader ---------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(1_000)]
    public void Build_roundTripsThroughReader_everyNameKeepsItsPageAndPosition(int count)
    {
        var registry = new PdfObjectRegistry();
        var pages = new[] { NewPage(), NewPage(), NewPage() };
        var pageRefs = pages.Select(_ => registry.Reserve()).ToArray();
        var pageRefMap = pages.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => pageRefs[x.i]);

        var table = new NamedDestinationTable();
        var expected = new List<(string Name, int Page, double Left, double Top)>();
        for (var i = 0; i < count; i++)
        {
            var name = $"dest.{i * 7919 % 100003:D6}";
            table.Add(name, pages[i % 3], i, i + 0.5);
            expected.Add((name, i % 3, i, i + 0.5));
        }
        expected.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        var root = table.Build(registry, pageRefMap)!;
        var pdf = BuildPdfWithPages(registry, pageRefs, root, labelsRoot: null);
        var (reader, readRoot) = OpenRoot(pdf, nameTree: true);
        using var _ = reader;
        var walked = Walk(reader, readRoot, nameTree: true);

        Assert.Equal(count, walked.Entries.Count);
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(Encoding.ASCII.GetBytes(expected[i].Name), (byte[])walked.Entries[i].Key);
            var value = Assert.IsType<PdfArray>(reader.ResolveValue(walked.Entries[i].Value));
            Assert.Equal(5, value.Count);
            Assert.Equal(pageRefs[expected[i].Page].ObjectNumber, Assert.IsType<PdfIndirectReference>(value[0]).ObjectNumber);
            Assert.Equal("XYZ", Assert.IsType<PdfName>(value[1]).Value);
            Assert.Equal(expected[i].Left, AsDouble(value[2]));
            Assert.Equal(expected[i].Top, AsDouble(value[3]));
            Assert.IsType<PdfNull>(value[4]);
        }
    }

    private static double AsDouble(PdfObject value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => throw new InvalidOperationException(value.GetType().Name),
    };

    // ---- oracles ------------------------------------------------------------------------------

    [Fact]
    public void Build_qpdfCheckIsClean_andPdfinfoListsEachNameWithItsPage()
    {
        var registry = new PdfObjectRegistry();
        var pages = new[] { NewPage(), NewPage(), NewPage() };
        var pageRefs = pages.Select(_ => registry.Reserve()).ToArray();
        var table = new NamedDestinationTable();
        table.Add("intro", pages[0], 10, 700);
        table.Add("chap-2", pages[1], 20, 600);
        table.Add("A.end:3", pages[2], 30, 500);
        var root = table.Build(registry, pages.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => pageRefs[x.i]))!;
        var pdf = BuildPdfWithPages(registry, pageRefs, root, labelsRoot: null);

        var dir = Path.Combine(Path.GetTempPath(), "vellum_dests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "dests.pdf");
            File.WriteAllBytes(path, pdf);

            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout);

            if (!ExternalTool.TryRun("pdfinfo", ["-dests", path], out exit, out stdout, out stderr, out timedOut))
                OracleGate.Unavailable("pdfinfo");
            Assert.False(timedOut);
            Assert.True(exit == 0, $"pdfinfo -dests exit {exit}\n{stdout}\n{stderr}");
            var lines = stdout.Split('\n', StringSplitOptions.TrimEntries);
            Assert.True(lines.Any(l => l.StartsWith("1 ", StringComparison.Ordinal) && l.Contains("intro", StringComparison.Ordinal)), stdout);
            Assert.True(lines.Any(l => l.StartsWith("2 ", StringComparison.Ordinal) && l.Contains("chap-2", StringComparison.Ordinal)), stdout);
            Assert.True(lines.Any(l => l.StartsWith("3 ", StringComparison.Ordinal) && l.Contains("A.end:3", StringComparison.Ordinal)), stdout);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- cost ---------------------------------------------------------------------------------

    private static long AllocatedBytesPerDestination(int count)
    {
        var page = NewPage();
        var names = Enumerable.Range(0, count).Select(i => $"d{i:D7}").ToArray();
        NamedDestinationTable Make()
        {
            var table = new NamedDestinationTable();
            foreach (var name in names)
                table.Add(name, page, 1, 2);
            return table;
        }

        // Warm the JIT so its allocations are not measured.
        var warmRegistry = new PdfObjectRegistry();
        Make().Build(warmRegistry, new Dictionary<PdfPage, PdfIndirectReference> { [page] = warmRegistry.Reserve() });

        var registry = new PdfObjectRegistry();
        var pageRef = registry.Reserve();
        var pageRefs = new Dictionary<PdfPage, PdfIndirectReference> { [page] = pageRef };
        var before = GC.GetAllocatedBytesForCurrentThread();
        Make().Build(registry, pageRefs);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / count;
    }

    [Fact]
    public void AddAndBuild_allocationPerDestination_isRoughlyConstantFrom1000To100000()
    {
        var small = AllocatedBytesPerDestination(1_000);
        var mid = AllocatedBytesPerDestination(10_000);
        var large = AllocatedBytesPerDestination(100_000);
        var message = $"bytes per destination: 1,000 -> {small}, 10,000 -> {mid}, 100,000 -> {large}";
        // Measured in Release on net10.0 and net11.0, both: 707, 723 and 692 bytes per destination
        // at 1,000, 10,000 and 100,000. A step that re-copied per node, or a name lookup that grew
        // with the table, would make the large figure grow with the count; the factor leaves room
        // for the list and dictionary doubling.
        Assert.True(large <= small * 3 / 2 + 8, message);
        Assert.True(mid <= small * 3 / 2 + 8, message);
    }
}
