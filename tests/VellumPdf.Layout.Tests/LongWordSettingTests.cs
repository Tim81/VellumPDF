// Copyright © Timothy van der Ham (@Tim81)
// SPDX-License-Identifier: Apache-2.0

using VellumPdf.Document;
using VellumPdf.Fonts;
using VellumPdf.Layout.Core;
using VellumPdf.Layout.Elements;
using VellumPdf.Layout.Elements.Table;
using VellumPdf.Layout.Rendering;
using VellumPdf.Layout.Rendering.Table;
using VellumPdf.TestSupport;

namespace VellumPdf.Layout.Tests;

/// <summary>
/// <see cref="Document.BreakLongWordsAtPlaces"/> (#594): off, every wrap cuts a word at the glyph
/// as 2.3.3 did; on, every wrap the document performs cuts after a place. The fixtures are
/// Courier at 10 pt, a 60 pt line of ten characters, and the word <c>aa/bb/cccccccccc</c>, which a
/// cut at the glyph ends after <c>aa/bb/cccc</c> and a cut after a place after <c>aa/bb/</c>.
/// </summary>
public sealed class LongWordSettingTests
{
    private const string Path = "aa/bb/cccccccccc";

    private static readonly string[] AtTheGlyph = ["aa/bb/cccc", "cccccc"];
    private static readonly string[] AtThePlace = ["aa/bb/", "cccccccccc"];

    private static TextStyle Courier() => new() { Font = Standard14.Courier, FontSize = 10 };

    public enum Path_
    {
        Paragraph,
        Heading,
        ListItem,
        NoteBlock,
        CellString,
        CellRuns,
        ForwardedParagraph,
    }

    // A document that holds one element of the kind, on a page whose content area gives the element
    // a 60 pt line (a list entry and a note block lose their indent and padding to it).
    private static Document Build(Path_ kind, bool on)
    {
        var doc = new Document
        {
            BreakLongWordsAtPlaces = on,
            PageSize = kind == Path_.CellString || kind == Path_.CellRuns
                ? new PdfRectangle(0, 0, 112, 300)
                : new PdfRectangle(0, 0, 100, 300),
            Margins = new EdgeInsets(20),
        };
        switch (kind)
        {
            case Path_.Paragraph:
                doc.Add(new Paragraph(Path, Courier()));
                break;
            case Path_.Heading:
                doc.Add(new Heading(Path, Courier()));
                break;
            case Path_.ListItem:
                doc.PageSize = new PdfRectangle(0, 0, 120, 300);
                doc.Add(new ListElement { DefaultStyle = Courier(), Indent = 20 }.Add(Path));
                break;
            case Path_.NoteBlock:
                doc.Add(new NoteBlock { Padding = new EdgeInsets(0), BorderWidth = 0 }.Add(Path, Courier()));
                break;
            case Path_.CellString:
                {
                    var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
                    table.AddRow().AddCell(Path);
                    doc.Add(table);
                    break;
                }

            case Path_.CellRuns:
                {
                    var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
                    table.AddRow().AddCell(new Cell([new TextRun(Path, Courier())]));
                    doc.Add(table);
                    break;
                }

            case Path_.ForwardedParagraph:
                doc.Add(new Forward(new ParagraphRenderer(new Paragraph(Path, Courier()))));
                break;
        }

        return doc;
    }

    private static string[] Shown(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
        var all = ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))
            .Where(p => p.Text.Length > 0).Select(p => p.Text).ToArray();
        return all.Length > 2 ? all[^2..] : all;
    }

    [Fact]
    public void TheSettingIsOffByDefault()
    {
        using var doc = new Document();

        Assert.False(doc.BreakLongWordsAtPlaces);
    }

    [Theory]
    [InlineData(Path_.Paragraph)]
    [InlineData(Path_.Heading)]
    [InlineData(Path_.ListItem)]
    [InlineData(Path_.NoteBlock)]
    [InlineData(Path_.CellString)]
    [InlineData(Path_.CellRuns)]
    [InlineData(Path_.ForwardedParagraph)]
    public void WithTheSettingOff_everyWrapCutsAtTheGlyph(Path_ kind)
    {
        using var doc = Build(kind, on: false);

        Assert.Equal(AtTheGlyph, Shown(doc));
    }

    [Theory]
    [InlineData(Path_.Paragraph)]
    [InlineData(Path_.Heading)]
    [InlineData(Path_.ListItem)]
    [InlineData(Path_.NoteBlock)]
    [InlineData(Path_.CellString)]
    [InlineData(Path_.CellRuns)]
    [InlineData(Path_.ForwardedParagraph)]
    public void WithTheSettingOn_everyWrapCutsAfterThePlace(Path_ kind)
    {
        using var doc = Build(kind, on: true);

        Assert.Equal(AtThePlace, Shown(doc));
    }

    [Fact]
    public void TheStaticWraps_takeNoValueAndCutAtTheGlyph()
    {
        // The static wraps take no context, so no value reaches them: they cut at the glyph as main
        // does, whatever a document elsewhere says.
        using var doc = new Document { BreakLongWordsAtPlaces = true };

        var (lines, _) = ParagraphRenderer.WordWrap([new TextRun(Path, Courier())], 60, out _);

        Assert.Equal(AtTheGlyph, lines.Select(l => string.Concat(l.Select(f => f.Text))));
    }

    // ── What turning it on can refuse ────────────────────────────────────────

    private static void SaveTo(Document doc)
    {
        using var ms = new MemoryStream();
        doc.Save(ms);
    }

    // The word is 60 characters, ten segments of six. A line holds ten, so a cut at the glyph takes
    // six lines and a cut after a solidus ten, and a page holds one line.
    private static Document ManyAddresses(bool on, int words)
    {
        var doc = new Document
        {
            BreakLongWordsAtPlaces = on,
            PageSize = new PdfRectangle(0, 0, 80, 37),
            Margins = new EdgeInsets(10),
        };
        var word = string.Concat(Enumerable.Repeat("aaaaa/", 10));
        doc.Add(new Paragraph(string.Join(' ', Enumerable.Repeat(word, words)), Courier()));
        return doc;
    }

    [Fact]
    public void WithTheSettingOn_aParagraphThatTakesMoreThanTheLimitIsRefused_andSavesWithItOff()
    {
        // 6,000 words: 36,000 pages cut at the glyph, 60,000 cut after the solidus.
        using var off = ManyAddresses(on: false, 6000);
        SaveTo(off);

        using var on = ManyAddresses(on: true, 6000);
        var ex = Assert.Throws<InvalidOperationException>(() => SaveTo(on));

        Assert.Equal(
            "A single element needed more than 50000 page continuations, so its layout is not converging. "
            + "Either an overflow renderer never advances, or the element genuinely spans more pages than one "
            + "element may and should be split into several, or its height sits within a rounding error of the "
            + "content area, so pagination refuses and accepts it by turns.",
            ex.Message);
    }

    // The header is one cell holding four segments of six characters: three lines cut at the glyph,
    // four cut after the solidus. On a 108 pt page the three fit with one line of the row below them,
    // and the four do not.
    private static Document HeaderAndARow(bool on)
    {
        var doc = new Document
        {
            BreakLongWordsAtPlaces = on,
            PageSize = new PdfRectangle(0, 0, 112, 108),
            Margins = new EdgeInsets(20),
        };
        var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
        table.AddHeaderRow().AddCell("aaaaa/aaaaa/aaaaa/aaaaa/");
        table.AddRow().AddCell("x");
        doc.Add(table);
        return doc;
    }

    [Fact]
    public void WithTheSettingOn_aHeaderRowThatNoLongerFitsIsRefusedAsTooTall_andSavesWithItOff()
    {
        using var off = HeaderAndARow(on: false);
        SaveTo(off);

        using var on = HeaderAndARow(on: true);
        var ex = Assert.Throws<InvalidOperationException>(() => SaveTo(on));

        Assert.Equal(
            "An element is too tall to fit on a single page and cannot be rendered. Reduce the element's content "
            + "or increase the page size. Table row 1, cell 0 needs 20.0pt for its vertical padding and one line, "
            + "but a page leaves 12.0pt under the 56.0pt of repeated header rows, so the row cannot be split "
            + "between pages.",
            ex.Message);
    }

    [Fact]
    public void ATabbedParagraphCutsAfterThePlaceWhenOn_andAtTheGlyphWhenOff()
    {
        // A paragraph with tab stops wraps through its own routine, which cuts a word the same way.
        static string[] Lines(bool on)
        {
            using var doc = new Document { BreakLongWordsAtPlaces = on, PageSize = new PdfRectangle(0, 0, 100, 300), Margins = new EdgeInsets(20) };
            doc.Add(new Paragraph(Path + "\tx", Courier()) { TabStops = [new TabStop(40)] });
            using var ms = new MemoryStream();
            doc.Save(ms);
            return [.. ContentStreamReadback.TextPlacements(PdfTestUtil.DecompressAllFlatStreams(ms.ToArray()))
                .Where(p => p.Text.Length > 0).Select(p => p.Text)];
        }

        Assert.Contains("aa/bb/", Lines(on: true));
        Assert.DoesNotContain("aa/bb/", Lines(on: false));
        Assert.Contains("aa/bb/cccc", Lines(on: false));
    }

    [Fact]
    public void ACellOfRunsHoldingACharacterOutsideTheBasicPlane_isCutAtTheOffsetsOfItsCharacters()
    {
        // The cut is found over characters and mapped back to offsets in the text, and a character
        // outside the basic plane takes two UTF-16 units, so counting units one by one misplaces the
        // lines that follow it. The string wrap and the run wrap have to agree.
        var style = Courier();
        var text = "aa/😀😀/bbbbbbbbbbbbbbb";
        var byString = TableRenderer.WordWrapLines(text, style, 60, honourBreaks: true, atPlaces: true);
        var byRuns = new Cell([new TextRun(text, style)]).RunText!.Lines(60, honourBreaks: true, atPlaces: true);

        Assert.Equal(byString, byRuns);
        Assert.Equal(string.Concat(byString), text);
        Assert.True(byString.Count > 1);
    }

    [Fact]
    public void WithTheSettingOn_aRowSpanGroupThatNoLongerFitsIsRefusedAsTooTall_andSavesWithItOff()
    {
        static Document Build(bool on)
        {
            var doc = new Document { BreakLongWordsAtPlaces = on, PageSize = new PdfRectangle(0, 0, 112, 120), Margins = new EdgeInsets(20) };
            var table = new TableElement { DefaultCellStyle = Courier() }.SetColumnWidths(72);
            table.AddRow().AddCell(new Cell(string.Concat(Enumerable.Repeat("aaaaa/", 10))) { RowSpan = 2 });
            table.AddRow();
            doc.Add(table);
            return doc;
        }

        using var off = Build(on: false);
        SaveTo(off);

        using var on = Build(on: true);
        var ex = Assert.Throws<InvalidOperationException>(() => SaveTo(on));

        Assert.StartsWith("An element is too tall to fit on a single page and cannot be rendered.", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoSavesInARow_eachReadItsOwnDocumentsValue()
    {
        using (var on = Build(Path_.Paragraph, on: true)) Assert.Equal(AtThePlace, Shown(on));

        using (var off = Build(Path_.Paragraph, on: false)) Assert.Equal(AtTheGlyph, Shown(off));

        using var refused = ManyAddresses(on: true, 6000);
        Assert.Throws<InvalidOperationException>(() => SaveTo(refused));

        using var after = Build(Path_.Paragraph, on: false);
        Assert.Equal(AtTheGlyph, Shown(after));
    }

    // A renderer of a caller's that lays out a built-in one inside the document's layout.
    private sealed class Forward(IRenderer inner) : IRenderer
    {
        public LayoutResult Layout(LayoutContext context) => inner.Layout(context);

        public void Draw(DrawContext context) => inner.Draw(context);
    }
}
