// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VellumPdf.Annotations;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Encryption;
using VellumPdf.Reader;
using VellumPdf.Signing;
using VellumPdf.TestSupport;
using static VellumPdf.Kernel.Tests.SignatureTestHelpers;
using static VellumPdf.Kernel.Tests.TreeTestSupport;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// #262, part 2: <see cref="PdfDocument.AddNamedDestination"/>,
/// <see cref="PdfLinkAnnotation.DestName"/> and <see cref="PdfDocument.AddPageLabelRange"/>,
/// written through the shared catalog builder on <see cref="PdfDocument.Save(Stream)"/> and
/// <see cref="PdfDocument.PrepareForSigning"/> (ISO 32000-2, 12.3.2.4, 12.4.2, Table 161 and Table
/// 176). Exact bytes are asserted as written, structure is read back through
/// <c>VellumPdf.Reader</c>, and qpdf and pdfinfo resolve the destinations on their own.
/// </summary>
public sealed class NavigationTests
{
    private static readonly DateTimeOffset Pinned = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);

    private static PdfDocument NewDocument(int pages, out PdfPage[] list)
    {
        var document = new PdfDocument { Timestamp = Pinned };
        document.Info.Title = "T";
        list = [.. Enumerable.Range(0, pages).Select(_ => document.AddPage(new PdfRectangle(0, 0, 200, 200)))];
        return document;
    }

    private static byte[] Save(PdfDocument document)
    {
        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    private static byte[] Sign(PdfDocument document) =>
        document.PrepareForSigning(new SignaturePlaceholderOptions { SigningTime = Pinned });

    private static string Text(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    // A label prefix as written: UTF-16BE behind the byte order mark FE FF (ISO 32000-2,
    // 7.9.2.2.2).
    private static string PrefixText(PdfObject? value)
    {
        var bytes = Assert.IsType<PdfLiteralString>(value).Bytes.ToArray();
        Assert.Equal([(byte)0xFE, (byte)0xFF], bytes[..2]);
        return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
    }

    private static string CatalogObject(byte[] pdf)
    {
        var matches = Regex.Matches(Text(pdf), @"\d+ 0 obj\n<<\n/Type /Catalog\n.*?\nendobj", RegexOptions.Singleline);
        return Assert.Single(matches).Value;
    }

    private static string ObjectText(byte[] pdf, int number)
    {
        var m = Regex.Match(Text(pdf), $@"(?<![0-9])\n{number} 0 obj\n(?<body>.*?)\nendobj", RegexOptions.Singleline);
        Assert.True(m.Success, $"object {number} not found");
        return m.Groups["body"].Value;
    }

    private static int[] PageObjectNumbers(byte[] pdf)
    {
        var root = Regex.Match(CatalogObject(pdf), @"/Pages (\d+) 0 R").Groups[1].Value;
        var pages = Regex.Match(ObjectText(pdf, int.Parse(root, System.Globalization.CultureInfo.InvariantCulture)), @"/Kids \[(?<k>[^\]]*)\]");
        Assert.True(pages.Success);
        return [.. Regex.Matches(pages.Groups["k"].Value, @"(\d+) 0 R").Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static string WithTemp(byte[] pdf, Func<string, string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "vellum_nav_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "in.pdf");
            File.WriteAllBytes(path, pdf);
            return body(path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Qpdf(IReadOnlyList<string> args)
    {
        ExternalTool.TryRun("qpdf", args, out var exit, out var stdout, out var stderr, out var timedOut);
        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf {string.Join(' ', args)} exit {exit}\n{stdout}\n{stderr}");
        return stdout;
    }

    private static void AssertQpdfCheckClean(byte[] pdf, string? password = null, bool linearized = false) =>
        WithTemp(pdf, path =>
        {
            var stdout = Qpdf(password is null ? ["--check", path] : ["--password=" + password, "--check", path]);
            Assert.Contains("No syntax or stream encoding errors found", stdout);
            if (linearized)
            {
                Assert.Contains("File is linearized", stdout);
                var lin = Qpdf(["--show-linearization", path]);
                Assert.DoesNotContain("WARNING", lin);
            }
            return "";
        });

    private static string Pdfinfo(string path, params string[] extra)
    {
        List<string> args = [.. extra, "-dests", path];
        if (!ExternalTool.TryRun("pdfinfo", args, out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdfinfo");
        Assert.False(timedOut);
        Assert.True(exit == 0, $"pdfinfo -dests exit {exit}\n{stdout}\n{stderr}");
        return stdout;
    }

    // A document of 4 pages with roman then decimal labels, two destinations and one link on page 1
    // that names the first.
    private static PdfDocument Navigation(Action<PdfDocument>? extra = null)
    {
        var document = NewDocument(4, out var pages);
        document.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman);
        document.AddPageLabelRange(2, PdfPageLabelStyle.Decimal);
        document.AddNamedDestination("chap-1", pages[2], 0, 700);
        document.AddNamedDestination("end", pages[3], 10, 20);
        document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(10, 10, 60, 30), DestName = "chap-1" });
        extra?.Invoke(document);
        return document;
    }

    // ---- page labels --------------------------------------------------------------------------

    [Fact]
    public void Labels_romanThenDecimal_writeTheExactNumberTree()
    {
        var document = NewDocument(4, out _);
        document.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman);
        document.AddPageLabelRange(2, PdfPageLabelStyle.Decimal);
        var catalog = CatalogObject(Save(document));
        Assert.EndsWith("\n/PageLabels <<\n/Nums [0 <<\n/S /r\n>> 2 <<\n/S /D\n>>]\n>>\n>>\nendobj", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("/Names", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_inTheOppositeOrder_writeTheSameBytes()
    {
        var forward = NewDocument(4, out _);
        forward.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman);
        forward.AddPageLabelRange(2, PdfPageLabelStyle.Decimal);
        var backward = NewDocument(4, out _);
        backward.AddPageLabelRange(2, PdfPageLabelStyle.Decimal);
        backward.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman);
        Assert.Equal(CatalogGoldenTests.NormalisedSha256(Save(forward)), CatalogGoldenTests.NormalisedSha256(Save(backward)));
    }

    [Fact]
    public void Labels_withoutARangeAtZero_getADecimalOneThere()
    {
        var document = NewDocument(4, out _);
        document.AddPageLabelRange(2, PdfPageLabelStyle.Decimal);
        Assert.EndsWith("\n/PageLabels <<\n/Nums [0 <<\n/S /D\n>> 2 <<\n/S /D\n>>]\n>>\n>>\nendobj", CatalogObject(Save(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_prefixAndFirstNumber_writeTheUtf16Prefix()
    {
        var document = NewDocument(8, out _);
        document.AddPageLabelRange(7, PdfPageLabelStyle.Decimal, "A-", 8);
        var catalog = CatalogObject(Save(document));
        Assert.Contains("7 <<\n/S /D\n/P (þÿ\0A\0-)\n/St 8\n>>", catalog, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_emptyPrefix_isTheSameAsNone()
    {
        var none = NewDocument(2, out _);
        none.AddPageLabelRange(0, PdfPageLabelStyle.Decimal);
        var empty = NewDocument(2, out _);
        empty.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, "");
        Assert.Equal(CatalogGoldenTests.NormalisedSha256(Save(none)), CatalogGoldenTests.NormalisedSha256(Save(empty)));
    }

    [Fact]
    public void Labels_noneWithNoPrefix_isAccepted_andWritesAnEmptyDictionary()
    {
        var document = NewDocument(2, out _);
        document.AddPageLabelRange(0, PdfPageLabelStyle.None);
        Assert.EndsWith("/PageLabels <<\n/Nums [0 <<\n>>]\n>>\n>>\nendobj", CatalogObject(Save(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_qpdfReportsEachPagesLabel()
    {
        var pdf = Save(Navigation());
        var json = WithTemp(pdf, path => Qpdf(["--json=2", "--json-key=pages", path]));
        var labels = JsonDocument.Parse(json).RootElement.GetProperty("pages").EnumerateArray()
            .Select(p => (p.GetProperty("label").GetProperty("/S").GetString(), p.GetProperty("label").GetProperty("/St").GetInt32()))
            .ToArray();
        Assert.Equal([("/r", 1), ("/r", 2), ("/D", 1), ("/D", 2)], labels);
    }

    [Fact]
    public void Labels_roundTripThroughTheReader_everyKeyAndValue()
    {
        var document = NewDocument(12, out _);
        document.AddPageLabelRange(0, PdfPageLabelStyle.UpperRoman, "Front ", 3);
        document.AddPageLabelRange(5, PdfPageLabelStyle.UpperLetters);
        document.AddPageLabelRange(9, PdfPageLabelStyle.LowerLetters, null, 4);
        var (reader, root) = OpenRoot(Save(document), nameTree: false);
        var walked = Walk(reader, root, nameTree: false);
        Assert.Equal([0L, 5L, 9L], walked.Entries.Select(e => (long)e.Key).ToArray());
        var first = Assert.IsType<PdfDictionary>(walked.Entries[0].Value);
        Assert.Equal("R", Assert.IsType<PdfName>(first.Get(new PdfName("S"))).Value);
        Assert.Equal(3, Assert.IsType<PdfInteger>(first.Get(new PdfName("St"))).Value);
        Assert.Equal("Front ", PrefixText(first.Get(new PdfName("P"))));
        var second = (PdfDictionary)walked.Entries[1].Value;
        Assert.Equal("A", Assert.IsType<PdfName>(second.Get(new PdfName("S"))).Value);
        Assert.Null(second.Get(new PdfName("St")));
        Assert.Null(second.Get(new PdfName("P")));
        var third = (PdfDictionary)walked.Entries[2].Value;
        Assert.Equal("a", Assert.IsType<PdfName>(third.Get(new PdfName("S"))).Value);
        Assert.Equal(4, Assert.IsType<PdfInteger>(third.Get(new PdfName("St"))).Value);
    }

    // ---- destinations and links ---------------------------------------------------------------

    [Fact]
    public void Destination_writesTheTreeTheArrayAndTheLink()
    {
        var document = NewDocument(4, out var pages);
        document.AddNamedDestination("chap-1", pages[2], 0, 700);
        document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(10, 10, 60, 30), DestName = "chap-1" });
        var pdf = Save(document);

        var catalog = CatalogObject(pdf);
        var tree = Regex.Match(catalog, @"/Names <<\n/Dests <<\n/Names \[\(chap-1\) (\d+) 0 R\]\n>>\n>>\n>>\nendobj$");
        Assert.True(tree.Success, catalog);
        var pageObjects = PageObjectNumbers(pdf);
        Assert.Equal($"[{pageObjects[2]} 0 R /XYZ 0 700 null]", ObjectText(pdf, int.Parse(tree.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)));

        var link = Text(pdf).Split("endobj").Single(o => o.Contains("/Subtype /Link", StringComparison.Ordinal));
        Assert.Contains("\n/Dest (chap-1)\n", link, StringComparison.Ordinal);
        Assert.DoesNotContain("/A ", link, StringComparison.Ordinal);
        Assert.DoesNotContain("/GoTo", link, StringComparison.Ordinal);
    }

    [Fact]
    public void Destination_pdfinfoListsThePage_andAReorderedFileMovesIt()
    {
        var pdf = Save(Navigation());
        WithTemp(pdf, path =>
        {
            var lines = Pdfinfo(path).Split('\n', StringSplitOptions.TrimEntries);
            Assert.Contains(lines, l => l.StartsWith("3 ", StringComparison.Ordinal) && l.Contains("chap-1", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.StartsWith("4 ", StringComparison.Ordinal) && l.Contains("end", StringComparison.Ordinal));

            var reordered = Path.Combine(Path.GetDirectoryName(path)!, "out.pdf");
            Qpdf([path, "--pages", ".", "3,1,2,4", "--", reordered]);
            var after = Pdfinfo(reordered).Split('\n', StringSplitOptions.TrimEntries);
            Assert.Contains(after, l => l.StartsWith("1 ", StringComparison.Ordinal) && l.Contains("chap-1", StringComparison.Ordinal));
            return "";
        });
    }

    // Resolves every /Link's /Dest through the name tree as qpdf parsed it, to the page object qpdf
    // lists.
    private static void AssertQpdfResolvesEveryLink(byte[] pdf, string? password, Dictionary<string, int> expectedPageIndexByName)
    {
        List<string> baseArgs = password is null ? [] : ["--password=" + password];
        var pagesJson = WithTemp(pdf, path => Qpdf([.. baseArgs, "--json=2", "--json-key=pages", path]));
        var pageObjects = JsonDocument.Parse(pagesJson).RootElement.GetProperty("pages").EnumerateArray()
            .Select(p => p.GetProperty("object").GetString()!).ToArray();
        var qpdfJson = WithTemp(pdf, path => Qpdf([.. baseArgs, "--json=2", "--json-key=qpdf", path]));
        var objects = JsonDocument.Parse(qpdfJson).RootElement.GetProperty("qpdf")[1];

        JsonElement Resolve(JsonElement value) =>
            value.ValueKind == JsonValueKind.String && Regex.IsMatch(value.GetString()!, @"^\d+ 0 R$")
                ? objects.GetProperty("obj:" + value.GetString()).GetProperty("value")
                : value;

        var catalog = objects.EnumerateObject()
            .Select(o => o.Value.TryGetProperty("value", out var v) ? v : default)
            .First(v => v.ValueKind == JsonValueKind.Object && v.TryGetProperty("/Type", out var t) && t.GetString() == "/Catalog");
        var dests = Resolve(Resolve(catalog.GetProperty("/Names")).GetProperty("/Dests"));

        var leaves = new Dictionary<string, JsonElement>();
        void Collect(JsonElement node)
        {
            node = Resolve(node);
            if (node.TryGetProperty("/Kids", out var kids))
            {
                foreach (var kid in kids.EnumerateArray())
                    Collect(kid);
                return;
            }
            var names = node.GetProperty("/Names").EnumerateArray().ToArray();
            for (var i = 0; i < names.Length; i += 2)
                leaves[names[i].GetString()!] = names[i + 1];
        }
        Collect(dests);

        var seen = 0;
        foreach (var o in objects.EnumerateObject())
        {
            if (!o.Value.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object)
                continue;
            if (!v.TryGetProperty("/Subtype", out var st) || st.GetString() != "/Link")
                continue;
            var key = v.GetProperty("/Dest").GetString()!;
            Assert.StartsWith("u:", key, StringComparison.Ordinal);
            var name = key[2..];
            var array = Resolve(leaves[key]);
            Assert.Equal(pageObjects[expectedPageIndexByName[name]], array[0].GetString());
            seen++;
        }
        Assert.Equal(expectedPageIndexByName.Count, seen);
    }

    [Fact]
    public void Destination_qpdfResolvesEveryLinkToItsPage()
    {
        var pdf = Save(Navigation(d =>
            d.RegisterLinkAnnotation(d.Pages[1], new PdfLinkAnnotation { Rect = new PdfRectangle(1, 1, 5, 5), DestName = "end" })));
        AssertQpdfCheckClean(pdf);
        // Two links: one on page 1 naming chap-1 (page 3) and one on page 2 naming end (page 4).
        AssertQpdfResolvesEveryLink(pdf, null, new Dictionary<string, int> { ["chap-1"] = 2, ["end"] = 3 });
    }

    [Fact]
    public void Destination_qpdfResolvesALinkPerNameToThePageOfThatName()
    {
        var document = NewDocument(4, out var pages);
        var expected = new Dictionary<string, int>();
        for (var i = 0; i < 4; i++)
        {
            var name = "d" + i;
            document.AddNamedDestination(name, pages[i], 1, 2);
            document.RegisterLinkAnnotation(pages[(i + 1) % 4], new PdfLinkAnnotation { Rect = new PdfRectangle(1, 1, 5, 5), DestName = name });
            expected[name] = i;
        }
        AssertQpdfResolvesEveryLink(Save(document), null, expected);
    }

    [Fact]
    public void Destination_roundTripsThroughTheReader()
    {
        var pdf = Save(Navigation());
        var pageObjects = PageObjectNumbers(pdf);
        var (reader, root) = OpenRoot(pdf, nameTree: true);
        var walked = Walk(reader, root, nameTree: true);
        Assert.Equal(["chap-1", "end"], walked.Entries.Select(e => Encoding.ASCII.GetString((byte[])e.Key)).ToArray());
        var first = (PdfArray)reader.ResolveValue(walked.Entries[0].Value)!;
        Assert.Equal(pageObjects[2], Assert.IsType<PdfIndirectReference>(first[0]).ObjectNumber);
        Assert.Equal("XYZ", Assert.IsType<PdfName>(first[1]).Value);
        Assert.Equal(0, Assert.IsType<PdfInteger>(first[2]).Value);
        Assert.Equal(700, Assert.IsType<PdfInteger>(first[3]).Value);
        Assert.IsType<PdfNull>(first[4]);
        var end = (PdfArray)reader.ResolveValue(walked.Entries[1].Value)!;
        Assert.Equal(pageObjects[3], Assert.IsType<PdfIndirectReference>(end[0]).ObjectNumber);
        Assert.Equal("XYZ", Assert.IsType<PdfName>(end[1]).Value);
        Assert.Equal(10, Assert.IsType<PdfInteger>(end[2]).Value);
        Assert.Equal(20, Assert.IsType<PdfInteger>(end[3]).Value);
        Assert.IsType<PdfNull>(end[4]);
    }

    // ---- refusals -----------------------------------------------------------------------------

    [Theory]
    [InlineData("a b")]
    [InlineData("")]
    [InlineData("é")]
    public void DestName_outsideTheRule_isRefusedByTheInitializer(string name)
    {
        var ex = Assert.Throws<ArgumentException>(() => new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = name });
        Assert.Equal("value", ex.ParamName);
    }

    [Fact]
    public void DestName_of128Characters_isRefused_and127AreAccepted()
    {
        Assert.Throws<ArgumentException>(() => new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = new string('a', 128) });
        var link = new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = new string('a', 127) };
        Assert.Equal(127, link.DestName!.Length);
        Assert.Null(new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = null }.DestName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.com")]
    public void Link_withDestNameAndUri_isRefusedAndNotStored(string uri)
    {
        var document = NewDocument(1, out var pages);
        var link = new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "a", Uri = uri };
        var ex = Assert.Throws<ArgumentException>(() => document.RegisterLinkAnnotation(pages[0], link));
        Assert.Equal("annotation", ex.ParamName);
        // Had it been stored, the save would now refuse the unknown name.
        Assert.DoesNotContain("/Subtype /Link", Text(Save(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void Link_withDestNameAndDestPage_isRefusedAndNotStored()
    {
        var document = NewDocument(1, out var pages);
        var link = new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "a", DestPage = pages[0] };
        var ex = Assert.Throws<ArgumentException>(() => document.RegisterLinkAnnotation(pages[0], link));
        Assert.Equal("annotation", ex.ParamName);
        Assert.DoesNotContain("/Subtype /Link", Text(Save(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void Link_withDestName_ignoresDestLeftAndDestTop()
    {
        var document = NewDocument(2, out var pages);
        document.AddNamedDestination("chap-1", pages[1], 0, 0);
        document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "chap-1", DestLeft = 5, DestTop = 6 });
        var link = Text(Save(document)).Split("endobj").Single(o => o.Contains("/Subtype /Link", StringComparison.Ordinal));
        Assert.Contains("\n/Dest (chap-1)\n", link, StringComparison.Ordinal);
        Assert.DoesNotContain("/XYZ", link, StringComparison.Ordinal);
        Assert.DoesNotContain("/A ", link, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_withUriAndDestPage_keepsWritingOnlyTheAction()
    {
        var document = NewDocument(2, out var pages);
        document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), Uri = "https://example.com", DestPage = pages[1] });
        var link = Text(Save(document)).Split("endobj").Single(o => o.Contains("/Subtype /Link", StringComparison.Ordinal));
        Assert.Contains("/A <<\n/S /URI", link, StringComparison.Ordinal);
        Assert.DoesNotContain("/Dest", link, StringComparison.Ordinal);
    }

    [Fact]
    public void Link_naming_anUnknownDestination_isRefusedBySave_andTheRetryWritesOnce()
    {
        PdfDocument Make(bool withDestination)
        {
            var document = NewDocument(2, out var pages);
            document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "missing" });
            if (withDestination)
                document.AddNamedDestination("missing", pages[1], 0, 0);
            return document;
        }

        var document = Make(withDestination: false);
        var ex = Assert.Throws<InvalidOperationException>(() => Save(document));
        Assert.Contains("'missing'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("page 1", ex.Message, StringComparison.Ordinal);
        Assert.Throws<InvalidOperationException>(() => Sign(document));

        document.AddNamedDestination("missing", document.Pages[1], 0, 0);
        var retried = Save(document);
        Assert.Equal(CatalogGoldenTests.NormalisedSha256(Save(Make(withDestination: true))), CatalogGoldenTests.NormalisedSha256(retried));
    }

    [Fact]
    public void Link_naming_anUnknownDestination_isRefusedBySigning_andTheRetryWritesOnce()
    {
        var document = NewDocument(2, out var pages);
        document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "missing" });
        Assert.Throws<InvalidOperationException>(() => Sign(document));
        document.AddNamedDestination("missing", pages[1], 0, 0);
        var retried = Sign(document);

        var oneShot = NewDocument(2, out var p2);
        oneShot.RegisterLinkAnnotation(p2[0], new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "missing" });
        oneShot.AddNamedDestination("missing", p2[1], 0, 0);
        Assert.Equal(CatalogGoldenTests.NormalisedSha256(Sign(oneShot)), CatalogGoldenTests.NormalisedSha256(retried));
    }

    [Fact]
    public void Link_onAPageTheDocumentDoesNotOwn_isNotJudged_andNeverWritten()
    {
        var document = NewDocument(1, out _);
        var foreign = new PdfPage(new PdfRectangle(0, 0, 10, 10));
        document.RegisterLinkAnnotation(foreign, new PdfLinkAnnotation { Rect = new PdfRectangle(0, 0, 1, 1), DestName = "missing" });
        Assert.DoesNotContain("/Subtype /Link", Text(Save(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void NullAnnotation_onAPageTheDocumentDoesNotOwn_stillSaves()
    {
        var document = NewDocument(1, out _);
        document.RegisterLinkAnnotation(new PdfPage(new PdfRectangle(0, 0, 10, 10)), null!);
        Assert.StartsWith("%PDF-", Text(Save(document)), StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_startingAtOrPastThePageCount_areRefusedBySaveAndSigning_andAddingAPageFixesIt()
    {
        var document = NewDocument(4, out _);
        document.AddPageLabelRange(5, PdfPageLabelStyle.Decimal);
        Assert.Throws<InvalidOperationException>(() => Save(document));
        Assert.Throws<InvalidOperationException>(() => Sign(document));
        document.AddPage(new PdfRectangle(0, 0, 200, 200));
        document.AddPage(new PdfRectangle(0, 0, 200, 200));

        var oneShot = NewDocument(6, out _);
        oneShot.AddPageLabelRange(5, PdfPageLabelStyle.Decimal);
        Assert.Equal(CatalogGoldenTests.NormalisedSha256(Save(oneShot)), CatalogGoldenTests.NormalisedSha256(Save(document)));
    }

    [Fact]
    public void Labels_addedOutOfOrder_areJudgedByTheirLargestStart()
    {
        var document = NewDocument(4, out _);
        document.AddPageLabelRange(5, PdfPageLabelStyle.Decimal);
        document.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman);
        Assert.Contains("page index 5", Assert.Throws<InvalidOperationException>(() => Save(document)).Message, StringComparison.Ordinal);
        Assert.Contains("page index 5", Assert.Throws<InvalidOperationException>(() => Sign(document)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Labels_startingAtTheLastPage_areAccepted_andOnePastIsRefused()
    {
        var ok = NewDocument(4, out _);
        ok.AddPageLabelRange(3, PdfPageLabelStyle.Decimal);
        Assert.Contains("/PageLabels", Text(Save(ok)), StringComparison.Ordinal);

        var past = NewDocument(4, out _);
        past.AddPageLabelRange(4, PdfPageLabelStyle.Decimal);
        Assert.Throws<InvalidOperationException>(() => Save(past));
    }

    [Fact]
    public void Labels_onADocumentWithNoPages_giveTheNoPagesRefusalFirst()
    {
        var document = new PdfDocument();
        document.AddPageLabelRange(3, PdfPageLabelStyle.Decimal);
        Assert.Equal("The document has no pages.", Assert.Throws<InvalidOperationException>(() => Save(document)).Message);
    }

    [Fact]
    public void AddNamedDestination_refusesWhatTheTableRefuses_atTheCall()
    {
        var document = NewDocument(2, out var pages);
        Assert.Equal("name", Assert.Throws<ArgumentNullException>(() => document.AddNamedDestination(null!, pages[0], 0, 0)).ParamName);
        Assert.Equal("page", Assert.Throws<ArgumentNullException>(() => document.AddNamedDestination("a", null!, 0, 0)).ParamName);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => document.AddNamedDestination("a b", pages[0], 0, 0)).ParamName);
        document.AddNamedDestination("a", pages[0], 0, 0);
        Assert.Equal("name", Assert.Throws<ArgumentException>(() => document.AddNamedDestination("a", pages[1], 0, 0)).ParamName);
    }

    [Theory]
    [InlineData(double.NaN, 0.0, "left")]
    [InlineData(double.PositiveInfinity, 0.0, "left")]
    [InlineData(double.NegativeInfinity, 0.0, "left")]
    [InlineData(0.0, double.NaN, "top")]
    [InlineData(0.0, double.PositiveInfinity, "top")]
    [InlineData(0.0, double.NegativeInfinity, "top")]
    [InlineData(3.404e38, 0.0, "left")]
    [InlineData(0.0, -3.404e38, "top")]
    public void AddNamedDestination_refusesACoordinateOutsideTheRealRange(double left, double top, string paramName)
    {
        var document = NewDocument(1, out var pages);
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => document.AddNamedDestination("a", pages[0], left, top));
        Assert.Equal(paramName, ex.ParamName);
    }

    [Fact]
    public void AddNamedDestination_refusesTheNextRealPastTheBound_onEitherSide()
    {
        var past = Math.BitIncrement(3.403e38);
        var document = NewDocument(1, out var pages);
        foreach (var (left, top, paramName) in new[] { (past, 0.0, "left"), (-past, 0.0, "left"), (0.0, past, "top"), (0.0, -past, "top") })
            Assert.Equal(paramName, Assert.Throws<ArgumentOutOfRangeException>(() => document.AddNamedDestination("a", pages[0], left, top)).ParamName);
    }

    [Fact]
    public void AddNamedDestination_acceptsTheBound_andTheFileIsValid()
    {
        var document = NewDocument(1, out var pages);
        document.AddNamedDestination("hi", pages[0], 3.403e38, -3.403e38);
        var pdf = Save(document);
        AssertQpdfCheckClean(pdf);
        Assert.Contains("/XYZ 340300000000000000000000000000000000000.0 -340300000000000000000000000000000000000.0 null]", Text(pdf), StringComparison.Ordinal);
    }

    [Fact]
    public void AddNamedDestination_refusesAPageThisDocumentDidNotReturn()
    {
        var document = NewDocument(1, out _);
        var other = NewDocument(1, out var otherPages);
        var byConstructor = new PdfPage(new PdfRectangle(0, 0, 10, 10));
        Assert.Equal("page", Assert.Throws<ArgumentException>(() => document.AddNamedDestination("a", byConstructor, 0, 0)).ParamName);
        Assert.Equal("page", Assert.Throws<ArgumentException>(() => document.AddNamedDestination("a", otherPages[0], 0, 0)).ParamName);
        Assert.NotNull(other);
        // Nothing was added, so the name is still free.
        document.AddNamedDestination("a", document.Pages[0], 0, 0);
    }

    [Fact]
    public void AddPageLabelRange_refusesWhatTheTableRefuses_atTheCall()
    {
        var document = NewDocument(2, out _);
        Assert.Equal("startPageIndex", Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPageLabelRange(-1, PdfPageLabelStyle.Decimal)).ParamName);
        Assert.Equal("firstNumber", Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, null, 0)).ParamName);
        Assert.Equal("style", Assert.Throws<ArgumentOutOfRangeException>(() => document.AddPageLabelRange(0, (PdfPageLabelStyle)99)).ParamName);
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => document.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, "\uD800")).ParamName);
        Assert.Equal("prefix", Assert.Throws<ArgumentException>(() => document.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, new string('x', 16_383))).ParamName);
        document.AddPageLabelRange(0, PdfPageLabelStyle.Decimal, new string('x', 16_382));
        Assert.Equal("startPageIndex", Assert.Throws<ArgumentException>(() => document.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman)).ParamName);
    }

    [Fact]
    public void AddingAfterTheDocumentWasWritten_isRefused()
    {
        var document = NewDocument(1, out var pages);
        Save(document);
        Assert.Contains("already been written", Assert.Throws<InvalidOperationException>(() => document.AddNamedDestination("a", pages[0], 0, 0)).Message, StringComparison.Ordinal);
        Assert.Contains("already been written", Assert.Throws<InvalidOperationException>(() => document.AddPageLabelRange(0, PdfPageLabelStyle.Decimal)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddingAfterTheDocumentWasDisposed_isRefused()
    {
        var document = NewDocument(1, out var pages);
        document.Dispose();
        Assert.Throws<ObjectDisposedException>(() => document.AddNamedDestination("a", pages[0], 0, 0));
        Assert.Throws<ObjectDisposedException>(() => document.AddPageLabelRange(0, PdfPageLabelStyle.Decimal));
        // The disposed check comes before the argument checks, so a null name or page is not
        // reported first.
        Assert.Throws<ObjectDisposedException>(() => document.AddNamedDestination(null!, pages[0], 0, 0));
        Assert.Throws<ObjectDisposedException>(() => document.AddNamedDestination("a", null!, 0, 0));
    }

    [Fact]
    public void AddingAfterARefusedSave_isStillAllowed()
    {
        var document = NewDocument(1, out var pages);
        document.AddPageLabelRange(9, PdfPageLabelStyle.Decimal);
        Assert.Throws<InvalidOperationException>(() => Save(document));
        document.AddNamedDestination("a", pages[0], 0, 0);
        document.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman);
    }

    // ---- no new output for a document that uses none of it ------------------------------------

    [Fact]
    public void ADocumentThatUsesNoneOfIt_hasNeitherKey()
    {
        var catalog = CatalogObject(Save(NewDocument(2, out _)));
        Assert.DoesNotContain("/Names", catalog, StringComparison.Ordinal);
        Assert.DoesNotContain("/PageLabels", catalog, StringComparison.Ordinal);
    }

    // ---- other save modes ---------------------------------------------------------------------

    [Fact]
    public void Encrypted_destinationsAndLabels_resolveWithThePassword()
    {
        var document = Navigation();
        document.Encrypt(new PdfEncryptionSettings { UserPassword = "u", OwnerPassword = "o" });
        var pdf = Save(document);
        AssertQpdfCheckClean(pdf, password: "u");

        var reader = PdfReader.Open(pdf, new PdfReaderOptions { Password = "u" });
        var dests = (PdfDictionary)reader.ResolveValue(((PdfDictionary)reader.ResolveValue(reader.Catalog.Get(new PdfName("Names"))!)!).Get(new PdfName("Dests"))!)!;
        var walked = Walk(reader, dests, nameTree: true);
        Assert.Equal("chap-1", Encoding.ASCII.GetString((byte[])walked.Entries[0].Key));

        var pagesRoot = (PdfDictionary)reader.ResolveValue(reader.Catalog.Get(PdfName.Pages)!)!;
        var firstPage = (PdfDictionary)reader.ResolveValue(((PdfArray)reader.ResolveValue(pagesRoot.Get(PdfName.Kids)!)!)[0])!;
        var annot = (PdfDictionary)reader.ResolveValue(((PdfArray)reader.ResolveValue(firstPage.Get(new PdfName("Annots"))!)!)[0])!;
        Assert.Equal("chap-1", Encoding.ASCII.GetString(Assert.IsType<PdfLiteralString>(reader.ResolveValue(annot.Get(new PdfName("Dest"))!)).Bytes.ToArray()));

        AssertQpdfResolvesEveryLink(pdf, "u", new Dictionary<string, int> { ["chap-1"] = 2 });
        WithTemp(pdf, path =>
        {
            Assert.Contains("chap-1", Pdfinfo(path, "-upw", "u"), StringComparison.Ordinal);
            return "";
        });
    }

    [Fact]
    public void Linearized_andObjectStreams_passQpdfAndResolve()
    {
        var linear = Save(Navigation(d => d.Linearize = true));
        AssertQpdfCheckClean(linear, linearized: true);
        AssertQpdfResolvesEveryLink(linear, null, new Dictionary<string, int> { ["chap-1"] = 2 });

        var objStm = Save(Navigation(d => d.UseObjectStreams = true));
        AssertQpdfCheckClean(objStm);
        AssertQpdfResolvesEveryLink(objStm, null, new Dictionary<string, int> { ["chap-1"] = 2 });
        var (reader, root) = OpenRoot(objStm, nameTree: true);
        Assert.Equal(2, Walk(reader, root, nameTree: true).Entries.Count);
    }

    // ---- signing path, DSS and archive timestamp ----------------------------------------------

    [Fact]
    public void Signing_dssAndArchiveTimestamp_keepTheTreesOfTheFirstRevision()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=VellumPdf Navigation Test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var pinned = new DateTimeOffset(2026, 1, 15, 10, 30, 0, TimeSpan.Zero);

        // Two ranges with a prefix and a first number, so /P and /St are compared as well as /S.
        var document = NewDocument(4, out var pages);
        document.AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman, "Front ", 3);
        document.AddPageLabelRange(2, PdfPageLabelStyle.Decimal);
        document.AddNamedDestination("chap-1", pages[2], 0, 700);
        document.AddNamedDestination("end", pages[3], 10, 20);
        document.RegisterLinkAnnotation(pages[0], new PdfLinkAnnotation { Rect = new PdfRectangle(10, 10, 60, 30), DestName = "chap-1" });
        using var signed = new MemoryStream();
        document.Sign(signed, new PdfSignatureSettings { Certificate = cert, TimestampClient = new TestTimestampClient(pinned) });
        var first = signed.ToArray();
        var withDss = DssBuilder.AddLongTermValidation(first, new CannedRevocation());
        var archived = ArchiveTimestampBuilder.AddArchiveTimestamp(withDss, new TestTimestampClient(pinned));

        static string Describe(PdfObject? value) => value switch
        {
            null => "absent",
            PdfName n => "/" + n.Value,
            PdfInteger i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            PdfReal r => r.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            PdfNull => "null",
            PdfIndirectReference r => r.ObjectNumber + " R",
            PdfLiteralString s => Convert.ToHexString(s.Bytes.Span),
            PdfArray a => "[" + string.Join(' ', Enumerable.Range(0, a.Count).Select(i => Describe(a[i]))) + "]",
            _ => value.GetType().Name,
        };

        (string[] Names, string[] Dests, long[] Starts, string[] Ranges) Trees(byte[] pdf)
        {
            var (r, dests) = OpenRoot(pdf, nameTree: true);
            var walkedDests = Walk(r, dests, nameTree: true).Entries;
            var names = walkedDests.Select(e => Encoding.ASCII.GetString((byte[])e.Key)).ToArray();
            var resolved = walkedDests.Select(e => Describe(r.ResolveValue(e.Value))).ToArray();
            var (r2, labels) = OpenRoot(pdf, nameTree: false);
            var ranges = Walk(r2, labels, nameTree: false).Entries;
            return (names, resolved, [.. ranges.Select(e => (long)e.Key)],
                [.. ranges.Select(e => string.Join(' ', new[] { "S", "P", "St" }.Select(k => k + "=" + Describe(((PdfDictionary)e.Value).Get(new PdfName(k))))))]);
        }

        var pageObjects = PageObjectNumbers(first);
        var expected = Trees(first);
        Assert.Equal(["chap-1", "end"], expected.Names);
        Assert.Equal([$"[{pageObjects[2]} R /XYZ 0 700 null]", $"[{pageObjects[3]} R /XYZ 10 20 null]"], expected.Dests);
        Assert.Equal([0L, 2L], expected.Starts);
        Assert.Equal(["S=/r P=FEFF00460072006F006E00740020 St=3", "S=/D P=absent St=absent"], expected.Ranges);
        foreach (var later in new[] { Trees(withDss), Trees(archived) })
        {
            Assert.Equal(expected.Names, later.Names);
            Assert.Equal(expected.Dests, later.Dests);
            Assert.Equal(expected.Starts, later.Starts);
            Assert.Equal(expected.Ranges, later.Ranges);
        }
    }

    // ---- veraPDF ------------------------------------------------------------------------------

    private static List<string> FailedRules(byte[] pdf) =>
        [.. WithTemp(pdf, path =>
        {
            ExternalTool.TryRun("verapdf", ["--flavour", "2u", path], out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);
            Assert.False(timedOut, "veraPDF timed out.");
            Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
            Assert.Contains("<validationReport", report);
            return string.Join('\n', Regex.Matches(report, @"<rule\b[^>]*>")
                .Where(m => m.Value.Contains("status=\"failed\"", StringComparison.Ordinal))
                .Select(m => Regex.Match(m.Value, "clause=\"([^\"]*)\"").Groups[1].Value + "-" + Regex.Match(m.Value, "testNumber=\"([^\"]*)\"").Groups[1].Value)
                .Order(StringComparer.Ordinal));
        }).Split('\n', StringSplitOptions.RemoveEmptyEntries)];

    [Fact]
    public void PdfA2u_navigation_passesVeraPdf_onBothPaths()
    {
        var saved = Save(Navigation(d => d.Conformance = PdfConformance.PdfA2u));
        var signed = Sign(Navigation(d => d.Conformance = PdfConformance.PdfA2u));
        // The fixtures hold the features, so the verdict is about them.
        foreach (var pdf in new[] { saved, signed })
        {
            Assert.Contains("/PageLabels <<", Text(pdf), StringComparison.Ordinal);
            Assert.Contains("/Dest (chap-1)", Text(pdf), StringComparison.Ordinal);
        }
        Assert.Empty(FailedRules(saved));
        // The placeholder signature's own rules are not the subject here; only the navigation
        // entries must add no failure over a signing document without them.
        var plain = Sign(NewDocumentWith(d => d.Conformance = PdfConformance.PdfA2u));
        Assert.Equal(FailedRules(plain), FailedRules(signed));
    }

    private static PdfDocument NewDocumentWith(Action<PdfDocument> tweak)
    {
        var document = NewDocument(4, out _);
        tweak(document);
        return document;
    }

    // ---- shape and cost -----------------------------------------------------------------------

    private static int TrailerSize(byte[] pdf) =>
        int.Parse(Regex.Match(Text(pdf), @"/Size (\d+)").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(64, 64)]
    [InlineData(65, 67)]
    [InlineData(1_000, 1_016)]
    [InlineData(10_000, 10_160)]
    [InlineData(100_000, 101_588)]
    public void Destinations_addTheObjectsTheTreeShapeNeeds(int count, int added)
    {
        var baseline = TrailerSize(Save(NewDocument(1, out _)));
        var document = NewDocument(1, out var pages);
        for (var i = 0; i < count; i++)
            document.AddNamedDestination($"d{i:D6}", pages[0], 1, 2);
        var pdf = Save(document);
        Assert.Equal(baseline + added, TrailerSize(pdf));
        if (count == 10_000)
        {
            WithTemp(pdf, path =>
            {
                var lines = Pdfinfo(path).Split('\n', StringSplitOptions.TrimEntries).Count(l => Regex.IsMatch(l, @"^1 .*d\d{6}"));
                Assert.Equal(10_000, lines);
                return "";
            });
        }
    }

    private static long AllocatedPerDestination(int count)
    {
        long Run()
        {
            var document = NewDocument(1, out var pages);
            var names = new string[count];
            for (var i = 0; i < count; i++)
                names[i] = $"d{i:D7}";
            var before = GC.GetAllocatedBytesForCurrentThread();
            foreach (var name in names)
                document.AddNamedDestination(name, pages[0], 1, 2);
            using var sink = new MemoryStream();
            document.Save(sink);
            return (GC.GetAllocatedBytesForCurrentThread() - before) / count;
        }

        Run(); // warm the JIT so its allocations are not measured
        return Run();
    }

    [Fact]
    public void AddAndSave_allocationPerDestination_isRoughlyConstantFrom1000To100000()
    {
        var small = AllocatedPerDestination(1_000);
        var mid = AllocatedPerDestination(10_000);
        var large = AllocatedPerDestination(100_000);
        var message = $"bytes per destination: 1,000 -> {small}, 10,000 -> {mid}, 100,000 -> {large}";
        Assert.True(mid <= small * 3 / 2, message);
        Assert.True(large <= small * 3 / 2, message);
    }

    private sealed class CannedRevocation : IRevocationClient
    {
        public RevocationData GetRevocationData(X509Certificate2 certificate, X509Certificate2 issuer)
            => new()
            {
                Ocsp = new byte[] { 0x30, 0x03, 0x0A, 0x01, 0x00 },
                Crl = new byte[] { 0x30, 0x04, 0x02, 0x01, 0x2A },
            };
    }
}
