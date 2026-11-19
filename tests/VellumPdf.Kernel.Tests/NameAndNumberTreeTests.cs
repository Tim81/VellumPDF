// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.IO;
using VellumPdf.TestSupport;
using static VellumPdf.Kernel.Tests.TreeTestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #241: the name tree (ISO 32000-2, 7.9.6) and number tree (7.9.7) writers. Every structural test
/// serialises the tree into a PDF and walks it back through <c>VellumPdf.Reader</c>.
/// </summary>
public sealed class NameAndNumberTreeTests
{
    // The expected depth below the root for each size follows from PdfTreeNodes: up to 64 entries are
    // the root itself (depth 0); then leaves of at most 64 sit directly under the root while there are
    // at most 64 of them (depth 1); then one intermediate level more (depth 2).
    // 100 -> 2 leaves; 10,000 -> 157 leaves -> 3 parents; 100,000 -> 1,563 leaves -> 25 parents.
    public static TheoryData<int, int, int> Shapes => new()
    {
        { 0, 0, 1 },
        { 1, 0, 1 },
        { 2, 0, 1 },
        { 64, 0, 1 },
        { 65, 1, 2 },
        { 100, 1, 2 },
        { 4096, 1, 64 },
        { 4097, 2, 65 },
        { 10_000, 2, 157 },
        { 100_000, 2, 1_563 },
    };

    // Distinct pseudo-random byte keys of length 1 to 6, so prefixes of one another, the 0x80-0xFF
    // range and the empty key all occur.
    private static List<byte[]> RandomKeys(int count, int seed, bool includeEmpty)
    {
        var random = new Random(seed);
        var seen = new HashSet<string>();
        var keys = new List<byte[]>(count);
        if (includeEmpty && count > 0)
        {
            keys.Add([]);
            seen.Add("");
        }
        while (keys.Count < count)
        {
            var length = random.Next(1, 7);
            var key = new byte[length];
            random.NextBytes(key);
            if (seen.Add(Convert.ToHexString(key)))
                keys.Add(key);
        }
        return keys;
    }

    // qpdf 12.4.2 compares name-tree keys after converting them from PDFDocEncoding to UTF-8, not byte by
    // byte as 7.9.6 says. Measured: --check reports "keys are not sorted" for a tree whose two keys are
    // the single bytes 0x7F and 0x80, which is byte order, and for 20 of the 254 adjacent single-byte
    // pairs 0x01..0xFF (24, 25, 28, 29, 31, 127, 128, 131, 132, 133, 135, 138, 139, 140, 142, 148, 153,
    // 159, 160 and 173 as the lower byte). Printable ASCII is a range where both orders agree, so the
    // qpdf fixture stays inside it; the byte-order claim for the rest is pinned by the Reader walk above.
    private static List<byte[]> PrintableAsciiKeys(int count, int seed)
    {
        var random = new Random(seed);
        var seen = new HashSet<string>();
        var keys = new List<byte[]>(count);
        while (keys.Count < count)
        {
            var key = new byte[random.Next(1, 7)];
            for (var i = 0; i < key.Length; i++)
                key[i] = (byte)random.Next(0x20, 0x7F);
            if (seen.Add(Convert.ToHexString(key)))
                keys.Add(key);
        }
        return keys;
    }

    private static (List<KeyValuePair<byte[], PdfObject>> Entries, Dictionary<string, long> Expected) NameEntries(
        PdfObjectRegistry registry, List<byte[]> keys)
    {
        var entries = new List<KeyValuePair<byte[], PdfObject>>(keys.Count);
        var expected = new Dictionary<string, long>(keys.Count);
        for (var i = 0; i < keys.Count; i++)
        {
            // Two thirds of the values are indirect, to cover both forms 7.9.6 allows.
            PdfObject value = i % 3 == 0 ? new PdfInteger(i) : registry.Add(new PdfInteger(i));
            entries.Add(new(keys[i], value));
            expected[Convert.ToHexString(keys[i])] = i;
        }
        return (entries, expected);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void NameTree_roundTripsThroughReader_withLimitsAndDepthWithinBounds(int count, int expectedDepth, int expectedLeaves)
    {
        var registry = new PdfObjectRegistry();
        var keys = RandomKeys(count, seed: 241 + count, includeEmpty: true);
        var (entries, expected) = NameEntries(registry, keys);
        var objectsBefore = registry.ObjectCount;

        var root = NameTreeWriter.Write(entries, registry);
        var pdf = BuildPdf(registry, root, nameTree: true);
        var (reader, readRoot) = OpenRoot(pdf, nameTree: true);
        using var _ = reader;
        var walked = Walk(reader, readRoot, nameTree: true);

        Assert.Equal(count, walked.Entries.Count);
        var sorted = keys.ToArray();
        Array.Sort(sorted, (a, b) => Compare(a, b, nameTree: true));
        for (var i = 0; i < count; i++)
        {
            Assert.Equal(sorted[i], (byte[])walked.Entries[i].Key);
            Assert.Equal(expected[Convert.ToHexString(sorted[i])], ((PdfInteger)reader.ResolveValue(walked.Entries[i].Value)!).Value);
        }
        AssertShape(walked, count, expectedDepth, expectedLeaves);
        // Every node except the root is its own indirect object: the root is the one not registered.
        Assert.Equal(expectedDepth == 0 ? 0 : walked.Nodes - 1, registry.ObjectCount - objectsBefore - 3);
    }

    [Theory]
    [MemberData(nameof(Shapes))]
    public void NumberTree_roundTripsThroughReader_withLimitsAndDepthWithinBounds(int count, int expectedDepth, int expectedLeaves)
    {
        var registry = new PdfObjectRegistry();
        // Spread over the full 32-bit range, negative keys included, always containing both extremes.
        var random = new Random(7 + count);
        var seen = new HashSet<long>();
        var keys = new List<long>();
        if (count > 0) { keys.Add(int.MinValue); seen.Add(int.MinValue); }
        if (count > 1) { keys.Add(int.MaxValue); seen.Add(int.MaxValue); }
        while (keys.Count < count)
        {
            var k = random.Next(int.MinValue, int.MaxValue);
            if (seen.Add(k)) keys.Add(k);
        }
        var entries = keys.Select((k, i) => new KeyValuePair<long, PdfObject>(k, i % 3 == 0 ? new PdfInteger(i) : registry.Add(new PdfInteger(i)))).ToList();

        var root = NumberTreeWriter.Write(entries, registry);
        var pdf = BuildPdf(registry, root, nameTree: false);
        var (reader, readRoot) = OpenRoot(pdf, nameTree: false);
        using var _ = reader;
        var walked = Walk(reader, readRoot, nameTree: false);

        var sorted = keys.Order().ToArray();
        Assert.Equal(sorted, walked.Entries.Select(e => (long)e.Key).ToArray());
        var byKey = entries.ToDictionary(e => e.Key, e => e.Value);
        foreach (var (key, value) in walked.Entries)
            Assert.Equal(((PdfInteger)reader.ResolveValue(byKey[(long)key])!).Value, ((PdfInteger)reader.ResolveValue(value)!).Value);
        AssertShape(walked, count, expectedDepth, expectedLeaves);
    }

    private static void AssertShape(Walked walked, int count, int expectedDepth, int expectedLeaves)
    {
        Assert.Equal(expectedDepth, walked.MaxLeafDepth);
        Assert.Equal(expectedDepth, walked.MinLeafDepth); // every leaf at one depth
        Assert.Equal(expectedLeaves, walked.Leaves);
        Assert.True(walked.MaxPairsInLeaf <= PdfTreeNodes.MaxEntriesPerLeaf);
        Assert.True(walked.MaxKidsInNode <= PdfTreeNodes.MaxKidsPerNode);
        if (count > PdfTreeNodes.MaxEntriesPerLeaf)
        {
            // Spread evenly: no leaf smaller than the average by more than one entry, so no stub leaf.
            Assert.True(walked.MaxPairsInLeaf - (count / walked.Leaves) <= 1);
        }
    }

    [Fact]
    public void NameTree_empty_isARootWithAnEmptyNamesArray()
    {
        var registry = new PdfObjectRegistry();
        var root = NameTreeWriter.Write([], registry);
        Assert.Equal(0, registry.ObjectCount);
        Assert.Null(root.Get(new PdfName("Kids")));
        Assert.Null(root.Get(new PdfName("Limits")));
        Assert.Equal(0, Assert.IsType<PdfArray>(root.Get(new PdfName("Names"))).Count);

        var (reader, readRoot) = OpenRoot(BuildPdf(registry, root, nameTree: true), nameTree: true);
        using var _ = reader;
        Assert.Empty(Walk(reader, readRoot, nameTree: true).Entries);
    }

    [Fact]
    public void NumberTree_empty_isARootWithAnEmptyNumsArray()
    {
        var registry = new PdfObjectRegistry();
        var root = NumberTreeWriter.Write([], registry);
        Assert.Equal(0, registry.ObjectCount);
        Assert.Equal(0, Assert.IsType<PdfArray>(root.Get(new PdfName("Nums"))).Count);
        Assert.Null(root.Get(new PdfName("Kids")));
    }

    [Fact]
    public void NameTree_singleLeafAtTheThreshold_hasNoKidsAndNoLimits()
    {
        var registry = new PdfObjectRegistry();
        var entries = RandomKeys(PdfTreeNodes.MaxEntriesPerLeaf, 1, includeEmpty: false)
            .Select((k, i) => new KeyValuePair<byte[], PdfObject>(k, new PdfInteger(i))).ToList();
        var root = NameTreeWriter.Write(entries, registry);
        Assert.Null(root.Get(new PdfName("Kids")));
        Assert.Null(root.Get(new PdfName("Limits")));
        Assert.NotNull(root.Get(new PdfName("Names")));
        Assert.Equal(0, registry.ObjectCount);

        var over = RandomKeys(PdfTreeNodes.MaxEntriesPerLeaf + 1, 1, includeEmpty: false)
            .Select((k, i) => new KeyValuePair<byte[], PdfObject>(k, new PdfInteger(i))).ToList();
        var overRoot = NameTreeWriter.Write(over, registry);
        Assert.NotNull(overRoot.Get(new PdfName("Kids")));
        Assert.Null(overRoot.Get(new PdfName("Names")));
        Assert.Null(overRoot.Get(new PdfName("Limits")));
        Assert.Equal(2, registry.ObjectCount);
    }

    // ---- byte ordering ------------------------------------------------------------------------

    [Fact]
    public void NameTree_ordersKeysByUnsignedBytes_notByCultureOrUtf16()
    {
        byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
        var fullWidthTilde = Utf8("～");   // EF BD 9E
        var linearBSyllable = Utf8("\U00010000"); // F0 90 80 80
        // Expected order, spelled out: empty, then by first differing byte, a prefix before its extension.
        byte[][] expectedOrder =
        [
            [],
            Utf8("B"),            // 0x42 sorts before 0x61 although a culture comparison puts "a" first
            Utf8("a"),
            Utf8("ab"),           // "a" is a prefix of "ab"
            Utf8("b"),
            [0x80],
            Utf8("é"),       // C3 A9
            fullWidthTilde,       // EF ...
            linearBSyllable,      // F0 ... : after EF in UTF-8, before it in UTF-16 (D800 < FF5E)
            [0xFF],
        ];
        var registry = new PdfObjectRegistry();
        var shuffled = expectedOrder.Reverse().Select((k, i) => new KeyValuePair<byte[], PdfObject>(k, new PdfInteger(i))).ToList();

        var root = NameTreeWriter.Write(shuffled, registry);
        var names = Assert.IsType<PdfArray>(root.Get(new PdfName("Names")));
        var written = Enumerable.Range(0, names.Count / 2).Select(i => ((PdfLiteralString)names[2 * i]).Bytes.ToArray()).ToArray();
        Assert.Equal(expectedOrder, written);

        // The fixture only discriminates if the wrong comparers really would reorder it.
        var utf16 = expectedOrder.Select(Encoding.UTF8.GetString).ToArray();
        Assert.True(string.CompareOrdinal(utf16[7], utf16[8]) > 0, "UTF-16 code units put U+10000 before U+FF5E");
        Assert.True(string.Compare("a", "B", StringComparison.InvariantCulture) < 0, "a culture comparison puts a before B");
    }

    [Fact]
    public void NameTree_largerTree_limitsFollowByteOrderAtEveryNode()
    {
        // 300 keys drawn so that bytes at or above 0x80 and shared prefixes are common; the walker checks
        // each node's Limits against an independent byte comparison.
        var registry = new PdfObjectRegistry();
        var (entries, _) = NameEntries(registry, RandomKeys(300, seed: 99, includeEmpty: false));
        var root = NameTreeWriter.Write(entries, registry);
        var (reader, readRoot) = OpenRoot(BuildPdf(registry, root, nameTree: true), nameTree: true);
        using var _ = reader;
        var walked = Walk(reader, readRoot, nameTree: true);
        Assert.Equal(300, walked.Entries.Count);
    }

    [Fact]
    public void NameTree_keyAtTheMaximumLength_isWrittenAndOneLongerIsRefused()
    {
        var registry = new PdfObjectRegistry();
        var atLimit = new byte[NameTreeWriter.MaxKeyBytes];
        Array.Fill(atLimit, (byte)'x');
        var root = NameTreeWriter.Write([new(atLimit, new PdfInteger(1))], registry);
        Assert.Equal(NameTreeWriter.MaxKeyBytes, ((PdfLiteralString)((PdfArray)root.Get(new PdfName("Names"))!)[0]).Bytes.Length);

        var tooLong = new byte[NameTreeWriter.MaxKeyBytes + 1];
        var ex = Assert.Throws<ArgumentException>(() => NameTreeWriter.Write([new(tooLong, new PdfInteger(1))], registry));
        Assert.Contains("32768", ex.Message);
        Assert.Contains("32767", ex.Message);
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Fact]
    public void NameTree_duplicateKey_isRefused_evenWhenTheDuplicatesAreFarApart()
    {
        var registry = new PdfObjectRegistry();
        var entries = RandomKeys(500, 5, includeEmpty: false)
            .Select((k, i) => new KeyValuePair<byte[], PdfObject>(k, new PdfInteger(i))).ToList();
        entries.Add(new(entries[3].Key.ToArray(), new PdfInteger(-1)));
        var ex = Assert.Throws<ArgumentException>(() => NameTreeWriter.Write(entries, registry));
        Assert.Contains("Duplicate name-tree key", ex.Message);
        Assert.Contains(Convert.ToHexString(entries[3].Key), ex.Message);
        Assert.Equal(0, registry.ObjectCount); // nothing was registered before the refusal
    }

    [Fact]
    public void NameTree_duplicateEmptyKey_isRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => NameTreeWriter.Write(
            [new([], new PdfInteger(1)), new([], new PdfInteger(2))], new PdfObjectRegistry()));
        Assert.Contains("(empty key)", ex.Message);
    }

    [Fact]
    public void NumberTree_duplicateKey_isRefused()
    {
        var ex = Assert.Throws<ArgumentException>(() => NumberTreeWriter.Write(
            [new(5, new PdfInteger(1)), new(9, new PdfInteger(2)), new(5, new PdfInteger(3))], new PdfObjectRegistry()));
        Assert.Contains("Duplicate number-tree key 5", ex.Message);
    }

    [Fact]
    public void NameTree_nullKey_nullValue_nullArguments_areRefused()
    {
        var registry = new PdfObjectRegistry();
        Assert.Equal("entries", Assert.Throws<ArgumentNullException>(() => NameTreeWriter.Write([new(null!, new PdfInteger(1))], registry)).ParamName);
        Assert.Equal("entries", Assert.Throws<ArgumentNullException>(() => NameTreeWriter.Write([new([1], null!)], registry)).ParamName);
        Assert.Equal("entries", Assert.Throws<ArgumentNullException>(() => NameTreeWriter.Write(null!, registry)).ParamName);
        Assert.Equal("registry", Assert.Throws<ArgumentNullException>(() => NameTreeWriter.Write([], null!)).ParamName);
    }

    [Fact]
    public void NumberTree_nullValue_nullArguments_areRefused()
    {
        var registry = new PdfObjectRegistry();
        Assert.Equal("entries", Assert.Throws<ArgumentNullException>(() => NumberTreeWriter.Write([new(1, null!)], registry)).ParamName);
        Assert.Equal("entries", Assert.Throws<ArgumentNullException>(() => NumberTreeWriter.Write(null!, registry)).ParamName);
        Assert.Equal("registry", Assert.Throws<ArgumentNullException>(() => NumberTreeWriter.Write([], null!)).ParamName);
    }

    [Theory]
    [InlineData(int.MinValue, true)]
    [InlineData(int.MaxValue, true)]
    [InlineData(0, true)]
    [InlineData((long)int.MaxValue + 1, false)]
    [InlineData((long)int.MinValue - 1, false)]
    [InlineData(long.MaxValue, false)]
    [InlineData(long.MinValue, false)]
    public void NumberTree_keyRange_isThe32BitSignedRange(long key, bool accepted)
    {
        var registry = new PdfObjectRegistry();
        if (accepted)
        {
            var root = NumberTreeWriter.Write([new(key, new PdfInteger(1))], registry);
            Assert.Equal(key, ((PdfInteger)((PdfArray)root.Get(new PdfName("Nums"))!)[0]).Value);
        }
        else
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() => NumberTreeWriter.Write([new(key, new PdfInteger(1))], registry));
            Assert.Equal(key, ex.ActualValue);
        }
    }

    [Fact]
    public void Trees_refuseADirectStream_andAnIndirectObjectWrapper()
    {
        var registry = new PdfObjectRegistry();
        var stream = new PdfStream([1, 2, 3]);
        Assert.Contains("indirect reference", Assert.Throws<ArgumentException>(() => NameTreeWriter.Write([new([1], stream)], registry)).Message);
        Assert.Contains("indirect reference", Assert.Throws<ArgumentException>(() => NumberTreeWriter.Write([new(1, stream)], registry)).Message);
        var wrapper = new PdfIndirectObject(7, new PdfInteger(1));
        Assert.Contains("wrapper", Assert.Throws<ArgumentException>(() => NameTreeWriter.Write([new([1], wrapper)], registry)).Message);

        // The reference to the same stream is what is allowed.
        var root = NameTreeWriter.Write([new([1], registry.Add(stream))], registry);
        Assert.IsType<PdfIndirectReference>(((PdfArray)root.Get(new PdfName("Names"))!)[1]);
    }

    [Fact]
    public void Trees_refuseAStreamOrWrapperNestedInADirectArrayOrDictionary()
    {
        var registry = new PdfObjectRegistry();
        var stream = new PdfStream([1, 2, 3]);
        var wrapper = new PdfIndirectObject(7, new PdfInteger(1));
        PdfObject[] nested =
        [
            new PdfArray([new PdfInteger(1), stream]),
            new PdfDictionary().Set(new PdfName("S"), stream),
            new PdfArray([new PdfDictionary().Set(new PdfName("D"), new PdfArray([stream]))]),
            new PdfArray([wrapper]),
        ];
        foreach (var value in nested)
        {
            Assert.Throws<ArgumentException>(() => NameTreeWriter.Write([new([1], value)], registry));
            Assert.Throws<ArgumentException>(() => NumberTreeWriter.Write([new(1, value)], registry));
        }

        // A reference to the stream, at any depth, is what is allowed.
        var reference = registry.Add(stream);
        var ok = new PdfArray([new PdfDictionary().Set(new PdfName("D"), new PdfArray([reference]))]);
        NameTreeWriter.Write([new([1], ok)], registry);

        // A direct array that contains itself is visited once rather than looped on.
        var cycle = new PdfArray();
        cycle.Add(cycle);
        NameTreeWriter.Write([new([1], cycle)], registry);
    }

    [Fact]
    public void NameTree_doesNotAliasTheCallersKeyArray()
    {
        var key = new byte[] { 1, 2, 3 };
        var root = NameTreeWriter.Write([new(key, new PdfInteger(1))], new PdfObjectRegistry());
        key[0] = 9;
        Assert.Equal(new byte[] { 1, 2, 3 }, ((PdfLiteralString)((PdfArray)root.Get(new PdfName("Names"))!)[0]).Bytes.ToArray());
    }

    // ---- cost ---------------------------------------------------------------------------------

    private static long AllocatedBytesPerEntry(int count)
    {
        var keys = RandomKeys(count, seed: count, includeEmpty: false);
        var entries = keys.Select((k, i) => new KeyValuePair<byte[], PdfObject>(k, new PdfInteger(i))).ToList();
        NameTreeWriter.Write(entries, new PdfObjectRegistry()); // warm the JIT so its allocations are not measured
        var registry = new PdfObjectRegistry();
        var before = GC.GetAllocatedBytesForCurrentThread();
        NameTreeWriter.Write(entries, registry);
        return (GC.GetAllocatedBytesForCurrentThread() - before) / count;
    }

    [Fact]
    public void NameTree_allocationPerEntry_isRoughlyConstantFrom1000To100000()
    {
        var small = AllocatedBytesPerEntry(1_000);
        var mid = AllocatedBytesPerEntry(10_000);
        var large = AllocatedBytesPerEntry(100_000);
        // Measured in Release on net10.0 and net11.0, both: 171, 189 and 178 bytes per entry at 1,000,
        // 10,000 and 100,000 entries. A step that re-copied arrays per node, or a sort that allocated per
        // comparison, would grow the large figure with the count; the 1.5 factor leaves room for list
        // doubling, whose amortised cost is constant but whose last step lands at an arbitrary point.
        Assert.True(large <= small * 3 / 2 + 8, $"bytes per entry: 1,000 -> {small}, 10,000 -> {mid}, 100,000 -> {large}");
        Assert.True(mid <= small * 3 / 2 + 8, $"bytes per entry: 1,000 -> {small}, 10,000 -> {mid}, 100,000 -> {large}");
        Assert.True(large < 300, $"bytes per entry: 1,000 -> {small}, 10,000 -> {mid}, 100,000 -> {large}");
    }

    // ---- oracle -------------------------------------------------------------------------------

    private static string WriteTemp(byte[] pdf, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vellum_tree_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, pdf);
        return path;
    }

    // qpdf's tree helpers validate values with a check of their own: a name tree of destinations wants an
    // array (or a dictionary with /D) and a number tree of page labels wants a dictionary, and qpdf
    // reports an integer value as "item N is invalid" on --check. 7.9.6 and 7.9.7 allow any object, so the
    // fixture takes the shape each tree is used for rather than the shape the clause would permit.
    private static PdfObject OracleValue(int i, bool nameTree) =>
        nameTree
            ? new PdfArray([new PdfInteger(i), new PdfName("Fit")])
            : new PdfDictionary().Set(new PdfName("St"), i + 1);

    [Theory]
    [InlineData(100, true)]
    [InlineData(10_000, true)]
    [InlineData(100, false)]
    [InlineData(10_000, false)]
    public void Tree_qpdfCheckIsClean_andJsonShowsTheExpectedStructure(int count, bool nameTree)
    {
        var registry = new PdfObjectRegistry();
        PdfDictionary root;
        if (nameTree)
        {
            var entries = PrintableAsciiKeys(count, 11).Select((k, i) => new KeyValuePair<byte[], PdfObject>(k, i % 2 == 0 ? OracleValue(i, nameTree: true) : registry.Add(OracleValue(i, nameTree: true)))).ToList();
            root = NameTreeWriter.Write(entries, registry);
        }
        else
        {
            var entries = Enumerable.Range(0, count).Select(i => new KeyValuePair<long, PdfObject>(i * 3L - 50, i % 2 == 0 ? OracleValue(i, nameTree: false) : registry.Add(OracleValue(i, nameTree: false)))).ToList();
            root = NumberTreeWriter.Write(entries, registry);
        }
        var pdf = BuildPdf(registry, root, nameTree);
        var path = WriteTemp(pdf, "tree.pdf");
        try
        {
            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout);

            ExternalTool.TryRun("qpdf", ["--json=2", path], out exit, out stdout, out stderr, out timedOut, timeoutMs: 60_000);
            Assert.False(timedOut);
            Assert.Equal(0, exit);
            AssertQpdfJsonStructure(stdout, count, nameTree);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    // Walks qpdf's own parse (--json=2) rather than ours: the catalog's tree root, its kids and its leaves.
    private static void AssertQpdfJsonStructure(string json, int count, bool nameTree)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var objects = doc.RootElement.GetProperty("qpdf")[1];
        System.Text.Json.JsonElement Obj(string reference) => objects.GetProperty("obj:" + reference).GetProperty("value");

        var trailer = objects.GetProperty("trailer").GetProperty("value");
        var catalog = Obj(trailer.GetProperty("/Root").GetString()!);
        var rootNode = nameTree
            ? catalog.GetProperty("/Names").GetProperty("/Dests")
            : catalog.GetProperty("/PageLabels");

        Assert.False(rootNode.TryGetProperty("/Limits", out _), "qpdf sees no /Limits on the root");
        Assert.True(rootNode.TryGetProperty("/Kids", out var kids), "qpdf sees /Kids on the root");
        Assert.False(rootNode.TryGetProperty(nameTree ? "/Names" : "/Nums", out _), "qpdf sees no entries array beside /Kids on the root");

        var leaves = 0;
        var pairs = 0;
        foreach (var kid in kids.EnumerateArray())
        {
            var node = Obj(kid.GetString()!);
            Assert.True(node.TryGetProperty("/Limits", out var limits), "every non-root node has /Limits");
            Assert.Equal(2, limits.GetArrayLength());
            Assert.False(node.TryGetProperty("/Kids", out _) && node.TryGetProperty(nameTree ? "/Names" : "/Nums", out _));
            if (node.TryGetProperty(nameTree ? "/Names" : "/Nums", out var entries))
            {
                leaves++;
                pairs += entries.GetArrayLength() / 2;
            }
        }
        // 100 entries and 10,000 entries both fit a root of leaves (2 and 157 leaves) or one level of parents.
        if (count == 100)
        {
            Assert.Equal(2, kids.GetArrayLength());
            Assert.Equal(2, leaves);
            Assert.Equal(100, pairs);
        }
        else
        {
            Assert.Equal(3, kids.GetArrayLength()); // 157 leaves grouped under 3 intermediate nodes
            Assert.Equal(0, leaves);
        }
    }
}
