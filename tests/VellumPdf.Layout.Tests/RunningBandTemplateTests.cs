// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Rendering;
using VellumPdf.TestSupport;
using static VellumPdf.Layout.Tests.PlacementLoopSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// #518: a running band's template is scanned once and resolved as the fitting walk asks for it.
/// The reference for every resolver test is the public <see cref="RunningBand.Resolve"/>, which
/// still replaces <c>{page}</c> and then <c>{pages}</c> over the whole template.
/// </summary>
public sealed class RunningBandTemplateTests
{
    // Pieces a template is drawn from: both tokens, their near misses, the characters around
    // them, a surrogate pair, a lone surrogate of each kind, and plain text.
    private static readonly string[] Pieces =
    [
        "{page}", "{pages}", "{page", "page}", "{pages", "{", "}", "{{page}}", "{pages}}", "{{pages}",
        "{Page}", "{PAGES}", "{ page }", "{page }", "{pag}", "{pagess}", "{page}s", "s}",
        "a", "bc", " ", "  ", "xyz", "\U0001F600", "\uD800", "\uDC00", "0", "12", "-", "p", "e",
    ];

    private static string RandomTemplate(Random random)
    {
        var sb = new StringBuilder();
        var count = random.Next(0, 14);
        for (var i = 0; i < count; i++)
            sb.Append(Pieces[random.Next(Pieces.Length)]);
        return sb.ToString();
    }

    private static readonly int[] Numbers = [0, 1, 2, 9, 10, 11, 99, 100, 101, 999, 1000, 54321, 1000000, int.MaxValue, -1, -10, int.MinValue];

    private static BandText Resolve(string template, int page, int total)
    {
        var text = new BandText(BandTemplate.Compile(template));
        text.Reset(page, total);
        return text;
    }

    [Fact]
    public void Resolver_agreesWithResolve_forTheWholeText_theLength_andEveryPrefix()
    {
        var random = new Random(518);
        for (var iteration = 0; iteration < 6000; iteration++)
        {
            var template = RandomTemplate(random);
            var page = Numbers[random.Next(Numbers.Length)];
            var total = Numbers[random.Next(Numbers.Length)];
            var expected = new RunningBand(template).Resolve(page, total);

            var text = Resolve(template, page, total);
            var context = $"template {template.Replace("\uD800", "<hi>").Replace("\uDC00", "<lo>")} page {page} total {total}";
            Assert.True(expected.Length == text.Length, $"length {text.Length} vs {expected.Length}: {context}");

            // Characters read one at a time, in order, as the fitting walk reads them.
            for (var i = 0; i < expected.Length; i++)
                Assert.True(expected[i] == text[i], $"index {i}: {context}");

            // A prefix of any length on a fresh resolution, and a slice from the middle.
            var cut = random.Next(0, expected.Length + 1);
            Assert.Equal(expected[..cut], Resolve(template, page, total).Prefix(cut));
            var start = random.Next(0, expected.Length + 1);
            var length = random.Next(0, expected.Length - start + 1);
            Assert.Equal(expected.Substring(start, length), Resolve(template, page, total).Slice(start, length));
        }
    }

    [Fact]
    public void Resolver_isReusedAcrossPages_andAReadAfterResetStartsAgain()
    {
        var band = new RunningBand("p{page}/{pages}.{page}");
        var text = new BandText(band.Compiled);
        foreach (var (page, total) in new[] { (1, 3), (10, 12), (100, 120), (2, 3), (1000000, 1000001) })
        {
            text.Reset(page, total);
            // Read half, then reset: the next page must not see the previous page's cursor.
            _ = text[text.Length / 2];
            text.Reset(page, total);
            Assert.Equal(band.Resolve(page, total), text.Prefix(text.Length));
        }
    }

    [Fact]
    public void Resolver_producesOnlyWhatWasRead()
    {
        // A template of 100,000 characters that begins with both tokens: reading the first ten
        // characters of "7/12 xxxxx..." produces ten, not 100,000.
        var text = Resolve("{page}/{pages} " + new string('x', 100_000), 7, 12);
        Assert.Equal(100_005, text.Length);

        Assert.Equal('7', text[0]);
        Assert.Equal(1, text.Produced);
        Assert.Equal("7/12 xxxxx", text.Prefix(10));
        Assert.Equal(10, text.Produced);
        Assert.Equal('x', text[11]);
        Assert.Equal(12, text.Produced);

        // Reading ahead of what was produced produces up to the end of the read, never beyond it.
        Assert.Equal("xx", text.Slice(20, 2));
        Assert.Equal(22, text.Produced);
    }

    [Fact]
    public void Length_isTheLiteralLength_plusEachTokenCountTimesItsDigits()
    {
        // Three {page} and one {pages}, on a page whose number has fewer digits than the total:
        // 3 * 1 + 3 + the five literal characters.
        var text = Resolve("a{page}b{page}{pages}c{page}d", 7, 105);
        Assert.Equal("a7b7105c7d", new RunningBand("a{page}b{page}{pages}c{page}d").Resolve(7, 105));
        Assert.Equal(10, text.Length);

        // The same template on a page with more digits than the total.
        text.Reset(105, 7);
        Assert.Equal("a105b1057c105d".Length, text.Length);
    }

    [Fact]
    public void NullTemplate_isRefusedWithTheSameExceptionAsResolve()
    {
        var band = new RunningBand(null!);
        var viaResolve = Assert.Throws<ArgumentNullException>(() => band.Resolve(1, 1));
        var viaCompiled = Assert.Throws<ArgumentNullException>(() => band.Compiled);
        Assert.Equal("template", viaResolve.ParamName);
        Assert.Equal(viaResolve.ParamName, viaCompiled.ParamName);
        Assert.Equal(viaResolve.Message, viaCompiled.Message);
    }

    [Fact]
    public void NullTemplate_stillThrowsFromTheSave_naming_template()
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 300), Margins = new EdgeInsets(20) };
        doc.Footer = new RunningBand(null!);
        doc.Add(new Paragraph("body", new TextStyle { Font = Standard14.Helvetica, FontSize = 10 }));
        var ex = Assert.Throws<ArgumentNullException>(() => doc.Save(new MemoryStream()));
        Assert.Equal("template", ex.ParamName);
    }

    // ── The cost per page ────────────────────────────────────────────────────

    private static readonly TextStyle Ten = new() { Font = Standard14.Helvetica, FontSize = 10 };

    private static Paragraph LongBody(int words) =>
        new(string.Join(' ', Enumerable.Repeat("lorem", words)), Ten);

    private static long SaveAllocation(string template, int words)
    {
        using var doc = new Document { PageSize = new PdfRectangle(0, 0, 200, 300), Margins = new EdgeInsets(20) };
        doc.Footer = new RunningBand(template, Ten);
        doc.Add(LongBody(words));
        using var ms = new MemoryStream();
        var before = GC.GetAllocatedBytesForCurrentThread();
        doc.Save(ms);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // The issue's shape: a document deep enough that page count multiplies the per-page cost.
    // Measured with the template resolved in full on every page, the 100,000-character template
    // allocated 276,551,992 bytes against 37,413,456 for the 200-character one on 600 pages, a
    // ratio of 7.39. Allocation on the calling thread is deterministic where a clock is not.
    [Fact]
    public void LongTemplate_costsAboutWhatAShortOneDoes_onADeepDocument()
    {
        const int Words = 60_000;
        var shortTemplate = "{page}/{pages} " + new string('x', 200);
        var longTemplate = "{page}/{pages} " + new string('x', 100_000);

        // Warm up so JIT and static tables are not charged to the first measurement.
        SaveAllocation(shortTemplate, 2000);
        SaveAllocation(longTemplate, 2000);

        var small = SaveAllocation(shortTemplate, Words);
        var large = SaveAllocation(longTemplate, Words);

        Assert.True((double)large / small <= 1.05, $"ratio {(double)large / small}: {large} against {small}");
        Assert.True(large - small < 1_000_000, $"{large - small} more bytes for 99,800 more template characters");
    }

    private static (long Produced, int Pages) ProducedOverADocument(RunningBand? header, RunningBand? footer)
    {
        var renderer = new DocumentRenderer(NewPdf(), new PdfRectangle(0, 0, 200, 300), new EdgeInsets(20))
        {
            Header = header,
            Footer = footer,
        };
        renderer.Add(new ParagraphRenderer(LongBody(4000)));
        var pages = PageCount(Render(renderer));
        return (renderer.BandCharactersProduced, pages);
    }

    [Fact]
    public void Band_producesTheFittedPrefixAndOneCharacterOfLookAhead_onEveryPage()
    {
        // Helvetica 'x' is 500 thousandths, so 5.0 pt at 10 pt: exactly 32 fit in the 160 pt
        // content width and the 33rd is the one that breaks the walk. 100,000 characters follow.
        var template = new string('x', 100_000);
        var (produced, pages) = ProducedOverADocument(null, new RunningBand(template, Ten));

        Assert.True(pages > 30, $"{pages} pages");
        Assert.Equal(33L * pages, produced);

        var (both, bothPages) = ProducedOverADocument(new RunningBand(template, Ten), new RunningBand(template, Ten));
        Assert.Equal(2L * 33 * bothPages, both);
    }

    // The cost is what fits, not what is drawn: the walk reads one character past the cut, and
    // the word-boundary cut then drops back to the last space. On this 40-page document page 1
    // draws "1/40", 4 characters, and produces 33.
    [Fact]
    public void Band_producesWhatFits_notWhatIsDrawn_whenTheCutFallsBackToAWordBoundary()
    {
        var template = "{page}/{pages} " + new string('x', 200);
        var renderer = new DocumentRenderer(
            NewPdf(), new PdfRectangle(0, 0, 200, 300), new EdgeInsets(20))
        {
            Footer = new RunningBand(template, Ten),
        };
        renderer.Add(new ParagraphRenderer(LongBody(4000)));
        var bytes = Render(renderer);
        var pages = PageCount(bytes);

        Assert.Equal(40, pages);
        Assert.Equal(33L * pages, renderer.BandCharactersProduced);

        var report = Assert.Single(renderer.BandTruncations);
        Assert.Equal(1, report.PageNumber);
        Assert.Equal(4, report.DrawnCharacters);
    }

    // A high surrogate at the cut costs two characters of look-ahead, because the walk reads the
    // next unit to decide whether the pair is whole. Thirty-one 'x' take 155 pt of the 160 pt
    // content width and the pair does not fit in the 5 pt left, which the count confirms: 31 that
    // fit plus the pair is 33, where one character of look-ahead would give 32.
    [Fact]
    public void Band_atASurrogatePairAtTheCut_producesTwoCharactersOfLookAhead()
    {
        var template = new string('x', 31) + "😀" + new string('x', 1000);
        var (produced, pages) = ProducedOverADocument(null, new RunningBand(template, Ten));

        Assert.True(pages > 30, $"{pages} pages");
        Assert.Equal(33L * pages, produced);
    }

    // Compiled is built once and kept, so every page of a save and every save of the band share
    // the one scan.
    [Fact]
    public void Compiled_isBuiltOnce_andKept()
    {
        var band = new RunningBand("{page}/{pages} footer", Ten);
        Assert.Same(band.Compiled, band.Compiled);
    }

    // The public Resolve scans a template with its own pass per token, apart from the save's table.
    // A token added to BandTokens and BandText.Reset but not to Resolve shows here as a token left
    // in the text, and a token that is a prefix of another shows as a differing scan.
    [Fact]
    public void Resolve_substitutesEveryEntryOfBandTokens_asTheSaveDoes()
    {
        var entries = BandTokens.Entries.ToArray();
        Assert.NotEmpty(entries);

        foreach (var (text, _) in entries)
        {
            foreach (var (other, _) in entries)
            {
                if (!ReferenceEquals(text, other))
                    Assert.False(other.StartsWith(text, StringComparison.Ordinal), $"{text} is a prefix of {other}");
            }
        }

        var template = "<" + string.Concat(entries.Select(e => e.Text + "-")) + ">";
        var viaSave = Resolve(template, 7, 4321);
        var viaPublic = new RunningBand(template, Ten).Resolve(7, 4321);

        Assert.Equal(viaSave.Prefix(viaSave.Length), viaPublic);
        foreach (var (text, _) in entries)
            Assert.DoesNotContain(text, viaPublic, StringComparison.Ordinal);
    }

    // Header and footer each keep their own BandText for the whole save. A footer that reused the
    // header's cache would allocate two BandText per page, about 313 KB over 632 pages on a 38 MB
    // save in a reviewer's measurement, which is too thin a margin to bound across runtimes. The
    // fields are checked instead: after a save each holds the text for its own band, never the
    // other's. With the footer passed `ref _headerText`, both asserts on null fail.
    [Fact]
    public void HeaderAndFooter_keepTheirOwnBandText_acrossTheSave()
    {
        var header = new RunningBand("Head {page}", Ten);
        var footer = new RunningBand("Foot", Ten);
        var renderer = new DocumentRenderer(NewPdf(), new PdfRectangle(0, 0, 200, 300), new EdgeInsets(20))
        {
            Header = header,
            Footer = footer,
        };
        renderer.Add(new ParagraphRenderer(LongBody(4000)));
        Assert.True(PageCount(Render(renderer)) > 2);

        const System.Reflection.BindingFlags Flags =
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var headerText = (BandText?)typeof(DocumentRenderer).GetField("_headerText", Flags)!.GetValue(renderer);
        var footerText = (BandText?)typeof(DocumentRenderer).GetField("_footerText", Flags)!.GetValue(renderer);

        Assert.NotNull(headerText);
        Assert.NotNull(footerText);
        Assert.NotSame(headerText, footerText);
        Assert.Same(header.Compiled, headerText.Template);
        Assert.Same(footer.Compiled, footerText.Template);
    }

    // Token-dense: 16,666 tokens of 6 characters. The scan is built once per band, which costs the
    // long template 1.2 MB more than the short one on net11.0; with the cache removed it was 886 MB
    // more. The bound sits between the two. Allocation on the calling thread is deterministic where
    // a clock is not.
    [Fact]
    public void LongTokenDenseTemplate_costsAboutWhatAShortOneDoes_onADeepDocument()
    {
        const int Words = 60_000;
        var shortTemplate = string.Concat(Enumerable.Repeat("{page}", 33));
        var longTemplate = string.Concat(Enumerable.Repeat("{page}", 16_666));

        SaveAllocation(shortTemplate, 2000);
        SaveAllocation(longTemplate, 2000);

        var small = SaveAllocation(shortTemplate, Words);
        var large = SaveAllocation(longTemplate, Words);

        Assert.True(
            large - small < 10_000_000,
            $"{large - small} more bytes for 16,633 more tokens ({large} against {small})");
    }

    [Fact]
    public void Band_withTokens_producesAFewDozenCharactersPerPage_notTheTemplate()
    {
        var template = "{page}/{pages} " + new string('x', 100_000);
        var (produced, pages) = ProducedOverADocument(new RunningBand(template, Ten), null);

        // No character is narrower than 2.78 pt here, so at most 57 fit in 160 pt, plus one.
        Assert.True(produced <= 58L * pages, $"{produced} over {pages} pages");
        Assert.True(produced >= 20L * pages, $"{produced} over {pages} pages");
    }

    // A renderer's Draw can replace the band between two pages, because Header is settable. The
    // cached resolution must follow the band it was built for, not the first one drawn.
    private sealed class SwapHeader(DocumentRenderer owner, RunningBand replacement) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => LayoutResult.Full(context.Area.WithHeight(0));

        public void Draw(DrawContext context) => owner.Header = replacement;
    }

    [Fact]
    public void ReplacingTheHeaderBetweenPages_drawsTheNewTemplate()
    {
        var renderer = new DocumentRenderer(NewPdf(), new PdfRectangle(0, 0, 200, 300), new EdgeInsets(20))
        {
            Header = new RunningBand("A {page}/{pages}", Ten),
        };
        renderer.Add(new ParagraphRenderer(new Paragraph("one", Ten)));
        renderer.Add(new PageBreakRenderer(new PageBreak()));
        renderer.Add(new SwapHeader(renderer, new RunningBand("B {page}/{pages}", Ten)));
        renderer.Add(new ParagraphRenderer(new Paragraph("two", Ten)));

        var stream = PdfTestUtil.DecompressAllFlatStreams(Render(renderer));
        Assert.Contains("(A 1/2) Tj", stream);
        Assert.Contains("(B 2/2) Tj", stream);
    }
}
