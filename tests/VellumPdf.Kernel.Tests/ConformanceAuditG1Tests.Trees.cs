// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;
using static VellumPdf.Kernel.Tests.ConformanceAuditG1Support;

namespace VellumPdf.Kernel.Tests;

// Name trees, number trees, named destinations and page labels (ISO 32000-1, 7.9.6, 7.9.7, 12.3.2,
// 12.4.2; ISO 32000-2 has the same rules at the same clause numbers except where a test says so).
public sealed partial class ConformanceAuditG1Tests
{
    private static string DestinationName(int i) => "d" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture);

    private static byte[] DocumentWithDestinations(IEnumerable<string> names, int pages = 1)
    {
        using var doc = new PdfDocument { Timestamp = Pinned };
        doc.Info.Title = "G1";
        var list = Enumerable.Range(0, pages).Select(_ => doc.AddPage(new PdfRectangle(0, 0, 200, 200))).ToArray();
        foreach (var name in names)
            doc.AddNamedDestination(name, list[0], 0, 150);
        return Save(doc);
    }

    // ── 7.9.6 Name trees ────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(100)]
    [InlineData(10_000)]
    public void Iso32000_1_7_9_6_NameTreeNodesFollowTable36(int count)
    {
        var names = Enumerable.Range(0, count).Select(DestinationName).ToList();
        using var pdf = new Pdf(DocumentWithDestinations(names));

        // The construct is present: the Dests entry of the name dictionary is a tree of this size.
        Assert.NotNull(pdf.NameDictionary);
        Assert.NotNull(pdf.NameDictionary!.Get(N("Dests")));
        var tree = DestsTree(pdf);
        Assert.Equal(count, tree.Entries.Count);

        // The walk has asserted: one of Kids or Names on the root, no Limits there, Limits on every
        // other node and equal to the keys found below it, kids as indirect references, strictly
        // ascending keys across the whole tree. Two rules remain that a walk cannot see by itself.
        Assert.Equal(tree.RootHasEntries, tree.Nodes == 1); // a root with Names "shall be the only node"
        Assert.False(tree.RootHasLimits);
        Assert.Equal(names, tree.Entries.Select(e => Encoding.ASCII.GetString(e.NameKey!)));
        if (count > 64)
            Assert.True(tree.RootHasKids);
    }

    [Fact]
    public void Iso32000_1_7_9_6_KeysAreOrderedByteByByteAndAPrefixSortsFirst()
    {
        // Inserted in an order that is neither ascending nor descending.
        string[] scrambled =
        [
            "a_", "Z", "a0", "Chapter-2", "a", "_", "A1", "a-", "9", "Chapter-10", ":", "A", "a:", "Chapter-1", ".", "-", "0", "a.",
        ];
        string[] expected =
        [
            "-", ".", "0", "9", ":", "A", "A1", "Chapter-1", "Chapter-10", "Chapter-2", "Z", "_", "a", "a-", "a.", "a0", "a:", "a_",
        ];

        using var pdf = new Pdf(DocumentWithDestinations(scrambled));
        var tree = DestsTree(pdf);

        Assert.Equal(scrambled.Length, tree.Entries.Count);
        Assert.Equal(expected, tree.Entries.Select(e => Encoding.ASCII.GetString(e.NameKey!)));
    }

    [Fact]
    public void Iso32000_1_7_9_6_KeysAreComparedAsUnsignedBytes()
    {
        // The public name rule is ASCII, so bytes above 0x7F reach a tree only through the internal
        // writer. 7.9.6 allows any self-consistent encoding and compares keys byte by byte: U+FF5E
        // encodes to EF BD 9E and U+10000 to F0 90 80 80 in UTF-8, so a comparison of UTF-16 units or a
        // signed comparison of bytes orders them differently.
        byte[][] ascending =
        [
            [], [0x00], [0x7F], [0x80], [0xEF], [0xEF, 0xBD], [0xEF, 0xBD, 0x9E], [0xEF, 0xBD, 0x9E, 0x00], [0xF0, 0x90, 0x80, 0x80], [0xFF],
        ];
        var scrambled = new[] { 9, 3, 0, 6, 8, 1, 5, 2, 7, 4 };
        var registry = new PdfObjectRegistry();
        var entries = scrambled.Select(i => new KeyValuePair<byte[], PdfObject>(ascending[i], new PdfInteger(i))).ToList();
        var root = NameTreeWriter.Write(entries, registry);
        using var pdf = new Pdf(TreeTestSupport.BuildPdf(registry, root, nameTree: true));

        var tree = Walk(pdf, pdf.Dict(pdf.Dict(pdf.Catalog.Get(N("Names"))).Get(N("Dests"))), nameTree: true);

        Assert.Equal(10, tree.Entries.Count);
        for (var i = 0; i < ascending.Length; i++)
            Assert.Equal(ascending[i], tree.Entries[i].NameKey);
    }

    [Fact]
    public void Iso32000_1_7_9_6_LimitsAreTheLeastAndGreatestKeysWhenKeysUseTheHighBytes()
    {
        // 300 keys of random bytes, so that the tree has intermediate nodes and every Limits entry is
        // a pair of keys that differ in the high bit. The walk compares each Limits entry with the
        // keys it found below the node.
        var random = new Random(32000);
        var keys = new HashSet<string>();
        var entries = new List<KeyValuePair<byte[], PdfObject>>();
        while (entries.Count < 300)
        {
            var key = new byte[random.Next(0, 6)];
            random.NextBytes(key);
            if (keys.Add(Convert.ToHexString(key)))
                entries.Add(new KeyValuePair<byte[], PdfObject>(key, new PdfInteger(entries.Count)));
        }

        var registry = new PdfObjectRegistry();
        var root = NameTreeWriter.Write(entries, registry);
        using var pdf = new Pdf(TreeTestSupport.BuildPdf(registry, root, nameTree: true));
        var tree = Walk(pdf, pdf.Dict(pdf.Dict(pdf.Catalog.Get(N("Names"))).Get(N("Dests"))), nameTree: true);

        Assert.True(tree.RootHasKids);
        Assert.Equal(300, tree.Entries.Count);
        Assert.Contains(tree.Entries, e => e.NameKey!.Length > 0 && e.NameKey[0] >= 0x80);
    }

    [Fact]
    public void Iso32000_1_7_9_6_ValuesOfTheDestsTreeAreIndirectAndAStreamMustBe()
    {
        using var pdf = new Pdf(DocumentWithDestinations(["one", "two"]));
        var tree = DestsTree(pdf);

        // Arrays "should be" indirect (Table 36 and 7.9.6); the library writes every destination so.
        Assert.Equal(2, tree.Entries.Count);
        Assert.All(tree.Entries, e => Assert.IsType<PdfIndirectReference>(e.Value));

        // A stream "shall" be indirect: the writer refuses a direct one rather than write it inline.
        var registry = new PdfObjectRegistry();
        var entries = new List<KeyValuePair<byte[], PdfObject>> { new([1], new PdfStream([1, 2, 3])) };
        Assert.Throws<ArgumentException>(() => NameTreeWriter.Write(entries, registry));
    }

    [Fact]
    public void Iso32000_1_7_9_6_AKeyAppearsOnce()
    {
        using var doc = new PdfDocument { Timestamp = Pinned };
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 200));
        doc.AddNamedDestination("same", page, 0, 100);

        Assert.Throws<ArgumentException>(() => doc.AddNamedDestination("same", page, 0, 50));
        Assert.Throws<ArgumentException>(() => NameTreeWriter.Write(
            [new KeyValuePair<byte[], PdfObject>([1], PdfNull.Instance), new KeyValuePair<byte[], PdfObject>([1], PdfNull.Instance)],
            new PdfObjectRegistry()));
    }

    // ── 12.3.2 Destinations ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Iso32000_1_12_3_2_2_ADestinationIsAPageReferenceAndFiveOperands()
    {
        using var pdf = new Pdf(DocumentWithDestinations(["target"], pages: 3));
        var tree = DestsTree(pdf);

        var array = pdf.Array(Assert.Single(tree.Entries).Value);
        Assert.Equal(5, array.Count);
        // "page is an indirect reference to a page object", and it is the first page of the three.
        var page = Assert.IsType<PdfIndirectReference>(array[0]);
        Assert.Equal(pdf.PageObjectNumber(0), page.ObjectNumber);
        Assert.Equal("Page", Assert.IsType<PdfName>(pdf.Dict(page).Get(PdfName.Type)).Value);
        Assert.Equal("XYZ", Assert.IsType<PdfName>(array[1]).Value);
        Assert.Equal(0, Number(array[2]));
        Assert.Equal(150, Number(array[3]));
        // A null zoom keeps the current magnification (Table 151).
        Assert.IsType<PdfNull>(array[4]);
    }

    [Fact]
    public void Iso32000_1_12_3_2_3_NamesAreAKeyedTreeInTheNameDictionaryNotACatalogDictionary()
    {
        using var pdf = new Pdf(DocumentWithDestinations(["target"]));

        // The PDF 1.1 form (a /Dests dictionary of the catalog) is not written; the PDF 1.2 form is.
        Assert.Null(pdf.Catalog.Get(N("Dests")));
        var names = pdf.NameDictionary!;
        Assert.Equal(["Dests"], names.Entries.Select(e => e.Key.Value));
    }

    // ── 7.9.7 Number trees and 12.4.2 page labels ───────────────────────────────────────────

    private static byte[] DocumentWithLabels(int pages, params (int Start, PdfPageLabelStyle Style, string? Prefix, int First)[] ranges)
    {
        using var doc = new PdfDocument { Timestamp = Pinned };
        doc.Info.Title = "G1";
        for (var i = 0; i < pages; i++)
            doc.AddPage(new PdfRectangle(0, 0, 200, 200));
        foreach (var (start, style, prefix, first) in ranges)
            doc.AddPageLabelRange(start, style, prefix, first);
        return Save(doc);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    [InlineData(10_000)]
    public void Iso32000_1_7_9_7_NumberTreeKeysAscendAndLimitsAreIntegers(int count)
    {
        var ranges = Enumerable.Range(0, count).Select(i => (i, PdfPageLabelStyle.Decimal, (string?)null, 1)).ToArray();
        using var pdf = new Pdf(DocumentWithLabels(count, ranges));

        Assert.NotNull(pdf.Catalog.Get(N("PageLabels")));
        var tree = LabelsTree(pdf);

        Assert.Equal(count, tree.Entries.Count);
        Assert.Equal(Enumerable.Range(0, count).Select(i => (long)i), tree.Entries.Select(e => e.NumberKey));
        Assert.Equal(tree.RootHasEntries, tree.Nodes == 1);
        Assert.False(tree.RootHasLimits);
        // Every Limits entry of a non-root node is a pair of integers, which the walk has compared
        // with the keys below it; show that the tree under test had nodes with one.
        Assert.Equal(count > 64, tree.RootHasKids);
    }

    [Fact]
    public void Iso32000_1_12_4_2_TheTreeIncludesAValueForPageIndexZero()
    {
        // The first range starts at page 3, so the caller gave nothing for pages 0 to 2.
        using var pdf = new Pdf(DocumentWithLabels(8, (3, PdfPageLabelStyle.LowerRoman, null, 1), (6, PdfPageLabelStyle.Decimal, null, 1)));
        var tree = LabelsTree(pdf);

        Assert.Equal([0L, 3, 6], tree.Entries.Select(e => e.NumberKey));
        var zero = pdf.Dict(tree.Entries[0].Value);
        Assert.Equal("D", Assert.IsType<PdfName>(zero.Get(N("S"))).Value);
        Assert.Null(zero.Get(N("St")));
        Assert.Equal(["1", "2", "3", "i", "ii", "iii", "1", "2"], EvaluateLabels(pdf, 8));
    }

    [Fact]
    public void Iso32000_1_12_4_2_AGivenRangeAtPageIndexZeroIsKept()
    {
        using var pdf = new Pdf(DocumentWithLabels(3, (0, PdfPageLabelStyle.UpperRoman, "Front-", 1)));
        var tree = LabelsTree(pdf);

        var only = Assert.Single(tree.Entries);
        Assert.Equal(0, only.NumberKey);
        Assert.Equal(["Front-I", "Front-II", "Front-III"], EvaluateLabels(pdf, 3));
    }

    [Fact]
    public void Iso32000_1_12_4_2_ALabelDictionaryHoldsOnlyTable159EntriesWithDefinedValues()
    {
        (int, PdfPageLabelStyle, string?, int)[] ranges =
        [
            (0, PdfPageLabelStyle.LowerRoman, null, 1),
            (4, PdfPageLabelStyle.Decimal, null, 1),
            (7, PdfPageLabelStyle.Decimal, "A-", 8),
            (10, PdfPageLabelStyle.UpperLetters, null, 25),
            (14, PdfPageLabelStyle.LowerLetters, null, 1),
            (50, PdfPageLabelStyle.None, "Contents", 1),
            (52, PdfPageLabelStyle.UpperRoman, "Äpp-–", 3995),
        ];
        using var pdf = new Pdf(DocumentWithLabels(57, ranges));
        var tree = LabelsTree(pdf);

        Assert.Equal(ranges.Length, tree.Entries.Count);
        foreach (var entry in tree.Entries)
        {
            var dict = pdf.Dict(entry.Value);
            Assert.All(dict.Entries.Select(e => e.Key.Value), key => Assert.Contains(key, new[] { "Type", "S", "P", "St" }));
            if (dict.Get(N("S")) is { } style)
                Assert.Contains(Assert.IsType<PdfName>(style).Value, new[] { "D", "R", "r", "A", "a" });
            if (dict.Get(N("St")) is { } start)
                Assert.True(Assert.IsType<PdfInteger>(start).Value >= 1, "St shall be 1 or more");
            if (dict.Get(N("P")) is { } prefix)
                _ = TextString(prefix); // a text string, not a name or a number
        }

        // No S entry: "page labels shall consist solely of a label prefix with no numeric portion".
        Assert.Null(pdf.Dict(tree.Entries[5].Value).Get(N("S")));
        // A prefix is a text string; the writer gives it as UTF-16BE behind the byte order mark.
        var written = Assert.IsType<PdfLiteralString>(pdf.Dict(tree.Entries[6].Value).Get(N("P"))).Bytes.ToArray();
        Assert.Equal([(byte)0xFE, (byte)0xFF], written[..2]);
        Assert.Equal("Äpp-–", TextString(pdf.Dict(tree.Entries[6].Value).Get(N("P"))!));
    }

    [Fact]
    public void Iso32000_1_12_4_2_LabelsCountFromEachRangesStartInTheDefinedNumberingStyles()
    {
        (int, PdfPageLabelStyle, string?, int)[] ranges =
        [
            (0, PdfPageLabelStyle.LowerRoman, null, 1),
            (4, PdfPageLabelStyle.Decimal, null, 1),
            (7, PdfPageLabelStyle.Decimal, "A-", 8),
            (10, PdfPageLabelStyle.UpperLetters, null, 25),
            (14, PdfPageLabelStyle.LowerLetters, null, 1),
            (50, PdfPageLabelStyle.None, "Contents", 1),
            (52, PdfPageLabelStyle.UpperRoman, null, 3995),
        ];
        using var pdf = new Pdf(DocumentWithLabels(57, ranges));
        var labels = EvaluateLabels(pdf, 57);

        // The example of 12.4.2: i, ii, iii, iv, 1, 2, 3, A-8, A-9 ...
        Assert.Equal(["i", "ii", "iii", "iv", "1", "2", "3", "A-8", "A-9", "A-10"], labels[..10]);
        // A to Z for the first 26 pages, then AA to ZZ, and so on: number 25 is Y, 27 is AA, 28 is BB.
        Assert.Equal(["Y", "Z", "AA", "BB"], labels[10..14]);
        Assert.Equal("a", labels[14]);
        Assert.Equal("z", labels[39]);
        Assert.Equal("aa", labels[40]);
        Assert.Equal("bb", labels[41]);
        Assert.All(labels[50..52], l => Assert.Equal("Contents", l));
        Assert.Equal(["MMMCMXCV", "MMMCMXCVI", "MMMCMXCVII", "MMMCMXCVIII", "MMMCMXCIX"], labels[52..57]);
    }

    [Fact]
    public void Iso32000_1_12_4_2_ARangeMayNotStartPastTheLastPage()
    {
        // Keys are page indices; one that no page has would label nothing, and the writer refuses it
        // when the file is written, not when the range is added.
        using var doc = new PdfDocument { Timestamp = Pinned };
        doc.AddPage(new PdfRectangle(0, 0, 200, 200));
        doc.AddPageLabelRange(1, PdfPageLabelStyle.Decimal);

        Assert.Throws<InvalidOperationException>(() => Save(doc));
    }

    [Fact]
    public void Iso32000_1_12_4_2_StartMustBeOneOrMoreAndAKeyIsUsedOnce()
    {
        using var doc = new PdfDocument { Timestamp = Pinned };
        doc.AddPage(new PdfRectangle(0, 0, 200, 200));

        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, null, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.AddPageLabelRange(-1, PdfPageLabelStyle.Decimal));
        doc.AddPageLabelRange(0, PdfPageLabelStyle.Decimal);
        // 32000-2, 12.4.2: "Labelling ranges shall not overlap, so that each page shall have only one label".
        Assert.Throws<ArgumentException>(() => doc.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman));
    }

    [Fact]
    public void Iso32000_1_7_9_7_NumberTreeKeysFitAThirtyTwoBitInteger()
    {
        // Keys are integers (7.9.7); the range is the one of ISO 32000-1, Table C.1 and ISO 19005-2,
        // 6.1.13. Reached through the internal writer, because a page index of a document is an int.
        var registry = new PdfObjectRegistry();
        var entries = new List<KeyValuePair<long, PdfObject>>
        {
            new(int.MaxValue, new PdfInteger(1)),
            new(int.MinValue, new PdfInteger(2)),
            new(0, new PdfInteger(3)),
        };
        var root = NumberTreeWriter.Write(entries, registry);
        using var pdf = new Pdf(TreeTestSupport.BuildPdf(registry, root, nameTree: false));
        var tree = Walk(pdf, pdf.Dict(pdf.Catalog.Get(N("PageLabels"))), nameTree: false);

        Assert.Equal([(long)int.MinValue, 0, int.MaxValue], tree.Entries.Select(e => e.NumberKey));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NumberTreeWriter.Write([new KeyValuePair<long, PdfObject>(1L + int.MaxValue, new PdfInteger(1))], new PdfObjectRegistry()));
    }
}
