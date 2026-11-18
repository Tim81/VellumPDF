// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// Pins <see cref="StructureDumper"/> on documents written out by hand: constructs the Layout writer
/// does not produce yet (MCR and OBJR kids, attribute arrays, forms with their own MCIDs) and
/// deliberately wrong ones (a parent tree that points at the wrong element). A dumper that only ever
/// saw writer output could not be shown to notice any of them.
/// </summary>
public sealed class StructureDumperTests
{
    private const string DocElem = "<< /Type /StructElem /S /Document /P 5 0 R";

    private static string[] Lines(byte[] pdf) => StructureDumper.Dump(pdf).Split('\n');

    private static string Dump(byte[] pdf) => StructureDumper.Dump(pdf);

    // Failure message carries the whole dump, so a mismatch shows what was printed instead.
    private static void Has(string expected, IEnumerable<string> lines)
    {
        var all = lines.ToList();
        Assert.True(all.Contains(expected), "missing line: " + expected + Environment.NewLine + "--- dump ---" + Environment.NewLine + string.Join(Environment.NewLine, all));
    }

    // ── Kids ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void McrKid_withItsOwnPg_printsPageAndNoProblem()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC EMC",
            "/StructParents 0",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1",
            (6, $"{DocElem} /K [<< /Type /MCR /Pg 3 0 R /MCID 0 >>] >>", null),
            (9, "<< /Nums [0 [6 0 R]] >>", null));

        var lines = Lines(pdf);
        Has("    MCR mcid 0 p1", lines);
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void McrKid_withStm_isMatchedAgainstTheFormsOwnMcidsAndStructParents()
    {
        var pdf = RawPdf.Doc(
            "/Fm0 Do",
            "/StructParents 0 /Resources << /XObject << /Fm0 8 0 R >> >>",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 2",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /K [<< /Type /MCR /Pg 3 0 R /Stm 8 0 R /MCID 0 >>] >>", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /StructParents 1 >>", "/P <</MCID 0>> BDC 0 0 5 5 re f EMC"),
            (9, "<< /Nums [0 [] 1 [7 0 R]] >>", null));

        var dump = Dump(pdf);
        var lines = dump.Split('\n');
        Has("      MCR mcid 0 p1 stm form1", lines);
        Has("UNMARKED form1", lines);
        Has("  BDC /P {MCID=0}", lines);
        Has("    path fill", lines);

        // The only marker is the form drawn outside any sequence; the MCR matched the form's MCID.
        Has("1 problem line(s)", lines);

        // The same MCID 0 on the page's own stream would be a different key: an MCR without /Stm
        // for it must not match the form's content.
        var wrong = RawPdf.Doc(
            "/Fm0 Do",
            "/StructParents 0 /Resources << /XObject << /Fm0 8 0 R >> >>",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 2",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /K [<< /Type /MCR /Pg 3 0 R /MCID 0 >>] >>", null),
            (8, "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /StructParents 1 >>", "/P <</MCID 0>> BDC 0 0 5 5 re f EMC"),
            (9, "<< /Nums [0 [] 1 [7 0 R]] >>", null));
        var wrongLines = Lines(wrong);
        Has("ORPHAN mcid 0 p1 form1", wrongLines);
        Has("MISSING mcid 0 p1 in Document/P[0]", wrongLines);
    }

    [Fact]
    public void ObjrKid_toALinkAnnotation_printsOnePathAndNoProblem()
    {
        var pdf = RawPdf.Doc(
            "",
            "/StructParents 0 /Annots [9 0 R]",
            "/K [6 0 R] /ParentTree 10 0 R /ParentTreeNextKey 2",
            (6, $"{DocElem} /K [8 0 R] >>", null),
            (8, "<< /Type /StructElem /S /Link /P 6 0 R /Pg 3 0 R /K [<< /Type /OBJR /Pg 3 0 R /Obj 9 0 R >>] >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /StructParent 1 /Contents (c) /QuadPoints [0 0 10 0 0 10 10 10] /A << /S /URI /URI (http://x) >> >>", null),
            (10, "<< /Nums [0 [] 1 8 0 R] >>", null));

        var lines = Lines(pdf);
        Has("      OBJR Link p1", lines);
        Has(
            "annot 0: Subtype=Link StructParent=1 -> Document/Link[0] Contents=\"c\" QuadPoints=present dest=URI",
            lines);
        Has("0 problem line(s)", lines);
    }

    [Fact]
    public void ObjrKid_whoseAnnotationPointsElsewhere_printsMismatch()
    {
        var pdf = RawPdf.Doc(
            "",
            "/StructParents 0 /Annots [9 0 R]",
            "/K [6 0 R] /ParentTree 10 0 R /ParentTreeNextKey 3",
            (6, $"{DocElem} /K [7 0 R 8 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R >>", null),
            (8, "<< /Type /StructElem /S /Link /P 6 0 R /Pg 3 0 R /K [<< /Type /OBJR /Pg 3 0 R /Obj 9 0 R >>] >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 10 10] /StructParent 2 >>", null),
            (10, "<< /Nums [0 [] 2 7 0 R] >>", null));

        Has(
            "PARENTTREE MISMATCH annot p1 #0: parent tree has Document/P[0], element holding the OBJR is Document/Link[1]",
            Lines(pdf));
    }

    [Fact]
    public void AnnotationWithStructParent_butNoObjr_printsNoObjr()
    {
        var pdf = RawPdf.Doc(
            "",
            "/Annots [9 0 R]",
            "/K [6 0 R] /ParentTree 10 0 R /ParentTreeNextKey 2",
            (6, $"{DocElem} >>", null),
            (9, "<< /Type /Annot /Subtype /Link /Rect [0 0 1 1] /StructParent 1 /Dest [3 0 R /Fit] >>", null),
            (10, "<< /Nums [1 6 0 R] >>", null));

        var lines = Lines(pdf);
        Has("NO-OBJR annot p1 #0", lines);
        Has("annot 0: Subtype=Link StructParent=1 -> Document Contents=absent QuadPoints=absent dest=GoTo p1 Fit", lines);
    }

    [Fact]
    public void MixedKids_keepTheirOrder()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC EMC /Span <</MCID 2>> BDC EMC /P <</MCID 1>> BDC EMC",
            "/StructParents 0",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1",
            (6, $"{DocElem} /K [7 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K [0 8 0 R 1] >>", null),
            (8, "<< /Type /StructElem /S /Span /P 7 0 R /Pg 3 0 R /K 2 >>", null),
            (9, "<< /Nums [0 [7 0 R 7 0 R 8 0 R]] >>", null));

        var lines = Lines(pdf).ToList();
        var at = lines.IndexOf("      mcid 0 p1");
        Assert.True(at >= 0);
        Assert.Equal("      Span", lines[at + 1]);
        Assert.Equal("        mcid 2 p1", lines[at + 2]);
        Assert.Equal("      mcid 1 p1", lines[at + 3]);
        Has("0 problem line(s)", lines);
    }

    // ── Element line ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ElementLine_printsEveryKeySorted_withRevisionNumbers_andRoleMapping()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R] /RoleMap << /MyHead /H1 >>",
            (6, $"{DocElem} /K [7 0 R 8 0 R] >>", null),
            (7, "<< /Type /StructElem /S /MyHead /P 6 0 R /Lang (nl-NL) /T (title) /E (Expansion) /ID (id1) "
                + "/Alt (Alt text) /ActualText <FEFF00480069> /C [/Cls 3] "
                + "/A [<< /O /List /ListNumbering /Decimal >> 2 << /O /Layout /BBox [72.5 0.004 -0.004 100] >>] >>", null),
            (8, "<< /Type /StructElem /S /Weird /P 6 0 R >>", null));

        var lines = Lines(pdf);
        Has(
            "    MyHead→H1 A=[{ListNumbering=/Decimal O=/List}@2 {BBox=[72.5 0 0 100] O=/Layout}] ActualText=\"Hi\" "
            + "Alt=\"Alt text\" C=[/Cls@3] E=\"Expansion\" ID=\"id1\" Lang=\"nl-NL\" T=\"title\"",
            lines);
        Has("    Weird→?", lines);
    }

    [Fact]
    public void ElementLine_escapesInvisibleCharacters()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /Alt <FEFF006100AD0062200B006300A00064> >>", null));

        Has("  Document Alt=\"a\\u{00AD}b\\u{200B}c\\u{00A0}d\"", Lines(pdf));
    }

    // ── Content ───────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ArtifactSequence_printsEveryPropertyKey()
    {
        var pdf = RawPdf.Doc(
            "/Artifact <</Type /Pagination /Subtype /Footer /BBox [10 20.5 30 40] /Attached [/Bottom]>> BDC 0 0 1 1 re f EMC 0 0 1 1 re S",
            "",
            "");

        var lines = Lines(pdf);
        Has("BDC /Artifact {Attached=[/Bottom] BBox=[10 20.5 30 40] Subtype=/Footer Type=/Pagination}", lines);
        Has("  path fill", lines);
        Has("UNMARKED path stroke", lines);
    }

    [Fact]
    public void UnbalancedEmc_isPrinted()
    {
        var lines = Lines(RawPdf.Doc("EMC /P <</MCID 0>> BDC EMC EMC", "", ""));

        Assert.Equal(2, lines.Count(l => l == "UNBALANCED-EMC"));
    }

    [Fact]
    public void Show_decodesThroughToUnicode_andFallsBackToHex()
    {
        const string cmap =
            "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n"
            + "1 begincodespacerange <0000> <FFFF> endcodespacerange\n"
            + "2 beginbfchar <0001> <0048> <0004> <00AD> endbfchar\n"
            + "1 beginbfrange <0002> <0003> <0065> endbfrange\n"
            + "endcmap end end";
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC BT /F1 12 Tf <00010002000300040009> Tj [<0001> -20 <0002>] TJ /F2 12 Tf (AB) Tj /F3 12 Tf <0001> Tj ET EMC BT /F1 12 Tf <0001> Tj ET",
            "/Resources << /Font << /F1 7 0 R /F2 8 0 R /F3 9 0 R >> >>",
            "",
            (7, "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /ToUnicode 10 0 R >>", null),
            (8, "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", null),
            (9, "<< /Type /Font /Subtype /Type0 /BaseFont /Y /Encoding /Identity-H >>", null),
            (10, "<< >>", cmap));

        var lines = Lines(pdf);
        Has("  show \"Hef\\u{00AD}\\u{FFFD}\"", lines);
        Has("  show \"He\"", lines);
        Has("  show <4142>", lines);
        Has("  show <0001>", lines);
        Has("UNMARKED show \"H\"", lines);
    }

    [Fact]
    public void ToUnicodeMap_readsBfrangeArrayForm()
    {
        var map = ToUnicodeMap.Parse(Encoding.Latin1.GetBytes("1 beginbfrange <10> <12> [<0041> <0042> <0043>] endbfrange"));

        Assert.True(map.TryMap(0x11, 1, out var t));
        Assert.Equal("B", t);
        Assert.False(map.TryMap(0x13, 1, out _));
    }

    // ── Problem lines ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParentTree_pointingAtTheWrongElement_printsMismatch()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC EMC",
            "/StructParents 0",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 1",
            (6, $"{DocElem} /K [7 0 R 8 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /Pg 3 0 R /K 0 >>", null),
            (8, "<< /Type /StructElem /S /Span /P 6 0 R /Pg 3 0 R >>", null),
            (9, "<< /Nums [0 [8 0 R]] >>", null));

        Has(
            "PARENTTREE MISMATCH mcid 0 p1: parent tree has Document/Span[1], element holding it is Document/P[0]",
            Lines(pdf));
    }

    [Fact]
    public void ContentAndStructureDisagreement_printsOrphanMissingDupMcidNoStructParentsAndDupKey()
    {
        var pdf = RawPdf.Doc(
            "/P <</MCID 0>> BDC EMC /P <</MCID 0>> BDC EMC /P <</MCID 1>> BDC EMC",
            "",
            "/K [6 0 R] /ParentTree 9 0 R /ParentTreeNextKey 0",
            (6, $"{DocElem} /Pg 3 0 R /K [0 2] >>", null),
            (9, "<< /Nums [0 [] 0 []] >>", null));

        var lines = Lines(pdf);
        Has("DUP-KEY 0", lines);
        Has("NO-STRUCTPARENTS p1", lines);
        Has("DUP-MCID mcid 0 p1", lines);
        Has("ORPHAN mcid 1 p1", lines);
        Has("MISSING mcid 2 p1 in Document", lines);
        Has("ParentTreeNextKey: 0 (exceeds every key: no)", lines);
    }

    [Fact]
    public void KidWithoutAPage_printsNoPg()
    {
        var lines = Lines(RawPdf.Doc("", "", "/K [6 0 R]", (6, $"{DocElem} /K [0] >>", null)));

        Has("    mcid 0 p?", lines);
        Has("    NO-PG", lines);
    }

    [Fact]
    public void BothStructParentKeys_areReported()
    {
        var pdf = RawPdf.Doc("", "/StructParents 0 /StructParent 1", "/K [6 0 R]", (6, $"{DocElem} >>", null));

        Has("BOTH-STRUCTPARENT(S) p1", Lines(pdf));
    }

    [Fact]
    public void CyclicDuplicateAndUnresolvedKids_arePrintedNotSkipped()
    {
        var pdf = RawPdf.Doc(
            "",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /K [7 0 R 7 0 R 99 0 R] >>", null),
            (7, "<< /Type /StructElem /S /P /P 6 0 R /K [6 0 R] >>", null));

        var lines = Lines(pdf);
        Has("      CYCLE", lines);
        Has("    DUPLICATE-ELEMENT", lines);
        Has("    UNRESOLVED", lines);
    }

    // ── Caps ──────────────────────────────────────────────────────────────────────────────────

    private static byte[] NestedElements(int count)
    {
        var objs = new List<(int, string, string?)>();
        for (var i = 0; i < count; i++)
        {
            var kids = i + 1 < count ? $"/K [{7 + i} 0 R]" : "";
            objs.Add((6 + i, $"<< /Type /StructElem /S /Div /P {(i == 0 ? 5 : 5 + i)} 0 R {kids} >>", null));
        }

        return RawPdf.Doc("", "", "/K [6 0 R]", [.. objs]);
    }

    [Fact]
    public void ElementDepthCap_isRefusedJustAbove_andAcceptedAtTheCap()
    {
        Has("0 problem line(s)", Lines(NestedElements(StructureDumper.MaxElementDepth)));

        var ex = Assert.Throws<InvalidOperationException>(() => Dump(NestedElements(StructureDumper.MaxElementDepth + 1)));
        Assert.Contains("element depth cap", ex.Message);
    }

    private static byte[] ManyKids(int count) =>
        RawPdf.Doc("", "", "/K [6 0 R]", (6, $"{DocElem} /K [{string.Join(' ', Enumerable.Repeat("null", count))}] >>", null));

    [Fact]
    public void NodeCap_isRefusedJustAbove_andAcceptedAtTheCap()
    {
        // The Document element is node 1, so this many null kids lands exactly on the cap.
        // Each null kid prints UNRESOLVED, and the footer counts every one of them.
        Has("99999 problem line(s)", Lines(ManyKids(StructureDumper.MaxNodes - 1)));

        var ex = Assert.Throws<InvalidOperationException>(() => Dump(ManyKids(StructureDumper.MaxNodes)));
        Assert.Contains("node cap", ex.Message);
    }

    private static byte[] NestedParentTree(int depth)
    {
        var objs = new List<(int, string, string?)> { (6, $"{DocElem} >>", null) };
        for (var i = 0; i < depth; i++)
        {
            objs.Add((9 + i, i + 1 < depth ? $"<< /Kids [{10 + i} 0 R] >>" : "<< /Nums [] >>", null));
        }

        return RawPdf.Doc("", "", "/K [6 0 R] /ParentTree 9 0 R", [.. objs]);
    }

    [Fact]
    public void ParentTreeDepthCap_isRefusedJustAbove_andAcceptedAtTheCap()
    {
        Has("0 problem line(s)", Lines(NestedParentTree(StructureDumper.MaxParentTreeDepth)));

        var ex = Assert.Throws<InvalidOperationException>(() => Dump(NestedParentTree(StructureDumper.MaxParentTreeDepth + 1)));
        Assert.Contains("ParentTree depth cap", ex.Message);
    }

    [Fact]
    public void MarkedContentDepthCap_isRefusedJustAbove_andAcceptedAtTheCap()
    {
        static byte[] Nested(int n) => RawPdf.Doc(string.Concat(Enumerable.Repeat("/Span BMC ", n)), "", "");

        var lines = Lines(Nested(StructureDumper.MaxMarkedContentDepth));
        Has("UNCLOSED-MC 64 p1", lines);
        Has("1 problem line(s)", lines);

        var ex = Assert.Throws<InvalidOperationException>(() => Dump(Nested(StructureDumper.MaxMarkedContentDepth + 1)));
        Assert.Contains("Marked-content depth cap", ex.Message);
    }

    // ── Determinism ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Dump_isIdenticalUnderADecimalCommaCulture()
    {
        var pdf = RawPdf.Doc(
            "/Artifact <</BBox [10 20.5 30.125 40] >> BDC EMC",
            "",
            "/K [6 0 R]",
            (6, $"{DocElem} /A << /O /Layout /BBox [72.5 0.004 -0.004 100.333] >> >>", null));

        var invariant = Dump(pdf);
        Assert.Contains("72.5", invariant);
        Assert.Contains("30.13", invariant);

        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("nl-NL");
            Assert.Equal(invariant, Dump(pdf));
        }
        finally
        {
            CultureInfo.CurrentCulture = saved;
        }
    }
}
