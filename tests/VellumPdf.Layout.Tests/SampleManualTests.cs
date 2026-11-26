// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Globalization;
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
/// qpdf finds nothing wrong with it, its named destinations and bookmarks resolve, its page labels
/// are the ones built, the Dutch passage and the colophon read back, the same inputs give the same
/// bytes, and the sample reaches VellumPdf only through the Layout API.
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
    public void EveryBookmark_resolvesToAPage_andEveryLinkIsTheOneExternalAddress()
    {
        using var json = QpdfJson();
        var objects = Objects(json);

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

        // The Layout API writes links to a URI only; the manual has exactly one, in the colophon.
        // An internal link would be a GoTo action or a /Dest, and there is none (#299).
        var links = objects.EnumerateObject()
            .Where(o => o.Value.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("/Subtype", out var s) && s.GetString() == "/Link")
            .Select(o => o.Value.GetProperty("value"))
            .ToArray();
        var link = Assert.Single(links);
        Assert.Equal("u:" + ManualDocument.ColophonAddress, link.GetProperty("/A").GetProperty("/URI").GetString());
        Assert.False(link.TryGetProperty("/Dest", out _));
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
    public void EveryCrossReference_namesAHeadingThatExists_andShowsThePlaceholder()
    {
        using var json = QpdfJson();
        var numbers = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement nodes)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                var title = node.GetProperty("title").GetString()!;
                numbers.Add(title.Split(' ')[0]);
                Walk(node.GetProperty("kids"));
            }
        }

        Walk(json.RootElement.GetProperty("outlines"));
        var all = Squash(Text());
        var references = Regex.Matches(all, @"(?:section|paragraaf) (\d+\.\d+) \[p\. \?\]");
        Assert.True(references.Count >= 8, $"Only {references.Count} cross-references.");
        foreach (Match m in references)
            Assert.True(numbers.Contains(m.Groups[1].Value), $"Section {m.Groups[1].Value} does not exist.");
    }

    [Fact]
    public void TheContentsAndIndex_carryAVisiblePlaceholderInsteadOfPageNumbers()
    {
        var contents = Squash(Text(3) + Text(4));
        Assert.Contains("Placeholder", contents, StringComparison.Ordinal);
        Assert.True(Regex.Matches(contents, Regex.Escape(ManualWriter.PagePlaceholder)).Count >= 40);
        Assert.Contains("7.2 Error codes", contents, StringComparison.Ordinal);
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
    public void EveryWorkaround_isMarkedOnThePage_withAPlaceholderNote()
    {
        var all = Squash(Text());
        // One note for each workaround the README lists.
        foreach (var fragment in new[]
        {
            "one-cell tables, because the Layout API has no shaded paragraph",
            "button names in this table should be bold",
            "Figure 1 has no caption",
            "fonts are read by name from resources of the Standard14 package",
            "Heading numbers are typed by hand",
            "a bottom margin adds no space",
        })
            Assert.Contains(fragment, all, StringComparison.OrdinalIgnoreCase);
        Assert.True(Regex.Matches(all, "Placeholder:").Count >= 6);
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
        // PdfDocumentInfo,
        // PdfRectangle, PdfPageLabelStyle, EmbeddedFontHandle and PdfImageXObject. PageSize (the A4
        // value) and PngImageLoader (how an image is made) make values for those. A change to this
        // list is a change to what a Layout-only program must reach for.
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
                "VellumPdf.Images.PdfImageXObject",
                "VellumPdf.Images.PngImageLoader",
            ],
            kernel);
    }
}
