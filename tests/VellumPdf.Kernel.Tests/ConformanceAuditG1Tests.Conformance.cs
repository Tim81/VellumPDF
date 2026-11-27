// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.IO;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using static VellumPdf.Kernel.Tests.ConformanceAuditG1Support;
using TextRun = VellumPdf.Layout.Elements.TextRun;

namespace VellumPdf.Kernel.Tests;

// What ISO 19005-2 (clauses 6.1.13, 6.3, 6.5, 6.7.6) and ISO 14289-1 (7.17, 7.18) say about the
// navigation constructs, and the oracles that check the file against them. ISO 19005-2 is not held in
// the repository; the clause numbers are those of the notes taken from a licensed copy.
public sealed partial class ConformanceAuditG1Tests
{
    // ── ISO 19005-2, 6.3 and 6.5: annotations and actions ───────────────────────────────────

    [Theory]
    [InlineData(PdfConformance.PdfA2b)]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    public void Iso19005_2_6_3_ALinkAnnotationNeedsFlagsAndNoAppearance(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var links = AnnotationsOf(pdf);

        Assert.NotEmpty(links);
        // 6.3.1: only a type ISO 32000-1 defines, and none of 3D, Sound, Screen or Movie.
        Assert.All(links, l => Assert.Equal("Link", Subtype(l.Dict)));
        // 6.3.2: F is present; Print is 1; Hidden, Invisible, ToggleNoView and NoView are 0.
        Assert.All(links, l =>
        {
            var flags = Assert.IsType<PdfInteger>(l.Dict.Get(N("F"))).Value;
            Assert.Equal(4, flags & 295); // Invisible 1, Hidden 2, Print 4, NoView 32 and ToggleNoView 256
        });
        // 6.3.3: a Link is exempt from the appearance dictionary, and none is written, so the rule
        // that an AP holds only N has nothing to apply to.
        Assert.All(links, l => Assert.Null(l.Dict.Get(N("AP"))));
    }

    [Theory]
    [InlineData(PdfConformance.PdfA2b)]
    [InlineData(PdfConformance.PdfA2u)]
    [InlineData(PdfConformance.PdfA2a)]
    public void Iso19005_2_6_5_TheOnlyActionsAreUriActionsAndNothingHasAdditionalActions(PdfConformance claim)
    {
        using var pdf = new Pdf(Navigation(claim));
        var links = AnnotationsOf(pdf);

        var actions = links.Where(l => l.Dict.Get(N("A")) is not null).Select(l => pdf.Dict(l.Dict.Get(N("A")))).ToList();
        Assert.NotEmpty(actions);
        // 6.5.1 lists the actions it refuses (Launch, Sound, Movie, ResetForm, ImportData, Hide,
        // SetOCGState, Rendition, Trans, GoTo3DView, JavaScript, Named other than four); URI is not one.
        Assert.All(actions, a => Assert.Equal("URI", Assert.IsType<PdfName>(a.Get(N("S"))).Value));
        // 6.5.2: no AA on the catalog, on a page, or on an annotation.
        Assert.Null(pdf.Catalog.Get(N("AA")));
        Assert.Null(pdf.Catalog.Get(N("OpenAction")));
        Assert.All(pdf.Pages(), p => Assert.Null(p.Get(N("AA"))));
        Assert.All(links, l => Assert.Null(l.Dict.Get(N("AA"))));
        // No JavaScript name tree either.
        Assert.Null(pdf.NameDictionary!.Get(N("JavaScript")));
    }

    // ── ISO 19005-2, 6.1.13 and the oracles ─────────────────────────────────────────────────

    [Theory]
    [InlineData(PdfConformance.PdfA2b, "2b")]
    [InlineData(PdfConformance.PdfA2u, "2u")]
    [InlineData(PdfConformance.PdfA2a, "2a")]
    public void Iso19005_2_TheNavigationFixtureIsAcceptedByVeraPdfAndQpdf(PdfConformance claim, string flavour)
    {
        var bytes = Navigation(claim);

        // The change is in the file: destinations, labels, an outline and links of both kinds.
        using (var pdf = new Pdf(bytes))
        {
            Assert.NotNull(pdf.Catalog.Get(N("Outlines")));
            Assert.NotNull(pdf.Catalog.Get(N("PageLabels")));
            Assert.NotEmpty(DestsTree(pdf).Entries);
            Assert.Contains(AnnotationsOf(pdf), l => l.Dict.Get(N("Dest")) is not null);
            Assert.Contains(AnnotationsOf(pdf), l => l.Dict.Get(N("A")) is not null);
        }

        Assert.Empty(VeraPdfFailures(flavour, bytes));
        QpdfChecks(bytes);
    }

    [Fact]
    public void Iso19005_2_6_1_13_EveryNavigationStringIsWithinTheLimits()
    {
        // 6.1.13: a string is at most 32,767 bytes, a name at most 127, an integer within 32 bits.
        using var pdf = new Pdf(Navigation(PdfConformance.PdfA2u));
        var strings = new List<byte[]>();
        var integers = new List<long>();
        var names = new List<string>();

        void Collect(PdfObject? value, HashSet<PdfObject> seen)
        {
            switch (value)
            {
                case null or PdfIndirectReference:
                    return;
                case PdfLiteralString s:
                    strings.Add(s.Bytes.ToArray());
                    return;
                case PdfInteger i:
                    integers.Add(i.Value);
                    return;
                case PdfName n:
                    names.Add(n.Value);
                    return;
                case PdfArray a when seen.Add(a):
                    foreach (var item in a.Items())
                        Collect(item, seen);
                    return;
                case PdfDictionary d when seen.Add(d):
                    foreach (var entry in d.Entries)
                    {
                        names.Add(entry.Key.Value);
                        Collect(entry.Value, seen);
                    }

                    return;
            }
        }

        var seen = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        foreach (var link in AnnotationsOf(pdf))
            Collect(link.Dict, seen);
        foreach (var node in Flatten(ReadOutline(pdf).Top))
            Collect(node.Dict, seen);
        foreach (var entry in LabelsTree(pdf).Entries)
            Collect(pdf.Resolve(entry.Value), seen);
        foreach (var entry in DestsTree(pdf).Entries)
        {
            strings.Add(entry.NameKey!);
            Collect(pdf.Resolve(entry.Value), seen);
        }

        Assert.True(strings.Count > 50 && integers.Count > 50 && names.Count > 50, "the walk reached the constructs");
        Assert.All(strings, s => Assert.True(s.Length <= 32767));
        Assert.All(names, n => Assert.True(Encoding.UTF8.GetByteCount(n) <= 127));
        Assert.All(integers, i => Assert.InRange(i, int.MinValue, int.MaxValue));
    }

    [Fact]
    public void Iso19005_2_6_1_13_TheBoundsTheLibraryAppliesToNavigationStringsStayWithinTheLimits()
    {
        // A destination name is at most 127 characters (a name object is limited to 127 bytes, and the
        // name is also a string key); the prefix of a page label is at most 16,382 UTF-16 units, which
        // are 32,766 bytes with the byte order mark; a name-tree key is at most 32,767 bytes.
        using var doc = new PdfDocument { Timestamp = Pinned };
        var page = doc.AddPage(new PdfRectangle(0, 0, 200, 200));
        doc.AddNamedDestination(new string('n', 127), page, 0, 100);
        Assert.Throws<ArgumentException>(() => doc.AddNamedDestination(new string('n', 128), page, 0, 100));
        doc.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, new string('p', 16_382));
        Assert.Throws<ArgumentException>(() => doc.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, new string('p', 16_383)));
        using var pdf = new Pdf(Save(doc));

        var name = Assert.Single(DestsTree(pdf).Entries).NameKey!;
        Assert.Equal(127, name.Length);
        var prefix = Assert.IsType<PdfLiteralString>(pdf.Dict(Assert.Single(LabelsTree(pdf).Entries).Value).Get(N("P"))).Bytes;
        Assert.Equal(32_766, prefix.Length);

        var registry = new PdfObjectRegistry();
        _ = NameTreeWriter.Write([new KeyValuePair<byte[], PdfObject>(new byte[32_767], PdfNull.Instance)], registry);
        Assert.Throws<ArgumentException>(() =>
            NameTreeWriter.Write([new KeyValuePair<byte[], PdfObject>(new byte[32_768], PdfNull.Instance)], registry));
    }

    // Not yet true: a URI and a bookmark title of any length are written. Draft issue G1-01.
    [Fact(Skip = "#666: a URI or a bookmark title over 32,767 bytes is written and breaks ISO 19005-2, 6.1.13 (veraPDF 6.1.13-3)")]
    public void Iso19005_2_6_1_13_AUriOrABookmarkTitleLongerThan32767BytesIsRefusedOrNotWritten()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfA2b);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Paragraph([new TextRun("x", new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.com/" + new string('a', 40000) })]));

        // Either the save refuses it, or the file it writes has no string over the limit.
        byte[] bytes;
        try
        {
            bytes = Save(doc);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        Assert.DoesNotContain("6.1.13-3", VeraPdfFailures("2b", bytes));
    }

    // ── ISO 19005-2, 6.7.6, and ISO 14289-1, 7.18: tagging and Contents of links (#550) ──────

    [Fact(Skip = "#550: a link is written untagged and without Contents; ISO 19005-2, 6.7.6 (level A, should) and ISO 14289-1, 7.18.5 (shall)")]
    public void Iso19005_2_6_7_6_AnnotationsThatShowNoTextCarryContents()
    {
        using var pdf = new Pdf(Navigation(PdfConformance.PdfA2a));
        Assert.All(AnnotationsOf(pdf), l => Assert.NotNull(l.Dict.Get(N("Contents"))));
    }

    [Fact(Skip = "#550: a link is written untagged and without Contents; ISO 14289-1, 7.18.5 and 7.18.1")]
    public void Iso14289_1_7_18_5_LinksAreTaggedAsLinkElementsAndCarryContents()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfUA1);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Paragraph([new TextRun("link", new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.com/" })]));
        var bytes = Save(doc);

        using var pdf = new Pdf(bytes);
        var link = Assert.Single(AnnotationsOf(pdf));
        Assert.NotNull(link.Dict.Get(N("Contents")));
        Assert.NotNull(link.Dict.Get(N("StructParent")));
        Assert.Empty(VeraPdfFailures("ua1", bytes));
    }

    [Fact]
    public void Iso14289_1_7_18_5_ALinkUnderUa1FailsExactlyTheRulesOfIssue550()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfUA1);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Paragraph([new TextRun("link", new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.com/" })]));
        var bytes = Save(doc);

        using var pdf = new Pdf(bytes);
        var link = Assert.Single(AnnotationsOf(pdf));
        // The construct is present, and shows the gap: no Contents, no StructParent, no Link element.
        Assert.Null(link.Dict.Get(N("Contents")));
        Assert.Null(link.Dict.Get(N("StructParent")));
        // What the oracle says, pinned so that the day #550 is fixed this test fails and is replaced
        // by the skipped one above.
        Assert.Equal(["7.18.1-2", "7.18.5-1", "7.18.5-2"], VeraPdfFailures("ua1", bytes).Distinct().Order());
    }

    // ── ISO 14289-1, 7.18.3 and 7.17 ────────────────────────────────────────────────────────

    [Fact]
    public void Iso14289_1_7_18_3_EveryPageWithAnAnnotationDeclaresTabsS()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfUA1);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        var style = new TextStyle { FontRef = font, FontSize = 11, LinkUri = "https://example.com/" };
        for (var i = 0; i < 3; i++)
        {
            doc.Add(new Paragraph([new TextRun("link " + i, style)]));
            doc.Add(new PageBreak());
        }

        doc.Add(new Paragraph("a page without a link", new TextStyle { FontRef = font, FontSize = 11 }));
        var bytes = Save(doc);

        using var pdf = new Pdf(bytes);
        var pages = pdf.Pages();
        Assert.Equal(4, pages.Count);
        for (var i = 0; i < 3; i++)
        {
            Assert.NotNull(pages[i].Get(PdfName.Annots));
            Assert.Equal("S", Assert.IsType<PdfName>(pages[i].Get(N("Tabs"))).Value);
        }

        Assert.Null(pages[3].Get(PdfName.Annots));
        Assert.DoesNotContain("7.18.3-1", VeraPdfFailures("ua1", bytes));
    }

    [Fact]
    public void Iso14289_1_7_17_And7_2_ADocumentWithAnOutlineAndLabelsPassesUa1AndDeclaresItsLanguage()
    {
        var parts = NavigationDocument(PdfConformance.PdfUA1, withLinks: false);
        var bytes = Save(parts.Doc);

        using (var pdf = new Pdf(bytes))
        {
            // 7.17: the outline is there, in reading order of the headings; 7.2 (Matterhorn 11-003):
            // the language of the bookmark text is determinable, which here is the catalog's.
            var (_, top) = ReadOutline(pdf);
            Assert.Equal(8, Flatten(top).Count());
            Assert.Equal("en-US", TextString(pdf.Catalog.Get(N("Lang"))!));
            Assert.NotNull(pdf.Catalog.Get(N("PageLabels")));
            Assert.Empty(AnnotationsOf(pdf));
            // Anchors that nothing links to are still destinations.
            Assert.Equal(Anchors.Length, DestsTree(pdf).Entries.Count);
        }

        Assert.Empty(VeraPdfFailures("ua1", bytes));
        QpdfChecks(bytes);
    }

    [Fact]
    public void Iso14289_1_7_18_5_ALinkToAnAnchorIsRefusedUnderUa1BecauseItWouldBeUntagged()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfUA1);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Paragraph("target", new TextStyle { FontRef = font, FontSize = 11 }) { Anchor = "target" });
        doc.Add(new Paragraph([new TextRun("go", new TextStyle { FontRef = font, FontSize = 11, LinkAnchor = "target" })]));

        var failure = Assert.Throws<InvalidOperationException>(() => Save(doc));
        Assert.Contains("7.18.5", failure.Message, StringComparison.Ordinal);
    }

    // ── The same fixture with no claim ──────────────────────────────────────────────────────

    [Fact]
    public void Iso32000_1_TheNavigationFixtureWithoutAClaimPassesQpdf()
    {
        var bytes = Navigation(PdfConformance.None);
        using (var pdf = new Pdf(bytes))
            Assert.NotEmpty(AnnotationsOf(pdf));

        QpdfChecks(bytes);
    }

    // ── Destinations and text strings that name nothing or are not text (#737 and #738) ──

    [Fact(Skip = "#737: an outline entry for a page the document does not hold is written as /Dest [null /XYZ ...]; ISO 32000-1, 12.3.2.2")]
    public void Iso32000_1_12_3_2_2_AnOutlineEntryForAPageOfAnotherDocumentIsRefused()
    {
        using var other = new PdfDocument();
        var foreign = other.AddPage();
        using var doc = new PdfDocument { Timestamp = Pinned };
        doc.AddPage();
        doc.AddOutlineEntry(new VellumPdf.Annotations.PdfOutlineEntry { Title = "x", DestPage = foreign });

        // Either the save refuses it, or the destination's first operand is an indirect reference.
        byte[] bytes;
        try
        {
            bytes = Save(doc);
        }
        catch (InvalidOperationException)
        {
            return;
        }

        using var pdf = new Pdf(bytes);
        var item = Assert.Single(Flatten(ReadOutline(pdf).Top));
        Assert.IsType<PdfIndirectReference>(pdf.Array(item.Dict.Get(N("Dest")))[0]);
    }

    [Fact(Skip = "#738: an unpaired surrogate in a bookmark title is written as UTF-16BE; ISO 32000-2, 7.9.2.2.1")]
    public void Iso32000_2_7_9_2_2_ABookmarkTitleIsWellFormedUtf16()
    {
        var doc = NewLayoutDocument(PdfConformance.PdfA2b);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        doc.Add(new Heading([new TextRun("h", new TextStyle { FontRef = font, FontSize = 11 })]) { BookmarkTitle = "lone \uD800 end" });
        using var pdf = new Pdf(Save(doc));

        var bytes = Assert.IsType<PdfLiteralString>(Assert.Single(Flatten(ReadOutline(pdf).Top)).Dict.Get(N("Title"))).Bytes.ToArray();
        // Strict decoding throws on an unpaired surrogate.
        var strict = new UnicodeEncoding(bigEndian: true, byteOrderMark: false, throwOnInvalidBytes: true);
        _ = strict.GetString(bytes, 2, bytes.Length - 2);
    }
}
