// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VellumPdf.Canvas;
using VellumPdf.Document;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.Signing;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The general diagnostics channel and the opt-in strict switch. Every assertion is on exact item
/// fields, exact strings or exact bytes, because "an item exists" is also true of a channel that
/// reports the wrong thing.
/// </summary>
public sealed class LayoutDiagnosticsTests
{
    private const string NullStyleMessage =
        "A paragraph run with a null style yields no text and was saved without a style.";

    private static string StarMessage =>
        "U+2605 is not in WinAnsiEncoding and is written as '?'.";

    private static readonly PdfRectangle SmallPage = new(0, 0, 300, 200);

    private static Document NewDoc() => new() { PageSize = SmallPage, Margins = new EdgeInsets(50) };

    private static Paragraph NullStyleParagraph() => new([new TextRun("   ", null!)]);

    private static Document NullStyleDoc(bool strict = false)
    {
        var doc = NewDoc();
        doc.StrictDiagnostics = strict;
        doc.Add(NullStyleParagraph());
        return doc;
    }

    private const string NullStyleStrictMessage =
        "StrictDiagnostics refused the save: NullTextRunStyle in Paragraph 0, page 1: " + NullStyleMessage;

    // ── The items ────────────────────────────────────────────────────────────

    [Fact]
    public void TwoCodes_inOneDocument_giveExactlyTwoItemsWithExactFields()
    {
        using var doc = new Document();
        doc.Add(NullStyleParagraph());
        doc.Add(new Paragraph("Black star: ★"));

        doc.Save(new MemoryStream());

        Assert.Equal(2, doc.Diagnostics.Count);

        var first = doc.Diagnostics[0];
        Assert.Equal(LayoutDiagnosticCode.NullTextRunStyle, first.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, first.Severity);
        Assert.Null(first.Clause);
        Assert.Equal(LayoutElementKind.Paragraph, first.ElementKind);
        Assert.Equal(0, first.ElementIndex);
        Assert.Equal(1, first.PageNumber);
        Assert.Equal(1, first.Count);
        Assert.Equal(NullStyleMessage, first.Message);
        Assert.Equal("Error NullTextRunStyle Paragraph[0] page 1: " + NullStyleMessage, first.ToString());

        var second = doc.Diagnostics[1];
        Assert.Equal(LayoutDiagnosticCode.UnencodableCharacter, second.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, second.Severity);
        Assert.Null(second.Clause);
        Assert.Equal(LayoutElementKind.Paragraph, second.ElementKind);
        Assert.Equal(1, second.ElementIndex);
        Assert.Equal(1, second.PageNumber);
        Assert.Equal(1, second.Count);
        Assert.Equal("Error UnencodableCharacter Paragraph[1] page 1: " + StarMessage, second.ToString());
    }

    [Fact]
    public void Diagnostics_isEmptyBeforeASave_andForADocumentWithNothingToReport()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("plain text"));
        Assert.Empty(doc.Diagnostics);

        doc.Save(new MemoryStream());

        Assert.Empty(doc.Diagnostics);
    }

    [Fact]
    public void HeadingContinuation_isReportedAsAHeading_notAsTheParagraphItOverflowsInto()
    {
        using var doc = NewDoc();
        var text = string.Join(' ', Enumerable.Repeat("word", 200)) + " ★";
        doc.Add(new Heading(text));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.UnencodableCharacter, item.Code);
        Assert.Equal(LayoutElementKind.Heading, item.ElementKind);
        Assert.Equal(0, item.ElementIndex);
        Assert.Equal(8, item.PageNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HundredThousandOccurrences_areOneItemWithTheirCount(bool withFooter)
    {
        using var doc = new Document();
        if (withFooter) doc.Footer = new RunningBand("page {page} of {pages}");
        doc.Add(new Paragraph(string.Join(' ', Enumerable.Repeat("⚹", 100_000))));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.UnencodableCharacter, item.Code);
        Assert.Equal(100_000, item.Count);
        Assert.Equal(100_000, doc.TextEncodingWarnings.Count);
    }

    private static string DistinctCharacters(int count) =>
        string.Join(' ', Enumerable.Range(0x4E00, count).Select(c => char.ConvertFromUtf32(c)));

    private static DocumentRenderer RenderParagraph(string text)
    {
        var pdf = new PdfDocument();
        var renderer = new DocumentRenderer(pdf);
        renderer.Add(new ParagraphRenderer(new Paragraph(text)));
        renderer.RunLayout();
        return renderer;
    }

    [Fact]
    public void ThousandAndOneDistinctSubjects_storeAThousandAndASentinelCountingTheRest()
    {
        var renderer = RenderParagraph(DistinctCharacters(1001));

        var items = renderer.Diagnostics;
        Assert.Equal(1001, items.Count);
        Assert.All(items.Take(1000), i => Assert.Equal(LayoutDiagnosticCode.UnencodableCharacter, i.Code));
        var sentinel = items[^1];
        Assert.Equal(LayoutDiagnosticCode.DiagnosticsDropped, sentinel.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Warning, sentinel.Severity);
        Assert.Equal(1, sentinel.Count);
        Assert.Equal(1000, renderer.Sink.SeenCount);
    }

    [Fact]
    public void ThousandDistinctSubjects_storeAThousandAndNoSentinel()
    {
        var renderer = RenderParagraph(DistinctCharacters(1000));

        Assert.Equal(1000, renderer.Diagnostics.Count);
        Assert.DoesNotContain(renderer.Diagnostics, i => i.Code == LayoutDiagnosticCode.DiagnosticsDropped);
        Assert.Equal(1000, renderer.Sink.SeenCount);
    }

    [Fact]
    public void ADroppedSubjectThatRepeats_countsEveryDroppedReport()
    {
        // The 1,001st distinct scalar, written five times, after a thousand others.
        var text = DistinctCharacters(1000) + string.Concat(Enumerable.Repeat(" 凨", 5));

        var renderer = RenderParagraph(text);

        var sentinel = renderer.Diagnostics[^1];
        Assert.Equal(LayoutDiagnosticCode.DiagnosticsDropped, sentinel.Code);
        Assert.Equal(5, sentinel.Count);
        Assert.Equal(1000, renderer.Sink.SeenCount);
    }

    [Fact]
    public void ANullStyleParagraphThatSplitsOverTwoPages_isCountedOnce()
    {
        using var doc = NewDoc();
        var body = new TextRun(string.Join(' ', Enumerable.Repeat("word", 120)), TextStyle.Default);
        doc.Add(new Paragraph([new TextRun("   ", null!), body]));
        using var ms = new MemoryStream();

        doc.Save(ms);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullTextRunStyle, item.Code);
        Assert.Equal(1, item.Count);
        var text = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        var pages = System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page(?![s\w])").Count;
        Assert.True(pages >= 2, $"the paragraph must split, got {pages} page(s)");
    }

    [Fact]
    public void ANullStyleParagraph_withAFooter_isStillCountedOnce()
    {
        // A footer makes the document lay every element out twice, once to count pages.
        using var doc = NewDoc();
        doc.Footer = new RunningBand("page {page} of {pages}");
        doc.Add(NullStyleParagraph());
        doc.Add(NullStyleParagraph());

        doc.Save(new MemoryStream());

        Assert.Equal([1, 1], doc.Diagnostics.Select(i => i.Count));
        Assert.Equal([0, 1], doc.Diagnostics.Select(i => i.ElementIndex ?? -1));
    }

    [Fact]
    public void TwoNullStyleRuns_inOneParagraph_giveCountTwo()
    {
        using var doc = NewDoc();
        doc.Add(new Paragraph([new TextRun("", null!), new TextRun("\n", null!), new TextRun("x", TextStyle.Default)]));

        doc.Save(new MemoryStream());

        Assert.Equal(2, Assert.Single(doc.Diagnostics).Count);
    }

    // ── Band truncation ──────────────────────────────────────────────────────

    private static Document CutFooterDoc(bool strict = false)
    {
        var doc = NewDoc();
        doc.StrictDiagnostics = strict;
        var style = new TextStyle { FontSize = 10 };
        doc.Footer = new RunningBand(new string('Æ', 25), style);
        doc.Add(new Paragraph("body", style));
        return doc;
    }

    [Fact]
    public void ACutBand_isReportedAsAnError_andTheOldListKeepsItsEntry()
    {
        using var doc = CutFooterDoc();

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.BandTruncated, item.Code);
        Assert.Equal(LayoutDiagnosticSeverity.Error, item.Severity);
        Assert.Equal(LayoutElementKind.Footer, item.ElementKind);
        Assert.Null(item.ElementIndex);
        Assert.Equal(1, item.PageNumber);
        Assert.Equal(1, item.Count);
        Assert.Equal(
            "Error BandTruncated Footer page 1: The footer band's text was cut to fit the content width: "
            + "20 of 25 characters drawn on the page that lost the most.",
            item.ToString());
        Assert.Equal(
            new BandTruncationWarning(RunningBandKind.Footer, 1, 20, 25),
            Assert.Single(doc.BandTruncations));
    }

    [Fact]
    public void ACutBand_isRefusedInStrictMode_withTheExactMessage()
    {
        using var doc = CutFooterDoc(strict: true);

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Equal(
            "StrictDiagnostics refused the save: BandTruncated in Footer, page 1: The footer band's text "
            + "was cut to fit the content width: 20 of 25 characters drawn on the page that lost the most.",
            ex.Message);
    }

    [Fact]
    public void TextEncodingWarnings_keepOneEntryPerSubstitutedCharacter()
    {
        using var doc = new Document();
        doc.Add(new Paragraph("a ★ b ★ c ☃"));

        doc.Save(new MemoryStream());

        Assert.Equal(
            [new System.Text.Rune('★'), new System.Text.Rune('★'), new System.Text.Rune('☃')],
            doc.TextEncodingWarnings.Select(w => w.Character));
        Assert.Equal(2, doc.Diagnostics.Count);
        Assert.Equal(2, doc.Diagnostics[0].Count);
        Assert.Equal(1, doc.Diagnostics[1].Count);
    }

    // ── Lists ────────────────────────────────────────────────────────────────

    [Fact]
    public void NullsBelowTheSecondLevel_areReportedOnce_withTheirCounts()
    {
        var top = new ListItem("a");
        var child = new ListItem("b");
        child.AddChild((ListItem)null!);
        child.AddChild(new ListItem(null!));
        child.AddChild(new ListItem(null!));
        top.AddChild(child);
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [top]));

        doc.Save(new MemoryStream());

        Assert.Equal(2, doc.Diagnostics.Count);
        var text = doc.Diagnostics[0];
        Assert.Equal(LayoutDiagnosticCode.NullGrandchildText, text.Code);
        Assert.Equal(2, text.Count);
        Assert.Equal(
            "Error NullGrandchildText List[0] page 1: A list item below the second level has a null text and is not drawn.",
            text.ToString());
        var nullChild = doc.Diagnostics[1];
        Assert.Equal(LayoutDiagnosticCode.NullGrandchild, nullChild.Code);
        Assert.Equal(1, nullChild.Count);
        Assert.Equal(
            "Error NullGrandchild List[0] page 1: A list item below the first level was given a null child, which is not drawn.",
            nullChild.ToString());
    }

    [Fact]
    public void ASecondLevelHolderSharedByTwoParents_countsOnce()
    {
        var shared = new ListItem("shared");
        shared.AddChild((ListItem)null!);
        var first = new ListItem("first");
        first.AddChild(shared);
        var second = new ListItem("second");
        second.AddChild(shared);
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [first, second]));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullGrandchild, item.Code);
        Assert.Equal(1, item.Count);
    }

    [Fact]
    public void ASecondLevelHolderThatIsItsOwnChild_countsOnce()
    {
        var holder = new ListItem("holder");
        holder.AddChild((ListItem)null!);
        holder.AddChild(holder);
        var top = new ListItem("top");
        top.AddChild(holder);
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [top]));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullGrandchild, item.Code);
        Assert.Equal(1, item.Count);
    }

    [Fact]
    public void AListThatSplitsOverPages_reportsItsGrandchildNullsOnce()
    {
        var items = Enumerable.Range(0, 60).Select(i => new ListItem("item " + i)).ToList();
        var child = new ListItem("child");
        child.AddChild(new ListItem(null!));
        items[0].AddChild(child);
        using var doc = NewDoc();
        doc.Add(new ListElement(ListStyle.OrderedDecimal, items));
        using var ms = new MemoryStream();

        doc.Save(ms);

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullGrandchildText, item.Code);
        Assert.Equal(1, item.Count);
        var text = System.Text.Encoding.Latin1.GetString(ms.ToArray());
        Assert.True(System.Text.RegularExpressions.Regex.Matches(text, @"/Type\s*/Page(?![s\w])").Count >= 2);
    }

    [Fact]
    public void AnItemThatIsItsOwnChild_savesAndTerminates()
    {
        var a = new ListItem("a");
        a.AddChild(a);
        var b = new ListItem("b");
        b.AddChild(b);
        b.AddChild(new ListItem(null!));
        var top = new ListItem("top");
        top.AddChild(b);
        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [a, top]));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullGrandchildText, item.Code);
        Assert.Equal(1, item.Count);
    }

    [Fact]
    public void AGraphWithTwoToTheFortyPaths_savesAndCountsEachDistinctItemOnce()
    {
        var levels = Enumerable.Range(0, 41).Select(i => new ListItem(i == 40 ? null! : "level " + i)).ToArray();
        for (var i = 0; i < 40; i++)
        {
            levels[i].AddChild(levels[i + 1]);
            levels[i].AddChild(levels[i + 1]);
        }

        using var doc = new Document();
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [levels[0]]));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullGrandchildText, item.Code);
        Assert.Equal(1, item.Count);
    }

    // ── Library-written text is not the caller's ─────────────────────────────

    private static ListElement NestedBulletList(string childText)
    {
        var top = new ListItem("top");
        top.AddChild(childText);
        return new ListElement(ListStyle.Unordered, [top]);
    }

    [Fact]
    public void ANestedBulletMarkerInAStandard14Font_isNotReported_andDoesNotTripStrict()
    {
        using var doc = new Document { StrictDiagnostics = true };
        doc.Add(NestedBulletList("child"));

        doc.Save(new MemoryStream());

        Assert.Empty(doc.Diagnostics);
        // The older list still records the substitution, as before.
        Assert.Equal(new System.Text.Rune(0x25E6), Assert.Single(doc.TextEncodingWarnings).Character);
    }

    [Fact]
    public void CallerTextInTheSameList_isStillReported()
    {
        using var doc = new Document();
        doc.Add(NestedBulletList("child ★"));

        doc.Save(new MemoryStream());

        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.UnencodableCharacter, item.Code);
        Assert.Equal(StarMessage, item.Message);
        Assert.Equal(1, item.Count);
        Assert.Equal(2, doc.TextEncodingWarnings.Count);
    }

    // ── Where each kind of item is attributed ────────────────────────────────

    private sealed class TextWritingRenderer(string text) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));

        public void Draw(DrawContext ctx)
        {
            ctx.Canvas.BeginText();
            ctx.Canvas.SetFont(ctx.GetFont(VellumPdf.Fonts.Standard14.Helvetica), 10);
            ctx.Canvas.ShowText(text);
            ctx.Canvas.EndText();
        }
    }

    [Fact]
    public void TableCustomAndHeaderItems_carryTheirOwnKindAndIndex()
    {
        // Image, line separator and pie chart write no text through the canvas's ShowText, so none
        // of them can raise a report today and none is covered here.
        using var doc = NewDoc();
        doc.Header = new RunningBand("★");
        var table = new Elements.Table.TableElement();
        table.AddRow().AddCell("☃");
        doc.Add(table);
        doc.Add(new TextWritingRenderer("☀"));

        doc.Save(new MemoryStream());

        Assert.Equal(
            [
                (LayoutElementKind.Table, (int?)0),
                (LayoutElementKind.Custom, 1),
                (LayoutElementKind.Header, null),
            ],
            doc.Diagnostics.Select(i => (i.ElementKind, i.ElementIndex)));
    }

    // ── Default bytes ────────────────────────────────────────────────────────

    /// <summary>
    /// The channel changes no byte of a saved document. This one holds an input for each code this
    /// release reports, with the switch off, and its inflated bytes are pinned with the timestamp and
    /// id.
    /// </summary>
    [Fact]
    public void ADocumentWithReportableInputs_savesTheBytesItDidBeforeTheChannel()
    {
        using var doc = NewDoc();
        var style = new TextStyle { FontSize = 10 };
        doc.Footer = new RunningBand(new string('Æ', 25), style);
        doc.Add(NullStyleParagraph());
        doc.Add(new Paragraph("Black star: ⚹ and a bullet ★", style));
        var top = new ListItem("top");
        var child = new ListItem("child");
        child.AddChild(new ListItem(null!));
        top.AddChild(child);
        doc.Add(new ListElement(ListStyle.OrderedDecimal, [top]));
        var pdf = (PdfDocument)typeof(Document)
            .GetField("_pdf", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(doc)!;
        pdf.Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        pdf.DocumentId = new byte[16];
        var ms = new MemoryStream();

        doc.Save(ms);

        var inflated = PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()).Replace("\r\n", "\n");
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.Latin1.GetBytes(inflated)));
        Assert.Equal("0B20297CEF64A9029E09C716DBDC4B61FA4F3B42C1FA7724DE44080D0B6F6DD2", hash);
    }

    // ── Strict mode ──────────────────────────────────────────────────────────

    [Fact]
    public void Strict_save_stream_throwsTheExactMessageAndWritesNothing()
    {
        using var doc = NullStyleDoc(strict: true);
        var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(ms));

        Assert.Equal(NullStyleStrictMessage, ex.Message);
        Assert.Equal(0, ms.Length);
        var item = Assert.Single(doc.Diagnostics);
        Assert.Equal(LayoutDiagnosticCode.NullTextRunStyle, item.Code);
        var again = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Contains("cannot be saved again", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Strict_saveAsync_stream_throwsTheExactMessageAndWritesNothing()
    {
        using var doc = NullStyleDoc(strict: true);
        var ms = new MemoryStream();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => doc.SaveAsync(ms, TestContext.Current.CancellationToken));

        Assert.Equal(NullStyleStrictMessage, ex.Message);
        Assert.Equal(0, ms.Length);
    }

    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void Strict_savePath_leavesAnExistingFileUntouched_andCreatesNothingAtANewPath()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var existing = Path.Combine(dir, "existing.pdf");
            File.WriteAllBytes(existing, [1, 2, 3, 4, 5]);
            var before = Sha(existing);
            using (var doc = NullStyleDoc(strict: true))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(existing));
                Assert.Equal(NullStyleStrictMessage, ex.Message);
            }

            Assert.Equal(before, Sha(existing));

            var fresh = Path.Combine(dir, "fresh.pdf");
            using (var doc = NullStyleDoc(strict: true))
                Assert.Throws<InvalidOperationException>(() => doc.Save(fresh));
            Assert.False(File.Exists(fresh));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Strict_saveAsyncPath_leavesAnExistingFileUntouched_andCreatesNothingAtANewPath()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            var existing = Path.Combine(dir, "existing.pdf");
            File.WriteAllBytes(existing, [9, 8, 7]);
            var before = Sha(existing);
            using (var doc = NullStyleDoc(strict: true))
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                    () => doc.SaveAsync(existing, TestContext.Current.CancellationToken));
                Assert.Equal(NullStyleStrictMessage, ex.Message);
            }

            Assert.Equal(before, Sha(existing));

            var fresh = Path.Combine(dir, "fresh.pdf");
            using (var doc = NullStyleDoc(strict: true))
                await Assert.ThrowsAsync<InvalidOperationException>(
                    () => doc.SaveAsync(fresh, TestContext.Current.CancellationToken));
            Assert.False(File.Exists(fresh));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Strict_signing_throwsTheExactMessageAndWritesNothing()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=VellumPdf Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var doc = NullStyleDoc(strict: true);
        var ms = new MemoryStream();

        var ex = Assert.Throws<InvalidOperationException>(
            () => doc.Sign(ms, new PdfSignatureSettings { Certificate = cert, SignerName = "Tester" }));

        Assert.Equal(NullStyleStrictMessage, ex.Message);
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public void Strict_messageCountsTheFurtherErrorItems()
    {
        using var doc = NewDoc();
        doc.StrictDiagnostics = true;
        doc.Add(NullStyleParagraph());
        doc.Add(new Paragraph("★"));
        doc.Add(new Paragraph("☃"));

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Equal(NullStyleStrictMessage + " 2 further error item(s).", ex.Message);
    }

    [Fact]
    public void Strict_aCleanDocument_savesTheSameBytesAsWithoutTheSwitch()
    {
        static byte[] Bytes(bool strict)
        {
            using var doc = new Document { StrictDiagnostics = strict };
            doc.Add(new Paragraph("Clean text"));
            doc.Add(new ListElement(ListStyle.OrderedDecimal, [new ListItem("one"), new ListItem("two")]));
            var pdf = (PdfDocument)typeof(Document)
                .GetField("_pdf", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(doc)!;
            pdf.Timestamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
            pdf.DocumentId = new byte[16];
            var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        Assert.Equal(Bytes(false), Bytes(true));
    }

    /// <summary>Reports through the context the document passes, as a later lane's renderer will.</summary>
    private sealed class ReportingRenderer(Action<LayoutDiagnosticSink> report) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));

        public void Draw(DrawContext ctx) => report(ctx.Diagnostics!);
    }

    [Fact]
    public void Strict_aWarningAloneNeverRefuses()
    {
        using var doc = NewDoc();
        doc.StrictDiagnostics = true;
        doc.Add(new ReportingRenderer(sink =>
        {
            for (var i = 0; i < 5; i++) sink.Report(LayoutDiagnosticCode.DiagnosticsDropped, i, "w");
        }));

        doc.Save(new MemoryStream());

        Assert.Equal(5, doc.Diagnostics.Count);
        Assert.All(doc.Diagnostics, i => Assert.Equal(LayoutDiagnosticSeverity.Warning, i.Severity));
    }

    [Fact]
    public void Strict_whenEveryDroppedReportWasAnError_saysSoWithTheirCount()
    {
        using var doc = NewDoc();
        doc.StrictDiagnostics = true;
        doc.Add(new ReportingRenderer(sink =>
        {
            // A thousand warnings fill the channel; the error that follows is dropped, not stored.
            for (var i = 0; i < 1001; i++) sink.Report(LayoutDiagnosticCode.DiagnosticsDropped, i, "w");
            sink.Report(LayoutDiagnosticCode.NullGrandchild, 0, "e", count: 3);
        }));

        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Equal(
            "StrictDiagnostics refused the save: 3 error report(s) were dropped past the channel's "
            + "limit of 1000 items.",
            ex.Message);
        Assert.Equal(4, doc.Diagnostics[^1].Count);
    }

    // ── Strict does not reorder an existing refusal ──────────────────────────

    private sealed class NanDrawRenderer : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));

        public void Draw(DrawContext ctx) => ctx.Canvas.MoveTo(double.NaN, 0);
    }

    private static Exception Capture(Func<Document> build)
    {
        using var doc = build();
        return Assert.ThrowsAny<Exception>(() => doc.Save(new MemoryStream()));
    }

    private static void AssertSameRefusal(Func<bool, Document> build)
    {
        var off = Capture(() => build(false));
        var on = Capture(() => build(true));

        Assert.IsType(off.GetType(), on);
        Assert.Equal(off.Message, on.Message);
    }

    [Fact]
    public void Strict_withALayoutRefusalToo_throwsTheSameExceptionAsWithoutTheSwitch()
    {
        AssertSameRefusal(strict =>
        {
            var doc = NullStyleDoc(strict);
            doc.Add(new Paragraph("x", new TextStyle { FontSize = double.NaN }));
            return doc;
        });
    }

    [Fact]
    public void Strict_withANonFiniteValueTheCanvasRefuses_throwsTheCanvasException()
    {
        AssertSameRefusal(strict =>
        {
            var doc = NullStyleDoc(strict);
            doc.Add(new NanDrawRenderer());
            return doc;
        });

        using var probe = NullStyleDoc(strict: true);
        probe.Add(new NanDrawRenderer());
        var ex = Assert.Throws<ArgumentException>(() => probe.Save(new MemoryStream()));
        Assert.StartsWith("PDF does not support NaN or Infinity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_withANullDestination_reportsThePreconditionFirst()
    {
        using var doc = NullStyleDoc(strict: true);
        using var lenient = NullStyleDoc(strict: false);
        var withoutSwitch = Assert.Throws<ArgumentNullException>(() => lenient.Save((Stream)null!));

        var withSwitch = Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));

        Assert.Equal(withoutSwitch.Message, withSwitch.Message);

        // The precondition left the document usable, so the strict refusal still comes later.
        var ex = Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));
        Assert.Equal(NullStyleStrictMessage, ex.Message);
    }

    [Fact]
    public void AFailedStrictSave_doesNotLeakItsItemsIntoAnotherDocument()
    {
        using var refused = NullStyleDoc(strict: true);
        using var clean = new Document();
        clean.Add(new Paragraph("clean"));

        Assert.Throws<InvalidOperationException>(() => refused.Save(new MemoryStream()));
        clean.Save(new MemoryStream());

        Assert.Equal(LayoutDiagnosticCode.NullTextRunStyle, Assert.Single(refused.Diagnostics).Code);
        Assert.Empty(clean.Diagnostics);
    }

    [Fact]
    public void AFailedSave_thatIsNotAStrictRefusal_leavesDiagnosticsAlone()
    {
        using var doc = NewDoc();
        doc.Add(NullStyleParagraph());
        doc.Add(new Paragraph("x", new TextStyle { FontSize = double.NaN }));

        Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        // The null-style run was found before the layout threw, and none of it is kept.
        Assert.Empty(doc.Diagnostics);
    }

    // ── The walk below the second level is linear ────────────────────────────

    /// <summary>A chain of <paramref name="length"/> items, each the only child of the one before.</summary>
    private static ListItem Chain(int length)
    {
        var items = Enumerable.Range(0, length).Select(i => new ListItem("link " + i)).ToArray();
        for (var i = 0; i + 1 < length; i++) items[i].AddChild(items[i + 1]);
        return items[0];
    }

    private static DocumentRenderer LayOutLists(IEnumerable<ListElement> lists)
    {
        var renderer = new DocumentRenderer(new PdfDocument());
        foreach (var list in lists) renderer.Add(new ListRenderer(list));
        renderer.RunLayout();
        return renderer;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void OneListAddedManyTimes_walksItsChainOnce(int times)
    {
        const int length = 1000;
        var top = new ListItem("top");
        top.AddChild(Chain(length));
        var list = new ListElement(ListStyle.OrderedDecimal, [top]);

        var renderer = LayOutLists(Enumerable.Repeat(list, times));

        // The distinct items, not the paths and not the lists: the same count for 1 and for 20.
        Assert.Equal(length, renderer.ListWalk.NodesVisited);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void ManyListsSharingAChain_walkItOnce_andEachReportsItsOwnCount(int lists)
    {
        const int length = 1000;
        var tail = new ListItem(null!);
        var head = Chain(length);
        var last = head;
        while (last.Children is { Count: > 0 } kids) last = kids[0];
        last.AddChild(tail);
        using var doc = NewDoc();
        for (var i = 0; i < lists; i++)
        {
            var top = new ListItem("top " + i);
            top.AddChild(head);
            doc.Add(new ListElement(ListStyle.OrderedDecimal, [top]));
        }

        doc.Save(new MemoryStream());

        Assert.Equal(lists, doc.Diagnostics.Count);
        Assert.All(doc.Diagnostics, i =>
        {
            Assert.Equal(LayoutDiagnosticCode.NullGrandchildText, i.Code);
            Assert.Equal(1, i.Count);
        });
        var renderer = LayOutLists(Enumerable.Range(0, lists).Select(i =>
        {
            var top = new ListItem("top " + i);
            top.AddChild(head);
            return new ListElement(ListStyle.OrderedDecimal, [top]);
        }));
        Assert.Equal(length + 1, renderer.ListWalk.NodesVisited);
    }

    // ── What Diagnostics hands out, and cancellation ─────────────────────────

    [Fact]
    public void Diagnostics_isAReadOnlyView()
    {
        using var doc = NullStyleDoc();
        doc.Save(new MemoryStream());

        var view = doc.Diagnostics;

        Assert.False(view is List<LayoutDiagnostic>);
        var list = Assert.IsAssignableFrom<IList<LayoutDiagnostic>>(view);
        Assert.True(list.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => list.Clear());
        Assert.Single(doc.Diagnostics);
    }

    private sealed class CancellingStarRenderer(CancellationTokenSource source) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(10));

        public void Draw(DrawContext ctx)
        {
            source.Cancel();
            ctx.Canvas.BeginText();
            ctx.Canvas.SetFont(ctx.GetFont(VellumPdf.Fonts.Standard14.Helvetica), 10);
            ctx.Canvas.ShowText("\u2605");
            ctx.Canvas.EndText();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ATokenCancelledWhileAnElementIsDrawn_givesTheStrictRefusalOnlyInStrictMode(bool strict)
    {
        using var source = new CancellationTokenSource();
        using var doc = NewDoc();
        doc.StrictDiagnostics = strict;
        doc.Add(new CancellingStarRenderer(source));

        if (strict)
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => doc.SaveAsync(new MemoryStream(), source.Token));
            Assert.StartsWith("StrictDiagnostics refused the save: UnencodableCharacter in Custom 0", ex.Message, StringComparison.Ordinal);
        }
        else
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => doc.SaveAsync(new MemoryStream(), source.Token));
        }
    }

    // ── Reporting boundaries ─────────────────────────────────────────────────

    [Fact]
    public void ADrawContextTheCallerBuilds_reportsNothingAndDoesNotThrow()
    {
        var pdf = new PdfDocument();
        var page = pdf.AddPage(SmallPage);
        var box = new LayoutBox(0, 0, 300, 200);
        var renderer = new ParagraphRenderer(NullStyleParagraph());
        renderer.Layout(new LayoutContext(box));
        var ctx = new DrawContext(new PdfCanvas(page), box, new RendererContext(page, pdf), pdf, page);

        renderer.Draw(ctx);

        Assert.Null(ctx.Diagnostics);
    }

    [Fact]
    public void EveryCodeIsNamedInTheDiagnosticsDocumentation()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "VellumPdf.Layout", "Document.cs"));
        var start = source.IndexOf("private IReadOnlyList<LayoutDiagnostic> _diagnostics", StringComparison.Ordinal);
        var end = source.IndexOf("public IReadOnlyList<LayoutDiagnostic> Diagnostics", StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        var doc = source[start..end];

        foreach (var name in Enum.GetNames<LayoutDiagnosticCode>())
            Assert.Contains($"LayoutDiagnosticCode.{name}", doc, StringComparison.Ordinal);
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "VellumPdf.slnx"))) return dir.FullName;
        }

        throw new InvalidOperationException("Could not locate VellumPdf.slnx.");
    }
}
