// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using System.Text.RegularExpressions;
using VellumPdf.Core;
using VellumPdf.Document;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Reader;
using VellumPdf.TestSupport;
using TextRun = VellumPdf.Layout.Elements.TextRun;

namespace VellumPdf.Kernel.Tests;

/// <summary>
/// What the navigation audit (G1) reads back from a saved file: the name tree and number tree
/// walked with a comparison that does not share code with the writer, the outline hierarchy, the
/// link annotations of every page, and the documents the clause tests are run against.
/// </summary>
internal static class ConformanceAuditG1Support
{
    internal static readonly DateTimeOffset Pinned = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);

    internal static readonly string FontsDir = Path.Combine(AppContext.BaseDirectory, "Fonts");

    internal static byte[] DejaVuBytes => File.ReadAllBytes(Path.Combine(FontsDir, "DejaVuSans.ttf"));

    internal static PdfName N(string value) => new(value);

    // ── Opening a file and following references ─────────────────────────────────────────────

    internal sealed class Pdf : IDisposable
    {
        internal Pdf(byte[] bytes)
        {
            Bytes = bytes;
            Reader = PdfReader.Open(bytes);
        }

        internal byte[] Bytes { get; }

        internal PdfDocumentReader Reader { get; }

        internal PdfDictionary Catalog => Reader.Catalog;

        internal PdfObject? Resolve(PdfObject? value) => value is null ? null : Reader.ResolveValue(value);

        internal PdfDictionary Dict(PdfObject? value) => Assert.IsType<PdfDictionary>(Resolve(value));

        internal PdfArray Array(PdfObject? value) => Assert.IsType<PdfArray>(Resolve(value));

        internal PdfDictionary? NameDictionary => Catalog.Get(N("Names")) is { } names ? Dict(names) : null;

        internal PdfObject? Get(PdfDictionary dictionary, string key) => dictionary.Get(N(key));

        /// <summary>The page dictionaries in page-tree order.</summary>
        internal List<PdfDictionary> Pages() =>
            [.. Enumerable.Range(0, Reader.PageCount).Select(i => Reader.GetPage(i).Dictionary)];

        /// <summary>The object number a page dictionary has, by page index.</summary>
        internal int PageObjectNumber(int index) => Reader.GetPage(index).ObjectNumber!.Value;

        public void Dispose() => Reader.Dispose();
    }

    internal static byte[] Save(PdfDocument document)
    {
        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    internal static byte[] Save(VellumPdf.Layout.Document document)
    {
        using var ms = new MemoryStream();
        document.Save(ms);
        return ms.ToArray();
    }

    // ── Name trees and number trees ─────────────────────────────────────────────────────────

    /// <summary>One leaf entry of a tree: the key (bytes, or an integer) and the value as written.</summary>
    internal sealed record Entry(byte[]? NameKey, long NumberKey, PdfObject Value);

    internal sealed record TreeShape(
        List<Entry> Entries,
        int Nodes,
        int Leaves,
        int LeafDepthMin,
        int LeafDepthMax,
        bool RootHasLimits,
        bool RootHasKids,
        bool RootHasEntries,
        int MaxKeysInLeaf,
        int MaxKidsInNode);

    /// <summary>
    /// Walks a name tree (<paramref name="nameTree"/>) or a number tree from its root. Every rule the
    /// walk can check is asserted on the way: one of Kids or the entries array on the root and no
    /// Limits there, Limits on every other node and equal to the first and last key found below it,
    /// kids as indirect references, an even entries array, strictly ascending keys across the whole
    /// walk (so leaf ranges are contiguous and disjoint), and a key of the tree's own type. The key
    /// comparison is a plain loop, not the writer's.
    /// </summary>
    internal static TreeShape Walk(Pdf pdf, PdfDictionary root, bool nameTree)
    {
        var entries = new List<Entry>();
        var leafDepths = new List<int>();
        var nodes = 0;
        var maxKeys = 0;
        var maxKids = 0;
        var entriesKey = N(nameTree ? "Names" : "Nums");

        (Entry First, Entry Last)? Visit(PdfDictionary node, int depth, bool isRoot)
        {
            nodes++;
            var kids = node.Get(N("Kids"));
            var leaf = node.Get(entriesKey);
            Assert.True((kids is null) != (leaf is null), "a node holds Kids or the entries array, never both and never neither");
            Assert.Equal(isRoot, node.Get(N("Limits")) is null);

            (Entry, Entry)? range = null;
            if (leaf is not null)
            {
                leafDepths.Add(depth);
                var array = pdf.Array(leaf);
                Assert.Equal(0, array.Count % 2);
                maxKeys = Math.Max(maxKeys, array.Count / 2);
                for (var i = 0; i < array.Count; i += 2)
                {
                    var entry = nameTree
                        ? new Entry(Assert.IsType<PdfLiteralString>(array[i]).Bytes.ToArray(), 0, array[i + 1])
                        : new Entry(null, Assert.IsType<PdfInteger>(array[i]).Value, array[i + 1]);
                    if (entries.Count > 0)
                        Assert.True(Compare(entries[^1], entry) < 0, "keys ascend strictly across the whole walk");
                    entries.Add(entry);
                    range = (range?.Item1 ?? entry, entry);
                }
            }
            else
            {
                var array = pdf.Array(kids);
                Assert.NotEmpty(array.Items());
                maxKids = Math.Max(maxKids, array.Count);
                for (var i = 0; i < array.Count; i++)
                {
                    Assert.IsType<PdfIndirectReference>(array[i]);
                    var child = Visit(pdf.Dict(array[i]), depth + 1, isRoot: false)!.Value;
                    range = (range?.Item1 ?? child.First, child.Last);
                }
            }

            if (!isRoot)
            {
                var limits = pdf.Array(node.Get(N("Limits")));
                Assert.Equal(2, limits.Count);
                Assert.Equal(0, Compare(Key(limits[0], nameTree), range!.Value.Item1));
                Assert.Equal(0, Compare(Key(limits[1], nameTree), range!.Value.Item2));
            }

            return range;
        }

        Visit(root, 0, isRoot: true);
        return new TreeShape(
            entries, nodes, leafDepths.Count, leafDepths.Min(), leafDepths.Max(),
            root.Get(N("Limits")) is not null, root.Get(N("Kids")) is not null, root.Get(entriesKey) is not null,
            maxKeys, maxKids);
    }

    private static Entry Key(PdfObject key, bool nameTree) => nameTree
        ? new Entry(Assert.IsType<PdfLiteralString>(key).Bytes.ToArray(), 0, key)
        : new Entry(null, Assert.IsType<PdfInteger>(key).Value, key);

    /// <summary>Unsigned bytes one at a time, a prefix before its extension; integers numerically.</summary>
    internal static int Compare(Entry a, Entry b)
    {
        if (a.NameKey is null || b.NameKey is null)
            return a.NumberKey.CompareTo(b.NumberKey);
        for (var i = 0; i < Math.Min(a.NameKey.Length, b.NameKey.Length); i++)
        {
            if (a.NameKey[i] != b.NameKey[i])
                return a.NameKey[i] < b.NameKey[i] ? -1 : 1;
        }

        return a.NameKey.Length.CompareTo(b.NameKey.Length);
    }

    internal static TreeShape DestsTree(Pdf pdf) =>
        Walk(pdf, pdf.Dict(pdf.Dict(pdf.Catalog.Get(N("Names"))).Get(N("Dests"))), nameTree: true);

    internal static TreeShape LabelsTree(Pdf pdf) =>
        Walk(pdf, pdf.Dict(pdf.Catalog.Get(N("PageLabels"))), nameTree: false);

    internal static IEnumerable<PdfObject> Items(this PdfArray array)
    {
        for (var i = 0; i < array.Count; i++)
            yield return array[i];
    }

    // ── A page label evaluated from the file, by the clause's own rules ───────────────────────

    /// <summary>
    /// The label of every page, evaluated from the number tree alone: the range a page falls in is
    /// the one with the greatest key not above its index, the numeric portion counts from the range's
    /// St (default 1), and the style is read from S. Written from ISO 32000-1, Table 159, not from
    /// the library's formatter.
    /// </summary>
    internal static string[] EvaluateLabels(Pdf pdf, int pageCount)
    {
        var tree = LabelsTree(pdf);
        var labels = new string[pageCount];
        for (var page = 0; page < pageCount; page++)
        {
            var entry = tree.Entries.Last(e => e.NumberKey <= page);
            var dict = pdf.Dict(entry.Value);
            long start = dict.Get(N("St")) is PdfInteger st ? st.Value : 1;
            var number = start + (page - entry.NumberKey);
            var style = dict.Get(N("S")) is PdfName s ? s.Value : null;
            var prefix = dict.Get(N("P")) is { } p ? TextString(p) : "";
            labels[page] = prefix + style switch
            {
                null => "",
                "D" => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "R" => Roman(number),
                "r" => Roman(number).ToLowerInvariant(),
                "A" => new string((char)('A' + (int)((number - 1) % 26)), (int)((number - 1) / 26 + 1)),
                "a" => new string((char)('a' + (int)((number - 1) % 26)), (int)((number - 1) / 26 + 1)),
                _ => throw new InvalidOperationException("S is not one of D, R, r, A, a: " + style),
            };
        }

        return labels;
    }

    private static string Roman(long number)
    {
        var sb = new StringBuilder();
        (int, string)[] table = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"), (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        foreach (var (value, text) in table)
        {
            while (number >= value)
            {
                sb.Append(text);
                number -= value;
            }
        }

        return sb.ToString();
    }

    /// <summary>A text string (ISO 32000-1, 7.9.2.2): UTF-16BE behind FE FF, or PDFDocEncoding.</summary>
    internal static string TextString(PdfObject value)
    {
        var bytes = Assert.IsType<PdfLiteralString>(value).Bytes.ToArray();
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        Assert.All(bytes, b => Assert.True(b < 0x80, "a text string with a byte above 0x7F must be UTF-16BE here"));
        return Encoding.ASCII.GetString(bytes);
    }

    // ── Outline ─────────────────────────────────────────────────────────────────────────────

    internal sealed record OutlineItem(
        int Number, string Title, int Depth, PdfDictionary Dict, int Count, bool HasCount, List<OutlineItem> Children);

    /// <summary>
    /// Walks the outline from the catalog by First and Next only, asserting the linked-list rules of
    /// Table 152 and Table 153 on the way: Parent is the containing item or the outline dictionary,
    /// Prev and Next are the neighbours and reciprocal, First and Last are the ends of the child
    /// list, and every one of those is an indirect reference.
    /// </summary>
    internal static (PdfDictionary Root, List<OutlineItem> Top) ReadOutline(Pdf pdf)
    {
        var rootRef = Assert.IsType<PdfIndirectReference>(pdf.Catalog.Get(N("Outlines")));
        var root = pdf.Dict(rootRef);
        var top = new List<OutlineItem>();
        Children(root, rootRef, 0, top);
        return (root, top);

        void Children(PdfDictionary parent, PdfIndirectReference parentRef, int depth, List<OutlineItem> into)
        {
            var first = parent.Get(N("First"));
            var last = parent.Get(N("Last"));
            if (first is null)
            {
                Assert.Null(last);
                return;
            }

            Assert.IsType<PdfIndirectReference>(first);
            Assert.IsType<PdfIndirectReference>(last);
            PdfIndirectReference? previous = null;
            var current = (PdfIndirectReference?)first;
            PdfIndirectReference? seenLast = null;
            while (current is not null)
            {
                var dict = pdf.Dict(current);
                Assert.Equal(parentRef, Assert.IsType<PdfIndirectReference>(dict.Get(N("Parent"))));
                if (previous is null)
                    Assert.Null(dict.Get(N("Prev")));
                else
                    Assert.Equal(previous, Assert.IsType<PdfIndirectReference>(dict.Get(N("Prev"))));
                var item = new OutlineItem(
                    current.ObjectNumber, TextString(dict.Get(N("Title"))!), depth, dict,
                    dict.Get(N("Count")) is PdfInteger c ? (int)c.Value : 0, dict.Get(N("Count")) is not null, []);
                Children(dict, current, depth + 1, item.Children);
                into.Add(item);
                seenLast = current;
                previous = current;
                current = dict.Get(N("Next")) is { } next ? Assert.IsType<PdfIndirectReference>(next) : null;
            }

            Assert.Equal(last, seenLast);
        }
    }

    internal static IEnumerable<OutlineItem> Flatten(IEnumerable<OutlineItem> items)
    {
        foreach (var item in items)
        {
            yield return item;
            foreach (var child in Flatten(item.Children))
                yield return child;
        }
    }

    // ── Link annotations ────────────────────────────────────────────────────────────────────

    internal sealed record Link(int PageIndex, int ObjectNumber, PdfDictionary Dict);

    /// <summary>Every annotation of every page, with the page it was found on.</summary>
    internal static List<Link> AnnotationsOf(Pdf pdf)
    {
        var found = new List<Link>();
        var pages = pdf.Pages();
        for (var i = 0; i < pages.Count; i++)
        {
            if (pages[i].Get(PdfName.Annots) is not { } annots)
                continue;
            foreach (var item in pdf.Array(annots).Items())
                found.Add(new Link(i, Assert.IsType<PdfIndirectReference>(item).ObjectNumber, pdf.Dict(item)));
        }

        return found;
    }

    internal static string? Subtype(PdfDictionary dict) => dict.Get(PdfName.Subtype) is PdfName n ? n.Value : null;

    internal static double Number(PdfObject value) => value switch
    {
        PdfInteger i => i.Value,
        PdfReal r => r.Value,
        _ => throw new InvalidOperationException("not a number: " + value),
    };

    // ── Oracles ─────────────────────────────────────────────────────────────────────────────

    internal sealed class TempDir : IDisposable
    {
        // Not CreateTempSubdirectory (mode 0700): the Docker veraPDF on CI runs as another user.
        private readonly string _root = Directory.CreateDirectory(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vellum-g1-" + Guid.NewGuid().ToString("N"))).FullName;

        public string Path(string name) => System.IO.Path.Combine(_root, name);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { /* best-effort cleanup */ }
            catch (UnauthorizedAccessException) { /* best-effort cleanup */ }
        }
    }

    /// <summary>The failed rules of a veraPDF run as <c>clause-testNumber</c>, in report order.</summary>
    internal static List<string> VeraPdfFailures(string flavour, byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        ExternalTool.TryRun("verapdf", ["--flavour", flavour, path],
            out var exit, out var report, out var stderr, out var timedOut, timeoutMs: 120_000);
        Assert.False(timedOut, $"veraPDF timed out.\nstderr:\n{stderr}");
        Assert.True(exit is 0 or 1, $"veraPDF exited {exit}: {VeraPdfExitCode.Describe(exit)}\n{report}\n{stderr}");
        // The run must have validated against the profile asked for, or an empty list proves nothing.
        Assert.Contains("isCompliant=", report, StringComparison.Ordinal);
        return [.. Regex.Matches(report, "<rule specification=\"[^\"]+\" clause=\"([^\"]+)\" testNumber=\"(\\d+)\" status=\"failed\"")
            .Select(m => $"{m.Groups[1].Value}-{m.Groups[2].Value}")];
    }

    /// <summary>Runs <c>qpdf --check</c> and asserts the report names no error.</summary>
    internal static void QpdfChecks(byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        if (!ExternalTool.TryRun("qpdf", ["--check", path], out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("qpdf");
        Assert.False(timedOut);
        Assert.True(exit == 0, $"qpdf --check exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("No syntax or stream encoding errors found", stdout, StringComparison.Ordinal);
    }

    /// <summary>
    /// The text pdftotext extracts, whitespace collapsed to single spaces: of the whole file, or of
    /// the one page <paramref name="page"/> (counted from 0).
    /// </summary>
    internal static string PopplerText(byte[] pdf, int? page = null)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        string[] range = page is { } p ? ["-f", (p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "-l", (p + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)] : [];
        if (!ExternalTool.TryRun("pdftotext", [.. range, "-layout", path, "-"], out var exit, out var text, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdftotext");
        Assert.True(exit == 0 && !timedOut, $"pdftotext exit {exit}, stderr: {stderr}");
        return Regex.Replace(text, @"\s+", " ");
    }

    /// <summary>The page number poppler resolves each named destination to, from <c>pdfinfo -dests</c>.</summary>
    internal static Dictionary<string, int> PopplerDestinations(byte[] pdf)
    {
        using var dir = new TempDir();
        var path = dir.Path("subject.pdf");
        File.WriteAllBytes(path, pdf);
        if (!ExternalTool.TryRun("pdfinfo", ["-dests", path], out var exit, out var stdout, out var stderr, out var timedOut))
            OracleGate.Unavailable("pdfinfo");
        Assert.True(exit == 0 && !timedOut, $"pdfinfo -dests exit {exit}\n{stdout}\n{stderr}");
        return Regex.Matches(stdout, @"^\s*(?<page>\d+)\s+\[[^\]]*\]\s+""(?<name>[^""]+)""", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups["name"].Value, m => int.Parse(m.Groups["page"].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    // ── Documents ───────────────────────────────────────────────────────────────────────────

    internal static VellumPdf.Layout.Document NewLayoutDocument(PdfConformance conformance)
    {
        var doc = new VellumPdf.Layout.Document
        {
            Conformance = conformance,
            Timestamp = Pinned,
            DocumentId = new byte[16],
            PageSize = new PdfRectangle(0, 0, 400, 500),
            Margins = new EdgeInsets(40),
        };
        doc.Info.Title = "Navigation audit";
        doc.Language = "en-US";
        if (conformance is PdfConformance.PdfA2u or PdfConformance.PdfA2b or PdfConformance.PdfA2a)
            doc.SetPdfAOutputIntent(IccProfiles.Srgb, 3, "sRGB");
        return doc;
    }

    /// <summary>A 2 x 2 24-bit BMP, which needs no checksum to build.</summary>
    internal static byte[] TinyBmp()
    {
        byte[] row = [0, 0, 255, 0, 255, 0, 0, 0]; // two BGR pixels and two bytes of padding
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B');
        w.Write((byte)'M');
        w.Write(14 + 40 + row.Length * 2);
        w.Write(0);
        w.Write(14 + 40);
        w.Write(40);
        w.Write(2);
        w.Write(2);
        w.Write((short)1);
        w.Write((short)24);
        w.Write(0);
        w.Write(row.Length * 2);
        w.Write(2835);
        w.Write(2835);
        w.Write(0);
        w.Write(0);
        w.Write(row);
        w.Write(row);
        return ms.ToArray();
    }

    internal sealed record Parts(VellumPdf.Layout.Document Doc, FontReference Font);

    /// <summary>
    /// The G1 fixture: roman front matter with a contents list of page references and links, a
    /// decimal body, an appendix range with a prefix and a start, an anchor on every element kind that
    /// takes one, a link by URI and by anchor from a paragraph, a list item, a heading, a table cell
    /// and both bands, a justified link that wraps lines and a page, a heading that splits across a
    /// page, and a closed heading with children.
    /// </summary>
    internal static Parts NavigationDocument(PdfConformance conformance, bool withLinks = true)
    {
        var doc = NewLayoutDocument(conformance);
        var font = doc.UseTrueTypeFont(DejaVuBytes);
        TextStyle Plain() => new() { FontRef = font, FontSize = 11 };
        TextStyle ToUri(string uri) => new() { FontRef = font, FontSize = 11, LinkUri = uri };
        TextStyle ToAnchor(string name) => new() { FontRef = font, FontSize = 11, LinkAnchor = name };
        TextRun Link(string text, string anchor) => new(text, withLinks ? ToAnchor(anchor) : Plain());
        TextRun UriLink(string text, string uri) => new(text, withLinks ? ToUri(uri) : Plain());
        // A page reference is always a link to the anchor, which a PDF/UA-1 claim refuses.
        TextRun PageRef(string anchor, TextStyle style) => withLinks ? TextRun.PageReference(anchor, style) : new TextRun("0", style);

        doc.PageLabel = new PageLabelRange(PdfPageLabelStyle.LowerRoman);
        if (withLinks)
        {
            doc.Header = new RunningBand("Back to the contents", new TextStyle { FontRef = font, FontSize = 9, LinkAnchor = "toc" }, HorizontalAlignment.Left);
            doc.Footer = new RunningBand("{page}", new TextStyle { FontRef = font, FontSize = 9, LinkUri = "https://example.com/footer" });
        }

        doc.Add(new Heading([new TextRun("Contents", Plain())]) { Anchor = "toc", Level = 0 });
        var toc = new ListElement { DefaultStyle = Plain(), Anchor = "toclist" };
        toc.Add(new ListItem([Link("Chapter one", "ch1"), new TextRun(", page ", Plain()), PageRef("ch1", Plain())]) { Anchor = "tocitem" });
        toc.Add(new ListItem([Link("The long link", "longlink"), new TextRun(", page ", Plain()), PageRef("longlink", Plain())]));
        toc.Add(new ListItem([Link("Appendix", "appendix"), new TextRun(", page ", Plain()), PageRef("appendix", Plain())]));
        doc.Add(toc);
        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal) });

        doc.Add(new Heading([new TextRun("Chapter one", Plain())]) { Anchor = "ch1", Level = 0 });
        doc.Add(new Paragraph([
            new TextRun("A link by anchor ", Plain()), Link("back to the contents", "toc"),
            new TextRun(" and one by URI ", Plain()), UriLink("to a site", "https://example.com/a?x=1&y=2"),
            new TextRun(".", Plain()),
        ])
        { Anchor = "para" });
        doc.Add(new Heading([new TextRun("A section whose closed bookmark holds two children", Plain())]) { Level = 1, BookmarkExpanded = false, Anchor = "closed" });
        doc.Add(new Heading([new TextRun("Child of the closed section", Plain())]) { Level = 2 });
        doc.Add(new Heading([new TextRun("Another child", Plain())]) { Level = 2 });
        doc.Add(new Heading([new TextRun("Linking heading: ", Plain()), Link("see the table", "table")]) { Level = 1 });

        var table = new TableElement { DefaultCellStyle = Plain(), Anchor = "table" }.SetColumnWidths(150, 150);
        var row = table.AddRow();
        row.AddCell(new Cell("cell with a URI link") { Style = withLinks ? ToUri("https://example.com/cell") : Plain() });
        row.AddCell(new Cell([new TextRun("cell with a page reference, page ", Plain()), PageRef("image", Plain()), new TextRun(" and ", Plain()), Link("an anchor link", "pre")]));
        doc.Add(table);
        doc.Add(new NoteBlock { Anchor = "note" }.Add("A note block with an anchor.", Plain()));
        doc.Add(new Preformatted("preformatted\n  text", new TextStyle { FontRef = font, FontSize = 10 }) { Anchor = "pre" });
        doc.Add(new LayoutImage(VellumPdf.Images.BmpImageLoader.Load(TinyBmp())) { Width = 40, Anchor = "image", AltText = "A small image" });

        // A justified link that wraps over several lines and runs on to the next page.
        var long_ = string.Join(' ', Enumerable.Repeat("a link whose words wrap and justify across lines and pages", 24));
        doc.Add(new Heading([new TextRun("The long link", Plain())]) { Level = 1, Anchor = "longlink" });
        doc.Add(new Paragraph([UriLink(long_, "https://example.com/long")]) { Alignment = HorizontalAlignment.Justify });

        doc.Add(new PageBreak { PageLabel = new PageLabelRange(PdfPageLabelStyle.Decimal, "A-", 8) });
        doc.Add(new Heading([new TextRun("Appendix", Plain())]) { Anchor = "appendix", Level = 0 });
        doc.Add(new Paragraph([new TextRun("Back to ", Plain()), Link("chapter one", "ch1"), new TextRun(", which is on page ", Plain()), PageRef("ch1", Plain())]));
        return new Parts(doc, font);
    }
}
