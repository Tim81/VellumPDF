// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// Pins the parts of <see cref="StructureDumper"/> that exist to refuse or to report: a hostile or
/// merely malformed file must produce a bounded dump with a problem line, never a crash and never a
/// silently shorter listing. Every document is written out by hand.
/// </summary>
public sealed class StructureDumperHardeningTests
{
    private const string DocElem = "<< /Type /StructElem /S /Document /P 5 0 R";

    private static string[] Lines(byte[] pdf) => StructureDumper.Dump(pdf).Split('\n');

    private static void Has(string expected, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        Assert.True(all.Contains(expected), "missing line: " + expected + Environment.NewLine + "--- dump ---" + Environment.NewLine + string.Join(Environment.NewLine, all));
    }

    private static void HasTrimmed(string expected, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        Assert.True(all.Any(l => l.Trim() == expected), "missing line: " + expected + Environment.NewLine + "--- dump ---" + Environment.NewLine + string.Join(Environment.NewLine, all));
    }

    private static void Lacks(string text, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        Assert.False(all.Any(l => l.Contains(text, StringComparison.Ordinal)), "unexpected: " + text + Environment.NewLine + "--- dump ---" + Environment.NewLine + string.Join(Environment.NewLine, all));
    }

    // ── Destinations ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Destination_dictionaryThatPointsAtItself_isACycleNotAStackOverflow()
    {
        var annot = (int n, string extra) => (n, $"<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] {extra} >>", (string?)null);
        var pdf = RawPdf.Doc(
            "",
            "/Annots [9 0 R 11 0 R 12 0 R 13 0 R 14 0 R 16 0 R]",
            "",
            annot(9, "/Dest 10 0 R"),
            (10, "<< /D 10 0 R >>", null),
            annot(11, "/Dest 15 0 R"),
            (15, "<< /D [3 0 R /XYZ 0 0 0] >>", null),
            annot(12, "/Dest (chapter1)"),
            annot(13, "/A << /S /GoTo /D /Intro >>"),
            annot(14, "/Dest 99 0 R"),
            annot(16, "/Dest 17 0 R"),
            (17, "<< /D 18 0 R >>", null),
            (18, "<< /D [3 0 R /Fit] >>", null));

        var lines = Lines(pdf);
        Has("annot 0: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=CYCLE", lines);
        Has("annot 1: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=GoTo p1 XYZ", lines);
        Has("annot 2: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=named \"chapter1\"", lines);
        Has("annot 3: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=named /Intro", lines);
        Has("annot 4: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=unresolved", lines);
        Has("annot 5: Subtype=Link StructParent=none Contents=absent QuadPoints=absent dest=CYCLE", lines);
    }

    // ── Marked content ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void NamedPropertyList_isResolvedFromThePageAndFromAFormsOwnResources()
    {
        var pdf = RawPdf.Doc(
            "/P /MC0 BDC EMC /Span BMC /Fm0 Do EMC",
            "/StructParents 0 /Resources << /Properties << /MC0 8 0 R >> /XObject << /Fm0 10 0 R >> >>",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 2",
            (6, $"{DocElem} /K [7 0 R 11 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>", null),
            (8, "<< /MCID 0 >>", null),
            (9, "<< /Nums [0 [7 0 R] 1 [11 0 R]] >>", null),
            (10, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] /StructParents 1 /Resources << /Properties << /MC1 12 0 R >> >> >>", "/P /MC1 BDC 0 0 1 1 re f EMC"),
            (11, "<< /Type /StructElem /S /P /P 6 0 R /K [<< /Type /MCR /Pg 3 0 R /Stm 10 0 R /MCID 0 >>] >>", null),
            (12, "<< /MCID 0 >>", null));

        var lines = Lines(pdf);
        Has("BDC /P {MCID=0}", lines);
        Has("    BDC /P {MCID=0}", lines);
        Lacks("unresolved-properties", lines);
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void FormDrawnInsideASequence_isNotUnmarked()
    {
        var pdf = RawPdf.Doc(
            "/Span BMC /Fm0 Do EMC",
            "/Resources << /XObject << /Fm0 8 0 R >> >>",
            "",
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] >>", "0 0 1 1 re f"));

        var lines = Lines(pdf);
        Has("  form1", lines);
        Has("    path fill", lines);
        Lacks("UNMARKED", lines);
    }

    [Fact]
    public void UnclosedSequences_arePrintedForThePageAndForAForm()
    {
        var pdf = RawPdf.Doc(
            "/Fm0 Do /Span BMC",
            "/Resources << /XObject << /Fm0 8 0 R >> >>",
            "",
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] >>", "/P BMC"));

        var lines = Lines(pdf);
        HasTrimmed("UNCLOSED-MC 1 form1", lines);
        Has("UNCLOSED-MC 1 p1", lines);
    }

    [Fact]
    public void FormWithMcidsDrawnTwice_isReportedOnce_andItsMcidIsNotCountedTwice()
    {
        var pdf = RawPdf.Doc(
            "/Span BMC /Fm0 Do /Fm0 Do EMC",
            "/Resources << /XObject << /Fm0 8 0 R >> >>",
            "",
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] /StructParents 0 >>", "/P <</MCID 0>> BDC EMC"));

        var lines = Lines(pdf);
        Has("MULTI-DO p1 form1 with MCIDs (invoked 2 times)", lines);
        Lacks("DUP-MCID", lines);
    }

    [Fact]
    public void LineMarker_precedesTheFirstShowOfEachTextLine()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC BT /F1 12 Tf 1 0 0 1 10 100 Tm (A) Tj (B) Tj 1 0 0 1 10 80 Tm (C) Tj ET EMC",
            "/Resources << /Font << /F1 8 0 R >> >>",
            "",
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null));

        var lines = Lines(pdf).ToList();
        var at = lines.IndexOf("BDC /P {MCID=0}");
        Assert.True(at >= 0);
        Assert.Equal(["  line", "  show <41>", "  show <42>", "  line", "  show <43>"], lines.Skip(at + 1).Take(5));
    }

    [Fact]
    public void QuoteOperators_areDecodedLikeTj()
    {
        const string cmap = "1 beginbfchar <41> <0058> endbfchar";
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC BT /F1 12 Tf 14 TL 10 100 Td (A) ' 1 2 (A) \" ET EMC",
            "/Resources << /Font << /F1 8 0 R >> >>",
            "",
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /ToUnicode 9 0 R >>", null),
            (9, "<< >>", cmap));

        var shows = Lines(pdf).Count(l => l == "  show \"X\"");
        Assert.Equal(2, shows);
    }

    [Fact]
    public void Type0FontWithAnotherEncoding_printsRawCodes()
    {
        const string cmap = "1 beginbfchar <0041> <0058> endbfchar";
        var pdf = RawPdf.Doc(
            "BT /F1 12 Tf <0041> Tj ET",
            "/Resources << /Font << /F1 8 0 R >> >>",
            "",
            (8, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /UniJIS-UCS2-H /ToUnicode 9 0 R >>", null),
            (9, "<< >>", cmap));

        Has("UNMARKED show <0041>", Lines(pdf));
    }

    [Fact]
    public void OddTrailingByteOfATwoByteString_printsTheReplacementCharacter()
    {
        const string cmap = "1 beginbfchar <0001> <0048> endbfchar";
        var pdf = RawPdf.Doc(
            "BT /F1 12 Tf <000100> Tj ET",
            "/Resources << /Font << /F1 8 0 R >> >>",
            "",
            (8, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /ToUnicode 9 0 R >>", null),
            (9, "<< >>", cmap));

        Has("UNMARKED show \"H\\u{FFFD}\"", Lines(pdf));
    }

    [Fact]
    public void InlineImageAndShading_arePrinted_andReaderDiagnosticsAreNotHidden()
    {
        var pdf = RawPdf.Doc("BI /W 2 /H 3 /BPC 8 /CS /G ID abcdef EI /Sh0 sh", "", "");

        var lines = Lines(pdf);
        Has("UNMARKED inline-image 2x3", lines);
        Has("UNMARKED path shade", lines);
        Assert.Contains(lines, l => l.StartsWith("DIAG ", StringComparison.Ordinal));
    }

    [Fact]
    public void MarkedContentDepthCap_isReachedAcrossAPageAndAForm()
    {
        var page = string.Concat(Enumerable.Repeat("/Span BMC ", StructureDumper.MaxMarkedContentDepth - 1)) + "/Fm0 Do";
        byte[] Build(int formDepth) => RawPdf.Doc(
            page,
            "/Resources << /XObject << /Fm0 8 0 R >> >>",
            "",
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 9 9] >>", string.Concat(Enumerable.Repeat("/Span BMC ", formDepth))));

        Has("UNCLOSED-MC 1 form1", Lines(Build(1)).Select(l => l.Trim()));
        var ex = Assert.Throws<InvalidOperationException>(() => StructureDumper.Dump(Build(2)));
        Assert.Contains("Marked-content depth cap", ex.Message);
    }

    // ── Structure tree ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParentThatIsNotTheWalkingOwner_isReported()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R 8 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 8 0 R >>", null),
            (8, "<< /Type /StructElem /S /P >>", null));

        var lines = Lines(pdf).ToList();
        var at = lines.IndexOf("    P");
        Assert.True(at >= 0);
        Assert.Equal("      P-MISMATCH", lines[at + 1]);
        Assert.Equal("    P", lines[at + 2]);
        Assert.Equal("      P-MISMATCH", lines[at + 3]);
    }

    [Fact]
    public void SharedAttributeObject_isExpandedOnce_andTheCopiesPointAtTheFirst()
    {
        var objs = new List<(int, string, string?)>();
        var kids = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"{7 + i} 0 R"));
        objs.Add((6, $"{DocElem} /K [{kids}] >>", null));
        for (var i = 0; i < 200; i++)
            objs.Add((7 + i, "<< /Type /StructElem /S /Div /P 6 0 R /A 300 0 R >>", null));
        objs.Add((300, "<< /O /Layout /Placement /Block >>", null));

        var lines = Lines(RawPdf.Doc("", "", "/K [6 0 R]", [.. objs]));
        Has("    Div A={O=/Layout Placement=/Block}", lines);
        Assert.Equal(199, lines.Count(l => l == "    Div A=<same as Document/Div[0]>"));
    }

    [Fact]
    public void PrintedValueBudget_isRefused()
    {
        var value = new string('x', 300);
        var attrs = string.Join(' ', Enumerable.Range(0, 32).Select(k => $"/K{k:D2} ({value})"));
        var objs = new List<(int, string, string?)>();
        var kids = string.Join(' ', Enumerable.Range(0, 200).Select(i => $"{7 + i} 0 R"));
        objs.Add((6, $"{DocElem} /K [{kids}] >>", null));
        for (var i = 0; i < 200; i++)
            objs.Add((7 + i, $"<< /Type /StructElem /S /Div /P 6 0 R /A << {attrs} >> >>", null));

        var ex = Assert.Throws<InvalidOperationException>(() => StructureDumper.Dump(RawPdf.Doc("", "", "/K [6 0 R]", [.. objs])));
        Assert.Contains("Formatted-value budget", ex.Message);
    }

    [Fact]
    public void LongStringsAndNames_areCapped()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /T ({new string('t', 300)}) /N /{new string('n', 250)} >>", null));

        var line = Lines(pdf).Single(l => l.StartsWith("  Document", StringComparison.Ordinal));
        Assert.Contains(new string('t', 200) + "…(+100)", line);
        Assert.Contains(new string('n', 200) + "…(+50)", line);
        Assert.DoesNotContain(new string('t', 201), line);
    }

    [Fact]
    public void IntegersOutsideIntRange_arePrintedAsBadNotWrapped()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 4294967296>> BDC EMC",
            "/StructParents 4294967296",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /K [4294967296 << /Type /MCR /MCID 4294967296 /Pg 3 0 R >>] >>", null),
            (9, "<< /Nums [4294967296 [] 0 []] >>", null));

        var lines = Lines(pdf);
        Has("    BAD-MCID", lines);
        Has("BAD-MCID", lines);
        Has("PARENTTREE BAD-KEY", lines);
        Has("BAD-KEY StructParents p1", lines);
        Lacks("mcid 0 p1", lines);
    }

    [Fact]
    public void MalformedContainers_areReportedNotSkipped()
    {
        var pdf = RawPdf.Doc(
            "",
            "/Annots 99 0 R",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /Pg 2 0 R /K [11 0 R << /Type /MCR /MCID 0 /Stm 5 /Pg 3 0 R >> 5 0 R 6 0 R] >>", null),
            (9, "<< /Kids [10 0 R 10 0 R 12 0 R 13 0 R] >>", null),
            (10, "<< /Nums [0 [] 1] >>", null),
            (11, "0", null),
            (12, "<< /Nums 5 >>", null),
            (13, "<< /Kids [13 0 R] >>", null));

        var lines = Lines(pdf);
        Has("    mcid 0 p?", lines);
        Has("    BAD-PG", lines);
        Lacks("NO-PG", lines);
        Has("    BAD-STM", lines);
        Has("    CYCLE root", lines);
        Has("    CYCLE", lines);
        Has("ANNOTS UNRESOLVED", lines);
        Has("PARENTTREE ODD-NUMS", lines);
        Has("PARENTTREE SHARED-NODE", lines);
        Has("PARENTTREE BAD-NUMS", lines);
        Has("PARENTTREE CYCLE", lines);
    }

    [Fact]
    public void ObjrToANonAnnotation_isCheckedThroughItsOwnStructParent()
    {
        byte[] Build(string key) => RawPdf.Doc(
            "",
            "/StructParents 0",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 3",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /Figure /P 6 0 R /Pg 3 0 R /K [<< /Type /OBJR /Pg 3 0 R /Obj 8 0 R >>] >>", null),
            (8, $"<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] /StructParent {key} >>", "0 0 1 1 re f"),
            (9, "<< /Nums [0 [] 1 7 0 R] >>", null));

        Has("0 problem line(s)", Lines(Build("1")));
        Has(
            "PARENTTREE MISMATCH OBJR target Form in Document/Figure[0]: parent tree has nothing, element holding the OBJR is Document/Figure[0]",
            Lines(Build("2")));
    }


    [Fact]
    public void McrWhoseStmIsAnAppearanceStream_isStmNotRunNotMissing()
    {
        var pdf = RawPdf.Doc(
            "",
            "/Annots [9 0 R]",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /K [<< /Type /MCR /Pg 3 0 R /Stm 10 0 R /MCID 0 >>] >>", null),
            (9, "<< /Type /Annot /Subtype /Widget /Rect [0 0 1 1] /AP << /N 10 0 R >> >>", null),
            (10, "<< /Type /XObject /Subtype /Form /BBox [0 0 1 1] >>", "/P <</MCID 0>> BDC EMC"));

        var lines = Lines(pdf);
        Has("STM-NOT-RUN mcid 0 in Document/P[0]", lines);
        Lacks("MISSING", lines);
    }

    [Fact]
    public void ContentsAndTabs_arePrinted()
    {
        var pdf = RawPdf.Doc(
            "",
            "/Tabs /S /Annots [9 0 R]",
            "",
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /Contents (Hi) >>", null));

        var lines = Lines(pdf);
        Has("Tabs: /S", lines);
        Has("annot 0: Subtype=Link StructParent=none Contents=\"Hi\" QuadPoints=absent dest=none", lines);
        Has("Tabs: absent", Lines(RawPdf.Doc("", "", "")));
    }

    [Fact]
    public void AnnotationStructParent_pointingNowhereOrAtANonElement_isMarked()
    {
        var pdf = RawPdf.Doc(
            "",
            "/Annots [9 0 R 10 0 R]",
            "/K [6 0 R] /ParentTree 11 0 R",
            (6, $"{DocElem} >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /StructParent 7 >>", null),
            (10, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /StructParent 8 >>", null),
            (11, "<< /Nums [8 3 0 R] >>", null));

        var lines = Lines(pdf);
        Has("annot 0: Subtype=Link StructParent=7 -> NO-ENTRY Contents=absent QuadPoints=absent dest=none", lines);
        Has("annot 1: Subtype=Link StructParent=8 -> NOT-AN-ELEMENT Contents=absent QuadPoints=absent dest=none", lines);
        Has("4 problem line(s)", lines); // two annotation fields and two NO-OBJR lines
    }

    [Fact]
    public void ElementsHoldingTheSameMcid_areReported()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC EMC",
            "/StructParents 0",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /Pg 3 0 R /K [7 0 R 8 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>", null),
            (8, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>", null),
            (9, "<< /Nums [0 [7 0 R]] >>", null));

        Has("DUP-HOLDER mcid 0 p1: Document/P[0], Document/P[1]", Lines(pdf));
    }

    [Fact]
    public void McrWithItsOwnPg_overridesTheElementsPg()
    {
        var pdf = RawPdf.DocPages(
            ["", "/P <</MCID 0>> BDC EMC"],
            ["", "/StructParents 0"],
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 100 0 R /K [<< /Type /MCR /Pg 101 0 R /MCID 0 >>] >>", null),
            (9, "<< /Nums [0 [7 0 R]] >>", null));

        var lines = Lines(pdf);
        Has("      MCR mcid 0 p2", lines);
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void NodeCap_isReachedThroughParentTreeEntries()
    {
        static byte[] Tree(int pairs) => RawPdf.Doc(
            "",
            "",
            "/K [6 0 R] /ParentTree 9 0 R",
            (6, $"{DocElem} >>", null),
            (9, $"<< /Nums [{string.Join(' ', Enumerable.Range(0, pairs).Select(i => $"{i} null"))}] >>", null));

        Has("0 problem line(s)", Lines(Tree(StructureDumper.MaxNodes - 1)));
        var ex = Assert.Throws<InvalidOperationException>(() => StructureDumper.Dump(Tree(StructureDumper.MaxNodes)));
        Assert.Contains("node cap", ex.Message);
    }

    // ── Footer ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Footer_countsEveryMarkerTheDumpPrints()
    {
        var sample = new[]
        {
            "known defects: MISSING-NOTHING",
            "  UNRESOLVED",
            "      NO-PG",
            "UNMARKED show \"x\"",
            "DIAG ResourceMissing",
            "show \"MISSING in a string\"",
            "annot 0: Subtype=Link StructParent=7 -> NO-ENTRY Contents=absent",
            "annot 1: Subtype=Link StructParent=none Contents=absent",
            "PARENTTREE MISMATCH mcid 0 p1: x",
            "DUP-KEY 0",
        };

        Assert.Equal(7, StructureDumper.CountProblems(sample));
    }

    // ── ToUnicodeMap ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ToUnicodeMap_neverThrowsOnWideRanges()
    {
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes("1 beginbfrange <00000000> <FFFFFFFF> <0041> endbfrange"));

        Assert.True(map.TryMap(0x80000001, 4, out var text));
        Assert.Single(text);
        Assert.True(map.TryMap(0xFFFFFFFF, 4, out _));
        Assert.False(map.TryMap(0x41, 2, out _));
    }

    [Fact]
    public void ToUnicodeMap_keysByteLengthApart_andDocumentsItsOverlapRules()
    {
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes(
            "2 beginbfchar <41> <0058> <41> <0059> endbfchar "
            + "2 beginbfrange <10> <20> <0041> <15> <18> <0061> endbfrange"));

        Assert.True(map.TryMap(0x41, 1, out var one));
        Assert.Equal("Y", one); // a repeated bfchar is later-wins
        Assert.False(map.TryMap(0x41, 2, out _)); // a one-byte entry does not answer a two-byte code
        Assert.True(map.TryMap(0x16, 1, out var overlap));
        Assert.Equal("G", overlap); // overlapping ranges: the first in the file wins
    }

    [Fact]
    public void ToUnicodeMap_answersEveryCodeOfManyRanges()
    {
        var sb = new StringBuilder("20000 beginbfrange\n");
        for (var i = 0; i < 20000; i++)
            sb.Append($"<{i * 2:X4}> <{(i * 2) + 1:X4}> <{0x4E00 + (i % 1000):X4}>\n");
        sb.Append("endbfrange");
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes(sb.ToString()));

        for (var i = 0; i < 20000; i++)
        {
            Assert.True(map.TryMap((uint)(i * 2) + 1, 2, out var text));
            Assert.Equal(((char)((0x4E00 + (i % 1000)) + 1)).ToString(), text);
        }

        Assert.False(map.TryMap(40000, 2, out _));
    }
}
