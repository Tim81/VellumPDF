// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// Pins the structural guarantees of <see cref="StructureDumper"/>, each of which replaced a
/// per-site fix: one output budget for every line, one escape-and-cap for every document-controlled
/// string with problems counted when they are emitted, one integer reader, and one flattened CMap
/// lookup. Every document is written out by hand.
/// </summary>
public sealed class StructureDumperClassTests
{
    private const string DocElem = "<< /Type /StructElem /S /Document /P 5 0 R";

    private static string[] Lines(byte[] pdf) => StructureDumper.Dump(pdf).Split('\n');

    private static void Has(string expected, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        Assert.True(all.Contains(expected), "missing line: " + expected + Environment.NewLine + "--- dump ---" + Environment.NewLine + string.Join(Environment.NewLine, all));
    }

    private static void Lacks(string text, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        Assert.False(all.Any(l => l.Contains(text, StringComparison.Ordinal)), "unexpected: " + text + Environment.NewLine + "--- dump ---" + Environment.NewLine + string.Join(Environment.NewLine, all));
    }

    // ── C1: one output budget, bounded paths ──────────────────────────────────────────────────

    // A chain of long-named elements with the given number of integer kids at the bottom, none of
    // them present in the content, so each prints a MISSING line carrying the leaf's path.
    private static byte[] DeepChainWithMissingKids(int depth, int kids)
    {
        var name = new string('N', 100);
        var objs = new List<(int, string, string?)>();
        for (var i = 0; i < depth; i++)
        {
            var parent = i == 0 ? 5 : 5 + i;
            var k = i + 1 < depth ? $"/K [{7 + i} 0 R]" : $"/K [{string.Join(' ', Enumerable.Range(0, kids))}] /Pg 3 0 R";
            objs.Add((6 + i, $"<< /Type /StructElem /S /{name} /P {parent} 0 R {k} >>", null));
        }

        return RawPdf.Doc("", "", "/K [6 0 R]", [.. objs]);
    }

    [Fact]
    public void ManyLongPathLines_hitTheOutputBudget()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StructureDumper.Dump(DeepChainWithMissingKids(250, 30_000)));
        Assert.Contains("Output budget", ex.Message);
    }

    [Fact]
    public void APathInAProblemLine_isCappedAtItsTail()
    {
        var lines = Lines(DeepChainWithMissingKids(100, 1));

        var missing = lines.Single(l => l.StartsWith("MISSING", StringComparison.Ordinal));
        Assert.True(missing.Length < 300, missing.Length + ": " + missing);
        Assert.Contains(" in …/", missing);
        Assert.EndsWith("[0]", missing);
    }

    // ── C2: escaped strings, problems counted at emission ─────────────────────────────────────

    [Fact]
    public void ANameCannotForgeALineOrAProblem()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /Span#0AORPHAN#20mcid#209#20p1 /P 6 0 R >>", null));

        var lines = Lines(pdf);
        Assert.DoesNotContain(lines, l => l.TrimStart().StartsWith("ORPHAN", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Span\\u{000A}ORPHAN mcid 9 p1", StringComparison.Ordinal));
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void TextThatLooksLikeAMarker_isNotCounted()
    {
        var pdf = RawPdf.Doc(
            "",
            "/Annots [9 0 R 10 0 R]",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /UNMARKED /P 6 0 R >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /Contents (-> NO-ENTRY dest=CYCLE) >>", null),
            (10, "<< /Type /Annot /Subtype /x#3A#20UNRESOLVED /Rect [0 0 1 1] >>", null));

        var lines = Lines(pdf);
        Has("    UNMARKED→?", lines);
        Assert.Contains(lines, l => l.StartsWith("annot 0:", StringComparison.Ordinal) && l.Contains("-> NO-ENTRY dest=CYCLE", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("annot 1: Subtype=x: UNRESOLVED", StringComparison.Ordinal));
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void Footer_countsTheProblemsEmitted()
    {
        var pdf = RawPdf.Doc(
            "0 0 1 1 re f",
            "/Annots [9 0 R]",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [99 0 R] >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /StructParent 7 >>", null));

        // UNRESOLVED kid, UNMARKED path, the annotation line (NO-ENTRY) and NO-OBJR.
        Has("4 problem line(s)", Lines(pdf));
    }

    // ── C3: one integer reader ────────────────────────────────────────────────────────────────

    [Fact]
    public void RealNumbersWhereIntegersAreRequired_areBadNotAbsentOrTruncated()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0.5>> BDC EMC /P <</MCID 0.0>> BDC EMC",
            "/StructParents 0.0 /Annots [9 0 R]",
            "/K [6 0 R] /ParentTreeNextKey 3.0",
            (6, $"{DocElem} >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /StructParent 1.0 >>", null));

        var lines = Lines(pdf);
        Assert.Equal(2, lines.Count(l => l == "BAD-MCID"));
        Has("BAD-KEY StructParents p1", lines);
        Has("ParentTreeNextKey: BAD-KEY", lines);
        Assert.Contains(lines, l => l.StartsWith("annot 0:", StringComparison.Ordinal) && l.Contains("StructParent=BAD-KEY", StringComparison.Ordinal));
        Lacks("NO-STRUCTPARENTS", lines);
        Has("6 problem line(s)", lines);
    }

    [Fact]
    public void ObjrTargetWithARealStructParent_isBad()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /Figure /P 6 0 R /Pg 3 0 R /K [<< /Type /OBJR /Pg 3 0 R /Obj 8 0 R >>] >>", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /StructParent 1.0 >>", "0 0 1 1 re f"),
            (9, "<< /Nums [1 7 0 R] >>", null));

        var lines = Lines(pdf);
        Has("BAD-KEY StructParent OBJR target Form in Document/Figure[0]", lines);
        Lacks("has no /StructParent", lines);
    }

    [Fact]
    public void RealNumberKids_andNumsKeys_areBad()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /K [0.5] >>", null),
            (9, "<< /Nums [1.0 null] >>", null));

        var lines = Lines(pdf);
        Has("    BAD-MCID", lines);
        Has("PARENTTREE BAD-KEY", lines);
    }

    // ── C4: unresolvable property lists ───────────────────────────────────────────────────────

    [Fact]
    public void NamedPropertyListWithoutAResource_isACountedProblem()
    {
        var lines = Lines(RawPdf.Doc("/P /MC9 BDC EMC", "", ""));

        Has("BAD-PROPERTIES /MC9 p1", lines);
        Has("1 problem line(s)", lines);
    }

    // ── C5: flattened CMap ranges, one parse per document ─────────────────────────────────────

    [Fact]
    public void WideEarlyRange_withManySmallOnes_isFirstWins()
    {
        var sb = new StringBuilder("50001 beginbfrange\n<0000> <FFFF> <0041>\n");
        for (var i = 1; i <= 50_000; i++)
            sb.Append($"<{i:X4}> <{i:X4}> <{0x4E00 + (i % 1000):X4}>\n");
        sb.Append("endbfrange");
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes(sb.ToString()));

        for (var code = 0u; code < 60_000; code++)
        {
            Assert.True(map.TryMap(code, 2, out var text));
            Assert.Equal(((char)(0x41 + code)).ToString(), text);
        }
    }

    [Fact]
    public void ManySmallEarlyRanges_underAWideLateOne_areFlattenedAndBinarySearched()
    {
        var sb = new StringBuilder("50001 beginbfrange\n");
        for (var i = 1; i <= 50_000; i++)
            sb.Append($"<{i:X4}> <{i:X4}> <{0x4E00 + (i % 1000):X4}>\n");
        sb.Append("<0000> <FFFF> <0041>\nendbfrange");
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes(sb.ToString()));

        for (var code = 0u; code < 60_000; code++)
        {
            Assert.True(map.TryMap(code, 2, out var text));
            var expected = code is >= 1 and <= 50_000 ? (char)(0x4E00 + (code % 1000)) : (char)(0x41 + code);
            Assert.Equal(expected.ToString(), text);
        }

        // 50,002 segments (measured; adjacent small ranges are not merged): a linear scan would take
        // tens of thousands of steps per lookup; a binary search at
        // most 17.
        Assert.True(map.Comparisons <= 60_000 * 20, map.Comparisons.ToString());
    }

    [Fact]
    public void NarrowEarlyRange_beatsAWideLaterOne_insideItsOwnBounds()
    {
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes(
            "2 beginbfrange <0100> <01FF> <0061> <0000> <FFFF> <1000> endbfrange"));

        Assert.True(map.TryMap(0x0150, 2, out var inner));
        Assert.Equal(((char)(0x61 + 0x50)).ToString(), inner);
        Assert.True(map.TryMap(0x0300, 2, out var outer));
        Assert.Equal(((char)(0x1000 + 0x300)).ToString(), outer);
        Assert.True(map.TryMap(0x0000, 2, out var low));
        Assert.Equal("က", low);
    }

    [Fact]
    public void AFontShownOnTwoPages_hasItsCMapParsedOnce()
    {
        const string cmap = "1 beginbfchar <0001> <0048> endbfchar";
        var pdf = RawPdf.DocPages(
            ["BT /F1 12 Tf <0001> Tj ET", "BT /F1 12 Tf <0001> Tj ET"],
            ["/Resources << /Font << /F1 8 0 R >> >>", "/Resources << /Font << /F1 8 0 R >> >>"],
            "",
            (8, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /ToUnicode 9 0 R >>", null),
            (9, "<< >>", cmap));

        ToUnicodeMap.ParseCount = 0;
        StructureDumper.Dump(pdf);

        Assert.Equal(1, ToUnicodeMap.ParseCount);
    }

    // ── Low-severity fixes ────────────────────────────────────────────────────────────────────

    [Fact]
    public void DictionaryKeys_areCapped()
    {
        var key = new string('k', 300);
        var lines = Lines(RawPdf.Doc($"/P <</{key} 1>> BDC EMC", "", ""));

        Assert.Contains(lines, l => l.Contains(new string('k', 200) + "…(+100)=1", StringComparison.Ordinal));
    }

    [Fact]
    public void AttributeItemNamingAnElement_printsItsPath()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /A [6 0 R] >>", null));

        Has("    P A=[Document]", Lines(pdf));
    }

    [Fact]
    public void McrWithABadStm_isNotRecordedAgainstThePage()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC EMC",
            "/StructParents 0",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /K [<< /Type /MCR /Pg 3 0 R /Stm 5 /MCID 0 >>] >>", null),
            (9, "<< /Nums [0 [6 0 R]] >>", null));

        var lines = Lines(pdf);
        Has("ORPHAN mcid 0 p1", lines);
        Has("    BAD-STM", lines);
        Lacks("PARENTTREE MISMATCH", lines);
    }

    [Fact]
    public void FormWithMcidsDrawnInsideAContentItem_isReported()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC /Fm0 Do EMC",
            "/Resources << /XObject << /Fm0 8 0 R >> >>",
            "",
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] >>", "/P <</MCID 0>> BDC EMC"));

        Assert.Contains(Lines(pdf), l => l.Trim() == "FORM-MCIDS-INSIDE-CONTENT-ITEM form1");
    }

    [Fact]
    public void FormWithMcidsDrawnInsideAnUnnumberedSequence_isNotReported()
    {
        var pdf = RawPdf.Doc(
            "/Span BMC /Fm0 Do EMC",
            "/Resources << /XObject << /Fm0 8 0 R >> >>",
            "",
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] >>", "/P <</MCID 0>> BDC EMC"));

        Lacks("FORM-MCIDS-INSIDE-CONTENT-ITEM", Lines(pdf));
    }

    // ── Cheap guards of the trusted-input scope ───────────────────────────────────────────────

    private static byte[] ExpandingFontDoc(int codes)
    {
        var expands = string.Concat(Enumerable.Repeat("0058", 256)); // 512 bytes: the longest allowed
        var tooLong = string.Concat(Enumerable.Repeat("0059", 257)); // 514 bytes: unmapped
        var cmap = $"2 beginbfchar <41> <{expands}> <42> <{tooLong}> endbfchar";
        return RawPdf.Doc(
            $"BT /F1 12 Tf ({new string('A', codes)}) Tj (B) Tj ET",
            "/Resources << /Font << /F1 8 0 R >> >>",
            "",
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 9 0 R >>", null),
            (9, "<< >>", cmap));
    }

    [Fact]
    public void ShowText_isCutAtTheCap_andAnOverlongDestinationIsUnmapped()
    {
        var lines = Lines(ExpandingFontDoc(4000));

        var show = lines.First(l => l.Contains("show \"X", StringComparison.Ordinal));
        Assert.True(show.Length < 400, show.Length.ToString());
        Assert.EndsWith("…(+3999 codes)\"", show);
        Has("UNMARKED show \"\\u{FFFD}\"", lines);
    }

    [Fact]
    public void ShowText_decodesOnlyWhatItPrints()
    {
        var small = ExpandingFontDoc(400);
        var large = ExpandingFontDoc(4000);
        StructureDumper.Dump(small); // warm the JIT and the caches

        var before = GC.GetAllocatedBytesForCurrentThread();
        StructureDumper.Dump(small);
        var smallBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        before = GC.GetAllocatedBytesForCurrentThread();
        StructureDumper.Dump(large);
        var largeBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        // Measured: 123,936 bytes for the small run and 138,360 for the large one in Debug and in
        // Release, a difference of 14,424. Decoding every code would add 3,600 codes x 256
        // characters x 2 bytes, over 1.8 MB, to the large run.
        Assert.True(largeBytes - smallBytes < 100_000, $"small {smallBytes}, large {largeBytes}");
    }

    [Fact]
    public void ElementLine_capsItsKeys()
    {
        var keys = string.Join(' ', Enumerable.Range(0, 7000).Select(i => $"/K{i:D4} {i}"));
        var pdf = RawPdf.Doc("", "", "/K [6 0 R]", (6, $"{DocElem} {keys} >>", null));

        var line = Lines(pdf).Single(l => l.StartsWith("  Document", StringComparison.Ordinal));
        Assert.EndsWith("…(+6968 keys)", line);
        Assert.True(line.Length < 1_000, line.Length.ToString());
    }

    [Fact]
    public void ALineLongerThanTheLineCap_isCut()
    {
        // 32 keys, each a dictionary of 32 keys, each an array of 32 integers: about 80,000 characters
        // in one element line before the cap.
        var leaf = "[" + string.Join(' ', Enumerable.Range(0, 32)) + "]";
        var inner = "<< " + string.Join(' ', Enumerable.Range(0, 32).Select(i => $"/I{i:D2} {leaf}")) + " >>";
        var outer = "<< " + string.Join(' ', Enumerable.Range(0, 32).Select(i => $"/O{i:D2} {inner}")) + " >>";
        var pdf = RawPdf.Doc("", "", "/K [6 0 R]", (6, $"{DocElem} /X {outer} >>", null));

        var line = Lines(pdf).Single(l => l.StartsWith("  Document", StringComparison.Ordinal));
        Assert.EndsWith("…(line cut)", line);
        Assert.True(line.Length <= 20_100, line.Length.ToString());
    }

    [Fact]
    public void CheckLinesAlone_hitTheOutputBudget()
    {
        // Three shallow levels of long names: the structure listing is about 20 characters per kid,
        // each MISSING line about 210 (three names and their indexes), so only the check
        // lines can pass 8,000,000 characters.
        var name = new string('N', 60);
        var kids = string.Join(' ', Enumerable.Range(0, 60_000));
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"<< /Type /StructElem /S /{name} /P 5 0 R /K [7 0 R] >>", null),
            (7, $"<< /Type /StructElem /S /{name} /P 6 0 R /K [8 0 R] >>", null),
            (8, $"<< /Type /StructElem /S /{name} /P 7 0 R /Pg 3 0 R /K [{kids}] >>", null));

        var ex = Assert.Throws<InvalidOperationException>(() => StructureDumper.Dump(pdf));
        Assert.Contains("Output budget", ex.Message);
    }

    [Fact]
    public void NullAndDanglingValues_areAbsent()
    {
        var pdf = RawPdf.Doc(
            "",
            "/StructParents null /Tabs null /Annots [9 0 R 10 0 R]",
            "/K [6 0 R]",
            (6, $"{DocElem} >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /StructParent 99 0 R /Contents null /QuadPoints null /Dest null >>", null),
            (10, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /A << /S /GoTo /D null >> >>", null));

        var lines = Lines(pdf);
        Has("annot 0: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=none", lines);
        Has("annot 1: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=GoTo none", lines);
        Has("Tabs: absent", lines);
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void RevisionNumbersInAttributeLists_areRead_andBadOnesAreCounted()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /A [<< /O /Layout >> 2 << /O /List >> 1.5] /C [/Cls -1] >>", null));

        var lines = Lines(pdf);
        Has("    P A=[{O=/Layout}@2 {O=/List}@BAD] C=[/Cls@BAD]", lines);
        Has("      BAD-REVISION A", lines);
        Has("      BAD-REVISION C", lines);
        Has("2 problem line(s)", lines);
    }
}
