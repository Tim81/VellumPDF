// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.Json;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;
using VellumPdf.TestSupport;
using static VellumPdf.Kernel.Tests.TreeTestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #262: the page label table, its <c>/PageLabels</c> number tree (ISO 32000-2, 12.4.2 and 7.9.7) and the
/// formatter that computes a page's label. Exact bytes are asserted as written; structure is read back
/// through <c>VellumPdf.Reader</c>.
/// </summary>
public sealed class PageLabelTableTests
{
    private static string Text(PdfObject value)
    {
        using var ms = new MemoryStream();
        value.WriteTo(new PdfWriter(ms));
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static string Build(PageLabelTable table, int pageCount)
    {
        var root = table.Build(new PdfObjectRegistry(), pageCount);
        Assert.NotNull(root);
        return Text(root);
    }

    // ---- exact bytes: the tree ----------------------------------------------------------------

    [Fact]
    public void Build_lowerRomanFromPageZero_writesOneRange()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.LowerRoman, null, 1);
        Assert.Equal("<<\n/Nums [0 <<\n/S /r\n>>]\n>>", Build(table, 10));
    }

    [Fact]
    public void Build_twoRanges_writesThemInPageIndexOrder_whateverTheOrderTheyWereSetIn()
    {
        var table = new PageLabelTable();
        table.Set(4, PageLabelNumbering.Decimal, null, 1);
        table.Set(0, PageLabelNumbering.LowerRoman, null, 1);
        Assert.Equal("<<\n/Nums [0 <<\n/S /r\n>> 4 <<\n/S /D\n>>]\n>>", Build(table, 10));
    }

    [Fact]
    public void Build_noRangeAtPageZero_addsADecimalRangeThere()
    {
        var table = new PageLabelTable();
        table.Set(3, PageLabelNumbering.Decimal, null, 1);
        Assert.Equal("<<\n/Nums [0 <<\n/S /D\n>> 3 <<\n/S /D\n>>]\n>>", Build(table, 10));
    }

    [Fact]
    public void Build_aRangeAtPageZeroIsNotReplacedByTheSynthesisedOne()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.UpperLetters, null, 1);
        Assert.Equal("<<\n/Nums [0 <<\n/S /A\n>>]\n>>", Build(table, 10));
    }

    [Fact]
    public void Build_prefixAndFirstNumber_areWrittenInTheOrderOfTheStandardsExample()
    {
        var table = new PageLabelTable();
        table.Set(7, PageLabelNumbering.Decimal, "A-", 8);
        Assert.Equal("<<\n/Nums [0 <<\n/S /D\n>> 7 <<\n/S /D\n/P (þÿ\0A\0-)\n/St 8\n>>]\n>>", Build(table, 10));
    }

    [Fact]
    public void Build_firstNumberOne_writesNoSt()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.Decimal, null, 1);
        Assert.DoesNotContain("/St", Build(table, 1));
    }

    [Fact]
    public void Build_emptyPrefix_writesNoP_anAbsentOneDoesNotEither()
    {
        var empty = new PageLabelTable();
        empty.Set(0, PageLabelNumbering.Decimal, string.Empty, 1);
        var absent = new PageLabelTable();
        absent.Set(0, PageLabelNumbering.Decimal, null, 1);

        Assert.DoesNotContain("/P", Build(empty, 1));
        Assert.Equal(Build(absent, 1), Build(empty, 1));
    }

    [Fact]
    public void Build_noStyle_writesNoS_andKeepsThePrefix()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.None, "Cover", 1);
        Assert.Equal("<<\n/Nums [0 <<\n/P (þÿ\0C\0o\0v\0e\0r)\n>>]\n>>", Build(table, 1));
    }

    [Fact]
    public void Build_prefixWithAParenthesis_isEscapedByTheWriter()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.None, "(", 1);
        // FE FF 00 28 serialises as ( FE FF 00 \ ( ): the 0x28 byte is escaped, the rest is not.
        Assert.Equal("<<\n/Nums [0 <<\n/P (þÿ\0\\()\n>>]\n>>", Build(table, 1));
    }

    [Fact]
    public void Build_prefixWithANonAsciiCharacter_isUtf16BigEndianWithAByteOrderMark()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.None, "é", 1);
        Assert.Equal("<<\n/Nums [0 <<\n/P (þÿ\0é)\n>>]\n>>", Build(table, 1));
    }

    [Fact]
    public void Build_prefixWithASurrogatePair_isWrittenAsTwoCodeUnits()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.None, "\U0001F600", 1);
        // U+1F600 is the pair D83D DE00.
        Assert.Equal("<<\n/Nums [0 <<\n/P (þÿØ=Þ\0)\n>>]\n>>", Build(table, 1));
    }

    [Fact]
    public void Build_valuesAreDirectDictionaries()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.Decimal, null, 1);
        var registry = new PdfObjectRegistry();
        var root = table.Build(registry, 1)!;
        var nums = Assert.IsType<PdfArray>(root.Get(new PdfName("Nums")));
        Assert.IsType<PdfDictionary>(nums[1]);
        Assert.Equal(0, registry.ObjectCount);
    }

    // ---- empty and out-of-range ---------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    public void Build_emptyTable_returnsNull_andLeavesTheRegistryAlone(int pageCount)
    {
        var registry = new PdfObjectRegistry();
        Assert.Null(new PageLabelTable().Build(registry, pageCount));
        Assert.Equal(0, registry.ObjectCount);
    }

    [Theory]
    [InlineData(3, 3)]
    [InlineData(5, 3)]
    public void Build_aRangeAtOrBeyondThePageCount_isRefused(int start, int pageCount)
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.Decimal, null, 1);
        table.Set(start, PageLabelNumbering.LowerRoman, null, 1);
        var registry = new PdfObjectRegistry();
        var ex = Assert.Throws<InvalidOperationException>(() => table.Build(registry, pageCount));
        Assert.Contains($"page index {start}", ex.Message);
        Assert.Equal(0, registry.ObjectCount);
    }

    [Fact]
    public void Build_lastRangeOnTheLastPage_isAccepted()
    {
        var table = new PageLabelTable();
        table.Set(2, PageLabelNumbering.Decimal, null, 1);
        Assert.NotNull(table.Build(new PdfObjectRegistry(), 3));
    }

    [Fact]
    public void Build_nullRegistry_isRefused_evenForAnEmptyTable()
    {
        Assert.Equal("registry", Assert.Throws<ArgumentNullException>(() => new PageLabelTable().Build(null!, 1)).ParamName);
    }

    [Fact]
    public void Build_twice_givesTheSameBytes_andLeavesTheTableAlone()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.LowerRoman, "x", 3);
        table.Set(2, PageLabelNumbering.Decimal, null, 1);
        Assert.Equal(Build(table, 5), Build(table, 5));
        Assert.Equal(2, table.Count);
    }

    // ---- Set ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 1, "startPageIndex")]
    [InlineData(0, 0, "firstNumber")]
    [InlineData(0, -1, "firstNumber")]
    public void Set_refusesANegativeStartAndAFirstNumberBelowOne(int start, int first, string paramName)
    {
        var table = new PageLabelTable();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => table.Set(start, PageLabelNumbering.Decimal, null, first));
        Assert.Equal(paramName, ex.ParamName);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Set_refusesAnUndefinedStyle()
    {
        var table = new PageLabelTable();
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => table.Set(0, (PageLabelNumbering)99, null, 1));
        Assert.Equal("style", ex.ParamName);
        Assert.Equal(0, table.Count);
    }

    [Fact]
    public void Set_secondRangeAtTheSameStart_isRefused_andTheFirstStays()
    {
        var table = new PageLabelTable();
        table.Set(2, PageLabelNumbering.Decimal, null, 1);
        Assert.Throws<ArgumentException>(() => table.Set(2, PageLabelNumbering.LowerRoman, null, 1));
        Assert.Equal(1, table.Count);
        Assert.Contains("/S /D", Build(table, 3));
    }

    [Fact]
    public void Set_prefixWithAnUnpairedSurrogate_isRefused()
    {
        var table = new PageLabelTable();
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => table.Set(0, PageLabelNumbering.Decimal, "\uD800", 1)).ParamName);
        Assert.Throws<ArgumentException>(() => table.Set(0, PageLabelNumbering.Decimal, "a\uDC00", 1));
        Assert.Throws<ArgumentException>(() => table.Set(0, PageLabelNumbering.Decimal, "\uD800a", 1));
        table.Set(0, PageLabelNumbering.Decimal, "😀", 1);
        Assert.Equal(1, table.Count);
    }

    [Fact]
    public void Set_prefixAt16382Characters_isAccepted_and16383IsRefused()
    {
        var table = new PageLabelTable();
        table.Set(0, PageLabelNumbering.Decimal, new string('x', 16_382), 1);
        var ex = Assert.Throws<ArgumentException>(() => table.Set(1, PageLabelNumbering.Decimal, new string('x', 16_383), 1));
        Assert.Equal("prefix", ex.ParamName);

        // 16,382 characters are 2 + 2 x 16,382 = 32,766 bytes, inside the 32,767 of ISO 19005-2, 6.1.13.
        var root = table.Build(new PdfObjectRegistry(), 2)!;
        var nums = Assert.IsType<PdfArray>(root.Get(new PdfName("Nums")));
        var value = Assert.IsType<PdfDictionary>(nums[1]);
        Assert.Equal(32_766, Assert.IsType<PdfLiteralString>(value.Get(new PdfName("P"))).Bytes.Length);
    }

    // ---- the formatter ------------------------------------------------------------------------

    [Theory]
    [InlineData((int)PageLabelNumbering.Decimal, null, 1L, "1")]
    [InlineData((int)PageLabelNumbering.LowerRoman, null, 4L, "iv")]
    [InlineData((int)PageLabelNumbering.LowerRoman, null, 9L, "ix")]
    [InlineData((int)PageLabelNumbering.LowerRoman, null, 14L, "xiv")]
    [InlineData((int)PageLabelNumbering.LowerRoman, null, 3999L, "mmmcmxcix")]
    [InlineData((int)PageLabelNumbering.LowerRoman, null, 4000L, "mmmm")]
    [InlineData((int)PageLabelNumbering.UpperRoman, null, 1994L, "MCMXCIV")]
    [InlineData((int)PageLabelNumbering.UpperLetters, null, 1L, "A")]
    [InlineData((int)PageLabelNumbering.UpperLetters, null, 26L, "Z")]
    [InlineData((int)PageLabelNumbering.UpperLetters, null, 27L, "AA")]
    [InlineData((int)PageLabelNumbering.UpperLetters, null, 52L, "ZZ")]
    [InlineData((int)PageLabelNumbering.UpperLetters, null, 53L, "AAA")]
    [InlineData((int)PageLabelNumbering.LowerLetters, null, 28L, "bb")]
    [InlineData((int)PageLabelNumbering.Decimal, "A-", 8L, "A-8")]
    [InlineData((int)PageLabelNumbering.None, "Contents", 1L, "Contents")]
    [InlineData((int)PageLabelNumbering.None, null, 1L, "")]
    [InlineData((int)PageLabelNumbering.Decimal, null, 2147483648L, "2147483648")]
    public void Format_knownAnswers(int style, string? prefix, long number, string expected)
        => Assert.Equal(expected, PageLabelFormatter.Format((PageLabelNumbering)style, prefix, number));

    [Fact]
    public void Format_romanFollowsTheSubtractiveFormForEveryNumberUpTo3999()
    {
        // An independent reference: greedy subtraction over the full table, written separately from the formatter.
        (int, string)[] digits = [(1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i")];
        for (var n = 1; n <= 3999; n++)
        {
            var expected = new StringBuilder();
            var remaining = n;
            foreach (var (value, text) in digits)
            {
                while (remaining >= value)
                {
                    expected.Append(text);
                    remaining -= value;
                }
            }
            Assert.Equal(expected.ToString(), PageLabelFormatter.Format(PageLabelNumbering.LowerRoman, null, n));
        }
    }

    [Fact]
    public void Format_lettersRepeatTheLetter_theyAreNotBase26()
    {
        for (var n = 1; n <= 26 * 5; n++)
        {
            var expected = new string((char)('A' + (n - 1) % 26), (n - 1) / 26 + 1);
            Assert.Equal(expected, PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, n));
        }
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public void Format_numberBelowOne_isRefused(long number)
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.Decimal, null, number));
        Assert.Equal("number", ex.ParamName);
    }

    [Fact]
    public void Format_undefinedStyle_isRefused()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format((PageLabelNumbering)99, null, 1));
        Assert.Equal("style", ex.ParamName);
    }

    [Fact]
    public void Format_lettersAtTheLengthBound_areAccepted_andOneMoreIsRefused()
    {
        Assert.Equal(32_767, PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, 851_942).Length);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, 851_943));
        Assert.Equal("number", ex.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, int.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.LowerLetters, null, long.MaxValue));
    }

    [Fact]
    public void Format_romanAtTheLengthBound_isAccepted_andOneThousandMoreIsRefused()
    {
        // Thousands are one M each: 32,767 M, plus nothing for a remainder of 0.
        Assert.Equal(32_767, PageLabelFormatter.Format(PageLabelNumbering.UpperRoman, null, 32_767_000).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.UpperRoman, null, 32_768_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.LowerRoman, null, long.MaxValue));
    }

    [Fact]
    public void Format_boundIncludesThePrefix()
    {
        var prefix = new string('p', 32_000);
        Assert.Equal(32_000 + 767, PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, prefix, 19_942).Length);
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, prefix, 19_943));
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.None, new string('p', 32_768), 1));
    }

    [Fact]
    public void Format_lengthIsCheckedBeforeAnythingIsBuilt()
    {
        PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, 851_942); // warm the JIT
        var before = GC.GetAllocatedBytesForCurrentThread();
        var label = PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, 851_942);
        var accepted = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(32_767, label.Length);
        // One string of 32,767 two-byte characters is 65,534 bytes plus a header (65,560 measured in
        // Release on net10.0 and net11.0); allow twice the characters' size.
        Assert.True(accepted <= 2L * 32_767 * 2, $"accepted case allocated {accepted} bytes");

        // The exception's own allocation is counted too (624 bytes measured); the refused case must still
        // stay below what building the label costs, so no buffer was made before the length check.
        before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<ArgumentOutOfRangeException>(() => PageLabelFormatter.Format(PageLabelNumbering.UpperLetters, null, int.MaxValue));
        var refused = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(refused < accepted, $"refused case allocated {refused} bytes, accepted case {accepted}");
    }

    // ---- read back through the reader ---------------------------------------------------------

    private sealed record LabelRange(int Start, PageLabelNumbering Style, string? Prefix, int First);

    // The label a viewer shows for each page, worked out from the ranges the table was given.
    private static string[] ExpectedLabels(IReadOnlyList<LabelRange> ranges, int pageCount)
    {
        var labels = new string[pageCount];
        for (var page = 0; page < pageCount; page++)
        {
            var range = ranges.Where(r => r.Start <= page).OrderBy(r => r.Start).LastOrDefault()
                ?? new LabelRange(0, PageLabelNumbering.Decimal, null, 1);
            labels[page] = PageLabelFormatter.Format(range.Style, range.Prefix, range.First + (page - range.Start));
        }
        return labels;
    }

    private static readonly LabelRange[] FrontMatterRanges =
    [
        new(0, PageLabelNumbering.LowerRoman, null, 1),
        new(4, PageLabelNumbering.Decimal, "A-", 8),
        new(9, PageLabelNumbering.UpperLetters, null, 1),
        new(11, PageLabelNumbering.None, "Index", 1),
    ];

    private static (PageLabelTable Table, int PageCount) FrontMatter()
    {
        var table = new PageLabelTable();
        foreach (var range in FrontMatterRanges)
            table.Set(range.Start, range.Style, range.Prefix, range.First);
        return (table, 14);
    }

    [Fact]
    public void Build_roundTripsThroughReader_andEveryPageGetsTheLabelItsRangeComputes()
    {
        var (table, pageCount) = FrontMatter();
        var registry = new PdfObjectRegistry();
        var pageRefs = Enumerable.Range(0, pageCount).Select(_ => registry.Reserve()).ToArray();
        var root = table.Build(registry, pageCount)!;
        var pdf = BuildPdfWithPages(registry, pageRefs, destsRoot: null, labelsRoot: root);

        var (reader, readRoot) = OpenRoot(pdf, nameTree: false);
        using var _ = reader;
        var walked = Walk(reader, readRoot, nameTree: false);

        Assert.Equal(FrontMatterRanges.Select(r => (long)r.Start), walked.Entries.Select(e => (long)e.Key));
        for (var i = 0; i < FrontMatterRanges.Length; i++)
        {
            var value = Assert.IsType<PdfDictionary>(reader.ResolveValue(walked.Entries[i].Value));
            var range = FrontMatterRanges[i];
            var style = value.Get(new PdfName("S")) is PdfName name ? name.Value : null;
            Assert.Equal(range.Style switch
            {
                PageLabelNumbering.Decimal => "D",
                PageLabelNumbering.LowerRoman => "r",
                PageLabelNumbering.UpperLetters => "A",
                _ => null,
            }, style);
            Assert.Equal(range.Prefix, value.Get(new PdfName("P")) is PdfLiteralString p ? Encoding.BigEndianUnicode.GetString(p.Bytes.Span[2..]) : null);
            Assert.Equal(range.First, value.Get(new PdfName("St")) is PdfInteger st ? (int)st.Value : 1);
        }
        Assert.Equal(
            ["i", "ii", "iii", "iv", "A-8", "A-9", "A-10", "A-11", "A-12", "A", "B", "Index", "Index", "Index"],
            ExpectedLabels(FrontMatterRanges, pageCount));
    }

    // ---- oracles ------------------------------------------------------------------------------

    [Fact]
    public void Build_qpdfCheckIsClean_andItsJsonShowsTheRangesAndEachPagesLabel()
    {
        var (table, pageCount) = FrontMatter();
        var registry = new PdfObjectRegistry();
        var pageRefs = Enumerable.Range(0, pageCount).Select(_ => registry.Reserve()).ToArray();
        var root = table.Build(registry, pageCount)!;
        var pdf = BuildPdfWithPages(registry, pageRefs, destsRoot: null, labelsRoot: root);

        var dir = Path.Combine(Path.GetTempPath(), "vellum_labels_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "labels.pdf");
            File.WriteAllBytes(path, pdf);

            ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut);
            Assert.False(timedOut);
            Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
            Assert.Contains("No syntax or stream encoding errors found", stdout);

            ExternalTool.TryRun("qpdf", ["--json=2", "--json-key=pagelabels", path], out exit, out stdout, out stderr, out timedOut);
            Assert.False(timedOut);
            Assert.Equal(0, exit);
            using (var json = JsonDocument.Parse(stdout))
            {
                var ranges = json.RootElement.GetProperty("pagelabels").EnumerateArray().ToList();
                Assert.Equal(FrontMatterRanges.Select(r => r.Start), ranges.Select(r => r.GetProperty("index").GetInt32()));
                for (var i = 0; i < ranges.Count; i++)
                    Assert.Equal(QpdfLabel(FrontMatterRanges[i], 0), Properties(ranges[i].GetProperty("label")));
            }

            ExternalTool.TryRun("qpdf", ["--json=2", "--json-key=pages", path], out exit, out stdout, out stderr, out timedOut);
            Assert.False(timedOut);
            Assert.Equal(0, exit);
            using (var json = JsonDocument.Parse(stdout))
            {
                var pages = json.RootElement.GetProperty("pages").EnumerateArray().ToList();
                Assert.Equal(pageCount, pages.Count);
                for (var i = 0; i < pageCount; i++)
                {
                    var range = FrontMatterRanges.Last(r => r.Start <= i);
                    Assert.Equal(QpdfLabel(range, i - range.Start), Properties(pages[i].GetProperty("label")));
                }
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // qpdf --json=2 gives a page's label as the dictionary of its range with /St advanced to that page, and
    // spells a prefix "u:" + text. Written here from the ranges the table was given, not from the formatter.
    private static string QpdfLabel(LabelRange range, int offset)
    {
        var parts = new List<string>();
        if (range.Prefix is not null)
            parts.Add("/P=u:" + range.Prefix);
        var style = range.Style switch
        {
            PageLabelNumbering.Decimal => "/D",
            PageLabelNumbering.LowerRoman => "/r",
            PageLabelNumbering.UpperLetters => "/A",
            _ => null,
        };
        if (style is not null)
            parts.Add("/S=" + style);
        parts.Add("/St=" + (range.First + offset));
        return string.Join(",", parts);
    }

    private static string Properties(JsonElement label) =>
        string.Join(",", label.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).Select(p => p.Name + "=" + (p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText())));
}
