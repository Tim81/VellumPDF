// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using System.IO.Compression;
using System.Reflection;
using System.Text;
using VellumPdf.Document;
using VellumPdf.Images;
using VellumPdf.Layout;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Reader;
using TextRun = VellumPdf.Layout.Elements.TextRun;
using LayoutDocument = VellumPdf.Layout.Document;

namespace VellumPdf.Conformance.Tests.StructureDump;

/// <summary>
/// Records what the Layout writer emits today for a tagged PDF/UA-1 document, as structure dumps
/// (<see cref="StructureDumper"/>). Each golden is wrong on purpose where its header says so: the
/// <c>known defects</c> line names the audit findings the output still shows, and the fix for one of
/// them changes the golden in the same pull request, so the diff is the proof.
/// </summary>
/// <remarks>
/// <b>Do not "fix" a golden by accepting a new one without reading it.</b> A change that looks
/// like an improvement can also be a regression the header does not name. Every document here sets
/// <c>Conformance = PdfUA1</c>, a title, <c>Language = "nl-NL"</c>, an A4 page and an explicit
/// <see cref="TextStyle"/> on every element, because an element left on the default style takes the
/// default Helvetica, which the embedded-font rule of PDF/UA rejects. The "emphasis" runs in case 6
/// differ from their neighbours by colour, because only the regular DejaVu Sans is embedded in this
/// test project; what the case pins is that the writer splits a word where the style changes, not how
/// it draws bold.
/// </remarks>
public sealed class LayoutStructureGoldenTests
{
    private static readonly byte[] FontBytes = LoadFont();

    private static byte[] LoadFont()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("DejaVuSans.ttf")
            ?? throw new InvalidOperationException("DejaVuSans.ttf is not embedded in the test assembly.");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }

    private static readonly ColorRgb Red = new(0.8, 0, 0);

    private static (byte[] Pdf, int Pages) Build(Action<LayoutDocument, TextStyle> fill)
    {
        using var doc = new LayoutDocument
        {
            Conformance = VellumPdf.Document.PdfConformance.PdfUA1,
            Language = "nl-NL",
            PageSize = PageSize.A4,
        };
        doc.Info.Title = "Structuurdump";
        var font = doc.UseTrueTypeFont(FontBytes);
        fill(doc, new TextStyle { FontRef = font, FontSize = 24 });

        using var ms = new MemoryStream();
        doc.Save(ms);
        var pdf = ms.ToArray();
        using var reader = PdfReader.Open(pdf);
        return (pdf, reader.PageCount);
    }

    private static string DumpOf(int expectedPages, string[] knownDefects, Action<LayoutDocument, TextStyle> fill)
    {
        var (pdf, pages) = Build(fill);
        Assert.Equal(expectedPages, pages);
        var first = StructureDumper.Dump(pdf, knownDefects);
        Assert.Equal(first, StructureDumper.Dump(Build(fill).Pdf, knownDefects));
        return first;
    }

    // ── 1. Paragraph split over a page break, with one word longer than the line ──────────────

    [Fact]
    public async Task Case1_paragraphSplitOverPageBreak_withOverlongWord()
    {
        var dump = DumpOf(2, ["A11Y-04", "A11Y-17"], (doc, style) =>
        {
            // The overlong word is its own "sentence" so it sits between two whole words.
            var words = Enumerable.Range(1, 36).Select(i => $"Zin {i} van de alinea.").ToList();
            words.Insert(12, "Superlang" + new string('W', 40));
            doc.Add(new Paragraph(string.Join(' ', words), style));
        });

        await Verify(dump);
    }

    // ── 2. Table over three pages, one header row, a border ───────────────────────────────────

    [Fact]
    public async Task Case2_tableOverThreePages_withHeaderRow()
    {
        var dump = DumpOf(3, ["A11Y-04", "A11Y-05"], (doc, style) =>
        {
            var cellStyle = new TextStyle { FontRef = style.FontRef, FontSize = 14 };
            var padding = new EdgeInsets(50, 10);
            var table = new TableElement { BorderWidth = 1, BorderColor = ColorRgb.Black, DefaultCellStyle = cellStyle };
            table.SetColumnWidths(150, 150, 150);

            var header = table.AddHeaderRow();
            foreach (var h in new[] { "Naam", "Rol", "Afdeling" })
                header.AddCell(new Cell(h) { Style = cellStyle, Padding = padding });

            for (var r = 1; r <= 9; r++)
            {
                var row = table.AddRow();
                foreach (var c in new[] { $"Naam {r}", $"Rol {r}", $"Afdeling {r}" })
                    row.AddCell(new Cell(c) { Style = cellStyle, Padding = padding });
            }

            doc.Add(table);
        });

        await Verify(dump);
    }

    // ── 3. Ordered list with a nested child list, split over a page break ─────────────────────

    [Fact]
    public async Task Case3_orderedListWithNestedList_splitOverPageBreak()
    {
        var dump = DumpOf(2, ["A11Y-06", "A11Y-09", "A11Y-15"], (doc, style) =>
        {
            var big = new TextStyle { FontRef = style.FontRef, FontSize = 48 };
            var list = new ListElement(ListStyle.OrderedDecimal) { DefaultStyle = big };
            for (var i = 1; i <= 5; i++)
            {
                var item = new ListItem($"Punt {i}", big);
                item.AddChild($"Sub {i}.1", big);
                item.AddChild($"Sub {i}.2", big);
                list.Add(item);
            }

            doc.Add(list);
        });

        await Verify(dump);
    }

    // ── 4. Header and footer bands with {page} and {pages} ────────────────────────────────────

    [Fact]
    public async Task Case4_headerAndFooterBands()
    {
        var dump = DumpOf(2, ["A11Y-02"], (doc, style) =>
        {
            var band = new TextStyle { FontRef = style.FontRef, FontSize = 10 };
            doc.SetHeader("Kop {page}", band);
            doc.SetFooter("Pagina {page} van {pages}", band);
            var sentences = string.Join(' ', Enumerable.Range(1, 40).Select(i => $"Zin {i} van de tekst."));
            doc.Add(new Paragraph(sentences, style));
        });

        await Verify(dump);
    }

    // ── 5. Two images, one with alternative text and one without ──────────────────────────────

    [Fact]
    public async Task Case5_twoImages_oneWithAltText()
    {
        var dump = DumpOf(1, ["A11Y-07"], (doc, style) =>
        {
            doc.Add(new LayoutImage(PngImageLoader.Load(TwoByTwoPng())) { Width = 100, Height = 100, AltText = "Een gradient" });
            doc.Add(new LayoutImage(PngImageLoader.Load(TwoByTwoPng())) { Width = 100, Height = 100 });
        });

        await Verify(dump);
    }

    // ── 6. The audit's own runs: a word split across styles, and a link mid-sentence ──────────

    [Fact]
    public async Task Case6_runsSplitInsideWords_andALinkMidSentence()
    {
        var dump = DumpOf(1, ["A11Y-01", "BUG-01"], (doc, style) =>
        {
            var emphasis = new TextStyle { FontRef = style.FontRef, FontSize = style.FontSize, Color = Red };
            var link = new TextStyle { FontRef = style.FontRef, FontSize = style.FontSize, LinkUri = "https://example.org/doc" };
            doc.Add(new Paragraph(
            [
                new TextRun("Open ", style),
                new TextRun("Instellingen", emphasis),
                new TextRun(". Daarna astro", style),
                new TextRun("logie", emphasis),
                new TextRun(" en (", style),
                new TextRun("vet", emphasis),
                new TextRun(").", style),
            ]));
            doc.Add(new Paragraph(
            [
                new TextRun("Lees de ", style),
                new TextRun("documentatie", link),
                new TextRun(" voor details.", style),
            ]));
        });

        await Verify(dump);
    }

    // ── 7. Headings at levels 0..6 ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Case7_headingsAtLevelsZeroToSix()
    {
        var dump = DumpOf(1, ["A11Y-14"], (doc, style) =>
        {
            var small = new TextStyle { FontRef = style.FontRef, FontSize = 12 };
            for (var level = 0; level <= 6; level++)
                doc.Add(new Heading($"Kop {level}", small) { Level = level });
        });

        await Verify(dump);
    }

    // ── 8. A table with an empty cell ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Case8_tableWithAnEmptyCell()
    {
        var dump = DumpOf(1, ["A11Y-18"], (doc, style) =>
        {
            var cellStyle = new TextStyle { FontRef = style.FontRef, FontSize = 14 };
            var table = new TableElement { BorderWidth = 1, BorderColor = ColorRgb.Black, DefaultCellStyle = cellStyle };
            table.SetColumnWidths(150, 150);
            table.AddHeaderRow()
                .AddCell(new Cell("Naam") { Style = cellStyle })
                .AddCell(new Cell("Opmerking") { Style = cellStyle });
            table.AddRow()
                .AddCell(new Cell("Jan") { Style = cellStyle })
                .AddCell(new Cell("") { Style = cellStyle });
            doc.Add(table);
        });

        await Verify(dump);
    }

    // ── 9. Language overrides inside an nl-NL document ────────────────────────────────────────

    [Fact]
    public async Task Case9_languageOverrides()
    {
        var dump = DumpOf(1, ["A11Y-08"], (doc, style) =>
        {
            doc.Add(new Paragraph("Dit is Nederlands.", style));
            doc.Add(new Paragraph("This is English.", style) { Language = "en" });
            var list = new ListElement(ListStyle.Unordered) { DefaultStyle = style };
            list.Add(new ListItem("Een Nederlands punt", style));
            list.Add(new ListItem("Un point en francais", style) { Language = "fr" });
            doc.Add(list);
        });

        await Verify(dump);
    }

    // A 2x2 opaque RGB PNG, built here so the case needs no fixture file.
    private static byte[] TwoByTwoPng()
    {
        static uint Crc(byte[] d)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in d)
            {
                c ^= b;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            return ~c;
        }

        static void Chunk(Stream m, string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length);
            Array.Reverse(len);
            m.Write(len);
            var body = Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            m.Write(body);
            var crc = BitConverter.GetBytes(Crc(body));
            Array.Reverse(crc);
            m.Write(crc);
        }

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(ms, "IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        var raw = new byte[2 * (1 + (2 * 3))];
        for (var y = 0; y < 2; y++)
        {
            for (var x = 0; x < 6; x++)
                raw[(y * 7) + 1 + x] = (byte)(x * 40);
        }

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Optimal, true))
            z.Write(raw);
        Chunk(ms, "IDAT", deflated.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }
}
