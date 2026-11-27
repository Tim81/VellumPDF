// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Net;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VellumPdf.Samples.Manual;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// The sample manual under <c>samples/VellumPdf.Samples.Manual</c>: it saves, it conforms to
/// PDF/A-2u,
/// qpdf finds nothing wrong with it, its named destinations and bookmarks resolve, every internal
/// link lands on the heading it names and every printed page number is the label of that
/// heading's page, its page labels are the ones built, the Dutch passage and the colophon read
/// back, the same inputs give the same bytes, and the sample reaches VellumPdf only through the
/// Layout API.
/// </summary>
/// <remarks>
/// These tests live in the Layout test project rather than a project of their own because the
/// oracle helpers, the target frameworks and the CI coverage bookkeeping are already here. The
/// sample assembly is left out of coverage by <c>coverlet.testconfig.json</c>, so it does not
/// count as a shipping package.
/// </remarks>
public sealed class SampleManualTests : IClassFixture<SampleManualTests.Built>
{
    private readonly Built _built;

    public SampleManualTests(Built built) => _built = built;

    /// <summary>The manual built once with pinned inputs, written to a temporary file.</summary>
    public sealed class Built : IDisposable
    {
        private readonly FontReportSupport.TempDir _dir = new();

        public Built()
        {
            using var doc = ManualDocument.Build();
            using var ms = new MemoryStream();
            doc.Save(ms);
            Pdf = ms.ToArray();
            Diagnostics = [.. doc.Diagnostics.Select(d => $"{d.Severity} {d.Code}")];
            BandTruncations = doc.BandTruncations.Count;
            EncodingWarnings = doc.TextEncodingWarnings.Count;
            Path = _dir.Path("manual.pdf");
            File.WriteAllBytes(Path, Pdf);
        }

        public byte[] Pdf { get; }
        public string Path { get; }
        public string[] Diagnostics { get; }
        public int BandTruncations { get; }
        public int EncodingWarnings { get; }

        public void Dispose() => _dir.Dispose();
    }

    // ── The oracles ──────────────────────────────────────────────────────────

    private static string Run(string tool, params string[] args)
    {
        if (!ExternalTool.TryRun(tool, args, out var exit, out var stdout, out var stderr, out var timedOut,
                timeoutMs: 120_000))
            OracleGate.Unavailable(tool);
        Assert.False(timedOut, $"{tool} timed out.");
        Assert.True(exit == 0, $"{tool} exited {exit}.\n{stdout}\n{stderr}");
        return stdout;
    }

    private string Text(int? page = null) => page is { } p
        ? Run("pdftotext", "-layout", "-f", p.ToString(CultureInfo.InvariantCulture), "-l",
            p.ToString(CultureInfo.InvariantCulture), _built.Path, "-")
        : Run("pdftotext", "-layout", _built.Path, "-");

    private JsonDocument QpdfJson() => JsonDocument.Parse(
        Run("qpdf", "--json", "--json-key=qpdf", "--json-key=pages", "--json-key=outlines",
            "--json-key=pagelabels", _built.Path));

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    // The objects of qpdf's JSON, keyed "obj:N 0 R", each wrapped as {"value": ...}.
    private static JsonElement Objects(JsonDocument json) => json.RootElement.GetProperty("qpdf")[1];

    private static JsonElement Value(JsonElement objects, string reference) =>
        objects.GetProperty("obj:" + reference).GetProperty("value");

    private static JsonElement RootDictionary(JsonElement objects) =>
        Value(objects, objects.GetProperty("trailer").GetProperty("value").GetProperty("/Root").GetString()!);

    // Walks a name tree of the shape the library writes, a flat /Names array or /Kids of such
    // arrays,
    // and returns each name with the reference its destination is stored under.
    private static Dictionary<string, string> NamedDestinations(JsonElement objects)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var dests = RootDictionary(objects).GetProperty("/Names").GetProperty("/Dests");
        Collect(dests);
        return result;

        void Collect(JsonElement node)
        {
            if (node.TryGetProperty("/Names", out var names))
            {
                var list = names.EnumerateArray().ToArray();
                for (var i = 0; i + 1 < list.Length; i += 2)
                    result.Add(list[i].GetString()!["u:".Length..], list[i + 1].GetString()!);
            }

            if (node.TryGetProperty("/Kids", out var kids))
            {
                foreach (var kid in kids.EnumerateArray())
                    Collect(Value(objects, kid.GetString()!));
            }
        }
    }

    private static int PageIndexOfDestination(JsonDocument json, JsonElement objects, string reference)
    {
        var page = Value(objects, reference)[0].GetString()!;
        var pages = json.RootElement.GetProperty("pages").EnumerateArray().ToArray();
        for (var i = 0; i < pages.Length; i++)
        {
            if (pages[i].GetProperty("object").GetString() == page)
                return i;
        }

        Assert.Fail($"A destination points at {page}, which is not a page of the document.");
        return -1;
    }

    // The link annotations of every page: the page index, the rectangle, and either a named
    // destination or a URI.
    private sealed record PageLink(int Page, double[] Rect, string? Dest, string? Uri);

    private static List<PageLink> AllLinks(JsonDocument json)
    {
        var objects = Objects(json);
        var result = new List<PageLink>();
        var pages = json.RootElement.GetProperty("pages").EnumerateArray().ToArray();
        for (var i = 0; i < pages.Length; i++)
        {
            var page = Value(objects, pages[i].GetProperty("object").GetString()!);
            if (!page.TryGetProperty("/Annots", out var annots))
                continue;
            foreach (var reference in annots.EnumerateArray())
            {
                var annot = Value(objects, reference.GetString()!);
                if (annot.GetProperty("/Subtype").GetString() != "/Link")
                    continue;
                var rect = annot.GetProperty("/Rect").EnumerateArray().Select(n => n.GetDouble()).ToArray();
                string? dest = annot.TryGetProperty("/Dest", out var d) ? d.GetString()!["u:".Length..] : null;
                string? uri = annot.TryGetProperty("/A", out var a) && a.TryGetProperty("/URI", out var u)
                    ? u.GetString()!["u:".Length..]
                    : null;
                result.Add(new PageLink(i, rect, dest, uri));
            }
        }

        return result;
    }

    // The label a page prints, read from the label dictionary that /PageLabels gives it (qpdf
    // resolves the ranges), and not from the page's position.
    private static string LabelOf(JsonDocument json, int pageIndex)
    {
        var label = json.RootElement.GetProperty("pages")[pageIndex].GetProperty("label");
        var number = label.GetProperty("/St").GetInt32();
        var prefix = label.TryGetProperty("/P", out var p) ? p.GetString()!["u:".Length..] : "";
        return prefix + label.GetProperty("/S").GetString() switch
        {
            "/D" => number.ToString(CultureInfo.InvariantCulture),
            "/r" => LowerRoman(number),
            var other => throw new InvalidOperationException($"Unexpected label style {other}."),
        };
    }

    private static string LowerRoman(int n)
    {
        var sb = new StringBuilder();
        foreach (var (value, text) in new[] { (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"),
                     (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i") })
        {
            while (n >= value)
            {
                sb.Append(text);
                n -= value;
            }
        }

        return sb.ToString();
    }

    private sealed record Word(double X0, double Y0, double X1, double Y1, string Text);

    // The words of a page with the page's height, from pdftotext -bbox. The y axis runs down.
    private (double Height, List<Word> Words) Words(int page)
    {
        var xml = Run("pdftotext", "-bbox", "-f", page.ToString(CultureInfo.InvariantCulture), "-l",
            page.ToString(CultureInfo.InvariantCulture), _built.Path, "-");
        var height = double.Parse(
            Regex.Match(xml, "<page width=\"[0-9.]+\" height=\"([0-9.]+)\"").Groups[1].Value,
            CultureInfo.InvariantCulture);
        static double N(string v) => double.Parse(v, CultureInfo.InvariantCulture);
        var words = Regex.Matches(xml,
                "<word xMin=\"([0-9.]+)\" yMin=\"([0-9.]+)\" xMax=\"([0-9.]+)\" yMax=\"([0-9.]+)\">([^<]*)</word>")
            .Select(m => new Word(N(m.Groups[1].Value), N(m.Groups[2].Value), N(m.Groups[3].Value),
                N(m.Groups[4].Value), WebUtility.HtmlDecode(m.Groups[5].Value)))
            .ToList();
        return (height, words);
    }

    // The words whose centre lies inside the link's rectangle: the text the link covers.
    private static string TextUnder(PageLink link, (double Height, List<Word> Words) page)
    {
        var (x0, y0, x1, y1) = (link.Rect[0], page.Height - link.Rect[3], link.Rect[2], page.Height - link.Rect[1]);
        return string.Join(' ', page.Words
            .Where(w => (w.X0 + w.X1) / 2 >= x0 - 1 && (w.X0 + w.X1) / 2 <= x1 + 1
                && (w.Y0 + w.Y1) / 2 >= y0 && (w.Y0 + w.Y1) / 2 <= y1)
            .OrderBy(w => Math.Round(w.Y0)).ThenBy(w => w.X0)
            .Select(w => w.Text));
    }

    // ── The file ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheManual_saves_withNoDiagnostics_andRunsToTwentyToThirtyPages()
    {
        Assert.Empty(_built.Diagnostics);
        Assert.Equal(0, _built.BandTruncations);
        Assert.Equal(0, _built.EncodingWarnings);

        var pages = int.Parse(Run("qpdf", "--show-npages", _built.Path).Trim(), CultureInfo.InvariantCulture);
        Assert.InRange(pages, 20, 30);
    }

    [Fact]
    public void TheManual_isPdfA2uCompliant()
    {
        var report = FontReportSupport.VeraPdf("2u", _built.Pdf);
        Assert.Empty(FontReportSupport.FailedRules(report));
        Assert.Contains("isCompliant=\"true\"", report, StringComparison.Ordinal);
        Assert.Contains("PDF/A-2u validation profile", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TheManual_passesQpdfCheck()
    {
        var output = Run("qpdf", "--check", _built.Path);
        Assert.Contains("No syntax or stream encoding errors found", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryNamedDestination_resolvesToAPage_andTheChapterOnesLandOnTheirChapter()
    {
        using var json = QpdfJson();
        var objects = Objects(json);
        var dests = NamedDestinations(objects);

        // Every anchor the sample sets is there, and every one lands on a real page.
        foreach (var name in new[] { "contents", "ch1", "ch8", "app-A", "app-C", "sec-3.2", "fig-exploded-view",
                     Troubleshooting.ErrorTableAnchor, Network.ConsoleAnchor, Appendices.CommandTableAnchor })
            Assert.True(dests.ContainsKey(name), $"The named destination {name} is missing.");
        Assert.True(dests.Count >= 40, $"Only {dests.Count} named destinations.");
        foreach (var (name, reference) in dests)
            Assert.InRange(PageIndexOfDestination(json, objects, reference), 0, 40);

        // A value-level check: the page a chapter anchor names holds that chapter's heading. The
        // running header repeats the title on every page of the chapter, so the heading's own line
        // is
        // told apart by counting: the first page of a chapter shows the title twice, the header and
        // the heading, and every later page once.
        var titles = new Dictionary<string, string>
        {
            ["ch1"] = "1  Introduction",
            ["ch2"] = "2  Veiligheidsinstructies",
            ["ch3"] = "3  Installation",
            ["ch4"] = "4  Daily use",
            ["ch5"] = "5  Network and service console",
            ["ch6"] = "6  Care and maintenance",
            ["ch7"] = "7  Troubleshooting",
            ["ch8"] = "8  Specifications and warranty",
        };
        foreach (var (anchor, title) in titles)
        {
            var index = PageIndexOfDestination(json, objects, dests[anchor]);
            var squashed = Squash(title);
            var onPage = Text(index + 1).ReplaceLineEndings("\n").Split('\n').Count(l => Squash(l) == squashed);
            Assert.True(onPage == 2, $"{anchor} lands on page {index + 1}, where the title \"{title}\" is the whole of {onPage} lines.");
        }
    }

    [Fact]
    public void EveryBookmark_resolvesToAPage()
    {
        using var json = QpdfJson();

        var count = 0;
        void Walk(JsonElement nodes)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                count++;
                Assert.True(node.GetProperty("destpageposfrom1").ValueKind == JsonValueKind.Number,
                    $"The bookmark {node.GetProperty("title")} does not resolve to a page.");
                Walk(node.GetProperty("kids"));
            }
        }

        Walk(json.RootElement.GetProperty("outlines"));
        Assert.True(count >= 40, $"Only {count} bookmarks.");
    }

    [Fact]
    public void TheOnlyExternalLink_isTheColophonAddress()
    {
        using var json = QpdfJson();
        var external = AllLinks(json).Where(l => l.Uri is not null).ToArray();
        var link = Assert.Single(external);
        Assert.Equal(ManualDocument.ColophonAddress, link.Uri);
        Assert.Equal(1, link.Page);
        Assert.Null(link.Dest);
    }

    [Fact]
    public void ThePageLabels_areRomanForTheFrontMatterAndDecimalFromTheFirstChapter()
    {
        using var json = QpdfJson();
        var objects = Objects(json);
        var labels = json.RootElement.GetProperty("pagelabels").EnumerateArray().ToArray();

        Assert.Equal(2, labels.Length);
        Assert.Equal(0, labels[0].GetProperty("index").GetInt32());
        Assert.Equal("/r", labels[0].GetProperty("label").GetProperty("/S").GetString());
        Assert.Equal("/D", labels[1].GetProperty("label").GetProperty("/S").GetString());
        Assert.Equal(1, labels[1].GetProperty("label").GetProperty("/St").GetInt32());

        // The decimal range starts on the page of chapter 1 and on a right-hand (odd) page.
        var chapterOne = PageIndexOfDestination(json, objects, NamedDestinations(objects)["ch1"]);
        Assert.Equal(chapterOne, labels[1].GetProperty("index").GetInt32());
        Assert.Equal(0, chapterOne % 2);

        // The footer prints the label: "Page i" on the title page, "Page 1" on chapter 1.
        Assert.Matches(@"Page i of \d+", Squash(Text(1)));
        Assert.Matches(@"Page 1 of \d+", Squash(Text(chapterOne + 1)));
        // The header prints the chapter the page is in: the page after chapter 3 starts is still in
        // it.
        var chapterThree = PageIndexOfDestination(json, objects, NamedDestinations(objects)["ch3"]);
        Assert.Contains("3 Installation", Squash(Text(chapterThree + 2)), StringComparison.Ordinal);
        Assert.DoesNotContain("3 Installation", Squash(Text(chapterOne + 1)), StringComparison.Ordinal);
    }

    // ── The text ─────────────────────────────────────────────────────────────

    [Fact]
    public void TheDutchPassage_readsBack_andCarriesItsOwnLanguage()
    {
        var all = Squash(Text());
        Assert.Contains(Safety.FirstSentence, all, StringComparison.Ordinal);
        Assert.Contains("Veiligheidsinstructies", all, StringComparison.Ordinal);

        // The language is on the elements, not only somewhere in the file: the elements that carry
        // nl-NL, by structure type, are every Dutch heading, paragraph, list item (its paragraph),
        // table header and table cell, the contents entries for the chapter included. The counts
        // are
        // those of chapter 2 and its seven contents entries; a Dutch element left untagged lowers
        // one.
        using var json = QpdfJson();
        var objects = Objects(json);
        Assert.Equal("u:en-US", RootDictionary(objects).GetProperty("/Lang").GetString());
        var byType = objects.EnumerateObject()
            .Select(o => o.Value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object ? v : default)
            .Where(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("/Lang", out var l)
                && l.GetString() == "u:nl-NL" && v.TryGetProperty("/S", out _))
            .GroupBy(v => v.GetProperty("/S").GetString()!)
            .ToDictionary(g => g.Key, g => g.Count());
        Assert.Equal(
            new Dictionary<string, int> { ["/H1"] = 1, ["/H2"] = 7, ["/P"] = 27, ["/TD"] = 21, ["/TH"] = 5 },
            byType);
    }

    [Fact]
    public void TheColophon_isOnTheReverseOfTheTitlePage_andNamesTheAddressOnce()
    {
        Assert.Contains(Squash(ManualDocument.ColophonLine), Squash(Text(2)), StringComparison.Ordinal);

        // The name the colophon credits is read from the colophon line, so it is written in one
        // place.
        var name = Regex.Match(ManualDocument.ColophonLine, @"courtesy of the (\w+) team").Groups[1].Value;
        Assert.NotEmpty(name);
        Assert.DoesNotContain(name, Squash(Text(1)), StringComparison.Ordinal);

        var all = Text();
        Assert.Single(Regex.Matches(all, name));
        Assert.Single(Regex.Matches(all, Regex.Escape(ManualDocument.ColophonAddress)));
    }

    [Fact]
    public void EveryInternalLink_resolves_andEachEntryAndCrossReferenceLinksToTheSectionItNames()
    {
        using var json = QpdfJson();
        var objects = Objects(json);
        var dests = NamedDestinations(objects);
        var links = AllLinks(json).Where(l => l.Dest is not null).ToArray();
        var pageCache = new Dictionary<int, (double, List<Word>)>();
        (double Height, List<Word> Words) PageWords(int page) =>
            pageCache.TryGetValue(page, out var cached) ? cached : pageCache[page] = Words(page + 1);

        // Every internal link has a destination, and it lands on a page.
        foreach (var link in links)
        {
            Assert.True(dests.ContainsKey(link.Dest!), $"A link on page {link.Page + 1} names {link.Dest}, which is not a named destination.");
            Assert.InRange(PageIndexOfDestination(json, objects, dests[link.Dest!]), 0, 40);
        }

        var bookmarks = new List<string>();
        void Walk(JsonElement nodes)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var title = Squash(node.GetProperty("title").GetString()!);
                if (Regex.IsMatch(title, @"^((\d+(\.\d+)?|[A-Z]\.\d+) |Appendix )"))
                    bookmarks.Add(title);
                Walk(node.GetProperty("kids"));
            }
        }

        Walk(json.RootElement.GetProperty("outlines"));

        var indexPage = PageIndexOfDestination(json, objects, dests["app-C"]);
        var contents = new List<string>();
        var crossReferences = 0;
        var pageNumbers = 0;
        var indexNumbers = 0;
        foreach (var link in links)
        {
            var words = PageWords(link.Page);
            // pdftotext keeps the comma or bracket that follows a number in the same word.
            var covered = Squash(TextUnder(link, words)).TrimEnd(',', ')', ';', '.');
            var target = PageIndexOfDestination(json, objects, dests[link.Dest!]);
            if (Regex.IsMatch(covered, @"^[0-9A-Z]+\.\d+$"))
            {
                // The section number of a cross-reference links to that section's anchor.
                Assert.Equal("sec-" + covered, link.Dest);
                crossReferences++;
            }
            else if (Regex.IsMatch(covered, @"^(\d+|[ivxlc]+)$"))
            {
                // A printed page number is the label of the page its link lands on.
                Assert.True(LabelOf(json, target) == covered,
                    $"Page {link.Page + 1} prints {covered} for {link.Dest}, whose page is labelled {LabelOf(json, target)}.");
                pageNumbers++;
                if (link.Page >= indexPage)
                {
                    // On the index page the line's term sits at the left of the line. The place the
                    // number links to must hold that term.
                    var y = (link.Rect[1] + link.Rect[3]) / 2;
                    var term = string.Join(' ', words.Words
                        .Where(w => Math.Abs(words.Height - (w.Y0 + w.Y1) / 2 - y) < 4 && w.X1 < link.Rect[0]
                            && char.IsLetter(w.Text[0]))
                        .OrderBy(w => w.X0).Select(w => w.Text));
                    if (term.Length > 0)
                    {
                        var onTarget = Squash(Text(target + 1));
                        Assert.True(onTarget.Contains(term, StringComparison.OrdinalIgnoreCase),
                            $"The index entry \"{term}\" links to {link.Dest}, on page {target + 1}, which does not hold the term.");
                        indexNumbers++;
                    }
                }
            }
            else
            {
                // A contents entry links to the anchor of the heading it names, and that heading's
                // page holds the title.
                var match = Regex.Match(covered, @"^((?<n>[0-9A-Z]+\.\d+)|(?<c>\d+)|Appendix (?<a>[A-Z])\.) (?<t>.+)$");
                Assert.True(match.Success, $"The link on page {link.Page + 1} covers \"{covered}\", which is no heading.");
                var expected = match.Groups["n"].Success ? "sec-" + match.Groups["n"].Value
                    : match.Groups["c"].Success ? "ch" + match.Groups["c"].Value
                    : "app-" + match.Groups["a"].Value;
                Assert.Equal(expected, link.Dest);
                Assert.Contains(Squash(match.Groups["t"].Value), Squash(Text(target + 1)), StringComparison.Ordinal);
                contents.Add(covered);
            }
        }

        // Every numbered bookmark has one contents entry, and no entry lacks a bookmark.
        Assert.Equal(bookmarks.Order(StringComparer.Ordinal), contents.Order(StringComparer.Ordinal));
        Assert.True(contents.Count >= 40, $"Only {contents.Count} contents entries.");
        // Every contents entry prints a page number, and each cross-reference prints one after
        // its section number: a page number that is typed and not a reference is not a link, so it
        // is not counted here.
        Assert.True(crossReferences >= 12, $"Only {crossReferences} linked cross-references.");
        Assert.Equal(Appendices.IndexTerms.Length + 1, indexNumbers);
        Assert.Equal(contents.Count + crossReferences + indexNumbers, pageNumbers);
    }

    [Fact]
    public void EveryPrintedCrossReference_namesAHeadingThatExists_andPrintsThePageOfItsAnchor()
    {
        using var json = QpdfJson();
        var objects = Objects(json);
        var dests = NamedDestinations(objects);
        var all = Squash(Text());
        var references = Regex.Matches(all, @"(?:section|paragraaf) (\d+\.\d+), (?:page|pagina) (\w+)");
        Assert.True(references.Count >= 12, $"Only {references.Count} cross-references.");
        foreach (Match m in references)
        {
            var anchor = "sec-" + m.Groups[1].Value;
            Assert.True(dests.ContainsKey(anchor), $"Section {m.Groups[1].Value} does not exist.");
            var page = PageIndexOfDestination(json, objects, dests[anchor]);
            Assert.Equal(LabelOf(json, page), m.Groups[2].Value);
        }

        // The Dutch chapter's own reference is written in Dutch.
        Assert.Contains("paragraaf 4.5, pagina", all, StringComparison.Ordinal);
    }

    [Fact]
    public void TheErrorTable_spansPages_andRepeatsItsHeaderRow()
    {
        using var json = QpdfJson();
        var objects = Objects(json);
        var first = PageIndexOfDestination(json, objects, NamedDestinations(objects)[Troubleshooting.ErrorTableAnchor]);

        var onFirst = Text(first + 1);
        var onNext = Text(first + 2);
        Assert.Contains("E101", onFirst, StringComparison.Ordinal);
        Assert.Contains("E606", Text(first + 3), StringComparison.Ordinal);

        // The header row, drawn again on the page the table continues on.
        static bool HasHeader(string text) =>
            Regex.IsMatch(text, @"Code\s+Meaning\s+What to do");
        Assert.True(HasHeader(onFirst));
        Assert.True(HasHeader(onNext));

        // All the rows are there, once each.
        var codes = Regex.Matches(Text(), @"\bE[1-6]\d\d\b").Select(m => m.Value).ToArray();
        Assert.Equal(Troubleshooting.ErrorCodeCount, codes.Distinct().Count());
    }

    // ── Reproducibility ──────────────────────────────────────────────────────

    [Fact]
    public void TwoRuns_withPinnedTimestampAndId_areByteIdentical()
    {
        var stamp = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var id = Convert.FromHexString("00112233445566778899AABBCCDDEEFF");

        static byte[] Make(DateTimeOffset stamp, byte[] id)
        {
            using var doc = ManualDocument.Build(stamp, id);
            using var ms = new MemoryStream();
            doc.Save(ms);
            return ms.ToArray();
        }

        var one = Make(stamp, id);
        var two = Make(stamp, id);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(one)), Convert.ToHexString(SHA256.HashData(two)));

        // The pinned values are in the file, so the comparison is not between two empty shells; and
        // a
        // different identifier gives different bytes.
        Assert.Contains("00112233445566778899AABBCCDDEEFF", Encoding.ASCII.GetString(one), StringComparison.OrdinalIgnoreCase);
        var other = Make(stamp, Convert.FromHexString("FFEEDDCCBBAA99887766554433221100"));
        Assert.NotEqual(Convert.ToHexString(SHA256.HashData(one)), Convert.ToHexString(SHA256.HashData(other)));
    }

    [Fact]
    public void TwoSeparateProcesses_writeTheBytesOfTheInProcessBuild()
    {
        // The sample's own program, run twice as separate processes, so that a value that differs
        // per
        // process (a randomised string hash, the clock, a process-wide counter) shows. Both files
        // must
        // equal the in-process build with the same default date and identifier.
        var location = typeof(ManualDocument).Assembly.Location;
        var apphost = Path.ChangeExtension(location, OperatingSystem.IsWindows() ? ".exe" : null);
        Assert.True(File.Exists(apphost), $"The sample's launcher {apphost} is not beside its assembly.");

        using var dir = new FontReportSupport.TempDir();
        var hashes = new List<string>();
        for (var i = 0; i < 2; i++)
        {
            var output = dir.Path($"run{i}.pdf");
            using var process = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(apphost, [output])
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                })!;
            process.StandardOutput.ReadToEnd();
            Assert.True(process.WaitForExit(60_000), "The sample did not finish.");
            Assert.Equal(0, process.ExitCode);
            hashes.Add(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))));
        }

        Assert.Equal(hashes[0], hashes[1]);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(_built.Pdf)), hashes[0]);
    }

    [Fact]
    public void TheMargins_mirror_theInsideIsWiderOnARightHandPage()
    {
        using var json = QpdfJson();
        var objects = Objects(json);
        var chapterOne = PageIndexOfDestination(json, objects, NamedDestinations(objects)["ch1"]) + 1;

        // The left edge of the text on a recto page (odd) is the inner margin and on the verso page
        // after it the outer margin.
        double LeftEdge(int page) => Regex.Matches(
                Run("pdftotext", "-bbox", "-f", page.ToString(CultureInfo.InvariantCulture), "-l",
                    page.ToString(CultureInfo.InvariantCulture), _built.Path, "-"),
                "xMin=\"([0-9.]+)\"")
            .Min(m => double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));

        Assert.Equal(PageGeometry.Inner, LeftEdge(chapterOne), 1);
        Assert.Equal(PageGeometry.Outer, LeftEdge(chapterOne + 1), 1);
    }

    [Fact]
    public void EveryRemainingWorkaround_isMarkedOnThePage_andNoRemovedOneComesBack()
    {
        var all = Squash(Text());

        // One note for each workaround that is still needed, each naming its open issue.
        foreach (var fragment in new[]
        {
            "one-cell tables, because the Layout API has no shaded paragraph (#703)",
            "Figure 1 has no caption",
            "no caption element that stays with a figure (#295)",
            "heading numbers are typed by hand (#301)",
            "a bottom margin adds no space (#704)",
            "nothing can be placed outside the flow (#313)",
            "written by hand from the headings the sample adds",
            "cannot yet generate one (#294)",
            "this index is written by hand",
            "cannot yet collect the terms of a document (#294)",
        })
            Assert.Contains(fragment, all, StringComparison.Ordinal);
        Assert.Equal(5, Regex.Matches(all, "Placeholder:").Count);

        // A workaround the library now removes must not return: page numbers typed as a stand-in,
        // bold button names that are plain, a callout label that cannot be bold, fonts read by
        // name, and an index with no pages.
        foreach (var gone in new[]
        {
            "[p. ?]",
            "page numbers below are not real",
            "should be bold, as they are in the text",
            "label cannot be bold",
            "fonts are read by name",
            "cannot fill in the page number",
            "index needs the page of every term",
        })
            Assert.DoesNotContain(gone, all, StringComparison.Ordinal);
    }

    // ── The sample reaches VellumPdf through the Layout API only ─────────────

    // Read from the compiled assembly, not the source. A using alias, a global using, a type named
    // through var and code that a source generator wrote all leave a type reference in the
    // metadata,
    // where a search of the source for a name would miss them or match a comment.
    private static HashSet<(string Assembly, string Type)> VellumReferences()
    {
        using var stream = File.OpenRead(typeof(ManualDocument).Assembly.Location);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var set = new HashSet<(string, string)>();
        foreach (var handle in md.TypeReferences)
        {
            var reference = md.GetTypeReference(handle);
            if (reference.ResolutionScope.Kind != HandleKind.AssemblyReference)
                continue;
            var assembly = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)reference.ResolutionScope).Name);
            if (!assembly.StartsWith("VellumPdf", StringComparison.Ordinal))
                continue;
            set.Add((assembly, md.GetString(reference.Namespace) + "." + md.GetString(reference.Name)));
        }

        return set;
    }

    [Fact]
    public void TheSample_namesNoRendererCanvasOrPdfDocument()
    {
        var refs = VellumReferences();
        Assert.NotEmpty(refs);

        // Those three are how the sample would leave the Layout API. IRenderer is how it would add
        // an element Layout does not have, PdfCanvas how it would draw one, PdfDocument how it
        // would
        // build the file without Layout. Nothing in the Rendering namespace is a type to name
        // either:
        // those are the renderers behind the elements.
        var forbidden = refs.Where(r =>
            r.Type is "VellumPdf.Layout.Core.IRenderer" or "VellumPdf.Canvas.PdfCanvas"
                or "VellumPdf.Document.PdfDocument" or "VellumPdf.Layout.Core.DrawContext"
                or "VellumPdf.Layout.Core.LayoutContext"
            || r.Type.StartsWith("VellumPdf.Layout.Rendering.", StringComparison.Ordinal)).ToArray();
        Assert.Empty(forbidden);

        // The sample does not declare a type that implements an interface of the Layout.Core
        // namespace.
        var sample = typeof(ManualDocument).Assembly;
        Assert.DoesNotContain(sample.GetTypes(), t => t.GetInterfaces()
            .Any(i => i.Namespace == "VellumPdf.Layout.Core"));
    }

    [Fact]
    public void TheKernelTypesTheSampleTouches_arePinned()
    {
        // Six are types Layout's own public members take or return: PdfConformance,
        // PdfDocumentInfo, PdfRectangle, PdfPageLabelStyle, EmbeddedFontHandle and PdfImageXObject.
        // PageSize (the A4 value), PngImageLoader (how an image is made) and Standard14 (which
        // bundled face to ask for) make values for those. A change to this list is a change to
        // what a Layout-only program must reach for (#706).
        var kernel = VellumReferences()
            .Where(r => r.Assembly == "VellumPdf.Kernel")
            .Select(r => r.Type)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            [
                "VellumPdf.Document.PageSize",
                "VellumPdf.Document.PdfConformance",
                "VellumPdf.Document.PdfDocumentInfo",
                "VellumPdf.Document.PdfPageLabelStyle",
                "VellumPdf.Document.PdfRectangle",
                "VellumPdf.Fonts.EmbeddedFontHandle",
                "VellumPdf.Fonts.Standard14",
                "VellumPdf.Images.PdfImageXObject",
                "VellumPdf.Images.PngImageLoader",
            ],
            kernel);
    }
}
