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
        Assert.True(item.PageNumber > 1, $"the character sits on a later page, got {item.PageNumber}");
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

    [Fact]
    public void Strict_aWarningAloneNeverRefuses()
    {
        // No warning-severity code is reported by a save yet, so the severity table is pinned
        // directly: the sentinel is the one warning, and it must not be an error.
        Assert.Equal(
            LayoutDiagnosticSeverity.Warning,
            LayoutDiagnostic.SeverityOf(LayoutDiagnosticCode.DiagnosticsDropped));
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

        Assert.Throws<ArgumentNullException>(() => doc.Save((Stream)null!));

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
        doc.Add(new Paragraph("x", new TextStyle { FontSize = double.NaN }));

        Assert.Throws<InvalidOperationException>(() => doc.Save(new MemoryStream()));

        Assert.Empty(doc.Diagnostics);
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
